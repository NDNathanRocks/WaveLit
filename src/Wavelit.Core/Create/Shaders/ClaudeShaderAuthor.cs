using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Create.Shaders;

/// <summary>
/// Claude (Anthropic API) with the user's own key: the best shaders. Each request is one turn (the task, plus the
/// previous attempt and Wavelit's findings on a fix round), so the cached system prompt is the only shared prefix.
/// Uses structured output for the answer and the server-side refusal fallback where the model has one.
/// </summary>
public sealed class ClaudeShaderAuthor(SecretStore secrets, Func<string> model) : IShaderAuthor
{
    public const string AuthorId = "claude";
    public const string FallbackBeta = "server-side-fallback-2026-07-01";
    public const string Opus = "claude-opus-5-5", Sonnet = "claude-sonnet-5-5", Haiku = "claude-haiku-5-5";

    /// <summary>Models offered in Settings (id, label).</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> Models =
    [
        (Opus, "Claude Opus 5.5 (best, recommended)"),
        (Sonnet, "Claude Sonnet 5.5 (faster, cheaper)"),
        (Haiku, "Claude Haiku 5.5 (cheapest, simpler)"),
    ];

    private readonly Lock _gate = new();
    private AnthropicClient? _client;
    private string? _clientKey;

    public string Id => AuthorId;
    public string Label => "Claude (your key)";
    public string Model => Normalize(model());
    public bool IsConfigured => secrets.Has(SecretNames.AnthropicApiKey);

    public static string Normalize(string? id) => Models.Any(m => m.Id == id) ? id! : Opus;

    public IShaderSession Start(ShaderRequest request) => new Session(this, request, Model);

    /// <summary>The request for one turn (public for tests: the shape is what matters for caching and cost).</summary>
    public static MessageCreateParams BuildParams(string modelId, ShaderRequest request, string? previousCode, string? feedback)
    {
        string user = ShaderPrompt.UserMessage(request);
        if (feedback is not null && previousCode is not null)
            user += "\n\nYour previous attempt:\n```glsl\n" + previousCode.Trim() + "\n```\n\n" + feedback;

        var p = new MessageCreateParams
        {
            Model = modelId,
            MaxTokens = 16000,
            // The fixed contract and examples are the cached prefix; everything variable is in the user turn.
            System = new List<BetaTextBlockParam>
            {
                new() { Text = ShaderPrompt.System, CacheControl = new BetaCacheControlEphemeral() },
            },
            Thinking = new BetaThinkingConfigAdaptive(),
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.Medium,
                Format = new BetaJsonOutputFormat { Schema = Schema },
            },
            Messages = [new BetaUserMessageParam { Content = user }],
        };
        // Haiku has no server-side fallback (a fallback list is a 400 there).
        if (modelId != Haiku)
            p = p with { Betas = [FallbackBeta], Fallbacks = new Default() };
        return p;
    }

    private static readonly Dictionary<string, JsonElement> Schema = new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            plan = new { type = "string" },
            title = new { type = "string" },
            description = new { type = "string" },
            code = new { type = "string" },
            mood = new { type = "string", @enum = ShaderResult.Moods },
            usesPointer = new { type = "boolean" },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "plan", "title", "description", "code", "mood", "usesPointer" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    private AnthropicClient Client()
    {
        string key = secrets.Get(SecretNames.AnthropicApiKey)
            ?? throw new GenerationException("Add your Anthropic API key in Settings → AI Studio first.");
        lock (_gate)
        {
            if (_client is null || _clientKey != key)
            {
                (_client as IDisposable)?.Dispose();
                _client = new AnthropicClient { ApiKey = key, Timeout = TimeSpan.FromMinutes(6) };
                _clientKey = key;
            }
            return _client;
        }
    }

    /// <summary>Checks a key with a tiny request (Settings → Test).</summary>
    public async Task<string?> TestKeyAsync(string key, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = key, Timeout = TimeSpan.FromSeconds(30), MaxRetries = 0 };
        try
        {
            await client.Models.Retrieve(Opus, cancellationToken: ct).ConfigureAwait(false);
            return null;
        }
        catch (AnthropicUnauthorizedException) { return "Anthropic says that key isn't valid."; }
        catch (AnthropicForbiddenException) { return "That key isn't allowed to use Claude models."; }
        catch (AnthropicException ex) { return "Couldn't reach Anthropic: " + ex.Message; }
        catch (HttpRequestException) { return "Couldn't reach Anthropic. Check your internet connection."; }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    private sealed class Session(ClaudeShaderAuthor owner, ShaderRequest request, string modelId) : IShaderSession
    {
        private string? _lastCode;

        public async Task<ShaderDraft> WriteAsync(string? feedback, IProgress<string>? progress, CancellationToken ct)
        {
            var parameters = BuildParams(modelId, request, _lastCode, feedback);
            using var ticker = Ticker(progress, feedback is null ? "Claude is writing your shader" : "Claude is fixing it", ct);
            BetaMessage response;
            try
            {
                response = await owner.Client().Beta.Messages.Create(parameters, ct).ConfigureAwait(false);
            }
            catch (AnthropicUnauthorizedException) { throw new GenerationException("Anthropic says your API key isn't valid. Check it in Settings → AI Studio."); }
            catch (AnthropicForbiddenException) { throw new GenerationException("Your Anthropic key isn't allowed to use this model. Pick another model in Settings."); }
            catch (AnthropicRateLimitException) { throw new GenerationException("Anthropic is rate-limiting your key right now. Wait a minute and try again."); }
            catch (Anthropic5xxException) { throw new GenerationException("Anthropic's servers are busy. Try again in a moment."); }
            catch (AnthropicBadRequestException ex) { throw new GenerationException("Anthropic rejected the request: " + ex.Message); }
            catch (AnthropicApiException ex) { throw new GenerationException("Claude couldn't answer: " + ex.Message); }
            catch (AnthropicIOException) { throw new GenerationException("Couldn't reach Anthropic. Check your internet connection."); }

            if (response.StopReason?.Raw() == "refusal")
                throw new GenerationException("Claude declined this prompt. Try describing it differently.");

            // With a fallback, the answer is the text after the last model boundary.
            string text = "";
            foreach (var block in response.Content)
            {
                if (block.Value is BetaFallbackBlock) text = "";
                else if (block.Value is BetaTextBlock t) text += t.Text;
            }
            if (response.StopReason?.Raw() == "max_tokens" && !text.TrimEnd().EndsWith('}'))
                throw new GenerationException("Claude's answer was cut off. Try a simpler idea.");

            var draft = ShaderResult.ParseJson(text);
            _lastCode = draft.Code;
            var u = response.Usage;
            return draft with
            {
                Usage = new ShaderUsage(u.InputTokens, u.OutputTokens, u.CacheReadInputTokens ?? 0, u.CacheCreationInputTokens ?? 0),
            };
        }
    }

    /// <summary>"Claude is writing your shader… 0:12": something visibly happening during a long request.</summary>
    internal static IDisposable Ticker(IProgress<string>? progress, string text, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        progress?.Report(text + "…");
        var timer = new Timer(_ =>
        {
            if (ct.IsCancellationRequested) return;
            var e = DateTime.UtcNow - started;
            progress?.Report($"{text}… {(int)e.TotalMinutes}:{e.Seconds:00}");
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return timer;
    }
}
