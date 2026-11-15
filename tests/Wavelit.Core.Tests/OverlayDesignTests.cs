using Wavelit.Core.Create.Shaders;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Tests;

/// <summary>The Overlay Designer (choices → GLSL, no AI).</summary>
public class OverlayDesignTests
{
    public static IEnumerable<object[]> EveryShapeMotionCursor()
    {
        foreach (var s in OverlayDesign.Shapes)
            foreach (var m in OverlayDesign.Motions)
                foreach (var c in OverlayDesign.Cursors)
                    yield return [s.Id, m.Id, c.Id];
    }

    [Theory]
    [MemberData(nameof(EveryShapeMotionCursor))]
    public void Every_design_passes_the_lint_with_and_without_music(string shape, string motion, string cursor)
    {
        foreach (bool music in new[] { false, true })
        {
            var d = new OverlayDesign(shape, motion, cursor, Music: music);
            string glsl = d.ToGlsl();
            var problems = ShaderLint.Check(glsl, ShaderKind.Overlay);
            Assert.True(problems.Count == 0, $"{shape}/{motion}/{cursor}: {string.Join("; ", problems)}");
            Assert.Equal(music, AudioSettings.CodeUsesAudio(glsl));
            Assert.Equal(cursor != "none", d.UsesPointer);
            Assert.DoesNotContain("\r", glsl);
        }
    }

    [Fact]
    public void Designs_declare_four_sliders_from_their_choices()
    {
        var p = ShaderParams.Parse(new OverlayDesign(Density: 0.25, Size: 1, Speed: 0, Glow: 0.8).ToGlsl());
        Assert.Equal(["amount", "size", "speed", "glow"], p.Select(x => x.Name));
        Assert.Equal(0.25, p[0].Default);
        Assert.Equal(2.0, p[1].Default);
        Assert.Equal(0.3, p[2].Default);
        Assert.Equal(0.8, p[3].Default);
    }

    [Fact]
    public void Bad_values_normalize_and_the_glsl_is_stable()
    {
        var d = new OverlayDesign("lasers", "teleport", "bite", Density: 7, Size: double.NaN, Speed: -1).Normalize();
        Assert.Equal(("petal", "fall", "part"), (d.Shape, d.Motion, d.Cursor));
        Assert.Equal((1.0, 0.5, 0.0), (d.Density, d.Size, d.Speed));
        Assert.Equal(new OverlayDesign().ToGlsl(), new OverlayDesign().ToGlsl());
        Assert.Equal("Falling Petals", new OverlayDesign().Title);
        Assert.Equal("Swirling Bokeh", new OverlayDesign("orb", "swirl").Title);
        Assert.Contains("gathering to your cursor", new OverlayDesign(Cursor: "gather").Describe());
        Assert.Contains("moving to the music", new OverlayDesign(Music: true).Describe());
    }

    [Fact]
    public void Designer_creations_are_not_labelled_as_ai()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "wavelit-test-" + Guid.NewGuid().ToString("N")));
        var entry = new StudioEntry { Id = "d1", Kind = ShaderKind.Overlay, Prompt = "", Title = "Falling Petals", Code = new OverlayDesign().ToGlsl(), Model = OverlayDesign.Author, Design = new OverlayDesign() };
        Assert.True(entry.IsDesigned);
        var (_, overlay) = GeneratedShaders.CreateOverlay(paths, entry);
        Assert.Null(overlay.Model);
        Assert.Null(GeneratedShaders.MadeWith(OverlayDesign.Author));
        Assert.Equal("ai-chat", GeneratedShaders.MadeWith("pasted"));
        Assert.Contains("Designer", GeneratedShaders.AuthorLine(OverlayDesign.Author));
    }

    [Fact]
    public void Designer_entries_round_trip_through_history()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "wavelit-test-" + Guid.NewGuid().ToString("N")));
        var history = new StudioHistory(paths);
        var design = new OverlayDesign("flake", "swirl", "gather", 0.7, 0.2, 0.9, 0.1, 0.4, 0.6, Music: true);
        history.Add(new StudioEntry { Id = "d2", Kind = ShaderKind.Overlay, Prompt = "", Title = design.Title, Code = design.ToGlsl(), Model = OverlayDesign.Author, Design = design }, null);
        var back = new StudioHistory(paths).All.Single();
        Assert.Equal(design, back.Design);
    }
}
