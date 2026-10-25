using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavelit.App.Wallpaper;
using Wavelit.Core.Desktop;
using Wavelit.Core.Library;
using Wavelit.Core.Playback;

namespace Wavelit.App.Dashboard.ViewModels;

public sealed partial class DisplayTileViewModel(DisplaysViewModel owner, MonitorInfo monitor) : ObservableObject
{
    public MonitorInfo Monitor { get; } = monitor;
    public string Name => Monitor.DisplayName;
    public string Resolution => Monitor.Resolution;

    [ObservableProperty] private double _left;
    [ObservableProperty] private double _top;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private string _wallpaperTitle = "Nothing";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isPlaying;

    [RelayCommand]
    private void Choose() => owner.ChooseFor(this);
}

/// <summary>Multi-monitor layout: same / per display / span, with a live map of the displays.</summary>
public sealed partial class DisplaysViewModel : ObservableObject, IDisposable
{
    private const double MapWidth = 640;
    private const double MapHeight = 240;

    private readonly WallpaperService _wallpapers;

    public DisplaysViewModel(DashboardContext context, WallpaperService wallpapers)
    {
        Context = context;
        _wallpapers = wallpapers;
        context.PropertyChanged += OnContextChanged;
        context.Settings.Changed += OnSettingsChanged;
        Rebuild();
    }

    public DashboardContext Context { get; }
    public ObservableCollection<DisplayTileViewModel> Tiles { get; } = [];
    public bool HasMultipleDisplays => Context.Snapshot.Monitors.Count > 1;
    public bool UserPaused
    {
        get => Context.Snapshot.UserPaused;
        // Routed through the service so the tray and dashboard stay in sync.
        set => _wallpapers.SetUserPaused(value);
    }

    private DisplayLayout Layout => Context.Settings.Current.Displays.Layout;

    public bool IsDuplicate
    {
        get => Layout == DisplayLayout.Duplicate;
        set { if (value) Context.Actions.SetLayout(DisplayLayout.Duplicate); }
    }

    public bool IsPerDisplay
    {
        get => Layout == DisplayLayout.PerDisplay;
        set { if (value) Context.Actions.SetLayout(DisplayLayout.PerDisplay); }
    }

    public bool IsSpan
    {
        get => Layout == DisplayLayout.Span;
        set { if (value) Context.Actions.SetLayout(DisplayLayout.Span); }
    }

    public void ChooseFor(DisplayTileViewModel tile)
    {
        Context.SelectTarget(Layout == DisplayLayout.Span || !HasMultipleDisplays ? null : tile.Monitor.Id);
        Context.RequestNavigation("Library");
    }

    private void OnSettingsChanged(object? sender, Core.Storage.AppSettings e)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(IsDuplicate));
            OnPropertyChanged(nameof(IsPerDisplay));
            OnPropertyChanged(nameof(IsSpan));
        });

    private void OnContextChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardContext.Snapshot)) Rebuild();
    }

    private void Rebuild()
    {
        var snapshot = Context.Snapshot;
        var monitors = snapshot.Monitors;

        if (!Tiles.Select(t => t.Monitor).SequenceEqual(monitors))
        {
            Tiles.Clear();
            if (monitors.Count > 0)
            {
                var all = MonitorService.GetVirtualBounds(monitors);
                double scale = Math.Min(MapWidth / all.Width, MapHeight / all.Height);
                double offsetX = (MapWidth - all.Width * scale) / 2;
                double offsetY = (MapHeight - all.Height * scale) / 2;
                foreach (var m in monitors)
                {
                    Tiles.Add(new DisplayTileViewModel(this, m)
                    {
                        Left = offsetX + (m.Bounds.Left - all.Left) * scale + 4,
                        Top = offsetY + (m.Bounds.Top - all.Top) * scale + 4,
                        Width = Math.Max(40, m.Bounds.Width * scale - 8),
                        Height = Math.Max(30, m.Bounds.Height * scale - 8),
                    });
                }
            }
        }

        foreach (var tile in Tiles)
        {
            var host = snapshot.Hosts.FirstOrDefault(h => h.MonitorIds.Contains(tile.Monitor.Id));
            tile.WallpaperTitle = Context.Library.Find(host?.ItemId)?.Title ?? "Windows wallpaper";
            tile.IsPlaying = host?.State == PlaybackState.Playing;
            tile.Status = host is null ? "Not running"
                : host.Error is { } err ? $"Error: {err}"
                : host.State == PlaybackState.Playing ? "Playing"
                : $"Paused · {host.Reason}";
        }

        OnPropertyChanged(nameof(HasMultipleDisplays));
        OnPropertyChanged(nameof(UserPaused));
    }

    public void Dispose()
    {
        Context.PropertyChanged -= OnContextChanged;
        Context.Settings.Changed -= OnSettingsChanged;
    }
}
