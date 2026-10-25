using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Wavelit.App.Wallpaper;
using Wavelit.Core.Playback;
using Wavelit.Core.Storage;

namespace Wavelit.App.Tray;

/// <summary>System tray icon: left click opens the dashboard; right click shows Pause / Play / Quit.</summary>
public sealed class TrayService : IDisposable
{
    private readonly WallpaperService _wallpapers;
    private readonly LibraryStore _library;
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _status;
    private readonly MenuItem _pause;
    private readonly MenuItem _play;
    private DateTime _lastNotification;

    public TrayService(WallpaperService wallpapers, LibraryStore library, Action openDashboard, Action quit)
    {
        _wallpapers = wallpapers;
        _library = library;

        _status = new MenuItem { Header = "Wavelit", IsEnabled = false };
        _pause = new MenuItem { Header = "Pause", Icon = Symbol(Wpf.Ui.Controls.SymbolRegular.Pause24) };
        _play = new MenuItem { Header = "Play", Icon = Symbol(Wpf.Ui.Controls.SymbolRegular.Play24), IsEnabled = false };
        var open = new MenuItem { Header = "Open Wavelit", Icon = Symbol(Wpf.Ui.Controls.SymbolRegular.Apps24) };
        var quitItem = new MenuItem { Header = "Quit", Icon = Symbol(Wpf.Ui.Controls.SymbolRegular.Power24) };

        _pause.Click += (_, _) => wallpapers.SetUserPaused(true);
        _play.Click += (_, _) => wallpapers.SetUserPaused(false);
        open.Click += (_, _) => openDashboard();
        quitItem.Click += (_, _) => quit();

        var menu = new ContextMenu();
        menu.Items.Add(_status);
        menu.Items.Add(new Separator());
        menu.Items.Add(_pause);
        menu.Items.Add(_play);
        menu.Items.Add(new Separator());
        menu.Items.Add(open);
        menu.Items.Add(quitItem);

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/wavelit.ico")),
            ToolTipText = "Wavelit",
            ContextMenu = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(openDashboard),
        };
        // efficiencyMode: false — Wavelit manages its own EcoQoS state while wallpapers are suspended.
        _icon.ForceCreate(enablesEfficiencyMode: false);

        wallpapers.Changed += OnWallpapersChanged;
        Update(wallpapers.Snapshot);
    }

    public void Notify(string title, string message)
    {
        // Avoid spamming the notification area if several displays fail at once.
        if (DateTime.UtcNow - _lastNotification < TimeSpan.FromSeconds(10)) return;
        _lastNotification = DateTime.UtcNow;
        _icon.Dispatcher.BeginInvoke(() => _icon.ShowNotification(title, message, NotificationIcon.Warning));
    }

    private void OnWallpapersChanged(object? sender, WallpaperSnapshot snapshot)
        => _icon.Dispatcher.BeginInvoke(() => Update(snapshot));

    private void Update(WallpaperSnapshot snapshot)
    {
        _pause.IsEnabled = !snapshot.UserPaused;
        _play.IsEnabled = snapshot.UserPaused;

        string state = snapshot.UserPaused ? "Paused"
            : !snapshot.DesktopAvailable ? "Waiting for desktop"
            : snapshot.Hosts.Count == 0 ? "Idle"
            : snapshot.Hosts.All(h => h.State == PlaybackState.Suspended) ? $"Paused · {snapshot.Hosts[0].Reason}"
            : snapshot.Hosts.Any(h => h.State == PlaybackState.Playing) ? "Playing"
            : $"Paused · {snapshot.Hosts[0].Reason}";
        _status.Header = $"Wavelit · {state}";

        var titles = snapshot.Hosts.Select(h => _library.Find(h.ItemId)?.Title).OfType<string>().Distinct().ToList();
        string tooltip = titles.Count == 0 ? $"Wavelit — {state}" : $"Wavelit — {state}\n{string.Join(", ", titles)}";
        _icon.ToolTipText = tooltip.Length > 120 ? tooltip[..117] + "…" : tooltip;
    }

    private static Wpf.Ui.Controls.SymbolIcon Symbol(Wpf.Ui.Controls.SymbolRegular symbol) => new() { Symbol = symbol };

    public void Dispose()
    {
        _wallpapers.Changed -= OnWallpapersChanged;
        _icon.Dispose();
    }
}
