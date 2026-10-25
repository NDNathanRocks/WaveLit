using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavelit.App.Wallpaper;
using Wavelit.Core.Diagnostics;

namespace Wavelit.App.Dashboard.ViewModels;

public sealed partial class AboutViewModel(WallpaperService wallpapers) : ObservableObject
{
    [ObservableProperty] private string _stats = "";

    public string Version { get; } =
        typeof(AboutViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

    public void Activate() => RefreshStats();

    [RelayCommand]
    private void RefreshStats()
    {
        var s = ResourceSampler.Take(wallpapers.BrowserProcessIds);
        Stats = $"Wavelit: {s.PrivateBytes / 1048576.0:F0} MB · Renderer: {s.BrowserPrivateBytes / 1048576.0:F0} MB across {s.BrowserProcesses} process(es) · Handles: {s.Handles}";
    }

    [RelayCommand]
    private static void OpenLink(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
