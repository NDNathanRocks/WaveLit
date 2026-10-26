using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavelit.App.Services;
using Wavelit.Core.Create;
using Wavelit.Core.Storage;

namespace Wavelit.App.Dashboard.ViewModels;

/// <summary>
/// The "Local AI" card (Create page, Settings, first-run setup): what it is, size and disk space, whether
/// this PC's graphics card is suitable, and Download / Pause / Resume / Remove.
/// </summary>
public sealed partial class LocalAiViewModel : ObservableObject, IDisposable
{
    private readonly LocalAiService _service;
    private readonly AppPaths _paths;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    [ObservableProperty] private bool _acceptedLicense;

    public LocalAiViewModel(LocalAiService service, AppPaths paths)
    {
        _service = service;
        _paths = paths;
        _service.Changed += OnChanged;
    }

    public LocalAiState State => _service.State;
    public bool IsInstalled => State == LocalAiState.Installed;
    public bool IsDownloading => State == LocalAiState.Downloading;
    public bool CanStart => State is LocalAiState.NotInstalled or LocalAiState.Paused or LocalAiState.Failed;
    public bool ShowProgress => State is LocalAiState.Downloading or LocalAiState.Paused or LocalAiState.Failed;
    public double ProgressPercent => _service.Progress.Fraction * 100;

    public string SizeText => $"{LocalModelPack.TotalBytes / 1e9:0.0} GB download";

    public string DiskText
    {
        get
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(_paths.Models)!);
                double freeGb = drive.AvailableFreeSpace / 1e9;
                return freeGb < LocalModelPack.TotalBytes / 1e9 + 2
                    ? $"Not enough free space on {drive.Name.TrimEnd('\\')} ({freeGb:0} GB free)"
                    : $"{freeGb:0} GB free on {drive.Name.TrimEnd('\\')}";
            }
            catch (IOException)
            {
                return "";
            }
        }
    }

    public string GpuText => _service.Gpu is not { } gpu
        ? "No compatible graphics card found. Use Cloud instead."
        : gpu.Verdict switch
        {
            GpuVerdict.Great => $"Great on this PC: {gpu.Name} ({gpu.VideoMemoryGb:0} GB). About 2–6 seconds per image.",
            GpuVerdict.Slow => $"Will be slow on {gpu.Name} ({gpu.VideoMemoryGb:0} GB of video memory). Cloud is faster.",
            _ => $"Not supported on {gpu.Name}: it needs a graphics card with 4 GB or more. Use Cloud instead.",
        };

    public bool GpuSupported => _service.Gpu?.Verdict is GpuVerdict.Great or GpuVerdict.Slow;

    public string StatusText => State switch
    {
        LocalAiState.Installed => "Installed. Wallpapers are made entirely on this PC.",
        LocalAiState.Downloading => $"Downloading… {ProgressPercent:0}% ({_service.Progress.BytesDone / 1e9:0.0} of {LocalModelPack.TotalBytes / 1e9:0.0} GB)",
        LocalAiState.Paused => $"Paused at {ProgressPercent:0}%. Resume any time; nothing is lost.",
        LocalAiState.Failed => _service.Error ?? "The download stopped.",
        _ => "Not installed.",
    };

    public string StartLabel => State is LocalAiState.Paused or LocalAiState.Failed ? "Resume download" : "Download";

    public string SpaceUsedText => $"Uses {LocalModelPack.BytesOnDisk(_paths) / 1e9:0.0} GB";

    [RelayCommand]
    private void Start()
    {
        if (!AcceptedLicense && State == LocalAiState.NotInstalled) return;
        _service.StartDownload();
    }

    [RelayCommand]
    private void Pause() => _service.PauseDownload();

    [RelayCommand]
    private void Remove() => _service.Remove();

    [RelayCommand]
    private static void OpenLicense() => Process.Start(new ProcessStartInfo(LocalModelPack.LicenseUrl) { UseShellExecute = true });

    partial void OnAcceptedLicenseChanged(bool value) => StartCommand.NotifyCanExecuteChanged();

    private void OnChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        foreach (var name in new[]
                 {
                     nameof(State), nameof(IsInstalled), nameof(IsDownloading), nameof(CanStart), nameof(ShowProgress),
                     nameof(ProgressPercent), nameof(StatusText), nameof(StartLabel), nameof(SpaceUsedText), nameof(DiskText),
                 })
            OnPropertyChanged(name);
    });

    public void Dispose() => _service.Changed -= OnChanged;
}
