using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavelit.App.Wallpaper;
using Wavelit.Core.Library;
using Wavelit.Core.Library.BuiltIn;
using Wavelit.Core.Library.Pexels;
using Wavelit.Core.Library.Shadertoy;

namespace Wavelit.App.Dashboard.ViewModels;

/// <summary>Unified Pexels + Shadertoy search.</summary>
public sealed partial class DiscoverViewModel : ObservableObject, IDisposable
{
    private const int ShaderPageSize = 24;
    private readonly PexelsClient _pexels;
    private readonly ShadertoyClient _shadertoy;
    private readonly DispatcherTimer _debounce;
    private readonly SemaphoreSlim _detailsGate = new(4, 4);
    private CancellationTokenSource? _searchCts;
    private int _pexelsPage;
    private bool _pexelsHasMore;
    private int _shaderFrom;
    private bool _shaderHasMore;
    private bool _loadingMore;
    private bool _initialized;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _includeVideos = true;
    [ObservableProperty] private bool _includeShaders = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInitialLoading))]
    private bool _isLoading;
    [ObservableProperty] private string? _emptyMessage;
    [ObservableProperty] private string? _sourceNotice;

    public DiscoverViewModel(DashboardContext context, PexelsClient pexels, ShadertoyClient shadertoy)
    {
        Context = context;
        _pexels = pexels;
        _shadertoy = shadertoy;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = SearchAsync();
        };
        context.PropertyChanged += OnContextChanged;
    }

    public DashboardContext Context { get; }
    public ObservableCollection<CardViewModel> Results { get; } = [];
    public bool HasMore => _pexelsHasMore || _shaderHasMore;
    /// <summary>Spinner only for the first page; infinite scroll loads quietly below the fold.</summary>
    public bool IsInitialLoading => IsLoading && Results.Count == 0;

    /// <summary>Called when the page becomes visible.</summary>
    public void Activate()
    {
        if (_initialized) return;
        _initialized = true;
        _ = SearchAsync();
    }

    partial void OnQueryChanged(string value) => RestartDebounce();
    partial void OnIncludeVideosChanged(bool value) => RestartDebounce();
    partial void OnIncludeShadersChanged(bool value) => RestartDebounce();

    private void RestartDebounce()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    [RelayCommand]
    private Task SearchAsync()
    {
        _debounce.Stop();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        Results.Clear();
        _pexelsPage = 0;
        _shaderFrom = 0;
        _pexelsHasMore = IncludeVideos && _pexels.IsConfigured;
        _shaderHasMore = IncludeShaders && _shadertoy.IsConfigured;
        EmptyMessage = null;
        SourceNotice = BuildSourceNotice();

        if (!_pexels.IsConfigured && !_shadertoy.IsConfigured || !_pexelsHasMore && !_shaderHasMore)
        {
            ShowBuiltIns();
            return Task.CompletedTask;
        }
        return LoadPageAsync(_searchCts.Token);
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_loadingMore || IsLoading || !HasMore || _searchCts is null) return;
        _loadingMore = true;
        try
        {
            await LoadPageAsync(_searchCts.Token);
        }
        finally
        {
            _loadingMore = false;
        }
    }

    [RelayCommand]
    private void OpenSettings() => Context.RequestNavigation("Settings");

    private void ShowBuiltIns()
    {
        foreach (var item in BuiltInShaders.Items.Where(i => string.IsNullOrWhiteSpace(Query) || i.Title.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase)))
            AddCard(new CardViewModel(new SearchResult(item), Context.ApplyAsync));
        if (Results.Count == 0) EmptyMessage = "No matches in the included wallpapers.";
    }

    private string? BuildSourceNotice()
    {
        if (!_pexels.IsConfigured && !_shadertoy.IsConfigured)
            return "Showing the included wallpapers. Add free Pexels or Shadertoy API keys in Settings to search thousands more.";
        if (IncludeVideos && !_pexels.IsConfigured)
            return "Add a free Pexels API key in Settings to include videos.";
        if (IncludeShaders && !_shadertoy.IsConfigured)
            return "Add a Shadertoy API key in Settings to include live shaders.";
        return null;
    }

    private async Task LoadPageAsync(CancellationToken ct)
    {
        IsLoading = true;
        var errors = new List<string>();
        try
        {
            string q = Query.Trim();
            int maxHeight = Context.Snapshot.Monitors.Count > 0 ? Context.Snapshot.Monitors.Max(m => m.Bounds.Height) : 1080;

            Task<SearchPage?> videos = _pexelsHasMore
                ? Catch(() => _pexels.SearchAsync(q, ++_pexelsPage, maxHeight, ct), errors)
                : Task.FromResult<SearchPage?>(null);
            Task<ShaderIds?> shaders = _shaderHasMore
                ? Catch(async () =>
                {
                    var (ids, total) = await _shadertoy.SearchIdsAsync(q, _shaderFrom, ShaderPageSize, ct);
                    return new ShaderIds(ids, total);
                }, errors)
                : Task.FromResult<ShaderIds?>(null);

            await Task.WhenAll(videos, shaders);
            ct.ThrowIfCancellationRequested();

            var videoPage = videos.Result;
            if (videoPage is not null) _pexelsHasMore = videoPage.HasMore;
            else if (_pexelsHasMore) _pexelsHasMore = false;

            var shaderCards = new List<CardViewModel>();
            if (shaders.Result is { } s)
            {
                _shaderFrom += s.Ids.Count;
                _shaderHasMore = s.Ids.Count == ShaderPageSize && _shaderFrom < s.Total;
                foreach (var id in s.Ids)
                {
                    var placeholder = new WallpaperItem
                    {
                        Id = $"shadertoy-{id}",
                        Kind = WallpaperKind.Shader,
                        Source = WallpaperSources.Shadertoy,
                        Title = id,
                        ShaderId = id,
                        ThumbnailUrl = $"https://www.shadertoy.com/media/shaders/{id}.jpg",
                        PageUrl = $"https://www.shadertoy.com/view/{id}",
                    };
                    shaderCards.Add(new CardViewModel(new SearchResult(placeholder), Context.ApplyAsync) { IsDetailsLoaded = false });
                }
            }
            else
            {
                _shaderHasMore = false;
            }

            // Interleave so both sources are visible at the top of the grid.
            var videoCards = (videoPage?.Results ?? []).Select(r => new CardViewModel(r, Context.ApplyAsync)).ToList();
            int max = Math.Max(videoCards.Count, shaderCards.Count);
            for (int i = 0; i < max; i++)
            {
                if (i < videoCards.Count) AddCard(videoCards[i]);
                if (i < shaderCards.Count) AddCard(shaderCards[i]);
            }
            foreach (var card in shaderCards) _ = LoadShaderDetailsAsync(card, ct);

            if (errors.Count > 0) Context.Notify("Search problem", errors[0], NoticeLevel.Warning);
            if (Results.Count == 0)
                EmptyMessage = errors.Count > 0 ? errors[0] : string.IsNullOrEmpty(q) ? "Nothing to show yet." : $"No results for “{q}”. Try another word.";
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoading = false;
            OnPropertyChanged(nameof(HasMore));
        }
    }

    private static async Task<T?> Catch<T>(Func<Task<T>> work, List<string> errors) where T : class
    {
        try { return await work(); }
        catch (LibrarySourceException ex) { errors.Add(ex.Message); return null; }
    }

    private sealed record ShaderIds(IReadOnlyList<string> Ids, int Total);

    private async Task LoadShaderDetailsAsync(CardViewModel card, CancellationToken ct)
    {
        await _detailsGate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            var shader = await _shadertoy.GetShaderAsync(card.Item.ShaderId!, ct);
            if (ShadertoyClient.UnsupportedReason(shader) is not null)
            {
                Results.Remove(card); // e.g. needs webcam or microphone input
                return;
            }
            card.Result = new SearchResult(ShadertoyClient.ToItem(shader));
            card.IsDetailsLoaded = true;
        }
        catch (OperationCanceledException) { }
        catch (LibrarySourceException)
        {
            Results.Remove(card);
        }
        finally
        {
            _detailsGate.Release();
        }
    }

    private void AddCard(CardViewModel card)
    {
        card.IsActive = Context.IsShownAnywhere(card.Item.Id);
        Results.Add(card);
        _ = LoadThumbnailAsync(card, _searchCts?.Token ?? CancellationToken.None);
    }

    private async Task LoadThumbnailAsync(CardViewModel card, CancellationToken ct)
    {
        try
        {
            card.Thumbnail = await Context.Thumbnails.LoadAsync(card.Item, ct);
        }
        catch (OperationCanceledException) { }
    }

    private void OnContextChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DashboardContext.Snapshot)) return;
        foreach (var card in Results) card.IsActive = Context.IsShownAnywhere(card.Item.Id);
    }

    public void Dispose()
    {
        _debounce.Stop();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        Context.PropertyChanged -= OnContextChanged;
    }
}
