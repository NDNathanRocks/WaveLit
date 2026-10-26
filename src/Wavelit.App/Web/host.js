'use strict';

// Wallpaper host page. Receives commands from the Wavelit process over WebView2 web messages.
(function () {
  const webview = window.chrome && window.chrome.webview;
  if (!webview) return;

  const post = (msg) => webview.postMessage(msg);
  const stage = document.getElementById('stage');

  let options = { fps: 30, scale: 0.75, fit: 'cover', muted: true, volume: 0.4, smoothLoop: true };
  let pixelSize = null;
  let view = null;
  let framing = null;   // framing editor: { zoom, x, y } (centre as a fraction of the canvas), or automatic
  let current = null;
  let interactive = null;   // the interactive layer (interact.js) over whatever is showing
  let paused = false;

  const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
  const getOptions = () => options;
  const getPixelSize = () => pixelSize || {
    width: Math.round(window.innerWidth * window.devicePixelRatio),
    height: Math.round(window.innerHeight * window.devicePixelRatio),
  };

  // Where this window sits on the wallpaper canvas (physical pixels, top-left origin). In Extend mode
  // the canvas covers every display and each window draws its own slice; otherwise it's the window itself.
  const getView = () => {
    if (view) return view;
    const px = getPixelSize();
    return {
      canvas: { width: px.width, height: px.height },
      viewport: { x: 0, y: 0, width: px.width, height: px.height },
      focus: { x: px.width / 2, y: px.height / 2, size: Math.min(px.width, px.height) },
      clockEpoch: null,
    };
  };

  // ---------------------------------------------------------------- clock

  // Seconds on the wallpaper's timeline. Extend slices share a wall-clock epoch so every display shows
  // the same moment; a lone window runs its own clock, which stops while paused.
  let localBase = 0;
  let localStart = performance.now();

  const isSynced = () => !!(view && view.clockEpoch);
  const timeline = () => {
    if (isSynced()) return Math.max(0, (Date.now() - view.clockEpoch) / 1000);
    return paused ? localBase : localBase + (performance.now() - localStart) / 1000;
  };
  const resetClock = (seconds) => {
    localBase = seconds;
    localStart = performance.now();
  };

  const describe = (err) => String((err && err.message) || err || 'Unknown error');

  // ---------------------------------------------------------------- layout

  // Sizes media to the canvas (cover / contain / fill) with its centre on the framing-editor choice or
  // else as close to the focus point as the fit allows, then shifts it into this window's slice.
  // Keeps the subject off the bezels. Mirrors MediaPlacement.Place in Wavelit.Core (editor preview).
  function placeMedia(el, mediaWidth, mediaHeight) {
    if (!(mediaWidth > 0 && mediaHeight > 0)) return;
    const v = getView();
    const cw = v.canvas.width, ch = v.canvas.height;
    let w = cw, h = ch;
    if (options.fit !== 'fill') {
      const s = options.fit === 'contain'
        ? Math.min(cw / mediaWidth, ch / mediaHeight)
        : Math.max(cw / mediaWidth, ch / mediaHeight);
      w = mediaWidth * s;
      h = mediaHeight * s;
    }
    let cx = v.focus.x, cy = v.focus.y;
    if (framing) {
      const z = clamp(framing.zoom, 1, 4);
      w *= z;
      h *= z;
      cx = clamp(framing.x, -0.5, 1.5) * cw;
      cy = clamp(framing.y, -0.5, 1.5) * ch;
    }
    const x = clamp(cx - w / 2, Math.min(0, cw - w), Math.max(0, cw - w)) - v.viewport.x;
    const y = clamp(cy - h / 2, Math.min(0, ch - h), Math.max(0, ch - h)) - v.viewport.y;
    // Physical pixels → CSS pixels for this window's display.
    const k = v.viewport.width / Math.max(1, window.innerWidth);
    el.style.left = (x / k) + 'px';
    el.style.top = (y / k) + 'px';
    el.style.width = (w / k) + 'px';
    el.style.height = (h / k) + 'px';
  }

  // ---------------------------------------------------------------- video

  const FADE_SECONDS = 1.2;
  const PREPARE_SECONDS = 2.5;
  const MIN_CROSSFADE_DURATION = 6;
  const SEEK_THRESHOLD = 0.75;   // seconds off the timeline before we jump instead of nudging
  const NUDGE_THRESHOLD = 0.03;
  const MAX_NUDGE = 0.08;        // ±8% playback rate while catching up is invisible

  // Plays a clip on the wallpaper timeline. The position of every frame is a pure function of the
  // timeline, so Extend slices on different displays stay in step (drift is corrected continuously).
  // Smooth loop: clip A plays to its end while clip B fades in from the start over the last FADE seconds.
  class VideoRenderer {
    constructor(src, requestId) {
      this.src = src;
      this.requestId = requestId;
      this.videos = [];
      this.reportedLoaded = false;
      this.disposed = false;
      this.frameCallback = 0;
      this.frameVideo = null;
      this.timer = 0;
      this.cycle = -1;
    }

    mount(container) {
      this.container = container;
      this.videos.push(this.createVideo(container));
      this.applyOptions(options);
      if (!paused) this.resume();
    }

    createVideo(container) {
      const v = document.createElement('video');
      v.className = 'media';
      v.muted = true;
      v.playsInline = true;
      v.disablePictureInPicture = true;
      v.disableRemotePlayback = true;
      v.preload = 'auto';
      v.style.opacity = '0';
      v.addEventListener('error', () => this.fail(v));
      v.addEventListener('loadedmetadata', () => {
        placeMedia(v, v.videoWidth, v.videoHeight);
        if (v === this.videos[0]) post({ type: 'media', width: v.videoWidth, height: v.videoHeight });
        this.tick();
      });
      v.addEventListener('loadeddata', () => {
        if (this.reportedLoaded) return;
        this.reportedLoaded = true;
        if (!isSynced()) resetClock(0); // a lone window starts the clip from its first frame
        post({ type: 'loaded', id: this.requestId });
        this.tick();
      });
      v.src = this.src;
      container.appendChild(v);
      return v;
    }

    fail(v) {
      if (this.disposed) return;
      const code = v.error ? v.error.code : 0;
      const message = code === 4
        ? "This video format isn't supported. Use MP4 (H.264) or WebM."
        : code === 3 ? 'The video file is damaged and could not be decoded.'
        : 'The video could not be loaded.';
      const detail = v.error && v.error.message ? v.error.message : '';
      post({ type: 'error', id: this.requestId, message, detail });
    }

    get duration() {
      const v = this.videos[0];
      return v && Number.isFinite(v.duration) && v.duration > 0 ? v.duration : 0;
    }

    get crossfade() {
      return options.smoothLoop && this.duration >= MIN_CROSSFADE_DURATION;
    }

    second() {
      if (this.videos.length < 2) {
        const v = this.createVideo(this.container);
        if (this.videos[0].videoWidth) placeMedia(v, this.videos[0].videoWidth, this.videos[0].videoHeight);
        this.videos.push(v);
      }
      return this.videos[1];
    }

    // Brings every element to where the timeline says it should be.
    tick() {
      if (this.disposed || paused) return;
      const d = this.duration;
      if (!d) return;
      const t = timeline();

      if (!this.crossfade) {
        const v = this.videos[0];
        v.loop = true;
        this.show(v, 1, 1);
        if (this.videos[1]) {
          this.hide(this.videos[1]);
          this.videos[1].pause();
        }
        this.follow(v, t % d, d, true);
        this.listen(v);
        return;
      }

      const span = d - FADE_SECONDS;         // one loop cycle
      const cycle = Math.floor(t / span);
      const phase = t - cycle * span;        // position in the active clip
      const active = this.videos[cycle % 2 === 0 ? 0 : 1] || this.second();
      const other = active === this.videos[0] ? (this.videos[1] || null) : this.videos[0];
      active.loop = false;
      if (other) other.loop = false;

      // Active clip fades in over the first FADE seconds of its cycle, on top of the previous one.
      const fading = cycle > 0 && phase < FADE_SECONDS && other;
      this.show(active, fading ? phase / FADE_SECONDS : 1, 2);
      this.follow(active, phase, d, false);
      if (fading) {
        this.show(other, 1, 1);
        this.follow(other, phase + span, d, false);
      } else if (other) {
        if (phase >= span - PREPARE_SECONDS) {
          // Next cycle is near: park the other clip on its first frame so it can start instantly.
          this.hide(other);
          if (!other.paused) other.pause();
          if (other.currentTime > 0.05 && !other.seeking) other.currentTime = 0;
        } else {
          this.hide(other);
          if (!other.paused) other.pause();
        }
      } else if (phase >= span - PREPARE_SECONDS) {
        this.second();
      }
      this.applyAudio(active);
      this.listen(active);
    }

    show(v, opacity, z) {
      v.style.opacity = String(clamp(opacity, 0, 1));
      v.style.zIndex = String(z);
    }

    hide(v) {
      v.style.opacity = '0';
      v.style.zIndex = '0';
    }

    // Keeps one element on the timeline: plays it, nudges its speed for small drift, seeks for big drift.
    follow(v, expected, duration, wraps) {
      if (v.paused) {
        if (Math.abs(v.currentTime - expected) > NUDGE_THRESHOLD && !v.seeking) v.currentTime = expected;
        const p = v.play();
        if (p && p.catch) p.catch(() => {});
        return;
      }
      if (v.seeking) return;
      let diff = v.currentTime - expected;
      if (wraps) diff = ((diff + duration * 1.5) % duration) - duration / 2;
      if (Math.abs(diff) > SEEK_THRESHOLD) {
        v.currentTime = clamp(expected, 0, Math.max(0, duration - 0.05));
        v.playbackRate = 1;
      } else if (Math.abs(diff) > NUDGE_THRESHOLD) {
        v.playbackRate = 1 - clamp(diff * 0.5, -MAX_NUDGE, MAX_NUDGE);
      } else if (v.playbackRate !== 1) {
        v.playbackRate = 1;
      }
    }

    applyAudio(active) {
      for (const v of this.videos) {
        v.muted = v !== active || options.muted;
        v.volume = clamp(options.volume, 0, 1);
      }
    }

    // Per-frame updates while playing (smooth fades), plus a slow timer in case frames stop arriving.
    listen(v) {
      if (this.frameVideo !== v || !this.frameCallback) {
        if (this.frameCallback && this.frameVideo) this.frameVideo.cancelVideoFrameCallback(this.frameCallback);
        this.frameVideo = v;
        this.frameCallback = v.requestVideoFrameCallback ? v.requestVideoFrameCallback(() => {
          this.frameCallback = 0;
          this.tick();
        }) : 0;
      }
      if (!this.timer) this.timer = setInterval(() => this.tick(), 500);
    }

    stopListening() {
      if (this.frameCallback && this.frameVideo) this.frameVideo.cancelVideoFrameCallback(this.frameCallback);
      this.frameCallback = 0;
      this.frameVideo = null;
      if (this.timer) clearInterval(this.timer);
      this.timer = 0;
    }

    applyOptions(o) {
      for (const v of this.videos) {
        if (v.videoWidth) placeMedia(v, v.videoWidth, v.videoHeight);
      }
      if (this.videos.length) this.applyAudio(this.videos[0]);
      this.tick();
    }

    resize() {
      this.applyOptions(options);
    }

    pause() {
      this.stopListening();
      for (const v of this.videos) v.pause();
    }

    resume() {
      this.tick();
      if (!this.duration) {
        const p = this.videos[0] && this.videos[0].play();
        if (p && p.catch) p.catch(() => {});
      }
    }

    dispose() {
      this.disposed = true;
      this.stopListening();
      for (const v of this.videos) {
        v.pause();
        // Detaching the source releases the decoder and its GPU surfaces right away.
        v.removeAttribute('src');
        v.load();
        v.remove();
      }
      this.videos = [];
    }
  }

  // ---------------------------------------------------------------- image

  // A still image placed on the canvas, optionally brought to life by a motion effect ("living stills").
  class ImageRenderer {
    constructor(src, effect, requestId) {
      this.src = src;
      this.effect = effect;
      this.requestId = requestId;
    }

    mount(container) {
      const img = document.createElement('img');
      img.className = 'media';
      img.decoding = 'async';
      img.alt = '';
      img.addEventListener('load', () => {
        if (this.disposed) return;
        this.place();
        img.style.opacity = '1';
        post({ type: 'media', width: img.naturalWidth, height: img.naturalHeight });
        if (window.WavelitEffects) this.effects = window.WavelitEffects.create(this.effect, container, img, timeline, getOptions, getView, getPixelSize);
        if (this.effects && !paused) this.effects.start();
        post({ type: 'loaded', id: this.requestId });
      });
      img.addEventListener('error', () => {
        if (this.disposed) return;
        post({ type: 'error', id: this.requestId, message: "This image couldn't be opened. Use JPG, PNG, WebP, GIF or AVIF." });
      });
      img.src = this.src;
      container.appendChild(img);
      this.img = img;
    }

    place() {
      if (this.img && this.img.naturalWidth) placeMedia(this.img, this.img.naturalWidth, this.img.naturalHeight);
      if (this.effects) this.effects.resize();
    }

    applyOptions() { this.place(); }
    resize() { this.place(); }
    pause() { if (this.effects) this.effects.stop(); }
    resume() { if (this.effects) this.effects.start(); }

    dispose() {
      this.disposed = true;
      if (this.effects) this.effects.dispose();
      this.effects = null;
      if (this.img) {
        this.img.removeAttribute('src');
        this.img.remove();
      }
    }
  }

  // ---------------------------------------------------------------- shader

  class ShaderRenderer {
    constructor(program, requestId) {
      this.program = program;
      this.requestId = requestId;
    }

    mount(container) {
      this.canvas = document.createElement('canvas');
      this.canvas.className = 'layer';
      container.appendChild(this.canvas);
      this.runtime = new window.WavelitShaderRuntime(this.canvas, this.program, getOptions, getPixelSize, getView, timeline);
      if (!paused) this.runtime.start();
      post({ type: 'loaded', id: this.requestId });
    }

    applyOptions() { this.runtime && this.runtime.resize(); }
    resize() { this.runtime && this.runtime.resize(); }
    pause() { this.runtime && this.runtime.stop(); }
    resume() { this.runtime && this.runtime.start(); }

    dispose() {
      if (this.runtime) this.runtime.dispose();
      this.runtime = null;
      if (this.canvas) this.canvas.remove();
    }
  }

  // ---------------------------------------------------------------- interactive layer

  // Keeps the interactive layer in line with the options: created over any wallpaper, recreated when
  // its settings change, gone when it's off or nothing is showing.
  function syncInteractive() {
    const wanted = current && options.interactive && options.interactive.style !== 'off' ? options.interactive : null;
    const key = wanted ? JSON.stringify(wanted) : null;
    if (interactive && interactive.key === key) return;
    if (interactive) {
      try { interactive.dispose(); } catch (e) { /* already torn down */ }
      interactive = null;
    }
    if (!wanted || !window.WavelitInteractive) return;
    interactive = window.WavelitInteractive.create(wanted, stage, timeline, getOptions, getView, getPixelSize);
    if (interactive && !paused) interactive.start();
  }

  // ---------------------------------------------------------------- commands

  function swap(next) {
    if (interactive) {
      try { interactive.dispose(); } catch (e) { /* already torn down */ }
      interactive = null;
    }
    if (current) {
      try { current.dispose(); } catch (e) { /* already torn down */ }
    }
    current = null;
    stage.replaceChildren();
    resetClock(0);
    if (!next) return;
    current = next;
    next.mount(stage);
    if (paused) next.pause();
    syncInteractive();
  }

  function setPaused(value) {
    if (value === paused) return;
    if (value) localBase = timeline(); // freeze the lone-window clock where it is
    else localStart = performance.now();
    paused = value;
  }

  const isRect = (r) => r && [r.x, r.y, r.width, r.height].every(Number.isFinite) && r.width > 0 && r.height > 0;

  function handle(msg) {
    if (!msg || typeof msg.type !== 'string') return;
    try {
      switch (msg.type) {
        case 'options':
          options = Object.assign({}, options, msg.options || {});
          if (current) current.applyOptions(options);
          syncInteractive();
          break;
        case 'resize':
          if (msg.width > 0 && msg.height > 0) pixelSize = { width: msg.width, height: msg.height };
          if (current) current.resize();
          if (interactive) interactive.resize();
          break;
        case 'view': {
          const c = msg.canvas, f = msg.focus;
          if (!c || !(c.width > 0 && c.height > 0) || !isRect(msg.viewport) || !f || !Number.isFinite(f.x)) break;
          const wasSynced = isSynced();
          const now = timeline();
          view = {
            canvas: { width: c.width, height: c.height },
            viewport: msg.viewport,
            focus: { x: f.x, y: f.y, size: f.size > 0 ? f.size : Math.min(c.width, c.height) },
            clockEpoch: Number.isFinite(msg.clockEpoch) ? msg.clockEpoch : null,
          };
          // Leaving Extend: carry on from the current moment instead of jumping back to zero.
          if (wasSynced && !isSynced()) resetClock(now);
          if (current) current.resize();
          if (interactive) interactive.resize();
          break;
        }
        case 'video':
          if (typeof msg.src !== 'string' || !msg.src.startsWith('https://')) throw new Error('Invalid video address.');
          swap(new VideoRenderer(msg.src, msg.id));
          break;
        case 'shader':
          swap(new ShaderRenderer(msg.program, msg.id));
          break;
        case 'image':
          if (typeof msg.src !== 'string' || !msg.src.startsWith('https://')) throw new Error('Invalid image address.');
          swap(new ImageRenderer(msg.src, typeof msg.effect === 'string' ? msg.effect : 'none', msg.id));
          break;
        case 'framing': {
          const f = msg.framing;
          framing = f && [f.zoom, f.x, f.y].every(Number.isFinite) ? { zoom: f.zoom, x: f.x, y: f.y } : null;
          if (current) current.resize();
          break;
        }
        case 'clear':
          swap(null);
          break;
        case 'pause':
          setPaused(true);
          if (current) current.pause();
          if (interactive) interactive.stop();
          break;
        case 'resume':
          setPaused(false);
          if (current) current.resume();
          if (interactive) interactive.start();
          break;
        case 'pointer':
          if (window.WavelitPointer) window.WavelitPointer.update(msg);
          break;
        case 'stats':   // --frame-stats diagnostic: pacing of every running renderer
          post({ type: 'stats', items: window.WavelitFrameStats ? window.WavelitFrameStats() : [] });
          break;
      }
    } catch (err) {
      swap(null);
      post({ type: 'error', id: msg.id, message: describe(err) });
    }
  }

  webview.addEventListener('message', (e) => handle(e.data));
  window.addEventListener('contextmenu', (e) => e.preventDefault());
  post({ type: 'ready' });
})();
