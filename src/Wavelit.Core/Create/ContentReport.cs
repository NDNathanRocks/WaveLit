using System.Text;

namespace Wavelit.Core.Create;

/// <summary>
/// Reporting AI-made content (Microsoft Store policy 11.16, plan §21.6). Builds a pre-filled GitHub issue that
/// opens in the user's browser; nothing is sent by Wavelit itself, and the code or prompt goes in only when the
/// user ticks the box. The maintainers triage reports there (docs/store-reviewer-notes.md).
/// </summary>
public static class ContentReport
{
    /// <summary>Where reports go: the project's GitHub issues.</summary>
    public const string IssuesUrl = "https://github.com/NDNathanRocks/WaveLit/issues/new";
    private const int MaxUrl = 7000;   // browsers and GitHub cope with ~8 KB

    public static readonly IReadOnlyList<(string Id, string Label)> Reasons =
    [
        ("inappropriate", "Inappropriate or unsafe content"),
        ("flashing", "Flashing, or hard to look at"),
        ("broken", "Broken, or slows my PC down"),
        ("other", "Something else"),
    ];

    /// <param name="madeWith">The model or generator (e.g. claude-opus-5-5, local-sdxl).</param>
    /// <param name="details">The prompt and code (or picture prompt); included only if the user agreed.</param>
    public static string IssueUrl(string reason, string? note, string title, string madeWith, string? details, string appVersion)
    {
        string label = Reasons.FirstOrDefault(r => r.Id == reason).Label ?? "Something else";
        var body = new StringBuilder()
            .Append("**What's wrong:** ").Append(label).Append("\n\n")
            .Append("**Made with:** ").Append(madeWith).Append("\n")
            .Append("**Wavelit:** ").Append(appVersion).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(note)) body.Append("**Note:** ").Append(note.Trim() is { Length: > 500 } n ? n[..500] : note.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(details)) body.Append("**Details (shared by the reporter):**\n```\n").Append(details.Trim()).Append("\n```\n");
        string Url(string b) => $"{IssuesUrl}?labels=ai-content-report&title={Uri.EscapeDataString("AI content report: " + title)}&body={Uri.EscapeDataString(b)}";
        string url = Url(body.ToString());
        // Too long for a link: drop the attached details rather than the report.
        if (url.Length > MaxUrl && details is not null) url = Url(body.ToString()[..Math.Min(body.Length, 1500)] + "\n\n(Details were too long to include.)");
        return url;
    }
}
