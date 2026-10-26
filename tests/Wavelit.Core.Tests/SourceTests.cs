using System.Text.Json;
using Wavelit.Core.Library;
using Wavelit.Core.Library.Sources;

namespace Wavelit.Core.Tests;

public class SourceParserTests
{
    private static JsonElement Sample(string name)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", name))).RootElement;

    [Fact]
    public void Wallhaven_results_are_https_images_with_sizes()
    {
        var page = WallhavenSource.Parse(Sample("wallhaven.json"), "mountains");
        Assert.True(page.HasMore);
        Assert.NotEmpty(page.Results);
        Assert.All(page.Results, r =>
        {
            Assert.Equal(WallpaperKind.Image, r.Item.Kind);
            Assert.StartsWith("https://", r.Item.RemoteUrl);
            Assert.True(r.Item.Width > 0 && r.Item.Height > 0);
            Assert.StartsWith("Mountains · ", r.Item.Title);
            Assert.True(r.Item.IsCachedMedia);
        });
    }

    [Fact]
    public void Nasa_search_finds_videos_and_upgrades_links_to_https()
    {
        var page = NasaSource.Parse(Sample("nasa.json"));
        Assert.NotEmpty(page.Results);
        var item = page.Results[0].Item;
        Assert.Equal(WallpaperKind.Video, item.Kind);
        Assert.NotNull(item.SourceRef);
        Assert.StartsWith("https://", item.ThumbnailUrl);
    }

    [Fact]
    public void Nasa_asset_list_yields_mp4_renditions_largest_first()
    {
        var files = NasaSource.ParseAsset(Sample("nasa-asset.json"));
        Assert.Equal(["large", "medium", "small"], files.Select(f => f.Quality).ToArray()); // multi-GB masters skipped
        Assert.All(files, f => Assert.StartsWith("https://", f.Url));
        Assert.Equal("large", VideoFileSelectorPick(files, 1080));
    }

    private static string? VideoFileSelectorPick(IReadOnlyList<RemoteVideoFile> files, int height)
        => Wavelit.Core.Library.Pexels.VideoFileSelector.Select(files, height)?.Quality;

    [Fact]
    public void Openverse_results_carry_licence_and_creator()
    {
        var page = OpenverseSource.Parse(Sample("openverse.json"));
        var item = Assert.Single(page.Results.Take(1)).Item;
        Assert.StartsWith("CC ", item.License);
        Assert.False(string.IsNullOrEmpty(item.Author));
        Assert.StartsWith("https://", item.RemoteUrl);
    }

    [Fact]
    public void Pixabay_hits_become_videos_with_renditions()
    {
        var page = PixabaySource.Parse(Sample("pixabay.json"), page: 1);
        var result = Assert.Single(page.Results);
        Assert.Equal("Flowers, Yellow, Blossom", result.Item.Title);
        Assert.Equal(3, result.VideoFiles!.Count);
        Assert.Equal(3840, result.Item.Width);
        Assert.True(page.HasMore);
    }

    [Theory]
    [InlineData("http://images-assets.nasa.gov/video/a b/c.mp4", "https://images-assets.nasa.gov/video/a%20b/c.mp4")]
    [InlineData("ftp://example.com/x", null)]
    [InlineData("javascript:alert(1)", null)]
    public void Only_https_urls_survive(string input, string? expected) => Assert.Equal(expected, JsonSource.Https(input));
}

public class FitTests
{
    [Theory]
    [InlineData(3840, 2160, 1920, 1080, FitRating.Perfect)]
    [InlineData(6000, 3840, 3000, 1920, FitRating.Perfect)]
    [InlineData(3840, 2160, 3000, 1920, FitRating.Good)]
    [InlineData(7680, 2160, 1080, 1920, FitRating.Crops)]
    [InlineData(1280, 720, 3000, 1920, FitRating.LowResolution)]
    [InlineData(null, null, 1920, 1080, FitRating.Unknown)]
    public void Rates_media_against_the_canvas(int? w, int? h, int cw, int ch, FitRating expected)
        => Assert.Equal(expected, Fit.Rate(w, h, cw, ch));

    [Fact]
    public void Nearest_ratios_for_dual_and_portrait_setups()
    {
        Assert.StartsWith("16x10", Fit.NearestRatios(3000, 1920));
        Assert.StartsWith("32x9", Fit.NearestRatios(3840, 1080));
        Assert.StartsWith("9x16", Fit.NearestRatios(1080, 1920));
    }
}
