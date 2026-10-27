using Wavelit.Core.Desktop;
using Wavelit.Core.Storage;

namespace Wavelit.App.Wallpaper;

/// <summary>Per-renderer options sent to the host page.</summary>
internal sealed record RenderOptions(int Fps, double Scale, string Fit, bool Muted, double Volume, bool SmoothLoop,
    InteractiveSettings Interactive, IReadOnlyDictionary<string, string> OverlayFor, IReadOnlyDictionary<string, CustomOverlay> CustomOverlays,
    AudioSettings Audio, DesktopSettings Desktop)
{
    /// <summary>Every monitor's id (widgets on a monitor that's gone fall back to the primary).</summary>
    public IReadOnlyCollection<string> MonitorIds { get; init; } = [];

    /// <summary>Slider values chosen for Studio wallpapers (item id → values).</summary>
    public IReadOnlyDictionary<string, double[]> ParamsFor { get; init; } = new Dictionary<string, double[]>();

    /// <summary>The overlay for a wallpaper: its own style if one was chosen for it, else the global one.</summary>
    public InteractiveSettings OverlayOf(string? itemId)
        => Interactive.For(itemId is not null && OverlayFor.TryGetValue(itemId, out var style) ? style : null);

    /// <summary>The GLSL of a Studio overlay (null for built-in styles).</summary>
    public CustomOverlay? CustomOf(InteractiveSettings overlay)
        => InteractiveSettings.IsCustom(overlay.Style) && CustomOverlays.TryGetValue(overlay.Style, out var c) ? c : null;

    /// <summary>The overlay needs the cursor: on, cursor-aware, and (for Studio overlays) its code reads the cursor.</summary>
    public bool OverlayUsesPointer(string? itemId)
    {
        var o = OverlayOf(itemId);
        return o.UsesPointer && (CustomOf(o) is not { } c || c.UsesPointer);
    }

    /// <summary>The overlay listens to the music (an audio overlay, Music on, or Studio code that reads audio).</summary>
    public bool OverlayUsesAudio(string? itemId)
    {
        var o = OverlayOf(itemId);
        return o.IsOn && (o.UsesAudio || CustomOf(o) is { } c && AudioSettings.CodeUsesAudio(c.Code));
    }

    public static RenderOptions From(AppSettings s) => new RenderOptions(
        s.ShaderFpsCap,
        s.ShaderRenderScale,
        s.VideoScaling switch { VideoScaling.Fit => "contain", VideoScaling.Stretch => "fill", _ => "cover" },
        s.Muted,
        s.Volume / 100.0,
        s.SmoothLoop,
        s.Interactive,
        s.OverlayFor,
        s.CustomOverlays,
        s.Audio,
        s.Desktop) { ParamsFor = s.ParamsFor };
}
