using System.Globalization;
using System.Text;

namespace Wavelit.Core.Create.Shaders;

/// <summary>
/// The Overlay Designer: an overlay built from choices instead of a prompt, so anyone can make their own without
/// a key. The choices become GLSL from one template that follows the same rules as the built-in overlays
/// (stateless hashed particles, 3x3 cell search, bounded cursor displacement, premultiplied output through the
/// il* helpers), and the result goes through the same lab check as every other Studio shader.
/// </summary>
/// <param name="Shape">One of <see cref="Shapes"/>.</param>
/// <param name="Motion">One of <see cref="Motions"/>.</param>
/// <param name="Cursor">One of <see cref="Cursors"/>.</param>
/// <param name="Density">0 … 1 (a few … many).</param>
/// <param name="Size">0 … 1.</param>
/// <param name="Speed">0 … 1.</param>
/// <param name="Twinkle">0 … 1 (steady … blinking).</param>
/// <param name="Wind">0 … 1 (straight … wavering).</param>
/// <param name="Glow">0 … 1 (crisp … soft halo).</param>
/// <param name="Music">The particles move on the music clock and glow on the beat.</param>
public sealed record OverlayDesign(
    string Shape = "petal",
    string Motion = "fall",
    string Cursor = "part",
    double Density = 0.5,
    double Size = 0.5,
    double Speed = 0.5,
    double Twinkle = 0.3,
    double Wind = 0.5,
    double Glow = 0.3,
    bool Music = false)
{
    /// <summary>The "model" recorded on designer creations (not AI-made).</summary>
    public const string Author = "designer";

    public static readonly IReadOnlyList<(string Id, string Label, string Blurb)> Shapes =
    [
        ("petal", "Petals", "Soft sakura petals that tumble as they go."),
        ("flake", "Snowflakes", "Six-armed flakes, each turning slowly."),
        ("dot", "Dust", "Tiny glowing motes."),
        ("spark", "Embers", "Warm flickering sparks."),
        ("bubble", "Bubbles", "Thin wobbling rings."),
        ("leaf", "Leaves", "Little teardrop leaves that spin."),
        ("star", "Stars", "Four-point twinkling stars."),
        ("heart", "Hearts", "Small hearts."),
        ("confetti", "Confetti", "Tumbling paper squares."),
        ("orb", "Bokeh", "Large out-of-focus light discs."),
    ];

    public static readonly IReadOnlyList<(string Id, string Label)> Motions =
    [
        ("fall", "Fall"), ("rise", "Rise"), ("drift", "Drift sideways"), ("swirl", "Swirl around the centre"), ("still", "Hang in the air"),
    ];

    public static readonly IReadOnlyList<(string Id, string Label)> Cursors =
    [
        ("part", "Parts around the cursor"), ("gather", "Gathers to the cursor"), ("swirl", "Swirls around the cursor"), ("none", "Ignores the cursor"),
    ];

    public bool UsesPointer => Cursor != "none";

    public OverlayDesign Normalize() => this with
    {
        Shape = Shapes.Any(s => s.Id == Shape) ? Shape : "petal",
        Motion = Motions.Any(m => m.Id == Motion) ? Motion : "fall",
        Cursor = Cursors.Any(c => c.Id == Cursor) ? Cursor : "part",
        Density = Unit(Density, 0.5), Size = Unit(Size, 0.5), Speed = Unit(Speed, 0.5),
        Twinkle = Unit(Twinkle, 0.3), Wind = Unit(Wind, 0.5), Glow = Unit(Glow, 0.3),
    };

    public string Title
    {
        get
        {
            var d = Normalize();
            string shape = Shapes.First(s => s.Id == d.Shape).Label;
            string motion = d.Motion switch { "rise" => "Rising", "drift" => "Drifting", "swirl" => "Swirling", "still" => "Floating", _ => "Falling" };
            return $"{motion} {shape}";
        }
    }

    /// <summary>One sentence for the gallery card.</summary>
    public string Describe()
    {
        var d = Normalize();
        var sb = new StringBuilder(Shapes.First(s => s.Id == d.Shape).Blurb.TrimEnd('.'));
        sb.Append(d.Cursor switch
        {
            "part" => ", parting around your cursor",
            "gather" => ", gathering to your cursor",
            "swirl" => ", swirling around your cursor",
            _ => "",
        });
        if (d.Music) sb.Append(", moving to the music");
        return sb.Append('.').ToString();
    }

    /// <summary>The overlay's GLSL (helpers il* are predefined; see Web/interact.js).</summary>
    public string ToGlsl()
    {
        var d = Normalize();
        string time = d.Music ? "ilMusic()" : "ilTime()";
        string spin = d.Shape is "petal" or "leaf" or "confetti" or "flake" or "star" ? "1.0" : "0.0";
        string shapeScale = d.Shape switch { "orb" => "3.2", "bubble" => "1.6", "confetti" => "1.1", "flake" => "1.3", "heart" => "1.2", "star" => "1.3", _ => "1.0" };
        string velocity = d.Motion switch
        {
            "rise" => "vec2(0.18, 1.0)",
            "drift" => "vec2(1.0, -0.2)",
            "fall" => "vec2(0.25, -1.0)",
            _ => "vec2(0.0, 0.0)",
        };
        string frame = d.Motion == "swirl" ? "ilRot(-t * 0.03 * (1.0 + 0.4 * fl)) * p" : "p";
        string cursor = d.Cursor switch
        {
            "part" => "pos += ilPush(pos, 0.14, 0.6 / scale);",
            "gather" => """
                {
                  vec2 c = ilPtr();
                  float pull = ilPresence() * exp(-dot(pos - c, pos - c) / 0.04);
                  float ang = t * (0.5 + 0.6 * h) + h * 6.2831;
                  vec2 orbit = c + (0.03 + 0.05 * h) * vec2(cos(ang), sin(ang));
                  pos = mix(pos, orbit, clamp(pull * 0.9, 0.0, 0.9));
                }
                """,
            "swirl" => "pos = ilSwirl(pos, 0.12, 1.4 * min(ilResponse(), 1.5)); pos += ilPush(pos, 0.1, 0.4 / scale);",
            _ => "",
        };
        string shape = d.Shape switch
        {
            "petal" => "float cov = smoothstep(1.0, 0.55, length(q / vec2(1.0, 0.55 + 0.25 * sin(t * 1.3 + h * 9.0)))); hi = smoothstep(1.0, 0.0, length(q)) * 0.25;",
            "flake" => "float rr = length(q); float arm = abs(cos(atan(q.y, q.x) * 3.0)); float cov = smoothstep(1.0, 0.25, rr / (0.3 + 0.7 * arm)) + smoothstep(0.35, 0.0, rr); cov = min(cov, 1.0);",
            "dot" => "float cov = smoothstep(1.0, 0.35, length(q));",
            "spark" => "float rr = length(q * vec2(1.8, 0.7)); float cov = smoothstep(1.0, 0.0, rr) * (0.7 + 0.3 * sin(t * (9.0 + h * 7.0) + h * 13.0)); hi = smoothstep(1.0, 0.0, rr) * 0.6 * h;",
            "bubble" => "float rr = length(q); float cov = smoothstep(0.2, 0.05, abs(rr - 0.8)) * 0.9 + smoothstep(1.0, 0.0, rr) * 0.08; hi = smoothstep(0.25, 0.0, length(q - vec2(-0.35, 0.35))) * 0.6;",
            "leaf" => "float cov = smoothstep(1.0, 0.65, length(q / vec2(0.62, 1.0)) + 0.3 * q.y);",
            "star" => "float cov = smoothstep(1.0, 0.6, sqrt(abs(q.x)) + sqrt(abs(q.y)));",
            "heart" => "vec2 hq = q * 1.25; float hx = hq.x, hy = hq.y + 0.25; float aa = hx * hx + hy * hy - 1.0; float f = aa * aa * aa - hx * hx * hy * hy * hy; float cov = smoothstep(0.15, -0.15, f);",
            "confetti" => "float cov = smoothstep(1.0, 0.82, max(abs(q.x), abs(q.y) * 1.6));",
            _ => "float rr = length(q); float cov = smoothstep(1.0, 0.92, rr) * (0.3 + 0.7 * smoothstep(0.72, 0.98, rr));",
        };
        string colour = d.Shape switch
        {
            "spark" => "vec3 col = mix(wavelitTint.rgb, vec3(1.0, 0.3, 0.1), h * 1.6);",
            "orb" => "vec3 col = wavelitTint.rgb * (0.75 + 0.25 * cos(6.2831 * (h + vec3(0.0, 0.2, 0.4))));",
            "confetti" => "vec3 col = mix(wavelitTint.rgb, 0.5 + 0.5 * cos(6.2831 * (h * 3.0 + vec3(0.0, 0.33, 0.67))), 0.7);",
            _ => "vec3 col = mix(wavelitTint.rgb, vec3(1.0), hi);",
        };
        // ilBeat appears only when music is on, so a music-off design never turns the audio feed on.
        string beat = d.Music ? "ilBeat()" : "0.0";

        var sb = new StringBuilder();
        sb.Append("// @params amount 0 1 ").Append(F(d.Density)).Append(" | size 0.4 2 ").Append(F(0.6 + 1.4 * d.Size))
          .Append(" | speed 0.2 2.5 ").Append(F(0.3 + 2.0 * d.Speed)).Append(" | glow 0 1 ").Append(F(d.Glow)).Append('\n');
        sb.Append("// ").Append(Title).Append(": made in Wavelit's Overlay Designer.\n");
        sb.Append($$"""
            const float odTwinkle = {{F(d.Twinkle)}};
            const float odWind = {{F(d.Wind)}};
            const float odSpin = {{spin}};
            void mainImage(out vec4 o, in vec2 fc) {
              vec2 p = ilSpace(fc);
              float amt = ilAmount(), t = {{time}} * iParams.z, sz = ilSize() * iParams.y * {{shapeScale}};
              vec3 acc = vec3(0.0);
              float a = 0.0;
              for (int layer = 0; layer < 2; layer++) {
                float fl = float(layer);
                float scale = 4.5 + fl * 3.0;
                vec2 fr = {{frame}};
                vec2 motion = {{velocity}} * t * (0.045 + fl * 0.02);
                vec2 id = floor((fr - motion) * scale);
                for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++) {
                  vec2 cell = id + vec2(float(x), float(y));
                  vec2 key = cell + fl * 41.0;
                  float h = ilHash(key);
                  if (h > 0.05 + 0.4 * iParams.x * (0.5 + 0.5 * amt)) continue;
                  vec2 jit = ilHash2(key + 2.7) - 0.5;
                  vec2 pos = (cell + 0.5 + jit * 0.7) / scale + motion;
                  pos.x += odWind * 0.03 * sin(t * (0.6 + h) + h * 20.0);
                  pos.y += odWind * 0.012 * cos(t * (0.5 + h * 0.7) + h * 11.0);
                  {{cursor}}
                  float r = (0.011 + 0.007 * h) * sz * (1.0 - fl * 0.25);
                  float ang = odSpin * (t * (0.4 + h) * (h > 0.06 ? 1.0 : -1.0) + h * 6.28);
                  vec2 q = ilRot(ang) * (fr - pos) / r;
                  float hi = 0.0;
                  {{shape}}
                  cov *= mix(1.0, 0.5 + 0.5 * sin(t * (0.8 + h * 1.7) + h * 40.0), odTwinkle);
                  cov += iParams.w * 0.35 * exp(-dot(q, q) * 0.6) * (0.6 + 0.4 * {{beat}});
                  cov *= (0.9 - fl * 0.3);
                  {{colour}}
                  acc += col * cov;
                  a += cov;
                }
              }
              float k = (0.6 + 0.4 * amt) * (1.0 + 0.3 * {{beat}});
              o = vec4(clamp(acc, 0.0, 1.0) * k, clamp(a, 0.0, 1.0) * k) * wavelitStyle.w;
            }
            """);
        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static double Unit(double v, double fallback) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : fallback;
}
