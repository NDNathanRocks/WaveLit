using System.Text.Json;

namespace Wavelit.Core.Library.Sources;

/// <summary>
/// The hand-picked Featured list (featured.json, embedded). Every entry was checked by eye: scenery only,
/// no people or franchise characters. It stores links, never the media itself (nothing is re-hosted).
/// </summary>
public static class FeaturedCatalog
{
    private const string WallhavenLicense = "Community upload: rights belong to the creator. Personal use.";

    private sealed record Entry(
        string Source, string Id, string Kind, string Mood, string Title, int Width, int Height,
        string? Url, string? Thumb, string? Page, string? Author, string? License, double? Duration, List<EntryFile>? Files);

    private sealed record EntryFile(string Url, int Width, int Height, string Type, string Quality);

    private static readonly Lazy<IReadOnlyList<(string Mood, SearchResult Result)>> All = new(Load);

    /// <summary>
    /// Featured picks for a mood. "Featured" returns everything, interleaved across moods so videos and
    /// stills of every kind show near the top.
    /// </summary>
    public static IReadOnlyList<SearchResult> For(Mood mood)
    {
        var entries = mood == Moods.Featured ? All.Value : All.Value.Where(e => e.Mood == mood.Id).ToList();
        // Two stills, then a video, each stream round-robin across moods.
        var stills = new Queue<SearchResult>(AcrossMoods(entries.Where(e => e.Result.Item.Kind != WallpaperKind.Video)));
        var videos = new Queue<SearchResult>(AcrossMoods(entries.Where(e => e.Result.Item.Kind == WallpaperKind.Video)));
        var mixed = new List<SearchResult>(entries.Count);
        while (stills.Count + videos.Count > 0)
        {
            for (int i = 0; i < 2 && stills.Count > 0; i++) mixed.Add(stills.Dequeue());
            if (videos.Count > 0) mixed.Add(videos.Dequeue());
        }
        return mixed;
    }

    private static IEnumerable<SearchResult> AcrossMoods(IEnumerable<(string Mood, SearchResult Result)> entries)
    {
        var byMood = entries.GroupBy(e => e.Mood).Select(g => g.Select(e => e.Result).ToList()).ToList();
        for (int i = 0; byMood.Any(l => i < l.Count); i++)
            foreach (var list in byMood)
                if (i < list.Count) yield return list[i];
    }

    private static IReadOnlyList<(string, SearchResult)> Load()
    {
        using var stream = typeof(FeaturedCatalog).Assembly.GetManifestResourceStream("Wavelit.Core.Library.Sources.featured.json")
            ?? throw new InvalidOperationException("featured.json is missing from the build.");
        var entries = JsonSerializer.Deserialize<List<Entry>>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        return entries.Select(e => (e.Mood, ToResult(e))).Where(r => r.Item2 is not null).Select(r => (r.Mood, r.Item2!)).ToList();
    }

    private static SearchResult? ToResult(Entry e)
    {
        string? thumb = JsonSource.Https(e.Thumb), page = JsonSource.Https(e.Page);
        switch (e.Source)
        {
            case WallpaperSources.Wallhaven when JsonSource.Https(e.Url) is { } url:
                return new SearchResult(new WallpaperItem
                {
                    Id = $"wallhaven-{e.Id}",
                    Kind = WallpaperKind.Image,
                    Source = WallpaperSources.Wallhaven,
                    Title = $"{e.Title} · {e.Width}×{e.Height}",
                    PageUrl = page,
                    License = WallhavenLicense,
                    ThumbnailUrl = thumb,
                    RemoteUrl = url,
                    Width = e.Width,
                    Height = e.Height,
                });
            case WallpaperSources.Commons when e.Files is { Count: > 0 }:
                var files = e.Files.Select(f => JsonSource.Https(f.Url) is { } u ? new RemoteVideoFile(u, f.Width, f.Height, null, f.Type, f.Quality) : null)
                    .OfType<RemoteVideoFile>().ToList();
                if (files.Count == 0) return null;
                return new SearchResult(new WallpaperItem
                {
                    Id = CommonsSource.ItemId(e.Id),
                    Kind = WallpaperKind.Video,
                    Source = WallpaperSources.Commons,
                    Title = e.Title,
                    Author = e.Author,
                    PageUrl = page,
                    License = e.License,
                    ThumbnailUrl = thumb,
                    Width = e.Width,
                    Height = e.Height,
                    DurationSeconds = e.Duration,
                }, files);
            default:
                return null;
        }
    }
}
