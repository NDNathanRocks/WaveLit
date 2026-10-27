namespace Wavelit.Core.Library.BuiltIn;

/// <summary>
/// Original shaders that ship with Wavelit (written for this project, MIT-compatible, no API key needed).
/// They use the Shadertoy mainImage convention so they run through the same runtime.
/// They are canvas-aware: coordinates are measured from <c>iFocus</c> (the primary display's centre)
/// in units of that display's short side, so on several displays the picture continues across the
/// bezels with its subject on the primary screen, and keeps the same scale in portrait.
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
        vec2 focusSpace(vec2 fragCoord) { return (fragCoord - iFocus.xy) / iFocus.z; }
        float stars(vec2 fragCoord, float density, float time) {
            vec2 g = fragCoord / iFocus.z * density;
            vec2 id = floor(g), gv = fract(g) - 0.5;
            float h = hash12(id);
            vec2 off = vec2(hash12(id + 7.1), hash12(id + 3.7)) - 0.5;
            float s = step(0.982, h) * smoothstep(0.09, 0.0, length(gv - off * 0.6));
            return s * (0.55 + 0.45 * sin(time * (1.0 + h * 3.0) + h * 60.0));
        }
        """;

    private const string Aurora = Noise + """

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 p = focusSpace(fragCoord);
            float v = clamp(p.y + 0.5, 0.0, 1.0);
            float t = iTime * 0.06;

            vec3 col = mix(vec3(0.004, 0.012, 0.035), vec3(0.015, 0.05, 0.10), v);
            col += stars(fragCoord, 180.0, iTime) * smoothstep(0.1, 0.7, v) * 0.9;

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
            vec2 p = (fragCoord - iFocus.xy) / iFocus.z;
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
            vec2 p = focusSpace(fragCoord) * 1.6;
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
            vec2 uv = (fragCoord - iFocus.xy) / iFocus.z;
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
            vec2 p = (fragCoord - iFocus.xy) / iFocus.z;
            float t = iTime * 0.18;
            vec3 col = mix(vec3(0.015, 0.02, 0.05), vec3(0.04, 0.02, 0.08), clamp(p.y + 0.5, 0.0, 1.0));
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
            vec2 p = (fragCoord - iFocus.xy) / iFocus.z;
            // Lights fill the whole canvas (every display), at the same density as on one screen.
            vec2 lo = -iFocus.xy / iFocus.z - 0.15;
            vec2 ext = iResolution.xy / iFocus.z + 0.3;
            int count = int(clamp(36.0 * ext.x * ext.y / 2.7, 36.0, 120.0));
            vec3 col = mix(vec3(0.03, 0.02, 0.07), vec3(0.09, 0.03, 0.11), clamp(p.y + 0.5, 0.0, 1.0));
            for (int i = 0; i < 120; i++) {
                if (i >= count) break;
                float fi = float(i);
                float a = h11(fi), b = h11(fi + 17.0), c = h11(fi + 41.0);
                vec2 center = vec2(lo.x + a * ext.x + 0.08 * sin(iTime * 0.07 + fi),
                                   lo.y + mod(b * ext.y + iTime * 0.0104 * (0.4 + c), ext.y));
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

    // ---- Round 3 (plan §16.4). Same conventions; the interactive ones read iPointer (cursor over the
    // desktop, xy in fragCoord space, z = presence) and stay calm when it's 0.

    private const string PointerHelpers = """

        vec2 pointerSpace() { return (iPointer.xy - iFocus.xy) / iFocus.z; }
        """;

    private const string OceanSwell = Noise + """

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 p = focusSpace(fragCoord);
            float t = iTime;
            const float horizon = 0.06;
            vec2 sun = vec2(0.0, horizon + 0.11);

            float sy = max(p.y - horizon, 0.0);
            vec3 sky = mix(vec3(1.0, 0.56, 0.34), vec3(0.22, 0.2, 0.42), smoothstep(0.0, 0.5, sy));
            sky = mix(sky, vec3(0.04, 0.05, 0.16), smoothstep(0.45, 1.2, sy));
            float sd = length(p - sun);
            sky += vec3(1.0, 0.78, 0.5) * (smoothstep(0.066, 0.062, sd) * 1.1 + exp(-sd * 5.0) * 0.4);
            float band = fbm(vec2(p.x * 1.3 + t * 0.006, p.y * 7.0));
            sky = mix(sky, sky * 0.7 + vec3(0.32, 0.14, 0.18), smoothstep(0.55, 0.8, band) * smoothstep(horizon + 0.02, horizon + 0.3, p.y) * 0.55);
            vec3 col = sky;

            if (p.y < horizon) {
                float depth = horizon - p.y;
                float persp = 1.0 / (depth + 0.025);
                vec2 w = vec2(p.x * persp, persp);
                float swell = fbm(vec2(w.x * 0.3, w.y * 0.55 - t * 0.3));
                float chop = vnoise(vec2(w.x * 1.6 + swell * 2.0, w.y * 2.1 - t * 0.8));
                float h = swell * 0.65 + chop * 0.35;
                vec3 water = mix(vec3(0.1, 0.17, 0.28), vec3(0.015, 0.05, 0.11), smoothstep(0.0, 0.45, depth));
                water = mix(water, vec3(0.78, 0.44, 0.36), exp(-depth * 16.0) * 0.55);
                float path = exp(-abs(p.x - sun.x) * (2.6 + 0.35 / (depth + 0.02)));
                water += vec3(1.0, 0.72, 0.42) * (smoothstep(0.6, 0.88, h) * path * 1.5 + path * 0.1);
                water += vec3(0.45, 0.5, 0.58) * smoothstep(0.72, 0.9, chop) * 0.05 * smoothstep(0.0, 0.2, depth);
                col = mix(water, vec3(0.95, 0.62, 0.48), exp(-depth * 70.0) * 0.6);
            }
            col = 1.0 - exp(-col * 1.25);
            fragColor = vec4(col, 1.0);
        }
        """;

    private const string RainOnGlass = Noise + PointerHelpers + """

        // Out-of-focus city lights behind the glass.
        vec3 cityBokeh(vec2 p) {
            vec3 col = mix(vec3(0.015, 0.02, 0.04), vec3(0.05, 0.04, 0.08), p.y * 0.5 + 0.5);
            for (int i = 0; i < 3; i++) {
                float fi = float(i);
                vec2 q = p * (1.7 + fi * 1.3) + vec2(fi * 7.3, fi * 3.1);
                vec2 id = floor(q), gv = fract(q) - 0.5;
                float h = hash12(id + fi * 17.0);
                vec2 off = (vec2(hash12(id + 3.1), hash12(id + 8.7)) - 0.5) * 0.3;
                float r = 0.22 + 0.12 * hash12(id + 5.5);
                float lit = step(0.42, h) * smoothstep(r, r * 0.55, length(gv - off));
                vec3 tint = 0.6 + 0.4 * cos(6.2831 * (h + vec3(0.0, 0.12, 0.3)));
                col += tint * lit * (0.3 + 0.35 * hash12(id + 1.7)) * (0.9 - fi * 0.2);
            }
            col += vec3(1.0, 0.55, 0.25) * exp(-(p.y + 0.6) * 2.5) * 0.06;
            return col;
        }

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 p = focusSpace(fragCoord);
            float t = iTime;
            vec2 n = vec2(0.0);
            float drops = 0.0;

            // Still droplets that slowly form and evaporate.
            vec2 q = p * 13.0;
            vec2 id = floor(q), gv = fract(q) - 0.5;
            float h = hash12(id);
            vec2 off = (vec2(hash12(id + 1.3), hash12(id + 4.1)) - 0.5) * 0.45;
            float life = sin(3.14159 * fract(t * 0.03 + h * 7.0));
            float r = mix(0.08, 0.24, h * h) * step(0.55, h) * life;
            vec2 dv = gv - off;
            float m = r > 0.001 ? smoothstep(r, r * 0.6, length(dv * vec2(1.0, 1.15))) : 0.0; // smoothstep(0, 0, x) is undefined
            n += dv * m * 0.6;
            drops = max(drops, m);

            // Drops sliding down, each in its own column, with a trail of beads left behind.
            vec2 a = vec2(6.0, 1.4);
            vec2 s = p * a;
            float column = floor(s.x);
            float hc = hash12(vec2(column, 11.0));
            s.y += t * (0.18 + 0.22 * hc) + hc * 10.0;
            vec2 cid = floor(s), cgv = fract(s) - 0.5;
            float hd = hash12(cid + 2.3);
            if (hd > 0.3) {
                float x = (hd - 0.5) * 0.55 + sin(s.y * 3.0 + hd * 6.0) * 0.07;
                vec2 dp = (cgv - vec2(x, 0.0)) / a;
                float rad = 0.011 + 0.009 * hd;
                float dm = smoothstep(rad, rad * 0.55, length(dp * vec2(1.0, 0.8)));
                n += dp / rad * dm * 0.03;
                drops = max(drops, dm);
                float trail = smoothstep(rad * 0.6, 0.0, abs(dp.x)) * smoothstep(0.0, 0.01, dp.y) * smoothstep(0.32, 0.0, dp.y);
                float bead = fract(p.y * 34.0 + hd * 3.0) - 0.5;
                float bm = smoothstep(0.012, 0.004, length(vec2(dp.x, bead / 34.0))) * trail;
                n += vec2(dp.x, bead / 34.0) * bm * 1.5;
                drops = max(drops, bm);
            }

            // The cursor wipes the fog off the glass where it rests.
            float wipe = iPointer.z * smoothstep(0.16, 0.05, length(p - pointerSpace()));
            float fog = 0.55 * (1.0 - wipe);
            vec3 behind = cityBokeh(p);
            vec3 col = mix(behind, behind * 0.45 + vec3(0.05, 0.055, 0.07), fog);
            vec3 through = cityBokeh(p - n * 1.2) * 1.35;
            col = mix(col, through, drops);
            col += vec3(0.7, 0.75, 0.85) * drops * smoothstep(0.0, 0.02, n.y) * 0.08;
            fragColor = vec4(col, 1.0);
        }
        """;

    private const string WarpStars = Noise + PointerHelpers + """

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 p = focusSpace(fragCoord);
            // The vanishing point leans gently towards the cursor.
            vec2 center = pointerSpace() * 0.18 * iPointer.z;
            vec2 d = p - center;
            float r = length(d);
            float ang = atan(d.y, d.x);
            vec3 col = vec3(0.004, 0.006, 0.018) + vec3(0.05, 0.03, 0.1) * exp(-r * 2.2);
            col += vec3(0.08, 0.04, 0.12) * fbm(p * 1.5 + iTime * 0.01) * 0.6;
            float t = iTime * 0.1;
            for (int i = 0; i < 4; i++) {
                float fi = float(i);
                float sectors = 80.0 + fi * 46.0;
                float sa = ang / 6.2831 * sectors;
                float sid = floor(sa);
                float h = hash12(vec2(sid, fi * 13.1));
                float h2 = hash12(vec2(sid, fi * 13.1 + 5.0));
                if (h < 0.15) continue;
                float z = fract(h2 + t * (0.5 + 0.7 * h));
                float rr = 0.02 + z * z * 2.2;
                float len = 0.004 + 0.07 * z * z;
                float along = smoothstep(rr - len, rr, r) * smoothstep(rr + 0.003, rr, r);
                float across = abs(fract(sa) - 0.5 - (h - 0.5) * 0.5) * 6.2831 * r / sectors;
                float line = smoothstep(0.002 + 0.003 * z, 0.0, across);
                vec3 tint = mix(vec3(0.7, 0.8, 1.0), vec3(1.0, 0.85, 0.75), h2);
                col += tint * along * line * (0.5 + 2.0 * z) * smoothstep(0.0, 0.15, z);
            }
            col = 1.0 - exp(-col * 1.6);
            fragColor = vec4(col, 1.0);
        }
        """;

    private const string LavaLamp = Noise + PointerHelpers + """

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 p = focusSpace(fragCoord);
            vec2 lo = -iFocus.xy / iFocus.z;
            vec2 ext = iResolution.xy / iFocus.z;
            int count = int(clamp(10.0 * ext.x * ext.y / 1.78, 10.0, 26.0));
            float field = 0.0;
            for (int i = 0; i < 26; i++) {
                if (i >= count) break;
                float fi = float(i);
                float h1 = hash12(vec2(fi, 1.0)), h2 = hash12(vec2(fi, 2.0)), h3 = hash12(vec2(fi, 3.0));
                float x = lo.x + ext.x * (0.06 + 0.88 * h1) + 0.07 * sin(iTime * 0.11 * (0.5 + h3) + fi);
                float phase = fract(iTime / (45.0 + 35.0 * h2) + h3);
                float y = lo.y - 0.25 + (ext.y + 0.5) * (0.5 - 0.5 * cos(phase * 6.2831));
                float r = 0.06 + 0.07 * h2;
                vec2 dd = p - vec2(x, y);
                field += r * r / (dot(dd, dd) + 1e-4);
            }
            // The cursor carries a warm blob of its own that merges with the others.
            vec2 dp = p - pointerSpace();
            field += iPointer.z * 0.0065 / (dot(dp, dp) + 1e-4);

            vec3 bg = mix(vec3(0.05, 0.015, 0.07), vec3(0.16, 0.035, 0.09), smoothstep(-0.6, 0.6, p.y));
            bg += vec3(0.5, 0.12, 0.05) * exp(-length(p - vec2(0.0, -0.75)) * 1.6) * 0.35;
            vec3 blob = mix(vec3(1.0, 0.36, 0.1), vec3(0.95, 0.12, 0.42), smoothstep(-0.5, 0.6, p.y));
            blob = mix(blob, vec3(1.0, 0.82, 0.45), smoothstep(1.5, 4.0, field) * 0.55);
            float body = smoothstep(0.88, 1.06, field);
            vec3 col = mix(bg, blob * (0.75 + 0.25 * smoothstep(1.0, 2.5, field)), body);
            col += blob * smoothstep(0.35, 0.95, field) * (1.0 - body) * 0.3;
            fragColor = vec4(col, 1.0);
        }
        """;

    private const string GradientMesh = Noise + PointerHelpers + """

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 p = focusSpace(fragCoord);
            float t = iTime * 0.025;
            vec2 q = p * 0.85 + 0.3 * vec2(fbm(p * 0.7 + t), fbm(p * 0.7 - t + 3.1));
            float a = 0.5 + 0.5 * sin(q.x * 2.0 + t * 6.0);
            float b = 0.5 + 0.5 * sin(q.y * 2.6 - t * 4.0 + 1.3);
            vec3 c1 = vec3(0.86, 0.5, 0.52), c2 = vec3(0.42, 0.5, 0.86);
            vec3 c3 = vec3(0.45, 0.78, 0.74), c4 = vec3(0.88, 0.74, 0.5);
            vec3 col = mix(mix(c1, c2, a), mix(c3, c4, a), b) * 0.82;
            vec2 dp = p - pointerSpace();
            col += vec3(1.0, 0.96, 0.9) * iPointer.z * 0.14 * exp(-dot(dp, dp) * 14.0);
            col += (hash12(fragCoord + fract(iTime) * 113.0) - 0.5) * 0.012; // grain hides banding
            fragColor = vec4(col, 1.0);
        }
        """;

    private const string DriftingClouds = Noise + """

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 p = focusSpace(fragCoord);
            float t = iTime * 0.01;
            vec3 col = mix(vec3(0.62, 0.8, 0.96), vec3(0.2, 0.42, 0.8), smoothstep(-0.5, 0.9, p.y));
            vec2 sun = vec2(0.38, 0.3);
            float sd = length(p - sun);
            col += vec3(1.0, 0.9, 0.72) * (exp(-sd * 4.0) * 0.32 + smoothstep(0.034, 0.03, sd));
            for (int i = 0; i < 3; i++) {
                float fi = float(i);
                float sc = 1.1 + fi * 0.75;
                vec2 q = p * sc + vec2(t * (1.0 + fi * 0.8) * sc * 3.0, fi * 4.7);
                float d = fbm(q + 0.5 * fbm(q * 1.7 + t));
                float cover = smoothstep(0.5 - fi * 0.03, 0.76, d);
                float shade = fbm(q * 1.3 + vec2(0.3));
                vec3 cc = mix(vec3(0.74, 0.79, 0.88), vec3(1.0), smoothstep(0.35, 0.72, shade));
                cc += vec3(1.0, 0.86, 0.62) * exp(-sd * 2.5) * 0.22;
                col = mix(col, cc, cover * (0.88 - fi * 0.16));
            }
            fragColor = vec4(col, 1.0);
        }
        """;

    private const string InkInWater = Noise + PointerHelpers + """

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 p = focusSpace(fragCoord);
            // The cursor stirs the ink around it.
            vec2 c = pointerSpace();
            vec2 d = p - c;
            float swirl = iPointer.z * (1.6 + 1.5 * iPointer.w) * exp(-dot(d, d) * 22.0);
            float s = sin(swirl), co = cos(swirl);
            p = c + mat2(co, -s, s, co) * d;

            float t = iTime * 0.035;
            vec2 q = vec2(fbm(p * 1.3 + vec2(0.0, t)), fbm(p * 1.3 + vec2(5.2, 1.3) - t));
            vec2 r = vec2(fbm(p * 1.3 + 3.0 * q + vec2(1.7, 9.2) + t * 1.4), fbm(p * 1.3 + 3.0 * q + vec2(8.3, 2.8) - t));
            float f = fbm(p * 1.3 + 3.0 * r);
            vec3 col = mix(vec3(0.015, 0.015, 0.04), vec3(0.1, 0.34, 0.7), clamp(f * f * 2.0, 0.0, 1.0));
            col = mix(col, vec3(0.9, 0.24, 0.5), clamp(length(q) * 0.9 - 0.3, 0.0, 1.0) * 0.8);
            col = mix(col, vec3(0.95, 0.74, 0.42), clamp(r.x * r.x * 1.4 - 0.3, 0.0, 1.0) * 0.45);
            col *= 0.45 + 1.1 * f;
            fragColor = vec4(col, 1.0);
        }
        """;

    private const string Topographic = Noise + PointerHelpers + """

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 p = focusSpace(fragCoord);
            float t = iTime * 0.012;
            float h = fbm(p * 1.2 + vec2(t, -t * 0.7));
            // The cursor raises a small hill, so the contour lines flow around it.
            vec2 dp = p - pointerSpace();
            h += iPointer.z * 0.16 * exp(-dot(dp, dp) * 30.0);
            float lines = h * 26.0;
            float f = fract(lines);
            float w = fwidth(lines);
            float line = smoothstep(w * 1.4, 0.0, min(f, 1.0 - f));
            float major = step(mod(floor(lines + 0.5), 5.0), 0.5);
            vec3 bg = mix(vec3(0.025, 0.03, 0.045), vec3(0.05, 0.06, 0.085), h);
            vec3 ink = mix(vec3(0.25, 0.72, 0.78), vec3(0.95, 0.7, 0.42), smoothstep(0.3, 0.75, h));
            vec3 col = bg + ink * line * mix(0.28, 0.85, major);
            fragColor = vec4(col, 1.0);
        }
        """;

    private const string NightSkyline = Noise + """

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 p = focusSpace(fragCoord);
            vec3 col = mix(vec3(0.35, 0.1, 0.32), vec3(0.03, 0.02, 0.09), smoothstep(-0.35, 0.6, p.y));
            col += stars(fragCoord, 140.0, iTime) * smoothstep(0.0, 0.5, p.y) * 0.6;
            col += vec3(0.9, 0.3, 0.55) * exp(-(p.y + 0.35) * 4.0) * 0.18;
            for (int i = 0; i < 3; i++) {
                float fi = float(i);
                float density = 7.0 + fi * 5.0;
                float bx = p.x * density + fi * 13.0 + iTime * 0.004 * (fi + 1.0);
                float bid = floor(bx);
                float h = hash12(vec2(bid, fi));
                float base = -0.5 + fi * 0.04;
                float top = base + 0.2 + 0.5 * h * h * (1.0 - fi * 0.25) + (1.0 - fi) * 0.05;
                float inside = step(p.y, top) * step(0.06, fract(bx)) * step(fract(bx), 0.94);
                if (inside < 0.5) continue;
                float fogged = 0.08 + fi * 0.2;
                vec3 wall = mix(vec3(0.03, 0.02, 0.06), vec3(0.2, 0.08, 0.2), fogged);
                // Windows: a grid in building coordinates; some lit, slowly switching.
                vec2 wq = vec2(fract(bx) * 5.0, (p.y - base) * density * 2.6);
                vec2 wid = floor(wq), wgv = fract(wq);
                float lit = step(0.62, hash12(wid + bid * 7.0 + floor(iTime * 0.02 + hash12(wid + bid) * 5.0)));
                float win = step(0.25, wgv.x) * step(wgv.x, 0.75) * step(0.3, wgv.y) * step(wgv.y, 0.75);
                vec3 glow = mix(vec3(1.0, 0.72, 0.4), vec3(0.4, 0.85, 1.0), step(0.7, hash12(wid + bid * 3.0)));
                col = wall + glow * lit * win * (0.55 - fi * 0.12);
                // Red aviation light on the tallest towers.
                float beacon = step(0.8, h) * smoothstep(0.012, 0.0, length(vec2((fract(bx) - 0.5) / density, p.y - top - 0.006)));
                col += vec3(1.0, 0.15, 0.1) * beacon * step(0.5, fract(iTime * 0.5 + h * 3.0));
                break;
            }
            col = 1.0 - exp(-col * 1.4);
            fragColor = vec4(col, 1.0);
        }
        """;

    private const string MoonlitLake = Noise + """

        vec3 nightScene(vec2 p, vec2 fragCoord) {
            vec3 col = mix(vec3(0.1, 0.2, 0.32), vec3(0.01, 0.02, 0.06), smoothstep(-0.12, 0.7, p.y));
            col += stars(fragCoord, 170.0, iTime) * smoothstep(0.0, 0.6, p.y) * 0.85;
            vec2 moon = vec2(-0.32, 0.3);
            float md = length(p - moon);
            float craters = fbm(p * 18.0) * 0.25;
            col += vec3(0.95, 0.93, 0.85) * smoothstep(0.052, 0.048, md) * (0.85 - craters);
            col += vec3(0.5, 0.6, 0.75) * exp(-md * 7.0) * 0.25;
            float far = -0.12 + 0.09 * fbm(vec2(p.x * 1.6, 1.0)) + 0.05;
            col = mix(col, vec3(0.07, 0.11, 0.17), step(p.y, far));
            float near = -0.12 + 0.06 * fbm(vec2(p.x * 3.0 + 7.0, 2.0)) + 0.012;
            col = mix(col, vec3(0.02, 0.035, 0.05), step(p.y, near));
            return col;
        }

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 p = focusSpace(fragCoord);
            const float shore = -0.12;
            vec3 col;
            if (p.y >= shore) {
                col = nightScene(p, fragCoord);
            } else {
                float depth = shore - p.y;
                float ripple = (vnoise(vec2(p.x * 9.0, depth * 60.0 / (depth + 0.05) - iTime * 0.6)) - 0.5) * 0.02 * (0.3 + depth * 2.0);
                vec2 m = vec2(p.x + ripple, shore + depth * 1.08);
                col = nightScene(m, m * iFocus.z + iFocus.xy) * vec3(0.55, 0.62, 0.7);
                col += vec3(0.02, 0.03, 0.05);
                // The moon's glitter path on the water.
                float glint = smoothstep(0.6, 0.85, vnoise(vec2(p.x * 45.0, depth * 260.0 / (depth + 0.05) - iTime * 1.4)));
                col += vec3(0.9, 0.9, 0.8) * glint * exp(-abs(p.x + 0.32) * (6.0 + 1.0 / (depth + 0.05))) * 0.7;
            }
            fragColor = vec4(col, 1.0);
        }
        """;

    // ---------------------------------------------------------------- audio visualizers (plan §20.3)

    // Band lookups: 64 bands packed in 16 vec4s (see Web/audio.js), linearly interpolated.
    private const string AudioLib = """
        float specAt(int i) { i = clamp(i, 0, 63); return iSpectrum[i / 4][i % 4]; }
        float peakAt(int i) { i = clamp(i, 0, 63); return iPeaks[i / 4][i % 4]; }
        float spec(float i) { i = clamp(i, 0.0, 63.0); int a = int(i); return mix(specAt(a), specAt(a + 1), fract(i)); }
        float peak(float i) { i = clamp(i, 0.0, 63.0); int a = int(i); return mix(peakAt(a), peakAt(a + 1), fract(i)); }
        // A smooth forward surge on every beat that never runs backwards (count + eased progress).
        float beatTravel(float amount) { return amount * (iBeat.w - 1.0 + smoothstep(0.0, 0.35, iBeat.y)) * step(0.5, iBeat.w); }
        """;

    // Synthwave Drive: the sun's stripes breathe with the bass, the grid surges forward on each beat,
    // the horizon mountains are the live spectrum (mirrored, bass in the middle), stars twinkle on the treble.
    private const string SynthwaveDrive = Noise + AudioLib + """

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 uv = (fragCoord - iFocus.xy) / iFocus.z;
            float bass = iAudio.x, treble = iAudio.z, level = iAudio.w, pulse = iBeat.x;
            const float horizon = -0.06;
            float travel = iTime * 0.45 + beatTravel(0.18);
            vec3 col;
            if (uv.y > horizon) {
                float h = uv.y - horizon;
                col = mix(vec3(0.80, 0.20, 0.47), vec3(0.035, 0.015, 0.12), smoothstep(0.0, 0.6, h));
                col += stars(fragCoord, 170.0, iTime * (1.0 + treble * 4.0)) * (0.35 + treble * 1.4) * smoothstep(0.2, 0.55, h);

                vec2 sc = uv - vec2(0.0, 0.17);
                float d = length(sc);
                float radius = 0.24 + 0.008 * pulse;
                float disc = smoothstep(radius, radius - 0.004, d);
                float bandWidth = smoothstep(0.03, -0.22, sc.y) * (0.42 + 0.4 * bass);
                float gaps = step(fract(sc.y * 24.0 - iTime * 0.12), bandWidth);
                vec3 sun = mix(vec3(1.0, 0.86, 0.32), vec3(1.0, 0.18, 0.52), smoothstep(0.22, -0.22, sc.y));
                col = mix(col, sun, disc * (1.0 - gaps));
                col += vec3(1.0, 0.3, 0.55) * (0.2 + 0.22 * pulse + 0.1 * level) * exp(-d * 4.5);

                // Mountains: a quiet range that the music lifts, bass in the middle, treble at the sides.
                float ax = abs(uv.x);
                float range = 0.025 + 0.06 * fbm(vec2(ax * 2.6 + 3.1, 1.7)) * smoothstep(0.05, 0.6, ax);
                float lift = spec(ax * 36.0) * 0.15 * (0.25 + 0.75 * smoothstep(0.06, 0.4, ax));
                float ridge = range + lift;
                if (h < ridge) {
                    float shade = smoothstep(0.0, ridge, h);
                    col = mix(vec3(0.03, 0.005, 0.07), vec3(0.11, 0.02, 0.2), shade);
                }
                float edge = exp(-abs(h - ridge) * 260.0);
                vec3 neon = mix(vec3(1.0, 0.25, 0.75), vec3(0.2, 0.9, 1.0), smoothstep(0.1, 0.9, ax));
                col += neon * edge * (0.35 + 1.1 * lift / 0.15);
            } else {
                float depth = 0.11 / (horizon - uv.y + 0.0005);
                vec2 g = vec2(uv.x * depth * 2.2, depth + travel);
                vec2 fw = fwidth(g);
                vec2 a = abs(fract(g) - 0.5) / max(fw, vec2(1e-4));
                float line = 1.0 - min(min(a.x, a.y) * 0.6, 1.0);
                float fade = exp(-depth * 0.08);
                col = vec3(0.03, 0.0, 0.08) + vec3(1.0, 0.18, 0.75) * line * fade * (0.75 + 0.5 * level);
                col += vec3(0.9, 0.2, 0.6) * exp(-(horizon - uv.y) * 18.0) * (0.45 + 0.3 * pulse);
            }
            fragColor = vec4(pow(col, vec3(0.95)), 1.0);
        }
        """;

    // Neon Bars: 48 rounded bars on a reflective neon floor, magenta to cyan, falling peak caps, a wet
    // reflection that ripples on the beat, and a dim striped sun behind. Laid out on the primary display;
    // the floor and sky continue across the others.
    private const string NeonBars = AudioLib + """

        float h11(float n) { return fract(sin(n * 12.9898) * 43758.5453); }
        float box(vec2 q, vec2 b, float r) { vec2 d = abs(q) - b + r; return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - r; }

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 p = (fragCoord - iFocus.xy) / iFocus.z;
            float pulse = iBeat.x;
            const float base = -0.2;
            const float span = 1.44;
            const float w = span / 48.0;
            const float tall = 0.56;

            vec3 col = mix(vec3(0.015, 0.0, 0.05), vec3(0.09, 0.02, 0.17), smoothstep(-0.2, 0.6, p.y));
            // The sun behind the bars, striped and dim so the bars stay the subject.
            vec2 sc = p - vec2(0.0, 0.16);
            float d = length(sc);
            float stripes = step(fract(sc.y * 22.0 - iTime * 0.1), smoothstep(0.05, -0.25, sc.y) * 0.5);
            vec3 sun = mix(vec3(1.0, 0.7, 0.3), vec3(1.0, 0.2, 0.55), smoothstep(0.25, -0.25, sc.y));
            col = mix(col, sun * 0.55, smoothstep(0.3, 0.296, d) * (1.0 - stripes) * step(base, p.y));
            col += vec3(0.9, 0.25, 0.6) * (0.1 + 0.12 * pulse) * exp(-d * 4.0);

            bool below = p.y < base;
            float y = below ? 2.0 * base - p.y : p.y;
            float x = p.x;
            if (below) x += sin(p.y * 70.0 - iBeat.y * 14.0) * 0.006 * pulse;

            vec3 bars = vec3(0.0);
            float fi = floor((x + span * 0.5) / w);
            for (int k = -2; k <= 2; k++) {
                float i = fi + float(k);
                if (i < 0.0 || i > 47.0) continue;
                float band = 2.0 + i * 56.0 / 47.0;
                float idle = 0.025 + 0.02 * sin(iTime * 1.1 + i * 0.37);
                float v = max(spec(band), idle * (1.0 - iAudioPresence * 0.7));
                float hgt = 0.015 + v * tall;
                float cx = -span * 0.5 + (i + 0.5) * w;
                float bd = box(vec2(x - cx, y - base - hgt * 0.5), vec2(w * 0.34, hgt * 0.5), 0.003);
                vec3 tint = mix(vec3(1.0, 0.16, 0.72), vec3(0.12, 0.85, 1.0), i / 47.0);
                float body = smoothstep(0.0015, -0.0015, bd) * (0.55 + 0.45 * clamp((y - base) / max(hgt, 1e-3), 0.0, 1.0));
                bars += tint * (body + exp(-max(bd, 0.0) * 90.0) * 0.22 * (0.35 + v));
                float pk = max(peak(band), v);
                float cap = box(vec2(x - cx, y - base - 0.024 - pk * tall), vec2(w * 0.34, 0.0028), 0.002);
                bars += mix(tint, vec3(1.0), 0.55) * (smoothstep(0.0015, -0.0015, cap) * 0.9 + exp(-max(cap, 0.0) * 160.0) * 0.15);
            }
            if (below) {
                float depth = base - p.y;
                bars *= 0.32 * exp(-depth * 4.5);
                // The floor: a perspective neon grid fading into the distance.
                float z = 0.08 / (depth + 0.004);
                vec2 g = vec2(p.x * z * 2.0, z + iTime * 0.25);
                vec2 fw = fwidth(g);
                vec2 a = abs(fract(g) - 0.5) / max(fw, vec2(1e-4));
                float line = 1.0 - min(min(a.x, a.y) * 0.7, 1.0);
                col = mix(col, vec3(0.02, 0.0, 0.05), 0.6) + vec3(0.7, 0.15, 0.9) * line * exp(-z * 0.12) * 0.55;
                col += vec3(0.9, 0.25, 0.7) * exp(-depth * 30.0) * (0.25 + 0.25 * pulse);
            }
            col += bars;
            col = col / (1.0 + col * 0.35);
            fragColor = vec4(pow(col, vec3(0.92)), 1.0);
        }
        """;

    // Resonant Stardust: dust in deep space that buzzes with the treble, is pushed by a soft shockwave on
    // each kick, glows with loudness (warm for bass-heavy music, silvery for bright music) and, on sustained
    // notes, gathers onto the nodal lines of a Chladni plate whose figure follows the pitch. Stateless: each
    // mote moves a bounded distance (< one grid cell) from its home, so a 3×3 search always finds it.
    internal const string ResonanceLib = """
        float rsHash(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
        // Sustained, tonal music gathers the dust; drums and silence let it drift.
        float rsGather() { return iAudioPresence * smoothstep(0.1, 0.32, iAudio.y * 0.8 + iAudio.z * 0.4) * (1.0 - 0.5 * iBeat.x); }
        vec3 rsTint(float brightness) { return mix(vec3(1.0, 0.7, 0.42), vec3(0.72, 0.84, 1.0), smoothstep(0.3, 0.65, brightness)); }

        // Chladni plate f = cos(nπx)cos(mπy) − cos(mπx)cos(nπy) and its gradient (analytic, plate units).
        vec3 rsPlate(vec2 q, float n, float m) {
            const float PI = 3.14159265;
            float a = n * PI, b = m * PI;
            float cax = cos(a * q.x), sax = sin(a * q.x), cbx = cos(b * q.x), sbx = sin(b * q.x);
            float cay = cos(a * q.y), say = sin(a * q.y), cby = cos(b * q.y), sby = sin(b * q.y);
            float f = cax * cby - cbx * cay;
            vec2 g = vec2(-a * sax * cby + b * sbx * cay, -b * cax * sby + a * cbx * say);
            return vec3(f, g);
        }

        // Brightness of the dust at p (focus units). centre = where shockwaves start.
        float rsDust(vec2 p, vec2 centre, float sizeScale, float density, float t) {
            float treble = iAudio.z, level = iAudio.w, pres = iAudioPresence;
            float since = iBeat.y;
            float ring = since * 0.85;
            float ringAmp = pres * exp(-since * 2.4) * step(since, 3.0);
            float gather = rsGather();
            float n = 2.0 + iCentroid * 5.0, m = n + 1.35;
            float a = 0.0;
            for (int layer = 0; layer < 2; layer++) {
                float scale = layer == 0 ? 44.0 : 26.0;
                float seed = float(layer) * 31.7;
                vec2 id0 = floor(p * scale);
                for (int j = -1; j <= 1; j++) {
                    for (int i = -1; i <= 1; i++) {
                        vec2 id = id0 + vec2(float(i), float(j));
                        float h = rsHash(id + seed);
                        if (h > density) continue;
                        vec2 pos = (id + 0.5 + (vec2(rsHash(id + seed + 7.3), rsHash(id + seed + 1.9)) - 0.5) * 0.78) / scale;
                        pos += 0.14 / scale * vec2(sin(t * 0.31 + h * 20.0), cos(t * 0.27 + h * 13.0));
                        // Buzz: treble shakes every mote in place, each with its own phase.
                        pos += treble * treble * 0.12 / scale * vec2(sin(t * 37.0 + h * 91.0), cos(t * 31.0 + h * 57.0));
                        // Shockwave from the kick, then the dust settles back.
                        vec2 rel = pos - centre;
                        float r = length(rel);
                        pos += (r > 1e-5 ? rel / r : vec2(0.0)) * ringAmp * 0.45 / scale * exp(-pow((r - ring) / 0.07, 2.0));
                        // Cymatics: a bounded Newton step towards the plate's nodal line.
                        float near = 1.0;
                        if (gather > 0.01) {
                            vec3 pl = rsPlate(pos / 0.6, n, m);
                            vec2 grad = pl.yz / 0.6;
                            vec2 stepv = -pl.x * grad / max(dot(grad, grad), 1e-3);
                            float len = length(stepv), lim = 0.32 / scale;
                            pos += (len > lim ? stepv * lim / len : stepv) * gather;
                            near = mix(1.0, 0.18 + 1.5 * exp(-abs(pl.x) * 7.0), gather);
                        }
                        float d = length(p - pos);
                        float rad = (layer == 0 ? 0.0024 : 0.0036) * sizeScale * (1.0 + 0.5 * level);
                        float twinkle = 0.6 + 0.4 * sin(t * (0.7 + h * 1.9) + h * 40.0);
                        a += (smoothstep(rad, 0.0, d) + exp(-d * d / (rad * rad * 6.0)) * 0.25 * (0.3 + level)) * near * twinkle * (layer == 0 ? 1.0 : 0.6);
                    }
                }
            }
            return a;
        }
        """;

    private const string ResonantStardust = Noise + ResonanceLib + """

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 p = focusSpace(fragCoord);
            float level = iAudio.w;
            vec3 tint = rsTint(iCentroid);
            vec3 col = mix(vec3(0.006, 0.006, 0.02), vec3(0.025, 0.02, 0.06), clamp(p.y + 0.6, 0.0, 1.0));
            float neb = fbm(p * 1.6 + vec2(iTime * 0.01, 0.0));
            col += tint * pow(neb, 3.0) * (0.12 + 0.18 * level);
            col += stars(fragCoord, 220.0, iTime) * 0.35;
            float dust = rsDust(p, vec2(0.0), 1.1, 0.7, iTime);
            col += tint * dust * (0.9 + 0.7 * level + 0.25 * iBeat.x);
            // The figure itself glows faintly, like sand catching the light along the plate's still lines.
            float n = 2.0 + iCentroid * 5.0;
            col += tint * rsGather() * exp(-abs(rsPlate(p / 0.6, n, n + 1.35).x) * 10.0) * 0.06;
            col = 1.0 - exp(-col * 1.3);
            fragColor = vec4(col, 1.0);
        }
        """;

    /// <summary>Mood is the Discover chip the shader also appears under (see <c>Moods</c>).</summary>
    private static readonly (string Key, string Title, string Mood, string Code)[] Definitions =
    [
        ("aurora", "Aurora", "nature", Aurora),
        ("liquid", "Liquid Gradient", "abstract", LiquidGradient),
        ("nebula", "Nebula", "space", Nebula),
        ("synthwave", "Synthwave Sunset", "neon", Synthwave),
        ("silk", "Silk Ribbons", "abstract", Silk),
        ("bokeh", "Bokeh Lights", "cozy", Bokeh),
        ("ocean", "Ocean Swell", "nature", OceanSwell),
        ("rainglass", "Rain on Glass", "cozy", RainOnGlass),
        ("warp", "Warp Stars", "space", WarpStars),
        ("lava", "Lava Lamp", "abstract", LavaLamp),
        ("mesh", "Soft Gradient", "minimal", GradientMesh),
        ("clouds", "Drifting Clouds", "nature", DriftingClouds),
        ("ink", "Ink in Water", "abstract", InkInWater),
        ("topo", "Topographic", "minimal", Topographic),
        ("skyline", "Night Skyline", "neon", NightSkyline),
        ("moonlake", "Moonlit Lake", "cozy", MoonlitLake),
        ("synthdrive", "Synthwave Drive", "neon", SynthwaveDrive),
        ("neonbars", "Neon Bars", "neon", NeonBars),
        ("resonance", "Resonant Stardust", "space", ResonantStardust),
    ];

    /// <summary>Built-ins that react to the music (labelled "Music" in the gallery).</summary>
    public static readonly IReadOnlyList<string> AudioKeys = ["synthdrive", "neonbars", "resonance"];

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

    public static string? MoodOf(string itemId) => Definitions.FirstOrDefault(d => "builtin-" + d.Key == itemId).Mood;

    /// <summary>GLSL of a built-in (the Studio's examples and the lint tests).</summary>
    public static string? Source(string key) => Definitions.FirstOrDefault(d => d.Key == key).Code;

    public static ShaderProgram? Program(string key)
    {
        var def = Definitions.FirstOrDefault(d => d.Key == key);
        return def.Code is null ? null : new ShaderProgram(def.Title, [new ShaderPass("image", def.Code, null, [])]);
    }
}
