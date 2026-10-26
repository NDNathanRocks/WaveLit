using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wavelit.Core.Create;
using Wavelit.Core.Storage;

namespace Wavelit.App.Services;

public enum LocalAiState { NotInstalled, Downloading, Paused, Installed, Failed }

/// <summary>
/// The optional Local AI pack: download (resumable, verified), status for the UI, removal, and the
/// helper process (Wavelit.Create) that generates images and sharpens creations. The helper loads its
/// models only while the user creates and exits when idle.
/// </summary>
public sealed class LocalAiService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly ModelDownloader _downloader;
    private readonly ILogger<LocalAiService> _logger;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _downloadCts;
    private Process? _helper;
    private int _nextRequest;
    private readonly Dictionary<int, Action<JsonElement>> _listeners = new();

    public LocalAiService(AppPaths paths, SettingsStore settings, ModelDownloader downloader, ILogger<LocalAiService> logger)
    {
        _paths = paths;
        _settings = settings;
        _downloader = downloader;
        _logger = logger;
        State = LocalModelPack.IsInstalled(paths) ? LocalAiState.Installed
            : LocalModelPack.BytesOnDisk(paths) > 0 ? LocalAiState.Paused
            : LocalAiState.NotInstalled;
        Progress = new DownloadProgress(LocalModelPack.BytesOnDisk(paths), LocalModelPack.TotalBytes, "");
        Gpu = GpuProbe.Best();
    }

    /// <summary>Raised (on any thread) when <see cref="State"/>, <see cref="Progress"/> or <see cref="Error"/> change.</summary>
    public event EventHandler? Changed;

    public LocalAiState State { get; private set; }
    public DownloadProgress Progress { get; private set; }
    public string? Error { get; private set; }
    public GpuInfo? Gpu { get; }

    /// <summary>Resumes a download the user started earlier (e.g. before a restart).</summary>
    public void ResumeIfWanted()
    {
        if (_settings.Current.LocalAiWanted && State is LocalAiState.Paused or LocalAiState.NotInstalled) StartDownload();
    }

    public void StartDownload()
    {
        lock (_gate)
        {
            if (State is LocalAiState.Downloading or LocalAiState.Installed) return;
            _downloadCts = new CancellationTokenSource();
            State = LocalAiState.Downloading;
            Error = null;
        }
        _settings.Update(s => s with { LocalAiWanted = true });
        Changed?.Invoke(this, EventArgs.Empty);
        _ = RunDownloadAsync(_downloadCts.Token);
    }

    public void PauseDownload()
    {
        lock (_gate) _downloadCts?.Cancel();
    }

    private async Task RunDownloadAsync(CancellationToken ct)
    {
        var progress = new Progress<DownloadProgress>(p =>
        {
            Progress = p;
            Changed?.Invoke(this, EventArgs.Empty);
        });
        try
        {
            await Task.Run(() => _downloader.DownloadAsync(progress, ct), ct);
            State = LocalAiState.Installed;
            _logger.LogInformation("Local AI pack installed");
        }
        catch (OperationCanceledException)
        {
            State = LocalAiState.Paused;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Local AI download failed");
            State = LocalAiState.Failed;
            Error = ex is IOException { HResult: unchecked((int)0x80070070) }
                ? "Not enough disk space for the Local AI pack."
                : "The download stopped. Check your connection and press Resume.";
        }
        finally
        {
            Progress = Progress with { BytesDone = LocalModelPack.BytesOnDisk(_paths) };
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Remove()
    {
        PauseDownload();
        StopHelper();
        try
        {
            LocalModelPack.Remove(_paths);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not remove the Local AI pack");
        }
        _settings.Update(s => s with { LocalAiWanted = false });
        State = LocalAiState.NotInstalled;
        Progress = new DownloadProgress(0, LocalModelPack.TotalBytes, "");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ generation

    /// <summary>
    /// Generates <paramref name="count"/> images with the local model. Returns PNG paths (images the safety
    /// check rejected are skipped and counted in <c>Blocked</c>).
    /// </summary>
    public Task<(IReadOnlyList<string> Paths, int Blocked)> GenerateAsync(string prompt, int width, int height, long seed,
        int count, string outDir, IProgress<(double Value, string Stage)>? progress, CancellationToken ct)
    {
        if (State != LocalAiState.Installed) throw new GenerationException("Download the Local AI pack first.");
        return RunAsync(id => new
        {
            id, type = "generate", prompt, width, height, seed, count, outDir,
            models = LocalModelPack.SdxlRoot(_paths),
            safety = LocalModelPack.SafetyModel(_paths),
            adapter = Gpu?.AdapterIndex ?? 0,
        }, outDir, progress, ct);
    }

    /// <summary>
    /// Sharpens raw RGB pixels with the bundled Real-ESRGAN model (GPU when available) until they cover
    /// the target size; writes a PNG to <paramref name="output"/>. Works without the Local AI pack.
    /// </summary>
    public async Task UpscaleAsync(string rawRgbPath, int width, int height, int targetWidth, int targetHeight, string output,
        IProgress<(double Value, string Stage)>? progress, CancellationToken ct)
    {
        var (paths, _) = await RunAsync(id => new
        {
            id, type = "upscale", input = rawRgbPath, sourceWidth = width, sourceHeight = height,
            targetWidth, targetHeight, output, adapter = Gpu?.AdapterIndex ?? -1,
        }, Path.GetDirectoryName(output)!, progress, ct);
        if (paths.Count == 0) throw new GenerationException("Sharpening failed.");
    }

    private async Task<(IReadOnlyList<string> Paths, int Blocked)> RunAsync(Func<int, object> buildRequest, string outDir,
        IProgress<(double Value, string Stage)>? progress, CancellationToken ct)
    {
        var helper = EnsureHelper();
        int id = Interlocked.Increment(ref _nextRequest);
        var paths = new List<string>();
        int blocked = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_listeners)
        {
            _listeners[id] = e =>
            {
                switch (e.GetProperty("type").GetString())
                {
                    case "progress":
                        progress?.Report((e.GetProperty("value").GetDouble(), e.GetProperty("stage").GetString() ?? ""));
                        break;
                    case "image":
                        if (e.GetProperty("path").GetString() is { } p && IsInside(p, outDir)) paths.Add(p);
                        break;
                    case "blocked":
                        blocked++;
                        break;
                    case "done":
                        done.TrySetResult();
                        break;
                    case "error":
                        done.TrySetException(new GenerationException(e.GetProperty("message").GetString() ?? "Local generation failed."));
                        break;
                }
            };
        }
        try
        {
            string request = JsonSerializer.Serialize(buildRequest(id), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await helper.StandardInput.WriteLineAsync(request.AsMemory(), ct);
            await helper.StandardInput.FlushAsync(ct);
            await using (ct.Register(StopHelper)) // the helper can't be interrupted mid-step; stopping it is the cancel
                await done.Task.WaitAsync(ct);
            return (paths, blocked);
        }
        finally
        {
            lock (_listeners) _listeners.Remove(id);
        }
    }

    private static bool IsInside(string path, string folder)
        => Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private Process EnsureHelper()
    {
        lock (_gate)
        {
            if (_helper is { HasExited: false }) return _helper;
            _helper?.Dispose();
            string folder = Path.Combine(AppContext.BaseDirectory, "create");
            string dll = Path.Combine(folder, "Wavelit.Create.dll");
            string exe = Path.Combine(folder, "Wavelit.Create.exe");
            if (!File.Exists(dll)) throw new GenerationException("The local generator is missing from this installation. Reinstall Wavelit.");

            // Under `dotnet Wavelit.dll` (development) the helper runs the same way; installed builds use its exe.
            string host = Environment.ProcessPath ?? "";
            bool viaDotnet = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe);
            var start = new ProcessStartInfo(viaDotnet ? host : exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = folder,
            };
            if (viaDotnet) start.ArgumentList.Add(dll);
            var process = Process.Start(start) ?? throw new GenerationException("Couldn't start the local generator.");
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
            process.OutputDataReceived += OnHelperOutput;
            process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } d) _logger.LogDebug("Create helper: {Line}", d); };
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => FailAll("The local generator stopped unexpectedly.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _helper = process;
            _logger.LogInformation("Started local generator (pid {Pid})", process.Id);
            return process;
        }
    }

    private void OnHelperOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        try
        {
            using var doc = JsonDocument.Parse(e.Data);
            var root = doc.RootElement.Clone();
            if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out int id)) return;
            Action<JsonElement>? listener;
            lock (_listeners) _listeners.TryGetValue(id, out listener);
            listener?.Invoke(root);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Unreadable message from the local generator");
        }
    }

    private void FailAll(string message)
    {
        List<Action<JsonElement>> listeners;
        lock (_listeners) listeners = [.. _listeners.Values];
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { type = "error", message }));
        foreach (var l in listeners) l(doc.RootElement);
    }

    private void StopHelper()
    {
        lock (_gate)
        {
            if (_helper is null) return;
            try
            {
                if (!_helper.HasExited) _helper.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            _helper.Dispose();
            _helper = null;
        }
    }

    public void Dispose()
    {
        _downloadCts?.Cancel();
        StopHelper();
    }
}
