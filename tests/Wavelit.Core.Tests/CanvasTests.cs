using Wavelit.Core.Desktop;
using Wavelit.Core.Interop;

namespace Wavelit.Core.Tests;

public class CanvasLayoutTests
{
    // The dev PC: landscape primary plus a portrait display to its right, raised by 638 px.
    private static readonly MonitorInfo Primary = new("P", @"\.\DISPLAY1", 0, new RECT(0, 0, 1920, 1080), new RECT(0, 0, 1920, 1040), true);
    private static readonly MonitorInfo Portrait = new("V", @"\.\DISPLAY2", 1, new RECT(1920, -638, 3000, 1282), new RECT(1920, -638, 3000, 1242), false);

    [Fact]
    public void Canvas_covers_every_display_with_negative_coordinates()
    {
        var canvas = CanvasLayout.Extend([Primary, Portrait]);
        Assert.Equal(new RECT(0, -638, 3000, 1282), canvas.Bounds);
        Assert.Equal(3000, canvas.Whole.CanvasWidth);
        Assert.Equal(1920, canvas.Whole.CanvasHeight);
    }

    [Fact]
    public void Each_slice_is_its_display_relative_to_the_canvas()
    {
        var canvas = CanvasLayout.Extend([Primary, Portrait]);
        Assert.Equal(new RECT(0, 638, 1920, 1718), canvas.Slices["P"].Viewport);
        Assert.Equal(new RECT(1920, 0, 3000, 1920), canvas.Slices["V"].Viewport);
    }

    [Fact]
    public void Slices_tile_without_gaps_or_overlap_along_the_seam()
    {
        var canvas = CanvasLayout.Extend([Primary, Portrait]);
        Assert.Equal(canvas.Slices["P"].Viewport.Right, canvas.Slices["V"].Viewport.Left);
    }

    [Fact]
    public void Focus_is_the_primary_display_centre_and_scale_is_its_short_side()
    {
        var view = CanvasLayout.Extend([Portrait, Primary]).Slices["V"];
        Assert.Equal(960, view.FocusX);
        Assert.Equal(638 + 540, view.FocusY);
        Assert.Equal(1080, view.FocusSize);
    }

    [Fact]
    public void Without_a_primary_the_largest_display_gets_the_focus()
    {
        var small = Primary with { IsPrimary = false, Bounds = new RECT(0, 0, 1280, 720) };
        var big = Portrait with { IsPrimary = false };
        var view = CanvasLayout.Extend([small, big]).Whole;
        Assert.Equal(1920 + 540, view.FocusX);
    }

    [Fact]
    public void Single_view_is_centred_on_itself()
    {
        var view = CanvasView.Single(1080, 1920);
        Assert.Equal(new RECT(0, 0, 1080, 1920), view.Viewport);
        Assert.Equal(540, view.FocusX);
        Assert.Equal(960, view.FocusY);
        Assert.Equal(1080, view.FocusSize);
    }

    // Real sizes from the dev PC: the primary is 480 mm wide, the portrait screen 531 mm tall over 1920 px.
    private static readonly MonitorInfo PrimaryMm = Primary with { WidthMm = 480, HeightMm = 270 };
    private static readonly MonitorInfo PortraitMm = Portrait with { WidthMm = 299, HeightMm = 531 };

    [Fact]
    public void Physical_size_matching_scales_the_larger_pixels_around_the_shared_edge()
    {
        var canvas = CanvasLayout.Extend([PrimaryMm, PortraitMm], new CanvasOptions(MatchPhysicalSize: true));
        var p = canvas.Slices["P"].Viewport;
        var v = canvas.Slices["V"].Viewport;
        Assert.Equal(1920, p.Width);                        // the primary is the reference
        Assert.InRange(v.Height / 1920.0, 1.09, 1.12);      // 531/1920 ÷ 480/1920 ≈ 1.106
        Assert.Equal(p.Right, v.Left);                      // still touching
        // The primary's centre line meets the portrait screen at the same height as before scaling.
        double primaryMid = (p.Top + p.Bottom) / 2.0;
        double before = 540 - -638;                          // row of the portrait screen on that line, unscaled
        Assert.InRange((primaryMid - v.Top) / (v.Height / 1920.0), before - 1, before + 1);
    }

    [Fact]
    public void Bezel_gap_moves_the_other_screen_away_by_the_gap()
    {
        var canvas = CanvasLayout.Extend([PrimaryMm, PortraitMm], new CanvasOptions(BezelGapMm: 20));
        int gapPx = canvas.Slices["V"].Viewport.Left - canvas.Slices["P"].Viewport.Right;
        Assert.InRange(gapPx, 79, 81); // 20 mm at 0.25 mm per pixel
    }

    [Fact]
    public void Unknown_sizes_leave_the_layout_alone()
    {
        var plain = CanvasLayout.Extend([Primary, Portrait]);
        var matched = CanvasLayout.Extend([Primary, Portrait], new CanvasOptions(MatchPhysicalSize: true));
        Assert.Equal(plain.Slices["V"].Viewport, matched.Slices["V"].Viewport);
    }
}
