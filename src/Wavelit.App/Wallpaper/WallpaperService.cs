using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Wavelit.Core.Desktop;
using Wavelit.Core.Interop;
using Wavelit.Core.Library;
using Wavelit.Core.Playback;
using Wavelit.Core.Storage;

namespace Wavelit.App.Wallpaper;

public sealed record HostStatus(string Key, IReadOnlyList<string> MonitorIds, string? ItemId, PlaybackState State, string Reason, string? Error);

public sealed record WallpaperSnapshot(IReadOnlyList<MonitorInfo> Monitors, IReadOnlyList<HostStatus> Hosts, bool UserPaused, bool DesktopAvailable)
{
    public static readonly WallpaperSnapshot Empty = new([], [], false, true);
}

/// <summary>
/// Owns every wallpaper window. All state lives on a dedicated UI thread; public members are thread-safe
/// and may be called from anywhere. Keeps windows in sync with settings, monitors and Explorer restarts,
/// and drives pause/suspend decisions.
/// </summary>
public sealed class WallpaperService : IAsyncDisposable
{
    private const string SpanKey = "span";
    private readonly SettingsStore _settings;
    private readonly LibraryStore _library;
    private readonly WallpaperResolver _resolver;
    private readonly AppPaths _paths;
    private readonly DesktopHost _desktop;
    private readonly ILogger<WallpaperService> _logger;
    private readonly string _webRoot;

    private UiThread? _thread;
    private ConditionsMonitor? _conditions;
    private CoreWebView2Environment? _environment;
    private DesktopLayer? _layer;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private readonly Dictionary<string, HostEntry> _hosts = new();
    private readonly Dictionary<string, Task<ResolvedWallpaper>> _resolving = new();
    private bool _syncRunning;
    private bool _syncPending;
    private bool _userPaused;
    private bool _ecoQos;
    private bool _disposed;
    private DateTime _lastLayerAttempt;
    private volatile WallpaperSnapshot _snapshot = WallpaperSnapshot.Empty;
    private volatile int[] _browserProcessIds = [];

    public WallpaperService(SettingsStore settings, LibraryStore library, WallpaperResolver resolver, AppPaths paths,
        DesktopHost desktop, ILogger<WallpaperService> logger)
    {
        _settings = settings;
        _library = library;
        _resolver = resolver;
        _paths = paths;
        _desktop = desktop;
        _logger = logger;
        _webRoot = Path.Combine(AppContext.BaseDirectory, "Web");
    }

    /// <summary>Raised (on a background thread) when monitors, hosts or playback states change.</summary>
    public event EventHandler<WallpaperSnapshot>? Changed;

    /// <summary>Raised with a user-facing message when a wallpaper fails to load or crashes.</summary>
    public event EventHandler<string>? Error;

    /// <summary>Raised when a preview thumbnail was captured for an item (file is in the thumbnail cache).</summary>
    public event EventHandler<string>? ThumbnailCaptured;

    public WallpaperSnapshot Snapshot => _snapshot;

    public static string CapturedThumbnailPath(AppPaths paths, string itemId)
        => Path.Combine(paths.ThumbnailCache, "item-" + MediaCache.FileNameFor(itemId, ".jpg"));

    public Task StartAsync()
    {
        _thread = new UiThread("Wavelit.Wallpaper", ex => _logger.LogError(ex, "Unhandled error on wallpaper thread"));
        return _thread.InvokeAsync(async () =>
        {
            try
            {
                _environment = await CoreWebView2Environment.CreateAsync(null, _paths.WebView2, new CoreWebView2EnvironmentOptions
                {
                    // - autoplay: unmuted video may start without a click.
                    // - HardwareMediaKeyHandling/MediaSessionService: media keys and the Windows media flyout
                    //   must not pick up the wallpaper.
                    // - CalculateNativeWinOcclusion: Chromium thinks a window under the desktop icons is
                    //   hidden and stops painting it; Wavelit does its own occlusion handling.
                    AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required " +
                        "--disable-features=HardwareMediaKeyHandling,MediaSessionService,CalculateNativeWinOcclusion",
                });
            }
            catch (WebView2RuntimeNotFoundException ex)
            {
                _logger.LogError(ex, "WebView2 runtime missing");
                Error?.Invoke(this, "Wavelit needs the Microsoft Edge WebView2 Runtime. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                return;
            }

            _conditions = new ConditionsMonitor(_thread!.Dispatcher, _logger);
            _conditions.Tick += (_, _) => OnTick();
            _settings.Changed += OnSettingsChanged;
            RequestSync();
        });
    }

    public void SetUserPaused(bool paused) => Post(() =>
    {
        _userPaused = paused;
        if (_conditions is not null) _conditions.UserPaused = paused;
        _ = EvaluatePlaybackAsync();
    });

    /// <summary>Re-reads settings and reloads anything that changed.</summary>
    public void Refresh() => Post(RequestSync);

    private void OnSettingsChanged(object? sender, AppSettings e) => Post(RequestSync);

    private void Post(Action action)
    {
        if (_disposed || _thread is null) return;
        _thread.Dispatcher.BeginInvoke(action);
    }

    // ------------------------------------------------------------------ sync

    private void RequestSync()
    {
        if (_syncRunning)
        {
            _syncPending = true;
            return;
        }
        _ = SyncLoopAsync();
    }

    private async Task SyncLoopAsync()
    {
        _syncRunning = true;
        try
        {
            do
            {
                _syncPending = false;
                await SyncAsync();
            }
            while (_syncPending && !_disposed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Wallpaper sync failed");
        }
        finally
        {
            _syncRunning = false;
        }
    }

    private async Task SyncAsync()
    {
        if (_environment is null || _disposed) return;
        var settings = _settings.Current;
        _monitors = MonitorService.GetMonitors();

        if (_layer is null || !NativeMethods.IsWindow(_layer.Parent))
        {
            DisposeAllHosts();
            _layer = _desktop.FindLayer();
            _lastLayerAttempt = DateTime.UtcNow;
            if (_layer is null)
            {
                Publish(desktopAvailable: false);
                return;
            }
        }

        var desired = DesiredHosts(settings);

        foreach (var key in _hosts.Keys.Except(desired.Select(d => d.Key)).ToList())
        {
            _hosts[key].Dispose();
            _hosts.Remove(key);
        }

        var options = RenderOptions.From(settings);
        foreach (var spec in desired)
        {
            if (!_hosts.TryGetValue(spec.Key, out var entry))
            {
                var host = new WallpaperHost(spec.Key, spec.Monitors, _paths, _webRoot, _logger);
                entry = new HostEntry(host);
                _hosts[spec.Key] = entry;
                host.Failed += (_, message) => OnHostFailed(entry, message);
                host.Crashed += (_, _) => OnHostCrashed(entry);
                try
                {
                    await host.InitializeAsync(_layer, spec.Bounds, _environment);
                }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    _logger.LogError(ex, "Failed to create wallpaper window {Key}", spec.Key);
                    entry.Dispose();
                    _hosts.Remove(spec.Key);
                    continue;
                }
            }
            else
            {
                entry.Host.Reposition(_layer, spec.Bounds, spec.Monitors);
            }

            entry.Host.ApplyOptions(options);
            if (entry.DesiredItemId != spec.ItemId || entry.Host.LoadedItemId is null && spec.ItemId is not null && entry.LoadTask is null)
            {
                entry.DesiredItemId = spec.ItemId;
                StartLoad(entry, options);
            }
        }

        await EvaluatePlaybackAsync();
        Publish(desktopAvailable: true);
    }

    private sealed record HostSpec(string Key, IReadOnlyList<MonitorInfo> Monitors, RECT Bounds, string? ItemId);

    private List<HostSpec> DesiredHosts(AppSettings settings)
    {
        var d = settings.Displays;
        if (_monitors.Count == 0) return [];
        if (d.Layout == DisplayLayout.Span)
            return [new HostSpec(SpanKey, _monitors, MonitorService.GetVirtualBounds(_monitors), d.SharedItemId)];
        return _monitors.Select(m => new HostSpec(m.Id, [m], m.Bounds, d.ItemFor(m.Id))).ToList();
    }

    private void StartLoad(HostEntry entry, RenderOptions options)
    {
        entry.LoadCts?.Cancel();
        entry.LoadCts?.Dispose();
        var cts = new CancellationTokenSource();
        entry.LoadCts = cts;
        entry.LoadTask = LoadAsync(entry, entry.DesiredItemId, options, cts.Token);
    }

    private async Task LoadAsync(HostEntry entry, string? itemId, RenderOptions options, CancellationToken ct)
    {
        // Always complete asynchronously so StartLoad has stored LoadTask before the finally block clears it.
        await Task.Yield();
        try
        {
            var item = _library.Find(itemId);
            if (item is null)
            {
                entry.Error = null;
                entry.Host.Clear();
                return;
            }

            var resolved = await ResolveSharedAsync(item, ct);
            ct.ThrowIfCancellationRequested();
            await entry.Host.LoadAsync(resolved, options, ct);
            entry.Error = null;
            _logger.LogInformation("Host {Key} now shows {Item}", entry.Host.Key, item.Id);
            _ = CaptureThumbnailLaterAsync(entry, item, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is LibrarySourceException or IOException or HttpRequestException or TimeoutException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to load {Item} on {Key}", itemId, entry.Host.Key);
            entry.Error = ex.Message;
            entry.Host.Clear();
            Error?.Invoke(this, ex.Message);
        }
        finally
        {
            if (entry.LoadCts?.Token == ct) entry.LoadTask = null;
            Publish(desktopAvailable: true);
        }
    }

    // Duplicate mode loads the same item on every display; resolve (download) it once.
    private Task<ResolvedWallpaper> ResolveSharedAsync(WallpaperItem item, CancellationToken ct)
    {
        if (_resolving.TryGetValue(item.Id, out var existing)) return existing;
        var task = _resolver.ResolveAsync(item, CancellationToken.None);
        _resolving[item.Id] = task;
        _ = task.ContinueWith(_ => Post(() => _resolving.Remove(item.Id)), TaskScheduler.Default);
        return task.WaitAsync(ct);
    }

    private async Task CaptureThumbnailLaterAsync(HostEntry entry, WallpaperItem item, CancellationToken ct)
    {
        if (item.ThumbnailUrl is not null || item.IsBuiltIn) return;
        string path = CapturedThumbnailPath(_paths, item.Id);
        if (File.Exists(path)) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
            if (entry.Host.LoadedItemId != item.Id) return;
            var bitmap = await entry.Host.CaptureThumbnailAsync(480);
            if (bitmap is null) return;
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using (var fs = File.Create(path))
                encoder.Save(fs);
            ThumbnailCaptured?.Invoke(this, item.Id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Thumbnail capture failed for {Item}", item.Id);
        }
    }

    private void OnHostFailed(HostEntry entry, string message)
    {
        entry.Error = message;
        Error?.Invoke(this, message);
        // Renderer process restarts: reload what should be shown.
        if (entry.Host.LoadedItemId is null && entry.DesiredItemId is not null)
            StartLoad(entry, RenderOptions.From(_settings.Current));
        Publish(desktopAvailable: true);
    }

    private void OnHostCrashed(HostEntry entry)
    {
        if (!_hosts.TryGetValue(entry.Host.Key, out var current) || current != entry) return;
        entry.Dispose();
        _hosts.Remove(entry.Host.Key);
        RequestSync();
    }

    // ------------------------------------------------------------------ playback

    private void OnTick()
    {
        if (_disposed) return;

        // Explorer restarted, or our windows were destroyed with it.
        bool lost = _layer is not null && (!NativeMethods.IsWindow(_layer.Parent) || _hosts.Values.Any(h => !h.Host.IsAlive));
        if (lost)
        {
            _logger.LogInformation("Desktop layer lost (Explorer restarted?); re-attaching");
            _layer = null;
            RequestSync();
            return;
        }
        if (_layer is null && DateTime.UtcNow - _lastLayerAttempt > TimeSpan.FromSeconds(2))
        {
            RequestSync();
            return;
        }

        var monitors = MonitorService.GetMonitors();
        if (!MonitorsEqual(monitors, _monitors))
        {
            _logger.LogInformation("Display configuration changed ({Count} displays)", monitors.Count);
            RequestSync();
            return;
        }

        _ = EvaluatePlaybackAsync();
    }

    private static bool MonitorsEqual(IReadOnlyList<MonitorInfo> a, IReadOnlyList<MonitorInfo> b)
        => a.Count == b.Count && a.Zip(b).All(p => p.First.Id == p.Second.Id && p.First.Bounds.Equals(p.Second.Bounds));

    private async Task EvaluatePlaybackAsync()
    {
        if (_conditions is null || _disposed) return;
        var conditions = _conditions.Capture(_monitors);
        var rules = _settings.Current.Rules;
        bool changed = false;

        foreach (var entry in _hosts.Values.ToList())
        {
            var decision = PlaybackPlanner.Decide(entry.Host.Monitors.Select(m => m.Id).ToList(), conditions, rules);
            if (entry.Decision == decision) continue;
            entry.Decision = decision;
            changed = true;
            _logger.LogDebug("Host {Key} → {State} ({Reason})", entry.Host.Key, decision.State, decision.Reason);
            try
            {
                await entry.Host.SetStateAsync(decision.State);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                _logger.LogDebug(ex, "State change failed on {Key}", entry.Host.Key);
            }
        }

        bool allIdle = _hosts.Values.All(h => h.Decision.State == PlaybackState.Suspended);
        if (allIdle != _ecoQos)
        {
            _ecoQos = allIdle;
            EcoQos.Set(allIdle);
        }

        if (changed) Publish(desktopAvailable: _layer is not null);
    }

    private void Publish(bool desktopAvailable)
    {
        var hosts = _hosts.Values.Select(h => new HostStatus(
            h.Host.Key,
            h.Host.Monitors.Select(m => m.Id).ToList(),
            h.Host.LoadedItemId ?? h.DesiredItemId,
            h.Decision.State,
            h.LoadTask is not null ? "Loading…" : h.Decision.Reason,
            h.Error)).ToList();
        _snapshot = new WallpaperSnapshot(_monitors, hosts, _userPaused, desktopAvailable);
        try
        {
            // Browser, GPU, renderer and utility processes: everything a leak could hide in.
            _browserProcessIds = _environment?.GetProcessInfos().Select(p => p.ProcessId).Distinct().ToArray() ?? [];
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            _browserProcessIds = [];
        }
        Changed?.Invoke(this, _snapshot);
    }

    /// <summary>All WebView2 process ids (as of the last state change), for diagnostics.</summary>
    public IReadOnlyList<int> BrowserProcessIds => _browserProcessIds;

    // ------------------------------------------------------------------ teardown

    private void DisposeAllHosts()
    {
        foreach (var entry in _hosts.Values) entry.Dispose();
        _hosts.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed || _thread is null) return;
        _settings.Changed -= OnSettingsChanged;
        try
        {
            await _thread.InvokeAsync(() =>
            {
                _disposed = true;
                _conditions?.Dispose();
                DisposeAllHosts();
                if (_ecoQos) EcoQos.Set(false);
                DesktopHost.RefreshStaticWallpaper();
                return Task.CompletedTask;
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Wallpaper thread did not shut down in time");
        }
        _thread.Dispose();
    }

    private sealed class HostEntry(WallpaperHost host) : IDisposable
    {
        public WallpaperHost Host { get; } = host;
        public string? DesiredItemId { get; set; }
        public CancellationTokenSource? LoadCts { get; set; }
        public Task? LoadTask { get; set; }
        public PlaybackDecision Decision { get; set; } = PlaybackDecision.Play;
        public string? Error { get; set; }

        public void Dispose()
        {
            LoadCts?.Cancel();
            LoadCts?.Dispose();
            LoadCts = null;
            Host.Dispose();
        }
    }
}
