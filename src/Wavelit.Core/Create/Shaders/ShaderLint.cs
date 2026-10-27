using System.Globalization;
using System.Text.RegularExpressions;
using Wavelit.Core.Library;

namespace Wavelit.Core.Create.Shaders;

/// <summary>
/// Static checks on AI-written GLSL before it is compiled. The timed probe is the real gate for GPU cost;
/// this only rules out what can never be allowed (unbounded loops, textures, preprocessor tricks) and
/// gives the model a plain reason it can fix.
/// </summary>
public static partial class ShaderLint
{
    /// <summary>Largest constant loop bound accepted (the prompt asks for 64 or fewer).</summary>
    public const int MaxLoopBound = 256;
    /// <summary>Most iterations per pixel accepted across nested loops (calls into looping helpers aside).</summary>
    public const int MaxNestedIterations = 1024;

    /// <summary>Problems found, in plain English (empty = passes).</summary>
    public static IReadOnlyList<string> Check(string code, ShaderKind kind)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(code)) return ["There is no code."];
        if (code.Length > PastedShader.MaxLength) problems.Add("The shader is longer than 64 KB.");

        string src = StripComments(code);
        if (!MainImage().IsMatch(src)) problems.Add("It needs a function `void mainImage(out vec4 fragColor, in vec2 fragCoord)`.");
        if (Directive().Match(src) is { Success: true } d)
            problems.Add($"Remove the preprocessor line `{d.Value.Trim()}` (Wavelit adds #version itself; use const instead of #define).");
        if (WhileLoop().IsMatch(src) || DoLoop().IsMatch(src))
            problems.Add("Don't use while or do loops; use a for loop with a constant bound.");
        if (Texture().IsMatch(src)) problems.Add("Don't use textures (iChannel0..3 and texture() aren't available).");
        // An overlay must apply the user's opacity: through ilOut, or (for coloured premultiplied output) wavelitStyle.w.
        if (kind == ShaderKind.Overlay && !src.Contains("ilOut(", StringComparison.Ordinal) && !src.Contains("wavelitStyle.w", StringComparison.Ordinal))
            problems.Add("An overlay must end with fragColor = ilOut(colour, alpha).");

        var constants = ConstInts(src);
        bool loopsOk = true;
        foreach (Match m in ForHeader().Matches(src))
        {
            if (LoopProblem(m.Groups["init"].Value, m.Groups["cond"].Value, constants) is { } p)
            {
                problems.Add(p);
                loopsOk = false;
                break; // one loop message is enough to act on
            }
        }
        if (loopsOk && NestedIterations(src, constants) is var n and > MaxNestedIterations)
            problems.Add($"Nested loops run about {n:0} times per pixel; keep it under 256 (fewer items, or one loop instead of two).");
        return problems;
    }

    /// <summary>The largest product of iteration counts along any chain of lexically nested for loops.</summary>
    public static double NestedIterations(string src, IReadOnlyDictionary<string, double>? constants = null)
    {
        constants ??= ConstInts(src);
        var loops = new List<(int Start, int BodyStart, int BodyEnd, double Count)>();
        foreach (Match m in ForHeader().Matches(src))
        {
            int bodyStart = m.Index + m.Length;
            loops.Add((m.Index, bodyStart, BodyEnd(src, bodyStart), Iterations(m, constants)));
        }
        double max = 0;
        foreach (var loop in loops)
        {
            double product = loop.Count;
            foreach (var outer in loops)
                if (outer.Start != loop.Start && loop.Start >= outer.BodyStart && loop.Start < outer.BodyEnd) product *= outer.Count;
            max = Math.Max(max, product);
        }
        return max;
    }

    private static double Iterations(Match header, IReadOnlyDictionary<string, double> constants)
    {
        var c = Comparison().Match(header.Groups["cond"].Value.Trim());
        if (!c.Success) return MaxLoopBound;
        double? bound = Value(c.Groups["r"].Value, constants) ?? Value(c.Groups["l"].Value, constants);
        if (bound is not { } b) return MaxLoopBound;
        var init = InitValue().Match(header.Groups["init"].Value);
        double from = init.Success && Value(init.Groups["v"].Value, constants) is { } f ? f : 0;
        double step = StepValue().Match(header.Groups["step"].Value) is { Success: true } s
            && Value(s.Groups["v"].Value, constants) is { } v && Math.Abs(v) > 1e-6 ? Math.Abs(v) : 1;
        bool inclusive = c.Groups["op"].Value is "<=" or ">=";
        return Math.Max(1, Math.Floor(Math.Abs(b - from) / step) + (inclusive ? 1 : 0));
    }

    /// <summary>End of a loop body starting at <paramref name="pos"/>: a { block }, another for, or one statement.</summary>
    private static int BodyEnd(string src, int pos)
    {
        while (pos < src.Length && char.IsWhiteSpace(src[pos])) pos++;
        if (pos >= src.Length) return pos;
        if (src[pos] == '{')
        {
            int depth = 0;
            for (int i = pos; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return i + 1;
            }
            return src.Length;
        }
        var inner = ForHeader().Match(src, pos);
        if (inner.Success && inner.Index == pos) return BodyEnd(src, pos + inner.Length);
        int paren = 0, brace = 0;
        for (int i = pos; i < src.Length; i++)
        {
            char ch = src[i];
            if (ch == '(') paren++;
            else if (ch == ')') paren--;
            else if (ch == '{') brace++;
            else if (ch == '}') { if (--brace < 0) return i; }
            else if (ch == ';' && paren == 0 && brace == 0) return i + 1;
        }
        return src.Length;
    }

    /// <summary>
    /// A loop must compare its counter with a constant (a literal or a const) no larger than
    /// <see cref="MaxLoopBound"/>; a counting-down loop must also start from one.
    /// </summary>
    private static string? LoopProblem(string init, string cond, IReadOnlyDictionary<string, double> constants)
    {
        var c = Comparison().Match(cond.Trim());
        if (!c.Success) return $"Loop condition `{cond.Trim()}` must compare the counter with a constant, e.g. `i < 64`.";
        string left = c.Groups["l"].Value, op = c.Groups["op"].Value, right = c.Groups["r"].Value;

        // Normalise to "counter op bound".
        string counter = left, bound = right;
        if (!IsIdentifier(left) || Value(left, constants) is not null)
        {
            counter = right;
            bound = left;
            op = op switch { "<" => ">", "<=" => ">=", ">" => "<", ">=" => "<=", _ => op };
        }
        if (!IsIdentifier(counter)) return $"Loop condition `{cond.Trim()}` must compare the counter with a constant.";
        if (Value(bound, constants) is not { } limit)
            return $"Loop bound `{bound}` must be a constant (a number or a const), not a variable, so the loop can't run away.";

        if (op is "<" or "<=")
        {
            if (Math.Abs(limit) > MaxLoopBound) return $"Loop bound {bound} is too high; keep loops to {ShaderPrompt.SuggestedLoopBound} iterations or fewer.";
            return null;
        }
        if (op is ">" or ">=")
        {
            // Counting down: the start must be a constant too.
            var start = InitValue().Match(init);
            if (!start.Success || Value(start.Groups["v"].Value, constants) is not { } from)
                return "A loop that counts down must start from a constant.";
            if (Math.Abs(from - limit) > MaxLoopBound) return $"That loop runs too many times; keep loops to {ShaderPrompt.SuggestedLoopBound} iterations or fewer.";
            return null;
        }
        return $"Loop condition `{cond.Trim()}` must use <, <=, > or >= with a constant bound.";
    }

    private static bool IsIdentifier(string s) => Identifier().IsMatch(s);

    private static double? Value(string token, IReadOnlyDictionary<string, double> constants)
    {
        string t = token.Trim();
        if (double.TryParse(t.TrimEnd('f', 'F', 'u', 'U'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
        if (t.StartsWith("float(", StringComparison.Ordinal) && t.EndsWith(')')) return Value(t[6..^1], constants);
        return constants.TryGetValue(t, out double c) ? c : null;
    }

    private static Dictionary<string, double> ConstInts(string src)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (Match m in ConstDecl().Matches(src))
        {
            if (double.TryParse(m.Groups["v"].Value.TrimEnd('f', 'F', 'u', 'U'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                map[m.Groups["n"].Value] = v;
        }
        return map;
    }

    public static string StripComments(string code)
        => LineComment().Replace(BlockComment().Replace(code, " "), "");

    [GeneratedRegex(@"\bvoid\s+mainImage\s*\(")]
    private static partial Regex MainImage();

    [GeneratedRegex(@"(?m)^[ \t]*#[^\n]*")]
    private static partial Regex Directive();

    [GeneratedRegex(@"\bwhile\s*\(")]
    private static partial Regex WhileLoop();

    [GeneratedRegex(@"\bdo\s*\{")]
    private static partial Regex DoLoop();

    [GeneratedRegex(@"\b(iChannel[0-3]|texture|textureLod|texelFetch|textureGrad|textureSize)\s*[\(\[,;)]|\biChannel[0-3]\b")]
    private static partial Regex Texture();

    [GeneratedRegex(@"\bfor\s*\((?<init>[^;]*);(?<cond>[^;]*);(?<step>[^)]*)\)")]
    private static partial Regex ForHeader();

    [GeneratedRegex(@"^(?<l>[A-Za-z_][\w]*|[-+]?[\d.]+(?:[eE][-+]?\d+)?[fFuU]?|float\([^)]*\))\s*(?<op><=|>=|<|>|!=)\s*(?<r>[A-Za-z_][\w]*|[-+]?[\d.]+(?:[eE][-+]?\d+)?[fFuU]?|float\([^)]*\))$")]
    private static partial Regex Comparison();

    [GeneratedRegex(@"=\s*(?<v>[-+]?[\d.]+(?:[eE][-+]?\d+)?[fFuU]?|[A-Za-z_]\w*)\s*$")]
    private static partial Regex InitValue();

    [GeneratedRegex(@"(?:\+=|-=)\s*(?<v>[-+]?[\d.]+(?:[eE][-+]?\d+)?[fFuU]?|[A-Za-z_]\w*)\s*$")]
    private static partial Regex StepValue();

    [GeneratedRegex(@"\bconst\s+(?:int|float|uint)\s+(?<n>[A-Za-z_]\w*)\s*=\s*(?<v>[-+]?[\d.]+(?:[eE][-+]?\d+)?[fFuU]?)\s*;")]
    private static partial Regex ConstDecl();

    [GeneratedRegex(@"^[A-Za-z_]\w*$")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();
}
