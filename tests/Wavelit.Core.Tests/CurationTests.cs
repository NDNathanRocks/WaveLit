using System.Text.Json;
using Wavelit.Core.Library;
using Wavelit.Core.Library.Sources;

namespace Wavelit.Core.Tests;

public class CurationTests
{
    private static JsonElement Sample(string name)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", name))).RootElement;

    [Theory]
    [InlineData("Earth from Space in 4K – Expedition 65 Edition", true)]
    [InlineData("jsc2021m000138_4K_Earth_Views_Extended_Cut", true)]
    [InlineData("Aurora Time-lapse over Alaska", true)]
    [InlineData("How Can I See the Northern Lights? We Asked a NASA Expert", false)]
    [InlineData("NASA Science Live: Aurora Glow, Electric Flow", false)]
    [InlineData("Moving Ahead With Space Station Power Upgrades on This Week @NASA – 4K", false)]
    [InlineData("Expedition 68 Space Station Crew Talks with Consumer Electronics Show 4K", false)]
    [InlineData("The Aurora Named STEVE", true)]
    [InlineData("SDO: Year 6", false)]
    public void Nasa_keeps_scenic_footage_and_drops_talks(string title, bool expected)
        => Assert.Equal(expected, Curation.IsScenicNasaTitle(title));

    [Theory]
    [InlineData("Olive grove at dawn", true)]   // "live" must match whole words only
    [InlineData("Live coverage of the launch", false)]
    [InlineData("Wikipedia 25 Anthem", false)]
    [InlineData("Snow falling in Tuntorp", true)]
    public void Footage_filter_matches_whole_words(string title, bool expected)
        => Assert.Equal(expected, Curation.LooksLikeFootage(title));

    [Theory]
    [InlineData(1920, 1080, true)]
    [InlineData(6000, 4000, true)]
    [InlineData(1600, 900, false)]
    [InlineData(2400, 3000, false)]  // portrait scan
    [InlineData(5000, 1000, false)]  // panorama strip
    [InlineData(null, null, false)]
    public void Openverse_keeps_large_wide_images(int? w, int? h, bool expected)
        => Assert.Equal(expected, Curation.IsWallpaperSizedImage(w, h));

    [Fact]
    public void Curated_wallhaven_queries_exclude_people_and_rotate_daily()
    {
        var day = new DateOnly(2026, 10, 26);
        string home = Curation.WallhavenQuery(Moods.Featured, day);
        Assert.EndsWith(Curation.NoPeople, home);
        Assert.NotEqual(home, Curation.WallhavenQuery(Moods.Featured, day.AddDays(1)));
        Assert.StartsWith("minimalism ", Curation.WallhavenQuery(Moods.Minimal, day));
        Assert.Null(Curation.CommonsQuery(Moods.Anime, day));
    }

    [Fact]
    public void Commons_keeps_full_hd_landscape_clips_as_webm()
    {
        var page = CommonsSource.Parse(Sample("commons.json"));
        Assert.True(page.HasMore);
        Assert.True(page.Results.Count >= 4);
        Assert.All(page.Results, r =>
        {
            Assert.Equal(WallpaperKind.Video, r.Item.Kind);
            Assert.True(r.Item.Width >= 1920);
            Assert.InRange(r.Item.DurationSeconds!.Value, 8, 600);
            Assert.DoesNotContain("File:", r.Item.Title);
            Assert.DoesNotContain(".webm", r.Item.Title);
            Assert.False(string.IsNullOrEmpty(r.Item.License));
            Assert.All(r.VideoFiles!, f =>
            {
                Assert.Equal("video/webm", f.FileType);
                Assert.StartsWith("https://upload.wikimedia.org/", f.Url);
                Assert.True(f.Height >= 720);
            });
        });
        // 2-second and 720p clips in the sample are dropped.
        Assert.DoesNotContain(page.Results, r => r.Item.Title.Contains("Salzdahlum"));
        Assert.DoesNotContain(page.Results, r => r.Item.Title.Contains("Lake Myvatn"));
        // A WebM transcode is what gets downloaded, saved with a .webm name.
        var file = Wavelit.Core.Library.Pexels.VideoFileSelector.Select(page.Results[0].VideoFiles!, 1080);
        Assert.NotNull(file);
        Assert.Equal(".webm", WallpaperResolver.VideoExtensionFor(file!.Url));
    }

    [Theory]
    [InlineData("File:Timelapse of Milky way changing colors during Aurora - 53921478021.webm", "Timelapse of Milky way changing colors during Aurora")]
    [InlineData("File:Night Sky Timelapse (30549248476).webm", "Night Sky Timelapse")]
    [InlineData("File:2011-09-05_22-54-01-timelapse-clouds-evening.ogv", "2011-09-05 22-54-01-timelapse-clouds-evening")]
    public void Commons_titles_lose_file_noise(string fileTitle, string expected)
        => Assert.Equal(expected, CommonsSource.CleanTitle(fileTitle));

    [Theory]
    [InlineData("<a href=\"//commons.wikimedia.org/wiki/User:X\" title=\"User:X\">Thomas Bresson</a>", "Thomas Bresson")]
    [InlineData("<span>No name</span>", null)]
    [InlineData("[1]", null)]
    [InlineData("Joe &amp; Ann", "Joe & Ann")]
    public void Commons_artist_html_becomes_plain_text(string html, string? expected)
        => Assert.Equal(expected, CommonsSource.PlainText(html));

    [Fact]
    public void Featured_catalog_is_complete_and_safe()
    {
        var all = FeaturedCatalog.For(Moods.Featured);
        Assert.True(all.Count >= 80);
        Assert.Equal(all.Count, all.Select(r => r.Item.Id).Distinct().Count());
        Assert.Contains(all.Take(10), r => r.Item.Kind == WallpaperKind.Video); // interleaved: videos near the top
        Assert.All(all, r =>
        {
            Assert.StartsWith("https://", r.Item.ThumbnailUrl);
            Assert.True(r.Item.Width >= 1920);
            Assert.True(r.Item.IsCachedMedia);
            if (r.Item.Kind == WallpaperKind.Image) Assert.StartsWith("https://w.wallhaven.cc/", r.Item.RemoteUrl);
            else Assert.NotEmpty(r.VideoFiles!);
        });
        foreach (var mood in Moods.All.Skip(1).Where(m => m != Moods.Featured))
            Assert.NotEmpty(FeaturedCatalog.For(mood));
    }
}
