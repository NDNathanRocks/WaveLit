using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wavelit.Core.Create;

/// <summary>
/// The CLIP byte-level BPE tokenizer used by Stable Diffusion's text encoders, read from a Hugging Face
/// tokenizer.json. Produces exactly 77 ids: start token, up to 75 prompt tokens, end token, padding.
/// </summary>
public sealed partial class ClipTokenizer
{
    public const int Length = 77;
    public const int StartToken = 49406;
    public const int EndToken = 49407;
    private const string EndOfWord = "</w>";

    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string, string), int> _ranks;
    private readonly int _padToken;
    private readonly Dictionary<string, string[]> _cache = new();
    private static readonly char[] ByteToChar = BuildByteMap();

    public ClipTokenizer(Dictionary<string, int> vocab, IEnumerable<(string, string)> merges, int padToken)
    {
        _vocab = vocab;
        _ranks = new Dictionary<(string, string), int>();
        int rank = 0;
        foreach (var pair in merges) _ranks.TryAdd(pair, rank++);
        _padToken = padToken;
    }

    /// <summary>Loads tokenizer.json. <paramref name="padToken"/> is 49407 for CLIP-L and 0 ("!") for OpenCLIP-G.</summary>
    public static ClipTokenizer Load(string tokenizerJsonPath, int padToken)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(tokenizerJsonPath));
        var model = doc.RootElement.GetProperty("model");
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in model.GetProperty("vocab").EnumerateObject()) vocab[p.Name] = p.Value.GetInt32();
        var merges = new List<(string, string)>();
        foreach (var m in model.GetProperty("merges").EnumerateArray())
        {
            if (m.ValueKind == JsonValueKind.Array) merges.Add((m[0].GetString()!, m[1].GetString()!));
            else if (m.GetString() is { } s && s.Split(' ') is [var a, var b]) merges.Add((a, b)); // older "a b" format
        }
        return new ClipTokenizer(vocab, merges, padToken);
    }

    public int[] Encode(string text)
    {
        var ids = new List<int>(Length) { StartToken };
        string clean = WebUtility.HtmlDecode(Whitespace().Replace(text, " ")).Trim().ToLowerInvariant();
        foreach (Match m in Pattern().Matches(clean))
        {
            var bytes = Encoding.UTF8.GetBytes(m.Value);
            var word = new StringBuilder(bytes.Length);
            foreach (byte b in bytes) word.Append(ByteToChar[b]);
            foreach (var piece in Bpe(word.ToString()))
            {
                if (ids.Count >= Length - 1) break;
                if (_vocab.TryGetValue(piece, out int id)) ids.Add(id);
            }
        }
        ids.Add(EndToken);
        while (ids.Count < Length) ids.Add(_padToken);
        return [.. ids];
    }

    private string[] Bpe(string token)
    {
        if (_cache.TryGetValue(token, out var cached)) return cached;
        var word = token.Select(c => c.ToString()).ToList();
        word[^1] += EndOfWord;
        while (word.Count > 1)
        {
            int best = int.MaxValue, at = -1;
            for (int i = 0; i < word.Count - 1; i++)
            {
                if (_ranks.TryGetValue((word[i], word[i + 1]), out int r) && r < best)
                {
                    best = r;
                    at = i;
                }
            }
            if (at < 0) break;
            // Merge every occurrence of the best pair, left to right.
            string a = word[at], b = word[at + 1];
            var merged = new List<string>(word.Count);
            for (int i = 0; i < word.Count; i++)
            {
                if (i < word.Count - 1 && word[i] == a && word[i + 1] == b)
                {
                    merged.Add(a + b);
                    i++;
                }
                else merged.Add(word[i]);
            }
            word = merged;
        }
        var result = word.ToArray();
        if (_cache.Count < 10_000) _cache[token] = result;
        return result;
    }

    // GPT-2/CLIP byte-to-unicode table: printable bytes map to themselves, the rest to code points from 256.
    private static char[] BuildByteMap()
    {
        var map = new char[256];
        var direct = new List<int>();
        for (int b = '!'; b <= '~'; b++) direct.Add(b);
        for (int b = 0xA1; b <= 0xAC; b++) direct.Add(b);
        for (int b = 0xAE; b <= 0xFF; b++) direct.Add(b);
        int n = 0;
        for (int b = 0; b < 256; b++)
            map[b] = direct.Contains(b) ? (char)b : (char)(256 + n++);
        return map;
    }

    [GeneratedRegex(@"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
