using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavelit.App.Wallpaper;
using Wpf.Ui.Controls;

namespace Wavelit.App.Dashboard.ViewModels;

public sealed record NavItem(string Key, string Label, SymbolRegular Icon, object Page);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly WallpaperService _wallpapers;
    private bool _disposed;

    [ObservableProperty] private NavItem _selectedNav;

    public MainViewModel(DashboardContext context, DiscoverViewModel discover, StudioViewModel studio, LibraryViewModel library,
        DisplaysViewModel displays, OverlaysViewModel overlays, DesktopViewModel desktop, SettingsViewModel settings, AboutViewModel about, WallpaperService wallpapers)
    {
        Context = context;
        Discover = discover;
        Studio = studio;
        Library = library;
        Displays = displays;
        Overlays = overlays;
        Desktop = desktop;
        Settings = settings;
        About = about;
        _wallpapers = wallpapers;
        NavItems =
        [
            new("Discover", "Discover", SymbolRegular.Search24, discover),
            new("Studio", "Studio", SymbolRegular.Sparkle24, studio),
            new("Library", "Library", SymbolRegular.ImageMultiple24, library),
            new("Displays", "Displays", SymbolRegular.Desktop24, displays),
            new("Overlays", "Overlays", SymbolRegular.Layer24, overlays),
            new("Desktop", "Desktop", SymbolRegular.DesktopEdit24, desktop),
            new("Settings", "Settings", SymbolRegular.Settings24, settings),
            new("About", "About", SymbolRegular.Info24, about),
        ];
        _selectedNav = NavItems[0];
        context.NavigationRequested += OnNavigationRequested;
        context.PropertyChanged += OnContextChanged;
        discover.Activate();
    }

    public DashboardContext Context { get; }
    public DiscoverViewModel Discover { get; }
    public StudioViewModel Studio { get; }
    public LibraryViewModel Library { get; }
    public DisplaysViewModel Displays { get; }
    public OverlaysViewModel Overlays { get; }
    public DesktopViewModel Desktop { get; }
    public SettingsViewModel Settings { get; }
    public AboutViewModel About { get; }
    public IReadOnlyList<NavItem> NavItems { get; }

    public bool IsPaused => Context.Snapshot.UserPaused;
    public string PauseLabel => IsPaused ? "Resume wallpapers" : "Pause wallpapers";
    public SymbolRegular PauseIcon => IsPaused ? SymbolRegular.Play24 : SymbolRegular.Pause24;

    partial void OnSelectedNavChanged(NavItem value)
    {
        if (value.Page is DiscoverViewModel d) d.Activate();
        if (value.Page is AboutViewModel a) a.Activate();
        if (value.Page is StudioViewModel c) c.Activate();
    }

    [RelayCommand]
    private void TogglePause() => _wallpapers.SetUserPaused(!Context.Snapshot.UserPaused);

    private void OnNavigationRequested(object? sender, string key)
        => SelectedNav = NavItems.FirstOrDefault(n => n.Key == key) ?? SelectedNav;

    private void OnContextChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DashboardContext.Snapshot)) return;
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseLabel));
        OnPropertyChanged(nameof(PauseIcon));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Context.NavigationRequested -= OnNavigationRequested;
        Context.PropertyChanged -= OnContextChanged;
        Discover.Dispose();
        Studio.Dispose();
        Library.Dispose();
        Displays.Dispose();
        Overlays.Dispose();
        Desktop.Dispose();
        Settings.Dispose();
        Context.Dispose();
    }
}
