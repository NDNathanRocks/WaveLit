using System.IO.Compression;
using Wavelit.Core.Create;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Tests;

public class ClipTokenizerTests
{
    // Tiny vocabulary: "cat" merges to one token, "cats" to "cat" + "s</w>".
    private static ClipTokenizer Tiny() => new(
        new Dictionary<string, int>
        {
            ["c"] = 1, ["a"] = 2, ["t</w>"] = 3, ["ca"] = 4, ["cat</w>"] = 5, ["cat"] = 6, ["s</w>"] = 7, ["!</w>"] = 8,
        },
        [("c", "a"), ("ca", "t</w>"), ("ca", "t")],
        padToken: 0);

    [Fact]
    public void Wraps_tokens_in_start_and_end_and_pads_to_77()
    {
        int[] ids = Tiny().Encode("Cat");
        Assert.Equal(ClipTokenizer.Length, ids.Length);
        Assert.Equal([ClipTokenizer.StartToken, 5, ClipTokenizer.EndToken, 0], ids[..4]);
    }

    [Fact]
    public void Applies_merges_in_rank_order()
    {
        int[] ids = Tiny().Encode("cats!");
        Assert.Equal([ClipTokenizer.StartToken, 6, 7, 8, ClipTokenizer.EndToken], ids[..5]);
    }

    [Fact]
    public void Long_prompts_are_truncated_but_keep_the_end_token()
    {
        int[] ids = Tiny().Encode(string.Join(' ', Enumerable.Repeat("cat", 200)));
        Assert.Equal(ClipTokenizer.Length, ids.Length);
        Assert.Equal(ClipTokenizer.EndToken, ids[^1]);
    }

    // Uses the real SDXL tokenizer when it's available locally (set WAVELIT_CLIP_TOKENIZER to its path).
    [Fact]
    public void Matches_the_reference_clip_tokenizer_when_available()
    {
        string? path = Environment.GetEnvironmentVariable("WAVELIT_CLIP_TOKENIZER");
        if (path is null || !File.Exists(path)) return;
        var tokenizer = ClipTokenizer.Load(path, ClipTokenizer.EndToken);
        Assert.Equal([49406, 320, 1125, 539, 320, 2368, 49407, 49407], tokenizer.Encode("A photo of a  cat")[..8]);
    }
}

public class LightningScheduleTests
{
    [Fact]
    public void Four_step_trailing_timesteps_match_diffusers()
    {
        var (timesteps, sigmas) = LightningSchedule.Create(4);
        Assert.Equal([999L, 749, 499, 249], timesteps);
        Assert.Equal(5, sigmas.Length);
        Assert.Equal(0f, sigmas[^1]);
        Assert.InRange(sigmas[0], 14.5f, 14.7f); // SDXL's largest sigma ≈ 14.61
        Assert.True(sigmas[0] > sigmas[1] && sigmas[1] > sigmas[2] && sigmas[2] > sigmas[3] && sigmas[3] > 0);
    }

    [Theory]
    [InlineData(3000, 1920, 1_048_576, 64)]
    [InlineData(1080, 1920, 1_048_576, 64)]
    [InlineData(1920, 1080, 1_600_000, 16)]
    public void Generation_size_keeps_the_screen_shape(int cw, int ch, int pixels, int multiple)
    {
        var (w, h) = GenerationSize.ForAspect(cw, ch, pixels, multiple, 256, 2048);
        Assert.Equal(0, w % multiple);
        Assert.Equal(0, h % multiple);
        Assert.InRange((double)w / h, (double)cw / ch * 0.93, (double)cw / ch * 1.07);
        Assert.InRange(w * h, pixels * 0.85, pixels * 1.15);
    }

    [Fact]
    public void Extreme_aspects_are_capped() =>
        Assert.True(GenerationSize.ForAspect(10000, 1000, 1_048_576, 64, 256, 2048) is var (w, h) && (double)w / h <= 4.2);
}

public class PngWriterTests
{
    [Fact]
    public void Writes_a_valid_png_with_the_pixels()
    {
        byte[] rgb = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255];
        byte[] png = PngWriter.Encode(rgb, 2, 2);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        // IHDR CRC for a 2×2 8-bit RGB image is a known constant.
        Assert.Equal([0xFD, 0xD4, 0x9A, 0x73], png[29..33]); // zlib.crc32(b"IHDR" + header)

        // Inflate IDAT and compare scanlines (filter byte 0 + raw RGB).
        int idat = IndexOf(png, "IDAT"u8.ToArray());
        int len = (png[idat - 4] << 24) | (png[idat - 3] << 16) | (png[idat - 2] << 8) | png[idat - 1];
        using var z = new ZLibStream(new MemoryStream(png, idat + 4, len), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        Assert.Equal([0, 255, 0, 0, 0, 255, 0, 0, 0, 0, 255, 255, 255, 255], raw.ToArray());
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (int i = 0; i <= data.Length - pattern.Length; i++)
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern)) return i;
        return -1;
    }
}

public class LocalModelPackTests
{
    [Fact]
    public void Every_file_is_pinned_and_hashed()
    {
        Assert.All(LocalModelPack.Files, f =>
        {
            Assert.Matches("^[0-9a-f]{40}$", f.Commit);
            Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
            Assert.StartsWith("https://huggingface.co/", f.Url);
            Assert.True(f.Size > 0);
        });
        Assert.InRange(LocalModelPack.TotalBytes / 1e9, 6.5, 7.5);
    }

    [Fact]
    public void Nothing_is_installed_in_an_empty_folder()
    {
        string root = Path.Combine(Path.GetTempPath(), "wavelit-models-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            Assert.False(LocalModelPack.IsInstalled(paths));
            Assert.Equal(0, LocalModelPack.BytesOnDisk(paths));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
