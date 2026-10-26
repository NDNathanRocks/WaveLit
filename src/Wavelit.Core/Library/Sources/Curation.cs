namespace Wavelit.Core.Library.Sources;

/// <summary>
/// A Discover "mood" chip. Each one is a curated multi-source query, never a raw keyword, so an empty
/// search box still shows wallpaper-grade results.
/// </summary>
/// <param name="WallhavenQuery">Tag query (people are excluded by <see cref="Curation.WallhavenQuery"/>).</param>
/// <param name="WallhavenCategories">Wallhaven's general/anime/people bit string ("110" = general + anime).</param>
/// <param name="VideoQuery">Keyword for stock-video sources (Pixabay, Pexels); null = their curated picks.</param>
/// <param name="CommonsQuery">Wikimedia Commons search terms; null = no Commons videos for this mood.</param>
public sealed record Mood(string Id, string Label, string WallhavenQuery, string WallhavenCategories, string? VideoQuery, string? CommonsQuery);

public static class Moods
{
    public static readonly Mood Featured = new("featured", "Featured", "", "110", null, "");
    public static readonly Mood Nature = new("nature", "Nature", "nature", "110", "nature", "landscape timelapse");
    public static readonly Mood Space = new("space", "Space", "space", "110", "space", "milky way timelapse");
    public static readonly Mood Minimal = new("minimal", "Minimal", "minimalism", "110", "minimal", "fog lake");
    public static readonly Mood Anime = new("anime", "Anime", "landscape", "010", "fantasy landscape", null);
    public static readonly Mood Neon = new("neon", "Neon", "cyberpunk", "110", "neon", "city night timelapse");
    public static readonly Mood Abstract = new("abstract", "Abstract", "abstract", "110", "abstract", null);
    public static readonly Mood Cozy = new("cozy", "Cozy", "lofi", "110", "rain", "rain window");

    public static IReadOnlyList<Mood> All { get; } = [Featured, Nature, Space, Minimal, Anime, Neon, Abstract, Cozy];

    public static Mood? Find(string? id) => All.FirstOrDefault(m => m.Id == id);
}

/// <summary>Quality rules that keep Discover to wallpaper-grade content (round 3, plan §16.3).</summary>
public static partial class Curation
{
    /// <summary>Wallhaven tag exclusions: curated feeds are scenery, not portraits or characters.</summary>
    public const string NoPeople = "-women -anime_girls -men -portrait -face -skull";

    /// <summary>The Featured feed's Wallhaven rail rotates through these tags, one per day.</summary>
    public static readonly IReadOnlyList<string> HomeTags =
        ["landscape", "anime landscape", "minimalism", "space", "digital art", "sunset", "nature", "mountains"];

    /// <summary>The Featured feed's Commons rail rotates through these, one per day.</summary>
    public static readonly IReadOnlyList<string> HomeVideoTerms =
        ["timelapse clouds", "aurora", "ocean waves", "waterfall", "milky way timelapse", "fog forest", "snow falling", "sunset timelapse"];

    public static T ForDay<T>(IReadOnlyList<T> items, DateOnly day) => items[day.DayNumber % items.Count];

    public static string WallhavenQuery(Mood mood, DateOnly day)
        => $"{(mood == Moods.Featured ? ForDay(HomeTags, day) : mood.WallhavenQuery)} {NoPeople}";

    public static string? CommonsQuery(Mood mood, DateOnly day)
        => mood == Moods.Featured ? ForDay(HomeVideoTerms, day) : mood.CommonsQuery;

    private static readonly string[] VideoTitleBlocklist =
    [
        "briefing", "interview", "live", "news", "episode", "talks", "explains", "explained", "launch", "conference",
        "this week", "behind the scenes", "expert", "panel", "podcast", "lecture", "anthem", "wikipedia", "update",
        "trailer", "promo", "countdown", "what is", "how", "why", "recap", "mission video", "media reel", "q&a",
    ];

    private static readonly string[] NasaTitleAllowlist =
        ["4k", "timelapse", "time lapse", "views", "from orbit", "from space", "aurora", "earth from", "night lights"];

    /// <summary>Rejects talks, news and promos ("We Asked a NASA Expert", "This Week @NASA").</summary>
    public static bool LooksLikeFootage(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Contains('?')) return false;
        string words = Words(title);
        return !VideoTitleBlocklist.Any(term => words.Contains($" {term} ", StringComparison.Ordinal));
    }

    /// <summary>NASA is mostly documentaries, so only clearly scenic titles pass.</summary>
    public static bool IsScenicNasaTitle(string? title)
        => LooksLikeFootage(title) && NasaTitleAllowlist.Any(term => Words(title!).Contains($" {term} ", StringComparison.Ordinal));

    /// <summary>Lower-case words padded with spaces, so terms match whole words ("live" never matches "olive").</summary>
    private static string Words(string text) => $" {WordBreaks().Replace(text.ToLowerInvariant(), " ")} ";

    [System.Text.RegularExpressions.GeneratedRegex("[^a-z0-9&]+")]
    private static partial System.Text.RegularExpressions.Regex WordBreaks();

    /// <summary>Openverse mixes scans and snapshots in; keep large, wide images only.</summary>
    public static bool IsWallpaperSizedImage(int? width, int? height)
    {
        if (width is not >= 1920 || height is not > 0) return false;
        double aspect = (double)width.Value / height.Value;
        return aspect is >= 1.3 and <= 2.6;
    }

    /// <summary>A loopable wallpaper clip: Full HD or better, landscape, between 8 s and 10 min.</summary>
    public static bool IsWallpaperVideo(int width, int height, double durationSeconds)
        => width >= 1920 && width >= height * 1.3 && durationSeconds is >= 8 and <= 600;
}
