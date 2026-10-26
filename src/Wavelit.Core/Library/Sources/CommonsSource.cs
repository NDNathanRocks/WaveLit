using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Library.Sources;

/// <summary>
/// Wikimedia Commons videos: real Full HD footage (time-lapses, nature) under CC licences, no key needed.
/// Originals are often Ogg Theora, which Chromium no longer plays, so only Commons' VP9 WebM transcodes are used.
/// </summary>
public sealed partial class CommonsSource(IHttpClientFactory http, ContentGuard guard, ILogger<CommonsSource> logger)
    : JsonSource(http, guard, logger), IWallpaperSource
{
    public const int PageSize = 30;
    public string Id => WallpaperSources.Commons;
    public string Label => "Wikimedia Commons";
    public SourceKind Kind => SourceKind.Videos;
    public bool IsAvailable => true;
    protected override string Name => "Wikimedia Commons";

    public async Task<SearchPage> SearchAsync(SourceQuery query, int page, CancellationToken ct)
    {
        if (Guard.IsBlocked(query.Text)) return new SearchPage([], false);
        string? terms = query.Text.Trim() is { Length: > 0 } t ? t : Curation.CommonsQuery(query.Mood ?? Moods.Featured, query.Today);
        if (string.IsNullOrEmpty(terms)) return new SearchPage([], false);
        string search = $"{terms} filetype:video filew:>1919";
        string url = "https://commons.wikimedia.org/w/api.php?action=query&format=json&formatversion=2&generator=search&gsrnamespace=6"
            + $"&gsrlimit={PageSize}&gsroffset={(page - 1) * PageSize}&prop=videoinfo&viprop=size|url|derivatives|extmetadata&viurlwidth=640"
            + $"&gsrsearch={Uri.EscapeDataString(search)}";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    internal static SearchPage Parse(JsonElement root)
    {
        var results = new List<SearchResult>();
        if (root.TryGetProperty("query", out var q) && q.TryGetProperty("pages", out var pages))
        {
            foreach (var p in pages.EnumerateArray().OrderBy(p => Int(p, "index")))
            {
                if (!p.TryGetProperty("videoinfo", out var infos) || infos.GetArrayLength() == 0) continue;
                var v = infos[0];
                string? fileTitle = Str(p, "title");
                double duration = v.TryGetProperty("duration", out var d) && d.TryGetDouble(out double sec) ? sec : 0;
                if (fileTitle is null || !Curation.IsWallpaperVideo(Int(v, "width"), Int(v, "height"), duration)) continue;
                string title = CleanTitle(fileTitle);
                if (!Curation.LooksLikeFootage(title)) continue;

                var files = Transcodes(v);
                if (files.Count == 0) continue;
                var meta = v.TryGetProperty("extmetadata", out var m) ? m : default;
                results.Add(new SearchResult(new WallpaperItem
                {
                    Id = ItemId(fileTitle),
                    Kind = WallpaperKind.Video,
                    Source = WallpaperSources.Commons,
                    Title = title,
                    Author = PlainText(Meta(meta, "Artist")),
                    PageUrl = Https(Str(v, "descriptionurl")),
                    License = Meta(meta, "LicenseShortName") ?? "See the Commons page",
                    ThumbnailUrl = Https(Str(v, "thumburl")),
                    Width = Int(v, "width"),
                    Height = Int(v, "height"),
                    DurationSeconds = duration,
                }, files));
            }
        }
        return new SearchPage(results, root.TryGetProperty("continue", out _));
    }

    /// <summary>Stable library id from the file title (titles can be long and contain any character).</summary>
    public static string ItemId(string fileTitle) => "commons-" + MediaCache.FileNameFor(fileTitle, "")[..16];

    /// <summary>VP9 WebM transcodes of 720p and up, largest first.</summary>
    private static List<RemoteVideoFile> Transcodes(JsonElement info)
    {
        var files = new List<RemoteVideoFile>();
        if (!info.TryGetProperty("derivatives", out var derivatives)) return files;
        foreach (var x in derivatives.EnumerateArray())
        {
            string? key = Str(x, "transcodekey"), url = Https(Str(x, "src"));
            if (key is null || url is null || Str(x, "type")?.Contains("vp9", StringComparison.OrdinalIgnoreCase) != true) continue;
            if (Int(x, "height") < 720) continue;
            files.Add(new RemoteVideoFile(url, Int(x, "width"), Int(x, "height"), null, "video/webm", key));
        }
        return files.OrderByDescending(f => f.Height).ToList();
    }

    private static string? Meta(JsonElement meta, string name)
        => meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(name, out var e) ? Str(e, "value") : null;

    /// <summary>"File:Aurora_over_X.ogv" → "Aurora over X".</summary>
    internal static string CleanTitle(string fileTitle)
    {
        string t = fileTitle.StartsWith("File:", StringComparison.OrdinalIgnoreCase) ? fileTitle[5..] : fileTitle;
        t = Extension().Replace(t, "").Replace('_', ' ').Trim();
        t = UploadId().Replace(t, "").Trim(); // "Aurora - 53921478021", "Night Sky (30549248476)"
        return t.Length > 80 ? t[..80].TrimEnd() + "…" : t;
    }

    /// <summary>Commons' Artist field is HTML (links, spans); keep the visible text only.</summary>
    internal static string? PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        string text = Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(html, "")), " ").Trim();
        return text.Length is 0 or > 80 || text is "No name" or "[1]" || text.StartsWith('[') ? null : text;
    }

    [GeneratedRegex(@"(\.(ogv|ogg|webm|mov|mp4|mpg|mpeg))+$", RegexOptions.IgnoreCase)]
    private static partial Regex Extension();
    [GeneratedRegex(@"[\s_-]*\(?\d{8,}\)?$")]
    private static partial Regex UploadId();
    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
