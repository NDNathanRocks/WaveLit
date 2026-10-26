using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavelit.App.Services;
using Wavelit.Core.Library;
using Wavelit.Core.Library.BuiltIn;
using Wavelit.Core.Library.Shadertoy;
using Wavelit.Core.Library.Sources;

namespace Wavelit.App.Dashboard.ViewModels;

/// <summary>
/// One search over every online library: free sources need no key (Wallhaven, Commons, NASA, Openverse), others
/// use the user's free key (Pixabay, Pexels, Shadertoy). Results are interleaved so every source shows.
/// With an empty search box the page is curated: Wavelit's shaders, the hand-picked Featured list, then the
/// selected mood's curated source queries (plan §16.3).
/// </summary>
public sealed partial class DiscoverViewModel : ObservableObject, IDisposable
{
    private const int ShaderPageSize = 24;
    private readonly IReadOnlyList<IWallpaperSource> _sources;
    private readonly ShadertoyClient _shadertoy;
    private readonly CreateService _create;
    private readonly DispatcherTimer _debounce;
    private readonly SemaphoreSlim _detailsGate = new(4, 4);
    private readonly Dictionary<string, (int Page, bool HasMore)> _paging = new();
    private readonly HashSet<string> _shown = [];
    private CancellationTokenSource? _searchCts;
    private int _shaderFrom;
    private bool _shaderHasMore;
    private bool _loadingMore;
    private bool _initialized;
    private SourceQuery? _activeQuery;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _includeVideos = true;
    [ObservableProperty] private bool _includeImages = true;
    [ObservableProperty] private bool _includeShaders = true;
    [ObservableProperty] private bool _fitOnly;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInitialLoading))]
    private bool _isLoading;
    [ObservableProperty] private string? _emptyMessage;
    [ObservableProperty] private string? _sourceNotice;
    /// <summary>The mood chip; null while the user is searching their own words.</summary>
    [ObservableProperty] private Mood? _selectedMood = Moods.Featured;

    public DiscoverViewModel(DashboardContext context, IEnumerable<IWallpaperSource> sources, ShadertoyClient shadertoy, CreateService create)
    {
        Context = context;
        _sources = sources.ToList();
        _shadertoy = shadertoy;
        _create = create;
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
    public IReadOnlyList<Mood> MoodChips => Moods.All;
    public bool HasMore => _paging.Values.Any(p => p.HasMore) || _shaderHasMore;
    /// <summary>Spinner only for the first page; infinite scroll loads quietly below the fold.</summary>
    public bool IsInitialLoading => IsLoading && Results.Count == 0;

    /// <summary>Called when the page becomes visible.</summary>
    public void Activate()
    {
        if (_initialized) return;
        _initialized = true;
        _ = SearchAsync();
    }

    partial void OnQueryChanged(string value)
    {
        // Typing leaves the mood chips; clearing the box returns to the curated home.
        if (value.Trim().Length > 0) SelectedMood = null;
        else SelectedMood ??= Moods.Featured;
        RestartDebounce();
    }

    partial void OnSelectedMoodChanged(Mood? value)
    {
        if (value is null) return;
        Query = "";
        if (_initialized) _ = SearchAsync();
    }

    partial void OnIncludeVideosChanged(bool value) => RestartDebounce();
    partial void OnIncludeImagesChanged(bool value) => RestartDebounce();
    partial void OnIncludeShadersChanged(bool value) => RestartDebounce();
    partial void OnFitOnlyChanged(bool value) => RestartDebounce();

    private void RestartDebounce()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private IEnumerable<IWallpaperSource> ActiveSources => _sources.Where(s => s.IsAvailable
        && (s.Kind == SourceKind.Videos ? IncludeVideos : IncludeImages));

    [RelayCommand]
    private Task SearchAsync()
    {
        _debounce.Stop();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        Results.Clear();
        _shown.Clear();
        _paging.Clear();
        foreach (var s in ActiveSources) _paging[s.Id] = (0, true);
        _shaderFrom = 0;
        _shaderHasMore = IncludeShaders && _shadertoy.IsConfigured;
        EmptyMessage = null;
        SourceNotice = BuildSourceNotice();

        var (cw, ch) = _create.CanvasSize(Context.TargetMonitorId);
        int maxHeight = Context.Snapshot.Monitors.Count > 0 ? Context.Snapshot.Monitors.Max(m => m.Bounds.Height) : 1080;
        string text = Query.Trim();
        var mood = text.Length == 0 ? SelectedMood ?? Moods.Featured : null;
        _activeQuery = new SourceQuery(text, cw, ch, maxHeight, FitOnly, mood);

        // The included wallpapers always lead when shaders are wanted (they need no key and fit any setup),
        // then the hand-picked Featured list for the mood.
        if (IncludeShaders) ShowBuiltIns(mood);
        if (mood is not null) ShowFeatured(mood, _activeQuery);
        if (_paging.Count == 0 && !_shaderHasMore)
        {
            if (Results.Count == 0) EmptyMessage = "Turn on Videos, Images or Shaders to search.";
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

    private void ShowBuiltIns(Mood? mood)
    {
        string q = Query.Trim();
        var items = mood is null
            ? BuiltInShaders.Items.Where(i => i.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            : BuiltInShaders.Items.Where(i => mood == Moods.Featured || BuiltInShaders.MoodOf(i.Id) == mood.Id);
        foreach (var item in items)
            AddCard(new CardViewModel(new SearchResult(item), Context.ApplyAsync));
    }

    private void ShowFeatured(Mood mood, SourceQuery query)
    {
        foreach (var result in FeaturedCatalog.For(mood))
        {
            if (result.Item.Kind == WallpaperKind.Video ? !IncludeVideos : !IncludeImages) continue;
            var card = new CardViewModel(result, Context.ApplyAsync) { FitLabel = FitLabel(result.Item, query) };
            if (query.FitOnly && card.FitLabel is not ("Perfect fit" or "Good fit" or null)) continue;
            AddCard(card);
        }
    }

    private string? BuildSourceNotice()
    {
        if (IncludeVideos && !_sources.Any(s => s.Id == WallpaperSources.Pixabay && s.IsAvailable))
            return "Videos come from Wikimedia Commons. Add a free Pixabay key in Settings (instant sign-up) for thousands of 4K stock videos.";
        if (IncludeShaders && !_shadertoy.IsConfigured)
            return "Shaders: showing Wavelit's own. Shadertoy search needs a Shadertoy key (optional). You can also paste any shader in Library.";
        return null;
    }

    private async Task LoadPageAsync(CancellationToken ct)
    {
        if (_activeQuery is null) return;
        IsLoading = true;
        var errors = new List<string>();
        try
        {
            var query = _activeQuery;
            var pending = _paging.Where(p => p.Value.HasMore).Select(p => p.Key).ToList();
            var searches = pending.Select(id => (Id: id, Task: Catch(() =>
                _sources.First(s => s.Id == id).SearchAsync(query, _paging[id].Page + 1, ct), errors))).ToList();
            Task<ShaderIds?> shaders = _shaderHasMore
                ? Catch(async () =>
                {
                    var (ids, total) = await _shadertoy.SearchIdsAsync(query.Text, _shaderFrom, ShaderPageSize, ct);
                    return new ShaderIds(ids, total);
                }, errors)
                : Task.FromResult<ShaderIds?>(null);

            await Task.WhenAll(searches.Select(s => (Task)s.Task).Append(shaders));
            ct.ThrowIfCancellationRequested();

            var lists = new List<List<CardViewModel>>();
            foreach (var (id, task) in searches)
            {
                var page = task.Result;
                var (n, _) = _paging[id];
                _paging[id] = (n + 1, page?.HasMore == true);
                var cards = (page?.Results ?? [])
                    .Select(r => new CardViewModel(r, Context.ApplyAsync) { FitLabel = FitLabel(r.Item, query) })
                    .Where(c => !query.FitOnly || c.FitLabel is "Perfect fit" or "Good fit" || c.FitLabel is null)
                    .ToList();
                lists.Add(cards);
            }

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
                lists.Add(shaderCards);
            }
            else
            {
                _shaderHasMore = false;
            }

            // Interleave so every source is visible near the top of the grid.
            int max = lists.Count == 0 ? 0 : lists.Max(l => l.Count);
            for (int i = 0; i < max; i++)
                foreach (var list in lists)
                    if (i < list.Count) AddCard(list[i]);
            foreach (var card in shaderCards) _ = LoadShaderDetailsAsync(card, ct);

            if (errors.Count > 0 && Results.Count > 0) Context.Notify("Some sources didn't answer", errors[0], NoticeLevel.Warning);
            if (Results.Count == 0)
                EmptyMessage = errors.Count > 0 ? errors[0] : query.Text.Length == 0 ? "Nothing to show yet." : $"No results for “{query.Text}”. Try another word.";
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoading = false;
            OnPropertyChanged(nameof(HasMore));
        }
    }

    private static string? FitLabel(WallpaperItem item, SourceQuery q)
        => item.Kind is WallpaperKind.Image or WallpaperKind.Video ? Fit.Label(Fit.Rate(item.Width, item.Height, q.CanvasWidth, q.CanvasHeight)) : null;

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
        if (!_shown.Add(card.Item.Id)) return; // Featured picks also turn up in the curated source feeds
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
