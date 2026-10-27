using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Wavelit.Core.Desktop;

/// <summary>
/// One desktop widget (plan §22.2): a clock, date, title, greeting or divider drawn on the wallpaper under the
/// icons, Rainmeter-style. Positions are fractions of its monitor, so they survive resolution changes.
/// </summary>
/// <param name="Id">Stable id (the editor selects by it).</param>
/// <param name="Kind">One of <see cref="DesktopWidget.Kinds"/>.</param>
/// <param name="MonitorId">The monitor it sits on; null = the primary display.</param>
/// <param name="X">Anchor, 0 … 1 across the monitor (where <see cref="Align"/> puts the text).</param>
/// <param name="Y">Anchor, 0 … 1 down the monitor (the widget's vertical middle).</param>
/// <param name="Align">left / center / right.</param>
/// <param name="Font">One of <see cref="WidgetFonts.All"/>.</param>
/// <param name="Weight">100 … 900 (variable fonts follow it exactly).</param>
/// <param name="Size">Text height as % of the monitor's short side (0.6 … 40).</param>
/// <param name="Color">#RRGGBB.</param>
/// <param name="Opacity">0.1 … 1.</param>
/// <param name="Spacing">Letter spacing in em (−0.1 … 1).</param>
/// <param name="Upper">Upper case.</param>
/// <param name="Italic">Italic.</param>
/// <param name="Effect">none / shadow (soft, keeps text readable on any wallpaper) / glow.</param>
/// <param name="Format">Clock: 24, 24s, 12, 12s, stack. Date: long, short, compact, iso, weekday.</param>
/// <param name="Text">The title's text, or the name a greeting addresses.</param>
public sealed partial record DesktopWidget(
    string Id,
    string Kind,
    string? MonitorId = null,
    double X = 0.5,
    double Y = 0.4,
    string Align = "center",
    string Font = "segoe",
    int Weight = 300,
    double Size = 6,
    string Color = "#FFFFFF",
    double Opacity = 0.92,
    double Spacing = 0,
    bool Upper = false,
    bool Italic = false,
    string Effect = "shadow",
    string Format = "",
    string? Text = null)
{
    public const string Clock = "clock", Date = "date", Title = "title", Greeting = "greeting", Divider = "divider";
    public static readonly IReadOnlyList<string> Kinds = [Clock, Date, Title, Greeting, Divider];
    public static readonly IReadOnlyList<string> Aligns = ["left", "center", "right"];
    public static readonly IReadOnlyList<string> Effects = ["none", "shadow", "glow"];
    public static readonly IReadOnlyList<string> ClockFormats = ["24", "24s", "12", "12s", "stack"];
    public static readonly IReadOnlyList<string> DateFormats = ["long", "short", "compact", "iso", "weekday"];
    public const int MaxText = 80;

    [JsonIgnore] public bool ShowsSeconds => Kind == Clock && Format.EndsWith('s');

    public DesktopWidget Normalize()
    {
        string kind = Kinds.Contains(Kind) ? Kind : Title;
        string format = kind switch
        {
            Clock => ClockFormats.Contains(Format) ? Format : "24",
            Date => DateFormats.Contains(Format) ? Format : "long",
            _ => "",
        };
        string? text = Text is null ? null : Text.Length > MaxText ? Text[..MaxText] : Text;
        return this with
        {
            Id = IdPattern().IsMatch(Id ?? "") ? Id! : "w" + Guid.NewGuid().ToString("N")[..8],
            Kind = kind,
            X = Unit(X, 0.5),
            Y = Unit(Y, 0.4),
            Align = Aligns.Contains(Align) ? Align : "center",
            Font = WidgetFonts.All.ContainsKey(Font ?? "") ? Font! : "segoe",
            Weight = Math.Clamp(Weight / 100 * 100, 100, 900),
            Size = double.IsFinite(Size) ? Math.Clamp(Size, 0.6, 40) : 6,
            Color = Color is not null && HexColor().IsMatch(Color) ? Color.ToUpperInvariant() : "#FFFFFF",
            Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.1, 1) : 0.92,
            Spacing = double.IsFinite(Spacing) ? Math.Clamp(Spacing, -0.1, 1) : 0,
            Effect = Effects.Contains(Effect) ? Effect : "shadow",
            Format = format,
            Text = text,
        };
    }

    private static double Unit(double v, double fallback) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : fallback;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,24}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}

/// <summary>Widget fonts: Windows 11's own fonts only, so nothing is downloaded and licences stay clean.</summary>
public static class WidgetFonts
{
    /// <summary>id → (display name, CSS font stack). Must match FONTS in Web/widgets.js (a test checks).</summary>
    public static readonly IReadOnlyDictionary<string, (string Name, string Css)> All = new Dictionary<string, (string, string)>
    {
        ["segoe"] = ("Segoe UI Variable", "'Segoe UI Variable Display', 'Segoe UI Variable', 'Segoe UI', sans-serif"),
        ["bahnschrift"] = ("Bahnschrift", "Bahnschrift, 'Segoe UI', sans-serif"),
        ["sitka"] = ("Sitka", "'Sitka Display', 'Sitka Text', Georgia, serif"),
        ["georgia"] = ("Georgia", "Georgia, 'Sitka Text', serif"),
        ["constantia"] = ("Constantia", "Constantia, Georgia, serif"),
        ["gabriola"] = ("Gabriola", "Gabriola, 'Segoe Script', cursive"),
        ["ink"] = ("Ink Free", "'Ink Free', 'Segoe Print', cursive"),
        ["mono"] = ("Consolas", "Consolas, 'Cascadia Mono', monospace"),
    };
}

/// <summary>
/// The Desktop page (plan §22): widgets on the wallpaper and the taskbar. Everything is optional and off until
/// the user turns it on.
/// </summary>
/// <param name="WidgetsOn">Show the widgets on the desktop.</param>
/// <param name="Theme">The theme the widgets were last reset to (the editor highlights it).</param>
/// <param name="Taskbar">Taskbar preferences.</param>
public sealed record DesktopSettings(bool WidgetsOn = false, string Theme = "halo", IReadOnlyList<DesktopWidget>? Widgets = null,
    TaskbarSettings? Taskbar = null)
{
    public const int MaxWidgets = 12;

    public IReadOnlyList<DesktopWidget> Items => Widgets ?? WidgetThemes.Build("halo");

    public DesktopSettings Normalize()
    {
        var items = (Widgets ?? WidgetThemes.Build(WidgetThemes.Ids.Contains(Theme) ? Theme : "halo"))
            .Where(w => w is not null).Take(MaxWidgets).Select(w => w.Normalize()).ToList();
        // Ids must be unique for the editor to select reliably.
        var seen = new HashSet<string>();
        for (int i = 0; i < items.Count; i++)
            if (!seen.Add(items[i].Id)) items[i] = items[i] with { Id = "w" + Guid.NewGuid().ToString("N")[..8] };
        return this with
        {
            Theme = WidgetThemes.Ids.Contains(Theme) ? Theme : "custom",
            Widgets = items,
            Taskbar = (Taskbar ?? new()).Normalize(),
        };
    }

    /// <summary>Widgets on <paramref name="monitor"/> (widgets with no monitor, or a missing one, go on the primary).</summary>
    public IEnumerable<DesktopWidget> On(MonitorInfo monitor, IReadOnlyCollection<string> monitorIds)
        => Items.Where(w => w.MonitorId == monitor.Id || monitor.IsPrimary && (w.MonitorId is null || !monitorIds.Contains(w.MonitorId)));
}

/// <summary>
/// Taskbar preferences, applied through the documented per-user settings only (no injection, no admin).
/// Wavelit remembers the user's own values the first time it changes one and puts them back when the feature is
/// turned off or Wavelit quits (unless <see cref="KeepAfterQuit"/>).
/// </summary>
/// <param name="Align">"unchanged", "center" or "left".</param>
/// <param name="AutoHide">Invisible until the pointer touches the screen edge (Windows auto-hide).</param>
/// <param name="SolidWhenMaximized">With a TranslucentTB look: Windows' normal taskbar while a window is maximized.</param>
public sealed record TaskbarSettings(string Align = TaskbarSettings.Unchanged, bool AutoHide = false, bool KeepAfterQuit = false,
    bool SolidWhenMaximized = true)
{
    public const string Unchanged = "unchanged", Center = "center", Left = "left";
    public static readonly IReadOnlyList<string> Aligns = [Unchanged, Center, Left];

    public TaskbarSettings Normalize() => this with { Align = Aligns.Contains(Align) ? Align : Unchanged };

    [JsonIgnore] public bool ChangesAnything => Align != Unchanged || AutoHide;
}

/// <summary>One-click widget themes; the user tweaks from there.</summary>
public static class WidgetThemes
{
    public static readonly IReadOnlyList<(string Id, string Name, string Blurb)> All =
    [
        ("halo", "Halo", "A huge, thin clock with a letter-spaced date and a greeting."),
        ("editorial", "Editorial", "A serif greeting, small-caps date and a quiet clock, magazine-style."),
        ("stack", "Stack", "Hours over minutes, bold and tall, on the right."),
        ("neon", "Neon", "A glowing magenta clock with a cyan date."),
        ("mono", "Terminal", "Consolas, bottom-left, with seconds."),
        ("minimal", "Corner", "A small time and date in the top-right corner."),
    ];

    public static IReadOnlyList<string> Ids { get; } = All.Select(t => t.Id).ToList();

    public static IReadOnlyList<DesktopWidget> Build(string theme) => theme switch
    {
        "editorial" =>
        [
            new("greeting", DesktopWidget.Greeting, X: 0.08, Y: 0.24, Align: "left", Font: "sitka", Weight: 400, Size: 5.2, Italic: true, Opacity: 0.95),
            new("date", DesktopWidget.Date, X: 0.082, Y: 0.315, Align: "left", Font: "sitka", Weight: 500, Size: 1.7, Upper: true, Spacing: 0.32, Format: "long", Opacity: 0.8),
            new("divider", DesktopWidget.Divider, X: 0.082, Y: 0.35, Align: "left", Size: 7, Opacity: 0.5),
            new("clock", DesktopWidget.Clock, X: 0.08, Y: 0.41, Align: "left", Font: "sitka", Weight: 300, Size: 5, Format: "24"),
        ],
        "stack" =>
        [
            new("clock", DesktopWidget.Clock, X: 0.93, Y: 0.4, Align: "right", Font: "bahnschrift", Weight: 700, Size: 17, Format: "stack", Spacing: -0.02),
            new("date", DesktopWidget.Date, X: 0.93, Y: 0.61, Align: "right", Font: "bahnschrift", Weight: 400, Size: 2.2, Upper: true, Spacing: 0.18, Format: "compact", Opacity: 0.85),
        ],
        "neon" =>
        [
            new("clock", DesktopWidget.Clock, X: 0.5, Y: 0.4, Font: "bahnschrift", Weight: 300, Size: 14, Color: "#FF5AD9", Effect: "glow", Format: "24"),
            new("date", DesktopWidget.Date, X: 0.5, Y: 0.54, Font: "bahnschrift", Weight: 400, Size: 2, Color: "#6DF3FF", Effect: "glow", Upper: true, Spacing: 0.4, Format: "long"),
        ],
        "mono" =>
        [
            new("title", DesktopWidget.Title, X: 0.05, Y: 0.79, Align: "left", Font: "mono", Weight: 400, Size: 1.6, Opacity: 0.7, Text: "~ wavelit"),
            new("clock", DesktopWidget.Clock, X: 0.05, Y: 0.84, Align: "left", Font: "mono", Weight: 400, Size: 4.6, Format: "24s"),
            new("date", DesktopWidget.Date, X: 0.05, Y: 0.895, Align: "left", Font: "mono", Weight: 400, Size: 1.6, Format: "iso", Opacity: 0.75),
        ],
        "minimal" =>
        [
            new("clock", DesktopWidget.Clock, X: 0.97, Y: 0.065, Align: "right", Font: "segoe", Weight: 300, Size: 3.4, Format: "24"),
            new("date", DesktopWidget.Date, X: 0.97, Y: 0.11, Align: "right", Font: "segoe", Weight: 400, Size: 1.35, Format: "weekday", Opacity: 0.8),
        ],
        _ =>
        [
            new("greeting", DesktopWidget.Greeting, X: 0.5, Y: 0.25, Font: "segoe", Weight: 300, Size: 2.4, Opacity: 0.85, Spacing: 0.04),
            new("clock", DesktopWidget.Clock, X: 0.5, Y: 0.37, Font: "segoe", Weight: 100, Size: 17, Spacing: -0.01, Format: "24"),
            new("date", DesktopWidget.Date, X: 0.5, Y: 0.495, Font: "segoe", Weight: 400, Size: 1.8, Upper: true, Spacing: 0.42, Format: "long", Opacity: 0.85),
        ],
    };
}
