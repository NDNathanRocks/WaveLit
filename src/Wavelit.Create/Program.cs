using System.Text.Json;
using Wavelit.Core.Create;
using Wavelit.Create;

// Wavelit's local image helper: SDXL-Lightning generation and Real-ESRGAN upscaling on DirectML. Started by
// Wavelit when the user creates; exits after two idle minutes so the models' memory is returned.
// Protocol: one JSON request per line on stdin; JSON events, one per line, on stdout.
//   generate: { "id", "type": "generate", "prompt", "width", "height", "seed", "count", "outDir", "models", "safety", "adapter" }
//   upscale:  { "id", "type": "upscale", "input" (raw RGB file), "sourceWidth", "sourceHeight", "targetWidth", "targetHeight", "output" (PNG), "adapter" }
//   events:   progress { "value", "stage" } · image { "path" } · blocked · done · error { "message" }

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var output = Console.Out;
void Emit(object e)
{
    lock (output)
    {
        output.WriteLine(JsonSerializer.Serialize(e, json));
        output.Flush();
    }
}

string upscalerModel = Path.Combine(AppContext.BaseDirectory, "Models", "realesrgan-x4", "model.onnx");
SdxlLightningPipeline? pipeline = null;
SafetyChecker? safety = null;
Upscaler? upscaler = null;
var idle = TimeSpan.FromMinutes(2);

try
{
    while (true)
    {
        var read = Console.In.ReadLineAsync();
        if (await Task.WhenAny(read, Task.Delay(idle)) != read) break; // idle: exit and free the GPU
        string? line = await read;
        if (line is null) break;                                       // Wavelit closed the pipe
        HelperRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<HelperRequest>(line, json);
        }
        catch (JsonException)
        {
            continue;
        }
        if (request is null) continue;

        int id = request.Id;
        try
        {
            if (request.Type == "upscale")
            {
                Emit(new { id, type = "progress", value = 0.0, stage = "Sharpening to full screen size…" });
                int w = request.SourceWidth, h = request.SourceHeight;
                byte[] rgb = await File.ReadAllBytesAsync(request.Input!);
                if (w <= 0 || h <= 0 || rgb.Length != w * h * 3) throw new InvalidOperationException("The image to sharpen was unreadable.");
                upscaler ??= new Upscaler(upscalerModel, request.Adapter);
                var (big, bw, bh) = upscaler.Upscale(rgb, w, h, request.TargetWidth, request.TargetHeight,
                    p => Emit(new { id, type = "progress", value = p, stage = "Sharpening to full screen size…" }));
                await File.WriteAllBytesAsync(request.Output!, PngWriter.Encode(big, bw, bh));
                Emit(new { id, type = "image", path = request.Output });
                Emit(new { id, type = "done" });
                continue;
            }

            if (!Directory.Exists(request.Models) || !File.Exists(request.Safety))
                throw new InvalidOperationException("The Local AI pack isn't installed.");
            Directory.CreateDirectory(request.OutDir!);
            int width = Math.Clamp(request.Width / 64 * 64, 512, 2048);
            int height = Math.Clamp(request.Height / 64 * 64, 512, 2048);

            Emit(new { id, type = "progress", value = 0.0, stage = "Reading your prompt…" });
            pipeline ??= new SdxlLightningPipeline(request.Models!, request.Adapter);
            safety ??= new SafetyChecker(request.Safety!);
            var text = pipeline.Encode(request.Prompt ?? "");

            int count = Math.Clamp(request.Count, 1, 4);
            for (int n = 0; n < count; n++)
            {
                Emit(new { id, type = "progress", value = (double)n / count, stage = n == 0 ? "Painting (the first image loads the model)…" : $"Painting {n + 1} of {count}…" });
                byte[] rgb = pipeline.Generate(text, width, height, request.Seed + n,
                    p => Emit(new { id, type = "progress", value = (n + p) / count, stage = $"Painting {n + 1} of {count}…" }),
                    CancellationToken.None);
                if (safety.IsUnsafe(rgb, width, height))
                {
                    Emit(new { id, type = "blocked" });
                    continue;
                }
                string path = Path.Combine(request.OutDir!, $"local-{Guid.NewGuid():N}.png");
                await File.WriteAllBytesAsync(path, PngWriter.Encode(rgb, width, height));
                Emit(new { id, type = "image", path });
            }
            Emit(new { id, type = "done" });
        }
        catch (Exception ex)
        {
            // Native failures (driver resets, missing DirectML, out of video memory) must not kill the loop.
            Emit(new { id, type = "error", message = Describe(ex) });
        }
    }
}
finally
{
    pipeline?.Dispose();
    safety?.Dispose();
    upscaler?.Dispose();
}

static string Describe(Exception ex)
{
    string text = ex.Message;
    if (text.Contains("887A0005", StringComparison.OrdinalIgnoreCase) || text.Contains("device removed", StringComparison.OrdinalIgnoreCase))
        return "The graphics driver reset during generation. Update your graphics driver and try again.";
    if (text.Contains("out of memory", StringComparison.OrdinalIgnoreCase) || ex is OutOfMemoryException)
        return "Your graphics card ran out of memory. Close games or other GPU-heavy apps and try again, or use Cloud.";
    return "Local generation failed: " + (text.Length > 200 ? text[..200] + "…" : text);
}

internal sealed record HelperRequest(
    int Id, string? Type, string? Prompt, int Width, int Height, long Seed, int Count, string? OutDir, string? Models, string? Safety, int Adapter,
    string? Input, string? Output, int SourceWidth, int SourceHeight, int TargetWidth, int TargetHeight);
