'use strict';

// Studio page (dashboard): the live preview of a shader, or of an overlay over a sample wallpaper, in one
// window or across the user's real monitor layout; and the safety check that runs before anything can be
// used: compile, a staged timing probe, a blank-image test and a thumbnail. Uses the real wallpaper runtime.
// Shader code arrives as data in web messages and is only ever passed to gl.shaderSource.
(function () {
  const webview = window.chrome && window.chrome.webview;
  if (!webview) return;

  const post = (msg) => webview.postMessage(msg);
  const stage = document.getElementById('stage');
  const hint = document.getElementById('hint');
  const empty = document.getElementById('empty');
  const Runtime = window.WavelitShaderRuntime;
  const Interactive = window.WavelitInteractive;
  const describe = (err) => String((err && err.message) || err || 'Unknown error');

  // Overlays preview and test with middle-of-the-range controls.
  const OVERLAY_TINT = [1.0, 0.95, 0.88, 0.5];
  const OVERLAY_STYLE = [1.1, 1.0, 1.0, 1.0];   // size, speed, cursor response (gentle), opacity

  const started = performance.now();
  const timeline = () => (performance.now() - started) / 1000;

  // A program for Studio code. Overlays compile the shared helpers as a common pass, so compile errors
  // point at the user's own lines.
  function programFor(kind, code, name, params) {
    const p = Array.isArray(params) ? params.slice(0, 4) : undefined;
    if (kind === 'overlay') {
      return {
        name, overlay: true, tint: () => OVERLAY_TINT, style: () => OVERLAY_STYLE, params: p,
        passes: [{ type: 'common', code: Interactive.common }, { type: 'image', code, inputs: [] }],
      };
    }
    return { name, params: p, passes: [{ type: 'image', code, inputs: [] }] };
  }

  const AUDIO = /\b(iAudio|iBeat|iSpectrum|iPeaks|iCentroid|iAudioPresence)\b/;
  const usesAudio = (code) => typeof code === 'string' && AUDIO.test(code);

  // Loud, busy music for the probe, so a shader's heaviest musical moment is what gets timed.
  function busyAudio() {
    const spectrum = new Float32Array(64).fill(0.85), peaks = new Float32Array(64).fill(0.95);
    return {
      sample: () => ({ audio: [0.9, 0.85, 0.8, 0.85], beat: [1, 0.05, 128, 64], spectrum, peaks, centroid: 0.5, presence: 1 }),
      isActive: () => true, update() {}, configure() {}, demo() {},
    };
  }

  // Every slider at its maximum: no slider position may break the budget (plan §21.5).
  function maxParams(code) {
    const declared = window.WavelitShaderParams ? window.WavelitShaderParams(code) : [];
    return declared.map(d => d.max);
  }

  const isRect = (r) => r && [r.x, r.y, r.width, r.height].every(Number.isFinite) && r.width > 0 && r.height > 0;

  // ---------------------------------------------------------------- preview

  // The canvas the preview pretends to be: one 16:9 screen, or the user's real Extend layout.
  const SINGLE = {
    canvas: { width: 1920, height: 1080 },
    slices: [{ x: 0, y: 0, width: 1920, height: 1080 }],
    focus: { x: 960, y: 540, size: 1080 },
  };

  function validLayout(l) {
    if (!l || !l.canvas || !(l.canvas.width > 0 && l.canvas.height > 0) || !Array.isArray(l.slices)) return null;
    const slices = l.slices.filter(isRect).slice(0, 8);
    const f = l.focus;
    if (!slices.length || !f || ![f.x, f.y, f.size].every(Number.isFinite) || f.size <= 0) return null;
    return { canvas: { width: l.canvas.width, height: l.canvas.height }, slices, focus: { x: f.x, y: f.y, size: f.size } };
  }

  let preview = null;     // { msg, layout, runtimes, scale, ox, oy }
  let resizeTimer = 0;

  function clearPreview() {
    if (window.WavelitAudio) window.WavelitAudio.demo(false);
    if (preview) for (const r of preview.runtimes) { try { r.dispose(); } catch (e) { /* already gone */ } }
    preview = null;
    stage.replaceChildren();
    hint.textContent = '';
    empty.classList.remove('hidden');
  }

  function showPreview(msg) {
    clearPreview();
    const kind = msg.kind === 'overlay' ? 'overlay' : 'wallpaper';
    if (typeof msg.code !== 'string' || !msg.code) return;
    const layout = validLayout(msg.layout) || SINGLE;
    const margin = layout.slices.length > 1 ? 18 : 0;
    const W = Math.max(1, window.innerWidth - margin * 2), H = Math.max(1, window.innerHeight - margin * 2);
    const scale = Math.min(W / layout.canvas.width, H / layout.canvas.height);   // canvas units → CSS px
    const ox = (window.innerWidth - layout.canvas.width * scale) / 2;
    const oy = (window.innerHeight - layout.canvas.height * scale) / 2;
    const dpr = window.devicePixelRatio || 1;
    const runtimes = [];
    preview = { msg, layout, runtimes, scale, ox, oy };
    const options = () => ({ fps: 60, scale: 1 });

    try {
      for (const slice of layout.slices) {
        const screen = document.createElement('div');
        screen.className = 'screen';
        if (layout.slices.length === 1) screen.style.borderRadius = '0';
        screen.style.left = (ox + slice.x * scale) + 'px';
        screen.style.top = (oy + slice.y * scale) + 'px';
        screen.style.width = (slice.width * scale) + 'px';
        screen.style.height = (slice.height * scale) + 'px';
        stage.appendChild(screen);
        const pixelSize = () => ({ width: Math.max(1, Math.round(slice.width * scale * dpr)), height: Math.max(1, Math.round(slice.height * scale * dpr)) });
        const view = () => ({ canvas: layout.canvas, viewport: slice, focus: layout.focus });
        const add = (program, cls) => {
          const canvas = document.createElement('canvas');
          if (cls) canvas.className = cls;
          screen.appendChild(canvas);
          const r = new Runtime(canvas, program, options, pixelSize, view, timeline);
          runtimes.push(r);
          r.start();
        };
        if (kind === 'overlay') {
          if (msg.background && Array.isArray(msg.background.passes)) add(msg.background, '');
          add(programFor('overlay', msg.code, 'studio-overlay', msg.params), 'overlay');
        } else {
          add(programFor('wallpaper', msg.code, 'studio-preview', msg.params), '');
        }
      }
      // Music shaders preview with the built-in demo signal (a beat and chords; no audio file, nothing heard).
      if (window.WavelitAudio) window.WavelitAudio.demo(usesAudio(msg.code));
      hint.textContent = msg.usesPointer ? 'Move the mouse over the preview, and click' : '';
      empty.classList.add('hidden');
      post({ type: 'previewing', id: msg.id });
    } catch (err) {
      clearPreview();
      post({ type: 'error', id: msg.id, message: describe(err) });
    }
  }

  // The preview reacts to the mouse like the desktop does: positions in canvas units, over = on a screen.
  function feedPointer(e, over) {
    if (!preview || !window.WavelitPointer) return;
    const x = (e.clientX - preview.ox) / preview.scale;
    const y = (e.clientY - preview.oy) / preview.scale;
    const onScreen = over && preview.layout.slices.some(s => x >= s.x && y >= s.y && x < s.x + s.width && y < s.y + s.height);
    window.WavelitPointer.update({ x, y, over: onScreen, down: (e.buttons & 1) === 1 });
  }
  window.addEventListener('mousemove', (e) => feedPointer(e, true));
  window.addEventListener('mousedown', (e) => feedPointer(e, true));
  window.addEventListener('mouseup', (e) => feedPointer(e, true));
  document.documentElement.addEventListener('mouseleave', (e) => feedPointer(e, false));
  window.addEventListener('resize', () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => { if (preview) showPreview(preview.msg); }, 150);
  });

  // ---------------------------------------------------------------- check

  // A cursor that is present and sweeping, so the probe times a shader's busiest case.
  function busyPointer(width, height) {
    return {
      isActive: () => true,
      sample: () => {
        const trail = new Float32Array(64);
        for (let i = 0; i < 16; i++) {
          trail[i * 4] = width * (0.3 + 0.025 * i);
          trail[i * 4 + 1] = height * (0.5 + 0.1 * Math.sin(i * 0.5));
          trail[i * 4 + 2] = i * 0.08;
          trail[i * 4 + 3] = 0.8;
        }
        return {
          pointer: [width * 0.55, height * 0.5, 1, 0.6],
          click: [width * 0.5, height * 0.5, 0.4, 1],
          trail,
          mouse: [width * 0.55, height * 0.5, width * 0.5, height * 0.5],
        };
      },
    };
  }

  const TIMES = [3.7, 41.3, 187.9, 911.4, 3607.2, 7203.9];   // seconds: early, minutes and hours in

  // Renders and waits for the GPU (a 1-pixel read), returning milliseconds.
  function timedFrame(rt, t) {
    const gl = rt.gl;
    rt.time = t;
    const px = new Uint8Array(4);
    const t0 = performance.now();
    rt.render(1 / 60);
    gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, px);
    return performance.now() - t0;
  }

  function stats(samples) {
    const s = samples.slice().sort((a, b) => a - b);
    return { median: s[s.length >> 1], p95: s[Math.min(s.length - 1, Math.floor(s.length * 0.95))] };
  }

  // Creates a runtime on a detached canvas whose size can be changed (probe stages).
  function probeRuntime(program, size) {
    const canvas = document.createElement('canvas');
    const view = () => ({
      canvas: { width: size.width, height: size.height },
      viewport: { x: 0, y: 0, width: size.width, height: size.height },
      focus: { x: size.width / 2, y: size.height / 2, size: Math.min(size.width, size.height) },
    });
    return new Runtime(canvas, program, () => ({ fps: 60, scale: 1 }), () => size, view, () => 0);
  }

  // Cost of a draw + read-back with a trivial shader at each probe size, subtracted from the measurements.
  const baseline = new Map();
  function overhead(width, height) {
    const key = width + 'x' + height;
    if (baseline.has(key)) return baseline.get(key);
    const size = { width, height };
    const rt = probeRuntime(programFor('wallpaper', 'void mainImage(out vec4 c, in vec2 f) { c = vec4(f.x * 1e-4, 0.0, 0.0, 1.0); }', 'baseline'), size);
    try {
      timedFrame(rt, 0);
      const samples = [];
      for (let i = 0; i < 8; i++) samples.push(timedFrame(rt, i));
      const v = stats(samples).median;
      baseline.set(key, v);
      return v;
    } finally {
      rt.dispose();
    }
  }

  // Blank test at the smallest size: no visible variation (wallpaper) or nothing drawn (overlay).
  function isBlank(rt, kind, size) {
    const gl = rt.gl;
    const px = new Uint8Array(size.width * size.height * 4);
    let lo = 255, hi = 0, alpha = 0;
    for (const t of [TIMES[0], TIMES[1], TIMES[3]]) {
      rt.time = t;
      rt.render(1 / 60);
      gl.readPixels(0, 0, size.width, size.height, gl.RGBA, gl.UNSIGNED_BYTE, px);
      for (let i = 0; i < px.length; i += 4) {
        const l = px[i] * 0.3 + px[i + 1] * 0.59 + px[i + 2] * 0.11;
        if (l < lo) lo = l;
        if (l > hi) hi = l;
        if (px[i + 3] > alpha) alpha = px[i + 3];
      }
    }
    return kind === 'overlay' ? alpha < 3 : hi - lo < 2 || hi < 2;
  }

  function thumbnail(kind, code, background) {
    const size = { width: 384, height: 216 };
    const out = document.createElement('canvas');
    out.width = size.width;
    out.height = size.height;
    const ctx = out.getContext('2d');
    ctx.fillStyle = '#000';
    ctx.fillRect(0, 0, size.width, size.height);
    const draw = (program) => {
      const rt = probeRuntime(program, size);
      try {
        rt.time = 12;
        rt.render(1 / 60);
        ctx.drawImage(rt.canvas, 0, 0);   // same task as the draw, so the drawing buffer is still intact
      } finally {
        rt.dispose();
      }
    };
    if (kind === 'overlay' && background && Array.isArray(background.passes)) draw(background);
    draw(programFor(kind, code, 'thumbnail'));
    return out.toDataURL('image/png').replace(/^data:image\/png;base64,/, '');
  }

  function check(msg) {
    const kind = msg.kind === 'overlay' ? 'overlay' : 'wallpaper';
    if (typeof msg.code !== 'string' || !msg.code) return { ok: false, error: 'There is no code.' };
    const BUDGET_MS = Number(msg.budget) > 0 ? Number(msg.budget) : 3;
    const size = { width: 192, height: 108 };
    let rt;
    try {
      rt = probeRuntime(programFor(kind, msg.code, 'studio-check'), size);
    } catch (err) {
      return { ok: false, error: describe(err).replace(/^Image shader failed to compile: /, '') };
    }
    const realPointer = window.WavelitPointer;
    const realAudio = window.WavelitAudio;
    try {
      const gl = rt.gl;
      const lost = () => gl.isContextLost();
      // A wallpaper must look complete without the cursor (and in silence); an overlay may only follow it.
      if (kind === 'overlay') window.WavelitPointer = busyPointer(size.width, size.height);
      if (realAudio) realAudio.demo(false);
      const blank = isBlank(rt, kind, size);
      if (lost()) return { ok: false, error: 'The graphics card reset while testing this shader.' };
      // Timing: sliders at their maximum and loud music.
      rt.setParams(maxParams(msg.code));
      if (usesAudio(msg.code)) window.WavelitAudio = busyAudio();

      // Stage 1 (1/100 of 1080p): rules out anything that would stall the GPU before testing larger sizes.
      window.WavelitPointer = busyPointer(size.width, size.height);
      timedFrame(rt, TIMES[0]);
      const tiny = Math.max(0, timedFrame(rt, TIMES[1]) - overhead(192, 108));
      if (lost()) return { ok: false, error: 'The graphics card reset while testing this shader.' };
      if (tiny > 250) return { ok: true, frameMs: tiny * 100, p95Ms: tiny * 100, blank, thumbnail: null };

      // Stage 2 (1/16): a rough estimate; obviously heavy shaders stop here.
      size.width = 480; size.height = 270;
      rt.resize();
      window.WavelitPointer = busyPointer(size.width, size.height);
      const o2 = overhead(480, 270);
      const s2 = [];
      for (let i = 0; i < 4; i++) s2.push(Math.max(0, timedFrame(rt, TIMES[i % TIMES.length]) - o2) * 16);
      const st2 = stats(s2);
      if (st2.median > BUDGET_MS * 4) return { ok: true, frameMs: st2.median, p95Ms: st2.p95, blank, thumbnail: null };

      // Stage 3 (1/4): the measurement, scaled to 1920×1080.
      size.width = 960; size.height = 540;
      rt.resize();
      window.WavelitPointer = busyPointer(size.width, size.height);
      const o3 = overhead(960, 540);
      timedFrame(rt, TIMES[0]);
      const s3 = [];
      for (let i = 0; i < 24; i++) s3.push(Math.max(0, timedFrame(rt, TIMES[i % TIMES.length] + i * 0.37) - o3) * 4);
      if (lost()) return { ok: false, error: 'The graphics card reset while testing this shader.' };
      const st3 = stats(s3);
      window.WavelitPointer = realPointer;
      window.WavelitAudio = realAudio;
      return { ok: true, frameMs: st3.median, p95Ms: st3.p95, blank, thumbnail: thumbnail(kind, msg.code, msg.background) };
    } catch (err) {
      return { ok: false, error: describe(err) };
    } finally {
      window.WavelitPointer = realPointer;
      window.WavelitAudio = realAudio;
      try { rt.dispose(); } catch (e) { /* already gone */ }
    }
  }

  // ---------------------------------------------------------------- messages

  function handle(msg) {
    if (!msg || typeof msg.type !== 'string') return;
    switch (msg.type) {
      case 'preview':
        showPreview(msg);
        break;
      case 'clear':
        clearPreview();
        break;
      case 'params':   // the Studio's sliders, live
        if (preview && Array.isArray(msg.values)) {
          // Only the Studio shader's runtimes (not the wallpaper an overlay is previewed over).
          for (const r of preview.runtimes) if (r.program.passes.some(p => p.type === 'image' && p.code === preview.msg.code)) r.setParams(msg.values.slice(0, 4));
          preview.msg.params = msg.values.slice(0, 4);
        }
        break;
      case 'check': {
        const result = check(msg);
        post(Object.assign({ type: 'checked', id: msg.id }, result));
        break;
      }
    }
  }

  webview.addEventListener('message', (e) => handle(e.data));
  window.addEventListener('contextmenu', (e) => e.preventDefault());
  post({ type: 'ready' });
})();
