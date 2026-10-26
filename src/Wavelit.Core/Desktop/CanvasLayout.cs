using Wavelit.Core.Interop;

namespace Wavelit.Core.Desktop;

/// <summary>
/// Where one wallpaper window sits on the shared wallpaper canvas. Canvas units, top-left origin (one unit
/// is one pixel of the primary display). The focus point is where the picture's subject belongs (the
/// primary display's centre) and <see cref="FocusSize"/> is that display's short side, so content keeps
/// the same scale on one monitor or many. A window's viewport may be larger or smaller than its pixel
/// size when physical-size matching is on; renderers scale accordingly.
/// </summary>
public sealed record CanvasView(int CanvasWidth, int CanvasHeight, RECT Viewport, double FocusX, double FocusY, double FocusSize)
{
    /// <summary>A window that is a canvas of its own (one display, Duplicate or Per-display mode).</summary>
    public static CanvasView Single(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        return new(width, height, new RECT(0, 0, width, height), width / 2.0, height / 2.0, Math.Min(width, height));
    }
}

/// <summary>An Extend layout: one canvas over every display, cut into one slice per display (keyed by monitor id).</summary>
public sealed record ExtendedCanvas(RECT Bounds, CanvasView Whole, IReadOnlyDictionary<string, CanvasView> Slices);

/// <summary>
/// Extend fine-tuning. <see cref="MatchPhysicalSize"/> sizes every display's slice by its real pixel size
/// (from EDID), so a horizon stays straight across a 24" and a 27" screen. <see cref="BezelGapMm"/> leaves
/// room for the frames between screens, as if the picture continued behind them.
/// </summary>
public sealed record CanvasOptions(bool MatchPhysicalSize = false, int BezelGapMm = 0)
{
    public CanvasOptions Normalize() => this with { BezelGapMm = Math.Clamp(BezelGapMm, 0, 60) };
}

public static class CanvasLayout
{
    private const double DefaultMmPerPixel = 0.25; // a typical 24" 1080p panel, when EDID is missing

    /// <summary>
    /// Builds the canvas from the displays' real Windows positions, so the picture continues across
    /// screens of any size, resolution and orientation.
    /// </summary>
    public static ExtendedCanvas Extend(IReadOnlyList<MonitorInfo> monitors, CanvasOptions? options = null)
    {
        if (monitors.Count == 0) throw new ArgumentException("At least one display is required.", nameof(monitors));
        options = (options ?? new CanvasOptions()).Normalize();

        // The subject goes on the primary display (or the biggest one), never on a bezel.
        var focus = monitors.FirstOrDefault(m => m.IsPrimary)
            ?? monitors.OrderByDescending(m => (long)m.Bounds.Width * m.Bounds.Height).First();
        double refMm = focus.MmPerPixel ?? DefaultMmPerPixel;
        double gap = options.BezelGapMm / refMm; // canvas units

        var rects = monitors.ToDictionary(m => m.Id, m => Adjust(m, focus, monitors, options, refMm, gap));
        double left = rects.Values.Min(r => r.L), top = rects.Values.Min(r => r.T);
        double right = rects.Values.Max(r => r.R), bottom = rects.Values.Max(r => r.B);
        int w = Math.Max(1, (int)Math.Round(right - left)), h = Math.Max(1, (int)Math.Round(bottom - top));

        var f = rects[focus.Id];
        double fx = (f.L + f.R) / 2 - left, fy = (f.T + f.B) / 2 - top;
        double size = Math.Max(1, Math.Min(focus.Bounds.Width, focus.Bounds.Height));

        RECT ToRect((double L, double T, double R, double B) r) => new(
            (int)Math.Round(r.L - left), (int)Math.Round(r.T - top), (int)Math.Round(r.R - left), (int)Math.Round(r.B - top));

        var slices = rects.ToDictionary(kv => kv.Key, kv => new CanvasView(w, h, ToRect(kv.Value), fx, fy, size));
        var bounds = new RECT((int)Math.Round(left), (int)Math.Round(top), (int)Math.Round(left) + w, (int)Math.Round(top) + h);
        return new ExtendedCanvas(bounds, new CanvasView(w, h, new RECT(0, 0, w, h), fx, fy, size), slices);
    }

    /// <summary>
    /// A display's rectangle on the canvas: its Windows position, scaled to its physical pixel size around
    /// the point nearest the primary display (so the shared edge stays lined up), then moved out by the
    /// bezel gap for every screen edge between it and the primary.
    /// </summary>
    private static (double L, double T, double R, double B) Adjust(MonitorInfo m, MonitorInfo primary,
        IReadOnlyList<MonitorInfo> all, CanvasOptions options, double refMm, double gap)
    {
        var b = m.Bounds;
        (double L, double T, double R, double B) r = (b.Left, b.Top, b.Right, b.Bottom);
        if (m.Id == primary.Id) return r;

        if (options.MatchPhysicalSize && m.MmPerPixel is { } mm)
        {
            double s = mm / refMm;
            var p = primary.Bounds;
            double ax = Math.Clamp((p.Left + p.Right) / 2.0, b.Left, b.Right);
            double ay = Math.Clamp((p.Top + p.Bottom) / 2.0, b.Top, b.Bottom);
            r = (ax + (b.Left - ax) * s, ay + (b.Top - ay) * s, ax + (b.Right - ax) * s, ay + (b.Bottom - ay) * s);
        }

        if (gap > 0)
        {
            var p = primary.Bounds;
            double dx = 0, dy = 0;
            if (b.Left >= p.Right) dx = gap * all.Count(o => o.Id != m.Id && o.Bounds.Right <= b.Left && o.Bounds.Right >= p.Right);
            else if (b.Right <= p.Left) dx = -gap * all.Count(o => o.Id != m.Id && o.Bounds.Left >= b.Right && o.Bounds.Left <= p.Left);
            if (b.Top >= p.Bottom) dy = gap * all.Count(o => o.Id != m.Id && o.Bounds.Bottom <= b.Top && o.Bounds.Bottom >= p.Bottom);
            else if (b.Bottom <= p.Top) dy = -gap * all.Count(o => o.Id != m.Id && o.Bounds.Top >= b.Bottom && o.Bounds.Top <= p.Top);
            r = (r.L + dx, r.T + dy, r.R + dx, r.B + dy);
        }
        return r;
    }
}
