using Wavelit.Core.Storage;

namespace Wavelit.Core.Create.Shaders;

/// <summary>One conversation with a model about one shader (the first draft plus its fix rounds).</summary>
public interface IShaderSession
{
    /// <summary>The first draft (<paramref name="feedback"/> null) or a fixed one after Wavelit's check failed.</summary>
    Task<ShaderDraft> WriteAsync(string? feedback, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>A model that writes shaders (Claude, Cloudflare Workers AI, …).</summary>
public interface IShaderAuthor
{
    string Id { get; }
    /// <summary>Shown in the Studio's "made by" picker.</summary>
    string Label { get; }
    /// <summary>The model id, kept with each creation.</summary>
    string Model { get; }
    bool IsConfigured { get; }
    IShaderSession Start(ShaderRequest request);
}

/// <summary>Compiles and measures a shader in the real runtime (the Studio page's WebView2).</summary>
public interface IShaderLab
{
    Task<LabReport> CheckAsync(string code, ShaderKind kind, CancellationToken ct);
}

/// <param name="Error">Compile/link log (line numbers relative to the user's code), or a crash message.</param>
/// <param name="FrameMs">Median GPU time per frame, scaled to 1920×1080.</param>
/// <param name="P95Ms">95th percentile of the same.</param>
/// <param name="Blank">The image is empty: all black, all one colour, or NaN.</param>
/// <param name="Thumbnail">PNG preview (JPEG-sized, about 384×216), if rendered.</param>
public sealed record LabReport(bool Compiled, string? Error, double FrameMs, double P95Ms, bool Blank, byte[]? Thumbnail)
{
    public static LabReport Failed(string error) => new(false, error, 0, 0, false, null);
}

/// <summary>Why a shader was rejected, in words for the user and for the model.</summary>
public enum CheckFailure { None, Lint, Compile, TooSlow, Blank }

public sealed record CheckResult(CheckFailure Failure, string? Problem, LabReport? Report)
{
    public bool Passed => Failure == CheckFailure.None;
}

public sealed record StudioProgress(string Stage, int Round);

/// <summary>The outcome of one Studio run.</summary>
/// <param name="Draft">The last shader the model wrote (shown even when it failed, so "Show code" works).</param>
/// <param name="Problem">Why it failed, for the user (null when it passed).</param>
public sealed record StudioOutcome(ShaderDraft? Draft, LabReport? Report, string? Problem, int FixRounds, ShaderUsage? Usage)
{
    public bool Succeeded => Problem is null && Draft is not null && Report is not null;
}

/// <summary>
/// The Studio loop: the model writes, Wavelit lints, compiles and times it in the real runtime, and sends any
/// problem back to the model, up to <see cref="MaxFixRounds"/> times. Nothing is ever applied from here;
/// the user previews and decides.
/// </summary>
public sealed class ShaderStudio(IShaderLab lab, Func<double>? budgetMs = null)
{
    public const int MaxFixRounds = 3;
    /// <summary>p95 per frame at 1080p, on the user's GPU, above which a shader is rejected (plan §21.7).</summary>
    public const double DefaultBudgetMs = 3.0;
    /// <summary>The Saver preset (laptops, battery) is stricter.</summary>
    public const double SaverBudgetMs = 2.0;

    public double BudgetMs => budgetMs?.Invoke() ?? DefaultBudgetMs;

    /// <summary>The Saver preset (30 fps or less at half resolution) gets the stricter budget.</summary>
    public static double BudgetFor(AppSettings settings)
        => settings.ShaderFpsCap <= 30 && settings.ShaderRenderScale <= 0.5 ? SaverBudgetMs : DefaultBudgetMs;

    public async Task<StudioOutcome> CreateAsync(IShaderAuthor author, ShaderRequest request, IProgress<StudioProgress>? progress, CancellationToken ct)
    {
        var session = author.Start(request);
        ShaderDraft? draft = null;
        CheckResult? check = null;
        ShaderUsage? usage = null;
        string? feedback = null;
        for (int round = 0; round <= MaxFixRounds; round++)
        {
            progress?.Report(new StudioProgress(round == 0 ? "Writing your shader…" : FixingStage(check!), round));
            int current = round;
            var writing = new Progress<string>(s => progress?.Report(new StudioProgress(s, current)));
            draft = await session.WriteAsync(feedback, writing, ct).ConfigureAwait(false);
            usage = draft.Usage is null ? usage : draft.Usage.Add(usage);

            progress?.Report(new StudioProgress("Checking it runs smoothly…", round));
            check = await CheckAsync(draft.Code, request.Kind, ct).ConfigureAwait(false);
            if (check.Passed) return new StudioOutcome(draft, check.Report, null, round, usage);
            feedback = ShaderPrompt.FixMessage(check.Problem!);
        }
        return new StudioOutcome(draft, check?.Report, GiveUpMessage(check!), MaxFixRounds, usage);
    }

    /// <summary>Lint, compile and time one shader (also used for pasted code).</summary>
    public async Task<CheckResult> CheckAsync(string code, ShaderKind kind, CancellationToken ct)
    {
        var problems = ShaderLint.Check(code, kind);
        if (problems.Count > 0) return new CheckResult(CheckFailure.Lint, string.Join("\n", problems), null);

        var report = await lab.CheckAsync(code, kind, ct).ConfigureAwait(false);
        if (!report.Compiled)
            return new CheckResult(CheckFailure.Compile, "It didn't compile. WebGL said: " + (report.Error ?? "unknown error"), report);
        double budget = BudgetMs;
        if (report.P95Ms > budget)
            return new CheckResult(CheckFailure.TooSlow,
                $"It's too slow for a wallpaper: {report.P95Ms:0.0} ms per frame at 1920x1080 (the limit is {budget:0} ms on this PC, the aim under 1.5 ms). " +
                "Make it cheaper: fewer loop iterations, fewer fbm octaves, no nested loops over many items.", report);
        if (report.Blank)
            return new CheckResult(CheckFailure.Blank,
                kind == ShaderKind.Overlay
                    ? "It draws nothing visible (fully transparent, or NaN). Make sure particles or light actually appear with the default controls."
                    : "It renders a blank image (all black, one flat colour, or NaN). Check the maths and the colour output.", report);
        return new CheckResult(CheckFailure.None, null, report);
    }

    private static string FixingStage(CheckResult check) => check.Failure switch
    {
        CheckFailure.TooSlow => "Making it lighter on your graphics card…",
        CheckFailure.Blank => "Fixing an empty picture…",
        _ => "Fixing a small error…",
    };

    private static string GiveUpMessage(CheckResult check) => check.Failure switch
    {
        CheckFailure.TooSlow => $"Couldn't make it light enough to run as a wallpaper after {MaxFixRounds} tries. Try a simpler idea, or Refine with “simpler”.",
        CheckFailure.Blank => $"The AI's shader kept coming out empty after {MaxFixRounds} tries. Try describing it differently.",
        _ => $"Couldn't get a working shader after {MaxFixRounds} tries. Try simpler wording, or another AI.",
    };
}
