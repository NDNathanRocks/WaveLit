namespace Wavelit.Core.Library;

/// <summary>
/// How a video or image sits on the wallpaper canvas, chosen in the framing editor. Centre is where the
/// media's centre goes, as a fraction of the canvas (0–1); zoom 1 = just covers the canvas.
/// Saved per wallpaper and canvas size, so a docked and an undocked setup each keep their own framing.
/// </summary>
public sealed record MediaFraming(double Zoom, double CenterX, double CenterY)
{
    public const double MaxZoom = 4;

    public static string Key(string itemId, int canvasWidth, int canvasHeight) => $"{itemId}@{canvasWidth}x{canvasHeight}";

    public MediaFraming Normalize() => new(
        double.IsFinite(Zoom) ? Math.Clamp(Zoom, 1, MaxZoom) : 1,
        double.IsFinite(CenterX) ? Math.Clamp(CenterX, -0.5, 1.5) : 0.5,
        double.IsFinite(CenterY) ? Math.Clamp(CenterY, -0.5, 1.5) : 0.5);
}

/// <summary>Placement maths shared by the renderer (host.js placeMedia) and the framing editor preview.</summary>
public static class MediaPlacement
{
    /// <summary>
    /// Where media of the given size is drawn on a canvas: covers it (or fits/fills), centred on the
    /// framing's centre or else on the focus point, as far as it can while still covering the canvas.
    /// </summary>
    public static (double X, double Y, double Width, double Height) Place(
        double mediaWidth, double mediaHeight, double canvasWidth, double canvasHeight,
        double focusX, double focusY, string fit, MediaFraming? framing)
    {
        if (mediaWidth <= 0 || mediaHeight <= 0) return (0, 0, canvasWidth, canvasHeight);
        double w = canvasWidth, h = canvasHeight;
        if (fit != "fill")
        {
            double s = fit == "contain"
                ? Math.Min(canvasWidth / mediaWidth, canvasHeight / mediaHeight)
                : Math.Max(canvasWidth / mediaWidth, canvasHeight / mediaHeight);
            w = mediaWidth * s;
            h = mediaHeight * s;
        }
        double cx = focusX, cy = focusY;
        if (framing is not null)
        {
            var f = framing.Normalize();
            w *= f.Zoom;
            h *= f.Zoom;
            cx = f.CenterX * canvasWidth;
            cy = f.CenterY * canvasHeight;
        }
        double x = Clamp(cx - w / 2, Math.Min(0, canvasWidth - w), Math.Max(0, canvasWidth - w));
        double y = Clamp(cy - h / 2, Math.Min(0, canvasHeight - h), Math.Max(0, canvasHeight - h));
        return (x, y, w, h);
    }

    private static double Clamp(double v, double lo, double hi) => Math.Min(hi, Math.Max(lo, v));
}
