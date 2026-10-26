'use strict';

// "Living stills": motion effects that bring a still image to life for a few % of GPU.
// Drift is a slow camera move (compositor transform); the others are transparent shader overlays drawn
// by WavelitShaderRuntime. All of them run on the wallpaper timeline and work in canvas space, so in
// Extend mode the motion continues across displays.
(function () {
  const HASH = `
float wlHash(vec2 p) {
  p = fract(p * vec2(123.34, 456.21));
  p += dot(p, p + 45.32);
  return fract(p.x * p.y);
}
`;

  // Output is premultiplied alpha.
  const SHADERS = {
    snow: HASH + `
void mainImage(out vec4 o, in vec2 fc) {
  vec2 p = (fc - iFocus.xy) / iFocus.z;
  float a = 0.0;
  for (int i = 0; i < 4; i++) {
    float fi = float(i);
    float scale = 7.0 + fi * 5.0;
    vec2 q = p * scale;
    q.y += iTime * (0.75 - fi * 0.1) * scale * 0.1;
    q.x += sin(iTime * 0.21 + fi * 1.7) * 0.6;
    vec2 id = floor(q), gv = fract(q) - 0.5;
    float h = wlHash(id + fi * 13.0);
    if (h < 0.5) continue;
    vec2 off = vec2(wlHash(id + 3.1), wlHash(id + 7.7)) - 0.5;
    off.x += 0.3 * sin(iTime * (0.7 + h) + h * 30.0);
    float r = (0.05 + 0.06 * wlHash(id + 1.3)) * (1.0 - fi * 0.12);
    a += smoothstep(r, r * 0.25, length(gv - off * 0.55)) * (0.95 - fi * 0.17);
  }
  a = clamp(a, 0.0, 1.0);
  o = vec4(vec3(0.97, 0.98, 1.0) * a, a);
}`,

    rain: HASH + `
void mainImage(out vec4 o, in vec2 fc) {
  vec2 p = (fc - iFocus.xy) / iFocus.z;
  float a = 0.0;
  for (int i = 0; i < 3; i++) {
    float fi = float(i);
    vec2 q = p * vec2(55.0 + fi * 35.0, 1.6 + fi * 0.6);
    q.x += p.y * (4.0 + fi * 2.0);              // slight slant
    q.y += iTime * (2.6 + fi * 0.9);
    float column = floor(q.x);
    float h = wlHash(vec2(column, fi * 7.0));
    if (h < 0.55) continue;
    float y = fract(q.y + h * 7.0);
    float x = abs(fract(q.x) - 0.5);
    float streak = smoothstep(0.12, 0.0, x) * smoothstep(0.0, 0.03, y) * smoothstep(0.22, 0.0, y);
    a += streak * (0.6 - fi * 0.14);
  }
  a = clamp(a, 0.0, 1.0);
  float mood = 0.14;                            // a touch darker, like an overcast day
  o = vec4(vec3(0.78, 0.84, 0.95) * a, a + mood * (1.0 - a));
}`,

    fireflies: HASH + `
void mainImage(out vec4 o, in vec2 fc) {
  vec2 p = (fc - iFocus.xy) / iFocus.z;
  vec2 lo = -iFocus.xy / iFocus.z;
  vec2 ext = iResolution.xy / iFocus.z;
  int count = int(clamp(22.0 * ext.x * ext.y / 1.78, 22.0, 90.0));
  float glow = 0.0;
  for (int i = 0; i < 90; i++) {
    if (i >= count) break;
    float fi = float(i);
    float h1 = wlHash(vec2(fi, 1.0)), h2 = wlHash(vec2(fi, 2.0)), h3 = wlHash(vec2(fi, 3.0));
    vec2 home = lo + vec2(h1, 0.15 + 0.7 * h2) * ext;
    vec2 pos = home + 0.12 * vec2(sin(iTime * (0.21 + 0.2 * h3) + fi), cos(iTime * (0.17 + 0.2 * h1) + fi * 1.3));
    float blink = pow(0.5 + 0.5 * sin(iTime * (0.9 + h3 * 1.4) + h2 * 40.0), 3.0);
    float d = length(p - pos);
    glow += blink * 0.00022 / (d * d + 0.00012);
  }
  float a = clamp(glow, 0.0, 1.0);
  o = vec4(vec3(1.0, 0.86, 0.42) * a, a);
}`,

    dust: HASH + `
void mainImage(out vec4 o, in vec2 fc) {
  vec2 p = (fc - iFocus.xy) / iFocus.z;
  float a = 0.0;
  for (int i = 0; i < 3; i++) {
    float fi = float(i);
    float scale = 14.0 + fi * 9.0;
    vec2 q = p * scale + vec2(iTime * 0.05, iTime * 0.02) * scale * (1.0 + fi * 0.3);
    vec2 id = floor(q), gv = fract(q) - 0.5;
    float h = wlHash(id + fi * 31.0);
    if (h < 0.6) continue;
    vec2 off = vec2(wlHash(id + 5.3), wlHash(id + 9.1)) - 0.5;
    off += 0.25 * vec2(sin(iTime * 0.4 + h * 20.0), cos(iTime * 0.33 + h * 11.0));
    float twinkle = 0.55 + 0.45 * sin(iTime * (1.0 + h * 2.0) + h * 50.0);
    a += smoothstep(0.06, 0.0, length(gv - off * 0.6)) * twinkle * (0.6 - fi * 0.15);
  }
  a = clamp(a, 0.0, 0.8);
  o = vec4(vec3(1.0, 0.93, 0.8) * a, a);
}`,
  };

  // Slow "camera" drift around the focus point. The transform origin and offsets are in canvas
  // coordinates, so every Extend slice moves as one picture.
  class Drift {
    constructor(img, timeline, getView) {
      this.img = img;
      this.timeline = timeline;
      this.getView = getView;
      this.raf = 0;
      this.last = 0;
      this.onFrame = (now) => this.frame(now);
      img.style.willChange = 'transform';
    }

    start() {
      if (!this.raf) this.raf = requestAnimationFrame(this.onFrame);
    }

    stop() {
      if (this.raf) cancelAnimationFrame(this.raf);
      this.raf = 0;
    }

    frame(now) {
      this.raf = requestAnimationFrame(this.onFrame);
      if (now - this.last < 45) return; // ~20 updates/s is plenty for a movement this slow
      this.last = now;
      this.apply();
    }

    apply() {
      const t = this.timeline();
      const v = this.getView();
      const k = v.viewport.width / Math.max(1, window.innerWidth);
      const ox = (v.focus.x - v.viewport.x) / k;
      const oy = (v.focus.y - v.viewport.y) / k;
      const scale = 1.06 + 0.03 * Math.sin(t * Math.PI * 2 / 70);
      const amp = 0.006 * v.focus.size / k;
      const tx = amp * Math.sin(t * Math.PI * 2 / 90);
      const ty = amp * Math.cos(t * Math.PI * 2 / 110);
      this.img.style.transformOrigin = ox + 'px ' + oy + 'px';
      this.img.style.transform = 'translate(' + tx + 'px,' + ty + 'px) scale(' + scale + ')';
    }

    resize() { this.apply(); }

    dispose() {
      this.stop();
      this.img.style.transform = '';
      this.img.style.willChange = '';
    }
  }

  function overlay(code, name, container, timeline, getOptions, getView, getPixelSize) {
    const canvas = document.createElement('canvas');
    canvas.className = 'layer overlay';
    container.appendChild(canvas);
    const program = { name, overlay: true, passes: [{ type: 'image', code, inputs: [] }] };
    const runtime = new window.WavelitShaderRuntime(canvas, program, getOptions, getPixelSize, getView, timeline);
    return {
      start: () => runtime.start(),
      stop: () => runtime.stop(),
      resize: () => runtime.resize(),
      dispose: () => { runtime.dispose(); canvas.remove(); },
    };
  }

  window.WavelitEffects = {
    /** GLSL of an overlay effect (used by the shader test harness). */
    code: (effect) => SHADERS[effect] || null,
    create(effect, container, img, timeline, getOptions, getView, getPixelSize) {
      if (effect === 'drift') return new Drift(img, timeline, getView);
      const code = SHADERS[effect];
      return code ? overlay(code, effect, container, timeline, getOptions, getView, getPixelSize) : null;
    },
  };
})();
