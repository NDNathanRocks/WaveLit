using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavelit.App.Wallpaper;
using Wavelit.Core.Desktop;
using Wavelit.Core.Interop;
using Wavelit.Core.Library;
using Wavelit.Core.Storage;

namespace Wavelit.App.Dashboard.ViewModels;

public sealed record MapRect(double Left, double Top, double Width, double Height);

public sealed record EffectOption(string Key, string Label);

/// <summary>
/// Framing editor on the Displays page: the current video or image drawn behind a map of the monitors.
/// Drag to move it, scroll to zoom; the wallpaper follows live. Placement maths is shared with the
/// renderer (<see cref="MediaPlacement"/>), so the preview matches the desktop.
/// </summary>
public sealed partial class FramingEditorViewModel : ObservableObject, IDisposable
{
    public const double MapWidth = 640;
    public const double MapHeight = 240;

    private readonly DashboardContext _context;
    private readonly WallpaperService _wallpapers;
    private readonly DispatcherTimer _saveTimer;
    private bool _updatingEffect;
    private CancellationTokenSource? _thumbCts;
    private RECT _canvas;
    private double _scale, _offsetX, _offsetY, _focusX, _focusY;
    private double _mediaWidth, _mediaHeight;
    private string? _thumbItemId;
    private string? _key;
    private MediaFraming? _framing;

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private ImageSource? _image;
    [ObservableProperty] private MapRect _imageRect = new(0, 0, 0, 0);
    [ObservableProperty] private Geometry? _shade;
    [ObservableProperty] private bool _isCustom;
    [ObservableProperty] private bool _isImage;
    [ObservableProperty] private EffectOption? _selectedEffect;

    public FramingEditorViewModel(DashboardContext context, WallpaperService wallpapers)
    {
        _context = context;
        _wallpapers = wallpapers;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
    }

    public ObservableCollection<MapRect> Screens { get; } = [];

    /// <summary>"Living stills": motion that brings a still image to life.</summary>
    public IReadOnlyList<EffectOption> Effects { get; } =
    [
        new(ImageEffects.None, "None (still)"),
        new(ImageEffects.Drift, "Slow drift"),
        new(ImageEffects.Rain, "Rain"),
        new(ImageEffects.Snow, "Snow"),
        new(ImageEffects.Fireflies, "Fireflies"),
        new(ImageEffects.Dust, "Floating dust"),
    ];

    partial void OnSelectedEffectChanged(EffectOption? value)
    {
        if (_updatingEffect || value is null) return;
        var item = _context.Library.Find(_context.Settings.Current.Displays.SharedItemId);
        if (item is not { Kind: WallpaperKind.Image } || ImageEffects.Normalize(item.Effect) == value.Key) return;
        _context.Library.Upsert(item with { Effect = value.Key });
        _wallpapers.Reload(item.Id);
    }

    /// <summary>Re-reads layout, monitors and the current wallpaper.</summary>
    public void Refresh()
    {
        var settings = _context.Settings.Current;
        var monitors = _context.Snapshot.Monitors;
        var item = _context.Library.Find(settings.Displays.SharedItemId);
        IsImage = item?.Kind == WallpaperKind.Image;
        if (item is { Kind: WallpaperKind.Image })
        {
            _updatingEffect = true;
            SelectedEffect = Effects.First(e => e.Key == ImageEffects.Normalize(item.Effect));
            _updatingEffect = false;
        }

        bool extend = settings.Displays.Layout == DisplayLayout.Span && monitors.Count > 1;
        bool single = monitors.Count == 1;
        if (monitors.Count == 0 || !(extend || single) || item is not { Kind: WallpaperKind.Video or WallpaperKind.Image })
        {
            IsAvailable = false;
            Hint = monitors.Count > 1 && !extend
                ? "Framing works in Extend mode."
                : "Framing works with videos and images. Set one as your wallpaper to frame it.";
            return;
        }

        var canvas = extend ? CanvasLayout.Extend(monitors, settings.Canvas) : null;
        var view = canvas?.Whole ?? CanvasView.Single(monitors[0].Bounds.Width, monitors[0].Bounds.Height);
        _canvas = new RECT(0, 0, view.CanvasWidth, view.CanvasHeight);
        _focusX = view.FocusX;
        _focusY = view.FocusY;
        _scale = Math.Min(MapWidth / _canvas.Width, MapHeight / _canvas.Height);
        _offsetX = (MapWidth - _canvas.Width * _scale) / 2;
        _offsetY = (MapHeight - _canvas.Height * _scale) / 2;

        Screens.Clear();
        var shade = new GeometryGroup { FillRule = FillRule.EvenOdd };
        shade.Children.Add(new RectangleGeometry(new Rect(0, 0, MapWidth, MapHeight)));
        var screens = canvas is null ? [view.Viewport] : monitors.Select(m => canvas.Slices[m.Id].Viewport).ToList();
        foreach (var screen in screens)
        {
            var r = ToMap(screen);
            Screens.Add(r);
            shade.Children.Add(new RectangleGeometry(new Rect(r.Left, r.Top, r.Width, r.Height)));
        }
        shade.Freeze();
        Shade = shade;

        _key = MediaFraming.Key(item.Id, view.CanvasWidth, view.CanvasHeight);
        if (!_saveTimer.IsEnabled) _framing = settings.Framings.GetValueOrDefault(_key);
        IsCustom = _framing is not null;
        _mediaWidth = item.Width ?? 0;
        _mediaHeight = item.Height ?? 0;
        if (_thumbItemId != item.Id) LoadImage(item);
        IsAvailable = _mediaWidth > 0 && _mediaHeight > 0 || Image is BitmapSource;
        Hint = IsAvailable ? "Drag to move · scroll to zoom" : "Waiting for the wallpaper to load…";
        Layout();
    }

    private MapRect ToMap(RECT r) => new(
        _offsetX + (r.Left - _canvas.Left) * _scale,
        _offsetY + (r.Top - _canvas.Top) * _scale,
        r.Width * _scale,
        r.Height * _scale);

    private async void LoadImage(WallpaperItem item)
    {
        _thumbItemId = item.Id;
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _thumbCts = new CancellationTokenSource();
        var token = _thumbCts.Token;
        Image = null;
        try
        {
            // Captured thumbnails show a screen, not the media, so only real media previews are used.
            bool trustworthy = item.Kind == WallpaperKind.Image || item.ThumbnailUrl is not null;
            var image = trustworthy ? await _context.Thumbnails.LoadAsync(item, token) : null;
            if (token.IsCancellationRequested) return;
            Image = image;
            if (image is BitmapSource b && (_mediaWidth <= 0 || _mediaHeight <= 0))
            {
                _mediaWidth = b.PixelWidth;
                _mediaHeight = b.PixelHeight;
            }
            Refresh();
        }
        catch (OperationCanceledException) { }
    }

    private string Fit => _context.Settings.Current.VideoScaling switch
    {
        VideoScaling.Fit => "contain",
        VideoScaling.Stretch => "fill",
        _ => "cover",
    };

    private (double X, double Y, double W, double H) Placement()
        => MediaPlacement.Place(_mediaWidth, _mediaHeight, _canvas.Width, _canvas.Height, _focusX, _focusY, Fit, _framing);

    private void Layout()
    {
        if (!IsAvailable) return;
        var (x, y, w, h) = Placement();
        ImageRect = new MapRect(_offsetX + x * _scale, _offsetY + y * _scale, w * _scale, h * _scale);
    }

    /// <summary>Moves the media by a drag distance in map pixels.</summary>
    public void PanBy(double dx, double dy)
    {
        if (!IsAvailable || _canvas.Width <= 0) return;
        var f = Current();
        Apply(f with
        {
            CenterX = f.CenterX + dx / (_canvas.Width * _scale),
            CenterY = f.CenterY + dy / (_canvas.Height * _scale),
        });
    }

    /// <summary>Zooms by a mouse-wheel delta (120 per notch).</summary>
    public void ZoomBy(int wheelDelta)
    {
        if (!IsAvailable) return;
        var f = Current();
        Apply(f with { Zoom = f.Zoom * Math.Pow(1.1, wheelDelta / 120.0) });
    }

    [RelayCommand]
    private void Reset()
    {
        _framing = null;
        IsCustom = false;
        Layout();
        _saveTimer.Stop();
        Save();
    }

    // The automatic placement expressed as an explicit framing, so the first drag starts from what's on screen.
    private MediaFraming Current()
    {
        if (_framing is not null) return _framing;
        var (x, y, w, h) = Placement();
        return new MediaFraming(1, (x + w / 2) / _canvas.Width, (y + h / 2) / _canvas.Height);
    }

    private void Apply(MediaFraming framing)
    {
        framing = framing.Normalize();
        // Keep the centre where it still has a visible effect, so dragging back responds immediately.
        _framing = framing;
        var (_, _, w, h) = Placement();
        double cw = _canvas.Width, ch = _canvas.Height;
        _framing = framing with
        {
            CenterX = Math.Clamp(framing.CenterX, Math.Min(w / 2, cw - w / 2) / cw, Math.Max(w / 2, cw - w / 2) / cw),
            CenterY = Math.Clamp(framing.CenterY, Math.Min(h / 2, ch - h / 2) / ch, Math.Max(h / 2, ch - h / 2) / ch),
        };
        IsCustom = true;
        Layout();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save()
    {
        if (_key is null) return;
        string key = _key;
        var framing = _framing;
        _context.Settings.Update(s =>
        {
            var map = new Dictionary<string, MediaFraming>(s.Framings);
            if (framing is null) map.Remove(key);
            else map[key] = framing;
            return s with { Framings = map };
        });
    }

    public void Dispose()
    {
        _saveTimer.Stop();
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
    }
}
