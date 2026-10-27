using System.Globalization;
using System.Text.RegularExpressions;

namespace Wavelit.Core.Create.Shaders;

/// <summary>One slider an AI shader offers (plan §21.5), read in GLSL as iParams.x..w in order.</summary>
public sealed record ShaderParam(string Name, double Min, double Max, double Default)
{
    /// <summary>"glow" → "Glow".</summary>
    public string Label => Name.Length == 0 ? Name : char.ToUpperInvariant(Name[0]) + Name[1..].Replace('-', ' ').Replace('_', ' ');
}

/// <summary>
/// Parses the optional first-line declaration <c>// @params speed 0.2 3 1 | density 0 1 0.5</c>. Must agree with
/// parseParams in Web/shader-runtime.js: up to 4 entries; a malformed entry is skipped (never an error).
/// </summary>
public static partial class ShaderParams
{
    public const int Max = 4;

    public static IReadOnlyList<ShaderParam> Parse(string? code)
    {
        if (code is null || Declaration().Match(code) is not { Success: true } m) return [];
        var list = new List<ShaderParam>();
        foreach (string part in m.Groups[1].Value.Split('|').Take(Max))
        {
            var f = part.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 4 || !Name().IsMatch(f[0])) continue;
            if (!TryNum(f[1], out double min) || !TryNum(f[2], out double max) || !TryNum(f[3], out double def) || !(max > min)) continue;
            list.Add(new ShaderParam(f[0], min, max, Math.Clamp(def, min, max)));
        }
        return list;
    }

    /// <summary>Chosen values, clamped to each slider's range (missing or bad ones fall back to the default).</summary>
    public static double[] Clamp(IReadOnlyList<ShaderParam> declared, IReadOnlyList<double>? values)
    {
        var result = new double[declared.Count];
        for (int i = 0; i < declared.Count; i++)
        {
            var d = declared[i];
            double v = values is not null && i < values.Count && double.IsFinite(values[i]) ? values[i] : d.Default;
            result[i] = Math.Clamp(v, d.Min, d.Max);
        }
        return result;
    }

    private static bool TryNum(string s, out double v)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    [GeneratedRegex(@"^[ \t]*//[ \t]*@params[ \t]+(.+)$", RegexOptions.Multiline)]
    private static partial Regex Declaration();

    [GeneratedRegex(@"^[A-Za-z][\w-]{0,23}$")]
    private static partial Regex Name();
}
