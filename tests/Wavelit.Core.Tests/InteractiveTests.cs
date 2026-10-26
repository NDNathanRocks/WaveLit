using Wavelit.Core.Desktop;
using Wavelit.Core.Interop;
using Wavelit.Core.Library.BuiltIn;
using Wavelit.Core.Library.Sources;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Tests;

public class InteractiveTests
{
    // The dev PC: a 1920x1080 primary at (0,0) and a portrait 1080x1920 at (1920,-638).
    private static readonly MonitorInfo Primary = new("A", @"\\.\DISPLAY1", 0, new RECT(0, 0, 1920, 1080), new RECT(0, 0, 1920, 1040), true);
    private static readonly MonitorInfo Portrait = new("B", @"\\.\DISPLAY2", 1, new RECT(1920, -638, 3000, 1282), new RECT(1920, -638, 3000, 1282), false);

    [Theory]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    [InlineData("Shell_TrayWnd", false)]
    [InlineData("Chrome_WidgetWin_1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_bare_desktop_counts(string? rootClass, bool expected)
        => Assert.Equal(expected, PointerMapping.IsDesktopRoot(rootClass));

    [Fact]
    public void Extend_slices_map_the_cursor_onto_one_shared_canvas()
    {
        var canvas = CanvasLayout.Extend([Primary, Portrait]);
        // On the portrait screen, at negative virtual-screen coordinates.
        var p = new POINT { X = 2000, Y = -600 };
        Assert.Null(PointerMapping.ToCanvas(p, Primary.Bounds, canvas.Slices["A"]));
        var onB = PointerMapping.ToCanvas(p, Portrait.Bounds, canvas.Slices["B"]);
        Assert.Equal((2000.0, 38.0), onB); // canvas top is the portrait's top (-638)
        // The primary's centre is the canvas focus.
        var centre = PointerMapping.ToCanvas(new POINT { X = 960, Y = 540 }, Primary.Bounds, canvas.Slices["A"])!.Value;
        Assert.Equal(canvas.Whole.FocusX, centre.X, 3);
        Assert.Equal(canvas.Whole.FocusY, centre.Y, 3);
    }

    [Fact]
    public void Physical_size_matching_scales_the_cursor_with_the_slice()
    {
        var a = Primary with { WidthMm = 480, HeightMm = 270 };
        var b = Portrait with { WidthMm = 299, HeightMm = 531 };
        var canvas = CanvasLayout.Extend([a, b], new CanvasOptions(MatchPhysicalSize: true));
        var slice = canvas.Slices["B"];
        var topLeft = PointerMapping.ToCanvas(new POINT { X = 1920, Y = -638 }, b.Bounds, slice)!.Value;
        var bottomRight = PointerMapping.ToCanvas(new POINT { X = 2999, Y = 1281 }, b.Bounds, slice)!.Value;
        Assert.Equal(slice.Viewport.Left, topLeft.X, 3);
        Assert.Equal(slice.Viewport.Top, topLeft.Y, 3);
        Assert.InRange(bottomRight.X, slice.Viewport.Right - 2, slice.Viewport.Right);
        Assert.InRange(bottomRight.Y, slice.Viewport.Bottom - 2, slice.Viewport.Bottom);
    }

    [Fact]
    public void A_single_window_uses_its_own_pixels()
    {
        var view = CanvasView.Single(1080, 1920);
        Assert.Equal((10.0, 20.0), PointerMapping.ToCanvas(new POINT { X = 1930, Y = -618 }, Portrait.Bounds, view));
    }

    [Fact]
    public void Samples_compare_by_value_so_only_changes_are_sent()
    {
        var a = PointerSample.At((100.04, 200.0), down: false);
        var b = PointerSample.At((100.01, 200.0), down: false);
        Assert.Equal(a, b); // rounded to a tenth: sub-pixel jitter isn't sent
        Assert.NotEqual(a, PointerSample.At((100.04, 200.0), down: true));
        var away = PointerSample.Away(a with { Down = true });
        Assert.False(away.Over);
        Assert.False(away.Down);
        Assert.Equal(a.X, away.X);
    }

    [Fact]
    public void Interactive_settings_are_off_by_default_and_normalized()
    {
        Assert.False(new AppSettings().Normalize().Interactive.IsOn);
        var bad = new InteractiveSettings("lasers", double.NaN, "red", false).Normalize();
        Assert.Equal(InteractiveSettings.Off, bad.Style);
        Assert.Equal(0.35, bad.Amount); // the calmer default
        Assert.Equal("auto", bad.Color);
        var ok = new InteractiveSettings("fireflies", 3, "#A0B0C0").Normalize();
        Assert.Equal(("fireflies", 1.0, "#A0B0C0"), (ok.Style, ok.Amount, ok.Color));
    }

    [Fact]
    public void Built_in_shaders_have_thumbnails_and_valid_moods()
    {
        Assert.Equal(16, BuiltInShaders.Items.Count);
        string assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Wavelit.App", "Assets", "BuiltIn"));
        foreach (var item in BuiltInShaders.Items)
        {
            Assert.True(File.Exists(Path.Combine(assets, item.ShaderId + ".jpg")), $"{item.ShaderId}.jpg is missing");
            Assert.NotNull(Moods.Find(BuiltInShaders.MoodOf(item.Id)));
            Assert.NotNull(BuiltInShaders.Program(item.ShaderId!));
        }
    }
}
