'use strict';

// Wallpaper host page. Receives commands from the Wavelit process over WebView2 web messages.
(function () {
  const webview = window.chrome && window.chrome.webview;
  if (!webview) return;

  const post = (msg) => webview.postMessage(msg);
  const stage = document.getElementById('stage');

  let options = { fps: 30, scale: 0.75, fit: 'cover', muted: true, volume: 0.4, smoothLoop: true };
  let pixelSize = null;
  let current = null;
  let paused = false;

  const getOptions = () => options;
  const getPixelSize = () => pixelSize || {
    width: Math.round(window.innerWidth * window.devicePixelRatio),
    height: Math.round(window.innerHeight * window.devicePixelRatio),
  };

  const describe = (err) => String((err && err.message) || err || 'Unknown error');

  // ---------------------------------------------------------------- video

  const FADE_SECONDS = 1.2;
  const PREPARE_SECONDS = 2.5;
  const MIN_CROSSFADE_DURATION = 6;

  class VideoRenderer {
    constructor(src, requestId) {
      this.src = src;
      this.requestId = requestId;
      this.videos = [];
      this.active = 0;
      this.fading = false;
      this.prepared = false;
      this.reportedLoaded = false;
      this.disposed = false;
      this.frameCallback = 0;
    }

    mount(container) {
      this.videos.push(this.createVideo(container, true));
      this.applyOptions(options);
      this.playActive();
    }

    createVideo(container, visible) {
      const v = document.createElement('video');
      v.className = 'layer';
      v.muted = true;
      v.playsInline = true;
      v.disablePictureInPicture = true;
      v.disableRemotePlayback = true;
      v.preload = 'auto';
      v.style.opacity = visible ? '1' : '0';
      v.style.zIndex = visible ? '1' : '2';
      v.style.objectFit = options.fit;
      v.addEventListener('error', () => this.fail(v));
      v.addEventListener('loadeddata', () => {
        if (!this.reportedLoaded) {
          this.reportedLoaded = true;
          post({ type: 'loaded', id: this.requestId });
        }
      });
      v.addEventListener('ended', () => this.onEnded(v));
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

    get useCrossfade() {
      const v = this.videos[0];
      return options.smoothLoop && v && Number.isFinite(v.duration) && v.duration >= MIN_CROSSFADE_DURATION;
    }

    playActive() {
      const v = this.videos[this.active];
      v.loop = !options.smoothLoop;
      const p = v.play();
      if (p && p.catch) p.catch(() => {});
      this.watch();
    }

    // Crossfade loop: hides the cut between the last and first frame of clips that weren't made to loop.
    watch() {
      const v = this.videos[this.active];
      if (!v || this.disposed || paused || !v.requestVideoFrameCallback) return;
      if (this.frameCallback) v.cancelVideoFrameCallback(this.frameCallback);
      this.frameCallback = v.requestVideoFrameCallback(() => {
        this.frameCallback = 0;
        if (this.disposed || paused) return;
        if (!this.useCrossfade) {
          v.loop = true;
          return; // native loop; no per-frame work needed
        }
        v.loop = false;
        const remaining = v.duration - v.currentTime;
        if (!this.prepared && remaining <= PREPARE_SECONDS + FADE_SECONDS) this.prepareNext();
        if (!this.fading && remaining <= FADE_SECONDS) this.startFade();
        this.watch();
      });
    }

    prepareNext() {
      this.prepared = true;
      if (this.videos.length < 2) this.videos.push(this.createVideo(stage, false));
      const next = this.videos[1 - this.active];
      next.pause();
      next.currentTime = 0;
    }

    startFade() {
      this.fading = true;
      const prev = this.videos[this.active];
      const next = this.videos[1 - this.active];
      next.style.transitionDuration = '0s';
      next.style.opacity = '0';
      next.style.zIndex = '2';
      prev.style.zIndex = '1';
      next.muted = options.muted;
      next.volume = options.volume;
      prev.muted = true;
      const p = next.play();
      if (p && p.catch) p.catch(() => {});
      requestAnimationFrame(() => {
        next.style.transitionDuration = FADE_SECONDS + 's';
        next.style.opacity = '1';
      });
      this.fadeTimer = setTimeout(() => this.finishFade(prev), FADE_SECONDS * 1000 + 50);
      this.active = 1 - this.active;
      this.watch();
    }

    finishFade(prev) {
      this.fadeTimer = 0;
      if (this.disposed) return;
      prev.pause();
      prev.style.transitionDuration = '0s';
      prev.style.opacity = '0';
      this.fading = false;
      this.prepared = false;
    }

    onEnded(v) {
      // Fallback if a frame callback was missed: restart instantly rather than freeze on the last frame.
      if (this.disposed || v !== this.videos[this.active] || this.fading) return;
      v.currentTime = 0;
      this.playActive();
    }

    applyOptions(o) {
      for (const v of this.videos) v.style.objectFit = o.fit;
      const a = this.videos[this.active];
      if (a) {
        a.muted = o.muted;
        a.volume = Math.min(1, Math.max(0, o.volume));
        a.loop = !o.smoothLoop;
      }
      this.watch();
    }

    resize() { }

    pause() {
      for (const v of this.videos) v.pause();
      if (this.fadeTimer) {
        clearTimeout(this.fadeTimer);
        this.fadeTimer = 0;
        this.finishFade(this.videos[1 - this.active]);
      }
    }

    resume() {
      this.playActive();
    }

    dispose() {
      this.disposed = true;
      if (this.fadeTimer) clearTimeout(this.fadeTimer);
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
      this.runtime = new window.WavelitShaderRuntime(this.canvas, this.program, getOptions, getPixelSize);
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

  // ---------------------------------------------------------------- commands

  function swap(next) {
    if (current) {
      try { current.dispose(); } catch (e) { /* already torn down */ }
    }
    current = null;
    stage.replaceChildren();
    if (!next) return;
    current = next;
    next.mount(stage);
    if (paused) next.pause();
  }

  function handle(msg) {
    if (!msg || typeof msg.type !== 'string') return;
    try {
      switch (msg.type) {
        case 'options':
          options = Object.assign({}, options, msg.options || {});
          if (current) current.applyOptions(options);
          break;
        case 'resize':
          if (msg.width > 0 && msg.height > 0) pixelSize = { width: msg.width, height: msg.height };
          if (current) current.resize();
          break;
        case 'video':
          if (typeof msg.src !== 'string' || !msg.src.startsWith('https://')) throw new Error('Invalid video address.');
          swap(new VideoRenderer(msg.src, msg.id));
          break;
        case 'shader':
          swap(new ShaderRenderer(msg.program, msg.id));
          break;
        case 'clear':
          swap(null);
          break;
        case 'pause':
          paused = true;
          if (current) current.pause();
          break;
        case 'resume':
          paused = false;
          if (current) current.resume();
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
