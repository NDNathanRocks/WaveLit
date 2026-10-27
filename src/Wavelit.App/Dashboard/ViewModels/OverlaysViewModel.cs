using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavelit.Core.Storage;

namespace Wavelit.App.Dashboard.ViewModels;

/// <summary>One card in the overlay gallery (a built-in style, or one made in the Studio).</summary>
public sealed partial class OverlayCard(string id, string name, string description, string? thumbnailPath = null) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public bool IsCustom => InteractiveSettings.IsCustom(Id);
    public ImageSource? Thumbnail { get; } = LoadThumbnail(id, thumbnailPath);
    [ObservableProperty] private bool _isSelected;

    public override string ToString() => Name; // what screen readers announce

    private static ImageSource? LoadThumbnail(string id, string? path)
    {
        if (InteractiveSettings.IsCustom(id) && (path is null || !System.IO.File.Exists(path))) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = path is not null ? new Uri(path) : new Uri($"pack://application:,,,/Assets/Overlays/{id}.jpg");
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Overlays: a gallery of subtle layers that go over any wallpaper (plan §17.4). "None" is the default.
/// The style can apply to every wallpaper or only to the one showing now; the other controls are shared.
/// Changes apply live, so the desktop itself is the preview.
/// </summary>
public sealed partial class OverlaysViewModel : ObservableObject, IDisposable
{
    private bool _syncing;

    private readonly AppPaths _paths;

    public OverlaysViewModel(DashboardContext context, AppPaths paths)
    {
        _paths = paths;
        Context = context;
        _builtIn =
        [
            new(InteractiveSettings.Off, "None", "Just your wallpaper."),
            new("stardust", "Stardust", "Sparse drifting dust that parts around your cursor."),
            new("fireflies", "Fireflies", "Warm glows that gather and circle the cursor."),
            new("constellation", "Constellation", "Faint stars that link up as you pass."),
            new("ripples", "Ripples", "Soft rings of light follow the cursor."),
            new("petals", "Petals", "Sakura drifting on a breeze; the cursor makes a gust."),
            new("snowfall", "Snowfall", "Quiet snow that eddies around the cursor."),
            new("embers", "Embers", "Rising sparks that swirl as you move."),
            new("bokeh", "Bokeh", "Large soft orbs that drift out of your way."),
            new("halo", "Halo", "A soft lantern light that follows the cursor."),
            new("silk", "Silk trail", "The cursor draws a fading ribbon of light."),
            new("aurora", "Aurora veil", "Faint curtains of light that sway away from the cursor."),
            new("cinematic", "Cinematic", "Film grain, a gentle vignette and a warm light leak."),
            new("resonance", "Resonant Stardust", "Music: dust that buzzes, ripples on the beat and gathers into patterns."),
        ];
        foreach (var c in _builtIn) Cards.Add(c);
        context.Settings.Changed += OnSettingsChanged;
        Sync();
    }

    private readonly IReadOnlyList<OverlayCard> _builtIn;

    public DashboardContext Context { get; }
    /// <summary>The built-in styles, then the user's Studio overlays (newest last).</summary>
    public ObservableCollection<OverlayCard> Cards { get; } = [];

    /// <summary>Keeps the Studio overlays in the gallery in step with settings.</summary>
    private void SyncCustomCards()
    {
        var custom = Context.Settings.Current.CustomOverlays;
        var shown = Cards.Where(c => c.IsCustom).ToList();
        foreach (var card in shown.Where(c => !custom.ContainsKey(c.Id))) Cards.Remove(card);
        foreach (var (id, overlay) in custom)
        {
            if (shown.Any(c => c.Id == id)) continue;
            Cards.Add(new OverlayCard(id, overlay.Title, "Made in Studio" + (overlay.Prompt is { Length: > 0 } p ? ": " + p : "."), overlay.ThumbnailPath));
        }
    }

    [RelayCommand]
    private void RemoveCustom(OverlayCard? card)
    {
        if (card is not { IsCustom: true }) return;
        string? thumb = Context.Settings.Current.CustomOverlays.TryGetValue(card.Id, out var o) ? o.ThumbnailPath : null;
        // Normalize turns any use of a missing overlay off (globally and per wallpaper).
        Context.Settings.Update(s => s with { CustomOverlays = s.CustomOverlays.Where(kv => kv.Key != card.Id).ToDictionary(kv => kv.Key, kv => kv.Value) });
        if (thumb is not null && System.IO.Path.GetFullPath(thumb).StartsWith(_paths.Creations + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            try { System.IO.File.Delete(thumb); } catch (System.IO.IOException) { }
        }
    }

    public static IReadOnlyList<Choice> Colors { get; } =
    [
        new("auto", "Natural"),
        new("#FFFFFF", "White"),
        new("#FFD27A", "Warm gold"),
        new("#9FD8FF", "Ice blue"),
        new("#FF9EC4", "Rose"),
        new("#9DF5C9", "Mint"),
        new("#C7A6FF", "Violet"),
    ];

    public static IReadOnlyList<Choice> Responses { get; } =
    [
        new(InteractiveSettings.None, "Still: ignores the cursor"),
        new(InteractiveSettings.Gentle, "Gentle"),
        new(InteractiveSettings.Playful, "Playful"),
    ];

    [ObservableProperty] private OverlayCard? _selectedCard;
    /// <summary>True: the selected style applies only to the wallpaper showing now.</summary>
    [ObservableProperty] private bool _onlyThisWallpaper;

    private InteractiveSettings S => Context.Settings.Current.Interactive;
    private void Update(Func<InteractiveSettings, InteractiveSettings> change)
        => Context.Settings.Update(s => s with { Interactive = change(s.Interactive).Normalize() });

    /// <summary>The wallpaper shown on every display (Extend / Same everywhere), or the primary display's.</summary>
    private string? CurrentItemId
    {
        get
        {
            var settings = Context.Settings.Current;
            if (settings.Displays.SharedItemId is { } shared && settings.Displays.Layout != Core.Library.DisplayLayout.PerDisplay) return shared;
            var primary = Context.Snapshot.Monitors.FirstOrDefault(m => m.IsPrimary);
            return Context.Snapshot.Hosts.FirstOrDefault(h => primary is not null && h.MonitorIds.Contains(primary.Id))?.ItemId;
        }
    }

    public string CurrentWallpaperName => Context.Library.Find(CurrentItemId)?.Title ?? "the current wallpaper";
    public bool HasOverlay => (SelectedCard?.Id ?? InteractiveSettings.Off) != InteractiveSettings.Off;

    partial void OnSelectedCardChanged(OverlayCard? value)
    {
        foreach (var c in Cards) c.IsSelected = c == value;
        OnPropertyChanged(nameof(HasOverlay));
        if (_syncing || value is null) return;
        Apply(value.Id, OnlyThisWallpaper);
    }

    partial void OnOnlyThisWallpaperChanged(bool value)
    {
        if (_syncing || SelectedCard is null) return;
        Apply(SelectedCard.Id, value);
    }

    private void Apply(string style, bool onlyThis)
    {
        string? itemId = CurrentItemId;
        Context.Settings.Update(s =>
        {
            var map = new Dictionary<string, string>(s.OverlayFor);
            if (onlyThis && itemId is not null)
            {
                map[itemId] = style;
                return s with { OverlayFor = map };
            }
            if (itemId is not null) map.Remove(itemId);
            return s with { OverlayFor = map, Interactive = (s.Interactive with { Style = style }).Normalize() };
        });
    }

    public double Amount { get => Math.Round(S.Amount * 100); set => Update(i => i with { Amount = value / 100.0 }); }
    public double Size { get => Math.Round(S.Size * 100); set => Update(i => i with { Size = value / 100.0 }); }
    public double Speed { get => Math.Round(S.Speed * 100); set => Update(i => i with { Speed = value / 100.0 }); }
    public double Opacity { get => Math.Round(S.Opacity * 100); set => Update(i => i with { Opacity = value / 100.0 }); }

    public Choice SelectedColor
    {
        get => Colors.FirstOrDefault(c => string.Equals(c.Value, S.Color, StringComparison.OrdinalIgnoreCase)) ?? Colors[0];
        set { if (value is not null) Update(i => i with { Color = value.Value }); }
    }

    public Choice SelectedResponse
    {
        get => Responses.FirstOrDefault(c => c.Value == S.Response) ?? Responses[1];
        set { if (value is not null) Update(i => i with { Response = value.Value }); }
    }

    public bool ClickBursts { get => S.ClickBursts; set => Update(i => i with { ClickBursts = value }); }
    /// <summary>The overlay swells gently with the music playing (audio overlays always do).</summary>
    public bool Music { get => S.Music; set => Update(i => i with { Music = value }); }

    private void OnSettingsChanged(object? sender, AppSettings e)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(Sync);

    /// <summary>Re-reads settings (also called when the page is shown, as the wallpaper may have changed).</summary>
    public void Sync()
    {
        _syncing = true;
        try
        {
            SyncCustomCards();
            string? itemId = CurrentItemId;
            var settings = Context.Settings.Current;
            bool own = itemId is not null && settings.OverlayFor.ContainsKey(itemId);
            string style = own ? settings.OverlayFor[itemId!] : settings.Interactive.Style;
            OnlyThisWallpaper = own;
            SelectedCard = Cards.FirstOrDefault(c => c.Id == style) ?? Cards[0];
            foreach (var c in Cards) c.IsSelected = c == SelectedCard;
        }
        finally
        {
            _syncing = false;
        }
        foreach (var name in new[] { nameof(Amount), nameof(Size), nameof(Speed), nameof(Opacity), nameof(SelectedColor),
                     nameof(SelectedResponse), nameof(ClickBursts), nameof(Music), nameof(CurrentWallpaperName), nameof(HasOverlay) })
            OnPropertyChanged(name);
    }

    public void Dispose() => Context.Settings.Changed -= OnSettingsChanged;
}
