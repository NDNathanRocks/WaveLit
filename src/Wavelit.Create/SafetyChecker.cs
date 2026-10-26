using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Wavelit.Create;

/// <summary>
/// Screens generated images with a small ViT classifier (normal / nsfw, Apache-2.0, int8 on the CPU).
/// Runs on every local image before it is written to disk.
/// </summary>
internal sealed class SafetyChecker(string modelPath) : IDisposable
{
    private const int Size = 224;
    private const float Threshold = 0.5f;
    private InferenceSession? _session;

    public bool IsUnsafe(byte[] rgb, int width, int height)
    {
        _session ??= new InferenceSession(modelPath, new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR });
        var input = new DenseTensor<float>([1, 3, Size, Size]);
        // Bilinear resize to 224×224, then rescale to [-1, 1] (mean 0.5, std 0.5).
        for (int y = 0; y < Size; y++)
        {
            float sy = (y + 0.5f) * height / Size - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(sy), 0, height - 1), y1 = Math.Min(y0 + 1, height - 1);
            float fy = Math.Clamp(sy - y0, 0, 1);
            for (int x = 0; x < Size; x++)
            {
                float sx = (x + 0.5f) * width / Size - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(sx), 0, width - 1), x1 = Math.Min(x0 + 1, width - 1);
                float fx = Math.Clamp(sx - x0, 0, 1);
                for (int c = 0; c < 3; c++)
                {
                    float top = rgb[(y0 * width + x0) * 3 + c] * (1 - fx) + rgb[(y0 * width + x1) * 3 + c] * fx;
                    float bottom = rgb[(y1 * width + x0) * 3 + c] * (1 - fx) + rgb[(y1 * width + x1) * 3 + c] * fx;
                    input[0, c, y, x] = (top * (1 - fy) + bottom * fy) / 255f / 0.5f - 1f;
                }
            }
        }
        using var r = _session.Run([NamedOnnxValue.CreateFromTensor("pixel_values", input)]);
        var logits = r.First().AsEnumerable<float>().ToArray(); // [normal, nsfw]
        float max = Math.Max(logits[0], logits[1]);
        float e0 = MathF.Exp(logits[0] - max), e1 = MathF.Exp(logits[1] - max);
        return e1 / (e0 + e1) > Threshold;
    }

    public void Dispose() => _session?.Dispose();
}
