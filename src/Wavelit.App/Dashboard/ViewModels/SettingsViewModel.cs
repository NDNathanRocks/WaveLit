using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavelit.App.Services;
using Wavelit.Core.Library.Pexels;
using Wavelit.Core.Library.Shadertoy;
using Wavelit.Core.Playback;
using Wavelit.Core.Storage;

namespace Wavelit.App.Dashboard.ViewModels;

public sealed record Choice<T>(string Label, T Value);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly SecretStore _secrets;
    private readonly PexelsClient _pexels;
    private readonly ShadertoyClient _shadertoy;
    private readonly MediaCache _cache;
    private readonly AppPaths _paths;

    [ObservableProperty] private string _pexelsKey = "";
    [ObservableProperty] private string _shadertoyKey = "";
    [ObservableProperty] private string? _pexelsStatus;
    [ObservableProperty] private string? _shadertoyStatus;
    [ObservableProperty] private string _cacheSize = "";

    public SettingsViewModel(DashboardContext context, SecretStore secrets, PexelsClient pexels, ShadertoyClient shadertoy, MediaCache cache, AppPaths paths)
    {
        Context = context;
        _settings = context.Settings;
        _secrets = secrets;
        _pexels = pexels;
        _shadertoy = shadertoy;
        _cache = cache;
        _paths = paths;
        _pexelsKey = secrets.Get(SecretNames.PexelsApiKey) ?? "";
        _shadertoyKey = secrets.Get(SecretNames.ShadertoyApiKey) ?? "";
        _pexelsStatus = secrets.Has(SecretNames.PexelsApiKey) ? "Key saved" : null;
        _shadertoyStatus = secrets.Has(SecretNames.ShadertoyApiKey) ? "Key saved" : null;
        RefreshCacheSize();
    }

    public DashboardContext Context { get; }
    private AppSettings S => _settings.Current;
    private void Update(Func<AppSettings, AppSettings> change, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        _settings.Update(change);
        OnPropertyChanged(property);
    }

    public IReadOnlyList<Choice<int>> FpsChoices { get; } =
        [new("15 fps (lowest power)", 15), new("24 fps", 24), new("30 fps (recommended)", 30), new("45 fps", 45), new("60 fps", 60)];

    public IReadOnlyList<Choice<double>> ScaleChoices { get; } =
        [new("50% (fastest)", 0.5), new("75% (recommended)", 0.75), new("100% (sharpest)", 1.0)];

    public IReadOnlyList<Choice<VideoScaling>> ScalingChoices { get; } =
        [new("Fill (crop to fit the screen)", VideoScaling.Fill), new("Fit (show the whole video)", VideoScaling.Fit), new("Stretch", VideoScaling.Stretch)];

    public IReadOnlyList<Choice<int>> CacheChoices { get; } =
        [new("2 GB", 2), new("5 GB", 5), new("10 GB", 10), new("20 GB", 20), new("50 GB", 50)];

    public Choice<int> SelectedFps
    {
        get => FpsChoices.FirstOrDefault(c => c.Value == S.ShaderFpsCap) ?? FpsChoices[2];
        set { if (value is not null) Update(s => s with { ShaderFpsCap = value.Value }); }
    }

    public Choice<double> SelectedScale
    {
        get => ScaleChoices.OrderBy(c => Math.Abs(c.Value - S.ShaderRenderScale)).First();
        set { if (value is not null) Update(s => s with { ShaderRenderScale = value.Value }); }
    }

    public Choice<VideoScaling> SelectedScaling
    {
        get => ScalingChoices.First(c => c.Value == S.VideoScaling);
        set { if (value is not null) Update(s => s with { VideoScaling = value.Value }); }
    }

    public Choice<int> SelectedCache
    {
        get => CacheChoices.FirstOrDefault(c => c.Value == S.CacheLimitGb) ?? CacheChoices[1];
        set
        {
            if (value is null) return;
            Update(s => s with { CacheLimitGb = value.Value });
            _ = Context.Actions.TrimCacheAsync().ContinueWith(_ => System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshCacheSize), TaskScheduler.Default);
        }
    }

    public bool PauseOnFullscreen
    {
        get => S.Rules.PauseOnFullscreen;
        set => Update(s => s with { Rules = s.Rules with { PauseOnFullscreen = value } });
    }

    public bool PauseAllDisplaysOnFullscreen
    {
        get => S.Rules.PauseAllDisplaysOnFullscreen;
        set => Update(s => s with { Rules = s.Rules with { PauseAllDisplaysOnFullscreen = value } });
    }

    public bool PauseOnMaximized
    {
        get => S.Rules.PauseOnMaximized;
        set => Update(s => s with { Rules = s.Rules with { PauseOnMaximized = value } });
    }

    public bool PauseOnBattery
    {
        get => S.Rules.PauseOnBattery;
        set => Update(s => s with { Rules = s.Rules with { PauseOnBattery = value } });
    }

    public bool PauseOnBatterySaver
    {
        get => S.Rules.PauseOnBatterySaver;
        set => Update(s => s with { Rules = s.Rules with { PauseOnBatterySaver = value } });
    }

    public bool PauseOnRemoteSession
    {
        get => S.Rules.PauseOnRemoteSession;
        set => Update(s => s with { Rules = s.Rules with { PauseOnRemoteSession = value } });
    }

    public bool SmoothLoop
    {
        get => S.SmoothLoop;
        set => Update(s => s with { SmoothLoop = value });
    }

    public bool PlaySound
    {
        get => !S.Muted;
        set => Update(s => s with { Muted = !value });
    }

    public double Volume
    {
        get => S.Volume;
        set => Update(s => s with { Volume = (int)Math.Round(value) });
    }

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set => Update(s => s with { StartWithWindows = value });
    }

    public bool IsPackaged => StartupRegistration.IsPackaged;

    [RelayCommand]
    private async Task SavePexelsKeyAsync()
    {
        PexelsStatus = "Checking…";
        string key = PexelsKey.Trim();
        if (key.Length == 0)
        {
            _secrets.Set(SecretNames.PexelsApiKey, null);
            PexelsStatus = "Key removed";
            return;
        }
        bool ok = await _pexels.TestKeyAsync(key, CancellationToken.None);
        if (ok) _secrets.Set(SecretNames.PexelsApiKey, key);
        PexelsStatus = ok ? "Connected ✓" : "That key didn't work. Copy it again from pexels.com/api.";
    }

    [RelayCommand]
    private async Task SaveShadertoyKeyAsync()
    {
        ShadertoyStatus = "Checking…";
        string key = ShadertoyKey.Trim();
        if (key.Length == 0)
        {
            _secrets.Set(SecretNames.ShadertoyApiKey, null);
            ShadertoyStatus = "Key removed";
            return;
        }
        bool ok = await _shadertoy.TestKeyAsync(key, CancellationToken.None);
        if (ok) _secrets.Set(SecretNames.ShadertoyApiKey, key);
        ShadertoyStatus = ok ? "Connected ✓" : "That key didn't work. Check it at shadertoy.com/myapps.";
    }

    [RelayCommand]
    private static void OpenLink(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ClearCache()
    {
        Context.Actions.ClearCache();
        RefreshCacheSize();
        Context.Notify("Cache cleared", "Downloaded videos will be fetched again when needed.", NoticeLevel.Success);
    }

    [RelayCommand]
    private void OpenLogs() => Process.Start(new ProcessStartInfo(_paths.Logs) { UseShellExecute = true });

    private void RefreshCacheSize()
    {
        double gb = _cache.SizeBytes() / 1024d / 1024 / 1024;
        CacheSize = gb < 0.1 ? $"{_cache.SizeBytes() / 1024d / 1024:F0} MB used" : $"{gb:F1} GB used";
    }
}
