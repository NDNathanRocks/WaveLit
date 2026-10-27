namespace Wavelit.Core.Audio;

/// <summary>
/// One analysis of what the PC is playing, sent to audio wallpapers about 60 times a second (plan §20.2).
/// Every value is 0..1 except <see cref="Bpm"/> and <see cref="BeatCount"/>.
/// </summary>
/// <param name="Bands">64 log-spaced bands, 30 Hz–16 kHz, after automatic gain.</param>
/// <param name="Centroid">Spectral centroid on the band scale: 0 = all bass, 1 = all treble ("colour").</param>
/// <param name="Beat">A kick started in this frame.</param>
/// <param name="Bpm">Tempo estimate (0 until a few beats were heard).</param>
/// <param name="Silent">Nothing has played for <see cref="AudioAnalyzer.SilenceSeconds"/>.</param>
public sealed record AudioFrame(float[] Bands, float Bass, float Mid, float Treble, float Level, float Centroid,
    bool Beat, float Bpm, int BeatCount, bool Silent)
{
    public static AudioFrame Quiet { get; } = new(new float[AudioAnalyzer.BandCount], 0, 0, 0, 0, 0.5f, false, 0, 0, true);
}

/// <summary>
/// Turns loopback samples into an <see cref="AudioFrame"/>: a 2048-point Hann FFT over the latest samples,
/// 64 log bands in dB with slow automatic gain (quiet and loud masters both fill the range), bass/mid/treble,
/// a spectral centroid, a beat detector (bass spectral flux over an adaptive threshold) with a tempo estimate,
/// and silence detection. Not thread-safe: push and analyse under one lock.
/// </summary>
public sealed class AudioAnalyzer
{
    public const int FftSize = 2048;
    public const int BandCount = 64;
    public const double MinHz = 30, MaxHz = 16000;
    public const double SilenceSeconds = 1.5;

    private const double RangeDb = 54;          // dB shown between an empty and a full band
    private const double TiltDbPerOctave = 1.5; // music falls faster than pink noise at the top
    private const double GainReleaseDbPerSecond = 3;
    private const double MinCeilingDb = -42;    // never boost noise or near-silence to full bars
    private const double SilenceRms = 1e-4;     // −80 dBFS
    private const double MinBeatGap = 0.28;     // seconds; ≈ 214 BPM
    private const int FluxHistory = 64;         // frames (~1 s) for the adaptive threshold

    private readonly int _sampleRate;
    private readonly float[] _ring = new float[FftSize];
    private int _write;
    private long _received;
    private readonly double[] _window = new double[FftSize];
    private readonly double[] _re = new double[FftSize], _im = new double[FftSize];
    private readonly double[] _power = new double[FftSize / 2 + 1];
    private readonly (double Lo, double Hi)[] _bandHz = new (double, double)[BandCount];
    private readonly double[] _bandTilt = new double[BandCount];
    private readonly float[] _previous = new float[BandCount];
    private readonly Queue<double> _flux = new();
    private readonly List<double> _beats = new();
    private double _ceilingDb = MinCeilingDb;
    private double _lastTime = double.NaN;
    private double _lastBeat = double.NegativeInfinity;
    private double _lastSound = double.NegativeInfinity;
    private int _beatCount;
    private readonly int _bassBands, _midBands;

    public AudioAnalyzer(int sampleRate)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        for (int i = 0; i < FftSize; i++) _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
        double ratio = Math.Pow(MaxHz / MinHz, 1.0 / BandCount);
        for (int b = 0; b < BandCount; b++)
        {
            double lo = MinHz * Math.Pow(ratio, b), hi = lo * ratio;
            _bandHz[b] = (lo, hi);
            _bandTilt[b] = TiltDbPerOctave * Math.Log2(Math.Sqrt(lo * hi) / 1000);
        }
        _bassBands = BandIndex(150);
        _midBands = BandIndex(2000);
    }

    public int SampleRate => _sampleRate;

    /// <summary>The band containing <paramref name="hz"/> (exclusive upper index for a range).</summary>
    public static int BandIndex(double hz)
        => Math.Clamp((int)Math.Round(BandCount * Math.Log(hz / MinHz) / Math.Log(MaxHz / MinHz)), 0, BandCount);

    /// <summary>Adds mono samples (−1..1). Only the latest <see cref="FftSize"/> are kept.</summary>
    public void Push(ReadOnlySpan<float> mono)
    {
        if (mono.Length >= FftSize) mono = mono[^FftSize..];
        foreach (float s in mono)
        {
            _ring[_write] = float.IsFinite(s) ? s : 0;
            _write = (_write + 1) % FftSize;
        }
        _received += mono.Length;
    }

    /// <summary>Analyses the latest samples. <paramref name="now"/> is a monotonic clock in seconds.</summary>
    public AudioFrame Analyze(double now)
    {
        double dt = double.IsNaN(_lastTime) ? 1 / 60.0 : Math.Clamp(now - _lastTime, 0, 0.5);
        _lastTime = now;

        double sumSquares = 0;
        for (int i = 0; i < FftSize; i++)
        {
            double s = _ring[(_write + i) % FftSize];
            sumSquares += s * s;
            _re[i] = s * _window[i];
            _im[i] = 0;
        }
        double rms = Math.Sqrt(sumSquares / FftSize);
        if (rms >= SilenceRms) _lastSound = now;
        bool silent = now - _lastSound >= SilenceSeconds;

        Fft(_re, _im);
        // A full-scale sine peaks at amplitude N/4 under a Hann window: normalise that to 1 (0 dB).
        double norm = 16.0 / ((double)FftSize * FftSize);
        for (int k = 0; k < _power.Length; k++) _power[k] = (_re[k] * _re[k] + _im[k] * _im[k]) * norm;

        var db = new double[BandCount];
        double frameMax = double.NegativeInfinity;
        for (int b = 0; b < BandCount; b++)
        {
            db[b] = 10 * Math.Log10(BandEnergy(_bandHz[b].Lo, _bandHz[b].Hi) + 1e-14) + _bandTilt[b];
            frameMax = Math.Max(frameMax, db[b]);
        }

        // Automatic gain: jump up to a louder peak, drift down slowly in quiet passages.
        if (frameMax > _ceilingDb) _ceilingDb += (frameMax - _ceilingDb) * Math.Min(1, dt * 12);
        else _ceilingDb -= GainReleaseDbPerSecond * dt;
        _ceilingDb = Math.Clamp(_ceilingDb, MinCeilingDb, 12);
        double floor = _ceilingDb - RangeDb;

        var bands = new float[BandCount];
        if (!silent)
            for (int b = 0; b < BandCount; b++)
                bands[b] = (float)Math.Clamp((db[b] - floor) / RangeDb, 0, 1);

        float bass = Mean(bands, 0, _bassBands), mid = Mean(bands, _bassBands, _midBands), treble = Mean(bands, _midBands, BandCount);
        float level = Mean(bands, 0, BandCount);
        double weighted = 0, total = 0;
        for (int b = 0; b < BandCount; b++) { weighted += b * bands[b]; total += bands[b]; }
        float centroid = total > 1e-6 ? (float)(weighted / total / (BandCount - 1)) : 0.5f;

        bool beat = DetectBeat(bands, now, silent);
        Array.Copy(bands, _previous, BandCount);
        return new AudioFrame(bands, bass, mid, treble, level, centroid, beat, (float)Tempo(), _beatCount, silent);
    }

    // Energy of a log band: the power spectrum integrated over [lo, hi], with linear interpolation between
    // bins so the narrow bass bands (narrower than one bin) still read the right level.
    private double BandEnergy(double lo, double hi)
    {
        const int Samples = 4;
        double binHz = (double)_sampleRate / FftSize;
        double sum = 0;
        for (int i = 0; i < Samples; i++)
        {
            double f = lo * Math.Pow(hi / lo, (i + 0.5) / Samples) / binHz;
            int k = Math.Min((int)f, _power.Length - 2);
            double t = f - k;
            sum += _power[k] * (1 - t) + _power[k + 1] * t;
        }
        return sum / Samples * Math.Max(1, (hi - lo) / binHz);
    }

    private bool DetectBeat(float[] bands, double now, bool silent)
    {
        double flux = 0;
        for (int b = 0; b < _bassBands; b++) flux += Math.Max(0, bands[b] - _previous[b]);
        flux /= Math.Max(1, _bassBands);

        double mean = 0, variance = 0;
        foreach (double f in _flux) mean += f;
        mean = _flux.Count > 0 ? mean / _flux.Count : 0;
        foreach (double f in _flux) variance += (f - mean) * (f - mean);
        double std = _flux.Count > 1 ? Math.Sqrt(variance / _flux.Count) : 0;

        _flux.Enqueue(flux);
        while (_flux.Count > FluxHistory) _flux.Dequeue();

        bool beat = !silent && _flux.Count >= 8 && flux > Math.Max(0.04, mean + 1.5 * std) && now - _lastBeat >= MinBeatGap;
        if (!beat) return false;
        _lastBeat = now;
        _beatCount++;
        _beats.Add(now);
        if (_beats.Count > 16) _beats.RemoveAt(0);
        return true;
    }

    /// <summary>Median inter-beat interval over the last 16 beats, folded into 70–180 BPM.</summary>
    private double Tempo()
    {
        if (_beats.Count < 4 || _lastTime - _beats[^1] > 4) return 0;
        var gaps = new List<double>();
        for (int i = 1; i < _beats.Count; i++) gaps.Add(_beats[i] - _beats[i - 1]);
        gaps.Sort();
        double bpm = 60 / gaps[gaps.Count / 2];
        while (bpm < 70) bpm *= 2;
        while (bpm > 180) bpm /= 2;
        return Math.Round(bpm, 1);
    }

    private static float Mean(float[] v, int from, int to)
    {
        if (to <= from) return 0;
        float s = 0;
        for (int i = from; i < to; i++) s += v[i];
        return s / (to - from);
    }

    /// <summary>In-place iterative radix-2 FFT.</summary>
    internal static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            double wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double nr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = nr;
                }
            }
        }
    }
}
