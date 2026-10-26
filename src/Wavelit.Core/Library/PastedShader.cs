using System.Text.RegularExpressions;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Library;

/// <summary>
/// "Paste a shader": GLSL in the Shadertoy <c>mainImage</c> convention, copied from any site, used without
/// an API key. The code is data for <c>gl.shaderSource</c> only; the WebGL compiler is the validator.
/// </summary>
public static partial class PastedShader
{
    public const int MaxLength = 64 * 1024;

    /// <summary>Checks pasted code before it is saved; returns a user-facing problem or null.</summary>
    public static string? Validate(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "Paste some shader code first.";
        if (code.Length > MaxLength) return "That shader is too long (64 KB max).";
        if (!MainImage().IsMatch(code))
            return "Wavelit needs a Shadertoy-style shader with a mainImage(out vec4, in vec2) function.";
        if (code.Contains("#version", StringComparison.Ordinal))
            return "Remove the #version line; Wavelit adds it for you.";
        return null;
    }

    public static WallpaperItem CreateItem(string title, string code)
    {
        string normalized = code.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return new WallpaperItem
        {
            Id = "pasted-" + MediaCache.FileNameFor(normalized, ""),
            Kind = WallpaperKind.Shader,
            Source = WallpaperSources.Pasted,
            Title = string.IsNullOrWhiteSpace(title) ? "My shader" : title.Trim(),
            Author = "You",
            ShaderCode = normalized,
        };
    }

    public static ShaderProgram Program(WallpaperItem item)
    {
        if (Validate(item.ShaderCode) is { } problem) throw new LibrarySourceException(problem);
        return new ShaderProgram(item.Title, [new ShaderPass("image", item.ShaderCode!, null, [])]);
    }

    [GeneratedRegex(@"void\s+mainImage\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex MainImage();
}
