namespace Wavelit.Core.Library;

/// <summary>
/// Defence-in-depth search filter. Pexels and Shadertoy content is already moderated; this just keeps
/// obviously adult queries from reaching them (a Microsoft Store certification expectation).
/// </summary>
public sealed class ContentGuard
{
    private static readonly string[] BlockedTerms =
    [
        "nsfw", "porn", "porno", "nude", "nudes", "naked", "sex", "sexy", "xxx", "hentai", "erotic",
        "boobs", "nipple", "onlyfans", "fetish", "lingerie", "stripper", "gore",
    ];

    public bool IsBlocked(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        var words = query.ToLowerInvariant().Split([' ', '-', '_', ',', '.', '+', '/'], StringSplitOptions.RemoveEmptyEntries);
        return words.Any(w => BlockedTerms.Contains(w));
    }
}
