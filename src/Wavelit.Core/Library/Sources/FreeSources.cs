using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wavelit.Core.Library.Pexels;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Library.Sources;

/// <summary>
/// Wallhaven (wallhaven.cc): a large wallpaper catalogue, no key needed. Safe-for-work only: Wavelit never
/// sends a key, and the API refuses anything but SFW without one. Uploads belong to their creators.
/// </summary>
public sealed class WallhavenSource(IHttpClientFactory http, ContentGuard guard, ILogger<WallhavenSource> logger)
    : JsonSource(http, guard, logger), IWallpaperSource
{
    public string Id => WallpaperSources.Wallhaven;
    public string Label => "Wallhaven";
    public SourceKind Kind => SourceKind.Images;
    public bool IsAvailable => true;
    protected override string Name => "Wallhaven";

    public async Task<SearchPage> SearchAsync(SourceQuery query, int page, CancellationToken ct)
    {
        if (Guard.IsBlocked(query.Text)) return new SearchPage([], false);
        string q = query.Text.Trim();
        var mood = query.Mood ?? Moods.Featured;
        // Typed searches: general + anime (no "people"), by relevance. Empty box: the mood's curated tag query,
        // most-favourited over a year, with portraits excluded. purity 100 = SFW only.
        string url = $"https://wallhaven.cc/api/v1/search?purity=100&page={page}"
            + (q.Length > 0
                ? $"&categories=110&q={Uri.EscapeDataString(q)}&sorting=relevance"
                : $"&categories={mood.WallhavenCategories}&q={Uri.EscapeDataString(Curation.WallhavenQuery(mood, query.Today))}&sorting=favorites&topRange=1y")
            + (query.FitOnly
                ? $"&ratios={Fit.NearestRatios(query.CanvasWidth, query.CanvasHeight)}&atleast={Math.Min(query.CanvasWidth, 7680)}x{Math.Min(query.CanvasHeight, 4320)}"
                : "&atleast=1920x1080");
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        return Parse(doc.RootElement, q.Length > 0 ? q : mood == Moods.Featured ? "" : mood.Label);
    }

    internal static SearchPage Parse(JsonElement root, string query)
    {
        var results = new List<SearchResult>();
        foreach (var w in root.GetProperty("data").EnumerateArray())
        {
            if (Str(w, "purity") != "sfw") continue; // belt and braces
            string? id = Str(w, "id"), path = Https(Str(w, "path"));
            if (id is null || path is null) continue;
            string? thumb = w.TryGetProperty("thumbs", out var t) ? Https(Str(t, "large")) : null;
            int width = Int(w, "dimension_x"), height = Int(w, "dimension_y");
            results.Add(new SearchResult(new WallpaperItem
            {
                Id = $"wallhaven-{id}",
                Kind = WallpaperKind.Image,
                Source = WallpaperSources.Wallhaven,
                Title = TitleFor(query, Str(w, "category"), width, height),
                PageUrl = Https(Str(w, "url")),
                License = "Community upload: rights belong to the creator. Personal use.",
                ThumbnailUrl = thumb,
                RemoteUrl = path,
                Width = width,
                Height = height,
            }));
        }
        var meta = root.GetProperty("meta");
        return new SearchPage(results, Int(meta, "current_page") < Int(meta, "last_page"));
    }

    private static string TitleFor(string query, string? category, int w, int h)
    {
        string subject = query.Length > 0 ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(query) : category == "anime" ? "Anime" : "Wallpaper";
        return $"{subject} · {w}×{h}";
    }
}

/// <summary>NASA Image and Video Library (images.nasa.gov): Earth and space videos, no key, mostly public domain.</summary>
public sealed class NasaSource(IHttpClientFactory http, ContentGuard guard, ILogger<NasaSource> logger)
    : JsonSource(http, guard, logger), IWallpaperSource
{
    public const int PageSize = 24;
    public string Id => WallpaperSources.Nasa;
    public string Label => "NASA";
    public SourceKind Kind => SourceKind.Videos;
    public bool IsAvailable => true;
    protected override string Name => "NASA";

    /// <summary>NASA's "large" renditions of long films run to 2 GB; anything over this is skipped.</summary>
    public const long MaxVideoBytes = 600L * 1024 * 1024;

    public async Task<SearchPage> SearchAsync(SourceQuery query, int page, CancellationToken ct)
    {
        // Search only: NASA's library is mostly briefings and documentaries, so it never feeds the curated home.
        string q = query.Text.Trim();
        if (q.Length == 0 || Guard.IsBlocked(q)) return new SearchPage([], false);
        string url = $"https://images-api.nasa.gov/search?media_type=video&page_size={PageSize}&page={page}&q={Uri.EscapeDataString(q)}";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    internal static SearchPage Parse(JsonElement root)
    {
        var collection = root.GetProperty("collection");
        var results = new List<SearchResult>();
        foreach (var it in collection.GetProperty("items").EnumerateArray())
        {
            if (!it.TryGetProperty("data", out var dataArr) || dataArr.GetArrayLength() == 0) continue;
            var data = dataArr[0];
            string? nasaId = Str(data, "nasa_id");
            if (nasaId is null || Str(data, "media_type") != "video" || !Curation.IsScenicNasaTitle(Str(data, "title"))) continue;
            string? thumb = null;
            if (it.TryGetProperty("links", out var links))
                thumb = links.EnumerateArray().Select(l => Https(Str(l, "href"))).FirstOrDefault(h => h?.EndsWith("~large.jpg", StringComparison.OrdinalIgnoreCase) == true)
                    ?? links.EnumerateArray().Select(l => Https(Str(l, "href"))).FirstOrDefault(h => h is not null && h.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
            results.Add(new SearchResult(new WallpaperItem
            {
                Id = "nasa-" + MediaCacheId(nasaId),
                Kind = WallpaperKind.Video,
                Source = WallpaperSources.Nasa,
                Title = Str(data, "title") ?? nasaId,
                Author = "NASA" + (Str(data, "center") is { } c ? $" ({c})" : ""),
                PageUrl = $"https://images.nasa.gov/details/{Uri.EscapeDataString(nasaId)}",
                License = "NASA media: public domain (no endorsement implied)",
                ThumbnailUrl = thumb,
                SourceRef = nasaId,
            }));
        }
        bool more = collection.TryGetProperty("links", out var l2) && l2.EnumerateArray().Any(l => Str(l, "rel") == "next");
        return new SearchPage(results, more);
    }

    private static string MediaCacheId(string nasaId) => MediaCache.FileNameFor(nasaId, "")[..16];

    /// <summary>Looks up the video files once the user picks a result.</summary>
    public async Task<SearchResult> PrepareAsync(SearchResult result, CancellationToken ct)
    {
        if (result.VideoFiles is { Count: > 0 } || result.Item.SourceRef is not { } nasaId) return result;
        using var doc = await GetJsonAsync($"https://images-api.nasa.gov/asset/{Uri.EscapeDataString(nasaId)}", ct).ConfigureAwait(false);
        var files = ParseAsset(doc.RootElement);
        if (files.Count == 0) throw new LibrarySourceException("This NASA video has no downloadable MP4.");
        // Long films are huge even at 720p; keep only renditions a wallpaper can reasonably download.
        var sized = new List<RemoteVideoFile>();
        foreach (var f in files)
            if (await ContentLengthAsync(f.Url, ct).ConfigureAwait(false) is null or <= MaxVideoBytes) sized.Add(f);
        if (sized.Count == 0) throw new LibrarySourceException("This NASA video is a long film (too large to use as a wallpaper).");
        return result with { VideoFiles = sized };
    }

    internal static List<RemoteVideoFile> ParseAsset(JsonElement root)
    {
        // Renditions by name; NASA doesn't list sizes, so typical heights are assumed for selection.
        // "orig" masters can be several GB, so they're only used when nothing else exists.
        (string Suffix, int Height)[] renditions = [("~large.mp4", 1080), ("~medium.mp4", 720), ("~small.mp4", 480)];
        var hrefs = root.GetProperty("collection").GetProperty("items").EnumerateArray().Select(i => Https(Str(i, "href"))).OfType<string>().ToList();
        var files = new List<RemoteVideoFile>();
        foreach (var (suffix, height) in renditions)
        {
            string? href = hrefs.FirstOrDefault(h => Uri.UnescapeDataString(h).EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (href is not null) files.Add(new RemoteVideoFile(href, height * 16 / 9, height, null, "video/mp4", suffix[1..^4]));
        }
        if (files.Count == 0 && hrefs.FirstOrDefault(h => Uri.UnescapeDataString(h).EndsWith("~orig.mp4", StringComparison.OrdinalIgnoreCase)) is { } orig)
            files.Add(new RemoteVideoFile(orig, 1920, 1080, null, "video/mp4", "orig"));
        return files;
    }
}

/// <summary>Openverse (openverse.org): Creative Commons images, no key, mature content excluded.</summary>
public sealed class OpenverseSource(IHttpClientFactory http, ContentGuard guard, ILogger<OpenverseSource> logger)
    : JsonSource(http, guard, logger), IWallpaperSource
{
    public string Id => WallpaperSources.Openverse;
    public string Label => "Openverse";
    public SourceKind Kind => SourceKind.Images;
    public bool IsAvailable => true;
    protected override string Name => "Openverse";

    public async Task<SearchPage> SearchAsync(SourceQuery query, int page, CancellationToken ct)
    {
        // Search only: Openverse mixes scans and snapshots, so it never feeds the curated home.
        string q = query.Text.Trim();
        if (q.Length == 0 || Guard.IsBlocked(q)) return new SearchPage([], false);
        string aspect = query.CanvasWidth >= query.CanvasHeight ? "wide" : "tall";
        string url = $"https://api.openverse.org/v1/images/?q={Uri.EscapeDataString(q)}&page={page}&page_size=20&mature=false&size=large&aspect_ratio={aspect}";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    internal static SearchPage Parse(JsonElement root)
    {
        var results = new List<SearchResult>();
        foreach (var r in root.GetProperty("results").EnumerateArray())
        {
            if (r.TryGetProperty("mature", out var m) && m.ValueKind == JsonValueKind.True) continue;
            string? id = Str(r, "id"), url = Https(Str(r, "url"));
            if (id is null || url is null || !Curation.IsWallpaperSizedImage(Int(r, "width"), Int(r, "height"))) continue;
            string license = (Str(r, "license") ?? "cc").ToUpperInvariant();
            string licenseText = license is "CC0" or "PDM" ? license : $"CC {license} {Str(r, "license_version")}".Trim();
            results.Add(new SearchResult(new WallpaperItem
            {
                Id = $"openverse-{id}",
                Kind = WallpaperKind.Image,
                Source = WallpaperSources.Openverse,
                Title = string.IsNullOrWhiteSpace(Str(r, "title")) ? "Openverse image" : Str(r, "title")!.Trim(),
                Author = Str(r, "creator"),
                AuthorUrl = Https(Str(r, "creator_url")),
                PageUrl = Https(Str(r, "foreign_landing_url")),
                License = licenseText,
                ThumbnailUrl = Https(Str(r, "thumbnail")),
                RemoteUrl = url,
                Width = Int(r, "width") is > 0 and var w ? w : null,
                Height = Int(r, "height") is > 0 and var h ? h : null,
            }));
        }
        return new SearchPage(results, Int(root, "page") < Int(root, "page_count"));
    }
}

/// <summary>Pixabay videos (free key, issued instantly). Safe search is always on.</summary>
public sealed class PixabaySource(IHttpClientFactory http, ContentGuard guard, SecretStore secrets, ILogger<PixabaySource> logger)
    : JsonSource(http, guard, logger), IWallpaperSource
{
    public const int PageSize = 24;
    public string Id => WallpaperSources.Pixabay;
    public string Label => "Pixabay";
    public SourceKind Kind => SourceKind.Videos;
    public bool IsAvailable => secrets.Has(SecretNames.PixabayApiKey);
    protected override string Name => "Pixabay";

    public async Task<SearchPage> SearchAsync(SourceQuery query, int page, CancellationToken ct)
    {
        string key = secrets.Get(SecretNames.PixabayApiKey) ?? throw new LibrarySourceException("Add a free Pixabay key in Settings to search its videos.");
        if (Guard.IsBlocked(query.Text)) return new SearchPage([], false);
        string text = query.Text.Trim().Length > 0 ? query.Text : query.Mood?.VideoQuery ?? "";
        using var doc = await GetJsonAsync(Url(key, text, page, PageSize), ct).ConfigureAwait(false);
        return Parse(doc.RootElement, page);
    }

    public async Task<bool> TestKeyAsync(string key, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync(Url(key, "nature", 1, 3), ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("hits", out _);
        }
        catch (LibrarySourceException)
        {
            return false;
        }
    }

    private static string Url(string key, string text, int page, int perPage)
    {
        string q = text.Trim();
        return $"https://pixabay.com/api/videos/?key={Uri.EscapeDataString(key)}&safesearch=true&per_page={perPage}&page={page}&min_width=1920"
            + (q.Length > 0 ? $"&q={Uri.EscapeDataString(q)}" : "&editors_choice=true&order=popular");
    }

    internal static SearchPage Parse(JsonElement root, int page)
    {
        var results = new List<SearchResult>();
        foreach (var hit in root.GetProperty("hits").EnumerateArray())
        {
            if (!hit.TryGetProperty("videos", out var videos)) continue;
            var files = new List<RemoteVideoFile>();
            string? thumb = null;
            foreach (var name in new[] { "large", "medium", "small" })
            {
                if (!videos.TryGetProperty(name, out var v)) continue;
                thumb ??= Https(Str(v, "thumbnail"));
                if (Https(Str(v, "url")) is { } url && Int(v, "width") > 0)
                    files.Add(new RemoteVideoFile(url, Int(v, "width"), Int(v, "height"), null, "video/mp4", name));
            }
            if (files.Count == 0) continue;
            long id = hit.GetProperty("id").GetInt64();
            string tags = Str(hit, "tags") ?? "";
            string title = string.Join(", ", tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(3));
            var best = files[0];
            results.Add(new SearchResult(new WallpaperItem
            {
                Id = $"pixabay-{id}",
                Kind = WallpaperKind.Video,
                Source = WallpaperSources.Pixabay,
                Title = title.Length > 0 ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(title) : $"Pixabay video {id}",
                Author = Str(hit, "user"),
                PageUrl = Https(Str(hit, "pageURL")),
                License = "Pixabay Content License",
                ThumbnailUrl = thumb,
                Width = best.Width,
                Height = best.Height,
                DurationSeconds = hit.TryGetProperty("duration", out var d) && d.TryGetDouble(out double sec) ? sec : null,
            }, files));
        }
        int total = Int(root, "totalHits");
        return new SearchPage(results, page * PageSize < Math.Min(total, 500));
    }
}

/// <summary>Pexels videos through the existing client (new keys are paused by Pexels; existing keys work).</summary>
public sealed class PexelsSource(PexelsClient client) : IWallpaperSource
{
    public string Id => WallpaperSources.Pexels;
    public string Label => "Pexels";
    public SourceKind Kind => SourceKind.Videos;
    public bool IsAvailable => client.IsConfigured;
    public Task<SearchPage> SearchAsync(SourceQuery query, int page, CancellationToken ct)
        => client.SearchAsync(query.Text.Trim().Length > 0 ? query.Text : query.Mood?.VideoQuery ?? "", page, query.MaxMonitorHeight, ct);
}
