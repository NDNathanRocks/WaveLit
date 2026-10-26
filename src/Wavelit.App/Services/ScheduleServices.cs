using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Wavelit.App.Wallpaper;
using Wavelit.Core.Desktop;
using Wavelit.Core.Library;
using Wavelit.Core.Playback;
using Wavelit.Core.Storage;

namespace Wavelit.App.Services;

/// <summary>
/// Remembers the wallpaper arrangement for each monitor setup (docked, laptop only, TV attached…) and
/// restores it automatically when that setup comes back.
/// </summary>
public sealed class SetupMemoryService : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly WallpaperService _wallpapers;
    private readonly ILogger<SetupMemoryService> _logger;
    private readonly Lock _gate = new();
    private string? _current;

    public SetupMemoryService(SettingsStore settings, WallpaperService wallpapers, ILogger<SetupMemoryService> logger)
    {
        _settings = settings;
        _wallpapers = wallpapers;
        _logger = logger;
        wallpapers.Changed += OnWallpapersChanged;
        settings.Changed += OnSettingsChanged;
    }

    private void OnWallpapersChanged(object? sender, WallpaperSnapshot snapshot)
    {
        if (snapshot.Monitors.Count == 0) return;
        string signature = SetupMemory.Signature(snapshot.Monitors);
        DisplayAssignments? restore = null;
        lock (_gate)
        {
            if (signature == _current) return;
            // At startup just record the setup we start in; after a change, restore what it had last time.
            if (_current is not null) restore = _settings.Current.Setups.GetValueOrDefault(signature);
            _current = signature;
        }
        if (restore is not null)
        {
            _logger.LogInformation("Monitor setup changed; restoring its wallpapers");
            _settings.Update(s => s with { Displays = restore });
        }
        else
        {
            Remember();
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => Remember();

    private void Remember()
    {
        string? signature;
        lock (_gate) signature = _current;
        if (signature is null) return;
        var current = _settings.Current;
        if (current.Setups.TryGetValue(signature, out var saved) && ReferenceEquals(saved, current.Displays)) return;
        _settings.Update(s =>
        {
            var map = new Dictionary<string, DisplayAssignments>(s.Setups) { [signature] = s.Displays };
            while (map.Count > SetupMemory.MaxSetups) map.Remove(map.Keys.First(k => k != signature));
            return s with { Setups = map };
        });
    }

    public void Dispose()
    {
        _wallpapers.Changed -= OnWallpapersChanged;
        _settings.Changed -= OnSettingsChanged;
    }
}

/// <summary>"Follows the sun": switches between the day and night wallpapers at local sunrise and sunset.</summary>
public sealed class DayNightService : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly LibraryActions _actions;
    private readonly LibraryStore _library;
    private readonly ILogger<DayNightService> _logger;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool? _lastWasDay;
    private DayNightSchedule? _lastSchedule;

    public DayNightService(SettingsStore settings, LibraryActions actions, LibraryStore library, ILogger<DayNightService> logger)
    {
        _settings = settings;
        _actions = actions;
        _library = library;
        _logger = logger;
        _timer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => Check(), _dispatcher);
        settings.Changed += OnSettingsChanged;
    }

    public void Start()
    {
        _timer.Start();
        Check();
    }

    // Settings can change on any thread; the check runs on the UI thread.
    private void OnSettingsChanged(object? sender, AppSettings s) => _dispatcher.BeginInvoke(Check);

    private void Check()
    {
        var schedule = _settings.Current.DayNight;
        // Turning the schedule on, or changing its wallpapers or place, applies it right away.
        if (!ReferenceEquals(schedule, _lastSchedule)) _lastWasDay = null;
        _lastSchedule = schedule;
        if (!schedule.IsReady) return;
        bool day = SunTimes.IsDaytime(DateTime.UtcNow, schedule.Latitude, schedule.Longitude);
        string target = day ? schedule.DayItemId! : schedule.NightItemId!;
        // Otherwise only act at sunrise and sunset, so manual changes in between are respected.
        if (_lastWasDay == day) return;
        _lastWasDay = day;
        if (_library.Find(target) is null || _settings.Current.Displays.SharedItemId == target) return;
        _logger.LogInformation("Switching to the {Phase} wallpaper", day ? "day" : "night");
        _actions.Assign(target, null);
    }

    public void Dispose()
    {
        _timer.Stop();
        _settings.Changed -= OnSettingsChanged;
    }
}
