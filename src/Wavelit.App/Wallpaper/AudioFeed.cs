using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Wavelit.Core.Audio;

namespace Wavelit.App.Wallpaper;

/// <summary>
/// Tells audio wallpapers what the PC is playing (plan §20.2). WASAPI loopback of the default output device,
/// never the microphone, and only while <see cref="Enabled"/> (a visible wallpaper or overlay uses audio):
/// covered, frozen, paused or suspended wallpapers mean no capture at all. Samples are analysed in memory
/// (<see cref="AudioAnalyzer"/>) and only the analysis goes to Wavelit's own host page; nothing is recorded,
/// stored or sent anywhere. Follows default-device changes and treats a missing device as silence.
/// </summary>
internal sealed class AudioFeed : IDisposable
{
    private static readonly TimeSpan DeviceCheck = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryAfterError = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly Func<IEnumerable<WallpaperHost>> _hosts;
    private readonly DispatcherTimer _timer;
    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private WasapiRecorder? _capture;
    private WaveFormat? _format;
    private AudioAnalyzer? _analyzer;
    private string? _deviceId;
    private double _lastData;          // _clock seconds of the last packet (loopback sends none in silence)
    private double _lastDeviceCheck;
    private double _retryAt;
    private bool _sentSilence;
    private bool _disposed;
    private float[] _mono = [];

    public AudioFeed(Dispatcher dispatcher, Func<IEnumerable<WallpaperHost>> hosts, ILogger logger)
    {
        _hosts = hosts;
        _logger = logger;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick(), dispatcher);
        _timer.Stop();
    }

    public bool Enabled
    {
        get => _timer.IsEnabled;
        set
        {
            if (_disposed || value == _timer.IsEnabled) return;
            if (value)
            {
                _sentSilence = false;
                _retryAt = 0;
                _timer.Start();
                StartCapture();
            }
            else
            {
                _timer.Stop();
                StopCapture();
                // Let the visualizers ease into their idle state instead of freezing mid-beat.
                foreach (var host in _hosts()) host.SendAudio(AudioFrame.Quiet);
            }
        }
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    private void Tick()
    {
        if (_disposed) return;
        double now = Now;
        if (_capture is null && now >= _retryAt) StartCapture();
        else if (now - _lastDeviceCheck > DeviceCheck.TotalSeconds)
        {
            _lastDeviceCheck = now;
            if (DefaultDeviceId() is { } id && id != _deviceId)
            {
                _logger.LogInformation("Audio output changed; following the new default device");
                StopCapture();
                StartCapture();
            }
        }

        AudioFrame frame;
        lock (_gate)
        {
            if (_analyzer is null) frame = AudioFrame.Quiet;
            else
            {
                // Loopback delivers no packets while nothing plays: fill the gap with silence.
                double gap = now - _lastData;
                if (gap > 0.05)
                {
                    int samples = Math.Min(AudioAnalyzer.FftSize, (int)(gap * _analyzer.SampleRate));
                    if (samples > 0) _analyzer.Push(new float[samples]);
                    _lastData = now;
                }
                frame = _analyzer.Analyze(now);
            }
        }

        if (frame.Silent && _sentSilence) return;   // one quiet frame is enough; the page eases to idle
        _sentSilence = frame.Silent;
        foreach (var host in _hosts())
            if (host.NeedsAudio && host.State == Core.Playback.PlaybackState.Playing) host.SendAudio(frame);
    }

    private static string? DefaultDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }

    private void StartCapture()
    {
        if (_disposed || _capture is not null) return;
        try
        {
            _deviceId = DefaultDeviceId();
            _lastDeviceCheck = Now;
            if (_deviceId is null)
            {
                _retryAt = Now + RetryAfterError.TotalSeconds;   // no output device: silence until one appears
                return;
            }
            // System-wide loopback of the default render device: what the PC plays, never a microphone.
            var capture = new WasapiRecorderBuilder().WithLoopbackCapture().WithSharedMode().WithBufferLength(40).Build();
            var format = capture.WaveFormat;
            _format = format;
            lock (_gate)
            {
                _analyzer = new AudioAnalyzer(format.SampleRate);
                _lastData = Now;
            }
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            capture.StartRecording();
            _capture = capture;
            _logger.LogInformation("Audio capture started ({Rate} Hz, {Channels} ch, {Encoding})", format.SampleRate, format.Channels, format.Encoding);
        }
        catch (Exception ex)
        {
            // A boundary with the audio driver stack: whatever goes wrong here means silence, never a broken wallpaper.
            _logger.LogWarning(ex, "Audio capture could not start; retrying shortly");
            _retryAt = Now + RetryAfterError.TotalSeconds;
            StopCapture();
        }
    }

    private void StopCapture()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null) return;
        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnStopped;
        try
        {
            capture.StopRecording();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Stopping audio capture failed");
        }
        capture.Dispose();
        _logger.LogInformation("Audio capture stopped");
    }

    // Capture thread: down-mix to mono floats and hand them to the analyser. Nothing is kept beyond the
    // analyser's 2048-sample window.
    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var format = _format;
        if (format is null || buffer.IsEmpty) return;
        int channels = Math.Max(1, format.Channels);
        int bytesPerSample = format.BitsPerSample / 8;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32 && IsFloatExtensible(format);
        if (bytesPerSample is not (2 or 3 or 4)) return;
        int frames = buffer.Length / (bytesPerSample * channels);
        if (_mono.Length < frames) _mono = new float[frames];
        var span = buffer[..(frames * bytesPerSample * channels)];
        bool silentPacket = (flags & AudioClientBufferFlags.Silent) != 0;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int o = (f * channels + c) * bytesPerSample;
                sum += bytesPerSample switch
                {
                    4 when isFloat => BitConverter.ToSingle(span.Slice(o, 4)),
                    4 => BitConverter.ToInt32(span.Slice(o, 4)) / 2147483648f,
                    3 => ((span[o] | span[o + 1] << 8 | (sbyte)span[o + 2] << 16)) / 8388608f,
                    _ => BitConverter.ToInt16(span.Slice(o, 2)) / 32768f,
                };
            }
            _mono[f] = silentPacket ? 0 : sum / channels;
        }
        lock (_gate)
        {
            if (_analyzer is null || _analyzer.SampleRate != format.SampleRate) return;
            _analyzer.Push(_mono.AsSpan(0, frames));
            _lastData = Now;
        }
    }

    private static bool IsFloatExtensible(WaveFormat format)
        => format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype;

    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        // The device went away (unplugged, disabled, exclusive mode): retry on the wallpaper thread.
        _logger.LogInformation("Audio capture stopped by Windows ({Message}); retrying", e.Exception.Message);
        _timer.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || !ReferenceEquals(sender, _capture)) return;
            StopCapture();
            _retryAt = Now + 1;
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        StopCapture();
    }
}
