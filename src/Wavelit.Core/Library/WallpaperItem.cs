using System.Text.Json.Serialization;

namespace Wavelit.Core.Library;

[JsonConverter(typeof(JsonStringEnumConverter<WallpaperKind>))]
public enum WallpaperKind { Video, Shader, Web, Image }

public static class WallpaperSources
{
    public const string BuiltIn = "builtin";
    public const string Pexels = "pexels";
    public const string Shadertoy = "shadertoy";
    public const string Local = "local";
    public const string Url = "url";
    public const string Pixabay = "pixabay";
    public const string Wallhaven = "wallhaven";
    public const string Nasa = "nasa";
    public const string Openverse = "openverse";
    public const string Commons = "commons";
    /// <summary>Made on the Create page (AI image generation).</summary>
    public const string Generated = "generated";
    /// <summary>GLSL pasted by the user ("Paste a shader").</summary>
    public const string Pasted = "pasted";
}

/// <summary>
/// Something that can be shown as a wallpaper. Persisted in library.json, so every property
/// must stay backwards compatible (add, never rename).
/// </summary>
public sealed record WallpaperItem
{
    public required string Id { get; init; }
    public required WallpaperKind Kind { get; init; }
    public required string Source { get; init; }
    public required string Title { get; init; }

    public string? Author { get; init; }
    public string? AuthorUrl { get; init; }
    /// <summary>Public page for the item (Pexels/Shadertoy page) used for attribution links.</summary>
    public string? PageUrl { get; init; }
    public string? License { get; init; }
    public string? ThumbnailUrl { get; init; }
    /// <summary>Thumbnail captured from the live wallpaper (for items without a remote thumbnail).</summary>
    public string? ThumbnailPath { get; init; }

    /// <summary>Video/image: absolute path for local files, or cache file name for downloaded files.</summary>
    public string? FilePath { get; init; }
    /// <summary>Video/image: remote URL of the file so evicted cache entries can be re-downloaded.</summary>
    public string? RemoteUrl { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? DurationSeconds { get; init; }

    /// <summary>Web: https URL, or absolute path of a local .html file.</summary>
    public string? WebUrl { get; init; }

    /// <summary>Source-specific id used to fetch details later (e.g. NASA's asset id).</summary>
    public string? SourceRef { get; init; }

    /// <summary>Shader: built-in key or Shadertoy id.</summary>
    public string? ShaderId { get; init; }
    /// <summary>Shader: GLSL source for pasted shaders (Shadertoy mainImage convention).</summary>
    public string? ShaderCode { get; init; }

    /// <summary>Image: optional "living still" motion effect (see <see cref="ImageEffects"/>).</summary>
    public string? Effect { get; init; }
    /// <summary>Generated images: the prompt they were made from.</summary>
    public string? Prompt { get; init; }

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore] public bool IsBuiltIn => Source == WallpaperSources.BuiltIn;
    /// <summary>The file lives at an absolute path that Wavelit doesn't manage as cache (user files, creations).</summary>
    [JsonIgnore] public bool IsLocalFile => Source is WallpaperSources.Local or WallpaperSources.Generated;
    /// <summary>A video or image kept in Wavelit's media cache (as opposed to a user's own file).</summary>
    [JsonIgnore] public bool IsCachedMedia => Kind is WallpaperKind.Video or WallpaperKind.Image && !IsLocalFile;
}

/// <summary>Motion effects that turn a still image into a live wallpaper.</summary>
public static class ImageEffects
{
    public const string None = "none";
    public const string Drift = "drift";
    public const string Rain = "rain";
    public const string Snow = "snow";
    public const string Fireflies = "fireflies";
    public const string Dust = "dust";

    public static readonly IReadOnlyList<string> All = [None, Drift, Rain, Snow, Fireflies, Dust];

    public static string Normalize(string? effect) => effect is not null && All.Contains(effect) ? effect : None;
}

/// <summary>A search result from an online source, before the user picks it.</summary>
public sealed record SearchResult(WallpaperItem Item, IReadOnlyList<RemoteVideoFile>? VideoFiles = null);

public sealed record RemoteVideoFile(string Url, int Width, int Height, double? Fps, string FileType, string Quality);

public sealed record SearchPage(IReadOnlyList<SearchResult> Results, bool HasMore);

public sealed class LibrarySourceException(string message, Exception? inner = null) : Exception(message, inner);
