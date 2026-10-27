namespace Wavelit.Core.Library;

/// <summary>Virtual host names the wallpaper WebView maps to local folders.</summary>
public static class WebHosts
{
    public const string App = "app.wavelit.local";
    public const string Cache = "cache.wavelit.local";
    public const string Local = "local.wavelit.local";

    public static string CacheUrl(string relativePath) => $"https://{Cache}/{relativePath.Replace('\\', '/')}";
}

/// <summary>Renderer-neutral shader description consumed by Web/shader-runtime.js.</summary>
/// <param name="Params">Slider values for a shader that declares <c>// @params</c> (null = its defaults).</param>
public sealed record ShaderProgram(string Name, IReadOnlyList<ShaderPass> Passes, IReadOnlyList<double>? Params = null);

/// <param name="Type">"image", "buffer" or "common".</param>
/// <param name="OutputId">For buffers: id other passes use to sample this buffer.</param>
public sealed record ShaderPass(string Type, string Code, string? OutputId, IReadOnlyList<ShaderInput> Inputs);

/// <param name="Kind">"buffer", "texture" or "keyboard".</param>
/// <param name="Filter">"nearest", "linear" or "mipmap".</param>
/// <param name="Wrap">"clamp" or "repeat".</param>
public sealed record ShaderInput(int Channel, string Kind, string? BufferId, string? Url, string Filter, string Wrap, bool VFlip);
