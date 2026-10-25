using Microsoft.Extensions.Logging;
using Wavelit.Core.Interop;
using static Wavelit.Core.Interop.NativeMethods;

namespace Wavelit.Core.Desktop;

/// <summary>
/// The window our wallpaper windows get parented into, plus the window they must sit directly beneath.
/// </summary>
/// <param name="Parent">Window to SetParent into.</param>
/// <param name="InsertAfter">Sibling to place our window directly below (z-order), or 0 for the top of the parent.</param>
/// <param name="IconView">The SHELLDLL_DefView window that draws the desktop icons.</param>
/// <param name="IsRaisedDesktop">True for the Windows 11 24H2+ layout (DefView is a child of Progman).</param>
public sealed record DesktopLayer(nint Parent, nint InsertAfter, nint IconView, bool IsRaisedDesktop);

/// <summary>
/// Embeds windows between the desktop wallpaper and the desktop icons ("WorkerW trick").
/// Supports both the classic layout (Win10 – Win11 23H2) and the 24H2+ layout.
/// </summary>
public sealed class DesktopHost(ILogger<DesktopHost> logger)
{
    // Undocumented Progman message that makes Explorer split the wallpaper into its own WorkerW.
    private const uint WM_SPAWN_WORKER = 0x052C;

    public DesktopLayer? FindLayer()
    {
        nint progman = FindWindow("Progman", null);
        if (progman == 0)
        {
            logger.LogWarning("Progman not found (Explorer not running?)");
            return null;
        }

        // 0xD/0x1 is what current Explorer builds respond to; 0/0 is the legacy form.
        SendMessageTimeout(progman, WM_SPAWN_WORKER, 0xD, 0x1, SMTO_NORMAL, 1000, out _);
        var layer = Locate(progman);
        if (layer is null)
        {
            SendMessageTimeout(progman, WM_SPAWN_WORKER, 0, 0, SMTO_NORMAL, 1000, out _);
            layer = Locate(progman);
        }

        if (layer is null)
            logger.LogWarning("Could not locate the desktop wallpaper layer");
        else
            logger.LogInformation("Desktop layer found: parent={Parent:X} raised={Raised}", layer.Parent, layer.IsRaisedDesktop);
        return layer;
    }

    private static DesktopLayer? Locate(nint progman)
    {
        // Windows 11 24H2+: Progman → [SHELLDLL_DefView, (our window goes here), WorkerW].
        nint defView = FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
        if (defView != 0)
        {
            nint worker = FindWindowEx(progman, 0, "WorkerW", null);
            return worker != 0 ? new DesktopLayer(progman, defView, defView, IsRaisedDesktop: true) : null;
        }

        // Classic: a top-level WorkerW hosts DefView; the wallpaper WorkerW is the next top-level sibling.
        nint target = 0, icons = 0;
        EnumWindows((top, _) =>
        {
            nint dv = FindWindowEx(top, 0, "SHELLDLL_DefView", null);
            if (dv == 0) return true;
            icons = dv;
            target = FindWindowEx(0, top, "WorkerW", null);
            return false;
        }, 0);

        return target != 0 ? new DesktopLayer(target, 0, icons, IsRaisedDesktop: false) : null;
    }

    /// <summary>Parents <paramref name="hwnd"/> into the desktop layer and sizes it to <paramref name="screenBounds"/>.</summary>
    public bool Attach(nint hwnd, DesktopLayer layer, RECT screenBounds)
    {
        long style = GetWindowLongPtr(hwnd, GWL_STYLE);
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU);
        style |= WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
        SetWindowLongPtr(hwnd, GWL_STYLE, (nint)style);

        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex &= ~WS_EX_APPWINDOW;
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (layer.IsRaisedDesktop)
            ex |= WS_EX_LAYERED; // 24H2: a fully opaque layered child composites correctly under DefView.
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)ex);
        if (layer.IsRaisedDesktop)
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);

        SetParent(hwnd, layer.Parent);
        if (GetParent(hwnd) != layer.Parent)
        {
            logger.LogWarning("SetParent did not take effect for {Hwnd:X}", hwnd);
            return false;
        }

        Reposition(hwnd, layer, screenBounds);
        RedrawWindow(layer.IconView, 0, 0, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
        return true;
    }

    public static void Reposition(nint hwnd, DesktopLayer layer, RECT screenBounds)
    {
        // Convert screen coordinates to the parent's client coordinates (the virtual screen
        // can start at negative coordinates when a monitor is left of / above the primary).
        var r = screenBounds;
        MapWindowPoints(0, layer.Parent, ref r, 2);
        uint flags = SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED;
        nint after = layer.InsertAfter;
        if (after == 0) flags |= SWP_NOZORDER;
        SetWindowPos(hwnd, after, r.Left, r.Top, screenBounds.Width, screenBounds.Height, flags);
    }

    public static bool IsStillAttached(nint hwnd, DesktopLayer layer)
        => IsWindow(hwnd) && IsWindow(layer.Parent) && GetParent(hwnd) == layer.Parent;

    /// <summary>
    /// Forces Explorer to repaint the normal wallpaper after our windows are gone (otherwise the
    /// last rendered frame can stay stuck on the desktop). Deliberately does not call
    /// SPI_SETDESKWALLPAPER, which would break slideshow / Spotlight wallpapers.
    /// </summary>
    public static void RefreshStaticWallpaper()
    {
        const uint flags = RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW;
        nint progman = FindWindow("Progman", null);
        if (progman != 0) RedrawWindow(progman, 0, 0, flags);
        EnumWindows((top, _) =>
        {
            if (GetClassName(top) == "WorkerW") RedrawWindow(top, 0, 0, flags);
            return true;
        }, 0);
    }
}
