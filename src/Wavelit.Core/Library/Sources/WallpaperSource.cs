using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Wavelit.Core.Library.Sources;

public enum SourceKind { Videos, Images }

/// <param name="CanvasWidth">Size of what the wallpaper must fill (the Extend canvas or one display).</param>
/// <param name="FitOnly">"Fits my screens": ask the source for media shaped like the canvas.</param>
/// <param name="Mood">With an empty <paramref name="Text"/>, the curated mood to show (null = Featured).</param>
public sealed record SourceQuery(string Text, int CanvasWidth, int CanvasHeight, int MaxMonitorHeight, bool FitOnly, Mood? Mood = null)
{
    /// <summary>Picks the day's rotation of curated feeds.</summary>
    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.Now);
}

/// <summary>A searchable online library (Wallhaven, Commons, NASA, Openverse, Pixabay, Pexels).</summary>
public interface IWallpaperSource
{
    string Id { get; }
    string Label { get; }
    SourceKind Kind { get; }
    /// <summary>False when the source needs a key the user hasn't added.</summary>
    bool IsAvailable { get; }
    Task<SearchPage> SearchAsync(SourceQuery query, int page, CancellationToken ct);
    /// <summary>Fills in anything only needed once the user picks a result (e.g. NASA's file list).</summary>
    Task<SearchResult> PrepareAsync(SearchResult result, CancellationToken ct) => Task.FromResult(result);
}

/// <summary>Shared HTTP + JSON plumbing for sources, with a 24-hour response cache (Pixabay requires one).</summary>
public abstract class JsonSource(IHttpClientFactory httpFactory, ContentGuard guard, ILogger logger)
{
    public const string HttpClientName = "sources";
    private static readonly ConcurrentDictionary<string, (DateTime At, string Body)> Cache = new();
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(24);

    protected ContentGuard Guard => guard;

    /// <summary>The file's size from a HEAD request, or null when the server doesn't say.</summary>
    protected async Task<long?> ContentLengthAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? response.Content.Headers.ContentLength : null;
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "{Source} size check failed", Name);
            return null;
        }
    }
    protected abstract string Name { get; }

    protected async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct, Action<HttpRequestMessage>? configure = null)
    {
        if (Cache.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return JsonDocument.Parse(hit.Body);
        var client = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        configure?.Invoke(request);
        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new LibrarySourceException($"{Name} is busy (too many searches). Try again in a minute.");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new LibrarySourceException($"{Name} rejected the request. Check its key in Settings.");
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var doc = JsonDocument.Parse(body);
            if (Cache.Count > 300) Cache.Clear();
            Cache[url] = (DateTime.UtcNow, body);
            return doc;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "{Source} request failed", Name);
            throw new LibrarySourceException($"Couldn't reach {Name}. Check your internet connection.", ex);
        }
        catch (JsonException ex)
        {
            throw new LibrarySourceException($"{Name} returned data Wavelit couldn't read.", ex);
        }
    }

    /// <summary>Only https URLs are used; NASA serves http links for https-capable hosts.</summary>
    internal static string? Https(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[7..];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.AbsoluteUri;
    }

    internal static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;
}

public enum FitRating { Unknown, Perfect, Good, Crops, LowResolution }

/// <summary>How well media of a given size fills the user's canvas ("Fits your setup" badges).</summary>
public static class Fit
{
    public static FitRating Rate(int? mediaWidth, int? mediaHeight, int canvasWidth, int canvasHeight)
    {
        if (mediaWidth is not > 0 || mediaHeight is not > 0 || canvasWidth <= 0 || canvasHeight <= 0) return FitRating.Unknown;
        double mediaAspect = (double)mediaWidth.Value / mediaHeight.Value;
        double canvasAspect = (double)canvasWidth / canvasHeight;
        double aspectMatch = Math.Min(mediaAspect, canvasAspect) / Math.Max(mediaAspect, canvasAspect); // share kept by "cover"
        double upscale = Math.Max((double)canvasWidth / mediaWidth.Value, (double)canvasHeight / mediaHeight.Value);
        if (upscale > 1.6) return FitRating.LowResolution;
        if (aspectMatch >= 0.92 && upscale <= 1.15) return FitRating.Perfect;
        if (aspectMatch >= 0.75) return FitRating.Good;
        return FitRating.Crops;
    }

    public static string? Label(FitRating rating) => rating switch
    {
        FitRating.Perfect => "Perfect fit",
        FitRating.Good => "Good fit",
        FitRating.Crops => "Will crop",
        FitRating.LowResolution => "Low resolution",
        _ => null,
    };

    /// <summary>Wallhaven-style ratio strings close to the canvas aspect (e.g. "16x9", "32x9").</summary>
    public static string NearestRatios(int canvasWidth, int canvasHeight)
    {
        (string Name, double Value)[] known =
        [
            ("9x16", 9 / 16.0), ("10x16", 10 / 16.0), ("9x18", 0.5), ("1x1", 1), ("4x3", 4 / 3.0), ("5x4", 1.25),
            ("16x10", 1.6), ("16x9", 16 / 9.0), ("21x9", 21 / 9.0), ("32x9", 32 / 9.0), ("48x9", 48 / 9.0),
        ];
        double aspect = (double)canvasWidth / Math.Max(1, canvasHeight);
        return string.Join(',', known.OrderBy(k => Math.Abs(Math.Log(k.Value / aspect))).Take(2).Select(k => k.Name));
    }
}
