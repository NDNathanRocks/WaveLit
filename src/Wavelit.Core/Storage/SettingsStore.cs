using System.Text.Json.Serialization;
using Wavelit.Core.Desktop;
using Wavelit.Core.Library;
using Wavelit.Core.Playback;

namespace Wavelit.Core.Storage;

[JsonConverter(typeof(JsonStringEnumConverter<VideoScaling>))]
public enum VideoScaling { Fill, Fit, Stretch }

public sealed record AppSettings
{
    public const int CurrentSchema = 3;
    public int SchemaVersion { get; init; } = CurrentSchema;
    public bool FirstRunCompleted { get; init; }

    public DisplayAssignments Displays { get; init; } = new();
    /// <summary>Framing-editor choices, keyed by <see cref="MediaFraming.Key"/>.</summary>
    public Dictionary<string, MediaFraming> Framings { get; init; } = new();
    /// <summary>Extend fine-tuning: physical-size matching and bezel gap.</summary>
    public CanvasOptions Canvas { get; init; } = new();
    /// <summary>Arrangement remembered per monitor setup (<see cref="SetupMemory.Signature"/>).</summary>
    public Dictionary<string, DisplayAssignments> Setups { get; init; } = new();
    public DayNightSchedule DayNight { get; init; } = new();
    /// <summary>The interactive layer over every wallpaper (off by default).</summary>
    public InteractiveSettings Interactive { get; init; } = new();
    public AudioSettings Audio { get; init; } = new();
    /// <summary>Desktop page: widgets on the wallpaper and the taskbar (plan §22).</summary>
    public Wavelit.Core.Desktop.DesktopSettings Desktop { get; init; } = new();
    /// <summary>Overlay styles chosen for single wallpapers (item id → style), overriding <see cref="Interactive"/>.</summary>
    public Dictionary<string, string> OverlayFor { get; init; } = new();
    public PlaybackRules Rules { get; init; } = new();
    /// <summary>Overlays made in the Studio (id with <see cref="InteractiveSettings.CustomPrefix"/> → overlay).</summary>
    public Dictionary<string, CustomOverlay> CustomOverlays { get; init; } = new();
    /// <summary>Slider values chosen for Studio wallpapers (item id → values; plan §21.5).</summary>
    public Dictionary<string, double[]> ParamsFor { get; init; } = new();
    /// <summary>The Claude model the Studio uses (see <c>ClaudeShaderAuthor.Models</c>).</summary>
    public string StudioModel { get; init; } = "claude-opus-5-5";

    /// <summary>Motion: 60 = Pristine (default), 30 = Balanced, 15 = Saver. Paced to the display's refresh.</summary>
    public int ShaderFpsCap { get; init; } = 60;
    public double ShaderRenderScale { get; init; } = 0.75;
    public VideoScaling VideoScaling { get; init; } = VideoScaling.Fill;
    public bool SmoothLoop { get; init; } = true;
    public bool Muted { get; init; } = true;
    public int Volume { get; init; } = 40;

    public bool StartWithWindows { get; init; }
    /// <summary>The user asked for the Local AI pack; an interrupted download resumes at the next start.</summary>
    public bool LocalAiWanted { get; init; }
    public int CacheLimitGb { get; init; } = 5;

    public AppSettings Normalize()
    {
        var s = Migrate();
        var custom = (s.CustomOverlays ?? new())
            .Where(kv => InteractiveSettings.IsCustom(kv.Key) && kv.Value is { Code.Length: > 0 })
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return s with
        {
            CustomOverlays = custom,
            StudioModel = string.IsNullOrWhiteSpace(s.StudioModel) ? "claude-opus-5-5" : s.StudioModel,
            ShaderFpsCap = Math.Clamp(s.ShaderFpsCap, 10, 144),
            ShaderRenderScale = Math.Clamp(s.ShaderRenderScale, 0.25, 1.0),
            Volume = Math.Clamp(s.Volume, 0, 100),
            CacheLimitGb = Math.Clamp(s.CacheLimitGb, 1, 200),
            Displays = s.Displays ?? new(),
            Rules = s.Rules ?? new(),
            Framings = s.Framings ?? new(),
            Canvas = (s.Canvas ?? new()).Normalize(),
            Setups = s.Setups ?? new(),
            DayNight = s.DayNight ?? new(),
            Interactive = (s.Interactive ?? new()).Normalize(custom.Keys),
            Audio = (s.Audio ?? new()).Normalize(),
            Desktop = (s.Desktop ?? new()).Normalize(),
            ParamsFor = (s.ParamsFor ?? new()).Where(kv => kv.Value is { Length: <= 4 } v && v.All(double.IsFinite)).ToDictionary(kv => kv.Key, kv => kv.Value),
            OverlayFor = (s.OverlayFor ?? new()).Where(kv => InteractiveSettings.IsKnown(kv.Value, custom.Keys)).ToDictionary(kv => kv.Key, kv => kv.Value),
        };
    }

    private AppSettings Migrate()
    {
        var s = this;
        // v2: "Extend" became the default arrangement; v1 only defaulted to Duplicate.
        if (s.SchemaVersion < 2 && s.Displays is { Layout: DisplayLayout.Duplicate } d)
            s = s with { Displays = d with { Layout = DisplayLayout.Span } };
        // v3: smooth (Pristine) motion became the default; the old 30 fps default moves up with it.
        if (s.SchemaVersion < 3 && s.ShaderFpsCap == 30)
            s = s with { ShaderFpsCap = 60 };
        return s with { SchemaVersion = Math.Max(s.SchemaVersion, CurrentSchema) };
    }
}

/// <summary>An overlay written in the Studio. Its GLSL runs after the shared overlay helpers (Web/interact.js).</summary>
/// <param name="Params">Its slider values (null = the defaults it declares).</param>
/// <param name="Model">The AI that wrote it (provenance; "Made with AI").</param>
public sealed record CustomOverlay(string Title, string Code, bool UsesPointer, string? ThumbnailPath = null, string? Prompt = null,
    IReadOnlyList<double>? Params = null, string? Model = null);

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
