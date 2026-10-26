namespace Wavelit.Core.Create;

/// <summary>
/// Euler sampler schedule for SDXL-Lightning: "trailing" timestep spacing over the SDXL training noise
/// schedule (scaled-linear betas 0.00085–0.012, 1000 steps), no classifier-free guidance.
/// Matches diffusers' EulerDiscreteScheduler(timestep_spacing="trailing").
/// </summary>
public static class LightningSchedule
{
    public const int TrainSteps = 1000;
    public const float VaeScale = 0.13025f; // SDXL VAE scaling factor

    /// <summary>Timesteps and the matching sigmas (one extra trailing 0) for <paramref name="steps"/> steps.</summary>
    public static (long[] Timesteps, float[] Sigmas) Create(int steps)
    {
        steps = Math.Clamp(steps, 1, 50);
        double start = Math.Sqrt(0.00085), end = Math.Sqrt(0.012);
        var allSigmas = new double[TrainSteps];
        double cumulative = 1;
        for (int i = 0; i < TrainSteps; i++)
        {
            double beta = Math.Pow(start + (end - start) * i / (TrainSteps - 1), 2);
            cumulative *= 1 - beta;
            allSigmas[i] = Math.Sqrt((1 - cumulative) / cumulative);
        }

        var timesteps = new long[steps];
        var sigmas = new float[steps + 1];
        double ratio = (double)TrainSteps / steps;
        for (int i = 0; i < steps; i++)
        {
            // trailing: round(arange(1000, 0, -ratio)) - 1
            long t = (long)Math.Round(TrainSteps - i * ratio, MidpointRounding.ToEven) - 1;
            timesteps[i] = t;
            sigmas[i] = (float)allSigmas[t];
        }
        sigmas[steps] = 0;
        return (timesteps, sigmas);
    }

    /// <summary>Scale applied to the latents before the UNet at a given sigma.</summary>
    public static float InputScale(float sigma) => 1f / MathF.Sqrt(sigma * sigma + 1);
}

/// <summary>Picks generation sizes that keep the shape of the user's screens.</summary>
public static class GenerationSize
{
    /// <summary>
    /// The size closest to <paramref name="targetPixels"/> with the canvas aspect ratio, in multiples of
    /// <paramref name="multiple"/>, each side within [<paramref name="min"/>, <paramref name="max"/>].
    /// Extreme aspects are capped at 4:1 (models degrade beyond that; the renderer crops the rest).
    /// </summary>
    public static (int Width, int Height) ForAspect(int canvasWidth, int canvasHeight, int targetPixels, int multiple, int min, int max)
    {
        double aspect = Math.Clamp((double)Math.Max(1, canvasWidth) / Math.Max(1, canvasHeight), 0.25, 4.0);
        double h = Math.Sqrt(targetPixels / aspect);
        double w = h * aspect;
        int Round(double v) => Math.Clamp((int)Math.Round(v / multiple) * multiple, min, max);
        return (Round(w), Round(h));
    }
}
