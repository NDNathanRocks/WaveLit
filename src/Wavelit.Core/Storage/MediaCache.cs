using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Wavelit.Core.Storage;

/// <summary>
/// Content cache for downloaded videos with a size cap and least-recently-used eviction.
/// File names are derived from a hash of the source URL, so server-provided names never touch the file system.
/// </summary>
public sealed class MediaCache(AppPaths paths, IHttpClientFactory httpFactory, ILogger<MediaCache> logger)
{
    public const string DownloadClient = "downloads";
    private const long MaxFileBytes = 2L * 1024 * 1024 * 1024;
    private readonly SemaphoreSlim _evictGate = new(1, 1);

    public string Directory => paths.MediaCache;

    public static string FileNameFor(string url, string extension)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16)) + extension;
    }

    public string PathFor(string fileName) => Path.Combine(paths.MediaCache, Path.GetFileName(fileName));

    public bool TryGet(string fileName, out string fullPath)
    {
        fullPath = PathFor(fileName);
        if (!File.Exists(fullPath)) return false;
        Touch(fullPath);
        return true;
    }

    /// <summary>Downloads <paramref name="url"/> into the cache. Interrupted downloads are discarded, never half-used.</summary>
    public async Task<string> DownloadAsync(string url, string fileName, IProgress<double>? progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Only https downloads are allowed.");

        string target = PathFor(fileName);
        if (File.Exists(target))
        {
            Touch(target);
            progress?.Report(1);
            return target;
        }

        string part = target + ".part";
        var client = httpFactory.CreateClient(DownloadClient);
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                && mediaType != "application/octet-stream")
                throw new InvalidOperationException($"Unexpected content type '{mediaType}'.");

            long? total = response.Content.Headers.ContentLength;
            if (total > MaxFileBytes) throw new InvalidOperationException("File is too large.");

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dest = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long read = 0;
                int n;
                double lastReported = -1;
                while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    read += n;
                    if (read > MaxFileBytes) throw new InvalidOperationException("File is too large.");
                    await dest.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    if (total > 0)
                    {
                        double p = (double)read / total.Value;
                        if (p - lastReported >= 0.01) { progress?.Report(p); lastReported = p; }
                    }
                }
            }

            File.Move(part, target, overwrite: true);
            progress?.Report(1);
            logger.LogInformation("Downloaded {File} from {Url}", fileName, Redact.Url(url));
            return target;
        }
        finally
        {
            TryDelete(part);
        }
    }

    /// <summary>Deletes least-recently-used files until the cache fits <paramref name="limitBytes"/>.</summary>
    public async Task EvictAsync(long limitBytes, IReadOnlySet<string> protectedFileNames)
    {
        await _evictGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var files = new DirectoryInfo(paths.MediaCache).GetFiles()
                .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.LastAccessTimeUtc)
                .ToList();
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= limitBytes) break;
                if (protectedFileNames.Contains(f.Name)) continue;
                total -= f.Length;
                TryDelete(f.FullName);
                logger.LogInformation("Evicted {File} from cache", f.Name);
            }
        }
        finally
        {
            _evictGate.Release();
        }
    }

    public long SizeBytes()
        => new DirectoryInfo(paths.CacheRoot).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    public void Clear(IReadOnlySet<string> protectedFileNames)
    {
        foreach (var f in new DirectoryInfo(paths.MediaCache).GetFiles())
            if (!protectedFileNames.Contains(f.Name)) TryDelete(f.FullName);
        foreach (var dir in new[] { paths.ThumbnailCache })
            foreach (var f in new DirectoryInfo(dir).GetFiles()) TryDelete(f.FullName);
    }

    // NTFS last-access updates are often disabled, so record use explicitly for LRU.
    private static void Touch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
