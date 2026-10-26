using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Wavelit.Core.Storage;

namespace Wavelit.Core.Create;

/// <param name="Sha256">Expected hash (lowercase hex). Every file is verified before first use.</param>
public sealed record ModelFile(string Repo, string Commit, string Path, long Size, string Sha256)
{
    public string Url => $"https://huggingface.co/{Repo}/resolve/{Commit}/{Path}";
}

/// <summary>
/// The optional "Local AI" pack: SDXL-Lightning (4-step, CreativeML Open RAIL++-M) as ONNX, plus a small
/// image safety classifier. Downloaded only when the user asks for it, from pinned commits, and checked
/// against hard-coded SHA-256 hashes. Only model weights (data) are downloaded; the engine ships with the app.
/// </summary>
public static class LocalModelPack
{
    public const string Name = "SDXL-Lightning (4-step)";
    public const string License = "CreativeML Open RAIL++-M";
    public const string LicenseUrl = "https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0/blob/main/LICENSE.md";
    public const string ModelFolder = "sdxl-lightning";
    public const string SafetyFolder = "safety";
    private const string Sdxl = "d0gr/sdxl-lightning-onnx-webgpu";
    private const string SdxlCommit = "c0129962aa1a3f8ea02b3d55ddce5b3282342374";
    private const string Safety = "onnx-community/nsfw_image_detection-ONNX";
    private const string SafetyCommit = "1ceb3c7fe1e9f3f2507e6df577437f23a9149fd5";

    public static IReadOnlyList<ModelFile> Files { get; } =
    [
        new(Sdxl, SdxlCommit, "text_encoder/model.onnx", 360437, "21267b285b3e3ab33fc6d95610ce629a67e0bca01647c36e65e6cd04b6eaa6e7"),
        new(Sdxl, SdxlCommit, "text_encoder/model.onnx_data", 246120960, "4b61db0f4781c335e6bba50ab7471bbfe02aad41d050ff5935dcbb2d7724fda0"),
        new(Sdxl, SdxlCommit, "text_encoder_2/model.onnx", 1047642, "0cf6265108359805f27b16141209c83b9452d4ac66f7b626c6c6f046f648f484"),
        new(Sdxl, SdxlCommit, "text_encoder_2/model.onnx_data", 1389319680, "81330f8daecb663019d641658449d3d76205d90c2207356b9cccf1a427214c12"),
        new(Sdxl, SdxlCommit, "tokenizer/tokenizer.json", 1681537, "35bd8b310ba98bf39131d2f0907273a796e7c56bb824b34cb8287a170a77c717"),
        new(Sdxl, SdxlCommit, "tokenizer_2/tokenizer.json", 1681644, "bba43011e86d372b3f706213bacfbe1fbf59421c44be801300479bfd07297f9a"),
        new(Sdxl, SdxlCommit, "unet/model.onnx", 4061552, "cc0841c261fdcd4aeb0af0582e5e7726f63edcd67515adc9508f985566001698"),
        new(Sdxl, SdxlCommit, "unet/model.onnx_data", 2000000000, "99bf38881b2491d3d78789c9e7bbb1a81bdada86c143040417e69676700d64de"),
        new(Sdxl, SdxlCommit, "unet/model.onnx_data_1", 2000000000, "2e9e7629e2fc2a4c4d4afb5aa7779e3c50ee17e3612f26af07f6019b61eff56d"),
        new(Sdxl, SdxlCommit, "unet/model.onnx_data_2", 1134903040, "e50c0ee78767cfbdc49c905fc441d4736f40cb9c679ebf6252ecf3c392181036"),
        new(Sdxl, SdxlCommit, "vae_decoder/model.onnx", 135522, "d2dbbf47fba8ee8b1ec26c10b8a259d6e9870ec7d921df2b2d473fb97a2d0c03"),
        new(Sdxl, SdxlCommit, "vae_decoder/model.onnx_data", 98965248, "fc3ff1700fd5f86a3e789aaadf647aa7742f2c739be6221ca692c5c92a21d39c"),
        new(Safety, SafetyCommit, "onnx/model_int8.onnx", 87333629, "bdc941491cd09ae75c5f40b038a5cd64482fa4e2b5297747323c5cda31303920"),
    ];

    public static long TotalBytes { get; } = Files.Sum(f => f.Size);

    /// <summary>Where a file lives on disk: SDXL files under the model folder, the classifier under "safety".</summary>
    public static string LocalPath(AppPaths paths, ModelFile file)
        => Path.Combine(paths.Models, file.Repo == Safety ? SafetyFolder : ModelFolder, file.Path.Replace('/', Path.DirectorySeparatorChar));

    public static string SdxlRoot(AppPaths paths) => Path.Combine(paths.Models, ModelFolder);
    public static string SafetyModel(AppPaths paths) => Path.Combine(paths.Models, SafetyFolder, "onnx", "model_int8.onnx");

    private static string MarkerPath(AppPaths paths) => Path.Combine(paths.Models, "verified.txt");

    /// <summary>True when every file is present, the right size, and was verified after download.</summary>
    public static bool IsInstalled(AppPaths paths)
        => File.Exists(MarkerPath(paths)) && Files.All(f => new FileInfo(LocalPath(paths, f)) is { Exists: true } fi && fi.Length == f.Size);

    /// <summary>Bytes already on disk (completed files and partial downloads), for progress and "space used".</summary>
    public static long BytesOnDisk(AppPaths paths)
    {
        if (!Directory.Exists(paths.Models)) return 0;
        return new DirectoryInfo(paths.Models).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
    }

    internal static void MarkVerified(AppPaths paths) => File.WriteAllText(MarkerPath(paths), $"{Name} verified {DateTimeOffset.UtcNow:O}");

    public static void Remove(AppPaths paths)
    {
        if (Directory.Exists(paths.Models)) Directory.Delete(paths.Models, recursive: true);
        Directory.CreateDirectory(paths.Models);
    }
}

public sealed record DownloadProgress(long BytesDone, long BytesTotal, string CurrentFile)
{
    public double Fraction => BytesTotal <= 0 ? 0 : Math.Clamp((double)BytesDone / BytesTotal, 0, 1);
}

/// <summary>
/// Downloads the Local AI pack: resumable (HTTP Range, survives restarts), hash-checked, cancellable.
/// A file whose hash doesn't match is deleted and downloaded again once, then reported.
/// </summary>
public sealed class ModelDownloader(AppPaths paths, IHttpClientFactory httpFactory, ILogger<ModelDownloader> logger)
{
    public const string HttpClientName = "models";

    public async Task DownloadAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        long done = 0;
        foreach (var file in LocalModelPack.Files)
        {
            string target = LocalModelPack.LocalPath(paths, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            for (int attempt = 1; ; attempt++)
            {
                if (!(File.Exists(target) && new FileInfo(target).Length == file.Size))
                    await FetchAsync(file, target, done, progress, ct).ConfigureAwait(false);
                progress?.Report(new DownloadProgress(done + file.Size, LocalModelPack.TotalBytes, $"Checking {file.Path}"));
                if (await HashMatchesAsync(target, file.Sha256, ct).ConfigureAwait(false)) break;
                logger.LogWarning("Hash mismatch for {File} (attempt {Attempt})", file.Path, attempt);
                File.Delete(target);
                if (attempt >= 2) throw new InvalidOperationException($"{file.Path} failed its integrity check. Try again later.");
            }
            done += file.Size;
        }
        LocalModelPack.MarkVerified(paths);
        progress?.Report(new DownloadProgress(LocalModelPack.TotalBytes, LocalModelPack.TotalBytes, "Done"));
    }

    private async Task FetchAsync(ModelFile file, string target, long doneBefore, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        string part = target + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > file.Size) { File.Delete(part); have = 0; }

        var client = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // server ignored Range

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dest = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            long written = have;
            long lastReport = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                written += n;
                if (written > file.Size) throw new InvalidOperationException($"{file.Path} is larger than expected.");
                await dest.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                if (written - lastReport >= 8 << 20)
                {
                    lastReport = written;
                    progress?.Report(new DownloadProgress(doneBefore + written, LocalModelPack.TotalBytes, file.Path));
                }
            }
        }
        if (new FileInfo(part).Length != file.Size) throw new IOException($"{file.Path} download was incomplete.");
        File.Move(part, target, overwrite: true);
        logger.LogInformation("Downloaded model file {File}", file.Path);
    }

    private static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash) == expected;
    }
}
