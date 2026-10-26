using System.Text.Json;
using Wavelit.Core.Library;
using Wavelit.Core.Library.BuiltIn;
using Wavelit.Core.Library.Pexels;
using Wavelit.Core.Library.Shadertoy;

namespace Wavelit.Core.Tests;

public class DisplayAssignmentsTests
{
    private static readonly string[] Monitors = ["A", "B"];

    [Fact]
    public void Assigning_to_all_displays_extends_and_clears_per_display()
    {
        var d = new DisplayAssignments { Layout = DisplayLayout.PerDisplay, PerDisplay = new() { ["A"] = "x" } };
        var next = d.Assign("y", null, Monitors);
        Assert.Equal(DisplayLayout.Span, next.Layout);
        Assert.Equal("y", next.ItemFor("A"));
        Assert.Equal("y", next.ItemFor("B"));
    }

    [Fact]
    public void Assigning_to_one_display_keeps_the_others()
    {
        var d = new DisplayAssignments { SharedItemId = "base" };
        var next = d.Assign("new", "B", Monitors);
        Assert.Equal(DisplayLayout.PerDisplay, next.Layout);
        Assert.Equal("base", next.ItemFor("A"));
        Assert.Equal("new", next.ItemFor("B"));
    }

    [Fact]
    public void Span_layout_survives_assigning_to_all()
    {
        var d = new DisplayAssignments { Layout = DisplayLayout.Span, SharedItemId = "a" };
        Assert.Equal(DisplayLayout.Span, d.Assign("b", null, Monitors).Layout);
    }

    [Fact]
    public void Duplicate_layout_survives_assigning_to_all()
    {
        var d = new DisplayAssignments { Layout = DisplayLayout.Duplicate, SharedItemId = "a" };
        Assert.Equal(DisplayLayout.Duplicate, d.Assign("b", null, Monitors).Layout);
    }

    [Fact]
    public void Extend_is_the_default_layout() => Assert.Equal(DisplayLayout.Span, new DisplayAssignments().Layout);

    [Fact]
    public void Removing_an_item_falls_back()
    {
        var d = new DisplayAssignments { Layout = DisplayLayout.PerDisplay, SharedItemId = "gone", PerDisplay = new() { ["A"] = "gone", ["B"] = "keep" } };
        var next = d.WithoutItem("gone", "fallback");
        Assert.Equal("fallback", next.ItemFor("A"));
        Assert.Equal("keep", next.ItemFor("B"));
    }

    [Fact]
    public void Switching_to_per_display_seeds_every_monitor()
    {
        var next = new DisplayAssignments { SharedItemId = "s" }.WithLayout(DisplayLayout.PerDisplay, Monitors);
        Assert.Equal("s", next.PerDisplay["A"]);
        Assert.Equal("s", next.PerDisplay["B"]);
    }
}

public class VideoFileSelectorTests
{
    private static readonly RemoteVideoFile[] Files =
    [
        new("https://v/sd.mp4", 960, 540, 25, "video/mp4", "sd"),
        new("https://v/hd.mp4", 1920, 1080, 25, "video/mp4", "hd"),
        new("https://v/qhd.mp4", 2560, 1440, 25, "video/mp4", "hd"),
        new("https://v/uhd.mp4", 3840, 2160, 25, "video/mp4", "uhd"),
        new("https://v/uhd120.mp4", 3840, 2160, 120, "video/mp4", "uhd"),
        new("http://v/insecure.mp4", 1920, 1080, 25, "video/mp4", "hd"),
        new("https://v/portrait.mp4", 1080, 1920, 25, "video/mp4", "hd"),
    ];

    [Theory]
    [InlineData(1080, "https://v/hd.mp4")]
    [InlineData(1200, "https://v/qhd.mp4")]
    [InlineData(1440, "https://v/qhd.mp4")]
    [InlineData(2160, "https://v/uhd.mp4")]
    [InlineData(4320, "https://v/uhd.mp4")]
    [InlineData(600, "https://v/hd.mp4")] // never below 720p
    public void Picks_smallest_file_that_covers_the_display(int height, string expected)
        => Assert.Equal(expected, VideoFileSelector.Select(Files, height)?.Url);

    [Fact]
    public void Returns_null_when_nothing_is_usable()
        => Assert.Null(VideoFileSelector.Select([new RemoteVideoFile("http://x/a.mp4", 1920, 1080, 30, "video/mp4", "hd")], 1080));
}

public class PexelsParsingTests
{
    [Fact]
    public void Parses_a_search_response()
    {
        const string json = """
        {"page":1,"per_page":24,"total_results":2,"next_page":"https://api.pexels.com/videos/search/?page=2",
         "videos":[
          {"id":857195,"width":3840,"height":2160,"duration":14,"url":"https://www.pexels.com/video/waves-crashing-on-rocks-857195/",
           "image":"https://images.pexels.com/videos/857195/pictures/preview-0.jpg",
           "user":{"id":1,"name":"Jane Doe","url":"https://www.pexels.com/@jane"},
           "video_files":[{"id":1,"quality":"hd","file_type":"video/mp4","width":1920,"height":1080,"fps":29.97,"link":"https://videos.pexels.com/1.mp4"},
                          {"id":2,"quality":null,"file_type":"video/mp4","width":null,"height":null,"fps":null,"link":"https://videos.pexels.com/2.mp4"}]},
          {"id":5,"width":1080,"height":1920,"duration":9,"url":"https://www.pexels.com/video/5/","image":null,"user":null,
           "video_files":[{"id":3,"quality":"hd","file_type":"video/mp4","width":1080,"height":1920,"fps":30,"link":"https://videos.pexels.com/3.mp4"}]}
         ]}
        """;
        var response = JsonSerializer.Deserialize<PexelsVideoResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var results = response.Videos!.Select(PexelsClient.ToResult).OfType<SearchResult>().ToList();

        var only = Assert.Single(results); // the portrait video is skipped
        Assert.Equal("pexels-857195", only.Item.Id);
        Assert.Equal("Waves crashing on rocks", only.Item.Title);
        Assert.Equal("Jane Doe", only.Item.Author);
        Assert.Single(only.VideoFiles!); // file without dimensions is dropped
    }

    [Theory]
    [InlineData("https://www.pexels.com/video/a-b-c-123/", "A b c")]
    [InlineData("https://www.pexels.com/video/123/", null)]
    [InlineData(null, null)]
    public void Title_comes_from_the_page_slug(string? url, string? expected)
        => Assert.Equal(expected, PexelsClient.TitleFromUrl(url));
}

public class ShadertoyParsingTests
{
    private const string MultipassJson = """
    {"Shader":{"ver":"0.1","info":{"id":"Ms2SD1","name":"Seascape","username":"TDM","description":"","likes":3000},
     "renderpass":[
       {"inputs":[{"id":"4dXGR8","src":"/media/previz/buffer00.png","ctype":"buffer","channel":0,"sampler":{"filter":"linear","wrap":"clamp","vflip":"true"}}],
        "outputs":[{"id":"4dfGRr","channel":0}],"code":"void mainImage(out vec4 c, in vec2 f){c=texture(iChannel0,f/iResolution.xy);}","name":"Image","type":"image"},
       {"inputs":[{"id":30,"src":"/media/a/abc123.png","ctype":"texture","channel":1,"sampler":{"filter":"mipmap","wrap":"repeat","vflip":false}}],
        "outputs":[{"id":"4dXGR8","channel":0}],"code":"void mainImage(out vec4 c, in vec2 f){c=vec4(1);}","name":"Buffer A","type":"buffer"},
       {"inputs":[],"outputs":[],"code":"float k(){return 1.;}","name":"Common","type":"common"}
     ]}}
    """;

    [Fact]
    public void Parses_multipass_shader_with_mixed_types()
    {
        using var doc = JsonDocument.Parse(MultipassJson);
        var shader = ShadertoyClient.Parse(doc.RootElement);
        Assert.Equal("Seascape", shader.Name);
        Assert.Equal("TDM", shader.Author);
        Assert.Equal(3, shader.Passes.Count);
        var image = shader.Passes[0];
        Assert.Equal("buffer", image.Inputs[0].CType);
        Assert.True(image.Inputs[0].VFlip);
        Assert.Equal("30", shader.Passes[1].Inputs[0].Id); // numeric ids become strings
        Assert.Null(ShadertoyClient.UnsupportedReason(shader));
    }

    [Fact]
    public void Webcam_input_is_unsupported()
    {
        string json = MultipassJson.Replace("\"ctype\":\"texture\"", "\"ctype\":\"webcam\"");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("needs webcam input", ShadertoyClient.UnsupportedReason(ShadertoyClient.Parse(doc.RootElement)));
    }

    [Fact]
    public void Error_response_throws()
    {
        using var doc = JsonDocument.Parse("""{"Error":"Shader not found"}""");
        Assert.Throws<LibrarySourceException>(() => ShadertoyClient.Parse(doc.RootElement));
    }
}

public class LocalFilesTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("http://example.com/x", "http://example.com/x")]
    [InlineData("  https://a.b/c?d=1  ", "https://a.b/c?d=1")]
    public void Accepts_web_addresses(string input, string expected)
        => Assert.Equal(expected, LocalFiles.CreateUrlItem(input)?.WebUrl);

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com")]
    [InlineData("")]
    public void Rejects_unsafe_or_invalid_addresses(string input)
        => Assert.Null(LocalFiles.CreateUrlItem(input));

    [Theory]
    [InlineData(@"C:\v\clip.MP4", true)]
    [InlineData(@"C:\v\page.html", true)]
    [InlineData(@"C:\v\movie.mkv", false)]
    [InlineData(@"C:\v\run.exe", false)]
    public void Recognizes_supported_files(string path, bool supported)
        => Assert.Equal(supported, LocalFiles.IsSupported(path));
}

public class ContentGuardTests
{
    [Theory]
    [InlineData("ocean waves", false)]
    [InlineData("Sunset", false)]
    [InlineData("essex countryside", false)] // substring of a blocked word must not match
    [InlineData("nude beach", true)]
    [InlineData("NSFW", true)]
    public void Blocks_adult_terms_only_as_whole_words(string query, bool blocked)
        => Assert.Equal(blocked, new ContentGuard().IsBlocked(query));
}

public class BuiltInShaderTests
{
    [Fact]
    public void Every_built_in_item_has_a_program()
    {
        Assert.NotEmpty(BuiltInShaders.Items);
        foreach (var item in BuiltInShaders.Items)
        {
            var program = BuiltInShaders.Program(item.ShaderId!);
            Assert.NotNull(program);
            Assert.Contains("mainImage", program!.Passes.Single().Code);
        }
        Assert.NotNull(BuiltInShaders.Find(BuiltInShaders.DefaultItemId));
    }
}
