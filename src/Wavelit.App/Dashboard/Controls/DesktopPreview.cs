using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;
using Wavelit.App.Wallpaper;
using Wavelit.Core.Library;
using Wavelit.Core.Storage;

namespace Wavelit.App.Dashboard.Controls;

/// <summary>
/// The Desktop editor's WebView2 (Web/desk.html): the chosen monitor's wallpaper with the real widgets on top,
/// draggable. Uses the Studio's browser profile; created when the Desktop page is shown and released when it's
/// left. It is a native window (airspace): keep it out of ScrollViewers and never overlap it with WPF content.
/// </summary>
public sealed class DesktopPreview : Border
{
    private static readonly string AppOrigin = $"https://{WebHosts.App}/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private WebView2? _web;
    private TaskCompletionSource? _ready;
    private Task? _init;
    private object? _pending;

    public DesktopPreview()
    {
        Background = new SolidColorBrush(Color.FromRgb(11, 11, 16));
        Unloaded += (_, _) => Shutdown();
    }

    public AppPaths? Paths { get; set; }

    public event EventHandler<string?>? Selected;
    public event EventHandler<(string Id, double X, double Y)>? Moved;
    public event EventHandler<(string Id, double Size)>? Resized;
    public event EventHandler<string>? DeleteRequested;

    /// <summary>Shows a scene (see Web/desk.js); the latest one wins if the browser is still starting.</summary>
    public async void ShowScene(object scene)
    {
        _pending = scene;
        try
        {
            _init ??= InitializeWithRetryAsync();
            await _init;
            if (_pending is { } latest)
            {
                _pending = null;
                Post(latest);
            }
        }
        catch (Exception ex) when (ex is COMException or TimeoutException or InvalidOperationException or WebView2RuntimeNotFoundException or OperationCanceledException)
        {
            Log.Warning(ex, "Desktop editor preview could not start");
        }
    }

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
                Log.Debug(ex, "Desktop preview: start attempt {Attempt} failed; retrying", attempt);
                ReleaseBrowser();
                await Task.Delay(700 * attempt);
            }
            catch
            {
                ReleaseBrowser();
                _init = null;
                throw;
            }
        }
    }

    private async Task InitializeAsync()
    {
        if (Paths is null) throw new InvalidOperationException("DesktopPreview.Paths is not set.");
        var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(11, 11, 16) };
        _web = web;
        Child = web;
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
            if (!e.Uri.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += OnProcessFailed;
        core.IsMuted = true;
        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.Navigate(AppOrigin + "desk.html");
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private void Post(object message)
    {
        if (_web?.CoreWebView2 is not { } core) return;
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            Log.Debug(ex, "Desktop preview: post failed");
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
            string? id = root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetDouble(out double d) && double.IsFinite(d) ? d : double.NaN;
            switch (type)
            {
                case "ready":
                    _ready?.TrySetResult();
                    break;
                case "select":
                    Selected?.Invoke(this, id);
                    break;
                case "move" when id is not null && !double.IsNaN(Num(root, "x")) && !double.IsNaN(Num(root, "y")):
                    Moved?.Invoke(this, (id, Num(root, "x"), Num(root, "y")));
                    break;
                case "resize" when id is not null && !double.IsNaN(Num(root, "size")):
                    Resized?.Invoke(this, (id, Num(root, "size")));
                    break;
                case "delete" when id is not null:
                    DeleteRequested?.Invoke(this, id);
                    break;
            }
        }
        catch (JsonException) { }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Warning("Desktop preview process failed: {Kind} ({Reason})", e.ProcessFailedKind, e.Reason);
        Shutdown();
    }

    public void Shutdown()
    {
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
