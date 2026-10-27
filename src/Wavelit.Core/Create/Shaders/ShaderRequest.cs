using System.Text;
using System.Text.Json.Serialization;

namespace Wavelit.Core.Create.Shaders;

/// <summary>What the Studio writes: a full live wallpaper, or a transparent overlay for any wallpaper.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ShaderKind>))]
public enum ShaderKind { Wallpaper, Overlay }

/// <summary>How the shader responds to the cursor.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Interactivity>))]
public enum Interactivity { None, Reacts, Playful }

/// <summary>How much it moves.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MotionSpeed>))]
public enum MotionSpeed { Still, Calm, Lively }

/// <summary>A shader to make, or a change to one (<paramref name="Previous"/> + <paramref name="RefineNote"/>).</summary>
/// <param name="Palette">A palette chip ("auto" lets the model choose).</param>
public sealed record ShaderRequest(
    ShaderKind Kind,
    string Prompt,
    Interactivity Interactivity = Interactivity.None,
    string Palette = ShaderPalettes.Auto,
    MotionSpeed Speed = MotionSpeed.Calm,
    string? Previous = null,
    string? RefineNote = null,
    ShaderRecipe? Recipe = null,
    MusicReaction? Music = null)
{
    public const int MaxPromptLength = 400;

    public bool IsRefine => Previous is not null && !string.IsNullOrWhiteSpace(RefineNote);
}

public static class ShaderPalettes
{
    public const string Auto = "auto";

    /// <summary>Palette chips: id → how the model is told about it.</summary>
    public static readonly IReadOnlyList<(string Id, string Label, string Description)> All =
    [
        (Auto, "Any", "Choose the palette that suits the idea best."),
        ("night", "Midnight", "deep navy and indigo with small cool highlights"),
        ("sunset", "Sunset", "warm coral, peach and magenta fading into dusk purple"),
        ("forest", "Forest", "mossy greens, teal and warm amber light"),
        ("neon", "Neon", "electric magenta, cyan and violet on near-black"),
        ("pastel", "Pastel", "soft lilac, mint, peach and cream, low contrast"),
        ("mono", "Monochrome", "greys and one quiet accent, almost no saturation"),
    ];

    public static string Describe(string id)
    {
        foreach (var p in All)
            if (p.Id == id) return p.Description;
        return All[0].Description;
    }
}

/// <summary>
/// The Studio's prompt template (plan §21.4): a fill-in sentence whose slots are picked from chips or typed. It
/// becomes the "Idea" plus labelled lines in the user message, so the system prompt stays byte-stable.
/// </summary>
public sealed record ShaderRecipe(string? Subject = null, string? Motion = null, string? Mood = null, string? Light = null,
    string? Detail = null, string? Avoid = null)
{
    public const int MaxSlot = 80;
    public static readonly IReadOnlyList<string> Motions = ["drifts", "swirls", "flows", "pulses", "falls", "blooms"];
    public static readonly IReadOnlyList<string> Moods = ["calm", "dreamy", "moody", "playful", "cosmic"];
    public static readonly IReadOnlyList<string> Lights = ["moonlight", "neon", "golden hour", "candlelight", "soft daylight"];
    public static readonly IReadOnlyList<string> Details = ["minimal", "balanced", "rich"];
    public static readonly IReadOnlyList<string> Subjects =
    [
        "ink cloud", "aurora ribbons", "city rain on glass", "floating petals", "jellyfish", "sand dunes", "nebula",
        "paper lanterns", "ocean swell", "fireflies", "crystal caves", "silk waves",
    ];

    public bool IsEmpty => string.IsNullOrWhiteSpace(Subject);

    /// <summary>The recipe as one plain-English sentence (shown live, and used as the idea).</summary>
    public string Sentence(string? paletteLabel, Interactivity cursor)
    {
        var sb = new StringBuilder();
        sb.Append(Article(Slot(Subject) ?? "scene")).Append(' ').Append(Slot(Subject) ?? "scene");
        if (Slot(Motion) is { } motion) sb.Append(" that ").Append(motion);
        if (Slot(Mood) is { } mood) sb.Append(", in a ").Append(mood).Append(" mood");
        if (Slot(Light) is { } light) sb.Append(", lit by ").Append(light);
        if (paletteLabel is { Length: > 0 } p && p != "Any") sb.Append(", in ").Append(p.ToLowerInvariant()).Append(" colours");
        sb.Append('.');
        if (Slot(Detail) is { } detail) sb.Append(" Detail: ").Append(detail).Append('.');
        sb.Append(cursor switch
        {
            Interactivity.Reacts => " The cursor gently parts it.",
            Interactivity.Playful => " The cursor plays with it.",
            _ => "",
        });
        if (Slot(Avoid) is { } avoid) sb.Append(" Avoid: ").Append(avoid).Append('.');
        return sb.ToString();
    }

    internal void AppendTo(StringBuilder sb, Func<string, string> clean)
    {
        void Line(string label, string? value)
        {
            if (Slot(value) is { } v) sb.Append(label).Append(": ").Append(clean(v)).Append('\n');
        }
        Line("Subject", Subject);
        Line("Motion", Motion);
        Line("Mood", Mood);
        Line("Light", Light);
        Line("Detail", Detail);
        Line("Avoid", Avoid);
    }

    /// <summary>Random slots that go together (Surprise me).</summary>
    public static ShaderRecipe Surprise(Random random) => new(
        Subjects[random.Next(Subjects.Count)], Motions[random.Next(Motions.Count)], Moods[random.Next(Moods.Count)],
        Lights[random.Next(Lights.Count)], Details[random.Next(2)]);

    private static string? Slot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = string.Join(' ', value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return v.Length > MaxSlot ? v[..MaxSlot] : v;
    }

    private static string Article(string noun)
        => noun.EndsWith('s') && !noun.EndsWith("ss", StringComparison.Ordinal) ? "Some" : "aeiou".Contains(char.ToLowerInvariant(noun[0])) ? "An" : "A";
}

/// <summary>How an audio shader reacts to the music (the Studio's Audio tab).</summary>
public sealed record MusicReaction(string ReactsTo = "all", string ReactsBy = "pulsing")
{
    public static readonly IReadOnlyList<string> To = ["bass", "beat", "melody", "all"];
    public static readonly IReadOnlyList<string> By = ["pulsing", "shaking", "glowing", "gathering into patterns"];

    public string Describe() => $"reacts to {(To.Contains(ReactsTo) ? ReactsTo : "all")} by {(By.Contains(ReactsBy) ? ReactsBy : "pulsing")}";
}
