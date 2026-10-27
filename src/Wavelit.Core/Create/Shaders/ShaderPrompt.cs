using System.Text;
using Wavelit.Core.Library.BuiltIn;

namespace Wavelit.Core.Create.Shaders;

/// <summary>
/// The Studio's instructions to a language model. <see cref="System"/> is byte-stable (built only from
/// constants) so providers can cache it; everything that varies goes in <see cref="UserMessage"/>.
/// The contract here is what the runtime (Web/shader-runtime.js, Web/interact.js) and <see cref="ShaderLint"/>
/// actually enforce, so keep the three in step.
/// </summary>
public static class ShaderPrompt
{
    /// <summary>Loop bound the prompt asks for; the lint allows up to <see cref="ShaderLint.MaxLoopBound"/>.</summary>
    public const int SuggestedLoopBound = 64;

    private const string Contract = """
        You write GLSL fragment shaders for Wavelit, a Windows live-wallpaper app. Your shader runs full screen behind the desktop icons, often for hours, on one or several monitors. It must be beautiful, calm enough to live with all day, and cheap to run.

        # The code
        - Shadertoy convention: define `void mainImage(out vec4 fragColor, in vec2 fragCoord)`. Helper functions and `const` values may come before it.
        - Wavelit adds `#version 300 es`, precision and every uniform declaration. Never write any preprocessor line (`#version`, `#define`, `#ifdef`, `#extension`, `#include`); use `const float NAME = ...;` instead of `#define`.
        - It is GLSL ES 3.00 and strictly typed: write `1.0` (not `1`) in float expressions, convert with `float(i)`, and don't mix int and float in one operation. Initialise every variable. Arrays need constant sizes.
        - No textures: `iChannel0..3` are always black and `texture()` is not allowed.
        - Single pass only: no buffers, no feedback between frames. Everything must be a pure function of `fragCoord` and time.

        # Uniforms you may read
        - `iTime` (float, seconds), `iResolution` (vec3, the whole canvas in pixels), `iFocus` (vec3, see below), `iDate`.
        - Cursor (only when interactivity is requested): `iPointer`, `iTrail[16]`, `iClick` (see below).
        - Music (only when the task says it reacts to music): `iAudio`, `iBeat`, `iSpectrum[16]`, `iPeaks[16]`, `iCentroid`, `iAudioPresence` (see below).
        - Sliders: `iParams` (see "Sliders").

        # One picture across every monitor (essential)
        The monitors form one canvas, and `fragCoord`/`iResolution` cover all of it, so a stretch across screens looks wrong. Measure everything from `iFocus`:
            vec2 p = (fragCoord - iFocus.xy) / iFocus.z;
        `p` is 0 at the centre of the main monitor, about ±0.5 at its short-side edges, and continues seamlessly onto the other monitors (|p| can reach 2 or more). Put the subject near p = 0 and make the scene extend naturally in every direction: no vignette tied to `iResolution`, no frame or border, nothing that assumes one screen. Use `iResolution` only to fill the whole canvas (for example the extent `iResolution.xy / iFocus.z`).

        # Motion
        - Slow, continuous and graceful. No flashing, strobing, sudden cuts or fast camera moves. The user picks a speed:
          Still = barely perceptible drift; Calm = gentle (the usual choice); Lively = clearly moving but never frantic.
        - It runs for hours: keep arguments of sin/cos well-conditioned (small multipliers on `iTime`, or `mod(iTime, period)` for fast oscillations), and avoid anything that drifts away or saturates over time.

        # Cursor (only when asked; otherwise never read these)
        - `iPointer`: xy = cursor in fragCoord pixels, z = presence 0..1 (0 when the cursor isn't over the desktop; the picture must look complete at 0), w = energy 0..1 (recent speed).
        - `iTrail[i]`, i = 0..15, newest first: xy = a recent cursor position, z = its age in seconds, w = strength (0 = unused).
        - `iClick`: xy = last click, z = seconds since it, w = 1 if there was one.
        - Convert positions the same way: `(iPointer.xy - iFocus.xy) / iFocus.z`.
        - Effects must glide, never pop: soft falloffs (`exp`, `smoothstep`), multiply by `iPointer.z`, and fade trail and click effects with their age, e.g. `smoothstep(0.0, 0.15, age) * (1.0 - smoothstep(0.15, 1.4, age))`.
        - "Reacts" = a gentle, elegant response. "Playful" = a stronger response that is still graceful.
        - Bound displacements; never warp the whole picture.

        # Music (only when asked; otherwise never read these)
        Wavelit analyses what the PC is playing and smooths it, so values glide:
        - `iAudio`: x = bass, y = mid, z = treble, w = overall level, each 0..1.
        - `iBeat`: x = beat pulse 0..1 (swells on a kick, decays in ~0.4 s), y = seconds since the last beat, z = tempo in BPM (0 = unknown), w = beat count.
        - `iSpectrum[16]`: 64 log-spaced bands (30 Hz–16 kHz) packed as vec4s: band i is `iSpectrum[i / 4][i % 4]`. `iPeaks[16]`: falling peak caps, same packing.
        - `iCentroid`: 0 = bass-heavy … 1 = bright music (good for colour). `iAudioPresence`: 0 in silence … 1 while music plays.
        - It must look complete and calm in silence (everything 0, `iAudioPresence` 0): let it idle gently.
        - No flashing: beat effects may swell size, glow or position, but brightness changes stay small (at most about 25%) and eased. Never strobe or flash the whole screen.
        - A smooth lookup: `float band(float i) { i = clamp(i, 0.0, 63.0); int a = int(i); int b = min(a + 1, 63); return mix(iSpectrum[a / 4][a % 4], iSpectrum[b / 4][b % 4], fract(i)); }`

        # Sliders
        You may give the user up to 4 sliders so they can tweak the look without asking again. Declare them on the first line:
            // @params speed 0.2 3 1 | density 0 1 0.5 | glow 0 2 1 | hue -1 1 0
        Each entry is `name min max default` (a short lowercase name). Read them as `iParams.x`, `iParams.y`, `iParams.z`, `iParams.w` in that order. Offer only sliders that clearly change the look (speed, density, size, glow, hue, softness…). Every slider position must stay beautiful and cheap: Wavelit checks the speed with every slider at its maximum.

        # Overlays
        An overlay is a transparent layer drawn over the user's own wallpaper (a photo, a video or another shader), so it must be mostly empty: alpha 0 almost everywhere, with small soft particles, light or haze.
        - Output premultiplied alpha by ending with `fragColor = ilOut(colour, alpha);` (it applies the user's opacity).
        - Use the user's controls: `ilAmount()` (0..1, how much), `ilSize()` (about 0.6..1.6), `ilTime()` (use instead of `iTime`; includes the user's speed), `ilResponse()` (cursor strength 0..1.7; multiply cursor effects by it), and `wavelitTint.rgb` (the user's colour) unless the idea needs its own colours.
        - These helpers already exist in overlays; call them and never redefine them:
            float ilHash(vec2 p); vec2 ilHash2(vec2 p); float ilNoise(vec2 p);
            vec2 ilSpace(vec2 fragCoord);   // the iFocus mapping above
            float ilAmount(); float ilSize(); float ilTime(); float ilResponse();
            float ilPresence();             // iPointer.z scaled by the user's response
            vec2 ilPtr();                   // the cursor in ilSpace units
            mat2 ilRot(float angle);
            float ilEnv(float age, float life);   // grows in over 0.15 s, settles by `life`
            vec2 ilPush(vec2 pos, float radius, float limit);  // offset away from the cursor, trail and clicks, length <= limit
            vec2 ilSwirl(vec2 pos, float radius, float angle); // pos rotated around the cursor
            vec4 ilOut(vec3 colour, float alpha);
        - Particles must be stateless: hash a grid cell for each particle's home and animate it as a function of time. Search the 3x3 neighbouring cells and keep any displacement smaller than one cell.

        # Performance budget
        It must run smoothly on laptops. Aim for under 1.5 ms per frame at 1920x1080 on a mid-range GPU; anything over 3 ms on the user's own GPU is rejected.
        - Every loop is a `for` loop with a constant bound, typically 64 iterations or fewer, never more than 128. No `while` or `do`.
        - At most two nested loops, about 100 inner iterations in total. 4-5 octaves of fbm are plenty. No raymarching beyond 64 steps.

        # Quality
        - Original work; don't reproduce well-known Shadertoy shaders.
        - A refined, restrained palette with gentle contrast. Desktop icons and their labels sit on top, so avoid large areas of pure white and keep the overall brightness moderate.
        - Tone-map bright light (for example `col = 1.0 - exp(-col * 1.4)`), and add a little grain to large gradients to avoid banding.
        - Make it feel finished and premium: depth, soft light, subtle detail.
        - Guard edge cases: never divide by zero, never `smoothstep(a, a, x)`, and `pow` only on non-negative bases.

        # Examples (Wavelit built-ins)
        """;

    private const string OverlayExample = """
        // @params drift 0.3 2 1 | glow 0.5 2 1
        // Wandering glows that drift, circle the cursor and scatter on a click.
        void mainImage(out vec4 o, in vec2 fc) {
          vec2 q = ilSpace(fc);
          float amt = ilAmount(), t = ilTime() * iParams.x, sz = ilSize() * iParams.y;
          vec2 c = ilPtr();
          float pres = ilPresence();
          float a = 0.0;
          const float scale = 7.0;
          vec2 drift = vec2(t * 0.02, t * 0.012);
          vec2 id = floor((q + drift) * scale);
          for (int y = -2; y <= 2; y++)
          for (int x = -2; x <= 2; x++) {
            vec2 cell = id + vec2(float(x), float(y));
            float h = ilHash(cell + 3.7);
            if (h > 0.15 + 0.3 * amt) continue;
            vec2 home = (cell + 0.5 + (ilHash2(cell) - 0.5) * 0.8) / scale - drift;
            home += 0.06 * vec2(sin(t * (0.17 + 0.2 * h) + h * 21.0), cos(t * (0.13 + 0.2 * h) + h * 9.0));
            float pull = pres * exp(-dot(home - c, home - c) / 0.035) * min(ilResponse(), 1.4);
            float angle = t * (0.5 + 0.6 * h) + h * 6.2831;
            vec2 orbit = c + (0.035 + 0.04 * h) * vec2(cos(angle), sin(angle));
            vec2 pos = mix(home, orbit, clamp(pull * 0.9, 0.0, 0.95));
            if (iClick.w > 0.0 && iClick.z < 1.6) {
              vec2 v = pos - ilSpace(iClick.xy);
              float r = length(v);
              pos += (r > 1e-5 ? v / r : vec2(0.0)) * 0.12 * ilEnv(iClick.z, 1.6) * exp(-r * 4.0) * min(ilResponse(), 1.0);
            }
            float d = length(q - pos);
            float blink = mix(pow(0.5 + 0.5 * sin(t * (0.7 + h * 1.3) + h * 50.0), 2.0), 1.0, pull);
            a += blink * (0.00002 * sz * sz / (d * d + 0.00002 * sz * sz) * 0.5 + smoothstep(0.003 * sz, 0.0, d));
          }
          o = ilOut(wavelitTint.rgb, a * (0.6 + 0.4 * amt));
        }
        """;

    private const string JsonTail = """


        # How to answer
        Reply with the JSON object only:
        - `plan`: one or two sentences on how you will build it (the layers, the motion, how it stays cheap). Think it through before writing code.
        - `title`: 2 to 4 words, evocative, no quotes.
        - `description`: one short sentence for the user.
        - `code`: the complete shader (helpers + mainImage), plain GLSL without markdown fences.
        - `mood`: one of nature, space, abstract, neon, cozy, minimal.
        - `usesPointer`: true if the code reads iPointer, iTrail or iClick.
        When asked to fix or change a shader, return the complete updated shader in the same form.
        """;

    private const string ChatTail = """


        # How to answer
        Reply with the complete shader (helpers + mainImage) in a single ```glsl code block, then nothing else.
        """;

    private static readonly string Examples = BuildExamples();

    /// <summary>System prompt for API providers (structured JSON answer). Byte-stable.</summary>
    // Line endings in these literals follow the source file's checkout (CRLF with core.autocrlf), so normalise them:
    // the prompt must be byte-identical on every machine for provider caching.
    public static string System { get; } = Lf(Contract + Examples + JsonTail);

    /// <summary>The same contract for any chat AI (answer in a code block).</summary>
    public static string ChatSystem { get; } = Lf(Contract + Examples + ChatTail);

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string BuildExamples()
    {
        var sb = new StringBuilder();
        void Add(string title, string? code)
        {
            if (code is null) return;
            sb.Append("\n\n## ").Append(title).Append("\n```glsl\n").Append(code.Replace("\r\n", "\n", StringComparison.Ordinal).Trim()).Append("\n```");
        }
        Add("Live wallpaper: Aurora (calm, no cursor)", BuiltInShaders.Source("aurora"));
        Add("Live wallpaper: Lava Lamp (reacts to the cursor; fills every monitor)", BuiltInShaders.Source("lava"));
        Add("Live wallpaper: Ink in Water (the cursor stirs the ink)", BuiltInShaders.Source("ink"));
        Add("Overlay: Fireflies (helpers il* are predefined in overlays; two sliders)", OverlayExample);
        Add("Music wallpaper: Neon Bars (spectrum bars, peak caps, beat ripple; idles in silence)", BuiltInShaders.Source("neonbars"));
        return sb.ToString();
    }

    /// <summary>The variable part: what to make (or change), with the user's options.</summary>
    public static string UserMessage(ShaderRequest request)
    {
        var sb = new StringBuilder();
        if (request.IsRefine)
        {
            sb.Append("Here is the current ").Append(KindName(request.Kind)).Append(":\n```glsl\n")
              .Append(request.Previous!.Trim()).Append("\n```\n\n")
              .Append("Change it: ").Append(Clean(request.RefineNote!)).Append('\n')
              .Append("Keep everything else as it is unless the change needs it. Return the complete updated shader.\n\n")
              .Append("Original idea: ").Append(Clean(request.Prompt)).Append('\n');
        }
        else
        {
            sb.Append("Make a ").Append(KindName(request.Kind)).Append(".\n")
              .Append("Idea: ").Append(Clean(request.Prompt)).Append('\n');
        }
        sb.Append("Cursor: ").Append(request.Interactivity switch
        {
            Interactivity.Reacts => "reacts gently to the cursor",
            Interactivity.Playful => "playful, clearly responds to the cursor and clicks",
            _ => "none (do not read iPointer, iTrail or iClick)",
        }).Append('\n');
        sb.Append("Palette: ").Append(ShaderPalettes.Describe(request.Palette)).Append('\n');
        sb.Append("Speed: ").Append(request.Speed switch
        {
            MotionSpeed.Still => "still (barely perceptible drift)",
            MotionSpeed.Lively => "lively",
            _ => "calm",
        }).Append('\n');
        if (request.Recipe is { } r) r.AppendTo(sb, Clean);
        sb.Append("Music: ").Append(request.Music is { } m
            ? $"reacts to the music playing ({m.Describe()}); read the music uniforms and idle calmly in silence"
            : "none (do not read the music uniforms)").Append('\n');
        return sb.ToString();
    }

    /// <summary>Tells the model why its last shader was rejected, so it can fix it.</summary>
    public static string FixMessage(string problem)
        => "That shader didn't pass Wavelit's check:\n" + problem.Trim()
           + "\n\nFix it and return the complete corrected shader. Keep the look the same.";

    /// <summary>One paste-ready text for any chat AI (claude.ai, ChatGPT, Gemini, …).</summary>
    public static string ChatPrompt(ShaderRequest request) => ChatSystem + "\n\n# Your task\n" + UserMessage(request);

    /// <summary>What to paste back into the chat AI after a failed check.</summary>
    public static string ChatFixMessage(string problem) => FixMessage(problem) + " Reply with one ```glsl code block.";

    private static string KindName(ShaderKind kind) => kind == ShaderKind.Overlay
        ? "overlay (transparent layer over another wallpaper)"
        : "live wallpaper";

    // Prompts are short single-line text; keep them that way so they can't restructure the message.
    private static string Clean(string text)
    {
        string oneLine = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return oneLine.Length > ShaderRequest.MaxPromptLength ? oneLine[..ShaderRequest.MaxPromptLength] : oneLine;
    }
}
