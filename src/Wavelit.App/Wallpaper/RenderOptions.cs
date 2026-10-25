using Wavelit.Core.Storage;

namespace Wavelit.App.Wallpaper;

/// <summary>Per-renderer options sent to the host page.</summary>
internal sealed record RenderOptions(int Fps, double Scale, string Fit, bool Muted, double Volume, bool SmoothLoop)
{
    public static RenderOptions From(AppSettings s) => new(
        s.ShaderFpsCap,
        s.ShaderRenderScale,
        s.VideoScaling switch { VideoScaling.Fit => "contain", VideoScaling.Stretch => "fill", _ => "cover" },
        s.Muted,
        s.Volume / 100.0,
        s.SmoothLoop);
}
