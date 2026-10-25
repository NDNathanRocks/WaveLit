using System.Text.Json.Serialization;

namespace Wavelit.Core.Library;

[JsonConverter(typeof(JsonStringEnumConverter<WallpaperKind>))]
public enum WallpaperKind { Video, Shader, Web }

public static class WallpaperSources
{
    public const string BuiltIn = "builtin";
    public const string Pexels = "pexels";
    public const string Shadertoy = "shadertoy";
    public const string Local = "local";
    public const string Url = "url";
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

    /// <summary>Video: absolute path for local files, or cache file name for downloaded files.</summary>
    public string? FilePath { get; init; }
    /// <summary>Video: remote URL of the chosen file so evicted cache entries can be re-downloaded.</summary>
    public string? RemoteUrl { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? DurationSeconds { get; init; }

    /// <summary>Web: https URL, or absolute path of a local .html file.</summary>
    public string? WebUrl { get; init; }

    /// <summary>Shader: built-in key or Shadertoy id.</summary>
    public string? ShaderId { get; init; }

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore] public bool IsBuiltIn => Source == WallpaperSources.BuiltIn;
    [JsonIgnore] public bool IsLocalFile => Source == WallpaperSources.Local;
}

/// <summary>A search result from an online source, before the user picks it.</summary>
public sealed record SearchResult(WallpaperItem Item, IReadOnlyList<RemoteVideoFile>? VideoFiles = null);

public sealed record RemoteVideoFile(string Url, int Width, int Height, double? Fps, string FileType, string Quality);

public sealed record SearchPage(IReadOnlyList<SearchResult> Results, bool HasMore);

public sealed class LibrarySourceException(string message, Exception? inner = null) : Exception(message, inner);
