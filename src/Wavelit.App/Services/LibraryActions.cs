using System.IO;
using Microsoft.Extensions.Logging;
using Wavelit.App.Wallpaper;
using Wavelit.Core.Desktop;
using Wavelit.Core.Library;
using Wavelit.Core.Library.BuiltIn;
using Wavelit.Core.Library.Shadertoy;
using Wavelit.Core.Storage;

namespace Wavelit.App.Services;

/// <summary>User-level operations shared by the dashboard pages and the tray.</summary>
public sealed class LibraryActions(
    LibraryStore library,
    SettingsStore settings,
    WallpaperResolver resolver,
    ShadertoyClient shadertoy,
    MediaCache cache,
    WallpaperService wallpapers,
    AppPaths paths,
    ILogger<LibraryActions> logger)
{
    /// <summary>
    /// Makes <paramref name="result"/> the wallpaper for <paramref name="monitorId"/> (null = all displays):
    /// downloads/validates it, saves it to the library and updates display assignments.
    /// </summary>
    public async Task<WallpaperItem> SetWallpaperAsync(SearchResult result, string? monitorId, IProgress<double>? progress, CancellationToken ct)
    {
        var item = library.Find(result.Item.Id) ?? result.Item;

        if (item.Source == WallpaperSources.Pexels && (item.FilePath is null || !cache.TryGet(item.FilePath, out _)))
        {
            item = await resolver.AcquireVideoAsync(result with { Item = item }, TargetHeight(monitorId), progress, ct);
        }
        else if (item.Source == WallpaperSources.Shadertoy)
        {
            // Validates the shader and pre-downloads its textures so the wallpaper switch is instant.
            await shadertoy.BuildProgramAsync(item.ShaderId ?? "", ct);
        }

        library.Upsert(item);
        Assign(item.Id, monitorId);
        _ = TrimCacheAsync();
        return item;
    }

    public void Assign(string itemId, string? monitorId)
    {
        var monitorIds = wallpapers.Snapshot.Monitors.Select(m => m.Id).ToList();
        settings.Update(s => s with { Displays = s.Displays.Assign(itemId, monitorId, monitorIds) });
    }

    public void SetLayout(DisplayLayout layout)
    {
        var monitorIds = wallpapers.Snapshot.Monitors.Select(m => m.Id).ToList();
        settings.Update(s => s with { Displays = s.Displays.WithLayout(layout, monitorIds) });
    }

    public IReadOnlyList<WallpaperItem> AddLocalFiles(IEnumerable<string> paths)
    {
        var added = new List<WallpaperItem>();
        foreach (var path in paths.Where(File.Exists).Where(LocalFiles.IsSupported))
            added.Add(library.Upsert(LocalFiles.CreateItem(path)));
        return added;
    }

    public WallpaperItem? AddUrl(string text)
    {
        var item = LocalFiles.CreateUrlItem(text);
        return item is null ? null : library.Upsert(item);
    }

    public void Remove(WallpaperItem item)
    {
        if (item.IsBuiltIn) return;
        settings.Update(s => s with { Displays = s.Displays.WithoutItem(item.Id, BuiltInShaders.DefaultItemId) });
        library.Remove(item.Id);
        if (item.Source == WallpaperSources.Pexels && item.FilePath is not null)
        {
            try { File.Delete(cache.PathFor(item.FilePath)); }
            catch (IOException ex) { logger.LogDebug(ex, "Could not delete cached file"); }
        }
        try { File.Delete(WallpaperService.CapturedThumbnailPath(paths, item.Id)); }
        catch (IOException) { }
    }

    public async Task TrimCacheAsync()
    {
        try
        {
            long limit = settings.Current.CacheLimitGb * 1024L * 1024 * 1024;
            await cache.EvictAsync(limit, InUseCacheFiles());
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Cache trim failed");
        }
    }

    public void ClearCache() => cache.Clear(InUseCacheFiles());

    private HashSet<string> InUseCacheFiles()
        => settings.Current.Displays.ReferencedItemIds()
            .Select(library.Find)
            .Where(i => i?.Source == WallpaperSources.Pexels && i.FilePath is not null)
            .Select(i => Path.GetFileName(i!.FilePath!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private int TargetHeight(string? monitorId)
    {
        IEnumerable<MonitorInfo> monitors = wallpapers.Snapshot.Monitors;
        if (monitorId is not null) monitors = monitors.Where(m => m.Id == monitorId);
        var list = monitors.ToList();
        return list.Count == 0 ? 1080 : list.Max(m => m.Bounds.Height);
    }
}
