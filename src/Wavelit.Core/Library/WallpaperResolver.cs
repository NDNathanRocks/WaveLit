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
    public sealed record Shader(WallpaperItem Item, ShaderProgram Program) : ResolvedWallpaper(Item);
    /// <param name="LocalFolder">For local .html files: the folder mapped to <see cref="WebHosts.Local"/>.</param>
    public sealed record Web(WallpaperItem Item, string Url, string? LocalFolder) : ResolvedWallpaper(Item);
}

public static class LocalFiles
{
    public static readonly string[] VideoExtensions = [".mp4", ".m4v", ".webm", ".mov"];
    public static readonly string[] WebExtensions = [".html", ".htm"];

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsWeb(string path) => WebExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsSupported(string path) => IsVideo(path) || IsWeb(path);

    public static WallpaperItem CreateItem(string path)
    {
        string full = Path.GetFullPath(path);
        string id = "local-" + MediaCache.FileNameFor(full.ToLowerInvariant(), "");
        string title = Path.GetFileNameWithoutExtension(full);
        return IsWeb(full)
            ? new WallpaperItem { Id = id, Kind = WallpaperKind.Web, Source = WallpaperSources.Local, Title = title, WebUrl = full }
            : new WallpaperItem { Id = id, Kind = WallpaperKind.Video, Source = WallpaperSources.Local, Title = title, FilePath = full };
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
                return new ResolvedWallpaper.Video(item, await ResolveVideoAsync(item, ct).ConfigureAwait(false));

            case WallpaperKind.Shader:
                var program = item.IsBuiltIn
                    ? BuiltInShaders.Program(item.ShaderId ?? "") ?? throw new LibrarySourceException("Unknown built-in shader.")
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

    private async Task<string> ResolveVideoAsync(WallpaperItem item, CancellationToken ct)
    {
        if (item.IsLocalFile)
        {
            return item.FilePath is { } p && File.Exists(p)
                ? p
                : throw new LibrarySourceException($"Video file not found: {item.FilePath}");
        }

        if (item.FilePath is { } name && cache.TryGet(name, out string cached)) return cached;

        // Evicted from the cache: fetch it again.
        if (item.RemoteUrl is null || item.FilePath is null)
            throw new LibrarySourceException("This video is no longer available offline.");
        logger.LogInformation("Re-downloading evicted video {Id}", item.Id);
        return await cache.DownloadAsync(item.RemoteUrl, item.FilePath, null, ct).ConfigureAwait(false);
    }

    /// <summary>Downloads the best file of a Pexels result for <paramref name="targetHeight"/> and returns the library item.</summary>
    public async Task<WallpaperItem> AcquireVideoAsync(SearchResult result, int targetHeight, IProgress<double>? progress, CancellationToken ct)
    {
        var file = VideoFileSelector.Select(result.VideoFiles ?? [], targetHeight)
            ?? throw new LibrarySourceException("No compatible MP4 file for this video.");
        string fileName = MediaCache.FileNameFor(file.Url, ".mp4");
        await cache.DownloadAsync(file.Url, fileName, progress, ct).ConfigureAwait(false);
        return result.Item with { FilePath = fileName, RemoteUrl = file.Url, Width = file.Width, Height = file.Height };
    }
}
