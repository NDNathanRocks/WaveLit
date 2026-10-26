using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Wavelit.Create;

/// <summary>
/// Real-ESRGAN (compact x4) super-resolution in overlapping 128-pixel tiles, then an area resample to the
/// size the screens need. Runs on the GPU through DirectML when there is one, otherwise on the CPU.
/// </summary>
internal sealed class Upscaler(string modelPath, int adapterIndex) : IDisposable
{
    private const int Tile = 128;
    private const int Pad = 8;        // context around each tile, discarded from the output (hides seams)
    private const int Factor = 4;
    private InferenceSession? _session;

    private InferenceSession Session()
    {
        if (_session is not null) return _session;
        if (adapterIndex >= 0)
        {
            try
            {
                var gpu = new SessionOptions { EnableMemoryPattern = false, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
                gpu.AppendExecutionProvider_DML(adapterIndex);
                return _session = new InferenceSession(modelPath, gpu);
            }
            catch (OnnxRuntimeException)
            {
                // No usable DirectX 12 device: the CPU still works, just slower.
            }
        }
        return _session = new InferenceSession(modelPath, new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR });
    }

    /// <summary>
    /// Upscales RGB pixels so the result is at least <paramref name="targetWidth"/> × <paramref name="targetHeight"/>
    /// (never more than 4×, never smaller than the input). Returns the new pixels and size.
    /// </summary>
    public (byte[] Rgb, int Width, int Height) Upscale(byte[] rgb, int width, int height, int targetWidth, int targetHeight, Action<double>? progress)
    {
        double scale = Math.Clamp(Math.Max((double)targetWidth / width, (double)targetHeight / height), 1, Factor);
        int outW = (int)Math.Round(width * scale), outH = (int)Math.Round(height * scale);
        if (scale <= 1.01) return (rgb, width, height);

        int bigW = width * Factor, bigH = height * Factor;
        var big = new byte[bigW * bigH * 3];
        var input = new DenseTensor<float>([1, 3, Tile, Tile]);
        const int step = Tile - 2 * Pad;
        int tilesX = (width + step - 1) / step, tilesY = (height + step - 1) / step, done = 0;
        var session = Session();

        for (int ty = 0; ty < height; ty += step)
        {
            for (int tx = 0; tx < width; tx += step)
            {
                int ix = Math.Clamp(tx - Pad, 0, Math.Max(0, width - Tile));
                int iy = Math.Clamp(ty - Pad, 0, Math.Max(0, height - Tile));
                for (int y = 0; y < Tile; y++)
                {
                    int sy = Math.Min(iy + y, height - 1);
                    for (int x = 0; x < Tile; x++)
                    {
                        int sx = Math.Min(ix + x, width - 1), si = (sy * width + sx) * 3;
                        input[0, 0, y, x] = rgb[si] / 255f;
                        input[0, 1, y, x] = rgb[si + 1] / 255f;
                        input[0, 2, y, x] = rgb[si + 2] / 255f;
                    }
                }
                using var result = session.Run([NamedOnnxValue.CreateFromTensor("image", input)]);
                var tile = result.First().AsTensor<float>();

                // Keep only this tile's own region; its padding overlaps the neighbours.
                int x0 = tx * Factor, x1 = Math.Min(tx + step, width) * Factor;
                int y0 = ty * Factor, y1 = Math.Min(ty + step, height) * Factor;
                for (int oy = y0; oy < y1; oy++)
                {
                    int ly = oy - iy * Factor;
                    for (int ox = x0; ox < x1; ox++)
                    {
                        int lx = ox - ix * Factor, di = (oy * bigW + ox) * 3;
                        big[di] = ToByte(tile[0, 0, ly, lx]);
                        big[di + 1] = ToByte(tile[0, 1, ly, lx]);
                        big[di + 2] = ToByte(tile[0, 2, ly, lx]);
                    }
                }
                progress?.Invoke(++done / (double)(tilesX * tilesY));
            }
        }
        return outW == bigW && outH == bigH ? (big, bigW, bigH) : (ResizeArea(big, bigW, bigH, outW, outH), outW, outH);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    /// <summary>Box-filter downscale (each output pixel averages the source area it covers).</summary>
    internal static byte[] ResizeArea(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 3];
        double fx = (double)sw / dw, fy = (double)sh / dh;
        for (int y = 0; y < dh; y++)
        {
            double sy0 = y * fy, sy1 = sy0 + fy;
            for (int x = 0; x < dw; x++)
            {
                double sx0 = x * fx, sx1 = sx0 + fx;
                double r = 0, g = 0, b = 0, total = 0;
                for (int yy = (int)sy0; yy < Math.Min(sh, (int)Math.Ceiling(sy1)); yy++)
                {
                    double wy = Math.Min(sy1, yy + 1) - Math.Max(sy0, yy);
                    for (int xx = (int)sx0; xx < Math.Min(sw, (int)Math.Ceiling(sx1)); xx++)
                    {
                        double w = wy * (Math.Min(sx1, xx + 1) - Math.Max(sx0, xx));
                        int si = (yy * sw + xx) * 3;
                        r += src[si] * w;
                        g += src[si + 1] * w;
                        b += src[si + 2] * w;
                        total += w;
                    }
                }
                int di = (y * dw + x) * 3;
                dst[di] = (byte)Math.Round(r / total);
                dst[di + 1] = (byte)Math.Round(g / total);
                dst[di + 2] = (byte)Math.Round(b / total);
            }
        }
        return dst;
    }

    public void Dispose() => _session?.Dispose();
}
