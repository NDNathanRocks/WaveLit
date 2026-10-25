'use strict';

// Shadertoy-compatible WebGL2 runtime: Common + Buffer A–D (ping-pong float targets) + Image,
// texture and keyboard inputs, FPS cap and render-scale for low GPU cost.
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
  mainImage(color, gl_FragCoord.xy);
  wavelitFragColor = vec4(color.rgb, 1.0);
}`;

  const HEADER_LINES = HEADER.split('\n').length - 1;

  class ShaderRuntime {
    constructor(canvas, program, getOptions, getPixelSize) {
      this.canvas = canvas;
      this.program = program;
      this.getOptions = getOptions;
      this.getPixelSize = getPixelSize;
      this.raf = 0;
      this.running = false;
      this.lastFrame = 0;
      this.time = 0;
      this.frameIndex = 0;
      this.fpsEstimate = 30;
      this.disposed = false;
      this.onFrame = (t) => this.frame(t);

      const gl = canvas.getContext('webgl2', {
        alpha: false, antialias: false, depth: false, stencil: false,
        premultipliedAlpha: false, preserveDrawingBuffer: false, powerPreference: 'default',
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
          const source = HEADER + common + '\n' + p.code + (p.type === 'image' ? FOOTER_IMAGE : FOOTER_BUFFER);
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
            },
          };
        });
      gl.deleteShader(vs);

      if (!this.passes.some(p => p.type === 'image')) throw new Error('Shader has no image pass.');

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
      const fps = Math.max(10, this.getOptions().fps || 30);
      const interval = 1000 / fps;
      if (this.lastFrame && now - this.lastFrame < interval - 2) return;
      const dt = this.lastFrame ? Math.min((now - this.lastFrame) / 1000, 0.1) : 1 / fps;
      this.lastFrame = now;
      this.time += dt;
      this.fpsEstimate = this.fpsEstimate * 0.9 + (1 / Math.max(dt, 1e-3)) * 0.1;
      if (!this.gl.isContextLost()) this.render(dt);
    }

    render(dt) {
      const gl = this.gl;
      const d = new Date();
      const date = [d.getFullYear(), d.getMonth(), d.getDate(),
        d.getHours() * 3600 + d.getMinutes() * 60 + d.getSeconds() + d.getMilliseconds() / 1000];

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
        if (L.res) gl.uniform3f(L.res, w, h, 1);
        if (L.time) gl.uniform1f(L.time, this.time);
        if (L.delta) gl.uniform1f(L.delta, dt);
        if (L.rate) gl.uniform1f(L.rate, this.fpsEstimate);
        if (L.frame) gl.uniform1i(L.frame, this.frameIndex);
        if (L.chTime) gl.uniform1fv(L.chTime, [this.time, this.time, this.time, this.time]);
        if (L.chRes) gl.uniform3fv(L.chRes, chRes);
        if (L.mouse) gl.uniform4f(L.mouse, 0, 0, 0, 0);
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

    dispose() {
      this.disposed = true;
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
})();
