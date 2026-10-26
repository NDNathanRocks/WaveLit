using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavelit.App.Services;
using Wavelit.Core.Create;
using Wavelit.Core.Library;
using Wavelit.Core.Storage;

namespace Wavelit.App.Dashboard.ViewModels;

public sealed record GeneratorOption(string Id, string Label, string Description);

public sealed partial class CreationResultViewModel(CreationCandidate candidate, ImageSource? image, Func<CreationResultViewModel, Task> use) : ObservableObject
{
    public CreationCandidate Candidate { get; } = candidate;
    public ImageSource? Image { get; } = image;
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private Task Use() => use(this);
}

/// <summary>Create page: describe a wallpaper, pick a style and a generator, get four options shaped like your screens.</summary>
public sealed partial class CreateViewModel : ObservableObject, IDisposable
{
    private readonly CreateService _create;
    private readonly SecretStore _secrets;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _prompt = "";
    [ObservableProperty] private StylePreset _selectedStyle;
    [ObservableProperty] private GeneratorOption _selectedGenerator;
    [ObservableProperty] private EffectOption _selectedEffect;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _stage = "";
    [ObservableProperty] private string? _message;

    public CreateViewModel(DashboardContext context, CreateService create, LocalAiViewModel localAi, SecretStore secrets)
    {
        Context = context;
        _create = create;
        _secrets = secrets;
        LocalAi = localAi;
        _selectedStyle = Styles[0];
        // Best available by default: this PC, then the user's Cloudflare account, then the quick try.
        _selectedGenerator = localAi.IsInstalled ? Generators[2]
            : secrets.Has(SecretNames.CloudflareApiToken) ? Generators[1]
            : Generators[0];
        _selectedEffect = Effects[0];
        localAi.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LocalAiViewModel.IsInstalled)) OnPropertyChanged(nameof(NeedsLocalSetup)); };
    }

    public DashboardContext Context { get; }
    public LocalAiViewModel LocalAi { get; }
    public ObservableCollection<CreationResultViewModel> Results { get; } = [];
    public IReadOnlyList<StylePreset> Styles => Prompts.Styles;

    public IReadOnlyList<GeneratorOption> Generators { get; } =
    [
        new(GeneratorIds.Cloud, "Quick try (no account)", "Pollinations.ai: no sign-up, but lower quality and it may add a small logo. Your prompt is sent to Pollinations."),
        new(GeneratorIds.Cloudflare, "Cloudflare (free account)", "Sharp, no logo, free daily allowance on your own Cloudflare account (add it in Settings)."),
        new(GeneratorIds.Local, "On this PC (best)", "Private, unlimited and sharp. Needs the Local AI download and a graphics card with 4 GB or more."),
    ];

    public IReadOnlyList<EffectOption> Effects { get; } =
    [
        new(ImageEffects.None, "Still"),
        new(ImageEffects.Drift, "Slow drift"),
        new(ImageEffects.Rain, "Rain"),
        new(ImageEffects.Snow, "Snow"),
        new(ImageEffects.Fireflies, "Fireflies"),
        new(ImageEffects.Dust, "Floating dust"),
    ];

    public IReadOnlyList<string> Ideas { get; } =
    [
        "a quiet mountain lake at dawn, mist on the water",
        "a cozy rainy city street at night, warm shop lights",
        "floating islands above the clouds, waterfalls, golden hour",
        "an underwater coral reef with sunbeams",
        "a lone lighthouse on a cliff under the northern lights",
        "a futuristic city skyline with flying trains at sunset",
    ];

    public bool NeedsLocalSetup => SelectedGenerator.Id == GeneratorIds.Local && !LocalAi.IsInstalled;
    public bool NeedsCloudflareKey => SelectedGenerator.Id == GeneratorIds.Cloudflare
        && !(_secrets.Has(SecretNames.CloudflareAccountId) && _secrets.Has(SecretNames.CloudflareApiToken));
    public bool HasResults => Results.Count > 0;

    public string ShapeText
    {
        get
        {
            var (w, h) = _create.CanvasSize(Context.TargetMonitorId);
            string where = Context.TargetMonitorId is null && Context.Snapshot.Monitors.Count > 1
                && Context.Settings.Current.Displays.Layout == DisplayLayout.Span ? "all your screens (Extend)" : "your screen";
            return $"Made to fit {where}: {w} × {h}";
        }
    }

    partial void OnSelectedGeneratorChanged(GeneratorOption value)
    {
        OnPropertyChanged(nameof(NeedsLocalSetup));
        OnPropertyChanged(nameof(NeedsCloudflareKey));
        GenerateCommand.NotifyCanExecuteChanged();
    }

    partial void OnPromptChanged(string value) => GenerateCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => GenerateCommand.NotifyCanExecuteChanged();

    public void Activate()
    {
        OnPropertyChanged(nameof(ShapeText));
        OnPropertyChanged(nameof(NeedsCloudflareKey));
    }

    [RelayCommand]
    private void UseIdea(string idea) => Prompt = idea;

    [RelayCommand]
    private void OpenSettings() => Context.RequestNavigation("Settings");

    private bool CanGenerate() => !IsBusy && Prompt.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task Generate()
    {
        if (NeedsLocalSetup || NeedsCloudflareKey) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Message = null;
        Progress = 0;
        Stage = "Starting…";
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        var progress = new Progress<(double Value, string Stage)>(p => { Progress = p.Value * 100; Stage = p.Stage; });
        try
        {
            var (images, blocked) = await _create.GenerateAsync(Prompt, SelectedStyle, SelectedGenerator.Id, Context.TargetMonitorId, progress, _cts.Token);
            foreach (var image in images)
                Results.Add(new CreationResultViewModel(image, Decode(image.Path), UseAsync));
            OnPropertyChanged(nameof(HasResults));
            Message = images.Count == 0
                ? blocked > 0 ? "Those results didn't pass the safety check. Try a different prompt." : "No images came back. Try again."
                : blocked > 0 ? $"{blocked} result(s) were hidden by the safety check." : null;
        }
        catch (OperationCanceledException)
        {
            Message = "Stopped.";
        }
        catch (GenerationException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            Message = "Something went wrong while creating. Try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task UseAsync(CreationResultViewModel result)
    {
        if (IsBusy) return;
        result.IsBusy = true;
        IsBusy = true;
        Progress = 0;
        Stage = "Sharpening to full screen size…";
        var progress = new Progress<(double Value, string Stage)>(p => { Progress = p.Value * 100; Stage = p.Stage; });
        try
        {
            var item = await _create.KeepAsync(result.Candidate, SelectedEffect.Key, Context.TargetMonitorId, progress, CancellationToken.None);
            Context.Actions.Assign(item.Id, Context.TargetMonitorId);
            Context.Notify("Wallpaper set", $"“{item.Title}” is now your wallpaper. It's saved in your Library.", NoticeLevel.Success);
        }
        catch (IOException)
        {
            Context.Notify("Couldn't save the image", "Check that there's free disk space and try again.", NoticeLevel.Error);
        }
        finally
        {
            result.IsBusy = false;
            IsBusy = false;
        }
    }

    private static BitmapImage? Decode(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // no file lock: pending files are deleted later
            bmp.DecodePixelWidth = 640;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        LocalAi.Dispose();
    }
}
