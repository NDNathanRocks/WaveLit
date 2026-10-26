using Wavelit.Core.Storage;

namespace Wavelit.App.Wallpaper;

/// <summary>Per-renderer options sent to the host page.</summary>
internal sealed record RenderOptions(int Fps, double Scale, string Fit, bool Muted, double Volume, bool SmoothLoop,
    InteractiveSettings Interactive, IReadOnlyDictionary<string, string> OverlayFor)
{
    /// <summary>The overlay for a wallpaper: its own style if one was chosen for it, else the global one.</summary>
    public InteractiveSettings OverlayOf(string? itemId)
        => Interactive.For(itemId is not null && OverlayFor.TryGetValue(itemId, out var style) ? style : null);

    public static RenderOptions From(AppSettings s) => new(
        s.ShaderFpsCap,
        s.ShaderRenderScale,
        s.VideoScaling switch { VideoScaling.Fit => "contain", VideoScaling.Stretch => "fill", _ => "cover" },
        s.Muted,
        s.Volume / 100.0,
        s.SmoothLoop,
        s.Interactive,
        s.OverlayFor);
}
