using System.Windows.Threading;
using Wavelit.Core.Desktop;
using Wavelit.Core.Interop;

namespace Wavelit.App.Wallpaper;

/// <summary>
/// Tells interactive wallpapers where the cursor is. Polls <c>GetCursorPos</c> about 60 times a second on
/// the wallpaper thread, and only while <see cref="Enabled"/> (a visible wallpaper wants it). No input
/// hooks: nothing is intercepted, no latency is added, and the keyboard is never read. The cursor only
/// counts when it is over the bare desktop; over any app window the wallpaper is told it went away.
/// Positions stay on this PC: they go to Wavelit's own host page and are never logged or stored.
/// </summary>
internal sealed class PointerFeed : IDisposable
{
    private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, SM_SWAPBUTTON = 23;
    private const uint GA_ROOT = 2;
    private static readonly TimeSpan RootCacheLife = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _timer;
    private readonly Func<IEnumerable<(WallpaperHost Host, bool IsSlice)>> _hosts;
    private (POINT Point, bool Desktop, bool Down)? _last;
    private nint _cachedRoot;
    private bool _cachedRootIsDesktop;
    private DateTime _cachedAt;
    private bool _disposed;

    public PointerFeed(Dispatcher dispatcher, Func<IEnumerable<(WallpaperHost Host, bool IsSlice)>> hosts)
    {
        _hosts = hosts;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => Tick(), dispatcher);
        _timer.Stop();
    }

    /// <summary>Report clicks (bursts). Off: the button state is never read.</summary>
    public bool ClickBursts { get; set; } = true;

    public bool Enabled
    {
        get => _timer.IsEnabled;
        set
        {
            if (_disposed || value == _timer.IsEnabled) return;
            if (value)
            {
                _last = null;
                _timer.Start();
            }
            else
            {
                _timer.Stop();
                // Let the particles settle instead of freezing mid-swirl when the feed resumes.
                foreach (var (host, _) in _hosts())
                    if (host.LastPointer is { Over: true } p) host.SendPointer(PointerSample.Away(p));
            }
        }
    }

    private void Tick()
    {
        if (_disposed || !NativeMethods.GetCursorPos(out var point)) return;
        bool desktop = IsOverDesktop(point);
        bool down = desktop && ClickBursts && IsPrimaryButtonDown();
        if (_last is { } last && last.Point.X == point.X && last.Point.Y == point.Y && last.Desktop == desktop && last.Down == down) return;
        _last = (point, desktop, down);

        var hosts = _hosts().ToList();
        // The window under the cursor maps it onto its canvas; Extend slices share that canvas, so they all
        // get the same point and the effect continues across the bezel.
        (WallpaperHost Host, bool IsSlice)? under = desktop ? hosts.FirstOrDefault(h => Contains(h.Host.Bounds, point)) : null;
        (double X, double Y)? canvas = under is { Host.View: { } view } u ? PointerMapping.ToCanvas(point, u.Host.Bounds, view) : null;

        foreach (var (host, isSlice) in hosts)
        {
            if (!host.NeedsPointer || host.State != Core.Playback.PlaybackState.Playing) continue;
            bool shares = canvas is not null && (host == under!.Value.Host || isSlice && under.Value.IsSlice);
            if (shares) host.SendPointer(PointerSample.At(canvas!.Value, down));
            else if (host.LastPointer is { Over: true } p) host.SendPointer(PointerSample.Away(p));
        }
    }

    private static bool Contains(RECT r, POINT p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    private bool IsOverDesktop(POINT point)
    {
        nint window = NativeMethods.WindowFromPoint(point);
        nint root = window == 0 ? 0 : NativeMethods.GetAncestor(window, GA_ROOT);
        if (root == 0) return false;
        if (root != _cachedRoot || DateTime.UtcNow - _cachedAt > RootCacheLife)
        {
            _cachedRoot = root;
            _cachedRootIsDesktop = PointerMapping.IsDesktopRoot(NativeMethods.GetClassName(root));
            _cachedAt = DateTime.UtcNow;
        }
        return _cachedRootIsDesktop;
    }

    /// <summary>The primary button, honouring "switch primary and secondary buttons".</summary>
    private static bool IsPrimaryButtonDown()
    {
        int vk = NativeMethods.GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON;
        return (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
    }
}
