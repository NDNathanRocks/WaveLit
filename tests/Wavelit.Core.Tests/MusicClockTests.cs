using System.Text.RegularExpressions;
using Wavelit.Core.Audio;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Tests;

/// <summary>The music clock that moves overlays to the beat (MusicClock, wavelitMusic, ilMusic).</summary>
public class MusicClockTests
{
    private static string AppDir(params string[] parts)
        => Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Wavelit.App", .. parts]));

    private static AudioFrame Frame(float level, bool beat, bool silent = false)
        => AudioFrame.Quiet with { Level = level, Beat = beat, Silent = silent };

    [Fact]
    public void Silence_is_real_time()
    {
        var clock = new MusicClock();
        for (int i = 0; i < 120; i++) clock.Advance(Frame(0, false, silent: true), 1 / 60.0);
        Assert.Equal(0, clock.Extra, 6);
        Assert.Equal(1, clock.Pace, 6);
    }

    [Fact]
    public void Loud_music_runs_ahead_and_a_beat_surges()
    {
        var steady = new MusicClock();
        for (int i = 0; i < 120; i++) steady.Advance(Frame(0.8f, false), 1 / 60.0);
        Assert.True(steady.Extra > 0.5, $"extra {steady.Extra}");
        Assert.InRange(steady.Pace, 1.3, MusicClock.MaxPace);

        var beat = new MusicClock();
        for (int i = 0; i < 120; i++) beat.Advance(Frame(0.8f, i % 30 == 0), 1 / 60.0);
        Assert.True(beat.Extra > steady.Extra, "beats add speed");
    }

    [Fact]
    public void Pace_is_bounded_and_never_runs_backwards()
    {
        var clock = new MusicClock();
        double last = 0;
        for (int i = 0; i < 600; i++)
        {
            clock.Advance(Frame(i % 2 == 0 ? 1 : 0, i % 7 == 0), 1 / 60.0);
            Assert.InRange(clock.Pace, MusicClock.MinPace, MusicClock.MaxPace);
            Assert.True(clock.Extra >= last - 1 / 60.0 * (1 - MusicClock.MinPace) - 1e-9);   // may slow below real time, bounded
            last = clock.Extra;
        }
        // Bad timing never breaks it.
        clock.Advance(Frame(1, true), double.NaN);
        clock.Advance(Frame(1, true), -1);
        clock.Advance(Frame(1, true), 100);   // clamped to 0.25 s
        Assert.True(double.IsFinite(clock.Extra));
    }

    [Fact]
    public void Stamp_carries_the_clock_on_the_frame()
    {
        var clock = new MusicClock();
        for (int i = 0; i < 60; i++) clock.Advance(Frame(1, true), 1 / 60.0);
        var f = clock.Stamp(AudioFrame.Quiet);
        Assert.Equal(clock.Extra, f.MusicExtra);
        Assert.Equal((float)clock.Pace, f.Pace);
        Assert.Equal(1, AudioFrame.Quiet.Pace);
    }

    [Fact]
    public void Audio_detection_agrees_between_core_runtime_and_studio()
    {
        // The three lists must stay in step: which code makes a wallpaper or overlay listen to the music.
        string runtime = File.ReadAllText(AppDir("Web", "shader-runtime.js"));
        string studio = File.ReadAllText(AppDir("Web", "studio.js"));
        var rx = Regex.Match(runtime, @"AUDIO_UNIFORMS = /\\b\((.*?)\)\\b/");
        Assert.True(rx.Success);
        var names = rx.Groups[1].Value.Split('|');
        Assert.Contains("wavelitMusic", names);
        Assert.Contains("ilMusic", names);
        Assert.Contains("const AUDIO = /\\b(" + rx.Groups[1].Value + ")\\b/", studio);
        foreach (var n in names)
        {
            Assert.True(AudioSettings.CodeUsesAudio("x " + n + " y"), n);
        }
        Assert.False(AudioSettings.CodeUsesAudio("float ilTime(); iTime"));
        Assert.Contains("uniform vec4 wavelitMusic;", runtime);
    }

    [Fact]
    public void Particle_overlays_move_on_the_music_clock_and_the_helpers_exist()
    {
        string js = File.ReadAllText(AppDir("Web", "interact.js"));
        Assert.Contains("float ilMusic()", js);
        Assert.Contains("float ilBeat()", js);
        Assert.Contains("float ilAudio()", js);
        foreach (var style in new[] { "petals", "snowfall", "embers", "stardust", "fireflies", "bokeh" })
        {
            var m = Regex.Match(js, @"\n    " + style + @": COMMON \+ `(.*?)`,", RegexOptions.Singleline);
            Assert.True(m.Success, style);
            Assert.Contains("ilMusic()", m.Groups[1].Value);
        }
    }
}
