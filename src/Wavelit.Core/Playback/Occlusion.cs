using Wavelit.Core.Desktop;
using Wavelit.Core.Interop;
using static Wavelit.Core.Interop.NativeMethods;

namespace Wavelit.Core.Playback;

/// <summary>A visible top-level application window, captured for occlusion analysis.</summary>
public sealed record WindowSnapshot(RECT Bounds, bool IsMaximized, string ClassName);

/// <summary>Which monitors are fully hidden behind application windows.</summary>
public sealed record OcclusionResult(IReadOnlySet<string> FullscreenMonitors, IReadOnlySet<string> MaximizedMonitors)
{
    public static readonly OcclusionResult None = new(new HashSet<string>(), new HashSet<string>());
}

/// <summary>Pure occlusion logic — unit tested without any real windows.</summary>
public static class OcclusionEvaluator
{
    public static OcclusionResult Evaluate(IEnumerable<WindowSnapshot> windows, IReadOnlyList<MonitorInfo> monitors)
    {
        var fullscreen = new HashSet<string>();
        var maximized = new HashSet<string>();

        foreach (var w in windows)
        {
            if (w.Bounds.Width <= 0 || w.Bounds.Height <= 0) continue;

            foreach (var m in monitors)
            {
                if (Covers(w.Bounds, m.Bounds))
                    fullscreen.Add(m.Id);
            }

            if (w.IsMaximized && BestMonitor(w.Bounds, monitors) is { } best)
                maximized.Add(best.Id);
        }

        return new OcclusionResult(fullscreen, maximized);
    }

    public static bool Covers(RECT window, RECT monitor)
        => window.Left <= monitor.Left && window.Top <= monitor.Top
        && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    private static MonitorInfo? BestMonitor(RECT r, IReadOnlyList<MonitorInfo> monitors)
    {
        MonitorInfo? best = null;
        long bestArea = 0;
        foreach (var m in monitors)
        {
            long w = Math.Min(r.Right, m.Bounds.Right) - Math.Max(r.Left, m.Bounds.Left);
            long h = Math.Min(r.Bottom, m.Bounds.Bottom) - Math.Max(r.Top, m.Bounds.Top);
            if (w <= 0 || h <= 0) continue;
            if (w * h > bestArea) { bestArea = w * h; best = m; }
        }
        return best;
    }
}

/// <summary>Captures the real top-level windows that could hide the wallpaper.</summary>
public static class WindowScanner
{
    // Shell surfaces that are fullscreen-sized but never hide the wallpaper from the user's point of view
    // (or are the desktop itself).
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "ForegroundStaging", "MultitaskingViewFrame", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "Shell_InputSwitchTopLevelWindow",
    };

    public static List<WindowSnapshot> Capture()
    {
        var result = new List<WindowSnapshot>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd)) return true;

            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            // Click-through overlays (game overlays, screen-recorder borders) are transparent and fullscreen.
            if ((ex & WS_EX_TRANSPARENT) != 0 || (ex & WS_EX_TOOLWINDOW) != 0) return true;

            string cls = GetClassName(hwnd);
            if (IgnoredClasses.Contains(cls)) return true;

            // DWM frame bounds exclude the invisible resize border, matching what the user sees.
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT bounds, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(hwnd, out bounds);

            result.Add(new WindowSnapshot(bounds, IsZoomed(hwnd), cls));
            return true;
        }, 0);
        return result;
    }
}
