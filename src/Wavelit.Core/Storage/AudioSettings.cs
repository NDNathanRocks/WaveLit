using System.Text.RegularExpressions;

namespace Wavelit.Core.Storage;

/// <summary>
/// Audio visualizers (plan §20). Wavelit listens to what the PC plays (WASAPI loopback of the default output,
/// never the microphone) only while a visible wallpaper or overlay uses audio; nothing is recorded or sent.
/// </summary>
/// <param name="Enabled">Off: audio wallpapers show their calm idle animation and nothing is captured.</param>
/// <param name="Sensitivity">0 … 1 (0.5 = as analysed).</param>
/// <param name="ReduceFlashing">Caps beat swells and keeps them under three a second (WCAG). On by default.</param>
/// <summary>A motion + resolution pair (Settings → Quality).</summary>
public sealed record QualityPreset(string Id, int Fps, double Scale);

/// <summary>The presets measured in plan §21.2, and the first-run pick.</summary>
public static class QualityPresets
{
    public static readonly QualityPreset Pristine = new("pristine", 144, 1.0);
    public static readonly QualityPreset Balanced = new("balanced", 60, 0.75);
    public static readonly QualityPreset Saver = new("saver", 30, 0.5);
    public static readonly QualityPreset Custom = new("custom", 0, 0);

    public static QualityPreset Of(AppSettings s)
        => new[] { Pristine, Balanced, Saver }.FirstOrDefault(p => p.Fps == s.ShaderFpsCap && Math.Abs(p.Scale - s.ShaderRenderScale) < 0.01) ?? Custom;

    /// <summary>First run: a graphics card with its own memory gets Balanced, integrated graphics get Saver.</summary>
    public static QualityPreset ForGpu(long dedicatedVideoMemoryBytes)
        => dedicatedVideoMemoryBytes >= 2L * 1024 * 1024 * 1024 ? Balanced : Saver;
}

public sealed partial record AudioSettings(bool Enabled = true, double Sensitivity = 0.5, bool ReduceFlashing = true)
{
    public AudioSettings Normalize() => this with
    {
        Sensitivity = double.IsFinite(Sensitivity) ? Math.Clamp(Sensitivity, 0, 1) : 0.5,
    };

    /// <summary>Shader code that reads the music (the runtime decides from the same uniforms).</summary>
    public static bool CodeUsesAudio(string? code) => code is not null && AudioUniforms().IsMatch(code);

    [GeneratedRegex(@"\b(iAudio|iBeat|iSpectrum|iPeaks|iCentroid|iAudioPresence)\b")]
    private static partial Regex AudioUniforms();
}
