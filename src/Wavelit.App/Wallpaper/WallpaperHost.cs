using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Wavelit.Core.Audio;
using Wavelit.Core.Desktop;
using Wavelit.Core.Interop;
using Wavelit.Core.Library;
using Wavelit.Core.Playback;
using Wavelit.Core.Storage;

namespace Wavelit.App.Wallpaper;

/// <summary>
/// One wallpaper surface: a child window inside the desktop layer hosting a WebView2 that renders
/// video, shaders or web pages. Lives entirely on the wallpaper UI thread.
/// </summary>
internal sealed class WallpaperHost : IDisposable
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int HTTRANSPARENT = -1;
    private const int MA_NOACTIVATE = 3;
    private static readonly string AppOrigin = $"https://{WebHosts.App}/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ILogger _logger;
    private readonly AppPaths _paths;
    private readonly string _webRoot;
    private readonly Grid _root = new() { Background = Brushes.Black };
    private readonly Image _snapshot = new() { Stretch = Stretch.Fill };
    private readonly WebView2 _web = new();
    private readonly Dictionary<int, TaskCompletionSource<string?>> _pending = new();
    private HwndSource? _source;
    private TaskCompletionSource? _ready;
    private RenderOptions? _options;
    private CanvasView? _view;
    private long? _clockEpochMs;
    private MediaFraming? _framing;
    private WallpaperKind? _kind;
    private string? _localFolder;
    private bool _cacheMapped;
    private int _nextRequestId;
    private bool _disposed;
    // State is what the service wants; _applied is what the renderer currently does (null = unknown,
    // e.g. after a load). Changes requested while initializing or loading are applied afterwards.
    private PlaybackState? _applied = PlaybackState.Playing;
    private bool _applying;
    private int _loads;
    private int _epoch;
    private bool _shaderUsesPointer;
    private bool _shaderUsesAudio;
    private bool _lastAudioSilent = true;
    private double[]? _sentParams;   // slider values the page has (Studio wallpapers)
    private string? _itemId;   // the item being shown or loaded (per-wallpaper overlay)
    private PointerSample? _lastPointer;

    public WallpaperHost(string key, IReadOnlyList<MonitorInfo> monitors, AppPaths paths, string webRoot, ILogger logger)
    {
        Key = key;
        Monitors = monitors;
        _paths = paths;
        _webRoot = webRoot;
        _logger = logger;
        _root.Children.Add(_snapshot);
        _root.Children.Add(_web);
    }

    public string Key { get; }
    public IReadOnlyList<MonitorInfo> Monitors { get; private set; }
    public RECT Bounds { get; private set; }
    public nint Handle => _source?.Handle ?? 0;
    public string? LoadedItemId { get; private set; }
    public PlaybackState State { get; private set; } = PlaybackState.Playing;
    public bool IsAlive => !_disposed && _source is { IsDisposed: false } && NativeMethods.IsWindow(Handle);
    public int? BrowserProcessId { get; private set; }
    public CanvasView? View => _view;

    /// <summary>
    /// The page wants the cursor: the interactive layer is on, or the shader reacts to the mouse.
    /// Never for web pages (they're third-party content and get no input from Wavelit).
    /// </summary>
    public bool NeedsPointer => !_disposed && LoadedItemId is not null && _kind is not (null or WallpaperKind.Web)
        && (_shaderUsesPointer || _options?.OverlayUsesPointer(_itemId) == true);

    /// <summary>
    /// The page listens to the music: an audio shader, or an overlay that reacts to it. Never for web pages.
    /// </summary>
    public bool NeedsAudio => !_disposed && LoadedItemId is not null && _kind is not (null or WallpaperKind.Web)
        && _options?.Audio.Enabled == true && (_shaderUsesAudio || _options.OverlayUsesAudio(_itemId));

    /// <summary>Raised when the renderer reports an error or crashes (message is user-facing).</summary>
    public event EventHandler<string>? Failed;
    /// <summary>Raised when the browser process died; the host must be recreated.</summary>
    public event EventHandler? Crashed;
    /// <summary>Raised with the pixel size of the loaded video or image (for framing and "fits your setup").</summary>
    public event EventHandler<(string ItemId, int Width, int Height)>? MediaSized;

    public async Task InitializeAsync(DesktopLayer layer, RECT bounds, CoreWebView2Environment env)
    {
        Bounds = bounds;
        var parameters = new HwndSourceParameters("WavelitWallpaper")
        {
            ParentWindow = layer.Parent,
            WindowStyle = (int)(NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS),
            ExtendedWindowStyle = (int)(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE),
            PositionX = 0,
            PositionY = 0,
            Width = Math.Max(1, bounds.Width),
            Height = Math.Max(1, bounds.Height),
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _source.CompositionTarget.BackgroundColor = Colors.Black;
        _source.RootVisual = _root;
        DesktopHost.Reposition(_source.Handle, layer, bounds);

        _web.DefaultBackgroundColor = System.Drawing.Color.Black;
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        BrowserProcessId = (int)core.BrowserProcessId;

        var s = core.Settings;
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDefaultScriptDialogsEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;
        s.IsWebMessageEnabled = true;

        core.SetVirtualHostNameToFolderMapping(WebHosts.App, _webRoot, CoreWebView2HostResourceAccessKind.Deny);
        ConfigureHosts(cache: true, localFolder: null);

        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += OnProcessFailed;
        core.IsMuted = true;

        await NavigateToHostPageAsync();
        await ApplyStateAsync(); // a pause/suspend may have been requested while we were starting
    }

    public void Reposition(DesktopLayer layer, RECT bounds, IReadOnlyList<MonitorInfo> monitors)
    {
        Monitors = monitors;
        if (_source is null) return;
        bool sizeChanged = bounds.Width != Bounds.Width || bounds.Height != Bounds.Height;
        Bounds = bounds;
        DesktopHost.Reposition(_source.Handle, layer, bounds);
        if (sizeChanged) SendResize();
    }

    public async Task LoadAsync(ResolvedWallpaper wallpaper, RenderOptions options, CancellationToken ct)
    {
        var core = _web.CoreWebView2 ?? throw new InvalidOperationException("Renderer not initialized.");
        _options = options;
        _loads++;
        _epoch++;
        _applied = null;
        _lastPointer = null;
        _shaderUsesPointer = false;
        _shaderUsesAudio = false;
        _itemId = wallpaper.Item.Id;
        if (core.IsSuspended) core.Resume(); // the page must run to load, even if it stays hidden

        try
        {
            switch (wallpaper)
            {
                case ResolvedWallpaper.Video v:
                {
                    var (src, folder) = MediaSource(v.FilePath);
                    await EnsureHostPageAsync(cache: true, localFolder: folder, ct);
                    _kind = WallpaperKind.Video;
                    core.IsMuted = options.Muted;
                    await SendLoadAsync(new { type = "video", src }, ct);
                    break;
                }

                case ResolvedWallpaper.Image img:
                {
                    var (src, folder) = MediaSource(img.FilePath);
                    await EnsureHostPageAsync(cache: true, localFolder: folder, ct);
                    _kind = WallpaperKind.Image;
                    core.IsMuted = true;
                    await SendLoadAsync(new { type = "image", src, effect = ImageEffects.Normalize(img.Item.Effect) }, ct);
                    break;
                }

                case ResolvedWallpaper.Shader sh:
                    await EnsureHostPageAsync(cache: true, localFolder: null, ct);
                    _kind = WallpaperKind.Shader;
                    _shaderUsesPointer = sh.Program.Passes.Any(p => PointerUniforms.IsMatch(p.Code));
                    _shaderUsesAudio = sh.Program.Passes.Any(p => AudioSettings.CodeUsesAudio(p.Code));
                    core.IsMuted = true;
                    _sentParams = options.ParamsFor.GetValueOrDefault(sh.Item.Id);
                    var program = _sentParams is { } chosen ? sh.Program with { Params = chosen } : sh.Program;
                    await SendLoadAsync(new { type = "shader", program }, ct);
                    break;

                case ResolvedWallpaper.Web w:
                    _kind = WallpaperKind.Web;
                    // Remote pages get no access to Wavelit's cache; local pages see only their own folder.
                    ConfigureHosts(cache: false, localFolder: w.LocalFolder);
                    core.IsMuted = options.Muted;
                    _ready = null;
                    core.Navigate(w.Url);
                    break;
            }
            LoadedItemId = wallpaper.Item.Id;
            ShowWindow(visible: true);
        }
        finally
        {
            _loads--;
            await ApplyStateAsync();
        }
    }

    public void ApplyOptions(RenderOptions options)
    {
        _options = options;
        var core = _web.CoreWebView2;
        if (core is null) return;
        if (_kind is WallpaperKind.Video or WallpaperKind.Web) core.IsMuted = options.Muted;
        Post(new { type = "options", options = PageOptions(options) });
        // Studio sliders apply live, without reloading the shader.
        if (_kind == WallpaperKind.Shader && _itemId is not null && options.ParamsFor.GetValueOrDefault(_itemId) is { } values
            && (_sentParams is null || !values.SequenceEqual(_sentParams)))
        {
            _sentParams = values;
            Post(new { type = "params", values });
        }
    }

    /// <summary>Asks the page for its frame pacing (logged when it answers; --frame-stats).</summary>
    public void RequestFrameStats() => Post(new { type = "stats" });

    /// <summary>
    /// Sets this window's place on the wallpaper canvas. In Extend mode every display draws its own slice
    /// on a shared clock (<paramref name="clockEpochMs"/>, Unix ms); otherwise the window is its own canvas.
    /// </summary>
    public void SetView(CanvasView view, long? clockEpochMs)
    {
        if (view == _view && clockEpochMs == _clockEpochMs) return;
        _view = view;
        _clockEpochMs = clockEpochMs;
        PostView();
    }

    /// <summary>The framing-editor placement for videos and images (null = automatic, centred on the focus).</summary>
    public void SetFraming(MediaFraming? framing)
    {
        if (framing == _framing) return;
        _framing = framing;
        PostFraming();
    }

    /// <summary>Tells the page where the cursor is (only when it changed). See PointerFeed.</summary>
    public void SendPointer(PointerSample sample)
    {
        if (_lastPointer == sample || _kind is null or WallpaperKind.Web) return;
        _lastPointer = sample;
        Post(new { type = "pointer", x = sample.X, y = sample.Y, over = sample.Over, down = sample.Down });
    }

    public PointerSample? LastPointer => _lastPointer;

    /// <summary>Sends one music analysis (AudioFeed, ~60 a second while playing; one frame once it goes quiet).</summary>
    public void SendAudio(AudioFrame f)
    {
        if (_kind is null or WallpaperKind.Web || f.Silent && _lastAudioSilent) return;
        _lastAudioSilent = f.Silent;
        var bands = new double[f.Bands.Length];
        for (int i = 0; i < bands.Length; i++) bands[i] = Math.Round(f.Bands[i], 3);
        Post(new
        {
            type = "audio",
            b = bands,
            s = new[] { Math.Round(f.Bass, 3), Math.Round(f.Mid, 3), Math.Round(f.Treble, 3), Math.Round(f.Level, 3), Math.Round(f.Centroid, 3) },
            beat = f.Beat,
            bpm = f.Bpm,
            n = f.BeatCount,
            silent = f.Silent,
        });
    }

    private static readonly System.Text.RegularExpressions.Regex PointerUniforms =
        new(@"\b(iMouse|iPointer|iClick|iTrail)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void PostFraming()
        => Post(new { type = "framing", framing = _framing is { } f ? new { zoom = f.Zoom, x = f.CenterX, y = f.CenterY } : null });

    private void PostView()
    {
        if (_view is not { } v) return;
        Post(new
        {
            type = "view",
            canvas = new { width = v.CanvasWidth, height = v.CanvasHeight },
            viewport = new { x = v.Viewport.Left, y = v.Viewport.Top, width = v.Viewport.Width, height = v.Viewport.Height },
            focus = new { x = v.FocusX, y = v.FocusY, size = v.FocusSize },
            clockEpoch = _clockEpochMs,
        });
    }

    /// <summary>Shows nothing (the normal Windows wallpaper shows through), e.g. after a load error.</summary>
    public void Clear()
    {
        LoadedItemId = null;
        Post(new { type = "clear" });
        ShowWindow(visible: false);
    }

    public Task SetStateAsync(PlaybackState state)
    {
        // A wallpaper that stops playing forgets the cursor, so it doesn't resume with a stale one.
        if (state != PlaybackState.Playing && _lastPointer is { Over: true } p) SendPointer(PointerSample.Away(p));
        State = state;
        return ApplyStateAsync();
    }

    private async Task ApplyStateAsync()
    {
        if (_applying || _loads > 0 || _disposed || _web.CoreWebView2 is null) return;
        _applying = true;
        try
        {
            while (State != _applied && !_disposed && _loads == 0)
            {
                var target = State;
                int epoch = _epoch;
                _logger.LogDebug("Host {Key}: {From} → {To}", Key, _applied?.ToString() ?? "unknown", target);
                await ApplyAsync(target);
                // A load that started meanwhile invalidates what we just applied; go round again.
                if (epoch == _epoch) _applied = target;
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private async Task ApplyAsync(PlaybackState target)
    {
        switch (target)
        {
            case PlaybackState.Playing:
                await ResumeAsync();
                break;

            case PlaybackState.Frozen when _kind != WallpaperKind.Web:
                // Visible but still: stopping the page's render loop is enough (≈0% GPU) and keeps the frame on screen.
                await ResumeAsync(sendResume: false);
                Post(new { type = "pause" });
                break;

            case PlaybackState.Frozen:
                // Arbitrary web pages can't be paused from outside; freeze a screenshot and suspend the page.
                await CaptureSnapshotAsync();
                await SuspendAsync();
                break;

            case PlaybackState.Suspended:
                Post(new { type = "pause" });
                // Hold the last frame under the hidden renderer, so nothing else shows through when it wakes.
                if (_snapshot.Source is null && _web.Visibility == Visibility.Visible && LoadedItemId is not null) await CaptureSnapshotAsync();
                await SuspendAsync();
                break;
        }
    }

    /// <summary>The frame currently on screen at full size (diagnostics).</summary>
    public Task<BitmapSource?> CaptureFrameAsync() => CaptureAsync(decodeWidth: 0);

    public Task<BitmapSource?> CaptureThumbnailAsync(int width)
        => State == PlaybackState.Playing ? CaptureAsync(width) : Task.FromResult<BitmapSource?>(null);

    private async Task ResumeAsync(bool sendResume = true)
    {
        var core = _web.CoreWebView2;
        if (core is null) return;
        _web.Visibility = Visibility.Visible;
        if (core.IsSuspended) core.Resume();
        core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        if (sendResume) Post(new { type = "resume" });
        await Task.Delay(150); // let the page present a frame before dropping the snapshot
        if (State != PlaybackState.Suspended) _snapshot.Source = null;
    }

    private async Task SuspendAsync()
    {
        var core = _web.CoreWebView2;
        if (core is null) return;
        _web.Visibility = Visibility.Hidden; // sets the controller invisible, which TrySuspendAsync requires
        await Task.Yield();
        try
        {
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            if (!core.IsSuspended) await core.TrySuspendAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            _logger.LogDebug(ex, "TrySuspendAsync failed for host {Key}", Key);
        }
    }

    private async Task CaptureSnapshotAsync()
    {
        try
        {
            _snapshot.Source = await CaptureAsync(decodeWidth: 0);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Snapshot failed for host {Key}", Key);
        }
    }

    private async Task<BitmapSource?> CaptureAsync(int decodeWidth)
    {
        var core = _web.CoreWebView2;
        if (core is null) return null;
        using var stream = new MemoryStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
        stream.Position = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
        bmp.StreamSource = stream;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>The URL a video or image file is served from, and the local folder that must be mapped for it (if any).</summary>
    private (string Src, string? Folder) MediaSource(string filePath)
    {
        string full = Path.GetFullPath(filePath);
        if (full.StartsWith(_paths.CacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            string relative = Path.GetRelativePath(_paths.CacheRoot, full);
            return (WebHosts.CacheUrl(string.Join('/', relative.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString))), null);
        }
        return ($"https://{WebHosts.Local}/{Uri.EscapeDataString(Path.GetFileName(full))}", Path.GetDirectoryName(full));
    }

    /// <summary>
    /// Sets which local folders the page may load from. WebView2 only applies mapping changes to
    /// documents loaded afterwards, so callers navigate after a change (returns true if anything changed).
    /// </summary>
    private bool ConfigureHosts(bool cache, string? localFolder)
    {
        var core = _web.CoreWebView2;
        if (core is null) return false;
        bool changed = false;
        if (cache != _cacheMapped)
        {
            if (cache) core.SetVirtualHostNameToFolderMapping(WebHosts.Cache, _paths.CacheRoot, CoreWebView2HostResourceAccessKind.Allow);
            else core.ClearVirtualHostNameToFolderMapping(WebHosts.Cache);
            _cacheMapped = cache;
            changed = true;
        }
        if (!string.Equals(localFolder, _localFolder, StringComparison.OrdinalIgnoreCase))
        {
            if (_localFolder is not null) core.ClearVirtualHostNameToFolderMapping(WebHosts.Local);
            if (localFolder is not null)
                core.SetVirtualHostNameToFolderMapping(WebHosts.Local, localFolder, CoreWebView2HostResourceAccessKind.DenyCors);
            _localFolder = localFolder;
            changed = true;
        }
        return changed;
    }

    private async Task EnsureHostPageAsync(bool cache, string? localFolder, CancellationToken ct)
    {
        if (ConfigureHosts(cache, localFolder) || _ready is null) await NavigateToHostPageAsync();
        await _ready!.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
    }

    private Task NavigateToHostPageAsync()
    {
        _lastPointer = null;
        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _web.CoreWebView2.Navigate(AppOrigin + "host.html");
        return _ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private async Task SendLoadAsync(object message, CancellationToken ct)
    {
        int id = ++_nextRequestId;
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            if (_options is not null) Post(new { type = "options", options = PageOptions(_options) });
            SendResize();
            PostView();
            PostFraming();
            Post(MergeId(message, id));
            if (State != PlaybackState.Playing) Post(new { type = "pause" });
            string? error;
            try
            {
                error = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Host {Key}: renderer did not confirm load in time", Key);
                return;
            }
            if (error is not null) throw new LibrarySourceException(error);
        }
        finally
        {
            _pending.Remove(id);
        }
    }

    private static Dictionary<string, object?> MergeId(object message, int id)
    {
        var dict = JsonSerializer.SerializeToElement(message, Json).EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value);
        dict["id"] = id;
        return dict;
    }

    private object PageOptions(RenderOptions o) => new
    {
        fps = o.Fps,
        scale = o.Scale,
        fit = o.Fit,
        muted = o.Muted,
        volume = o.Volume,
        smoothLoop = o.SmoothLoop,
        interactive = OverlayOptions(o.OverlayOf(_itemId), o),
        audio = new { sensitivity = o.Audio.Sensitivity, reduceFlashing = o.Audio.ReduceFlashing },
        widgets = new { items = WidgetOptions(o) },
    };

    /// <summary>
    /// The widgets this window draws: those on its monitors, each with its monitor's rectangle relative to this
    /// window (physical px). Never on web pages (they replace the host page).
    /// </summary>
    private List<object> WidgetOptions(RenderOptions o)
    {
        var list = new List<object>();
        if (!o.Desktop.WidgetsOn || _kind == WallpaperKind.Web) return list;
        foreach (var monitor in Monitors)
        {
            var area = new { x = monitor.Bounds.Left - Bounds.Left, y = monitor.Bounds.Top - Bounds.Top, width = monitor.Bounds.Width, height = monitor.Bounds.Height };
            foreach (var w in o.Desktop.On(monitor, o.MonitorIds))
                list.Add(new
                {
                    id = w.Id, kind = w.Kind, x = w.X, y = w.Y, align = w.Align, font = w.Font, weight = w.Weight, size = w.Size,
                    color = w.Color, opacity = w.Opacity, spacing = w.Spacing, upper = w.Upper, italic = w.Italic,
                    effect = w.Effect, format = w.Format, text = w.Text, area,
                });
        }
        return list;
    }

    private static object OverlayOptions(InteractiveSettings i, RenderOptions o) => new
    {
        style = i.Style,
        code = o.CustomOf(i)?.Code,
        amount = i.Amount,
        color = i.Color,
        size = i.Size,
        speed = i.Speed,
        response = i.Response,
        opacity = i.Opacity,
        music = i.Music && o.Audio.Enabled,
        @params = o.CustomOf(i)?.Params,
    };

    private void SendResize() => Post(new { type = "resize", width = Bounds.Width, height = Bounds.Height });

    private void Post(object message)
    {
        if (_disposed || _web.CoreWebView2 is null || _kind == WallpaperKind.Web && _ready is null) return;
        try
        {
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            _logger.LogDebug(ex, "PostWebMessage failed for host {Key}", Key);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) { e.Cancel = true; return; }
        bool isHostPage = uri.Host.Equals(WebHosts.App, StringComparison.OrdinalIgnoreCase);
        bool allowed = _kind == WallpaperKind.Web
            ? uri.Scheme is "https" or "http"
            : isHostPage && uri.Scheme == "https";
        if (!allowed)
        {
            e.Cancel = true;
            _logger.LogInformation("Blocked navigation to {Url}", Redact.Url(e.Uri));
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only our own host page may talk to us; pages loaded as web wallpapers are ignored.
        if (!e.Source.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            int id = root.TryGetProperty("id", out var i) && i.TryGetInt32(out int n) ? n : 0;
            string? message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            if (root.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } d)
                _logger.LogWarning("Renderer error on host {Key}: {Message} ({Detail})", Key, message, d);
            switch (type)
            {
                case "ready":
                    _ready?.TrySetResult();
                    break;
                case "loaded":
                    if (_pending.TryGetValue(id, out var ok)) ok.TrySetResult(null);
                    break;
                case "media":
                    if (LoadedItemId is { } item
                        && root.TryGetProperty("width", out var mw) && mw.TryGetInt32(out int w) && w is > 0 and <= 32768
                        && root.TryGetProperty("height", out var mh) && mh.TryGetInt32(out int h) && h is > 0 and <= 32768)
                        MediaSized?.Invoke(this, (item, w, h));
                    break;
                case "overlayError":
                    _logger.LogWarning("Overlay failed on host {Key}: {Message}", Key, message);
                    break;
                case "stats":
                    _logger.LogInformation("Frame stats on host {Key}: {Stats}", Key,
                        root.TryGetProperty("items", out var items) ? items.GetRawText() : "[]");
                    break;
                case "error":
                    string text = string.IsNullOrWhiteSpace(message) ? "The wallpaper failed to render." : message;
                    if (_pending.TryGetValue(id, out var bad)) bad.TrySetResult(text);
                    else Failed?.Invoke(this, text);
                    break;
            }
        }
        catch (JsonException) { }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _logger.LogWarning("WebView2 process failed on host {Key}: {Kind} ({Reason})", Key, e.ProcessFailedKind, e.Reason);
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            Crashed?.Invoke(this, EventArgs.Empty);
        else if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            LoadedItemId = null; // forces the service to reload this host
            _ready = null;
            Failed?.Invoke(this, "The wallpaper renderer stopped responding and was restarted.");
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                handled = true;
                return HTTRANSPARENT;
            case WM_MOUSEACTIVATE:
                handled = true;
                return MA_NOACTIVATE;
        }
        return 0;
    }

    private void ShowWindow(bool visible)
    {
        if (Handle != 0) ShowWindowNative(Handle, visible ? 4 /* SW_SHOWNOACTIVATE */ : 0 /* SW_HIDE */);
    }

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowNative(nint hWnd, int nCmdShow);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var p in _pending.Values) p.TrySetCanceled();
        _pending.Clear();
        _ready?.TrySetCanceled();
        try
        {
            if (_web.CoreWebView2 is { } core)
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.WebMessageReceived -= OnWebMessage;
                core.ProcessFailed -= OnProcessFailed;
            }
        }
        catch (InvalidOperationException) { }
        _web.Dispose();
        _snapshot.Source = null;
        if (_source is { IsDisposed: false })
        {
            _source.RemoveHook(WndProc);
            _source.RootVisual = null;
            _source.Dispose();
        }
        _source = null;
    }
}
