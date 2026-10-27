using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;
using Wavelit.App.Wallpaper;
using Wavelit.Core.Create.Shaders;
using Wavelit.Core.Library;
using Wavelit.Core.Storage;

namespace Wavelit.App.Dashboard.Controls;

/// <summary>
/// The Studio's WebView2 (Web/studio.html, the real wallpaper runtime): the live preview, and the lab that
/// compiles, times and thumbnails every shader before the user can use it. Created when the Studio page is shown
/// and disposed when it's left, so it costs nothing elsewhere.
/// </summary>
public sealed class StudioPreview : Border, ViewModels.IStudioSurface
{
    private static readonly string AppOrigin = $"https://{WebHosts.App}/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(45);

    private readonly Dictionary<int, TaskCompletionSource<LabReport>> _checks = new();
    private WebView2? _web;
    private TaskCompletionSource? _ready;
    private Task? _init;
    private int _nextId;

    public StudioPreview()
    {
        Background = new SolidColorBrush(Color.FromRgb(11, 11, 16));
        Unloaded += (_, _) => Shutdown();
    }

    /// <summary>App data paths (for the shared WebView2 profile); set before the control is loaded.</summary>
    public AppPaths? Paths { get; set; }

    /// <summary>Raised with a user-facing message when the preview can't show a shader.</summary>
    public event EventHandler<string>? PreviewFailed;

    public Task EnsureReadyAsync()
    {
        _init ??= InitializeWithRetryAsync();
        return _init;
    }

    // WebView2 can refuse a new controller while another environment on the same profile is still starting
    // (0x8007139F at app start, when the wallpapers start too). It succeeds a moment later.
    private async Task InitializeWithRetryAsync()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await InitializeAsync();
                return;
            }
            catch (COMException ex) when (attempt < 4)
            {
                Log.Debug(ex, "Studio preview: start attempt {Attempt} failed; retrying", attempt);
                ReleaseBrowser();
                await Task.Delay(700 * attempt);
            }
            catch
            {
                ReleaseBrowser();
                _init = null; // the next use tries again
                throw;
            }
        }
    }

    private async Task InitializeAsync()
    {
        if (Paths is null) throw new InvalidOperationException("StudioPreview.Paths is not set.");
        var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(11, 11, 16) };
        _web = web;
        Child = web;
        // The browser needs the control's own window: wait until it is part of the loaded page, then let layout
        // settle. Creating it from the page's Loaded handler fails with 0x8007139F (invalid state).
        if (!web.IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            web.Loaded += (_, _) => loaded.TrySetResult();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var env = await WebViewSetup.CreateEnvironmentAsync(Paths, studio: true);
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2;
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
        s.IsWebMessageEnabled = true;
        core.SetVirtualHostNameToFolderMapping(WebHosts.App, Path.Combine(AppContext.BaseDirectory, "Web"), CoreWebView2HostResourceAccessKind.Deny);
        core.NavigationStarting += (_, e) =>
        {
            // The Studio page only; it never navigates anywhere else.
            if (!e.Uri.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += OnProcessFailed;
        core.IsMuted = true;
        await NavigateAsync();
    }

    private Task NavigateAsync()
    {
        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _web!.CoreWebView2.Navigate(AppOrigin + "studio.html");
        return _ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>Shows a shader live. <paramref name="layout"/> is the user's Extend canvas (two-screen preview) or null.</summary>
    public async Task ShowAsync(ShaderKind kind, string code, bool usesPointer, object? layout, ShaderProgram? background, IReadOnlyList<double>? parameters)
    {
        var message = new
        {
            type = "preview",
            id = ++_nextId,
            kind = kind == ShaderKind.Overlay ? "overlay" : "wallpaper",
            code,
            usesPointer,
            layout,
            background,
            @params = parameters,
        };
        await EnsureReadyAsync();
        Post(message);
    }

    public void ClearPreview()
    {
        Post(new { type = "clear" });
    }

    /// <summary>Moves the preview's sliders live (no rebuild).</summary>
    public void SetParams(IReadOnlyList<double> values) => Post(new { type = "params", values });

    /// <summary>The speed limit for checks (plan §21.7: 3 ms, 2 ms on the Saver preset).</summary>
    public double BudgetMs { get; set; } = ShaderStudio.DefaultBudgetMs;

    /// <summary>Compiles and measures a shader in the real runtime (see Web/studio.js).</summary>
    public async Task<LabReport> CheckAsync(string code, ShaderKind kind, CancellationToken ct)
    {
        await EnsureReadyAsync();
        int id = ++_nextId;
        var tcs = new TaskCompletionSource<LabReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        _checks[id] = tcs;
        try
        {
            Post(new
            {
                type = "check",
                id,
                kind = kind == ShaderKind.Overlay ? "overlay" : "wallpaper",
                code,
                background = kind == ShaderKind.Overlay ? OverlayBackground : null,
                budget = BudgetMs,
            });
            return await tcs.Task.WaitAsync(CheckTimeout, ct);
        }
        catch (TimeoutException)
        {
            return LabReport.Failed("The check took too long, so the shader is far too heavy for a wallpaper.");
        }
        finally
        {
            _checks.Remove(id);
        }
    }

    /// <summary>What overlays are previewed and thumbnailed over: a calm, dark built-in.</summary>
    public static ShaderProgram? OverlayBackground { get; } = Core.Library.BuiltIn.BuiltInShaders.Program("moonlake");

    private void Post(object message)
    {
        if (_web?.CoreWebView2 is not { } core) return;
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            Log.Debug(ex, "Studio preview: post failed");
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            int id = root.TryGetProperty("id", out var i) && i.TryGetInt32(out int n) ? n : 0;
            switch (type)
            {
                case "ready":
                    _ready?.TrySetResult();
                    break;
                case "checked":
                    if (_checks.TryGetValue(id, out var tcs)) tcs.TrySetResult(ReadReport(root));
                    break;
                case "error":
                    string message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    PreviewFailed?.Invoke(this, "The preview couldn't start: " + message);
                    break;
            }
        }
        catch (JsonException) { }
    }

    private static LabReport ReadReport(JsonElement r)
    {
        static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetDouble(out double d) && double.IsFinite(d) ? d : 0;
        bool ok = r.TryGetProperty("ok", out var okv) && okv.ValueKind == JsonValueKind.True;
        string? error = r.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : null;
        if (!ok) return LabReport.Failed(error ?? "The shader failed to run.");
        byte[]? thumb = null;
        if (r.TryGetProperty("thumbnail", out var th) && th.ValueKind == JsonValueKind.String && th.GetString() is { Length: > 0 and < 4_000_000 } b64)
        {
            try { thumb = Convert.FromBase64String(b64); } catch (FormatException) { }
        }
        return new LabReport(true, null, Num(r, "frameMs"), Num(r, "p95Ms"),
            r.TryGetProperty("blank", out var bl) && bl.ValueKind == JsonValueKind.True, thumb);
    }

    private async void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Warning("Studio preview process failed: {Kind} ({Reason})", e.ProcessFailedKind, e.Reason);
        foreach (var tcs in _checks.Values) tcs.TrySetResult(LabReport.Failed("The preview crashed while running this shader (the graphics driver reset)."));
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            Shutdown();
            return;
        }
        try
        {
            await NavigateAsync();
            PreviewFailed?.Invoke(this, "The preview crashed and was restarted.");
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or COMException)
        {
            Log.Warning(ex, "Studio preview: restart failed");
        }
    }

    /// <summary>Releases the browser (when the Studio page is left).</summary>
    public void Shutdown()
    {
        foreach (var tcs in _checks.Values) tcs.TrySetCanceled();
        _checks.Clear();
        _ready?.TrySetCanceled();
        ReleaseBrowser();
        _init = null;
    }

    private void ReleaseBrowser()
    {
        if (_web is not null)
        {
            try
            {
                if (_web.CoreWebView2 is { } core)
                {
                    core.WebMessageReceived -= OnWebMessage;
                    core.ProcessFailed -= OnProcessFailed;
                }
            }
            catch (InvalidOperationException) { }
            _web.Dispose();
        }
        _web = null;
        Child = null;
    }
}
