'use strict';

// Shadertoy-compatible WebGL2 runtime: Common + Buffer A–D (ping-pong float targets) + Image,
// texture and keyboard inputs, FPS cap and render-scale for low GPU cost.
// Canvas-aware: in Extend mode the image pass sees the whole multi-display canvas as iResolution and
// fragCoord, while this window only draws its slice. Wavelit extras (ignored by Shadertoy code):
//   iFocus    xy = where the subject belongs (primary display centre), z = that display's short side
//   iViewport this window's slice of the canvas (x, y, width, height), all in fragCoord pixels
//   iPointer, iClick, iTrail  the cursor over the desktop (see pointer.js); iMouse follows Shadertoy
// Shader source arrives as data (web message) and is only ever passed to gl.shaderSource.
(function () {
  const VERTEX = `#version 300 es
in vec2 wavelitPos;
void main() { gl_Position = vec4(wavelitPos, 0.0, 1.0); }`;

  const HEADER = `#version 300 es
precision highp float;
precision highp int;
precision highp sampler2D;
uniform vec3 iResolution;
uniform float iTime;
uniform float iTimeDelta;
uniform float iFrameRate;
uniform int iFrame;
uniform float iChannelTime[4];
uniform vec3 iChannelResolution[4];
uniform vec4 iMouse;
uniform vec4 iDate;
uniform float iSampleRate;
uniform sampler2D iChannel0;
uniform sampler2D iChannel1;
uniform sampler2D iChannel2;
uniform sampler2D iChannel3;
uniform vec3 iFocus;
uniform vec4 iViewport;
uniform vec2 wavelitOffset;
uniform vec4 iPointer;
uniform vec4 iClick;
uniform vec4 iTrail[16];
uniform vec4 wavelitTint;
uniform vec4 wavelitStyle;
out vec4 wavelitFragColor;
`;

  const FOOTER_BUFFER = `
void main() {
  vec4 color = vec4(0.0, 0.0, 0.0, 1.0);
  mainImage(color, gl_FragCoord.xy);
  wavelitFragColor = color;
}`;

  const FOOTER_IMAGE = `
void main() {
  vec4 color = vec4(0.0, 0.0, 0.0, 1.0);
  mainImage(color, gl_FragCoord.xy + wavelitOffset);
  wavelitFragColor = vec4(color.rgb, 1.0);
}`;

  // Transparent overlays (image effects) keep their alpha; output is premultiplied.
  const FOOTER_OVERLAY = `
void main() {
  vec4 color = vec4(0.0);
  mainImage(color, gl_FragCoord.xy + wavelitOffset);
  wavelitFragColor = clamp(color, 0.0, 1.0);
}`;

  const HEADER_LINES = HEADER.split('\n').length - 1;

  // Every live runtime, for the --frame-stats diagnostic.
  const live = new Set();

  class ShaderRuntime {
    constructor(canvas, program, getOptions, getPixelSize, getView, getTime) {
      this.canvas = canvas;
      this.program = program;
      this.getOptions = getOptions;
      this.getPixelSize = getPixelSize;
      this.getView = getView;
      this.getTime = getTime;
      this.raf = 0;
      this.running = false;
      this.lastFrame = 0;
      this.time = 0;
      this.frameIndex = 0;
      this.fpsEstimate = 30;
      this.disposed = false;
      this.onFrame = (t) => this.frame(t);
      this.vsync = 0;         // measured display refresh interval (ms)
      this.lastTick = 0;
      this.tick = 0;
      this.rendered = [];     // timestamps of recent rendered frames (frame stats)
      live.add(this);

      const overlay = !!program.overlay;
      const gl = canvas.getContext('webgl2', {
        alpha: overlay, antialias: false, depth: false, stencil: false,
        premultipliedAlpha: overlay, preserveDrawingBuffer: false, powerPreference: 'default',
      });
      if (!gl) throw new Error('WebGL 2 is not available on this PC (check your graphics drivers).');
      this.gl = gl;

      this.onLost = (e) => { e.preventDefault(); this.stop(); };
      this.onRestored = () => { this.build(); if (this.wantRunning) this.start(); };
      canvas.addEventListener('webglcontextlost', this.onLost);
      canvas.addEventListener('webglcontextrestored', this.onRestored);

      this.build();
    }

    build() {
      const gl = this.gl;
      this.halfFloat = !!gl.getExtension('EXT_color_buffer_float');
      gl.getExtension('OES_texture_float_linear');

      this.vao = gl.createVertexArray();
      gl.bindVertexArray(this.vao);
      this.vbo = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, this.vbo);
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
      gl.enableVertexAttribArray(0);
      gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);

      this.black = this.solidTexture();
      this.keyboard = this.keyboardTexture();
      this.textures = new Map();   // url -> { tex, w, h }
      this.samplers = new Map();   // "filter|wrap" -> WebGLSampler
      this.targets = new Map();    // buffer output id -> ping-pong target

      const passes = this.program.passes || [];
      const common = passes.filter(p => p.type === 'common').map(p => p.code).join('\n');
      const vs = this.compile(gl.VERTEX_SHADER, VERTEX, 'vertex', 0);

      this.passes = passes
        .filter(p => p.type === 'buffer' || p.type === 'image')
        .map(p => {
          const footer = p.type !== 'image' ? FOOTER_BUFFER : this.program.overlay ? FOOTER_OVERLAY : FOOTER_IMAGE;
          const source = HEADER + common + '\n' + p.code + footer;
          const fs = this.compile(gl.FRAGMENT_SHADER, source, p.type === 'image' ? 'Image' : 'Buffer',
            HEADER_LINES + (common ? common.split('\n').length : 0));
          const prog = gl.createProgram();
          gl.attachShader(prog, vs);
          gl.attachShader(prog, fs);
          gl.bindAttribLocation(prog, 0, 'wavelitPos');
          gl.linkProgram(prog);
          gl.deleteShader(fs);
          if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
            const log = gl.getProgramInfoLog(prog);
            gl.deleteProgram(prog);
            throw new Error('Shader failed to link: ' + log);
          }
          const u = (n) => gl.getUniformLocation(prog, n);
          return {
            type: p.type,
            outputId: p.outputId,
            inputs: (p.inputs || []).filter(i => i.channel >= 0 && i.channel < 4),
            prog,
            loc: {
              res: u('iResolution'), time: u('iTime'), delta: u('iTimeDelta'), rate: u('iFrameRate'),
              frame: u('iFrame'), chTime: u('iChannelTime'), chRes: u('iChannelResolution'),
              mouse: u('iMouse'), date: u('iDate'), sampleRate: u('iSampleRate'),
              ch: [u('iChannel0'), u('iChannel1'), u('iChannel2'), u('iChannel3')],
              focus: u('iFocus'), viewport: u('iViewport'), offset: u('wavelitOffset'),
              pointer: u('iPointer'), click: u('iClick'), trail: u('iTrail'), tint: u('wavelitTint'), style: u('wavelitStyle'),
            },
            usesPointer: /\b(iMouse|iPointer|iClick|iTrail)\b/.test(p.code + common),
          };
        });
      gl.deleteShader(vs);

      if (!this.passes.some(p => p.type === 'image')) throw new Error('Shader has no image pass.');
      this.usesPointer = this.passes.some(p => p.usesPointer);

      for (const pass of this.passes) {
        for (const input of pass.inputs) {
          if (input.kind === 'texture' && input.url && !this.textures.has(input.url)) this.loadTexture(input);
        }
      }

      this.resize();
    }

    compile(type, source, label, lineOffset) {
      const gl = this.gl;
      const shader = gl.createShader(type);
      gl.shaderSource(shader, source);
      gl.compileShader(shader);
      if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS) && !gl.isContextLost()) {
        const log = (gl.getShaderInfoLog(shader) || '')
          .replace(/ERROR: 0:(\d+)/g, (_, n) => `line ${Math.max(1, Number(n) - lineOffset)}`)
          .split('\n').filter(Boolean).slice(0, 4).join(' | ');
        gl.deleteShader(shader);
        throw new Error(`${label} shader failed to compile: ${log}`);
      }
      return shader;
    }

    solidTexture() {
      const gl = this.gl;
      const tex = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, tex);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([0, 0, 0, 255]));
      return tex;
    }

    // Shadertoy keyboard texture: 256×3, all keys released (wallpapers get no keyboard input).
    keyboardTexture() {
      const gl = this.gl;
      const tex = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, tex);
      gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8, 256, 3, 0, gl.RED, gl.UNSIGNED_BYTE, new Uint8Array(256 * 3));
      gl.pixelStorei(gl.UNPACK_ALIGNMENT, 4);
      return tex;
    }

    loadTexture(input) {
      const gl = this.gl;
      const entry = { tex: this.solidTexture(), w: 1, h: 1 };
      this.textures.set(input.url, entry);
      const img = new Image();
      img.crossOrigin = 'anonymous';
      img.onload = () => {
        if (this.disposed || gl.isContextLost()) return;
        gl.bindTexture(gl.TEXTURE_2D, entry.tex);
        gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, !!input.vFlip);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, img);
        gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
        gl.generateMipmap(gl.TEXTURE_2D);
        entry.w = img.naturalWidth;
        entry.h = img.naturalHeight;
      };
      img.src = input.url;
    }

    sampler(filter, wrap, allowMipmap) {
      const key = `${filter}|${wrap}|${allowMipmap}`;
      let s = this.samplers.get(key);
      if (s) return s;
      const gl = this.gl;
      s = gl.createSampler();
      const mag = filter === 'nearest' ? gl.NEAREST : gl.LINEAR;
      const min = filter === 'nearest' ? gl.NEAREST
        : filter === 'mipmap' && allowMipmap ? gl.LINEAR_MIPMAP_LINEAR : gl.LINEAR;
      const w = wrap === 'repeat' ? gl.REPEAT : gl.CLAMP_TO_EDGE;
      gl.samplerParameteri(s, gl.TEXTURE_MAG_FILTER, mag);
      gl.samplerParameteri(s, gl.TEXTURE_MIN_FILTER, min);
      gl.samplerParameteri(s, gl.TEXTURE_WRAP_S, w);
      gl.samplerParameteri(s, gl.TEXTURE_WRAP_T, w);
      this.samplers.set(key, s);
      return s;
    }

    resize() {
      const opts = this.getOptions();
      const px = this.getPixelSize();
      const scale = Math.min(1, Math.max(0.25, opts.scale || 1));
      const w = Math.max(1, Math.round(px.width * scale));
      const h = Math.max(1, Math.round(px.height * scale));
      if (this.canvas.width === w && this.canvas.height === h && this.targets.size === this.bufferCount()) return;
      this.canvas.width = w;
      this.canvas.height = h;
      this.recreateTargets(w, h);
      this.frameIndex = 0;
    }

    bufferCount() {
      return this.passes ? this.passes.filter(p => p.type === 'buffer' && p.outputId).length : 0;
    }

    recreateTargets(w, h) {
      const gl = this.gl;
      for (const t of this.targets.values()) this.deleteTarget(t);
      this.targets.clear();
      for (const pass of this.passes) {
        if (pass.type !== 'buffer' || !pass.outputId) continue;
        const make = () => {
          const tex = gl.createTexture();
          gl.bindTexture(gl.TEXTURE_2D, tex);
          if (this.halfFloat) gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA16F, w, h, 0, gl.RGBA, gl.HALF_FLOAT, null);
          else gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
          const fbo = gl.createFramebuffer();
          gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
          gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex, 0);
          gl.clearColor(0, 0, 0, 0);
          gl.clear(gl.COLOR_BUFFER_BIT);
          return { tex, fbo };
        };
        this.targets.set(String(pass.outputId), { read: make(), write: make(), w, h });
      }
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    }

    deleteTarget(t) {
      const gl = this.gl;
      for (const side of [t.read, t.write]) {
        gl.deleteFramebuffer(side.fbo);
        gl.deleteTexture(side.tex);
      }
    }

    start() {
      this.wantRunning = true;
      if (this.running || this.disposed) return;
      this.running = true;
      this.lastFrame = 0;
      this.lastTick = 0;
      this.raf = requestAnimationFrame(this.onFrame);
    }

    stop() {
      this.wantRunning = false;
      this.running = false;
      if (this.raf) cancelAnimationFrame(this.raf);
      this.raf = 0;
    }

    frame(now) {
      if (!this.running) return;
      this.raf = requestAnimationFrame(this.onFrame);
      const opts = this.getOptions();
      // A cursor-reactive shader follows the cursor at 60 fps while it's on the desktop, then drops back.
      const lively = this.usesPointer && window.WavelitPointer && window.WavelitPointer.isActive();
      const fps = Math.max(10, lively ? Math.max(60, opts.fps || 30) : opts.fps || 30);
      // Even pacing: render on every N-th display refresh, N = refresh / target. A time threshold would
      // land on an uneven mix of refreshes (at 143 Hz, "60 fps" became ~48 fps with judder).
      if (this.lastTick) {
        const d = now - this.lastTick;
        if (d > 2 && (!this.vsync || d < this.vsync * 1.6)) this.vsync = this.vsync ? this.vsync * 0.95 + d * 0.05 : d;
      }
      this.lastTick = now;
      const divisor = Math.max(1, Math.round(1000 / fps / (this.vsync || 1000 / 60)));
      if (++this.tick % divisor !== 0) return;
      this.rendered.push(now);
      if (this.rendered.length > 600) this.rendered.splice(0, this.rendered.length - 600);
      // iTime follows the page's wallpaper timeline (shared between Extend slices).
      const time = this.getTime();
      const dt = this.lastFrame ? Math.min(Math.max(time - this.time, 0), 0.1) : 1 / fps;
      this.lastFrame = now;
      this.time = time;
      this.fpsEstimate = this.fpsEstimate * 0.9 + (1 / Math.max(dt, 1e-3)) * 0.1;
      if (!this.gl.isContextLost()) this.render(dt);
    }

    // Maps the canvas (physical px, top-left origin) to fragCoord space (render-scaled, bottom-left origin).
    canvasSpace() {
      const v = this.getView();
      const k = this.canvas.width / Math.max(1, v.viewport.width);
      const ch = v.canvas.height;
      return {
        k,
        canvasHeight: ch,
        res: [v.canvas.width * k, ch * k],
        offset: [v.viewport.x * k, (ch - v.viewport.y - v.viewport.height) * k],
        focus: [v.focus.x * k, (ch - v.focus.y) * k, v.focus.size * k],
      };
    }

    render(dt) {
      const gl = this.gl;
      const space = this.canvasSpace();
      const d = new Date();
      const date = [d.getFullYear(), d.getMonth(), d.getDate(),
        d.getHours() * 3600 + d.getMinutes() * 60 + d.getSeconds() + d.getMilliseconds() / 1000];
      const ptr = this.usesPointer && window.WavelitPointer ? window.WavelitPointer.sample(space.k, space.canvasHeight) : null;
      const tint = this.program.tint ? this.program.tint() : null;
      const style = this.program.style ? this.program.style() : null;

      for (const pass of this.passes) {
        const target = pass.type === 'buffer' ? this.targets.get(String(pass.outputId)) : null;
        const w = target ? target.w : this.canvas.width;
        const h = target ? target.h : this.canvas.height;
        gl.bindFramebuffer(gl.FRAMEBUFFER, target ? target.write.fbo : null);
        gl.viewport(0, 0, w, h);
        gl.useProgram(pass.prog);

        const chRes = new Float32Array(12);
        for (let ch = 0; ch < 4; ch++) {
          gl.activeTexture(gl.TEXTURE0 + ch);
          gl.bindTexture(gl.TEXTURE_2D, this.black);
          gl.bindSampler(ch, null);
        }
        for (const input of pass.inputs) {
          const ch = input.channel;
          let tex = this.black, iw = 1, ih = 1, mip = false;
          if (input.kind === 'buffer') {
            const src = this.targets.get(String(input.bufferId));
            if (src) { tex = src.read.tex; iw = src.w; ih = src.h; }
          } else if (input.kind === 'texture') {
            const t = this.textures.get(input.url);
            if (t) { tex = t.tex; iw = t.w; ih = t.h; mip = true; }
          } else if (input.kind === 'keyboard') {
            tex = this.keyboard; iw = 256; ih = 3;
          }
          gl.activeTexture(gl.TEXTURE0 + ch);
          gl.bindTexture(gl.TEXTURE_2D, tex);
          gl.bindSampler(ch, this.sampler(input.kind === 'keyboard' ? 'nearest' : input.filter, input.wrap, mip));
          chRes[ch * 3] = iw; chRes[ch * 3 + 1] = ih; chRes[ch * 3 + 2] = 1;
        }

        const L = pass.loc;
        // Buffers stay window-sized (only single-window shaders use them); the image pass sees the canvas.
        const image = !target;
        const res = image ? space.res : [w, h];
        const offset = image ? space.offset : [0, 0];
        if (L.res) gl.uniform3f(L.res, res[0], res[1], 1);
        if (L.offset) gl.uniform2f(L.offset, offset[0], offset[1]);
        if (L.viewport) gl.uniform4f(L.viewport, offset[0], offset[1], w, h);
        if (L.focus) gl.uniform3f(L.focus, space.focus[0], space.focus[1], space.focus[2]);
        if (L.time) gl.uniform1f(L.time, this.time);
        if (L.delta) gl.uniform1f(L.delta, dt);
        if (L.rate) gl.uniform1f(L.rate, this.fpsEstimate);
        if (L.frame) gl.uniform1i(L.frame, this.frameIndex);
        if (L.chTime) gl.uniform1fv(L.chTime, [this.time, this.time, this.time, this.time]);
        if (L.chRes) gl.uniform3fv(L.chRes, chRes);
        // Buffers work in window pixels, the image pass in canvas pixels; the cursor follows suit.
        const shift = image ? [0, 0] : offset;
        if (L.mouse) {
          if (ptr) gl.uniform4f(L.mouse, ptr.mouse[0] - shift[0], ptr.mouse[1] - shift[1],
            ptr.mouse[2] - Math.sign(ptr.mouse[2]) * shift[0], ptr.mouse[3] - Math.sign(ptr.mouse[3]) * shift[1]);
          else gl.uniform4f(L.mouse, 0, 0, 0, 0);
        }
        if (L.pointer) {
          if (ptr) gl.uniform4f(L.pointer, ptr.pointer[0] - shift[0], ptr.pointer[1] - shift[1], ptr.pointer[2], ptr.pointer[3]);
          else gl.uniform4f(L.pointer, 0, 0, 0, 0);
        }
        if (L.click) gl.uniform4fv(L.click, ptr ? ptr.click : [0, 0, 1e4, 0]);
        if (L.trail && ptr) gl.uniform4fv(L.trail, ptr.trail);
        if (L.tint) gl.uniform4fv(L.tint, tint || [1, 1, 1, 1]);
        if (L.style) gl.uniform4fv(L.style, style || [1, 1, 1, 1]);
        if (L.date) gl.uniform4fv(L.date, date);
        if (L.sampleRate) gl.uniform1f(L.sampleRate, 44100);
        for (let ch = 0; ch < 4; ch++) if (L.ch[ch]) gl.uniform1i(L.ch[ch], ch);

        gl.drawArrays(gl.TRIANGLES, 0, 3);

        if (target) {
          const tmp = target.read;
          target.read = target.write;
          target.write = tmp;
        }
      }
      this.frameIndex++;
    }

    /** Frame pacing over the last ~600 rendered frames: rate, mean interval and p95 jitter (ms). */
    frameStats() {
      const t = this.rendered;
      if (t.length < 20) return null;
      const iv = [];
      for (let i = 1; i < t.length; i++) iv.push(t[i] - t[i - 1]);
      const sorted = iv.slice().sort((a, b) => a - b);
      const median = sorted[sorted.length >> 1];
      const dev = iv.map(v => Math.abs(v - median)).sort((a, b) => a - b);
      const mean = iv.reduce((a, b) => a + b, 0) / iv.length;
      return {
        name: this.program.name || 'shader',
        fps: Math.round(10000 / mean) / 10,
        meanMs: Math.round(mean * 100) / 100,
        p95JitterMs: Math.round(dev[Math.floor(dev.length * 0.95)] * 100) / 100,
        refreshHz: this.vsync ? Math.round(10000 / this.vsync) / 10 : null,
        frames: t.length,
      };
    }

    dispose() {
      this.disposed = true;
      live.delete(this);
      this.stop();
      const gl = this.gl;
      this.canvas.removeEventListener('webglcontextlost', this.onLost);
      this.canvas.removeEventListener('webglcontextrestored', this.onRestored);
      if (!gl.isContextLost()) {
        for (const t of this.targets.values()) this.deleteTarget(t);
        for (const t of this.textures.values()) gl.deleteTexture(t.tex);
        for (const s of this.samplers.values()) gl.deleteSampler(s);
        for (const p of this.passes || []) gl.deleteProgram(p.prog);
        gl.deleteTexture(this.black);
        gl.deleteTexture(this.keyboard);
        gl.deleteBuffer(this.vbo);
        gl.deleteVertexArray(this.vao);
      }
      // Release the GPU context immediately instead of waiting for garbage collection.
      const lose = gl.getExtension('WEBGL_lose_context');
      if (lose) lose.loseContext();
    }
  }

  window.WavelitShaderRuntime = ShaderRuntime;
  window.WavelitFrameStats = () => Array.from(live).map(r => r.frameStats()).filter(Boolean);
})();
