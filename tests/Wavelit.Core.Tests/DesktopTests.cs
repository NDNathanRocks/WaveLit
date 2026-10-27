using System.Text.Json;
using System.Text.RegularExpressions;
using Wavelit.Core.Desktop;
using Wavelit.Core.Interop;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Tests;

public class DesktopTests
{
    private static string AppDir(params string[] parts)
        => Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Wavelit.App", .. parts]));

    private static MonitorInfo Monitor(string id, bool primary, int x = 0, int y = 0, int w = 1920, int h = 1080)
        => new(id, id, 0, new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h }, new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h - 48 }, primary);

    // ---- widgets

    [Fact]
    public void Widget_fonts_match_the_page()
    {
        string js = File.ReadAllText(AppDir("Web", "widgets.js"));
        var block = Regex.Match(js, @"const FONTS = \{(.*?)\};", RegexOptions.Singleline).Groups[1].Value;
        var ids = Regex.Matches(block, @"^\s*(\w+):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(WidgetFonts.All.Keys.ToList(), ids);
        foreach (var (id, (_, css)) in WidgetFonts.All) Assert.Contains(css, js);
    }

    [Fact]
    public void Every_theme_is_valid_and_stays_on_screen()
    {
        foreach (var (id, _, _) in WidgetThemes.All)
        {
            var widgets = WidgetThemes.Build(id);
            Assert.NotEmpty(widgets);
            Assert.Contains(widgets, w => w.Kind == DesktopWidget.Clock);
            Assert.Equal(widgets.Count, widgets.Select(w => w.Id).Distinct().Count());
            foreach (var w in widgets)
            {
                Assert.Equal(w, w.Normalize());                              // themes are already normal
                Assert.InRange(w.X, 0.02, 0.98);
                Assert.InRange(w.Y, 0.02, 0.92);                              // clear of the taskbar
            }
        }
        // The page's test harness renders these.
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "widget-themes.json"),
            JsonSerializer.Serialize(WidgetThemes.All.ToDictionary(t => t.Id, t => WidgetThemes.Build(t.Id)), JsonFile.Options));
    }

    [Fact]
    public void Widgets_normalize_hostile_values()
    {
        var w = new DesktopWidget("bad id!", "script", X: double.NaN, Y: 9, Align: "justify", Font: "Comic Sans", Weight: 1234,
            Size: 500, Color: "red; background:url(x)", Opacity: -1, Spacing: 7, Effect: "blink", Format: "x", Text: new string('a', 500)).Normalize();
        Assert.Matches("^[A-Za-z0-9_-]{1,24}$", w.Id);
        Assert.Equal(DesktopWidget.Title, w.Kind);
        Assert.Equal(0.5, w.X);
        Assert.Equal(1, w.Y);
        Assert.Equal("center", w.Align);
        Assert.Equal("segoe", w.Font);
        Assert.Equal(900, w.Weight);
        Assert.Equal(40, w.Size);
        Assert.Equal("#FFFFFF", w.Color);
        Assert.Equal(0.1, w.Opacity);
        Assert.Equal(1, w.Spacing);
        Assert.Equal("shadow", w.Effect);
        Assert.Equal(DesktopWidget.MaxText, w.Text!.Length);
        Assert.Equal("24", new DesktopWidget("c", DesktopWidget.Clock, Format: "nope").Normalize().Format);
        Assert.Equal("long", new DesktopWidget("d", DesktopWidget.Date).Normalize().Format);
    }

    [Fact]
    public void Desktop_settings_cap_widgets_and_fix_duplicate_ids()
    {
        var many = Enumerable.Range(0, 20).Select(_ => new DesktopWidget("same", DesktopWidget.Clock)).ToList();
        var d = new DesktopSettings(Widgets: many).Normalize();
        Assert.Equal(DesktopSettings.MaxWidgets, d.Items.Count);
        Assert.Equal(d.Items.Count, d.Items.Select(w => w.Id).Distinct().Count());
        Assert.False(new AppSettings().Normalize().Desktop.WidgetsOn);           // off until the user turns it on
        Assert.Equal("halo", new AppSettings().Normalize().Desktop.Theme);
        Assert.Equal("custom", new DesktopSettings(Theme: "nonsense", Widgets: []).Normalize().Theme);
    }

    [Fact]
    public void Widgets_go_on_their_monitor_and_orphans_go_on_the_primary()
    {
        var primary = Monitor("A", true);
        var portrait = Monitor("B", false, 1920, -638, 1080, 1920);
        var d = new DesktopSettings(Widgets:
        [
            new("one", DesktopWidget.Clock),
            new("two", DesktopWidget.Date, MonitorId: "B"),
            new("three", DesktopWidget.Title, MonitorId: "gone"),
        ]).Normalize();
        var ids = new[] { "A", "B" };
        Assert.Equal(["one", "three"], d.On(primary, ids).Select(w => w.Id));
        Assert.Equal(["two"], d.On(portrait, ids).Select(w => w.Id));
    }

    // ---- taskbar

    private sealed class FakeTaskbar(int? alignment, bool autoHide) : ITaskbarBackend
    {
        public int? Alignment { get; set; } = alignment;
        public bool AutoHide { get; set; } = autoHide;
        public int Writes;
        int? ITaskbarBackend.Alignment { get => Alignment; set { Alignment = value; Writes++; } }
        bool ITaskbarBackend.AutoHide { get => AutoHide; set { AutoHide = value; Writes++; } }
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "wavelit-taskbar-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Taskbar_changes_nothing_until_asked()
    {
        var bar = new FakeTaskbar(null, false);
        string path = TempFile();
        new TaskbarController(bar, path).Apply(new TaskbarSettings());
        Assert.Equal(0, bar.Writes);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Taskbar_remembers_the_users_values_and_restores_them()
    {
        var bar = new FakeTaskbar(null, false);   // Windows 11 default: centred, never set
        string path = TempFile();
        var c = new TaskbarController(bar, path);

        c.Apply(new TaskbarSettings(TaskbarSettings.Left, AutoHide: true));
        Assert.Equal(0, bar.Alignment);
        Assert.True(bar.AutoHide);
        Assert.Equal(new TaskbarOriginal(null, false), c.Original);

        // Turning one feature off puts just that one back.
        c.Apply(new TaskbarSettings(TaskbarSettings.Left, AutoHide: false));
        Assert.False(bar.AutoHide);
        Assert.Equal(0, bar.Alignment);

        // Quitting restores everything, including "never set", and forgets the saved values.
        c.Restore();
        Assert.Null(bar.Alignment);
        Assert.False(bar.AutoHide);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Taskbar_survives_a_crash_and_turning_everything_off_restores()
    {
        var bar = new FakeTaskbar(0, true);       // the user had left-aligned and auto-hide
        string path = TempFile();
        new TaskbarController(bar, path).Apply(new TaskbarSettings(TaskbarSettings.Center));
        Assert.Equal(1, bar.Alignment);

        // A new session (after a crash) still knows the user's own values.
        var after = new TaskbarController(bar, path);
        Assert.Equal(new TaskbarOriginal(0, true), after.Original);
        after.Apply(new TaskbarSettings());
        Assert.Equal(0, bar.Alignment);
        Assert.True(bar.AutoHide);                // auto-hide was theirs; Wavelit never turns it off
        Assert.False(File.Exists(path));
    }
}
