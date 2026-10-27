using Wavelit.Core.Audio;

namespace Wavelit.Core.Tests;

public class AudioTests
{
    private const int Rate = 48000;
    private const double Step = 1 / 60.0;

    /// <summary>Feeds a signal through the analyser at 60 analyses a second; returns every frame.</summary>
    private static List<AudioFrame> Run(Func<double, double> signal, double seconds, AudioAnalyzer? analyzer = null)
    {
        var a = analyzer ?? new AudioAnalyzer(Rate);
        var frames = new List<AudioFrame>();
        int perFrame = Rate / 60;
        var buffer = new float[perFrame];
        long n = 0;
        for (double t = 0; t < seconds; t += Step)
        {
            for (int i = 0; i < perFrame; i++, n++) buffer[i] = (float)signal((double)n / Rate);
            a.Push(buffer);
            frames.Add(a.Analyze(t));
        }
        return frames;
    }

    private static Func<double, double> Sine(double hz, double amp = 0.5) => t => amp * Math.Sin(2 * Math.PI * hz * t);

    [Fact]
    public void Fft_puts_a_pure_tone_in_its_bin()
    {
        const int n = 1024;
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++) re[i] = Math.Sin(2 * Math.PI * 37 * i / n);
        AudioAnalyzer.Fft(re, im);
        var mags = Enumerable.Range(0, n / 2).Select(k => Math.Sqrt(re[k] * re[k] + im[k] * im[k])).ToList();
        Assert.Equal(37, mags.IndexOf(mags.Max()));
        Assert.Equal(n / 2.0, mags[37], 6);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(440)]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(12000)]
    public void A_sine_lights_the_band_that_holds_its_frequency(double hz)
    {
        var last = Run(Sine(hz), 1.0)[^1];
        int expected = AudioAnalyzer.BandIndex(hz);
        int loudest = Array.IndexOf(last.Bands, last.Bands.Max());
        Assert.InRange(loudest, expected - 1, expected + 1);
        Assert.False(last.Silent);
    }

    [Fact]
    public void Bass_mid_and_treble_follow_the_spectrum()
    {
        var bass = Run(Sine(70), 1.0)[^1];
        var treble = Run(Sine(8000), 1.0)[^1];
        Assert.True(bass.Bass > bass.Treble);
        Assert.True(treble.Treble > treble.Bass);
        Assert.True(treble.Centroid > bass.Centroid);
    }

    [Fact]
    public void Automatic_gain_fills_the_range_for_quiet_and_loud_masters()
    {
        var loud = Run(Sine(440, 0.8), 3.0)[^1];
        var quiet = Run(Sine(440, 0.02), 3.0)[^1];   // about −32 dB quieter
        Assert.InRange(loud.Bands.Max(), 0.85f, 1f);
        Assert.InRange(quiet.Bands.Max(), 0.85f, 1f);
    }

    [Fact]
    public void Silence_is_reported_after_a_moment_and_bands_go_dark()
    {
        var frames = Run(t => t < 1 ? Math.Sin(2 * Math.PI * 300 * t) * 0.5 : 0, 3.0);
        Assert.False(frames[50].Silent);
        Assert.False(frames[(int)(1.2 * 60)].Silent);   // a short gap is not silence
        Assert.True(frames[^1].Silent);
        Assert.All(frames[^1].Bands, b => Assert.Equal(0f, b));
        Assert.True(new AudioAnalyzer(Rate).Analyze(0).Silent);
    }

    [Fact]
    public void No_beats_on_a_steady_tone_or_in_silence()
    {
        Assert.DoesNotContain(Run(Sine(60), 4.0).Skip(30), f => f.Beat);
        Assert.DoesNotContain(Run(_ => 0, 2.0), f => f.Beat);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(128)]
    [InlineData(150)]
    public void Beats_and_tempo_are_found_on_a_kick_track(double bpm)
    {
        double period = 60 / bpm;
        var noise = new Random(7);
        // A decaying 55 Hz kick on every beat over quiet hi-hat noise.
        double Kick(double t)
        {
            double ph = t % period;
            double kick = Math.Exp(-ph * 18) * Math.Sin(2 * Math.PI * (55 + 60 * Math.Exp(-ph * 30)) * ph);
            return 0.8 * kick + 0.03 * (noise.NextDouble() - 0.5);
        }
        var frames = Run(Kick, 12.0);
        int beats = frames.Count(f => f.Beat);
        int expected = (int)(12.0 / period);
        Assert.InRange(beats, expected - 3, expected + 1);
        Assert.InRange(frames[^1].Bpm, bpm - 2, bpm + 2);
    }
}

public class AudioWiringTests
{
    private static string AppDir(params string[] parts)
        => Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Wavelit.App", .. parts]));

    private static string Squash(string s) => string.Join(' ', s.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void Resonance_overlay_uses_the_same_dust_as_the_wallpaper()
    {
        string js = Squash(File.ReadAllText(AppDir("Web", "interact.js")));
        Assert.Contains(Squash(Wavelit.Core.Library.BuiltIn.BuiltInShaders.ResonanceLib), js);
    }

    [Fact]
    public void Audio_built_ins_read_the_music_and_others_do_not()
    {
        foreach (var item in Wavelit.Core.Library.BuiltIn.BuiltInShaders.Items)
        {
            string key = item.ShaderId!;
            bool audio = Wavelit.Core.Storage.AudioSettings.CodeUsesAudio(Wavelit.Core.Library.BuiltIn.BuiltInShaders.Source(key));
            Assert.Equal(Wavelit.Core.Library.BuiltIn.BuiltInShaders.AudioKeys.Contains(key), audio);
        }
    }

    [Fact]
    public void Overlays_listen_only_when_music_is_on_or_made_for_music()
    {
        var s = new Wavelit.Core.Storage.InteractiveSettings("stardust");
        Assert.False(s.UsesAudio);
        Assert.True((s with { Music = true }).UsesAudio);
        Assert.True(new Wavelit.Core.Storage.InteractiveSettings("resonance").UsesAudio);
        Assert.False(new Wavelit.Core.Storage.InteractiveSettings(Wavelit.Core.Storage.InteractiveSettings.Off, Music: true).UsesAudio);
    }

    [Fact]
    public void Audio_settings_normalize_and_default_to_calm()
    {
        var d = new Wavelit.Core.Storage.AudioSettings();
        Assert.True(d.Enabled);
        Assert.True(d.ReduceFlashing);
        Assert.Equal(1, new Wavelit.Core.Storage.AudioSettings(Sensitivity: 7).Normalize().Sensitivity);
        Assert.Equal(0.5, new Wavelit.Core.Storage.AudioSettings(Sensitivity: double.NaN).Normalize().Sensitivity);
        Assert.NotNull(new Wavelit.Core.Storage.AppSettings { Audio = null! }.Normalize().Audio);
    }
}
