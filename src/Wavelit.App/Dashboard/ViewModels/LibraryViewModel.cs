using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Wavelit.App.Wallpaper;
using Wavelit.Core.Library;

namespace Wavelit.App.Dashboard.ViewModels;

/// <summary>Saved wallpapers: included shaders, downloads, local files and web pages.</summary>
public sealed partial class LibraryViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly WallpaperService _wallpapers;
    private CancellationTokenSource _thumbCts = new();
    private bool _disposed;

    [ObservableProperty] private string _urlText = "";
    [ObservableProperty] private bool _isAddingUrl;

    public LibraryViewModel(DashboardContext context, WallpaperService wallpapers)
    {
        Context = context;
        _wallpapers = wallpapers;
        context.Library.Changed += OnLibraryChanged;
        context.PropertyChanged += OnContextChanged;
        wallpapers.ThumbnailCaptured += OnThumbnailCaptured;
        Reload();
    }

    public DashboardContext Context { get; }
    public ObservableCollection<CardViewModel> Items { get; } = [];

    [RelayCommand]
    private void AddFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add wallpapers",
            Multiselect = true,
            Filter = "Videos and web pages|*.mp4;*.m4v;*.webm;*.mov;*.html;*.htm|Videos|*.mp4;*.m4v;*.webm;*.mov|Web pages|*.html;*.htm",
        };
        if (dialog.ShowDialog() == true) AddPaths(dialog.FileNames);
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var added = Context.Actions.AddLocalFiles(list);
        if (added.Count > 0)
            Context.Notify("Added to library", added.Count == 1 ? $"“{added[0].Title}” is ready to use." : $"{added.Count} wallpapers added.", NoticeLevel.Success);
        int skipped = list.Count - added.Count;
        if (skipped > 0)
            Context.Notify("Some files were skipped", "Wavelit supports MP4, M4V, WebM and MOV videos, and HTML pages.", NoticeLevel.Warning);
    }

    [RelayCommand]
    private void ToggleAddUrl() => IsAddingUrl = !IsAddingUrl;

    [RelayCommand]
    private void AddUrl()
    {
        if (string.IsNullOrWhiteSpace(UrlText)) return;
        var item = Context.Actions.AddUrl(UrlText);
        if (item is null)
        {
            Context.Notify("That address doesn't look right", "Enter a web address such as https://example.com.", NoticeLevel.Warning);
            return;
        }
        UrlText = "";
        IsAddingUrl = false;
        Context.Notify("Web page added", $"{item.Title} is in your library.", NoticeLevel.Success);
    }

    private void Remove(CardViewModel card)
    {
        Context.Actions.Remove(card.Item);
        Context.Notify("Removed", $"“{card.Item.Title}” was removed from your library.", NoticeLevel.Info);
    }

    private void Reload()
    {
        _thumbCts.Cancel();
        _thumbCts.Dispose();
        _thumbCts = new CancellationTokenSource();
        var token = _thumbCts.Token;

        Items.Clear();
        foreach (var item in Context.Library.All)
        {
            var card = new CardViewModel(new SearchResult(item), Context.ApplyAsync, Remove)
            {
                IsActive = Context.IsShownAnywhere(item.Id),
            };
            Items.Add(card);
            _ = LoadThumbnailAsync(card, token);
        }
    }

    private async Task LoadThumbnailAsync(CardViewModel card, CancellationToken ct)
    {
        try
        {
            card.Thumbnail = await Context.Thumbnails.LoadAsync(card.Item, ct);
        }
        catch (OperationCanceledException) { }
    }

    private void OnLibraryChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(Reload);

    private void OnThumbnailCaptured(object? sender, string itemId) => _dispatcher.BeginInvoke(() =>
    {
        foreach (var card in Items.Where(c => c.Item.Id == itemId))
            _ = LoadThumbnailAsync(card, _thumbCts.Token);
    });

    private void OnContextChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DashboardContext.Snapshot)) return;
        foreach (var card in Items) card.IsActive = Context.IsShownAnywhere(card.Item.Id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _thumbCts.Cancel();
        _thumbCts.Dispose();
        Context.Library.Changed -= OnLibraryChanged;
        Context.PropertyChanged -= OnContextChanged;
        _wallpapers.ThumbnailCaptured -= OnThumbnailCaptured;
    }
}
