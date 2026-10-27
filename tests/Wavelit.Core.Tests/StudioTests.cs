using System.Text.Json;
using System.Text.RegularExpressions;
using Wavelit.Core.Create.Shaders;
using Wavelit.Core.Library;
using Wavelit.Core.Library.BuiltIn;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Tests;

public class StudioTests
{
    private static string AppDir(params string[] parts)
        => Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Wavelit.App", .. parts]));

    /// <summary>The GLSL of each overlay style in interact.js (without the shared helpers).</summary>
    private static Dictionary<string, string> WebOverlays()
    {
        string js = File.ReadAllText(AppDir("Web", "interact.js"));
        return Regex.Matches(js, @"^    (\w+): COMMON \+ `(.*?)`,", RegexOptions.Multiline | RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    // ---- prompt

    [Fact]
    public void System_prompt_is_byte_stable_and_holds_the_contract_and_examples()
    {
        Assert.Equal(ShaderPrompt.System, ShaderPrompt.System.ToString());
        Assert.Contains("iFocus", ShaderPrompt.System);
        Assert.Contains("ilOut", ShaderPrompt.System);
        Assert.Contains(BuiltInShaders.Source("lava")!.Replace("\r\n", "\n").Trim(), ShaderPrompt.System);
        Assert.DoesNotContain("\r", ShaderPrompt.System);
        // The chat version is the same contract with a different answer format.
        Assert.StartsWith(ShaderPrompt.System[..2000], ShaderPrompt.ChatSystem);
        Assert.Contains("```glsl code block", ShaderPrompt.ChatSystem);
    }

    [Fact]
    public void User_message_carries_the_options_on_one_line_each()
    {
        var r = new ShaderRequest(ShaderKind.Overlay, "petals\nscattering\r\nfrom the cursor", Interactivity.Playful, "sunset", MotionSpeed.Lively);
        string m = ShaderPrompt.UserMessage(r);
        Assert.Contains("Idea: petals scattering from the cursor\n", m);
        Assert.Contains("overlay", m);
        Assert.Contains("playful", m);
        Assert.Contains("coral", m);
        Assert.Contains("Speed: lively", m);
        Assert.DoesNotContain(ShaderPrompt.System[..200], m);

        string none = ShaderPrompt.UserMessage(r with { Interactivity = Interactivity.None, Prompt = new string('a', 900) });
        Assert.Contains("do not read iPointer", none);
        Assert.Contains("Idea: " + new string('a', ShaderRequest.MaxPromptLength) + "\n", none);
    }

    [Fact]
    public void Refine_message_includes_the_previous_code_and_the_change()
    {
        var r = new ShaderRequest(ShaderKind.Wallpaper, "ink", Previous: "void mainImage(out vec4 c, in vec2 f) { c = vec4(1.0); }", RefineNote: "more purple");
        string m = ShaderPrompt.UserMessage(r);
        Assert.True(r.IsRefine);
        Assert.Contains("```glsl\nvoid mainImage", m);
        Assert.Contains("Change it: more purple", m);
    }

    // ---- lint

    [Fact]
    public void Lint_passes_every_built_in_wallpaper()
    {
        foreach (var item in BuiltInShaders.Items)
            Assert.True(ShaderLint.Check(BuiltInShaders.Source(item.ShaderId!)!, ShaderKind.Wallpaper) is { Count: 0 },
                $"{item.ShaderId}: {string.Join("; ", ShaderLint.Check(BuiltInShaders.Source(item.ShaderId!)!, ShaderKind.Wallpaper))}");
    }

    [Fact]
    public void Lint_passes_every_built_in_overlay()
    {
        var overlays = WebOverlays();
        Assert.Equal(13, overlays.Count);
        foreach (var (style, code) in overlays)
        {
            var problems = ShaderLint.Check(code, ShaderKind.Overlay);
            Assert.True(problems.Count == 0, $"{style}: {string.Join("; ", problems)}");
        }
    }

    [Theory]
    [InlineData("void mainImage(out vec4 c, in vec2 f) { float i = 0.0; while (i < 1.0) { i += 0.1; } c = vec4(i); }", "while")]
    [InlineData("void mainImage(out vec4 c, in vec2 f) { int i = 0; do { i++; } while (i < 3); c = vec4(1.0); }", "while")]
    [InlineData("void mainImage(out vec4 c, in vec2 f) { int n = int(iTime); for (int i = 0; i < n; i++) {} c = vec4(1.0); }", "constant")]
    [InlineData("void mainImage(out vec4 c, in vec2 f) { for (int i = 0; i < 1000; i++) {} c = vec4(1.0); }", "too high")]
    [InlineData("void mainImage(out vec4 c, in vec2 f) { int k = 5; for (int i = k; i > 0; i--) {} c = vec4(1.0); }", "counts down")]
    [InlineData("void mainImage(out vec4 c, in vec2 f) { c = texture(iChannel0, f); }", "textures")]
    [InlineData("#define PI 3.14159\nvoid mainImage(out vec4 c, in vec2 f) { c = vec4(PI); }", "#define")]
    [InlineData("#extension GL_OES_standard_derivatives : enable\nvoid mainImage(out vec4 c, in vec2 f) { c = vec4(1.0); }", "#extension")]
    [InlineData("void main() { }", "mainImage")]
    [InlineData("void mainImage(out vec4 c, in vec2 f) { float a = 0.0; for (int i = 0; i < 64; i++) for (int j = 0; j < 64; j++) a += 0.001; c = vec4(a); }", "Nested")]
    public void Lint_rejects_what_can_never_run_as_a_wallpaper(string code, string expected)
        => Assert.Contains(ShaderLint.Check(code, ShaderKind.Wallpaper), p => p.Contains(expected, StringComparison.Ordinal));

    [Fact]
    public void Lint_ignores_comments_and_accepts_const_bounds_and_float_loops()
    {
        const string code = """
            // while (true) would hang; this loop is bounded
            const int STEPS = 48;
            /* do { } */
            void mainImage(out vec4 c, in vec2 f) {
                float a = 0.0;
                for (int i = 0; i < STEPS; i++) a += 0.01;
                for (float x = 0.0; x < 1.0; x += 0.125) a += x;
                for (int j = 7; j >= 0; j--) a -= 0.001;
                c = vec4(a);
            }
            """;
        Assert.Empty(ShaderLint.Check(code, ShaderKind.Wallpaper));
        Assert.Equal(48, ShaderLint.NestedIterations(code));
    }

    [Fact]
    public void Lint_counts_nested_iterations_through_unbraced_loops()
    {
        const string code = "for (int y = -2; y <= 2; y++)\n for (int x = -2; x <= 2; x++) { a += 1.0; }\nfor (int k = 0; k < 16; k++) { b += 1.0; }";
        Assert.Equal(25, ShaderLint.NestedIterations(code));
    }

    [Fact]
    public void Overlays_must_use_ilOut()
        => Assert.Contains(ShaderLint.Check("void mainImage(out vec4 c, in vec2 f) { c = vec4(0.0); }", ShaderKind.Overlay), p => p.Contains("ilOut"));

    // ---- parsing

    [Fact]
    public void Structured_answers_parse_and_the_code_decides_cursor_use()
    {
        string json = JsonSerializer.Serialize(new
        {
            title = "Velvet Tide",
            description = "Slow waves.",
            code = "#version 300 es\nprecision highp float;\nuniform float iTime;\nvoid mainImage(out vec4 c, in vec2 f) { c = vec4(iPointer.z); }",
            mood = "Cozy",
            usesPointer = false,
        });
        var d = ShaderResult.ParseJson(json);
        Assert.Equal(("Velvet Tide", "Slow waves.", "cozy", true), (d.Title, d.Description, d.Mood, d.UsesPointer));
        Assert.Equal("void mainImage(out vec4 c, in vec2 f) { c = vec4(iPointer.z); }", d.Code);
    }

    [Fact]
    public void Chat_replies_parse_from_a_fenced_block_or_bare_code()
    {
        const string reply = "Here you go!\n```glsl\nfloat h(float x) { return x; }\n```\nAnd the main part:\n```glsl\nvoid mainImage(out vec4 c, in vec2 f) { c = vec4(1.0); }\n```\nEnjoy.";
        Assert.Equal("void mainImage(out vec4 c, in vec2 f) { c = vec4(1.0); }", ShaderResult.ParseText(reply).Code);
        Assert.Equal("void mainImage(out vec4 c, in vec2 f) { c = vec4(1.0); }", ShaderResult.ParseText("  void mainImage(out vec4 c, in vec2 f) { c = vec4(1.0); }\n").Code);
        Assert.Equal("Untitled", ShaderResult.ParseText(reply).Title);
        Assert.Throws<ShaderAnswerException>(() => ShaderResult.ParseText("Sorry, I can't help with that."));
        Assert.Throws<ShaderAnswerException>(() => ShaderResult.ParseJson("not json and no shader"));
        // JSON in prose still works.
        Assert.Equal("void mainImage(out vec4 c, in vec2 f) { }", ShaderResult.ParseText("{\"code\": \"void mainImage(out vec4 c, in vec2 f) { }\", \"title\": \"X\"}").Code);
    }

    // ---- providers

    [Fact]
    public void Claude_requests_cache_the_contract_and_ask_for_structured_output()
    {
        var r = new ShaderRequest(ShaderKind.Wallpaper, "aurora over a frozen lake");
        var p = ClaudeShaderAuthor.BuildParams(ClaudeShaderAuthor.Opus, r, null, null);
        string body = p.ToString();
        Assert.Equal(ClaudeShaderAuthor.Opus, p.Model.Raw());
        Assert.Contains("\"cache_control\"", body);
        Assert.Contains("\"json_schema\"", body);
        Assert.Contains("\"medium\"", body);
        Assert.Contains("\"adaptive\"", body);
        Assert.Contains("\"fallbacks\":\"default\"", body.Replace(" ", "").Replace("\n", ""));
        Assert.Contains(ClaudeShaderAuthor.FallbackBeta, p.Betas!.Select(b => b.Raw()));
        Assert.DoesNotContain("frozen lake", JsonSerializer.Serialize(p.System!.ToString()));   // the cached prefix never varies

        var haiku = ClaudeShaderAuthor.BuildParams(ClaudeShaderAuthor.Haiku, r, null, null);
        Assert.DoesNotContain("fallbacks", haiku.ToString());   // Haiku has no server-side fallback

        var fix = ClaudeShaderAuthor.BuildParams(ClaudeShaderAuthor.Opus, r, "void mainImage(out vec4 c, in vec2 f) { c = vec4(1); }", ShaderPrompt.FixMessage("line 1: cannot convert"));
        Assert.Contains("Your previous attempt", fix.ToString());
        Assert.Contains("cannot convert", fix.ToString());
    }

    [Fact]
    public void Unknown_models_fall_back_to_opus()
    {
        Assert.Equal(ClaudeShaderAuthor.Opus, ClaudeShaderAuthor.Normalize("gpt-9"));
        Assert.Equal(ClaudeShaderAuthor.Sonnet, ClaudeShaderAuthor.Normalize(ClaudeShaderAuthor.Sonnet));
    }

    [Fact]
    public void Cloudflare_answers_parse_in_both_shapes()
    {
        using var openAi = JsonDocument.Parse("""{"choices":[{"message":{"role":"assistant","content":"```glsl\nvoid mainImage(out vec4 c, in vec2 f) { }\n```"}}]}""");
        using var native = JsonDocument.Parse("""{"result":{"response":"hello"},"success":true}""");
        Assert.Contains("mainImage", CloudflareShaderAuthor.AnswerText(openAi.RootElement));
        Assert.Equal("hello", CloudflareShaderAuthor.AnswerText(native.RootElement));
        string body = JsonSerializer.Serialize(CloudflareShaderAuthor.BuildBody(new ShaderRequest(ShaderKind.Overlay, "snow"), null, null));
        Assert.Contains("gpt-oss-120b", body);
        Assert.Contains("glsl code block", body);   // the chat contract, not the JSON one
    }

    // ---- the loop

    private const string Good = "void mainImage(out vec4 c, in vec2 f) { c = vec4(0.5); }";

    private sealed class FakeAuthor(params string[] codes) : IShaderAuthor
    {
        public List<string?> Feedback { get; } = [];
        public string[] Codes { get; } = codes;
        public string Id => "fake";
        public string Label => "Fake";
        public string Model => "fake-model";
        public bool IsConfigured => true;
        public IShaderSession Start(ShaderRequest request) => new Session(this);

        private sealed class Session(FakeAuthor owner) : IShaderSession
        {
            public Task<ShaderDraft> WriteAsync(string? feedback, IProgress<string>? progress, CancellationToken ct)
            {
                owner.Feedback.Add(feedback);
                string code = owner.Codes[Math.Min(owner.Feedback.Count - 1, owner.Codes.Length - 1)];
                return Task.FromResult(new ShaderDraft("T", "D", code, "abstract", false) { Usage = new ShaderUsage(10, 20, 5, 0) });
            }
        }
    }

    private sealed class FakeLab(Func<string, LabReport> check) : IShaderLab
    {
        public int Calls { get; private set; }
        public Task<LabReport> CheckAsync(string code, ShaderKind kind, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(check(code));
        }
    }

    private static LabReport Ok(double ms = 1.2) => new(true, null, ms, ms * 1.2, false, [1, 2, 3]);

    [Fact]
    public async Task A_compile_error_is_sent_back_and_fixed()
    {
        var author = new FakeAuthor("void mainImage(out vec4 c, in vec2 f) { c = vec4(1); }", Good);
        var lab = new FakeLab(code => code == Good ? Ok() : LabReport.Failed("line 1: 'constructor' : not enough data"));
        var outcome = await new ShaderStudio(lab).CreateAsync(author, new ShaderRequest(ShaderKind.Wallpaper, "x"), null, default);
        Assert.True(outcome.Succeeded);
        Assert.Equal(1, outcome.FixRounds);
        Assert.Null(author.Feedback[0]);
        Assert.Contains("not enough data", author.Feedback[1]);
        Assert.Equal(new ShaderUsage(20, 40, 10, 0), outcome.Usage);
    }

    [Fact]
    public async Task Lint_problems_are_fixed_without_compiling()
    {
        var author = new FakeAuthor("void mainImage(out vec4 c, in vec2 f) { while (true) {} }", Good);
        var lab = new FakeLab(_ => Ok());
        var outcome = await new ShaderStudio(lab).CreateAsync(author, new ShaderRequest(ShaderKind.Wallpaper, "x"), null, default);
        Assert.True(outcome.Succeeded);
        Assert.Equal(1, lab.Calls);
        Assert.Contains("while", author.Feedback[1]);
    }

    [Fact]
    public async Task It_gives_up_after_three_fix_rounds_and_keeps_the_last_draft()
    {
        var author = new FakeAuthor(Good);
        var lab = new FakeLab(_ => Ok(ms: 9));   // always over budget
        var outcome = await new ShaderStudio(lab).CreateAsync(author, new ShaderRequest(ShaderKind.Wallpaper, "x"), null, default);
        Assert.False(outcome.Succeeded);
        Assert.Equal(ShaderStudio.MaxFixRounds + 1, author.Feedback.Count);
        Assert.Contains("too slow", author.Feedback[1]);
        Assert.Contains("light enough", outcome.Problem);
        Assert.Equal(Good, outcome.Draft!.Code);
    }

    [Fact]
    public async Task Blank_images_are_rejected()
    {
        var lab = new FakeLab(_ => new LabReport(true, null, 1, 1, true, null));
        var check = await new ShaderStudio(lab).CheckAsync(Good, ShaderKind.Wallpaper, default);
        Assert.Equal(CheckFailure.Blank, check.Failure);
    }

    // ---- storage

    [Fact]
    public void History_keeps_the_newest_fifty_and_their_thumbnails()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "wavelit-test-" + Guid.NewGuid().ToString("N")));
        try
        {
            var history = new StudioHistory(paths);
            StudioEntry Entry(int i) => new() { Id = "st-" + i, Kind = ShaderKind.Wallpaper, Prompt = "p" + i, Title = "T" + i, Code = Good };
            var first = history.Add(Entry(0), [1, 2, 3]);
            Assert.True(File.Exists(first.ThumbnailPath));
            for (int i = 1; i <= StudioHistory.Capacity; i++) history.Add(Entry(i), [4]);
            Assert.Equal(StudioHistory.Capacity, history.All.Count);
            Assert.Equal("st-50", history.All[0].Id);
            Assert.False(File.Exists(first.ThumbnailPath));   // trimmed with its entry
            Assert.Equal(StudioHistory.Capacity, new StudioHistory(paths).All.Count);   // persisted
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public void Saved_shaders_resolve_like_pasted_ones()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "wavelit-test-" + Guid.NewGuid().ToString("N")));
        try
        {
            var entry = new StudioEntry { Id = "st-1", Kind = ShaderKind.Wallpaper, Prompt = "glow", Title = "Glow", Code = Good, Model = ClaudeShaderAuthor.Opus };
            var item = GeneratedShaders.CreateItem(paths, entry);
            Assert.Equal(WallpaperSources.GeneratedShader, item.Source);
            Assert.Equal(item.Id, GeneratedShaders.CreateItem(paths, entry with { Id = "st-2" }).Id);   // same code, same item
            Assert.Equal(Good, PastedShader.Program(item).Passes[0].Code);
            Assert.Equal("You, with Claude", item.Author);
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public void Studio_overlays_survive_in_settings_only_while_they_exist()
    {
        var (id, overlay) = ("custom-abc123", new CustomOverlay("Glow", Good, UsesPointer: true));
        var s = new AppSettings
        {
            CustomOverlays = new() { [id] = overlay, ["custom-empty"] = overlay with { Code = "" }, ["stardust"] = overlay },
            Interactive = new InteractiveSettings(id),
            OverlayFor = new() { ["a"] = id, ["b"] = "custom-gone" },
        }.Normalize();
        Assert.Equal([id], s.CustomOverlays.Keys);
        Assert.Equal(id, s.Interactive.Style);
        Assert.Equal(["a"], s.OverlayFor.Keys);
        Assert.Equal("off", (s with { CustomOverlays = new() }).Normalize().Interactive.Style);   // deleted → off
        Assert.Equal(id, new InteractiveSettings("stardust").For(id).Style);
    }
}
