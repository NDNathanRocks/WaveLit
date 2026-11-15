namespace Wavelit.Core.Audio;

/// <summary>
/// A second clock that runs faster when the music is louder and surges on every beat: overlays that "move with
/// the music" advance their motion on it, so petals fall quicker on a kick and snow drifts with the volume.
/// Only the <em>extra</em> seconds beyond real time are kept (<see cref="Extra"/>): with no music the clock is
/// real time, and every Extend slice receives the same number, so a bezel never splits a petal in two.
/// </summary>
public sealed class MusicClock
{
    /// <summary>Slowest and fastest pace, as multiples of real time.</summary>
    public const double MinPace = 0.5, MaxPace = 2.6;
    private const double BeatDecaySeconds = 0.22;

    private double _beatEnvelope;

    /// <summary>Seconds the music clock is ahead of real time.</summary>
    public double Extra { get; private set; }

    /// <summary>The current pace (1 = real time).</summary>
    public double Pace { get; private set; } = 1;

    /// <summary>Advances by <paramref name="dt"/> seconds of real time given the latest analysis.</summary>
    public void Advance(AudioFrame frame, double dt)
    {
        if (!double.IsFinite(dt) || dt <= 0) return;
        dt = Math.Min(dt, 0.25);
        _beatEnvelope = Math.Max(_beatEnvelope * Math.Exp(-dt / BeatDecaySeconds), frame.Beat && !frame.Silent ? 1 : 0);
        double target = frame.Silent ? 1 : Math.Clamp(0.7 + 0.9 * frame.Level + 1.1 * _beatEnvelope, MinPace, MaxPace);
        Pace += (target - Pace) * Math.Min(1, dt * 8);
        Extra += (Pace - 1) * dt;
    }

    /// <summary>The frame with this clock's values attached (what the host page receives).</summary>
    public AudioFrame Stamp(AudioFrame frame) => frame with { MusicExtra = Extra, Pace = (float)Pace };
}
