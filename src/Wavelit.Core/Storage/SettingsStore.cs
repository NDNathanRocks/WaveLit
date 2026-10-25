using System.Text.Json.Serialization;
using Wavelit.Core.Library;
using Wavelit.Core.Playback;

namespace Wavelit.Core.Storage;

[JsonConverter(typeof(JsonStringEnumConverter<VideoScaling>))]
public enum VideoScaling { Fill, Fit, Stretch }

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public bool FirstRunCompleted { get; init; }

    public DisplayAssignments Displays { get; init; } = new();
    public PlaybackRules Rules { get; init; } = new();

    public int ShaderFpsCap { get; init; } = 30;
    public double ShaderRenderScale { get; init; } = 0.75;
    public VideoScaling VideoScaling { get; init; } = VideoScaling.Fill;
    public bool SmoothLoop { get; init; } = true;
    public bool Muted { get; init; } = true;
    public int Volume { get; init; } = 40;

    public bool StartWithWindows { get; init; }
    public int CacheLimitGb { get; init; } = 5;

    public AppSettings Normalize() => this with
    {
        ShaderFpsCap = Math.Clamp(ShaderFpsCap, 10, 144),
        ShaderRenderScale = Math.Clamp(ShaderRenderScale, 0.25, 1.0),
        Volume = Math.Clamp(Volume, 0, 100),
        CacheLimitGb = Math.Clamp(CacheLimitGb, 1, 200),
        Displays = Displays ?? new(),
        Rules = Rules ?? new(),
    };
}

/// <summary>Thread-safe settings holder. Every update is persisted immediately (atomic write).</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private AppSettings _current;

    public SettingsStore(AppPaths paths)
    {
        _path = paths.SettingsFile;
        _current = (JsonFile.Read<AppSettings>(_path) ?? new AppSettings()).Normalize();
    }

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        AppSettings next;
        lock (_gate)
        {
            next = change(_current).Normalize();
            if (next == _current) return next;
            _current = next;
            JsonFile.Write(_path, next);
        }
        Changed?.Invoke(this, next);
        return next;
    }
}
