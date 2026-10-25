using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Wavelit.App.Wallpaper;
using Wavelit.Core.Library;
using Wavelit.Core.Storage;

namespace Wavelit.App.Services;

/// <summary>
/// Loads card thumbnails with a disk cache, bounded concurrency and decode-at-display-size
/// (a 1080p JPEG decoded full size is ~8 MB; at 400 px wide it is ~0.3 MB).
/// </summary>
public sealed class ThumbnailLoader(IHttpClientFactory httpFactory, AppPaths paths, ILogger<ThumbnailLoader> logger)
{
    public const string HttpClientName = "thumbnails";
    private const int DecodeWidth = 400;
    private readonly SemaphoreSlim _gate = new(6, 6);

    public async Task<ImageSource?> LoadAsync(WallpaperItem item, CancellationToken ct)
    {
        if (item.IsBuiltIn) return LoadBuiltIn(item.ShaderId);

        string captured = WallpaperService.CapturedThumbnailPath(paths, item.Id);
        if (File.Exists(captured)) return await DecodeFileAsync(captured, ct);
        if (item.ThumbnailUrl is null || !item.ThumbnailUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;

        string cacheFile = Path.Combine(paths.ThumbnailCache, MediaCache.FileNameFor(item.ThumbnailUrl, ".img"));
        if (File.Exists(cacheFile)) return await DecodeFileAsync(cacheFile, ct);

        await _gate.WaitAsync(ct);
        try
        {
            var client = httpFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(item.ThumbnailUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength > 10 * 1024 * 1024) return null;
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var image = Decode(bytes);
            if (image is not null)
            {
                string tmp = cacheFile + ".part";
                await File.WriteAllBytesAsync(tmp, bytes, ct);
                File.Move(tmp, cacheFile, overwrite: true);
            }
            return image;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Thumbnail download failed for {Item}", item.Id);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Included wallpapers ship pre-rendered thumbnails so the gallery looks right on first launch.
    private static BitmapImage? LoadBuiltIn(string? key)
    {
        if (key is null) return null;
        var info = System.Windows.Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/BuiltIn/{key}.jpg"));
        if (info is null) return null;
        using var stream = info.Stream;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Decode(buffer.ToArray());
    }

    private static async Task<ImageSource?> DecodeFileAsync(string path, CancellationToken ct)
    {
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, ct);
            return Decode(bytes);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static BitmapImage? Decode(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = DecodeWidth;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
