using System.Text.RegularExpressions;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Tests;

public class OverlayTests
{
    private static string AppDir(params string[] parts)
        => Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Wavelit.App", .. parts]));

    [Fact]
    public void Overlay_ids_match_the_web_overlays_and_have_gallery_thumbnails()
    {
        string js = File.ReadAllText(AppDir("Web", "interact.js"));
        var webStyles = Regex.Matches(js, @"^    (\w+): COMMON \+", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(InteractiveSettings.Styles.Skip(1), webStyles);
        foreach (var style in InteractiveSettings.Styles)
            Assert.True(File.Exists(AppDir("Assets", "Overlays", style + ".jpg")), $"{style}.jpg is missing");
    }

    [Theory]
    [InlineData("stardust", "gentle", true)]
    [InlineData("stardust", "none", false)]     // "Still": no cursor feed at all
    [InlineData("cinematic", "playful", false)] // never uses the cursor
    [InlineData("off", "gentle", false)]
    public void Only_cursor_aware_overlays_turn_on_the_cursor_feed(string style, string response, bool expected)
        => Assert.Equal(expected, new InteractiveSettings(style, Response: response).Normalize().UsesPointer);

    [Fact]
    public void A_wallpaper_can_have_its_own_overlay()
    {
        var global = new InteractiveSettings("stardust", Amount: 0.2);
        Assert.Equal("petals", global.For("petals").Style);
        Assert.Equal(0.2, global.For("petals").Amount); // the fine-tuning stays shared
        Assert.Equal("stardust", global.For(null).Style);
        Assert.Equal("stardust", global.For("lasers").Style);
        Assert.Equal("off", global.For("off").Style);
    }

    [Fact]
    public void Settings_drop_unknown_overlays_and_clamp_controls()
    {
        var s = new AppSettings
        {
            OverlayFor = new() { ["builtin-lava"] = "petals", ["x"] = "lasers" },
            Interactive = new InteractiveSettings("embers", Size: 7, Speed: -1, Response: "wild", Opacity: double.NaN),
        }.Normalize();
        Assert.Equal(["builtin-lava"], s.OverlayFor.Keys);
        Assert.Equal((1.0, 0.0, "gentle", 1.0), (s.Interactive.Size, s.Interactive.Speed, s.Interactive.Response, s.Interactive.Opacity));
    }

    [Fact]
    public void Smooth_motion_is_the_default_and_old_defaults_move_up_once()
    {
        Assert.Equal(60, new AppSettings().Normalize().ShaderFpsCap);
        Assert.Equal(60, new AppSettings { SchemaVersion = 2, ShaderFpsCap = 30 }.Normalize().ShaderFpsCap);
        Assert.Equal(15, new AppSettings { SchemaVersion = 2, ShaderFpsCap = 15 }.Normalize().ShaderFpsCap);   // a choice is kept
        Assert.Equal(30, new AppSettings { SchemaVersion = 3, ShaderFpsCap = 30 }.Normalize().ShaderFpsCap);   // Balanced chosen later
    }
}
