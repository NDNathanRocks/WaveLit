'use strict';

// Overlays: subtle particle and light layers over any wallpaper, most of them responding to the cursor.
// Every particle is stateless: its home is a hash of its grid cell and its motion a function of the shared
// timeline, so every Extend slice draws the same particle in the same place with no syncing.
// The cursor (pointer.js: spring-smoothed, with a trail) acts in two ways:
//  - "holes" (Stardust, Constellation): each recent cursor position opens a hole that grows in and closes
//    again. A hole is the exactly invertible radial map r' = sqrt(r^2 + a^2), so dust parts without smearing.
//  - pushes and swirls (everything else): each particle's position is moved directly (forward), bounded to
//    less than one grid cell so a 3x3 cell search always finds it.
// Nothing pops: every cursor effect grows in over ~150 ms and settles over about a second (ilEnv).
//
// Uniforms: wavelitTint (rgb colour, a = amount) and wavelitStyle (x = size, y = speed,
// z = cursor response: 0 none / 1 gentle / ~1.7 playful, w = opacity). Output is premultiplied alpha.
(function () {
  const COMMON = `
float ilHash(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}
vec2 ilHash2(vec2 p) { return vec2(ilHash(p), ilHash(p + 17.31)); }
float ilNoise(vec2 p) {
  vec2 i = floor(p), f = fract(p);
  f = f * f * (3.0 - 2.0 * f);
  return mix(mix(ilHash(i), ilHash(i + vec2(1, 0)), f.x), mix(ilHash(i + vec2(0, 1)), ilHash(i + vec2(1, 1)), f.x), f.y);
}
vec2 ilSpace(vec2 fc) { return (fc - iFocus.xy) / iFocus.z; }
float ilAmount() { return clamp(wavelitTint.a, 0.0, 1.0); }
float ilSize() { return wavelitStyle.x; }
float ilTime() { return iTime * wavelitStyle.y; }
float ilResponse() { return wavelitStyle.z; }
float ilPresence() { return iPointer.z * min(ilResponse(), 1.0); }
vec2 ilPtr() { return ilSpace(iPointer.xy); }
mat2 ilRot(float a) { float s = sin(a), c = cos(a); return mat2(c, s, -s, c); }
vec4 ilOut(vec3 col, float a) { a = clamp(a, 0.0, 1.0) * wavelitStyle.w; return vec4(col * a, a); }

// Grows in over 150 ms, then settles by 'life' seconds: cursor effects never pop.
float ilEnv(float age, float life) { return smoothstep(0.0, 0.15, age) * (1.0 - smoothstep(0.15, life, age)); }

// Hole k (xy = centre in focus space, z = radius): 0..15 trail points (15 = oldest), 16 the resting
// cursor, 17 a click burst.
vec3 ilHole(int k, float size, float burst) {
  if (k < 16) {
    vec4 tp = iTrail[k];
    return vec3(ilSpace(tp.xy), tp.w > 0.0 ? size * tp.w * ilEnv(tp.z, 1.4) : 0.0);
  }
  if (k == 16) return vec3(ilPtr(), size * 0.8 * iPointer.z);
  return vec3(ilSpace(iClick.xy), iClick.w > 0.0 ? burst * ilEnv(iClick.z, 1.6) : 0.0);
}
int ilOrder(int j) { return j < 16 ? 15 - j : j; }

// Where undisturbed space ends up once the holes open (forward map).
vec2 ilOpen(vec2 q, float size, float burst) {
  for (int j = 0; j <= 17; j++) {
    vec3 h = ilHole(ilOrder(j), size, burst);
    if (h.z <= 0.0) continue;
    vec2 v = q - h.xy;
    float r = length(v);
    q = h.xy + (r > 1e-6 ? v / r : vec2(1.0, 0.0)) * sqrt(r * r + h.z * h.z);
  }
  return q;
}

// The exact inverse: which undisturbed point shows at q. False inside a hole.
bool ilPart(inout vec2 q, float size, float burst) {
  for (int j = 17; j >= 0; j--) {
    vec3 h = ilHole(ilOrder(j), size, burst);
    if (h.z <= 0.0) continue;
    vec2 v = q - h.xy;
    float r2 = dot(v, v);
    if (r2 <= h.z * h.z) return false;
    q = h.xy + v * sqrt(1.0 - h.z * h.z / r2);
  }
  return true;
}

// A gentle push away from the cursor, its recent path and a click; bounded by 'limit' (< one cell).
vec2 ilPush(vec2 pos, float radius, float limit) {
  float resp = ilResponse();
  if (resp <= 0.0) return vec2(0.0);
  vec2 d = vec2(0.0);
  vec2 v = pos - ilPtr();
  d += normalize(v + 1e-5) * iPointer.z * exp(-dot(v, v) / (radius * radius));
  for (int i = 0; i < 16; i += 4) {
    vec4 tp = iTrail[i];
    if (tp.w <= 0.0) continue;
    vec2 w = pos - ilSpace(tp.xy);
    d += normalize(w + 1e-5) * tp.w * ilEnv(tp.z, 1.4) * exp(-dot(w, w) / (radius * radius));
  }
  if (iClick.w > 0.0 && iClick.z < 1.6) {
    vec2 c = pos - ilSpace(iClick.xy);
    d += normalize(c + 1e-5) * 2.0 * ilEnv(iClick.z, 1.6) * exp(-dot(c, c) / (radius * radius * 4.0));
  }
  float len = length(d) * limit * 0.5 * resp;
  return len > 1e-6 ? normalize(d) * min(len, limit) : vec2(0.0);
}

// A swirl around the cursor (rotation by a distance-dependent angle; moves a point by less than its distance).
vec2 ilSwirl(vec2 pos, float radius, float angle) {
  vec2 v = pos - ilPtr();
  float a = ilPresence() * angle * exp(-dot(v, v) / (radius * radius));
  return ilPtr() + ilRot(a) * v;
}

// One layer of soft motes, found via the inverse and measured on screen (so motes near a rim stay round).
float ilMotes(vec2 p, vec2 q, float scale, float seed, float t, float radius, float density, float size, float burst) {
  vec2 drift = vec2(t * 0.016, t * 0.007);
  vec2 id = floor((q + drift) * scale);
  float h = ilHash(id + seed);
  if (h > density) return 0.0;
  vec2 off = (ilHash2(id + seed * 3.1) - 0.5) * 0.6;
  off += 0.1 * vec2(sin(t * 0.45 + h * 30.0), cos(t * 0.37 + h * 17.0));
  vec2 centre = (id + 0.5 + off) / scale - drift;
  float d = length(p - ilOpen(centre, size, burst));
  float twinkle = 0.55 + 0.45 * sin(t * (0.8 + h * 1.7) + h * 40.0);
  return smoothstep(radius, 0.0, d) * twinkle;
}
`;

  const STYLES = {
    // Sparse dust that drifts; the cursor parts it and the wake closes softly behind.
    stardust: COMMON + `
void mainImage(out vec4 o, in vec2 fc) {
  vec2 p = ilSpace(fc), q = p;
  float amt = ilAmount(), t = ilTime(), sz = ilSize();
  float size = 0.075 * ilResponse(), burst = 0.25 * min(ilResponse(), 1.0);
  float a = 0.0;
  if (ilPart(q, size, burst)) {
    float density = 0.08 + 0.3 * amt;
    a += ilMotes(p, q, 14.0, 1.0, t, 0.0042 * sz, density, size, burst) * 0.8;
    a += ilMotes(p, q, 8.0, 13.0, t * 0.8, 0.006 * sz, density * 0.6, size, burst) * 0.45;
  }
  o = ilOut(wavelitTint.rgb, a * (0.55 + 0.45 * amt));
}`,

    // Wandering glows that drift towards the cursor, circle it, and scatter on a click.
    fireflies: COMMON + `
void mainImage(out vec4 o, in vec2 fc) {
  vec2 q = ilSpace(fc);
  float amt = ilAmount(), t = ilTime(), sz = ilSize();
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
    if (iClick.w > 0.0 && iClick.z < 1.6) {   // a click scatters them
      vec2 v = pos - ilSpace(iClick.xy);
      float r = length(v);
      pos += (r > 1e-5 ? v / r : vec2(0.0)) * 0.12 * ilEnv(iClick.z, 1.6) * exp(-r * 4.0) * min(ilResponse(), 1.0);
    }
    float d = length(q - pos);
    float blink = mix(pow(0.5 + 0.5 * sin(t * (0.7 + h * 1.3) + h * 50.0), 2.0), 1.0, pull);
    a += blink * (0.00002 * sz * sz / (d * d + 0.00002 * sz * sz) * 0.5 + smoothstep(0.003 * sz, 0.0, d));
  }
  o = ilOut(wavelitTint.rgb, a * (0.6 + 0.4 * amt));
}`,

    // Faint stars that link up with thin lines near the cursor.
    constellation: COMMON + `
vec2 ilStar(vec2 cell, float t) {
  vec2 p = (ilHash2(cell + 5.1) - 0.5) * 0.75;
  float h = ilHash(cell + 9.7);
  return cell + 0.5 + p + 0.08 * vec2(sin(t * 0.2 + h * 30.0), cos(t * 0.17 + h * 20.0));
}
float ilSegment(vec2 p, vec2 a, vec2 b) {
  vec2 pa = p - a, ba = b - a;
  float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
  return length(pa - ba * h);
}
void mainImage(out vec4 o, in vec2 fc) {
  vec2 q = ilSpace(fc);
  float amt = ilAmount(), t = ilTime(), sz = ilSize();
  float a = 0.0;
  if (ilPart(q, 0.02 * ilResponse(), 0.1 * min(ilResponse(), 1.0))) {
    float scale = 7.0 / sz;
    vec2 g = q * scale;
    vec2 id = floor(g);
    vec2 c = ilPtr() * scale;
    float pres = ilPresence();
    vec2 pts[9];
    float on[9];
    int n = 0;
    for (int y = -1; y <= 1; y++)
    for (int x = -1; x <= 1; x++) {
      vec2 cell = id + vec2(float(x), float(y));
      pts[n] = ilStar(cell, t);
      on[n] = step(ilHash(cell + 1.9), 0.4 + 0.35 * amt);
      n++;
    }
    for (int i = 0; i < 9; i++) {
      if (on[i] < 0.5) continue;
      float d = length(g - pts[i]) / scale;
      float near = pres * exp(-dot(pts[i] - c, pts[i] - c) / 6.0);
      a += smoothstep(0.003 + 0.002 * near, 0.0, d) * (0.35 + 0.65 * near);
      for (int j = i + 1; j < 9; j++) {
        if (on[j] < 0.5) continue;
        float len = length(pts[j] - pts[i]);
        if (len > 1.6) continue;
        vec2 mid = (pts[i] + pts[j]) * 0.5;
        float link = pres * exp(-dot(mid - c, mid - c) / 4.0) * smoothstep(1.6, 0.9, len);
        if (link < 0.01) continue;
        a += smoothstep(0.0016, 0.0, ilSegment(g, pts[i], pts[j]) / scale) * link * 0.55;
      }
    }
  }
  o = ilOut(wavelitTint.rgb, a * (0.6 + 0.4 * amt));
}`,

    // Soft rings of light that spread from where the cursor passes, and a bigger one on click.
    ripples: COMMON + `
void mainImage(out vec4 o, in vec2 fc) {
  vec2 q = ilSpace(fc);
  float amt = ilAmount(), sz = ilSize(), resp = min(ilResponse(), 1.5);
  float a = 0.0;
  for (int i = 0; i < 16; i += 2) {
    vec4 tp = iTrail[i];
    if (tp.w <= 0.0) continue;
    float d = length(q - ilSpace(tp.xy));
    float radius = tp.z * 0.16 * sz;
    float w = 0.004 + tp.z * 0.006;
    a += exp(-pow((d - radius) / w, 2.0)) * tp.w * ilEnv(tp.z, 1.4) * 0.35;
  }
  if (iClick.w > 0.0 && iClick.z < 2.2) {
    float d = length(q - ilSpace(iClick.xy));
    float env = ilEnv(iClick.z, 2.2);
    for (int r = 0; r < 3; r++) {
      float radius = iClick.z * (0.28 - float(r) * 0.07) * sz;
      a += exp(-pow((d - radius) / (0.006 + iClick.z * 0.01), 2.0)) * env * (0.6 - float(r) * 0.15);
    }
  }
  float d0 = length(q - ilPtr());
  a += iPointer.z * 0.12 * exp(-pow((d0 - 0.035 * sz) / 0.006, 2.0));
  o = ilOut(wavelitTint.rgb, a * resp * (0.55 + 0.45 * amt));
}`,

    // Sakura petals drifting on a breeze; the cursor makes a gust.
    petals: COMMON + `
void mainImage(out vec4 o, in vec2 fc) {
  vec2 p = ilSpace(fc);
  float amt = ilAmount(), t = ilTime(), sz = ilSize();
  vec3 col = vec3(0.0);
  float a = 0.0;
  for (int layer = 0; layer < 2; layer++) {
    float fl = float(layer);
    float scale = 4.5 + fl * 3.0;
    vec2 motion = vec2(t * (0.03 + fl * 0.012), -t * (0.045 + fl * 0.02));
    vec2 id = floor((p - motion) * scale);
    for (int y = -1; y <= 1; y++)
    for (int x = -1; x <= 1; x++) {
      vec2 cell = id + vec2(float(x), float(y));
      vec2 key = cell + fl * 41.0;
      float h = ilHash(key);
      if (h > 0.12 + 0.3 * amt) continue;
      vec2 jit = ilHash2(key + 2.7) - 0.5;
      vec2 pos = (cell + 0.5 + jit * 0.7) / scale + motion;
      pos.x += 0.025 * sin(t * (0.6 + h) + h * 20.0);
      pos += ilPush(pos, 0.14, 0.6 / scale);
      vec2 local = ilRot(t * (0.4 + h) * (h > 0.06 ? 1.0 : -1.0) + h * 6.28) * (p - pos);
      float r = (0.011 + 0.007 * h) * sz * (1.0 - fl * 0.25);
      float d = length(local / vec2(1.0, 0.55 + 0.25 * sin(t * 1.3 + h * 9.0)));
      float petal = smoothstep(r, r * 0.55, d) * (0.9 - fl * 0.3);
      col += mix(wavelitTint.rgb * 0.92, vec3(1.0, 0.94, 0.96), smoothstep(r, 0.0, d) * 0.2) * petal;
      a += petal;
    }
  }
  float k = 0.6 + 0.4 * amt;
  o = vec4(clamp(col, 0.0, 1.0) * k, clamp(a, 0.0, 1.0) * k) * wavelitStyle.w;
}`,

    // Soft snowfall; the cursor stirs eddies in it.
    snowfall: COMMON + `
void mainImage(out vec4 o, in vec2 fc) {
  vec2 p = ilSpace(fc);
  float amt = ilAmount(), t = ilTime(), sz = ilSize();
  float a = 0.0;
  for (int layer = 0; layer < 2; layer++) {
    float fl = float(layer);
    float scale = 6.0 + fl * 5.0;
    vec2 motion = vec2(0.0, -t * (0.05 + fl * 0.025));
    vec2 id = floor((p - motion) * scale);
    for (int y = -1; y <= 1; y++)
    for (int x = -1; x <= 1; x++) {
      vec2 cell = id + vec2(float(x), float(y));
      vec2 key = cell + fl * 57.0;
      float h = ilHash(key);
      if (h > 0.15 + 0.35 * amt) continue;
      vec2 pos = (cell + 0.5 + (ilHash2(key + 4.2) - 0.5) * 0.7) / scale + motion;
      pos.x += 0.02 * sin(t * (0.5 + h * 0.6) + h * 30.0);
      pos = ilSwirl(pos, 0.12, 1.4 * min(ilResponse(), 1.5));
      pos += ilPush(pos, 0.1, 0.4 / scale);
      float r = (0.0035 + 0.003 * h) * sz * (1.0 - fl * 0.3);
      a += smoothstep(r, r * 0.25, length(p - pos)) * (0.85 - fl * 0.3);
    }
  }
  o = ilOut(wavelitTint.rgb, a * (0.6 + 0.4 * amt));
}`,

    // Warm sparks rising and flickering; they swirl around the cursor.
    embers: COMMON + `
void mainImage(out vec4 o, in vec2 fc) {
  vec2 p = ilSpace(fc);
  float amt = ilAmount(), t = ilTime(), sz = ilSize();
  vec3 col = vec3(0.0);
  float a = 0.0;
  const float scale = 8.0;
  vec2 motion = vec2(0.0, t * 0.06);
  vec2 id = floor((p - motion) * scale);
  for (int y = -1; y <= 1; y++)
  for (int x = -1; x <= 1; x++) {
    vec2 cell = id + vec2(float(x), float(y));
    float h = ilHash(cell + 7.7);
    if (h > 0.1 + 0.25 * amt) continue;
    vec2 pos = (cell + 0.5 + (ilHash2(cell) - 0.5) * 0.7) / scale + motion;
    pos.x += 0.03 * sin(t * (0.9 + h) + h * 40.0) + 0.01 * sin(t * 3.1 + h * 7.0);
    pos = ilSwirl(pos, 0.1, 1.8 * min(ilResponse(), 1.5));
    pos += ilPush(pos, 0.1, 0.5 / scale);
    float life = 0.5 + 0.5 * sin(t * (0.6 + h * 0.8) + h * 60.0);
    float flicker = 0.7 + 0.3 * sin(t * (9.0 + h * 7.0) + h * 13.0);
    float r = (0.0032 + 0.0022 * h) * sz;
    float d = length((p - pos) * vec2(1.0, 0.5));
    float spark = (smoothstep(r, 0.0, d) * 1.3 + 0.35 * exp(-d * d / (r * r * 16.0))) * life * flicker;
    col += mix(wavelitTint.rgb, vec3(1.0, 0.25, 0.08), h * 2.0) * spark;
    a += spark;
  }
  float k = 0.6 + 0.4 * amt;
  o = vec4(clamp(col, 0.0, 1.0) * k, clamp(a, 0.0, 1.0) * k) * wavelitStyle.w;
}`,

    // Large out-of-focus orbs floating slowly; they drift out of the cursor's way.
    bokeh: COMMON + `
void mainImage(out vec4 o, in vec2 fc) {
  vec2 p = ilSpace(fc);
  float amt = ilAmount(), t = ilTime(), sz = ilSize();
  vec3 col = vec3(0.0);
  float a = 0.0;
  const float scale = 3.0;
  vec2 motion = vec2(t * 0.01, t * 0.006);
  vec2 id = floor((p - motion) * scale);
  for (int y = -1; y <= 1; y++)
  for (int x = -1; x <= 1; x++) {
    vec2 cell = id + vec2(float(x), float(y));
    float h = ilHash(cell + 1.3);
    if (h > 0.25 + 0.35 * amt) continue;
    vec2 pos = (cell + 0.5 + (ilHash2(cell + 6.6) - 0.5) * 0.5) / scale + motion;
    pos += 0.03 * vec2(sin(t * 0.13 + h * 9.0), cos(t * 0.11 + h * 7.0));
    pos += ilPush(pos, 0.2, 0.08);
    float r = (0.035 + 0.045 * h) * sz;
    float d = length(p - pos);
    float disc = smoothstep(r, r * 0.9, d);
    float rim = smoothstep(r * 0.8, r * 0.97, d) * disc;
    float orb = (disc * 0.22 + rim * 0.14) * (0.6 + 0.4 * sin(t * 0.3 + h * 20.0));
    col += wavelitTint.rgb * (0.75 + 0.25 * cos(6.2831 * (h + vec3(0.0, 0.2, 0.4)))) * orb;
    a += orb;
  }
  float k = 0.6 + 0.4 * amt;
  o = vec4(clamp(col, 0.0, 1.0) * k, clamp(a, 0.0, 1.0) * k) * wavelitStyle.w;
}`,

    // A soft lantern light that follows the cursor, with a short glowing wake.
    halo: COMMON + `
void mainImage(out vec4 o, in vec2 fc) {
  vec2 p = ilSpace(fc);
  float amt = ilAmount(), sz = ilSize();
  float pres = iPointer.z;
  vec2 v = p - ilPtr();
  float r2 = dot(v, v) / (sz * sz);
  float a = pres * (0.22 * exp(-r2 / 0.006) + 0.07 * exp(-r2 / 0.05));
  a += pres * 0.12 * exp(-pow((sqrt(r2) - 0.045) / 0.004, 2.0));
  for (int i = 0; i < 16; i += 3) {
    vec4 tp = iTrail[i];
    if (tp.w <= 0.0) continue;
    vec2 w = p - ilSpace(tp.xy);
    a += 0.08 * tp.w * ilEnv(tp.z, 1.0) * exp(-dot(w, w) / (0.004 * sz * sz));
  }
  if (iClick.w > 0.0 && iClick.z < 1.2) {
    vec2 c = p - ilSpace(iClick.xy);
    a += 0.25 * ilEnv(iClick.z, 1.2) * exp(-dot(c, c) / (0.02 * sz * sz));
  }
  o = ilOut(wavelitTint.rgb, a * min(ilResponse(), 1.5) * (0.6 + 0.4 * amt));
}`,

    // The cursor draws a fading, softly iridescent ribbon of light.
    silk: COMMON + `
float ilSeg(vec2 p, vec2 a, vec2 b) {
  vec2 pa = p - a, ba = b - a;
  float h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-8), 0.0, 1.0);
  return length(pa - ba * h);
}
void mainImage(out vec4 o, in vec2 fc) {
  vec2 p = ilSpace(fc);
  float amt = ilAmount(), sz = ilSize(), t = ilTime();
  vec3 col = vec3(0.0);
  float a = 0.0;
  vec2 prev = ilPtr();
  for (int i = 0; i < 16; i++) {
    vec4 tp = iTrail[i];
    if (tp.w <= 0.0) break;
    vec2 cur = ilSpace(tp.xy);
    float fade = 1.0 - smoothstep(0.0, 1.2, tp.z);
    float w = 0.0045 * sz * (0.35 + 0.65 * fade);
    float g = exp(-pow(ilSeg(p, prev, cur) / w, 2.0)) * fade * smoothstep(0.0, 0.1, tp.z + 0.05);
    vec3 hue = 0.6 + 0.4 * cos(6.2831 * (float(i) * 0.04 + t * 0.05 + vec3(0.0, 0.33, 0.67)));
    col += mix(wavelitTint.rgb, hue, 0.45) * g;
    a += g;
    prev = cur;
  }
  float k = (0.6 + 0.4 * amt) * min(ilResponse(), 1.5);
  o = vec4(clamp(col, 0.0, 1.0) * k, clamp(a, 0.0, 1.0) * k) * wavelitStyle.w;
}`,

    // Faint curtains of light across the top of the screens, swaying away from the cursor.
    aurora: COMMON + `
void mainImage(out vec4 o, in vec2 fc) {
  vec2 p = ilSpace(fc);
  float amt = ilAmount(), t = ilTime(), sz = ilSize();
  float top = (iResolution.y - iFocus.y) / iFocus.z;     // the canvas top, in focus units
  float y = top - p.y;                                   // distance down from the top
  vec2 v = p - ilPtr();
  float sway = ilPresence() * 0.12 * sign(v.x) * exp(-v.x * v.x / 0.03) * exp(-v.y * v.y / 0.12);
  float x = (p.x - sway) / sz;
  float bands = ilNoise(vec2(x * 3.0 + t * 0.05, t * 0.07)) * 0.6 + ilNoise(vec2(x * 7.0 - t * 0.08, 3.0)) * 0.4;
  float rays = 0.6 + 0.4 * ilNoise(vec2(x * 40.0, t * 0.2));
  float curtain = smoothstep(0.35, 0.75, bands) * rays * exp(-y * 2.6) * smoothstep(0.0, 0.08, y);
  vec3 tint = mix(wavelitTint.rgb, vec3(0.55, 0.35, 1.0), smoothstep(0.1, 0.5, y));
  float a = curtain * (0.22 + 0.28 * amt);
  o = vec4(tint * a, a * 0.85) * wavelitStyle.w;
}`,

    // A cinematic finish: fine film grain, a gentle vignette and a slow warm light leak. Not interactive.
    cinematic: COMMON + `
void mainImage(out vec4 o, in vec2 fc) {
  float amt = ilAmount();
  vec2 uv = fc / iResolution.xy;
  float aspect = iResolution.x / iResolution.y;
  vec2 c = (uv - 0.5) * vec2(aspect, 1.0) / max(aspect, 1.0);
  float vig = smoothstep(0.35, 0.85, length(c) * 1.15) * (0.18 + 0.3 * amt);
  float g = ilHash(floor(fc) + floor(iTime * 24.0) * vec2(17.0, 31.0)) - 0.5;
  float grain = g * (0.03 + 0.05 * amt);
  float t = iTime * 0.02 * wavelitStyle.y;
  vec2 lp = vec2(0.08 + 0.06 * sin(t), 0.9 + 0.05 * cos(t * 0.7));
  vec2 ld = (uv - lp) * vec2(aspect, 1.0);
  float leak = exp(-dot(ld, ld) * 3.0) * (0.06 + 0.1 * amt) * (0.7 + 0.3 * sin(t * 3.0));
  vec3 light = mix(vec3(1.0, 0.55, 0.25), wavelitTint.rgb, 0.3) * leak + vec3(max(grain, 0.0));
  float dark = vig + max(-grain, 0.0);
  o = vec4(light, clamp(dark, 0.0, 1.0)) * wavelitStyle.w;
}`,
  };

  // "Natural" colours: each overlay's own light.
  const DEFAULT_TINT = {
    stardust: [1.0, 0.95, 0.86],
    fireflies: [1.0, 0.82, 0.38],
    constellation: [0.78, 0.88, 1.0],
    ripples: [0.9, 0.96, 1.0],
    petals: [1.0, 0.76, 0.85],
    snowfall: [0.95, 0.97, 1.0],
    embers: [1.0, 0.62, 0.25],
    bokeh: [1.0, 0.85, 0.7],
    halo: [1.0, 0.93, 0.8],
    silk: [0.85, 0.9, 1.0],
    aurora: [0.35, 1.0, 0.7],
    cinematic: [1.0, 1.0, 1.0],
  };

  function parseColor(value, style) {
    const m = typeof value === 'string' && /^#([0-9a-f]{6})$/i.exec(value);
    if (!m) return DEFAULT_TINT[style] || [1, 1, 1];
    const n = parseInt(m[1], 16);
    return [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
  }

  const RESPONSE = { none: 0, gentle: 1, playful: 1.7 };
  const num = (v, fallback) => (v !== undefined && v !== null && Number.isFinite(Number(v)) ? Math.min(1, Math.max(0, Number(v))) : fallback);

  // One overlay canvas above the wallpaper. Runs at the wallpaper's own rate (Pristine by default), raised
  // to at least 60 fps while the cursor is about, and never above 0.75 render scale.
  function create(settings, container, timeline, getOptions, getView, getPixelSize) {
    const style = settings && settings.style;
    const code = STYLES[style];
    if (!code) return null;
    const tint = parseColor(settings.color, style);
    const amount = num(settings.amount, 0.35);
    const uniforms = [
      0.6 + num(settings.size, 0.5),                                     // size 0.6 .. 1.6
      0.4 + num(settings.speed, 0.5) * 1.2,                              // speed 0.4 .. 1.6
      RESPONSE[settings.response] !== undefined ? RESPONSE[settings.response] : 1,
      0.15 + num(settings.opacity, 1) * 0.85,                            // opacity 0.15 .. 1
    ];
    const canvas = document.createElement('canvas');
    canvas.className = 'layer overlay interactive';
    container.appendChild(canvas);
    const layerOptions = () => {
      const o = getOptions();
      const active = uniforms[2] > 0 && window.WavelitPointer && window.WavelitPointer.isActive();
      return Object.assign({}, o, { fps: active ? Math.max(60, o.fps || 30) : o.fps || 30, scale: Math.min(o.scale || 1, 0.75) });
    };
    const program = {
      name: 'overlay-' + style,
      overlay: true,
      tint: () => [tint[0], tint[1], tint[2], amount],
      style: () => uniforms,
      passes: [{ type: 'image', code, inputs: [] }],
    };
    const runtime = new window.WavelitShaderRuntime(canvas, program, layerOptions, getPixelSize, getView, timeline);
    return {
      style,
      key: JSON.stringify(settings),
      start: () => runtime.start(),
      stop: () => runtime.stop(),
      resize: () => runtime.resize(),
      dispose: () => { runtime.dispose(); canvas.remove(); },
    };
  }

  window.WavelitInteractive = {
    styles: Object.keys(STYLES),
    /** GLSL of a style (used by the shader test harness). */
    code: (style) => STYLES[style] || null,
    create,
  };
})();
