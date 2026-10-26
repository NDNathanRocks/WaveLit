using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavelit.App.Services;
using Wavelit.App.Wallpaper;
using Wavelit.Core.Library;
using Wavelit.Core.Library.BuiltIn;
using Wavelit.Core.Storage;

namespace Wavelit.App.Dashboard.ViewModels;

public sealed partial class StarterOption(WallpaperItem item) : ObservableObject
{
    public WallpaperItem Item { get; } = item;
    [ObservableProperty] private ImageSource? _thumbnail;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// First-run setup, three short steps: a starting wallpaper (applied live), how multiple screens are used,
/// and optional extras (the Local AI download, which is off unless the user ticks it).
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly LibraryActions _actions;
    private readonly LocalAiService _localAi;

    [ObservableProperty] private bool _extend = true;
    [ObservableProperty] private bool _startWithWindows = true;
    [ObservableProperty] private bool _wantLocalAi;
    [ObservableProperty] private bool _acceptedLicense;
    [ObservableProperty] private bool _interactive;

    public WelcomeViewModel(SettingsStore settings, LibraryActions actions, LocalAiService localAi, ThumbnailLoader thumbnails, WallpaperService wallpapers, AppPaths paths)
    {
        _settings = settings;
        _actions = actions;
        _localAi = localAi;
        LocalAi = new LocalAiViewModel(localAi, paths);
        HasMultipleDisplays = wallpapers.Snapshot.Monitors.Count > 1;
        foreach (var item in BuiltInShaders.Items)
        {
            var option = new StarterOption(item) { IsSelected = item.Id == settings.Current.Displays.SharedItemId };
            Starters.Add(option);
            _ = LoadAsync(option, thumbnails);
        }
    }

    public ObservableCollection<StarterOption> Starters { get; } = [];
    public LocalAiViewModel LocalAi { get; }
    public bool HasMultipleDisplays { get; }

    /// <summary>Raised when the user finishes; the dashboard opens next.</summary>
    public event EventHandler? Finished;

    private static async Task LoadAsync(StarterOption option, ThumbnailLoader thumbnails)
        => option.Thumbnail = await thumbnails.LoadAsync(option.Item, CancellationToken.None);

    [RelayCommand]
    private void Pick(StarterOption option)
    {
        foreach (var s in Starters) s.IsSelected = s == option;
        _actions.Assign(option.Item.Id, null); // applied live, behind this window
    }

    /// <summary>Applied live like the starter wallpaper, so the user can try it on the desktop right away.</summary>
    partial void OnInteractiveChanged(bool value)
        => _settings.Update(s => s with { Interactive = s.Interactive with { Style = value ? "stardust" : InteractiveSettings.Off } });

    partial void OnExtendChanged(bool value)
        => _actions.SetLayout(value ? DisplayLayout.Span : DisplayLayout.Duplicate);

    public bool CanFinish => !WantLocalAi || AcceptedLicense;
    partial void OnWantLocalAiChanged(bool value) => OnPropertyChanged(nameof(CanFinish));
    partial void OnAcceptedLicenseChanged(bool value) => OnPropertyChanged(nameof(CanFinish));

    [RelayCommand]
    private void Finish()
    {
        if (!CanFinish) return;
        _settings.Update(s => s with { StartWithWindows = StartWithWindows });
        if (WantLocalAi && LocalAi.GpuSupported) _localAi.StartDownload();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => LocalAi.Dispose();
}
