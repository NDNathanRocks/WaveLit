using Wavelit.Core.Library;

namespace Wavelit.Core.Tests;

public class MediaPlacementTests
{
    // Dev PC canvas: 3000 × 1920 with the primary display's centre at (960, 1178).
    private const double Cw = 3000, Ch = 1920, Fx = 960, Fy = 1178;

    [Fact]
    public void Cover_fills_the_canvas_and_keeps_the_subject_on_the_primary_display()
    {
        var (x, y, w, h) = MediaPlacement.Place(3840, 2160, Cw, Ch, Fx, Fy, "cover", null);
        Assert.Equal(1920, h, 3);
        Assert.Equal(3413.333, w, 3);
        // Centring on x = 960 would leave a gap on the right, so it slides as far left as covering allows.
        Assert.Equal(Cw - w, x, 3);
        Assert.Equal(0, y, 3);
    }

    [Fact]
    public void Cover_never_leaves_gaps()
    {
        foreach (var (mw, mh) in new[] { (1920.0, 1080.0), (1080.0, 1920.0), (5120.0, 1440.0), (800.0, 800.0) })
        {
            var (x, y, w, h) = MediaPlacement.Place(mw, mh, Cw, Ch, Fx, Fy, "cover", null);
            Assert.True(x <= 0 && y <= 0 && x + w >= Cw - 0.001 && y + h >= Ch - 0.001, $"{mw}x{mh}");
        }
    }

    [Fact]
    public void Contain_shows_everything_inside_the_canvas()
    {
        var (x, y, w, h) = MediaPlacement.Place(1920, 1080, Cw, Ch, Fx, Fy, "contain", null);
        Assert.Equal(Cw, w, 3);
        Assert.True(y >= 0 && y + h <= Ch);
    }

    [Fact]
    public void Framing_zoom_and_centre_are_applied_but_still_cover()
    {
        var framing = new MediaFraming(2, 0.5, 0.5);
        var (x, y, w, h) = MediaPlacement.Place(3840, 2160, Cw, Ch, Fx, Fy, "cover", framing);
        Assert.Equal(3840.0 * 1920 / 2160 * 2, w, 3);
        Assert.Equal(Cw / 2, x + w / 2, 3);
        Assert.Equal(Ch / 2, y + h / 2, 3);

        var far = MediaPlacement.Place(3840, 2160, Cw, Ch, Fx, Fy, "cover", new MediaFraming(1, 5, 5));
        Assert.Equal(0, far.X, 3); // clamped: the canvas stays covered
    }

    [Fact]
    public void Framing_is_sanitized()
    {
        var f = new MediaFraming(double.NaN, 99, -99).Normalize();
        Assert.Equal(1, f.Zoom);
        Assert.Equal(1.5, f.CenterX);
        Assert.Equal(-0.5, f.CenterY);
        Assert.Equal(MediaFraming.MaxZoom, new MediaFraming(50, 0.5, 0.5).Normalize().Zoom);
    }

    [Fact]
    public void Framing_key_depends_on_canvas_size() =>
        Assert.NotEqual(MediaFraming.Key("a", 3000, 1920), MediaFraming.Key("a", 1920, 1080));
}

public class PastedShaderTests
{
    private const string Valid = "void mainImage(out vec4 c, in vec2 p) { c = vec4(p / iResolution.xy, 0.5, 1.0); }";

    [Fact]
    public void Accepts_a_shadertoy_style_shader()
    {
        Assert.Null(PastedShader.Validate(Valid));
        var item = PastedShader.CreateItem("Gradient", Valid);
        Assert.Equal(WallpaperKind.Shader, item.Kind);
        Assert.Equal(WallpaperSources.Pasted, item.Source);
        Assert.Single(PastedShader.Program(item).Passes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("void main() { }")]
    [InlineData("#version 300 es\nvoid mainImage(out vec4 c, in vec2 p) { }")]
    public void Rejects_unusable_code(string code) => Assert.NotNull(PastedShader.Validate(code));

    [Fact]
    public void Rejects_huge_code() => Assert.NotNull(PastedShader.Validate(Valid + new string(' ', PastedShader.MaxLength)));

    [Fact]
    public void Same_code_gets_the_same_id() =>
        Assert.Equal(PastedShader.CreateItem("a", Valid).Id, PastedShader.CreateItem("b", Valid.Replace("\n", "\r\n")).Id);
}

public class ImageFileTests
{
    [Theory]
    [InlineData(@"C:\p\photo.JPG", true)]
    [InlineData(@"C:\p\art.webp", true)]
    [InlineData(@"C:\p\clip.mp4", false)]
    public void Recognizes_images(string path, bool image) => Assert.Equal(image, LocalFiles.IsImage(path));

    [Fact]
    public void Local_images_become_image_items()
    {
        var item = LocalFiles.CreateItem(@"C:\Wallpapers\mountain.png");
        Assert.Equal(WallpaperKind.Image, item.Kind);
        Assert.True(item.IsLocalFile);
        Assert.False(item.IsCachedMedia);
    }

    [Theory]
    [InlineData("https://w.wallhaven.cc/full/5y/wallhaven-5yp258.jpg", ".jpg")]
    [InlineData("https://cdn.example/x/image.PNG?size=large", ".png")]
    [InlineData("https://image.pollinations.ai/prompt/misty%20lake?width=3000", ".jpg")]
    public void Cache_extension_comes_from_the_url(string url, string ext) => Assert.Equal(ext, LocalFiles.ImageExtensionFor(url));

    [Fact]
    public void Effects_are_normalized()
    {
        Assert.Equal(ImageEffects.Rain, ImageEffects.Normalize("rain"));
        Assert.Equal(ImageEffects.None, ImageEffects.Normalize("<script>"));
        Assert.Equal(ImageEffects.None, ImageEffects.Normalize(null));
    }
}
