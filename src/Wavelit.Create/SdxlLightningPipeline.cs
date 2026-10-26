using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Wavelit.Core.Create;

namespace Wavelit.Create;

/// <summary>
/// SDXL-Lightning text-to-image on ONNX Runtime + DirectML (any DirectX 12 GPU): two CLIP text encoders,
/// a 4-step Euler loop over the UNet without guidance, and the VAE decoder. Text encoders are released
/// after encoding to keep video memory for the UNet; the UNet and VAE stay loaded until the process exits.
/// </summary>
internal sealed class SdxlLightningPipeline(string modelRoot, int adapterIndex) : IDisposable
{
    public const int Steps = 4;
    private InferenceSession? _unet;
    private InferenceSession? _vae;
    private ClipTokenizer? _tokenizer1;
    private ClipTokenizer? _tokenizer2;

    private SessionOptions Options()
    {
        var o = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableMemoryPattern = false,             // required by DirectML
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        o.AppendExecutionProvider_DML(adapterIndex);
        return o;
    }

    private string File(string relative) => Path.Combine(modelRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Prompt → (encoder hidden states [1,77,2048], pooled text embeds [1,1280]).</summary>
    public (DenseTensor<float> Hidden, DenseTensor<float> Pooled) Encode(string prompt)
    {
        _tokenizer1 ??= ClipTokenizer.Load(File("tokenizer/tokenizer.json"), padToken: ClipTokenizer.EndToken);
        _tokenizer2 ??= ClipTokenizer.Load(File("tokenizer_2/tokenizer.json"), padToken: 0);
        int[] ids1 = _tokenizer1.Encode(prompt);
        long[] ids2 = _tokenizer2.Encode(prompt).Select(i => (long)i).ToArray();

        float[] h1, h2, pooled;
        using (var options = Options())
        using (var te1 = new InferenceSession(File("text_encoder/model.onnx"), options))
        {
            var input = new DenseTensor<int>(ids1, [1, ClipTokenizer.Length]);
            using var r = te1.Run([NamedOnnxValue.CreateFromTensor("input_ids", input)], ["hidden_states.11"]);
            h1 = r.First().AsEnumerable<float>().ToArray();                      // penultimate layer, 768 wide
        }
        using (var options = Options())
        using (var te2 = new InferenceSession(File("text_encoder_2/model.onnx"), options))
        {
            var input = new DenseTensor<long>(ids2, [1, ClipTokenizer.Length]);
            using var r = te2.Run([NamedOnnxValue.CreateFromTensor("input_ids", input)], ["text_embeds", "hidden_states.31"]);
            pooled = r.First(v => v.Name == "text_embeds").AsEnumerable<float>().ToArray();       // 1280
            h2 = r.First(v => v.Name == "hidden_states.31").AsEnumerable<float>().ToArray();     // penultimate, 1280 wide
        }

        // SDXL conditions on both encoders side by side: [77, 768 + 1280].
        var hidden = new DenseTensor<float>([1, ClipTokenizer.Length, 2048]);
        for (int t = 0; t < ClipTokenizer.Length; t++)
        {
            for (int c = 0; c < 768; c++) hidden[0, t, c] = h1[t * 768 + c];
            for (int c = 0; c < 1280; c++) hidden[0, t, 768 + c] = h2[t * 1280 + c];
        }
        return (hidden, new DenseTensor<float>(pooled, [1, 1280]));
    }

    /// <summary>Runs the sampler for one seed and returns RGB bytes (width × height × 3).</summary>
    public byte[] Generate((DenseTensor<float> Hidden, DenseTensor<float> Pooled) text, int width, int height, long seed,
        Action<double>? progress, CancellationToken ct)
    {
        _unet ??= new InferenceSession(File("unet/model.onnx"), Options());
        _vae ??= new InferenceSession(File("vae_decoder/model.onnx"), Options());

        int lw = width / 8, lh = height / 8, count = 4 * lw * lh;
        var (timesteps, sigmas) = LightningSchedule.Create(Steps);
        var latents = new float[count];
        var rng = new Random(unchecked((int)seed ^ (int)(seed >> 32)));
        for (int i = 0; i < count; i++) latents[i] = Gaussian(rng) * sigmas[0];

        var timeIds = new DenseTensor<float>(new float[] { height, width, 0, 0, height, width }, [1, 6]);
        var model = new float[count];
        for (int step = 0; step < Steps; step++)
        {
            ct.ThrowIfCancellationRequested();
            float scale = LightningSchedule.InputScale(sigmas[step]);
            for (int i = 0; i < count; i++) model[i] = latents[i] * scale;
            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("sample", new DenseTensor<float>(model, [1, 4, lh, lw])),
                NamedOnnxValue.CreateFromTensor("timestep", new DenseTensor<long>(new[] { timesteps[step] }, [1])),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", text.Hidden),
                NamedOnnxValue.CreateFromTensor("text_embeds", text.Pooled),
                NamedOnnxValue.CreateFromTensor("time_ids", timeIds),
            };
            using var r = _unet.Run(inputs);
            var noise = r.First().AsEnumerable<float>().ToArray();
            // Euler step (epsilon prediction): x += eps * (sigma_next - sigma).
            float dt = sigmas[step + 1] - sigmas[step];
            for (int i = 0; i < count; i++) latents[i] += noise[i] * dt;
            progress?.Invoke((step + 1.0) / (Steps + 1));
        }

        ct.ThrowIfCancellationRequested();
        for (int i = 0; i < count; i++) latents[i] /= LightningSchedule.VaeScale;
        using var decoded = _vae.Run([NamedOnnxValue.CreateFromTensor("latent_sample", new DenseTensor<float>(latents, [1, 4, lh, lw]))]);
        var pixels = decoded.First().AsEnumerable<float>().ToArray(); // [1,3,H,W] in -1..1
        progress?.Invoke(1);

        var rgb = new byte[width * height * 3];
        int plane = width * height;
        for (int p = 0; p < plane; p++)
            for (int c = 0; c < 3; c++)
                rgb[p * 3 + c] = (byte)Math.Clamp((int)MathF.Round((pixels[c * plane + p] / 2 + 0.5f) * 255), 0, 255);
        return rgb;
    }

    private static float Gaussian(Random rng)
    {
        // Box–Muller
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
    }

    public void Dispose()
    {
        _unet?.Dispose();
        _vae?.Dispose();
    }
}
