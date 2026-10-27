using System.Diagnostics;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavelit.Core.Library;

namespace Wavelit.App.Dashboard.ViewModels;

/// <summary>A thumbnail card in the Discover or Library grid.</summary>
public sealed partial class CardViewModel : ObservableObject
{
    private static readonly Color[][] Placeholders =
    [
        [Color.FromRgb(0x4C, 0x29, 0xD2), Color.FromRgb(0xEC, 0x48, 0x99)],
        [Color.FromRgb(0x0E, 0x76, 0xA8), Color.FromRgb(0x22, 0xC5, 0x5E)],
        [Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0x06, 0xB6, 0xD4)],
        [Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0xDB, 0x27, 0x77)],
        [Color.FromRgb(0x1E, 0x3A, 0x8A), Color.FromRgb(0x93, 0x33, 0xEA)],
        [Color.FromRgb(0x0F, 0x76, 0x6E), Color.FromRgb(0x38, 0xBD, 0xF8)],
    ];

    private readonly Func<CardViewModel, Task> _onSet;
    private readonly Action<CardViewModel>? _onRemove;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle), nameof(Badge), nameof(Item), nameof(Attribution))]
    private SearchResult _result;

    [ObservableProperty] private ImageSource? _thumbnail;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isDetailsLoaded = true;
    /// <summary>"Perfect fit" / "Good fit" / "Will crop" / "Low resolution" for the user's screens.</summary>
    [ObservableProperty] private string? _fitLabel;

    public CardViewModel(SearchResult result, Func<CardViewModel, Task> onSet, Action<CardViewModel>? onRemove = null)
    {
        _result = result;
        _onSet = onSet;
        _onRemove = onRemove;
        var palette = Placeholders[(int)((uint)StringComparer.Ordinal.GetHashCode(result.Item.Id) % Placeholders.Length)];
        PlaceholderStart = palette[0];
        PlaceholderEnd = palette[1];
    }

    public WallpaperItem Item => Result.Item;
    public string Title => IsDetailsLoaded ? Item.Title : "Loading…";

    /// <summary>Hover text: title, creator, source and licence (CC BY / BY-SA attribution).</summary>
    public string Attribution => Item.License is { Length: > 0 } license && IsDetailsLoaded && !Item.IsBuiltIn
        ? $"{Title}\n{Subtitle}\nLicence: {license}"
        : Title;

    public Color PlaceholderStart { get; }
    public Color PlaceholderEnd { get; }
    public bool CanRemove => _onRemove is not null && !Item.IsBuiltIn;
    public bool HasPage => Item.PageUrl is not null;

    /// <summary>"Made with AI" items get the label on their badge (plan §21.6).</summary>
    public string Badge => Item.IsAiMade ? "MADE WITH AI" : Item.Kind switch
    {
        WallpaperKind.Video => "VIDEO",
        WallpaperKind.Shader => "LIVE SHADER",
        WallpaperKind.Image when Item.Source == WallpaperSources.Generated => "CREATION",
        WallpaperKind.Image => ImageEffects.Normalize(Item.Effect) == ImageEffects.None ? "IMAGE" : "LIVING IMAGE",
        _ => "WEB",
    };

    public string Subtitle
    {
        get
        {
            string source = Item.Source switch
            {
                WallpaperSources.Pexels => "Pexels",
                WallpaperSources.Pixabay => "Pixabay",
                WallpaperSources.Wallhaven => "Wallhaven",
                WallpaperSources.Nasa => "NASA",
                WallpaperSources.Openverse => "Openverse",
                WallpaperSources.Commons => "Wikimedia Commons",
                WallpaperSources.Shadertoy => "Shadertoy",
                WallpaperSources.Pasted => "Your shader",
                WallpaperSources.Generated => "Created by you",
                WallpaperSources.GeneratedShader => "Made in Studio",
                WallpaperSources.BuiltIn => "Included",
                WallpaperSources.Local => "On this PC",
                _ => "Web page",
            };
            string duration = Item.DurationSeconds is > 0 and var d ? $" · {TimeSpan.FromSeconds(d):m\\:ss}" : "";
            return Item.Author is { Length: > 0 } author && Item.Source != WallpaperSources.BuiltIn
                ? $"{author} · {source}{duration}"
                : $"{source}{duration}";
        }
    }

    partial void OnIsDetailsLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Attribution));
    }

    [RelayCommand]
    private async Task SetAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsIndeterminate = true;
        Progress = 0;
        try
        {
            await _onSet(this);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>AI creations can be reported (Microsoft Store 11.16): opens a pre-filled public issue in the browser.</summary>
    public bool CanReport => Item.IsAiMade;

    [RelayCommand]
    private void Report()
    {
        string url = Wavelit.Core.Create.ContentReport.IssueUrl("other", null, Item.Title, Item.MadeWith ?? Item.Source, null,
            typeof(CardViewModel).Assembly.GetName().Version?.ToString() ?? "dev");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Serilog.Log.Warning(ex, "Couldn't open the report page");
        }
    }

    [RelayCommand]
    private void OpenPage()
    {
        if (Item.PageUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    [RelayCommand]
    private void Remove() => _onRemove?.Invoke(this);

    public void ReportProgress(double value)
    {
        IsIndeterminate = false;
        Progress = value * 100;
    }
}
