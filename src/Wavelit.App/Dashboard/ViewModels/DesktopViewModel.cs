using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavelit.App.Services;
using Wavelit.Core.Desktop;
using Wavelit.Core.Storage;

namespace Wavelit.App.Dashboard.ViewModels;

public sealed record ThemeCard(string Id, string Name, string Blurb, FontFamily Font, FontWeight Weight, string Sample, SolidColorBrush Ink, bool Glow)
{
    /// <summary>Glowing themes glow on their card; the others get a soft shadow.</summary>
    public double GlowRadius => Glow ? 22 : 8;
}
public sealed record FontChoice(string Id, string Name, FontFamily Family);
public sealed record MonitorChoice(string? Id, string Label);
public sealed record Swatch(string Hex, string Name, Brush Brush);

/// <summary>
/// The Desktop page (plan §22): Rainmeter-style widgets on the wallpaper and TranslucentTB-style taskbar options,
/// with a live WYSIWYG editor. Settings are the single source of truth; the editor and the desktop both follow
/// them, so every change shows up on the real desktop as you make it.
/// </summary>
public sealed partial class DesktopViewModel : ObservableObject, IDisposable
{
    private const int MaxUndo = 40;
    private readonly TaskbarService _taskbar;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _sceneTimer;
    private readonly Stack<IReadOnlyList<DesktopWidget>> _undo = new();
    private readonly Dictionary<string, string?> _backgrounds = new();
    private DateTime _lastUndoPush;
    private bool _active;
    private bool _disposed;

    [ObservableProperty] private string? _selectedId;
    [ObservableProperty] private MonitorChoice? _editMonitor;

    public DesktopViewModel(DashboardContext context, TaskbarService taskbar, AppPaths paths)
    {
        Context = context;
        Paths = paths;
        _taskbar = taskbar;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _sceneTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => PushScene(), _dispatcher);
        _sceneTimer.Stop();

        foreach (var (id, name, blurb) in WidgetThemes.All) Themes.Add(MakeThemeCard(id, name, blurb));
        foreach (var (id, (name, _)) in WidgetFonts.All) Fonts.Add(new FontChoice(id, name, new FontFamily(WpfFont(id))));
        foreach (var (hex, name) in new[]
        {
            ("#FFFFFF", "White"), ("#FFE9CC", "Warm"), ("#FFC2D4", "Rose"), ("#DCC8FF", "Lilac"), ("#AEDBFF", "Sky"),
            ("#BDF7DA", "Mint"), ("#FFD27A", "Gold"), ("#FF5AD9", "Neon pink"), ("#6DF3FF", "Neon cyan"), ("#16161D", "Ink"),
        })
            Swatches.Add(new Swatch(hex, name, (Brush)new BrushConverter().ConvertFromString(hex)!));

        RebuildMonitors();
        context.Settings.Changed += OnSettingsChanged;
        context.PropertyChanged += OnContextChanged;
        _taskbar.StatusChanged += OnTaskbarStatus;
    }

    public DashboardContext Context { get; }
    public AppPaths Paths { get; }
    public ObservableCollection<ThemeCard> Themes { get; } = [];
    public ObservableCollection<FontChoice> Fonts { get; } = [];
    public ObservableCollection<Swatch> Swatches { get; } = [];
    public ObservableCollection<MonitorChoice> Monitors { get; } = [];
    public ObservableCollection<MonitorChoice> WidgetMonitors { get; } = [];

    public IReadOnlyList<Choice> Aligns { get; } = [new("left", "Left"), new("center", "Centre"), new("right", "Right")];
    public IReadOnlyList<Choice> Effects { get; } = [new("none", "None"), new("shadow", "Soft shadow"), new("glow", "Glow")];
    public IReadOnlyList<Choice> ClockFormats { get; } =
        [new("24", "24-hour  (21:47)"), new("24s", "24-hour with seconds"), new("12", "12-hour  (9:47)"), new("12s", "12-hour with seconds"), new("stack", "Stacked hours over minutes")];
    public IReadOnlyList<Choice> DateFormats { get; } =
        [new("long", "Monday, 26 October"), new("short", "26 Oct 2026"), new("compact", "Mon 26"), new("iso", "2026-10-26"), new("weekday", "Monday")];
    public IReadOnlyList<Choice> TaskbarAligns { get; } =
        [new(TaskbarSettings.Unchanged, "Leave as it is"), new(TaskbarSettings.Center, "Centred"), new(TaskbarSettings.Left, "Left")];
    public IReadOnlyList<Choice> AddKinds { get; } =
        [new(DesktopWidget.Clock, "Clock"), new(DesktopWidget.Date, "Date"), new(DesktopWidget.Greeting, "Greeting"), new(DesktopWidget.Title, "Title"), new(DesktopWidget.Divider, "Divider line")];

    /// <summary>Raised (UI thread) with the scene to show in the editor (see Web/desk.js).</summary>
    public event EventHandler<object>? SceneChanged;

    private DesktopSettings D => Context.Settings.Current.Desktop;
    private TaskbarSettings T => D.Taskbar ?? new();

    // ---------------------------------------------------------------- page

    public void Activate()
    {
        _active = true;
        RebuildMonitors();
        _backgrounds.Clear();
        _ = RefreshBackgroundAsync();
        RequestScene();
    }

    public void Deactivate() => _active = false;

    public bool WidgetsOn
    {
        get => D.WidgetsOn;
        set => Update(d => d with { WidgetsOn = value }, undoable: false);
    }

    public string CurrentTheme => D.Theme;

    [RelayCommand]
    private void ApplyTheme(ThemeCard? card)
    {
        if (card is null) return;
        PushUndo(force: true);
        // A theme keeps your greeting's name and title text, and turns widgets on (that's why you clicked it).
        var keep = D.Items.Where(w => w.Text is { Length: > 0 }).GroupBy(w => w.Kind).ToDictionary(g => g.Key, g => g.First().Text);
        var widgets = WidgetThemes.Build(card.Id)
            .Select(w => keep.TryGetValue(w.Kind, out var text) && w.Kind is DesktopWidget.Greeting ? w with { Text = text } : w)
            .Select(w => w with { MonitorId = EditMonitor?.Id })
            .ToList();
        Update(d => d with { Theme = card.Id, Widgets = widgets, WidgetsOn = true }, undoable: false);
        SelectedId = null;
    }

    [RelayCommand]
    private void AddWidget(Choice? kind)
    {
        if (kind is null || D.Items.Count >= DesktopSettings.MaxWidgets) return;
        var sample = D.Items.FirstOrDefault(w => w.Kind != DesktopWidget.Divider);
        string id = kind.Value + "-" + Guid.NewGuid().ToString("N")[..6];
        var w = new DesktopWidget(id, kind.Value, EditMonitor?.Id, 0.5, 0.62,
            Font: sample?.Font ?? "segoe", Color: sample?.Color ?? "#FFFFFF", Effect: sample?.Effect ?? "shadow",
            Size: kind.Value switch { DesktopWidget.Clock => 9, DesktopWidget.Divider => 8, DesktopWidget.Title => 4, _ => 2.2 },
            Weight: kind.Value == DesktopWidget.Clock ? 200 : 400,
            Text: kind.Value == DesktopWidget.Title ? "Your title" : null);
        Update(d => d with { Theme = "custom", Widgets = [.. d.Items, w], WidgetsOn = true });
        SelectedId = id;
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedId is not { } id) return;
        Update(d => d with { Theme = "custom", Widgets = d.Items.Where(w => w.Id != id).ToList() });
        SelectedId = null;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (!_undo.TryPop(out var previous)) return;
        Context.Settings.Update(s => s with { Desktop = s.Desktop with { Widgets = previous, Theme = "custom" } });
        UndoCommand.NotifyCanExecuteChanged();
    }

    private bool CanUndo() => _undo.Count > 0;

    // ---------------------------------------------------------------- selected widget

    private DesktopWidget? Sel => SelectedId is null ? null : D.Items.FirstOrDefault(w => w.Id == SelectedId);

    public bool HasSelection => Sel is not null;
    public bool SelIsText => Sel is { Kind: not DesktopWidget.Divider };
    public bool SelHasText => Sel is { Kind: DesktopWidget.Title or DesktopWidget.Greeting };
    public bool SelIsClock => Sel?.Kind == DesktopWidget.Clock;
    public bool SelIsDate => Sel?.Kind == DesktopWidget.Date;
    public string SelTitle => Sel?.Kind switch
    {
        DesktopWidget.Clock => "Clock",
        DesktopWidget.Date => "Date",
        DesktopWidget.Greeting => "Greeting",
        DesktopWidget.Title => "Title",
        DesktopWidget.Divider => "Divider line",
        _ => "Nothing selected",
    };
    public string SelTextLabel => Sel?.Kind == DesktopWidget.Greeting ? "Your name (optional)" : "Text";

    public string SelText { get => Sel?.Text ?? ""; set => Edit(w => w with { Text = string.IsNullOrWhiteSpace(value) ? null : value }); }
    public FontChoice? SelFont { get => Fonts.FirstOrDefault(f => f.Id == Sel?.Font); set { if (value is not null) Edit(w => w with { Font = value.Id }); } }
    public double SelWeight { get => Sel?.Weight ?? 300; set => Edit(w => w with { Weight = (int)Math.Round(value / 100) * 100 }); }
    public double SelSize { get => Sel?.Size ?? 6; set => Edit(w => w with { Size = Math.Round(value, 1) }); }
    public double SelOpacity { get => (Sel?.Opacity ?? 0.9) * 100; set => Edit(w => w with { Opacity = value / 100 }); }
    public double SelSpacing { get => (Sel?.Spacing ?? 0) * 100; set => Edit(w => w with { Spacing = Math.Round(value) / 100 }); }
    public bool SelUpper { get => Sel?.Upper ?? false; set => Edit(w => w with { Upper = value }); }
    public bool SelItalic { get => Sel?.Italic ?? false; set => Edit(w => w with { Italic = value }); }
    public Choice? SelAlign { get => Aligns.FirstOrDefault(a => a.Value == Sel?.Align); set { if (value is not null) Edit(w => w with { Align = value.Value }); } }
    public Choice? SelEffect { get => Effects.FirstOrDefault(a => a.Value == Sel?.Effect); set { if (value is not null) Edit(w => w with { Effect = value.Value }); } }
    public Choice? SelClockFormat { get => ClockFormats.FirstOrDefault(a => a.Value == Sel?.Format); set { if (value is not null) Edit(w => w with { Format = value.Value }); } }
    public Choice? SelDateFormat { get => DateFormats.FirstOrDefault(a => a.Value == Sel?.Format); set { if (value is not null) Edit(w => w with { Format = value.Value }); } }
    public string SelColor { get => Sel?.Color ?? "#FFFFFF"; set { if (value is { Length: 7 } && value.StartsWith('#')) Edit(w => w with { Color = value }); } }
    public MonitorChoice? SelMonitor
    {
        get => WidgetMonitors.FirstOrDefault(m => m.Id == Sel?.MonitorId) ?? WidgetMonitors.FirstOrDefault();
        set
        {
            if (value is null || Sel is null || Sel.MonitorId == value.Id) return;
            Edit(w => w with { MonitorId = value.Id });
            // Follow the widget to the display it moved to.
            EditMonitor = Monitors.FirstOrDefault(m => m.Id == (value.Id ?? PrimaryId)) ?? EditMonitor;
        }
    }

    [RelayCommand]
    private void PickColor(Swatch? swatch)
    {
        if (swatch is not null) SelColor = swatch.Hex;
    }

    partial void OnSelectedIdChanged(string? value) => RaiseSelection();

    partial void OnEditMonitorChanged(MonitorChoice? value)
    {
        if (Sel is { } w && !OnMonitor(w, value)) SelectedId = null;
        _ = RefreshBackgroundAsync();
        RequestScene();
    }

    private void RaiseSelection()
    {
        foreach (var name in new[]
        {
            nameof(HasSelection), nameof(SelIsText), nameof(SelHasText), nameof(SelIsClock), nameof(SelIsDate), nameof(SelTitle),
            nameof(SelTextLabel), nameof(SelText), nameof(SelFont), nameof(SelWeight), nameof(SelSize), nameof(SelOpacity),
            nameof(SelSpacing), nameof(SelUpper), nameof(SelItalic), nameof(SelAlign), nameof(SelEffect), nameof(SelClockFormat),
            nameof(SelDateFormat), nameof(SelColor), nameof(SelMonitor),
        })
            OnPropertyChanged(name);
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    // Called by the editor (Web/desk.js) through the view.
    public void OnEditorSelected(string? id) => SelectedId = id;
    public void OnEditorMoved(string id, double x, double y) => EditById(id, w => w with { X = x, Y = y }, coalesce: false);
    public void OnEditorResized(string id, double size) => EditById(id, w => w with { Size = size }, coalesce: true);
    public void OnEditorDelete(string id)
    {
        SelectedId = id;
        DeleteSelected();
    }

    private void Edit(Func<DesktopWidget, DesktopWidget> change)
    {
        if (SelectedId is { } id) EditById(id, change, coalesce: true);
    }

    private void EditById(string id, Func<DesktopWidget, DesktopWidget> change, bool coalesce)
    {
        if (!D.Items.Any(w => w.Id == id)) return;
        PushUndo(force: !coalesce);
        Update(d => d with { Theme = "custom", Widgets = d.Items.Select(w => w.Id == id ? change(w) : w).ToList() }, undoable: false);
    }

    private void Update(Func<DesktopSettings, DesktopSettings> change, bool undoable = true)
    {
        if (undoable) PushUndo(force: true);
        Context.Settings.Update(s => s with { Desktop = change(s.Desktop) });
    }

    // Slider drags arrive as many small edits; one undo step covers a burst.
    private void PushUndo(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastUndoPush < TimeSpan.FromMilliseconds(800)) { _lastUndoPush = now; return; }
        _lastUndoPush = now;
        _undo.Push(D.Items);
        if (_undo.Count > MaxUndo)
        {
            var keep = _undo.Take(MaxUndo).Reverse().ToList();
            _undo.Clear();
            foreach (var k in keep) _undo.Push(k);
        }
        UndoCommand.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------- taskbar

    public Choice? TaskbarAlign
    {
        get => TaskbarAligns.FirstOrDefault(a => a.Value == T.Align);
        set { if (value is not null) Context.Settings.Update(s => s with { Desktop = s.Desktop with { Taskbar = T with { Align = value.Value } } }); }
    }

    public bool TaskbarAutoHide
    {
        get => T.AutoHide;
        set => Context.Settings.Update(s => s with { Desktop = s.Desktop with { Taskbar = T with { AutoHide = value } } });
    }

    public bool TaskbarKeep
    {
        get => T.KeepAfterQuit;
        set => Context.Settings.Update(s => s with { Desktop = s.Desktop with { Taskbar = T with { KeepAfterQuit = value } } });
    }

    public string? TaskbarProblem => _taskbar.Problem;
    public bool HasTaskbarProblem => _taskbar.Problem is not null;

    private void OnTaskbarStatus(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        OnPropertyChanged(nameof(TaskbarProblem));
        OnPropertyChanged(nameof(HasTaskbarProblem));
    });

    // ---------------------------------------------------------------- scene

    private string? PrimaryId => Context.Snapshot.Monitors.FirstOrDefault(m => m.IsPrimary)?.Id;

    private bool OnMonitor(DesktopWidget w, MonitorChoice? monitor)
    {
        var m = Context.Snapshot.Monitors.FirstOrDefault(x => x.Id == monitor?.Id);
        return m is not null && D.On(m, Context.Snapshot.Monitors.Select(x => x.Id).ToList()).Contains(w);
    }

    private void RebuildMonitors()
    {
        var monitors = Context.Snapshot.Monitors;
        string? keep = EditMonitor?.Id;
        Monitors.Clear();
        WidgetMonitors.Clear();
        WidgetMonitors.Add(new MonitorChoice(null, "Main display"));
        foreach (var m in monitors)
        {
            string label = $"{m.DisplayName} · {m.Resolution}";
            Monitors.Add(new MonitorChoice(m.Id, label));
            WidgetMonitors.Add(new MonitorChoice(m.Id, label));
        }
        EditMonitor = Monitors.FirstOrDefault(m => m.Id == keep) ?? Monitors.FirstOrDefault(m => m.Id == PrimaryId) ?? Monitors.FirstOrDefault();
    }

    private void RequestScene()
    {
        if (!_active || _disposed) return;
        _sceneTimer.Stop();
        _sceneTimer.Start();
    }

    private void PushScene()
    {
        _sceneTimer.Stop();
        var monitor = Context.Snapshot.Monitors.FirstOrDefault(m => m.Id == EditMonitor?.Id);
        if (monitor is null || _disposed) return;
        var ids = Context.Snapshot.Monitors.Select(m => m.Id).ToList();
        var widgets = D.On(monitor, ids).Select(w => new
        {
            id = w.Id, kind = w.Kind, x = w.X, y = w.Y, align = w.Align, font = w.Font, weight = w.Weight, size = w.Size,
            color = w.Color, opacity = w.Opacity, spacing = w.Spacing, upper = w.Upper, italic = w.Italic,
            effect = w.Effect, format = w.Format, text = w.Text,
        }).ToList();
        _backgrounds.TryGetValue(monitor.Id, out var background);
        SceneChanged?.Invoke(this, new
        {
            type = "scene",
            monitor = new { width = monitor.Bounds.Width, height = monitor.Bounds.Height },
            widgets,
            selected = SelectedId,
            background,
            reserved = new { bottom = Math.Max(0, monitor.Bounds.Bottom - monitor.WorkArea.Bottom) },
            taskbar = new { autoHide = T.AutoHide, align = T.Align },
        });
    }

    /// <summary>A frame of what the monitor shows now, or its wallpaper's thumbnail when it's covered.</summary>
    private async Task RefreshBackgroundAsync()
    {
        if (!_active || EditMonitor?.Id is not { } id) return;
        string? url = null;
        try
        {
            BitmapSource? image = await Context.Wallpapers.CaptureMonitorAsync(id);
            if (image is null)
            {
                string? itemId = Context.Snapshot.Hosts.FirstOrDefault(h => h.MonitorIds.Contains(id))?.ItemId;
                if (Context.Library.Find(itemId) is { } item)
                    image = await Context.Thumbnails.LoadAsync(item, CancellationToken.None) as BitmapSource;
            }
            if (image is not null) url = ToDataUrl(image);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            Serilog.Log.Debug(ex, "Desktop editor: background capture failed");
        }
        if (_disposed) return;
        _backgrounds[id] = url;
        RequestScene();
    }

    private static string ToDataUrl(BitmapSource image)
    {
        // Keep the message small: the editor shows it at a few hundred pixels.
        double scale = Math.Min(1, 1600.0 / Math.Max(1, image.PixelWidth));
        BitmapSource src = scale < 1 ? new TransformedBitmap(image, new ScaleTransform(scale, scale)) : image;
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
    }

    private void OnSettingsChanged(object? sender, AppSettings e) => _dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(WidgetsOn));
        OnPropertyChanged(nameof(CurrentTheme));
        OnPropertyChanged(nameof(TaskbarAlign));
        OnPropertyChanged(nameof(TaskbarAutoHide));
        OnPropertyChanged(nameof(TaskbarKeep));
        if (SelectedId is not null && Sel is null) SelectedId = null;
        else RaiseSelection();
        RequestScene();
    });

    private void OnContextChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DashboardContext.Snapshot) || _disposed) return;
        bool monitorsChanged = !Context.Snapshot.Monitors.Select(m => m.Id).SequenceEqual(Monitors.Select(m => m.Id));
        if (monitorsChanged) RebuildMonitors();
        RequestScene();
    }

    // ---------------------------------------------------------------- theme cards

    private static string WpfFont(string id) => id switch
    {
        "segoe" => "Segoe UI Variable Display, Segoe UI",
        "bahnschrift" => "Bahnschrift",
        "sitka" => "Sitka Text, Georgia",
        "georgia" => "Georgia",
        "constantia" => "Constantia",
        "gabriola" => "Gabriola",
        "ink" => "Ink Free",
        "mono" => "Consolas",
        _ => "Segoe UI",
    };

    private static ThemeCard MakeThemeCard(string id, string name, string blurb)
    {
        var clock = WidgetThemes.Build(id).First(w => w.Kind == DesktopWidget.Clock);
        string sample = clock.Format switch { "stack" => "21\n47", "24s" => "21:47:08", "12" or "12s" => "9:47", _ => "21:47" };
        var ink = (SolidColorBrush)new BrushConverter().ConvertFromString(clock.Color)!;
        ink.Freeze();
        return new ThemeCard(id, name, blurb, new FontFamily(WpfFont(clock.Font)), FontWeight.FromOpenTypeWeight(clock.Weight), sample, ink, clock.Effect == "glow");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sceneTimer.Stop();
        Context.Settings.Changed -= OnSettingsChanged;
        Context.PropertyChanged -= OnContextChanged;
        _taskbar.StatusChanged -= OnTaskbarStatus;
    }
}
