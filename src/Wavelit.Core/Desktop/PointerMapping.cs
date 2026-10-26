using Wavelit.Core.Interop;

namespace Wavelit.Core.Desktop;

/// <summary>
/// Pure helpers for interactive wallpapers: is the cursor over the bare desktop, and where is it on a
/// wallpaper window's canvas.
/// </summary>
public static class PointerMapping
{
    /// <summary>
    /// The top-level window under the cursor is the desktop itself: Progman (24H2+, where the icons and
    /// Wavelit's windows are its children) or WorkerW (classic layout). Anything else is an app, the
    /// taskbar or a menu, and the wallpaper must ignore the cursor.
    /// </summary>
    public static bool IsDesktopRoot(string? rootClassName) => rootClassName is "Progman" or "WorkerW";

    /// <summary>
    /// Maps a point in virtual-screen pixels into the canvas units of a wallpaper window that covers
    /// <paramref name="bounds"/> and shows <paramref name="view"/>'s viewport. Null if the point is
    /// outside the window. With physical-size matching a viewport can differ in size from the window, so
    /// this scales as well as shifts.
    /// </summary>
    public static (double X, double Y)? ToCanvas(POINT point, RECT bounds, CanvasView view)
    {
        if (point.X < bounds.Left || point.X >= bounds.Right || point.Y < bounds.Top || point.Y >= bounds.Bottom) return null;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        var v = view.Viewport;
        double x = v.Left + (point.X - bounds.Left) * (double)v.Width / bounds.Width;
        double y = v.Top + (point.Y - bounds.Top) * (double)v.Height / bounds.Height;
        return (x, y);
    }
}

/// <summary>What one wallpaper window is told about the cursor (sent only when it changes).</summary>
/// <param name="X">Canvas units, rounded to a tenth.</param>
/// <param name="Over">The cursor is over the bare desktop on this window's canvas.</param>
/// <param name="Down">The primary button is held (only reported while <paramref name="Over"/>).</param>
public readonly record struct PointerSample(double X, double Y, bool Over, bool Down)
{
    public static PointerSample Away(PointerSample last) => last with { Over = false, Down = false };

    public static PointerSample At((double X, double Y) canvas, bool down)
        => new(Math.Round(canvas.X, 1), Math.Round(canvas.Y, 1), true, down);
}
