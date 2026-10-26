using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Library.Pexels;

internal sealed record PexelsVideoResponse(
    int Page,
    [property: JsonPropertyName("per_page")] int PerPage,
    [property: JsonPropertyName("total_results")] int TotalResults,
    [property: JsonPropertyName("next_page")] string? NextPage,
    List<PexelsVideo>? Videos);

internal sealed record PexelsVideo(
    long Id, int Width, int Height, double Duration, string? Url, string? Image,
    PexelsUser? User,
    [property: JsonPropertyName("video_files")] List<PexelsVideoFile>? VideoFiles);

internal sealed record PexelsUser(long Id, string? Name, string? Url);

internal sealed record PexelsVideoFile(
    long Id, string? Quality,
    [property: JsonPropertyName("file_type")] string? FileType,
    int? Width, int? Height, double? Fps, string? Link);

/// <summary>Pexels Video API (https://www.pexels.com/api/documentation/#videos). Bring-your-own key.</summary>
public sealed class PexelsClient(IHttpClientFactory httpFactory, SecretStore secrets, ContentGuard guard, ILogger<PexelsClient> logger)
{
    public const string HttpClientName = "pexels";
    public const int PageSize = 24;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public bool IsConfigured => secrets.Has(SecretNames.PexelsApiKey);

    /// <summary>Requests remaining this month, from the last response's rate-limit headers.</summary>
    public int? RemainingQuota { get; private set; }

    /// <param name="query">Search text; empty returns popular videos.</param>
    /// <param name="maxMonitorHeight">Largest display height, used to ask for 4K only when it is useful.</param>
    public async Task<SearchPage> SearchAsync(string query, int page, int maxMonitorHeight, CancellationToken ct)
    {
        string? key = secrets.Get(SecretNames.PexelsApiKey) ?? throw new LibrarySourceException("Add a free Pexels API key in Settings to search videos.");
        if (guard.IsBlocked(query)) return new SearchPage([], false);

        string size = maxMonitorHeight > 1440 ? "large" : "medium";
        string path = string.IsNullOrWhiteSpace(query)
            ? $"videos/popular?min_width=1920&per_page={PageSize}&page={page}"
            : $"videos/search?query={Uri.EscapeDataString(query.Trim())}&orientation=landscape&size={size}&per_page={PageSize}&page={page}";

        var response = await SendAsync<PexelsVideoResponse>(key, path, ct).ConfigureAwait(false);
        var results = (response.Videos ?? [])
            .Select(ToResult)
            .OfType<SearchResult>()
            .ToList();
        return new SearchPage(results, response.NextPage is not null && results.Count > 0);
    }

    /// <summary>Validates a key with a minimal request.</summary>
    public async Task<bool> TestKeyAsync(string key, CancellationToken ct)
    {
        try
        {
            await SendAsync<PexelsVideoResponse>(key, "videos/popular?per_page=1", ct).ConfigureAwait(false);
            return true;
        }
        catch (LibrarySourceException)
        {
            return false;
        }
    }

    private async Task<T> SendAsync<T>(string key, string pathAndQuery, CancellationToken ct)
    {
        var client = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        request.Headers.TryAddWithoutValidation("Authorization", key);
        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            ReadQuota(response.Headers);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new LibrarySourceException("Pexels rejected the API key. Check it in Settings.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new LibrarySourceException("Pexels rate limit reached. Try again in a little while.");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct).ConfigureAwait(false)
                ?? throw new LibrarySourceException("Pexels returned an empty response.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Pexels request failed ({Path})", pathAndQuery.Split('?')[0]);
            throw new LibrarySourceException("Couldn't reach Pexels. Check your internet connection.", ex);
        }
        catch (JsonException ex)
        {
            throw new LibrarySourceException("Pexels returned data Wavelit couldn't read.", ex);
        }
    }

    private void ReadQuota(HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("X-Ratelimit-Remaining", out var values)
            && int.TryParse(values.FirstOrDefault(), out int remaining))
            RemainingQuota = remaining;
    }

    internal static SearchResult? ToResult(PexelsVideo v)
    {
        var files = (v.VideoFiles ?? [])
            .Where(f => f.Link is not null && f.Width > 0 && f.Height > 0)
            .Select(f => new RemoteVideoFile(f.Link!, f.Width!.Value, f.Height!.Value, f.Fps, f.FileType ?? "", f.Quality ?? ""))
            .ToList();
        if (files.Count == 0 || v.Width < v.Height) return null;

        var item = new WallpaperItem
        {
            Id = $"pexels-{v.Id}",
            Kind = WallpaperKind.Video,
            Source = WallpaperSources.Pexels,
            Title = TitleFromUrl(v.Url) ?? $"Pexels video {v.Id}",
            Author = v.User?.Name,
            AuthorUrl = v.User?.Url,
            PageUrl = v.Url,
            License = "Pexels License",
            ThumbnailUrl = v.Image,
            Width = v.Width,
            Height = v.Height,
            DurationSeconds = v.Duration,
        };
        return new SearchResult(item, files);
    }

    // Pexels has no title field; the page slug is the human-readable title
    // (https://www.pexels.com/video/waves-crashing-on-rocks-1234/).
    internal static string? TitleFromUrl(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        string slug = uri.Segments.LastOrDefault()?.Trim('/') ?? "";
        int lastDash = slug.LastIndexOf('-');
        if (lastDash > 0 && long.TryParse(slug[(lastDash + 1)..], out _)) slug = slug[..lastDash];
        if (string.IsNullOrWhiteSpace(slug) || long.TryParse(slug, out _)) return null;
        string words = slug.Replace('-', ' ');
        return char.ToUpperInvariant(words[0]) + words[1..];
    }
}

public static class VideoFileSelector
{
    /// <summary>
    /// Chooses the smallest MP4 or WebM that still covers <paramref name="targetHeight"/>; decoding 4K for a
    /// 1080p display costs ~4× the GPU time for no visible benefit.
    /// </summary>
    public static RemoteVideoFile? Select(IReadOnlyList<RemoteVideoFile> files, int targetHeight)
    {
        var mp4 = files
            .Where(f => (f.FileType.Equals("video/mp4", StringComparison.OrdinalIgnoreCase) || f.FileType.Equals("video/webm", StringComparison.OrdinalIgnoreCase))
                        && f.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        && f.Width >= f.Height
                        && (f.Fps is null || f.Fps <= 61))
            .ToList();
        if (mp4.Count == 0) return null;

        int target = Math.Clamp(targetHeight, 720, 2160);
        return mp4.Where(f => f.Height >= target).OrderBy(f => f.Height).ThenByDescending(f => f.Fps ?? 30).FirstOrDefault()
            ?? mp4.OrderByDescending(f => f.Height).ThenByDescending(f => f.Fps ?? 30).First();
    }
}
