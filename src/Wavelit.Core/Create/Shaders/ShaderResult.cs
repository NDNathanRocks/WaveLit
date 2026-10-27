using System.Text.Json;
using System.Text.RegularExpressions;
using Wavelit.Core.Library;

namespace Wavelit.Core.Create.Shaders;

/// <summary>A shader written by a model (or pasted from one), before Wavelit has checked it.</summary>
public sealed record ShaderDraft(string Title, string Description, string Code, string Mood, bool UsesPointer)
{
    /// <summary>Token usage reported by the provider (null when unknown, e.g. pasted).</summary>
    public ShaderUsage? Usage { get; init; }
}

public sealed record ShaderUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    public ShaderUsage Add(ShaderUsage? other) => other is null ? this : new(
        InputTokens + other.InputTokens, OutputTokens + other.OutputTokens,
        CacheReadTokens + other.CacheReadTokens, CacheWriteTokens + other.CacheWriteTokens);
}

/// <summary>Raised when a model's answer can't be used at all (no shader in it).</summary>
public sealed class ShaderAnswerException(string message) : Exception(message);

/// <summary>Turns model output into a <see cref="ShaderDraft"/>, defensively.</summary>
public static partial class ShaderResult
{
    public static readonly IReadOnlyList<string> Moods = ["nature", "space", "abstract", "neon", "cozy", "minimal"];

    /// <summary>Structured output: the JSON object asked for in <see cref="ShaderPrompt.System"/>.</summary>
    public static ShaderDraft ParseJson(string json)
        // Some models wrap the JSON in prose or fences; fall back to finding the code.
        => TryParseJson(json) ?? ParseText(json, allowJson: false);

    private static ShaderDraft? TryParseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "code") is not { } code) return null;
            return Build(Str(root, "title"), Str(root, "description"), code, Str(root, "mood"),
                root.TryGetProperty("usesPointer", out var up) && up.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Free text: a reply from any chat AI or a text model. Accepts JSON, a ```glsl block or bare GLSL.</summary>
    public static ShaderDraft ParseText(string text) => ParseText(text, allowJson: true);

    private static ShaderDraft ParseText(string text, bool allowJson)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ShaderAnswerException("Paste the shader code your AI wrote.");
        string trimmed = text.Trim();
        if (allowJson && trimmed.StartsWith('{') && TryParseJson(trimmed) is { } fromJson) return fromJson;

        string? code = null;
        foreach (Match m in Fence().Matches(text))
        {
            string block = m.Groups["code"].Value;
            if (block.Contains("mainImage", StringComparison.Ordinal) && (code is null || block.Length > code.Length)) code = block;
        }
        code ??= trimmed.Contains("mainImage", StringComparison.Ordinal) ? StripFenceLines(trimmed) : null;
        if (code is null)
            throw new ShaderAnswerException("No shader found. It needs a mainImage(out vec4 fragColor, in vec2 fragCoord) function.");
        return Build(null, null, code, null, false);
    }

    private static ShaderDraft Build(string? title, string? description, string code, string? mood, bool usesPointer)
    {
        string normalized = Normalize(code);
        return new ShaderDraft(
            Title: Short(title, 40) ?? "Untitled",
            Description: Short(description, 200) ?? "",
            Code: normalized,
            Mood: mood is not null && Moods.Contains(mood.Trim().ToLowerInvariant()) ? mood.Trim().ToLowerInvariant() : "abstract",
            // Trust the code over the claim: the runtime decides from the same uniforms.
            UsesPointer: usesPointer || PointerUse().IsMatch(normalized));
    }

    /// <summary>
    /// Removes what Wavelit adds itself and models often include anyway: #version, precision lines and
    /// re-declarations of Wavelit's uniforms. Anything else is left for the lint and the compiler.
    /// </summary>
    public static string Normalize(string code)
    {
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => !VersionLine().IsMatch(l) && !PrecisionLine().IsMatch(l) && !OurUniform().IsMatch(l));
        string result = string.Join('\n', lines).Trim();
        return result.Length > PastedShader.MaxLength ? result[..PastedShader.MaxLength] : result;
    }

    private static string StripFenceLines(string text)
        => string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => !l.TrimStart().StartsWith("```", StringComparison.Ordinal)));

    private static string? Str(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Short(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string one = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim().Trim('"');
        return one.Length > max ? one[..max].TrimEnd() + "…" : one;
    }

    [GeneratedRegex(@"```[ \t]*(?:glsl|frag|c|cpp|hlsl)?[ \t]*\r?\n(?<code>.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\s*#\s*version\b")]
    private static partial Regex VersionLine();

    [GeneratedRegex(@"^\s*precision\s+(lowp|mediump|highp)\s+\w+\s*;\s*$")]
    private static partial Regex PrecisionLine();

    [GeneratedRegex(@"^\s*uniform\s+\w+\s+(iResolution|iTime|iTimeDelta|iFrameRate|iFrame|iChannelTime|iChannelResolution|iMouse|iDate|iSampleRate|iChannel[0-3]|iFocus|iViewport|iPointer|iClick|iTrail|wavelitTint|wavelitStyle)\b[^;]*;\s*$")]
    private static partial Regex OurUniform();

    [GeneratedRegex(@"\b(iPointer|iTrail|iClick|iMouse|ilPtr|ilPresence|ilPush|ilSwirl)\b")]
    private static partial Regex PointerUse();
}
