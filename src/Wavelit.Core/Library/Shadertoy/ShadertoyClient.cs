using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Library.Shadertoy;

/// <summary>Parsed Shadertoy shader (only the parts Wavelit uses).</summary>
public sealed record ShadertoyShader(
    string Id, string Name, string Author, string Description, int Likes,
    IReadOnlyList<ShadertoyPass> Passes)
{
    public string PageUrl => $"https://www.shadertoy.com/view/{Id}";
    public string ThumbnailUrl => $"https://www.shadertoy.com/media/shaders/{Id}.jpg";
}

public sealed record ShadertoyPass(string Type, string Name, string Code, IReadOnlyList<ShadertoyInput> Inputs, IReadOnlyList<string> OutputIds);

public sealed record ShadertoyInput(int Channel, string CType, string Id, string? Src, string Filter, string Wrap, bool VFlip);

/// <summary>Shadertoy API (https://www.shadertoy.com/howto). Only shaders published as "Public + API" are visible.</summary>
public sealed partial class ShadertoyClient(
    IHttpClientFactory httpFactory, SecretStore secrets, AppPaths paths, ContentGuard guard, ILogger<ShadertoyClient> logger)
{
    public const string HttpClientName = "shadertoy";
    public const string DefaultLicense = "CC BY-NC-SA 3.0 (unless the author states otherwise)";
    private static readonly HashSet<string> SupportedInputs = ["buffer", "texture", "keyboard"];
    private static readonly HashSet<string> SupportedPasses = ["image", "buffer", "common", "sound"];

    [GeneratedRegex("^[A-Za-z0-9]{6,8}$")]
    private static partial Regex ShaderIdPattern();

    [GeneratedRegex(@"^/media/a/[A-Za-z0-9]+\.(png|jpg|jpeg)$")]
    private static partial Regex MediaPathPattern();

    public bool IsConfigured => secrets.Has(SecretNames.ShadertoyApiKey);

    /// <summary>Returns shader ids for a query (Shadertoy's search returns ids only).</summary>
    public async Task<(IReadOnlyList<string> Ids, int Total)> SearchIdsAsync(string query, int from, int count, CancellationToken ct)
    {
        string key = secrets.Get(SecretNames.ShadertoyApiKey) ?? throw new LibrarySourceException("Add a Shadertoy API key in Settings to search shaders.");
        if (guard.IsBlocked(query)) return ([], 0);

        string path = string.IsNullOrWhiteSpace(query)
            ? $"api/v1/shaders?sort=popular&from={from}&num={count}&key={Uri.EscapeDataString(key)}"
            : $"api/v1/shaders/query/{Uri.EscapeDataString(query.Trim())}?sort=popular&from={from}&num={count}&key={Uri.EscapeDataString(key)}";

        using var doc = await GetJsonAsync(path, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        int total = root.TryGetProperty("Shaders", out var t) && t.TryGetInt32(out int n) ? n : 0;
        var ids = new List<string>();
        if (root.TryGetProperty("Results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in results.EnumerateArray())
                if (r.GetString() is { } id && ShaderIdPattern().IsMatch(id)) ids.Add(id);
        }
        return (ids, total);
    }

    public async Task<bool> TestKeyAsync(string key, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync($"api/v1/shaders?num=1&key={Uri.EscapeDataString(key)}", ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("Shaders", out _);
        }
        catch (LibrarySourceException)
        {
            return false;
        }
    }

    /// <summary>Fetches shader details, using the on-disk cache when available.</summary>
    public async Task<ShadertoyShader> GetShaderAsync(string id, CancellationToken ct)
    {
        if (!ShaderIdPattern().IsMatch(id)) throw new LibrarySourceException("Invalid shader id.");
        string cacheFile = Path.Combine(paths.ShadertoyCache, id + ".json");
        if (File.Exists(cacheFile))
        {
            try
            {
                using var cached = JsonDocument.Parse(await File.ReadAllBytesAsync(cacheFile, ct).ConfigureAwait(false));
                return Parse(cached.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or LibrarySourceException)
            {
                File.Delete(cacheFile);
            }
        }

        string key = secrets.Get(SecretNames.ShadertoyApiKey) ?? throw new LibrarySourceException("Add a Shadertoy API key in Settings.");
        using var doc = await GetJsonAsync($"api/v1/shaders/{id}?key={Uri.EscapeDataString(key)}", ct).ConfigureAwait(false);
        var shader = Parse(doc.RootElement);
        await File.WriteAllTextAsync(cacheFile, doc.RootElement.GetRawText(), ct).ConfigureAwait(false);
        return shader;
    }

    /// <summary>Why a shader can't run in Wavelit, or null if it can.</summary>
    public static string? UnsupportedReason(ShadertoyShader shader)
    {
        foreach (var pass in shader.Passes)
        {
            if (!SupportedPasses.Contains(pass.Type)) return $"uses a {pass.Type} pass";
            if (pass.Type == "sound") continue;
            foreach (var input in pass.Inputs)
                if (!SupportedInputs.Contains(input.CType)) return $"needs {input.CType} input";
        }
        return shader.Passes.Any(p => p.Type == "image") ? null : "has no image pass";
    }

    public static WallpaperItem ToItem(ShadertoyShader s) => new()
    {
        Id = $"shadertoy-{s.Id}",
        Kind = WallpaperKind.Shader,
        Source = WallpaperSources.Shadertoy,
        Title = s.Name,
        Author = s.Author,
        AuthorUrl = $"https://www.shadertoy.com/user/{Uri.EscapeDataString(s.Author)}",
        PageUrl = s.PageUrl,
        ThumbnailUrl = s.ThumbnailUrl,
        License = DefaultLicense,
        ShaderId = s.Id,
    };

    /// <summary>Builds a renderer program, downloading any texture inputs into the cache first.</summary>
    public async Task<ShaderProgram> BuildProgramAsync(string id, CancellationToken ct)
    {
        var shader = await GetShaderAsync(id, ct).ConfigureAwait(false);
        if (UnsupportedReason(shader) is { } reason)
            throw new LibrarySourceException($"This shader {reason}, which Wavelit doesn't support yet.");

        var passes = new List<ShaderPass>();
        foreach (var pass in shader.Passes.Where(p => p.Type != "sound"))
        {
            var inputs = new List<ShaderInput>();
            foreach (var input in pass.Inputs)
            {
                string? url = null;
                if (input.CType == "texture")
                    url = await EnsureTextureAsync(input.Src, ct).ConfigureAwait(false);
                inputs.Add(new ShaderInput(input.Channel, input.CType, input.CType == "buffer" ? input.Id : null,
                    url, input.Filter, input.Wrap, input.VFlip));
            }
            passes.Add(new ShaderPass(pass.Type, pass.Code, pass.OutputIds.FirstOrDefault(), inputs));
        }

        // Shadertoy executes buffers in A→D order, then the image pass.
        var ordered = passes.Where(p => p.Type == "common")
            .Concat(passes.Where(p => p.Type == "buffer"))
            .Concat(passes.Where(p => p.Type == "image"))
            .ToList();
        return new ShaderProgram(shader.Name, ordered);
    }

    private async Task<string> EnsureTextureAsync(string? src, CancellationToken ct)
    {
        if (src is null || !MediaPathPattern().IsMatch(src))
            throw new LibrarySourceException("This shader uses a texture Wavelit can't load.");

        string fileName = Path.GetFileName(src);
        string localPath = Path.Combine(paths.ShadertoyMedia, fileName);
        if (!File.Exists(localPath))
        {
            Directory.CreateDirectory(paths.ShadertoyMedia);
            var client = httpFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(src.TrimStart('/'), ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string tmp = localPath + ".part";
            await using (var fs = File.Create(tmp))
                await response.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            File.Move(tmp, localPath, overwrite: true);
        }
        return WebHosts.CacheUrl($"shadertoy/media/{fileName}");
    }

    private async Task<JsonDocument> GetJsonAsync(string pathAndQuery, CancellationToken ct)
    {
        var client = httpFactory.CreateClient(HttpClientName);
        try
        {
            using var response = await client.GetAsync(pathAndQuery, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new LibrarySourceException("Shadertoy is rate limiting requests. Try again shortly.");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Error", out var err))
            {
                string message = err.GetString() ?? "Unknown error";
                doc.Dispose();
                throw new LibrarySourceException(message.Contains("key", StringComparison.OrdinalIgnoreCase)
                    ? "Shadertoy rejected the API key. Check it in Settings."
                    : $"Shadertoy: {message}");
            }
            return doc;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Shadertoy request failed ({Path})", Redact.Url(pathAndQuery));
            throw new LibrarySourceException("Couldn't reach Shadertoy. Check your internet connection.", ex);
        }
        catch (JsonException ex)
        {
            throw new LibrarySourceException("Shadertoy returned data Wavelit couldn't read.", ex);
        }
    }

    internal static ShadertoyShader Parse(JsonElement root)
    {
        if (!root.TryGetProperty("Shader", out var shader))
            throw new LibrarySourceException("Shader not found.");
        var info = shader.GetProperty("info");
        var passes = new List<ShadertoyPass>();
        foreach (var rp in shader.GetProperty("renderpass").EnumerateArray())
        {
            var inputs = new List<ShadertoyInput>();
            if (rp.TryGetProperty("inputs", out var ins))
            {
                foreach (var i in ins.EnumerateArray())
                {
                    var sampler = i.TryGetProperty("sampler", out var s) ? s : default;
                    inputs.Add(new ShadertoyInput(
                        Channel: Int(i, "channel"),
                        CType: Str(i, "ctype") ?? Str(i, "type") ?? "",
                        Id: Str(i, "id") ?? "",
                        Src: Str(i, "src") ?? Str(i, "filepath"),
                        Filter: (sampler.ValueKind == JsonValueKind.Object ? Str(sampler, "filter") : null) ?? "mipmap",
                        Wrap: (sampler.ValueKind == JsonValueKind.Object ? Str(sampler, "wrap") : null) ?? "repeat",
                        VFlip: sampler.ValueKind == JsonValueKind.Object && Bool(sampler, "vflip")));
                }
            }
            var outputs = new List<string>();
            if (rp.TryGetProperty("outputs", out var outs))
                foreach (var o in outs.EnumerateArray())
                    if (Str(o, "id") is { } oid) outputs.Add(oid);

            passes.Add(new ShadertoyPass(Str(rp, "type") ?? "", Str(rp, "name") ?? "", Str(rp, "code") ?? "", inputs, outputs));
        }

        return new ShadertoyShader(
            Str(info, "id") ?? "", Str(info, "name") ?? "Untitled", Str(info, "username") ?? "unknown",
            Str(info, "description") ?? "", Int(info, "likes"), passes);
    }

    // Shadertoy's JSON mixes strings and numbers for ids, channels and flags.
    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v)
        ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        }
        : null;

    private static int Int(JsonElement e, string name)
        => int.TryParse(Str(e, name), out int n) ? n : 0;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
}
