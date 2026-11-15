'use strict';

// Desktop widgets (plan §22.2): a clock, date, title, greeting or divider drawn over the wallpaper, under the
// desktop icons, Rainmeter-style. Wavelit sends the widgets for this window with the rectangle of the monitor
// each belongs to (physical pixels within this window), so Extend slices each draw only their own display's
// widgets and nothing is ever cut by a bezel. Text is data: always set with textContent.
//
// Ticking: one timer aligned to the next second (if any widget shows seconds) or minute. It keeps running while
// the wallpaper is frozen (paused), stops with the page when it's suspended, and catches up on resume.
//
// Also used by the Desktop editor (desk.js): render(list, container, k, opts) with opts.editing adds data-ids.
//
// Motion (optional, opts.motion = { follow, pulse } with opts.getView): widgets lean a little towards the cursor
// (reading the spring-smoothed WavelitPointer, so it glides) and swell softly on the beat (WavelitAudio). Both act
// through one transform on each element and stay small: at most about 1% of the monitor's short side, and a 3%
// swell, so nothing ever reads as jumping. The loop runs only while the cursor or music is active.
(function () {
  const LEAN = 0.012;          // of the short side, at full presence and a cursor on the far edge
  const PULSE = 0.03;          // scale swell on a full beat
  const IDLE_CHECK_MS = 250;
  // Must match WidgetFonts.All in Wavelit.Core (a test checks the ids).
  const FONTS = {
    segoe: "'Segoe UI Variable Display', 'Segoe UI Variable', 'Segoe UI', sans-serif",
    bahnschrift: "Bahnschrift, 'Segoe UI', sans-serif",
    sitka: "'Sitka Display', 'Sitka Text', Georgia, serif",
    georgia: "Georgia, 'Sitka Text', serif",
    constantia: 'Constantia, Georgia, serif',
    gabriola: "Gabriola, 'Segoe Script', cursive",
    ink: "'Ink Free', 'Segoe Print', cursive",
    mono: "Consolas, 'Cascadia Mono', monospace",
  };
  const KINDS = ['clock', 'date', 'title', 'greeting', 'divider'];
  const clamp = (v, lo, hi, d) => (Number.isFinite(Number(v)) ? Math.min(hi, Math.max(lo, Number(v))) : d);
  const hex = (c) => (typeof c === 'string' && /^#[0-9a-f]{6}$/i.test(c) ? c : '#FFFFFF');
  const pad = (n) => String(n).padStart(2, '0');

  function clockText(format, d) {
    const h24 = d.getHours(), m = pad(d.getMinutes()), s = pad(d.getSeconds());
    const h12 = h24 % 12 || 12;
    switch (format) {
      case '24s': return `${pad(h24)}:${m}:${s}`;
      case '12': return `${h12}:${m}`;
      case '12s': return `${h12}:${m}:${s}`;
      case 'stack': return `${pad(h24)}\n${m}`;
      default: return `${pad(h24)}:${m}`;
    }
  }

  const fmt = (opts) => new Intl.DateTimeFormat(undefined, opts);
  function dateText(format, d) {
    switch (format) {
      case 'short': return fmt({ day: 'numeric', month: 'short', year: 'numeric' }).format(d);
      case 'compact': return fmt({ weekday: 'short', day: 'numeric' }).format(d);
      case 'iso': return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
      case 'weekday': return fmt({ weekday: 'long' }).format(d);
      default: return fmt({ weekday: 'long', day: 'numeric', month: 'long' }).format(d);
    }
  }

  function greetingText(name, d) {
    const h = d.getHours();
    const part = h < 5 ? 'Good night' : h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : h < 22 ? 'Good evening' : 'Good night';
    const who = typeof name === 'string' ? name.trim() : '';
    return who ? `${part}, ${who}` : part;
  }

  function textOf(w, d) {
    switch (w.kind) {
      case 'clock': return clockText(w.format, d);
      case 'date': return dateText(w.format, d);
      case 'greeting': return greetingText(w.text, d);
      case 'title': return typeof w.text === 'string' ? w.text.slice(0, 80) : '';
      default: return '';
    }
  }

  // Applies one widget's look to its element. k = physical px per CSS px.
  function style(el, w, k) {
    const a = w.area;
    const short = Math.min(a.width, a.height);
    const size = clamp(w.size, 0.6, 40, 6) / 100 * short / k;          // CSS px
    const color = hex(w.color);
    el.style.left = ((a.x + clamp(w.x, 0, 1, 0.5) * a.width) / k) + 'px';
    el.style.top = ((a.y + clamp(w.y, 0, 1, 0.4) * a.height) / k) + 'px';
    const align = w.align === 'left' || w.align === 'right' ? w.align : 'center';
    // The alignment shift is the base transform; motion (lean, pulse) appends to it each frame.
    el.dataset.base = `translate(${align === 'left' ? '0' : align === 'right' ? '-100%' : '-50%'}, -50%)`;
    el.style.transform = el.dataset.base;
    el.style.transformOrigin = align === 'left' ? 'left center' : align === 'right' ? 'right center' : 'center center';
    el.style.textAlign = align;
    el.style.opacity = String(clamp(w.opacity, 0.1, 1, 0.92));
    if (w.kind === 'divider') {
      el.style.width = (clamp(w.size, 0.6, 40, 6) / 100 * a.width / k) + 'px';
      el.style.height = Math.max(1, short / 900 / k) + 'px';
      el.style.background = color;
      el.style.boxShadow = w.effect === 'glow' ? `0 0 ${size * 0.02 + 6}px ${color}` : w.effect === 'shadow' ? '0 1px 6px rgba(0,0,0,0.35)' : 'none';
      return;
    }
    el.style.fontFamily = FONTS[w.font] || FONTS.segoe;
    el.style.fontWeight = String(clamp(w.weight, 100, 900, 300));
    el.style.fontStyle = w.italic ? 'italic' : 'normal';
    el.style.fontSize = size + 'px';
    el.style.lineHeight = w.format === 'stack' ? '0.86' : '1.05';
    el.style.letterSpacing = clamp(w.spacing, -0.1, 1, 0) + 'em';
    el.style.textTransform = w.upper ? 'uppercase' : 'none';
    el.style.color = color;
    // Soft shadow: readable on any wallpaper without looking like a sticker. Glow: neon in the text's own colour.
    el.style.textShadow = w.effect === 'glow'
      ? `0 0 ${size * 0.06}px ${color}, 0 0 ${size * 0.22}px ${color}99, 0 0 ${size * 0.5}px ${color}55`
      : w.effect === 'shadow'
        ? `0 ${Math.max(1, size * 0.02)}px ${Math.max(2, size * 0.08)}px rgba(0,0,0,0.35), 0 0 ${size * 0.4}px rgba(0,0,0,0.18)`
        : 'none';
  }

  /**
   * Renders widgets into container. list: widgets with area = {x, y, width, height} in physical px within the
   * container; k = physical px per CSS px. Returns a handle with update(), dispose() and the elements by id.
   */
  function render(list, container, k, opts) {
    const layer = document.createElement('div');
    layer.className = 'widgets';
    container.appendChild(layer);
    const items = [];
    for (const w of Array.isArray(list) ? list.slice(0, 12) : []) {
      if (!w || !KINDS.includes(w.kind) || !w.area || !(w.area.width > 0 && w.area.height > 0)) continue;
      const el = document.createElement('div');
      el.className = 'widget widget-' + w.kind;
      if (opts && opts.editing && typeof w.id === 'string') el.dataset.id = w.id;
      style(el, w, k > 0 ? k : 1);
      layer.appendChild(el);
      items.push({ w, el });
    }
    let timer = 0;
    const seconds = items.some(i => i.w.kind === 'clock' && /s$/.test(i.w.format || ''));
    const update = () => {
      const d = new Date();
      for (const { w, el } of items) {
        if (w.kind === 'divider') continue;
        const t = textOf(w, d);
        if (el.textContent !== t) el.textContent = t;
      }
    };
    const schedule = () => {
      const now = Date.now();
      const step = seconds ? 1000 : 60000;
      timer = setTimeout(() => { update(); schedule(); }, step - (now % step) + 15);
    };
    update();
    if (items.length && !(opts && opts.static)) schedule();
    // A suspended page skips timers; catch up the moment it's visible again.
    const onVisible = () => { if (document.visibilityState === 'visible') update(); };
    document.addEventListener('visibilitychange', onVisible);

    // ---- motion: lean towards the cursor, pulse with the music
    const motion = opts && opts.motion && (opts.motion.follow || opts.motion.pulse) && typeof opts.getView === 'function' ? opts.motion : null;
    let raf = 0, idle = 0, paused = false, lastPulse = 0;
    const centre = (w) => ({ x: (w.area.x + clamp(w.x, 0, 1, 0.5) * w.area.width) / k, y: (w.area.y + clamp(w.y, 0, 1, 0.4) * w.area.height) / k });
    const rest = () => { for (const { el } of items) if (el.style.transform !== el.dataset.base) el.style.transform = el.dataset.base; };
    const active = () => (motion.follow && window.WavelitPointer && window.WavelitPointer.isActive())
      || (motion.pulse && window.WavelitAudio && window.WavelitAudio.isActive());
    function frame() {
      raf = 0;
      if (paused || !motion) return;
      if (!active()) { rest(); idle = setTimeout(() => { idle = 0; if (!paused) frame(); }, IDLE_CHECK_MS); return; }
      const v = opts.getView();
      let cursor = null;
      if (motion.follow && window.WavelitPointer) {
        // The pointer comes in canvas units (k px per unit, y flipped by sample); map it into this window's CSS px.
        const s = window.WavelitPointer.sample(1, v.canvas.height);
        if (s.pointer[2] > 0.001) cursor = { x: (s.pointer[0] - v.viewport.x) / k, y: (v.canvas.height - s.pointer[1] - v.viewport.y) / k, p: s.pointer[2] };
      }
      let pulse = 0;
      if (motion.pulse && window.WavelitAudio) {
        pulse = window.WavelitAudio.sample().beat[0];   // already eased by presence (audio.js)
      }
      lastPulse = pulse;
      for (const { w, el } of items) {
        const short = Math.min(w.area.width, w.area.height) / k;
        let dx = 0, dy = 0;
        if (cursor) {
          const c = centre(w);
          // Lean a fraction of the way towards the cursor, with a soft falloff so far-off cursors barely count.
          const rx = (cursor.x - c.x) / short, ry = (cursor.y - c.y) / short;
          const dist = Math.hypot(rx, ry);
          const fall = Math.exp(-dist * dist * 0.8);
          dx = Math.max(-1, Math.min(1, rx)) * LEAN * short * cursor.p * (0.35 + 0.65 * fall);
          dy = Math.max(-1, Math.min(1, ry)) * LEAN * short * cursor.p * (0.35 + 0.65 * fall);
        }
        const scale = 1 + PULSE * pulse;
        el.style.transform = `${el.dataset.base} translate(${dx.toFixed(2)}px, ${dy.toFixed(2)}px) scale(${scale.toFixed(4)})`;
      }
      raf = requestAnimationFrame(frame);
    }
    const start = () => { if (motion && !raf && !idle && !paused) raf = requestAnimationFrame(frame); };
    const stop = () => { if (raf) cancelAnimationFrame(raf); raf = 0; clearTimeout(idle); idle = 0; };
    start();

    return {
      layer,
      elements: new Map(items.map(i => [i.w.id, i.el])),
      update,
      /** Freezes motion (the wallpaper is paused); the clock keeps ticking. */
      pause() { paused = true; stop(); rest(); },
      resume() { paused = false; start(); },
      dispose() {
        clearTimeout(timer);
        stop();
        document.removeEventListener('visibilitychange', onVisible);
        layer.remove();
      },
    };
  }

  window.WavelitWidgets = { render, fonts: Object.keys(FONTS), textOf };
})();
