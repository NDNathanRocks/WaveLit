namespace Wavelit.Core.Library.BuiltIn;

/// <summary>
/// Original shaders that ship with Wavelit (written for this project, MIT-compatible, no API key needed).
/// They use the Shadertoy mainImage convention so they run through the same runtime.
/// </summary>
public static class BuiltInShaders
{
    private const string Noise = """
        float hash12(vec2 p) {
            vec3 p3 = fract(vec3(p.xyx) * 0.1031);
            p3 += dot(p3, p3.yzx + 33.33);
            return fract((p3.x + p3.y) * p3.z);
        }
        float vnoise(vec2 p) {
            vec2 i = floor(p), f = fract(p);
            f = f * f * (3.0 - 2.0 * f);
            float a = hash12(i), b = hash12(i + vec2(1, 0)), c = hash12(i + vec2(0, 1)), d = hash12(i + vec2(1, 1));
            return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
        }
        float fbm(vec2 p) {
            float v = 0.0, a = 0.5;
            mat2 r = mat2(0.8, -0.6, 0.6, 0.8);
            for (int i = 0; i < 5; i++) { v += a * vnoise(p); p = r * p * 2.03; a *= 0.5; }
            return v;
        }
        float stars(vec2 fragCoord, float density, float time) {
            vec2 g = fragCoord / iResolution.y * density;
            vec2 id = floor(g), gv = fract(g) - 0.5;
            float h = hash12(id);
            vec2 off = vec2(hash12(id + 7.1), hash12(id + 3.7)) - 0.5;
            float s = step(0.982, h) * smoothstep(0.09, 0.0, length(gv - off * 0.6));
            return s * (0.55 + 0.45 * sin(time * (1.0 + h * 3.0) + h * 60.0));
        }
        """;

    private const string Aurora = Noise + """

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 uv = fragCoord / iResolution.xy;
            vec2 p = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
            float t = iTime * 0.06;

            vec3 col = mix(vec3(0.004, 0.012, 0.035), vec3(0.015, 0.05, 0.10), uv.y);
            col += stars(fragCoord, 180.0, iTime) * smoothstep(0.1, 0.7, uv.y) * 0.9;

            for (int i = 0; i < 3; i++) {
                float fi = float(i);
                float x = p.x * (1.1 - fi * 0.15) + fi * 1.7;
                float base = -0.02 + fi * 0.09
                    + 0.12 * sin(x * 1.3 + t * 5.0 + fi * 2.0)
                    + 0.08 * (fbm(vec2(x * 1.6, t * 2.5 + fi)) - 0.5);
                float d = p.y - base;
                float rays = fbm(vec2(x * 9.0, t * 6.0 + fi * 4.0));
                float curtain = smoothstep(-0.01, 0.02, d) * exp(-max(d, 0.0) * (3.5 + fi)) * (0.35 + 0.9 * rays);
                float edge = exp(-abs(d) * 40.0) * 0.6;
                vec3 tint = mix(vec3(0.15, 1.0, 0.55), vec3(0.55, 0.25, 1.0), clamp(d * 2.5 + fi * 0.25, 0.0, 1.0));
                col += tint * (curtain + edge * rays) * (0.55 - fi * 0.12);
            }

            float ridge = -0.33 + 0.05 * fbm(vec2(p.x * 2.5, 3.0)) + 0.02 * sin(p.x * 7.0);
            float ground = smoothstep(0.0, 0.004, ridge - p.y);
            col = mix(col, vec3(0.003, 0.008, 0.016), ground);
            col = 1.0 - exp(-col * 1.4);
            fragColor = vec4(col, 1.0);
        }
        """;

    private const string LiquidGradient = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 p = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
            float t = iTime * 0.12;
            for (int i = 1; i < 6; i++) {
                float fi = float(i);
                p += vec2(0.32 / fi * sin(fi * p.y * 1.4 + t + fi * 1.7),
                          0.32 / fi * cos(fi * p.x * 1.4 + t * 1.13 + fi * 0.9));
            }
            vec3 c1 = vec3(0.30, 0.16, 0.82), c2 = vec3(0.97, 0.36, 0.55);
            vec3 c3 = vec3(0.08, 0.66, 0.90), c4 = vec3(0.99, 0.72, 0.34);
            float a = 0.5 + 0.5 * sin(p.x * 1.2);
            float b = 0.5 + 0.5 * cos(p.y * 1.3);
            vec3 col = mix(mix(c1, c2, a), mix(c3, c4, a), b);
            col *= 0.86 + 0.14 * sin(p.x * 3.0 + p.y * 2.0);
            float grain = fract(sin(dot(fragCoord, vec2(12.9898, 78.233))) * 43758.5453);
            col += (grain - 0.5) * 0.018;
            fragColor = vec4(col, 1.0);
        }
        """;

    private const string Nebula = Noise + """

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 p = (fragCoord - 0.5 * iResolution.xy) / iResolution.y * 1.6;
            float t = iTime * 0.015;
            vec2 q = vec2(fbm(p + t), fbm(p - t + vec2(5.2, 1.3)));
            vec2 r = vec2(fbm(p + q * 2.0 + vec2(1.7, 9.2) + t * 2.0), fbm(p + q * 2.0 + vec2(8.3, 2.8) - t * 1.5));
            float f = fbm(p + r * 2.0);

            vec3 col = mix(vec3(0.02, 0.0, 0.05), vec3(0.42, 0.06, 0.45), clamp(f * f * 2.2, 0.0, 1.0));
            col = mix(col, vec3(0.04, 0.30, 0.58), clamp(length(q) * 0.55, 0.0, 1.0));
            col = mix(col, vec3(1.0, 0.58, 0.32), clamp(r.x * r.x * 0.9, 0.0, 1.0) * f);
            col *= f * 1.7;
            col += stars(fragCoord, 140.0, iTime) * (1.0 - f) * 0.9;
            col += stars(fragCoord + 91.0, 60.0, iTime * 0.7) * 0.5;
            col = 1.0 - exp(-col * 1.3);
            fragColor = vec4(col, 1.0);
        }
        """;

    private const string Synthwave = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
            const float horizon = -0.06;
            vec3 col;
            if (uv.y > horizon) {
                float h = uv.y - horizon;
                col = mix(vec3(0.85, 0.22, 0.48), vec3(0.04, 0.02, 0.14), smoothstep(0.0, 0.55, h));
                vec2 sc = uv - vec2(0.0, 0.17);
                float d = length(sc);
                float disc = smoothstep(0.24, 0.236, d);
                float bandWidth = smoothstep(0.03, -0.22, sc.y) * 0.55;
                float gaps = step(fract(sc.y * 24.0 - iTime * 0.12), bandWidth);
                vec3 sun = mix(vec3(1.0, 0.86, 0.32), vec3(1.0, 0.18, 0.52), smoothstep(0.22, -0.22, sc.y));
                col = mix(col, sun, disc * (1.0 - gaps));
                col += vec3(1.0, 0.3, 0.55) * 0.22 * exp(-d * 4.5);
            } else {
                float depth = 0.11 / (horizon - uv.y + 0.0005);
                vec2 g = vec2(uv.x * depth * 2.2, depth + iTime * 0.55);
                vec2 fw = fwidth(g);
                vec2 a = abs(fract(g) - 0.5) / max(fw, vec2(1e-4));
                float line = 1.0 - min(min(a.x, a.y) * 0.6, 1.0);
                float fade = exp(-depth * 0.08);
                col = vec3(0.03, 0.0, 0.08) + vec3(1.0, 0.18, 0.75) * line * fade;
                col += vec3(0.9, 0.2, 0.6) * exp(-(horizon - uv.y) * 18.0) * 0.5;
            }
            col = pow(col, vec3(0.95));
            fragColor = vec4(col, 1.0);
        }
        """;

    private const string Silk = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 uv = fragCoord / iResolution.xy;
            vec2 p = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
            float t = iTime * 0.18;
            vec3 col = mix(vec3(0.015, 0.02, 0.05), vec3(0.04, 0.02, 0.08), uv.y);
            for (int i = 0; i < 7; i++) {
                float fi = float(i);
                float y = 0.13 * sin(p.x * (1.4 + fi * 0.27) + t * (1.0 + fi * 0.13) + fi * 1.3)
                        + 0.05 * sin(p.x * 3.7 - t * 1.3 + fi)
                        + (fi - 3.0) * 0.055;
                float d = abs(p.y - y);
                float w = 0.0018 + 0.0035 * (0.5 + 0.5 * sin(p.x * 2.0 + t + fi));
                float glow = w / (d + 0.0016);
                vec3 tint = 0.5 + 0.5 * cos(6.2831 * (vec3(0.0, 0.33, 0.67) + fi * 0.07 + p.x * 0.08 + t * 0.05));
                col += tint * glow * 0.17;
            }
            col = 1.0 - exp(-col * 1.25);
            fragColor = vec4(col, 1.0);
        }
        """;

    private const string Bokeh = """
        float h11(float n) { return fract(sin(n * 12.9898) * 43758.5453); }
        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 p = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
            float aspect = iResolution.x / iResolution.y;
            vec3 col = mix(vec3(0.03, 0.02, 0.07), vec3(0.09, 0.03, 0.11), fragCoord.y / iResolution.y);
            for (int i = 0; i < 36; i++) {
                float fi = float(i);
                float a = h11(fi), b = h11(fi + 17.0), c = h11(fi + 41.0);
                vec2 center = vec2((a - 0.5) * aspect * 1.1 + 0.08 * sin(iTime * 0.07 + fi),
                                   mod(b + iTime * 0.008 * (0.4 + c), 1.3) - 0.65);
                float r = 0.03 + 0.09 * c;
                float d = length(p - center);
                float disc = smoothstep(r, r * 0.82, d) * (0.10 + 0.22 * b);
                float rim = smoothstep(r * 0.88, r, d) * smoothstep(r * 1.03, r, d) * 0.08;
                vec3 tint = 0.55 + 0.45 * cos(6.2831 * (vec3(0.0, 0.15, 0.35) + a * 0.6 + 0.55));
                col += tint * (disc + rim);
            }
            col = 1.0 - exp(-col * 1.5);
            fragColor = vec4(col, 1.0);
        }
        """;

    private static readonly (string Key, string Title, string Code)[] Definitions =
    [
        ("aurora", "Aurora", Aurora),
        ("liquid", "Liquid Gradient", LiquidGradient),
        ("nebula", "Nebula", Nebula),
        ("synthwave", "Synthwave Sunset", Synthwave),
        ("silk", "Silk Ribbons", Silk),
        ("bokeh", "Bokeh Lights", Bokeh),
    ];

    public const string DefaultItemId = "builtin-aurora";

    public static IReadOnlyList<WallpaperItem> Items { get; } = Definitions.Select(d => new WallpaperItem
    {
        Id = "builtin-" + d.Key,
        Kind = WallpaperKind.Shader,
        Source = WallpaperSources.BuiltIn,
        Title = d.Title,
        Author = "Wavelit",
        License = "Included with Wavelit",
        ShaderId = d.Key,
        AddedAt = DateTimeOffset.UnixEpoch,
    }).ToList();

    public static WallpaperItem? Find(string id) => Items.FirstOrDefault(i => i.Id == id);

    public static ShaderProgram? Program(string key)
    {
        var def = Definitions.FirstOrDefault(d => d.Key == key);
        return def.Code is null ? null : new ShaderProgram(def.Title, [new ShaderPass("image", def.Code, null, [])]);
    }
}
