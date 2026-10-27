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
    private PointerFeed? _pointer;
    private AudioFeed? _audio;
    private CoreWebView2Environment? _environment;
    private DesktopLayer? _layer;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private readonly Dictionary<string, HostEntry> _hosts = new();
    private readonly Dictionary<string, Task<ResolvedWallpaper>> _resolving = new();
    // Items that can't be cut into per-display slices (web pages, multi-pass shaders); Extend shows
    // them in one window over every display instead.
    private readonly HashSet<string> _singleCanvasItems = new();
    // Shared timeline for Extend slices, so every display shows the same moment of the wallpaper.
    private (string? ItemId, long EpochMs) _canvasClock;
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
                _environment = await WebViewSetup.CreateEnvironmentAsync(_paths);
            }
            catch (WebView2RuntimeNotFoundException ex)
            {
                _logger.LogError(ex, "WebView2 runtime missing");
                Error?.Invoke(this, "Wavelit needs the Microsoft Edge WebView2 Runtime. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                return;
            }

            _conditions = new ConditionsMonitor(_thread!.Dispatcher, _logger);
            _pointer = new PointerFeed(_thread.Dispatcher, () => _hosts.Values.Select(e => (e.Host, e.IsSlice)));
            _audio = new AudioFeed(_thread.Dispatcher, () => _hosts.Values.Select(e => e.Host), _logger);
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

    /// <summary>Diagnostics (<c>--frame-stats</c>): logs every window's frame pacing (rate, p95 jitter).</summary>
    public void LogFrameStats() => Post(() =>
    {
        foreach (var entry in _hosts.Values) entry.Host.RequestFrameStats();
    });

    /// <summary>Reloads every window showing <paramref name="itemId"/> (e.g. after its effect changed).</summary>
    public void Reload(string itemId) => Post(() =>
    {
        var options = Options(_settings.Current);
        foreach (var entry in _hosts.Values.Where(e => e.DesiredItemId == itemId))
            StartLoad(entry, options);
    });

    private void OnSettingsChanged(object? sender, AppSettings e) => Post(RequestSync);

    private RenderOptions Options(AppSettings settings)
        => RenderOptions.From(settings) with { MonitorIds = _monitors.Select(m => m.Id).ToList() };

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

        var options = Options(settings);
        foreach (var spec in desired)
        {
            if (!_hosts.TryGetValue(spec.Key, out var entry))
            {
                var host = new WallpaperHost(spec.Key, spec.Monitors, _paths, _webRoot, _logger);
                entry = new HostEntry(host);
                _hosts[spec.Key] = entry;
                host.Failed += (_, message) => OnHostFailed(entry, message);
                host.Crashed += (_, _) => OnHostCrashed(entry);
                host.MediaSized += (_, size) => OnMediaSized(size.ItemId, size.Width, size.Height);
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

            entry.IsSlice = spec.IsSlice;
            entry.Host.SetView(spec.View, spec.ClockEpochMs);
            entry.Host.SetFraming(spec.ItemId is null ? null
                : settings.Framings.GetValueOrDefault(MediaFraming.Key(spec.ItemId, spec.View.CanvasWidth, spec.View.CanvasHeight)));
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

    /// <summary>
    /// The cursor feed runs only while a visible (Playing) wallpaper wants it: the interactive layer is on
    /// or the shader reacts to the mouse. Covered, frozen, paused or suspended wallpapers get nothing.
    /// </summary>
    private void UpdatePointerFeed()
    {
        if (_pointer is null) return;
        _pointer.ClickBursts = _settings.Current.Interactive.ClickBursts;
        _pointer.Enabled = !_disposed && !_userPaused
            && _hosts.Values.Any(e => e.Decision.State == PlaybackState.Playing && e.Host.NeedsPointer);
        UpdateAudioFeed();
    }

    /// <summary>Audio capture follows the same rule as the cursor: only while a Playing wallpaper listens.</summary>
    private void UpdateAudioFeed()
    {
        if (_audio is null) return;
        _audio.Enabled = !_disposed && !_userPaused
            && _hosts.Values.Any(e => e.Decision.State == PlaybackState.Playing && e.Host.NeedsAudio);
    }

    private sealed record HostSpec(string Key, IReadOnlyList<MonitorInfo> Monitors, RECT Bounds, string? ItemId,
        CanvasView View, long? ClockEpochMs, bool IsSlice);

    private List<HostSpec> DesiredHosts(AppSettings settings)
    {
        var d = settings.Displays;
        if (_monitors.Count == 0) return [];
        if (d.Layout == DisplayLayout.Span && _monitors.Count > 1)
        {
            if (NeedsSingleCanvas(d.SharedItemId))
            {
                // One window can't scale per display, so the fallback uses plain pixel positions.
                var plain = CanvasLayout.Extend(_monitors);
                return [new HostSpec(SpanKey, _monitors, plain.Bounds, d.SharedItemId, plain.Whole, null, IsSlice: false)];
            }
            var canvas = CanvasLayout.Extend(_monitors, settings.Canvas);

            // One window per display, each drawing its slice of the canvas on a shared clock. Keys match
            // the per-display hosts, so switching between Extend and Same-on-every-display needs no reload.
            if (_canvasClock.ItemId != d.SharedItemId)
                _canvasClock = (d.SharedItemId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return _monitors.Select(m => new HostSpec(m.Id, [m], m.Bounds, d.SharedItemId,
                canvas.Slices[m.Id], _canvasClock.EpochMs, IsSlice: true)).ToList();
        }
        return _monitors.Select(m => new HostSpec(m.Id, [m], m.Bounds, d.ItemFor(m.Id),
            CanvasView.Single(m.Bounds.Width, m.Bounds.Height), null, IsSlice: false)).ToList();
    }

    private bool NeedsSingleCanvas(string? itemId)
        => itemId is not null && (_singleCanvasItems.Contains(itemId) || _library.Find(itemId)?.Kind == WallpaperKind.Web);

    private static bool CanSlice(ResolvedWallpaper wallpaper) => wallpaper switch
    {
        ResolvedWallpaper.Web => false,
        // Feedback buffers would evolve differently in every slice.
        ResolvedWallpaper.Shader sh => sh.Program.Passes.All(p => p.Type != "buffer"),
        _ => true,
    };

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
            if (!CanSlice(resolved))
            {
                // Remembered even outside Extend, so switching to Extend later picks the right window layout.
                _singleCanvasItems.Add(item.Id);
                if (entry.IsSlice)
                {
                    _logger.LogInformation("{Item} can't be split per display; extending it as one window", item.Id);
                    RequestSync();
                    return;
                }
            }
            await entry.Host.LoadAsync(resolved, options, ct);
            entry.Error = null;
            _logger.LogInformation("Host {Key} now shows {Item}", entry.Host.Key, item.Id);
            UpdatePointerFeed();
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
        if (item.ThumbnailUrl is not null || item.IsBuiltIn || item.Kind == WallpaperKind.Image) return; // images are their own thumbnail
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
            StartLoad(entry, Options(_settings.Current));
        Publish(desktopAvailable: true);
    }

    // Local videos don't come with a size; remember it once the renderer knows it.
    private void OnMediaSized(string itemId, int width, int height)
    {
        if (_library.Find(itemId) is not { IsBuiltIn: false } item || (item.Width == width && item.Height == height)) return;
        _library.Upsert(item with { Width = width, Height = height });
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
            _logger.LogDebug("Host {Key} â†’ {State} ({Reason})", entry.Host.Key, decision.State, decision.Reason);
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

        UpdatePointerFeed();
        if (changed) Publish(desktopAvailable: _layer is not null);
    }

    private void Publish(bool desktopAvailable)
    {
        var hosts = _hosts.Values.Select(h => new HostStatus(
            h.Host.Key,
            h.Host.Monitors.Select(m => m.Id).ToList(),
            h.Host.LoadedItemId ?? h.DesiredItemId,
            h.Decision.State,
            h.LoadTask is not null ? "Loadingâ€¦" : h.Decision.Reason,
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

    /// <summary>
    /// Diagnostics (<c>--snapshot</c>): saves what every wallpaper window is drawing, laid out like the
    /// displays at half size, to logs/snapshot.png. Used to check that Extend lines up across the bezels.
    /// </summary>
    public async Task SaveSnapshotAsync()
    {
        if (_thread is null) return;
        await _thread.InvokeAsync(async () =>
        {
            if (_monitors.Count == 0) return;
            const double scale = 0.5;
            var all = MonitorService.GetVirtualBounds(_monitors);
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(System.Windows.Media.Brushes.Magenta, null, new System.Windows.Rect(0, 0, all.Width * scale, all.Height * scale));
                foreach (var entry in _hosts.Values.ToList())
                {
                    BitmapSource? frame = null;
                    try
                    {
                        frame = await entry.Host.CaptureFrameAsync();
                    }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
                    {
                        _logger.LogDebug(ex, "Snapshot of {Key} failed", entry.Host.Key);
                    }
                    if (frame is null) continue;
                    var b = entry.Host.Bounds;
                    dc.DrawImage(frame, new System.Windows.Rect((b.Left - all.Left) * scale, (b.Top - all.Top) * scale, b.Width * scale, b.Height * scale));
                }
            }
            var bitmap = new RenderTargetBitmap((int)(all.Width * scale), (int)(all.Height * scale), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(_paths.Logs, "snapshot.png");
            await using (var fs = File.Create(path))
                encoder.Save(fs);
            _logger.LogInformation("Saved wallpaper snapshot to {Path}", path);
        });
    }

    /// <summary>
    /// What one monitor shows right now (the Desktop editor's background), cropped from whichever window covers
    /// it. Null when nothing visible is there (no wallpaper, or the window is suspended behind a maximized app).
    /// </summary>
    public async Task<BitmapSource?> CaptureMonitorAsync(string monitorId)
    {
        if (_thread is null) return null;
        BitmapSource? result = null;
        await _thread.InvokeAsync(async () =>
        {
            var entry = _hosts.Values.FirstOrDefault(e => e.Host.Monitors.Any(m => m.Id == monitorId));
            var monitor = entry?.Host.Monitors.First(m => m.Id == monitorId);
            if (entry is null || monitor is null || entry.Host.LoadedItemId is null || entry.Decision.State == PlaybackState.Suspended) return;
            try
            {
                var frame = await entry.Host.CaptureFrameAsync();
                if (frame is null) return;
                var b = entry.Host.Bounds;
                double kx = frame.PixelWidth / (double)Math.Max(1, b.Width), ky = frame.PixelHeight / (double)Math.Max(1, b.Height);
                var crop = new System.Windows.Int32Rect(
                    (int)Math.Round((monitor.Bounds.Left - b.Left) * kx), (int)Math.Round((monitor.Bounds.Top - b.Top) * ky),
                    (int)Math.Round(monitor.Bounds.Width * kx), (int)Math.Round(monitor.Bounds.Height * ky));
                if (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0
                    || crop.X + crop.Width > frame.PixelWidth || crop.Y + crop.Height > frame.PixelHeight) { result = frame; return; }
                var cropped = new CroppedBitmap(frame, crop);
                cropped.Freeze();
                result = cropped;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException)
            {
                _logger.LogDebug(ex, "Capture of monitor {Monitor} failed", monitorId);
            }
        });
        return result;
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
                _pointer?.Dispose();
                _audio?.Dispose();
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
        public bool IsSlice { get; set; }

        public void Dispose()
        {
            LoadCts?.Cancel();
            LoadCts?.Dispose();
            LoadCts = null;
            Host.Dispose();
        }
    }
}
