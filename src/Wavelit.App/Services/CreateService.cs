using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Wavelit.App.Wallpaper;
using Wavelit.Core.Create;
using Wavelit.Core.Desktop;
using Wavelit.Core.Library;
using Wavelit.Core.Storage;

namespace Wavelit.App.Services;

/// <summary>A generated image waiting for the user to pick it (in creations/pending until then).</summary>
public sealed record CreationCandidate(string Path, string Prompt, StylePreset Style, string Generator);

/// <summary>
/// The Create page's engine: makes wallpapers from a prompt with the chosen generator, shaped like the
/// user's screens (the whole Extend canvas, or one display), and saves the one the user picks.
/// </summary>
public sealed class CreateService(
    PollinationsGenerator cloud,
    CloudflareGenerator cloudflare,
    LocalAiService local,
    ContentGuard guard,
    WallpaperService wallpapers,
    SettingsStore settings,
    LibraryStore library,
    AppPaths paths,
    ILogger<CreateService> logger)
{
    public const int Count = 4;

    public string PendingFolder => Path.Combine(paths.Creations, "pending");

    /// <summary>The canvas to fill: every display in Extend mode, otherwise the target (or primary) display.</summary>
    public (int Width, int Height) CanvasSize(string? monitorId)
    {
        var monitors = wallpapers.Snapshot.Monitors;
        if (monitors.Count == 0) return (1920, 1080);
        if (monitorId is null && settings.Current.Displays.Layout == DisplayLayout.Span && monitors.Count > 1)
        {
            var b = MonitorService.GetVirtualBounds(monitors);
            return (b.Width, b.Height);
        }
        var m = monitors.FirstOrDefault(x => x.Id == monitorId) ?? monitors.FirstOrDefault(x => x.IsPrimary) ?? monitors[0];
        return (m.Bounds.Width, m.Bounds.Height);
    }

    public async Task<(IReadOnlyList<CreationCandidate> Images, int Blocked)> GenerateAsync(string prompt, StylePreset style,
        string generator, string? monitorId, IProgress<(double Value, string Stage)>? progress, CancellationToken ct)
    {
        prompt = prompt.Trim();
        if (prompt.Length == 0) throw new GenerationException("Describe the wallpaper you want first.");
        if (prompt.Length > Prompts.MaxPromptLength) throw new GenerationException($"Keep the description under {Prompts.MaxPromptLength} characters.");
        if (guard.IsBlocked(prompt)) throw new GenerationException("That prompt isn't allowed. Wavelit only makes wallpapers that are safe for work.");

        ClearPending();
        Directory.CreateDirectory(PendingFolder);
        var (cw, ch) = CanvasSize(monitorId);
        long seed = Random.Shared.NextInt64(1, int.MaxValue);
        var results = new List<CreationCandidate>();

        if (generator == GeneratorIds.Local)
        {
            var (w, h) = GenerationSize.ForAspect(cw, ch, 1_048_576, 64, 512, 2048);
            string fullPrompt = Prompts.Compose(prompt, style);
            var (files, blocked) = await local.GenerateAsync(fullPrompt, w, h, seed, Count, PendingFolder, progress, ct);
            results.AddRange(files.Select(f => new CreationCandidate(f, prompt, style, generator)));
            logger.LogInformation("Local generation made {Count} images ({Blocked} filtered) at {W}x{H}", files.Count, blocked, w, h);
            return (results, blocked);
        }

        IImageGenerator engine = generator == GeneratorIds.Cloudflare ? cloudflare : cloud;
        var (target, multiple, min, max) = engine.SizeLimits;
        var (gw, gh) = GenerationSize.ForAspect(cw, ch, target, multiple, min, max);
        for (int i = 0; i < Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(((double)i / Count, $"Painting {i + 1} of {Count}…"));
            try
            {
                byte[] bytes = await engine.GenerateAsync(new GenerationRequest(prompt, style, gw, gh, seed + i), ct);
                string ext = bytes.Length > 1 && bytes[0] == 0x89 ? ".png" : ".jpg";
                string file = Path.Combine(PendingFolder, $"{engine.Id}-{Guid.NewGuid():N}{ext}");
                await File.WriteAllBytesAsync(file, bytes, ct);
                results.Add(new CreationCandidate(file, prompt, style, engine.Id));
            }
            catch (GenerationException) when (results.Count > 0)
            {
                break; // keep what we have (e.g. a rate limit after two images)
            }
        }
        progress?.Report((1, "Done"));
        logger.LogInformation("{Generator} generation made {Count} images at {W}x{H}", engine.Id, results.Count, gw, gh);
        return (results, 0);
    }

    /// <summary>
    /// Keeps the chosen image: sharpens it to the full size of the screens (Real-ESRGAN, bundled), saves it
    /// in creations and adds it to the library. If sharpening isn't possible the original is kept.
    /// </summary>
    public async Task<WallpaperItem> KeepAsync(CreationCandidate candidate, string effect, string? monitorId,
        IProgress<(double Value, string Stage)>? progress, CancellationToken ct)
    {
        byte[] bytes = await File.ReadAllBytesAsync(candidate.Path, ct);
        var (cw, ch) = CanvasSize(monitorId);
        try
        {
            bytes = await SharpenAsync(bytes, cw, ch, progress, ct);
        }
        catch (Exception ex) when (ex is GenerationException or IOException or NotSupportedException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Sharpening failed; keeping the original size");
        }
        var item = Creations.Save(paths, bytes, candidate.Prompt, candidate.Style, candidate.Generator)
            with { Effect = ImageEffects.Normalize(effect) };
        return library.Upsert(item);
    }

    private async Task<byte[]> SharpenAsync(byte[] image, int canvasWidth, int canvasHeight,
        IProgress<(double Value, string Stage)>? progress, CancellationToken ct)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(image), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        var rgb = new FormatConvertedBitmap(frame, PixelFormats.Rgb24, null, 0);
        int w = rgb.PixelWidth, h = rgb.PixelHeight;
        if (w >= canvasWidth * 0.95 && h >= canvasHeight * 0.95) return image; // already sharp enough

        var pixels = new byte[w * h * 3];
        rgb.CopyPixels(pixels, w * 3, 0);
        Directory.CreateDirectory(PendingFolder);
        string raw = Path.Combine(PendingFolder, $"{Guid.NewGuid():N}.rgb");
        string png = Path.ChangeExtension(raw, ".png");
        try
        {
            await File.WriteAllBytesAsync(raw, pixels, ct);
            await local.UpscaleAsync(raw, w, h, canvasWidth, canvasHeight, png, progress, ct);
            var big = BitmapDecoder.Create(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var encoder = new JpegBitmapEncoder { QualityLevel = 93 };
            encoder.Frames.Add(big);
            using var output = new MemoryStream();
            encoder.Save(output);
            logger.LogInformation("Sharpened creation from {W}x{H} to {BW}x{BH}", w, h, big.PixelWidth, big.PixelHeight);
            return output.ToArray();
        }
        finally
        {
            try { File.Delete(raw); File.Delete(png); } catch (IOException) { }
        }
    }

    public void ClearPending()
    {
        try
        {
            if (Directory.Exists(PendingFolder)) Directory.Delete(PendingFolder, recursive: true);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not clear pending creations");
        }
    }
}
