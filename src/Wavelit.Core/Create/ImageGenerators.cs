using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Wavelit.Core.Library;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Create;

public static class GeneratorIds
{
    public const string Cloud = "cloud";           // Pollinations: free, no key
    public const string Cloudflare = "cloudflare"; // Workers AI: free daily allowance, user's own token
    public const string Local = "local";           // SDXL-Lightning on this PC
}

public sealed record StylePreset(string Key, string Label, string Suffix);

/// <param name="Width">Pixel size to generate (already shaped like the user's screens).</param>
public sealed record GenerationRequest(string Prompt, StylePreset Style, int Width, int Height, long Seed);

public sealed class GenerationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Turns a prompt into image bytes (JPEG or PNG).</summary>
public interface IImageGenerator
{
    string Id { get; }
    /// <summary>Generation size budget for this engine (pixels per image, size multiple, side limits).</summary>
    (int TargetPixels, int Multiple, int Min, int Max) SizeLimits { get; }
    Task<byte[]> GenerateAsync(GenerationRequest request, CancellationToken ct);
}

public static class Prompts
{
    public static IReadOnlyList<StylePreset> Styles { get; } =
    [
        new("none", "No style", ""),
        new("anime", "Anime", "anime art style, vibrant colors, clean line art, detailed background, studio quality"),
        new("cinematic", "Cinematic", "cinematic lighting, dramatic atmosphere, film still, highly detailed"),
        new("neon", "Neon", "neon lights, synthwave, glowing, night, vivid magenta and cyan"),
        new("watercolour", "Watercolour", "watercolor painting, soft washes, paper texture, gentle colors"),
        new("pixel", "Pixel art", "pixel art, 16-bit, crisp pixels, retro game scene"),
        new("minimal", "Minimal", "minimalist, simple shapes, soft gradients, lots of negative space"),
        new("fantasy", "Fantasy", "epic fantasy landscape, magical atmosphere, matte painting, detailed"),
        new("space", "Space", "deep space, nebula, stars, cosmic colors, astrophotography"),
    ];

    /// <summary>The full prompt sent to a generator: the user's words, the style, and wallpaper hints.</summary>
    public static string Compose(string prompt, StylePreset style)
    {
        var parts = new List<string> { prompt.Trim() };
        if (style.Suffix.Length > 0) parts.Add(style.Suffix);
        parts.Add("desktop wallpaper, high resolution, no text, no watermark");
        return string.Join(", ", parts);
    }

    public const int MaxPromptLength = 400;
}

/// <summary>
/// Pollinations (image.pollinations.ai): free, no account. Requests ask for its safety filter; the
/// prompt leaves the PC, which the privacy policy discloses.
/// </summary>
public sealed class PollinationsGenerator(IHttpClientFactory httpFactory) : IImageGenerator
{
    public const string HttpClientName = "generate";
    public string Id => GeneratorIds.Cloud;
    public (int TargetPixels, int Multiple, int Min, int Max) SizeLimits => (1_600_000, 16, 256, 2048);

    public async Task<byte[]> GenerateAsync(GenerationRequest r, CancellationToken ct)
    {
        string prompt = Uri.EscapeDataString(Prompts.Compose(r.Prompt, r.Style));
        string url = $"https://image.pollinations.ai/prompt/{prompt}?width={r.Width}&height={r.Height}&seed={r.Seed}&nologo=true&safe=true&private=true&model=flux";
        var client = httpFactory.CreateClient(HttpClientName);
        try
        {
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            if ((int)response.StatusCode == 429) throw new GenerationException("The free cloud generator is busy. Wait a minute and try again.");
            if (!response.IsSuccessStatusCode) throw new GenerationException($"The cloud generator failed ({(int)response.StatusCode}). Try again, or use another generator.");
            if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                throw new GenerationException("The cloud generator didn't return an image. Try a different prompt.");
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GenerationException("Couldn't reach the cloud generator. Check your connection.", ex);
        }
    }
}

/// <summary>
/// Cloudflare Workers AI with the user's own free account: SDXL-Lightning (any size up to 2048 px).
/// Needs an account id and an API token with the "Workers AI" permission, stored with DPAPI.
/// </summary>
public sealed class CloudflareGenerator(IHttpClientFactory httpFactory, SecretStore secrets) : IImageGenerator
{
    public const string Model = "@cf/bytedance/stable-diffusion-xl-lightning";
    public string Id => GeneratorIds.Cloudflare;
    public (int TargetPixels, int Multiple, int Min, int Max) SizeLimits => (1_150_000, 64, 256, 2048);

    public bool IsConfigured => secrets.Has(SecretNames.CloudflareAccountId) && secrets.Has(SecretNames.CloudflareApiToken);

    public async Task<byte[]> GenerateAsync(GenerationRequest r, CancellationToken ct)
    {
        string? account = secrets.Get(SecretNames.CloudflareAccountId);
        string? token = secrets.Get(SecretNames.CloudflareApiToken);
        if (account is null || token is null) throw new GenerationException("Add your Cloudflare account ID and API token in Settings first.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(account, "^[0-9a-fA-F]{32}$"))
            throw new GenerationException("The Cloudflare account ID should be 32 letters and digits (find it in the Cloudflare dashboard).");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.cloudflare.com/client/v4/accounts/{account}/ai/run/{Model}")
        {
            Content = JsonContent.Create(new { prompt = Prompts.Compose(r.Prompt, r.Style), width = r.Width, height = r.Height, seed = r.Seed }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var client = httpFactory.CreateClient(PollinationsGenerator.HttpClientName);
        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new GenerationException("Cloudflare rejected the token. Check it has the Workers AI permission.");
            if ((int)response.StatusCode == 429)
                throw new GenerationException("You've used today's free Cloudflare allowance. It resets at midnight UTC.");
            if (!response.IsSuccessStatusCode)
                throw new GenerationException($"Cloudflare couldn't make that image ({(int)response.StatusCode}).");

            string? type = response.Content.Headers.ContentType?.MediaType;
            byte[] body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true) return body;
            // Some models answer JSON with a base64 image.
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("result", out var result) && result.TryGetProperty("image", out var image) && image.GetString() is { } b64)
                return Convert.FromBase64String(b64);
            throw new GenerationException("Cloudflare didn't return an image.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException)
        {
            throw new GenerationException("Couldn't reach Cloudflare. Check your connection.", ex);
        }
    }
}

/// <summary>Stores generated images and makes them library items.</summary>
public static class Creations
{
    public static WallpaperItem Save(AppPaths paths, byte[] image, string prompt, StylePreset style, string generator)
    {
        bool png = image.Length > 8 && image[0] == 0x89 && image[1] == (byte)'P';
        string id = "gen-" + Guid.NewGuid().ToString("N")[..12];
        string path = Path.Combine(paths.Creations, id + (png ? ".png" : ".jpg"));
        File.WriteAllBytes(path, image);
        string title = prompt.Trim();
        if (title.Length > 48) title = title[..47].TrimEnd() + "…";
        return new WallpaperItem
        {
            Id = id,
            Kind = WallpaperKind.Image,
            Source = WallpaperSources.Generated,
            Title = title.Length == 0 ? "Creation" : char.ToUpperInvariant(title[0]) + title[1..],
            Author = generator switch
            {
                GeneratorIds.Local => "Made on this PC (SDXL-Lightning)",
                GeneratorIds.Cloudflare => "Made with Cloudflare Workers AI",
                _ => "Made with Pollinations",
            },
            License = "Generated by you",
            FilePath = path,
            Prompt = style.Suffix.Length > 0 ? $"{prompt.Trim()} ({style.Label})" : prompt.Trim(),
        };
    }
}
