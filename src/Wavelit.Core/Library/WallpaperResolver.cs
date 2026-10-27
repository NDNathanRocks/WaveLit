using Microsoft.Extensions.Logging;
using Wavelit.Core.Library.BuiltIn;
using Wavelit.Core.Library.Pexels;
using Wavelit.Core.Library.Shadertoy;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Library;

/// <summary>Everything a renderer needs, with all downloads already done.</summary>
public abstract record ResolvedWallpaper(WallpaperItem Item)
{
    public sealed record Video(WallpaperItem Item, string FilePath) : ResolvedWallpaper(Item);
    public sealed record Image(WallpaperItem Item, string FilePath) : ResolvedWallpaper(Item);
    public sealed record Shader(WallpaperItem Item, ShaderProgram Program) : ResolvedWallpaper(Item);
    /// <param name="LocalFolder">For local .html files: the folder mapped to <see cref="WebHosts.Local"/>.</param>
    public sealed record Web(WallpaperItem Item, string Url, string? LocalFolder) : ResolvedWallpaper(Item);
}

public static class LocalFiles
{
    public static readonly string[] VideoExtensions = [".mp4", ".m4v", ".webm", ".mov"];
    public static readonly string[] WebExtensions = [".html", ".htm"];
    public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".avif"];

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsWeb(string path) => WebExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsSupported(string path) => IsVideo(path) || IsWeb(path) || IsImage(path);

    /// <summary>The file extension to cache a downloaded image under (".jpg" when the URL doesn't say).</summary>
    public static string ImageExtensionFor(string url)
    {
        string ext = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() : "";
        return ImageExtensions.Contains(ext) ? ext : ".jpg";
    }

    public static WallpaperItem CreateItem(string path)
    {
        string full = Path.GetFullPath(path);
        string id = "local-" + MediaCache.FileNameFor(full.ToLowerInvariant(), "");
        string title = Path.GetFileNameWithoutExtension(full);
        if (IsWeb(full))
            return new WallpaperItem { Id = id, Kind = WallpaperKind.Web, Source = WallpaperSources.Local, Title = title, WebUrl = full };
        var kind = IsImage(full) ? WallpaperKind.Image : WallpaperKind.Video;
        return new WallpaperItem { Id = id, Kind = kind, Source = WallpaperSources.Local, Title = title, FilePath = full };
    }

    /// <summary>Validates a user-entered web address; only http(s) is allowed.</summary>
    public static WallpaperItem? CreateUrlItem(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "https://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host))
            return null;
        return new WallpaperItem
        {
            Id = "url-" + MediaCache.FileNameFor(uri.AbsoluteUri, ""),
            Kind = WallpaperKind.Web,
            Source = WallpaperSources.Url,
            Title = uri.Host,
            WebUrl = uri.AbsoluteUri,
            PageUrl = uri.AbsoluteUri,
        };
    }
}

public sealed class WallpaperResolver(MediaCache cache, ShadertoyClient shadertoy, ILogger<WallpaperResolver> logger)
{
    public async Task<ResolvedWallpaper> ResolveAsync(WallpaperItem item, CancellationToken ct)
    {
        switch (item.Kind)
        {
            case WallpaperKind.Video:
                return new ResolvedWallpaper.Video(item, await ResolveFileAsync(item, ct).ConfigureAwait(false));

            case WallpaperKind.Image:
                return new ResolvedWallpaper.Image(item, await ResolveFileAsync(item, ct).ConfigureAwait(false));

            case WallpaperKind.Shader:
                var program = item.IsBuiltIn
                    ? BuiltInShaders.Program(item.ShaderId ?? "") ?? throw new LibrarySourceException("Unknown built-in shader.")
                    : item.Source is WallpaperSources.Pasted or WallpaperSources.GeneratedShader
                        ? PastedShader.Program(item)
                        : await shadertoy.BuildProgramAsync(item.ShaderId ?? "", ct).ConfigureAwait(false);
                return new ResolvedWallpaper.Shader(item, program);

            case WallpaperKind.Web:
                string target = item.WebUrl ?? throw new LibrarySourceException("This wallpaper has no address.");
                if (item.IsLocalFile)
                {
                    if (!File.Exists(target)) throw new LibrarySourceException($"File not found: {target}");
                    string folder = Path.GetDirectoryName(target)!;
                    return new ResolvedWallpaper.Web(item, $"https://{WebHosts.Local}/{Uri.EscapeDataString(Path.GetFileName(target))}", folder);
                }
                return new ResolvedWallpaper.Web(item, target, null);

            default:
                throw new LibrarySourceException("Unsupported wallpaper type.");
        }
    }

    private async Task<string> ResolveFileAsync(WallpaperItem item, CancellationToken ct)
    {
        string what = item.Kind == WallpaperKind.Image ? "Image" : "Video";
        if (item.IsLocalFile)
        {
            return item.FilePath is { } p && File.Exists(p)
                ? p
                : throw new LibrarySourceException($"{what} file not found: {item.FilePath}");
        }

        if (item.FilePath is { } name && cache.TryGet(name, out string cached)) return cached;

        // Evicted from the cache (or not downloaded yet): fetch it again.
        if (item.RemoteUrl is null)
            throw new LibrarySourceException($"This {what.ToLowerInvariant()} is no longer available offline.");
        logger.LogInformation("Downloading {Kind} {Id}", what, item.Id);
        return await cache.DownloadAsync(item.RemoteUrl, item.FilePath ?? CacheNameFor(item), null, ct).ConfigureAwait(false);
    }

    private static string CacheNameFor(WallpaperItem item) => item.Kind == WallpaperKind.Image
        ? MediaCache.FileNameFor(item.RemoteUrl!, LocalFiles.ImageExtensionFor(item.RemoteUrl!))
        : MediaCache.FileNameFor(item.RemoteUrl!, VideoExtensionFor(item.RemoteUrl!));

    /// <summary>".webm" for Commons' VP9 transcodes, ".mp4" for everything else.</summary>
    internal static string VideoExtensionFor(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ? ".webm" : ".mp4";

    /// <summary>Downloads a search result's media (video or image) and returns the library item pointing at it.</summary>
    public async Task<WallpaperItem> AcquireAsync(SearchResult result, int targetHeight, IProgress<double>? progress, CancellationToken ct)
    {
        if (result.VideoFiles is { Count: > 0 }) return await AcquireVideoAsync(result, targetHeight, progress, ct).ConfigureAwait(false);
        var item = result.Item;
        if (item.RemoteUrl is null) return item;
        string fileName = item.FilePath ?? CacheNameFor(item);
        await cache.DownloadAsync(item.RemoteUrl, fileName, progress, ct).ConfigureAwait(false);
        return item with { FilePath = fileName };
    }

    /// <summary>Downloads the best file of a Pexels result for <paramref name="targetHeight"/> and returns the library item.</summary>
    public async Task<WallpaperItem> AcquireVideoAsync(SearchResult result, int targetHeight, IProgress<double>? progress, CancellationToken ct)
    {
        var file = VideoFileSelector.Select(result.VideoFiles ?? [], targetHeight)
            ?? throw new LibrarySourceException("No compatible video file for this video.");
        string fileName = MediaCache.FileNameFor(file.Url, file.FileType.Equals("video/webm", StringComparison.OrdinalIgnoreCase) ? ".webm" : ".mp4");
        await cache.DownloadAsync(file.Url, fileName, progress, ct).ConfigureAwait(false);
        return result.Item with { FilePath = fileName, RemoteUrl = file.Url, Width = file.Width, Height = file.Height };
    }
}
