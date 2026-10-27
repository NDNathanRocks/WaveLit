using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Wavelit.App.Services;
using Wavelit.App.Wallpaper;
using Wavelit.Core.Library;
using Wavelit.Core.Storage;

namespace Wavelit.App.Dashboard.ViewModels;

public sealed record TargetOption(string Label, string? MonitorId);

public enum NoticeLevel { Info, Success, Warning, Error }

/// <summary>State shared by all dashboard pages: target display, wallpaper status and the notice banner.</summary>
public sealed partial class DashboardContext : ObservableObject, IDisposable
{
    private readonly WallpaperService _wallpapers;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _noticeTimer;

    [ObservableProperty] private TargetOption? _selectedTarget;
    [ObservableProperty] private WallpaperSnapshot _snapshot;
    [ObservableProperty] private bool _isNoticeOpen;
    [ObservableProperty] private string _noticeTitle = "";
    [ObservableProperty] private string _noticeMessage = "";
    [ObservableProperty] private NoticeLevel _noticeLevel;

    public DashboardContext(WallpaperService wallpapers, ThumbnailLoader thumbnails, LibraryActions actions, SettingsStore settings, LibraryStore library)
    {
        _wallpapers = wallpapers;
        Thumbnails = thumbnails;
        Actions = actions;
        Settings = settings;
        Library = library;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _snapshot = wallpapers.Snapshot;
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); IsNoticeOpen = false; };
        RebuildTargets();
        wallpapers.Changed += OnWallpapersChanged;
        wallpapers.Error += OnWallpaperError;
    }

    private void OnWallpaperError(object? sender, string message)
        => _dispatcher.BeginInvoke(() => Notify("Wallpaper problem", message, NoticeLevel.Error));

    public WallpaperService Wallpapers => _wallpapers;
    public ThumbnailLoader Thumbnails { get; }
    public LibraryActions Actions { get; }
    public SettingsStore Settings { get; }
    public LibraryStore Library { get; }
    public ObservableCollection<TargetOption> Targets { get; } = [];

    /// <summary>Raised when the user asks to navigate (e.g. "choose wallpaper for this display").</summary>
    public event EventHandler<string>? NavigationRequested;

    public string? TargetMonitorId => SelectedTarget?.MonitorId;

    public void RequestNavigation(string page) => NavigationRequested?.Invoke(this, page);

    public void SelectTarget(string? monitorId)
        => SelectedTarget = Targets.FirstOrDefault(t => t.MonitorId == monitorId) ?? Targets.FirstOrDefault();

    public bool IsShownAnywhere(string itemId) => Snapshot.Hosts.Any(h => h.ItemId == itemId);

    public void Notify(string title, string message, NoticeLevel level = NoticeLevel.Info)
    {
        NoticeTitle = title;
        NoticeMessage = message;
        NoticeLevel = level;
        IsNoticeOpen = true;
        _noticeTimer.Stop();
        if (level is NoticeLevel.Success or NoticeLevel.Info) _noticeTimer.Start();
    }

    private void OnWallpapersChanged(object? sender, WallpaperSnapshot snapshot)
        => _dispatcher.BeginInvoke(() =>
        {
            bool monitorsChanged = !snapshot.Monitors.Select(m => m.Id).SequenceEqual(Snapshot.Monitors.Select(m => m.Id));
            Snapshot = snapshot;
            if (monitorsChanged) RebuildTargets();
        });

    private void RebuildTargets()
    {
        string? previous = SelectedTarget?.MonitorId;
        Targets.Clear();
        Targets.Add(new TargetOption("All displays", null));
        if (Snapshot.Monitors.Count > 1)
        {
            foreach (var m in Snapshot.Monitors)
                Targets.Add(new TargetOption($"{m.DisplayName} · {m.Resolution}", m.Id));
        }
        SelectTarget(previous);
    }

    /// <summary>"Set as wallpaper" for any card: download/validate, save to library, assign to the target display.</summary>
    public async Task ApplyAsync(CardViewModel card)
    {
        string where = SelectedTarget is { MonitorId: not null } t ? t.Label.Split(" · ")[0] : "all displays";
        try
        {
            var progress = new Progress<double>(card.ReportProgress);
            var item = await Actions.SetWallpaperAsync(card.Result, TargetMonitorId, progress, CancellationToken.None);
            card.Result = card.Result with { Item = item };
            Notify("Wallpaper set", $"“{item.Title}” is now on {where}.", NoticeLevel.Success);
        }
        catch (LibrarySourceException ex)
        {
            Notify("Couldn't set wallpaper", ex.Message, NoticeLevel.Error);
        }
        catch (Wavelit.Core.Storage.MediaTooLargeException)
        {
            Notify("Couldn't set wallpaper", "This video is too large for a wallpaper (over 700 MB). Pick a shorter clip.", NoticeLevel.Warning);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or InvalidOperationException or TaskCanceledException)
        {
            Notify("Couldn't set wallpaper", "The download failed. Check your connection and try again.", NoticeLevel.Error);
        }
    }

    /// <summary>Title of the item showing on a monitor, for display tiles.</summary>
    public WallpaperItem? ItemOn(string monitorId)
    {
        var host = Snapshot.Hosts.FirstOrDefault(h => h.MonitorIds.Contains(monitorId));
        return Library.Find(host?.ItemId);
    }

    public void Dispose()
    {
        _wallpapers.Changed -= OnWallpapersChanged;
        _wallpapers.Error -= OnWallpaperError;
        _noticeTimer.Stop();
    }
}
