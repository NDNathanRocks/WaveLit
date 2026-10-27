using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Create.Shaders;

/// <summary>
/// Cloudflare Workers AI, on the same free account the Picture tab uses: a free daily allowance, simpler
/// results than Claude. Uses Workers AI's OpenAI-compatible chat endpoint, so any text model in the catalogue
/// works; the answer is parsed as free text (a ```glsl block).
/// </summary>
public sealed partial class CloudflareShaderAuthor(IHttpClientFactory httpFactory, SecretStore secrets) : IShaderAuthor
{
    public const string AuthorId = "cloudflare";
    /// <summary>A large open coding-capable model on Workers AI (plan §19.2; check the catalogue if it is retired).</summary>
    public const string ModelId = "@cf/openai/gpt-oss-120b";

    private readonly IHttpClientFactory _http = httpFactory;
    private readonly SecretStore _secrets = secrets;

    public string Id => AuthorId;
    public string Label => "Cloudflare (free, simpler)";
    public string Model => ModelId;
    public bool IsConfigured => _secrets.Has(SecretNames.CloudflareAccountId) && _secrets.Has(SecretNames.CloudflareApiToken);

    public IShaderSession Start(ShaderRequest request) => new Session(this, request);

    /// <summary>The chat request body (public for tests).</summary>
    public static object BuildBody(ShaderRequest request, string? previousCode, string? feedback)
    {
        string user = ShaderPrompt.UserMessage(request);
        if (feedback is not null && previousCode is not null)
            user += "\n\nYour previous attempt:\n```glsl\n" + previousCode.Trim() + "\n```\n\n" + feedback + " Reply with one ```glsl code block.";
        return new
        {
            model = ModelId,
            max_tokens = 8000,
            messages = new object[]
            {
                new { role = "system", content = ShaderPrompt.ChatSystem },
                new { role = "user", content = user },
            },
        };
    }

    private sealed class Session(CloudflareShaderAuthor owner, ShaderRequest request) : IShaderSession
    {
        private string? _lastCode;

        public async Task<ShaderDraft> WriteAsync(string? feedback, IProgress<string>? progress, CancellationToken ct)
        {
            string? account = owner._secrets.Get(SecretNames.CloudflareAccountId);
            string? token = owner._secrets.Get(SecretNames.CloudflareApiToken);
            if (account is null || token is null) throw new GenerationException("Add your Cloudflare account ID and API token in Settings first.");
            if (!AccountId().IsMatch(account))
                throw new GenerationException("The Cloudflare account ID should be 32 letters and digits (find it in the Cloudflare dashboard).");

            using var ticker = ClaudeShaderAuthor.Ticker(progress, feedback is null ? "Cloudflare is writing your shader" : "Cloudflare is fixing it", ct);
            using var message = new HttpRequestMessage(HttpMethod.Post, $"https://api.cloudflare.com/client/v4/accounts/{account}/ai/v1/chat/completions")
            {
                Content = JsonContent.Create(BuildBody(request, _lastCode, feedback)),
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var client = owner._http.CreateClient(PollinationsGenerator.HttpClientName);
            try
            {
                using var response = await client.SendAsync(message, ct).ConfigureAwait(false);
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                    throw new GenerationException("Cloudflare rejected the token. Check it has the Workers AI permission.");
                if ((int)response.StatusCode == 429)
                    throw new GenerationException("You've used today's free Cloudflare allowance. It resets at midnight UTC.");
                if (!response.IsSuccessStatusCode)
                    throw new GenerationException($"Cloudflare couldn't write that shader ({(int)response.StatusCode}).");

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
                string text = AnswerText(doc.RootElement) ?? throw new GenerationException("Cloudflare's model didn't answer. Try again.");
                var draft = ShaderResult.ParseText(text);
                _lastCode = draft.Code;
                return draft with { Usage = UsageOf(doc.RootElement) };
            }
            catch (ShaderAnswerException ex)
            {
                throw new GenerationException("Cloudflare's model didn't return a shader: " + ex.Message, ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                throw new GenerationException("Couldn't reach Cloudflare. Check your connection.", ex);
            }
        }
    }

    /// <summary>choices[0].message.content (OpenAI shape), or Workers AI's native result.response.</summary>
    public static string? AnswerText(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            return c.GetString();
        if (root.TryGetProperty("result", out var r) && r.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.String)
            return resp.GetString();
        return null;
    }

    private static ShaderUsage? UsageOf(JsonElement root)
        => root.TryGetProperty("usage", out var u)
           && u.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt64(out long input)
           && u.TryGetProperty("completion_tokens", out var ctk) && ctk.TryGetInt64(out long output)
            ? new ShaderUsage(input, output, 0, 0)
            : null;

    [GeneratedRegex("^[0-9a-fA-F]{32}$")]
    private static partial Regex AccountId();
}
