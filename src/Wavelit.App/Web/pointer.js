'use strict';

// Cursor state for interactive wallpapers. Wavelit sends the cursor in wallpaper-canvas units (top-left
// origin), already mapped across displays, so every Extend slice agrees on where it is. Wavelit only
// sends it while the cursor is over the bare desktop and this wallpaper is visible; nothing else ever
// reaches the page, and the position never leaves this PC.
//
// Smoothness: positions arrive ~60 times a second with timer jitter, out of step with the display. So
// renderers never see the raw cursor: a critically damped spring follows it (no overshoot, ~120 ms to
// settle), and the trail is sampled from that smooth path at a fixed rate. Every effect that depends on
// the cursor therefore glides, whatever the message timing or frame rate.
//
// Shaders read it through uniforms (see shader-runtime.js):
//   iPointer  xy = cursor (fragCoord space), z = presence 0..1 (eased in and out), w = energy (recent speed)
//   iClick    xy = last click, z = seconds since it, w = 1 if there was one
//   iTrail[i] xy = a recent cursor position, z = its age in seconds, w = its strength (0 = unused)
//   iMouse    Shadertoy semantics, so pasted shaders that use the mouse respond to it
(function () {
  const TRAIL = 16;
  const TRAIL_STEP_MS = 33;      // trail sampled from the smooth path ~30 times a second
  const TRAIL_LIFE = 1.4;        // seconds a trail point keeps acting
  const PRESENCE_TIME = 0.5;     // seconds to fade the cursor's influence in or out
  const SPRING = 34;             // rad/s; critically damped, settles in ~4/SPRING ≈ 120 ms
  const SUBSTEP = 1 / 240;       // spring integration step (stable for any frame rate)
  const FULL_SPEED = 1400;       // canvas units/s that count as a full-strength sweep

  const state = {
    target: null,            // { x, y } latest raw position
    x: 0, y: 0,              // smoothed position
    vx: 0, vy: 0,            // smoothed velocity (units/s)
    over: false,
    down: false,
    presence: 0,
    energy: 0,
    eased: 0,                // Date.now() of the last step
    lastTrail: 0,
    trail: [],               // { x, y, t, s }
    click: null,             // { x, y, t }
    press: null,             // { x, y } of the current press (Shadertoy iMouse.zw)
    lastActivity: -1e12,
  };

  function update(msg) {
    if (!msg || !Number.isFinite(msg.x) || !Number.isFinite(msg.y)) return;
    const now = Date.now();
    step(now);
    const over = !!msg.over;
    if (over) {
      if (!state.over || !state.target) {
        // Arriving on the desktop: start where the cursor is, never glide in from a stale spot.
        state.x = msg.x; state.y = msg.y; state.vx = 0; state.vy = 0;
      }
      state.target = { x: msg.x, y: msg.y };
      const down = !!msg.down;
      if (down && !state.down) {
        state.click = { x: msg.x, y: msg.y, t: now };
        state.press = { x: msg.x, y: msg.y };
      }
      state.down = down;
      state.lastActivity = now;
    } else {
      state.down = false;
    }
    state.over = over;
  }

  // Advances the spring, presence, energy and trail to `now`. A pure function of elapsed time, so several
  // renderers can call it in one frame.
  function step(now) {
    const elapsed = state.eased ? Math.min(Math.max((now - state.eased) / 1000, 0), 0.25) : 0;
    state.eased = now;
    if (elapsed <= 0) return;

    if (state.target) {
      for (let t = elapsed; t > 0; t -= SUBSTEP) {
        const h = Math.min(SUBSTEP, t);
        const ax = SPRING * SPRING * (state.target.x - state.x) - 2 * SPRING * state.vx;
        const ay = SPRING * SPRING * (state.target.y - state.y) - 2 * SPRING * state.vy;
        state.vx += ax * h; state.vy += ay * h;
        state.x += state.vx * h; state.y += state.vy * h;
      }
    }

    const target = state.over ? 1 : 0;
    const s = elapsed / PRESENCE_TIME;
    state.presence = target > state.presence ? Math.min(1, state.presence + s) : Math.max(0, state.presence - s);

    const speed = Math.hypot(state.vx, state.vy);
    const strength = state.over ? Math.min(1, speed / FULL_SPEED) : 0;
    state.energy += (strength - state.energy) * Math.min(1, elapsed * 3);
    if (now - state.lastTrail >= TRAIL_STEP_MS) {
      state.lastTrail = now;
      if (strength > 0.02) {
        state.trail.push({ x: state.x, y: state.y, t: now, s: strength });
        if (state.trail.length > TRAIL) state.trail.shift();
      }
    }
    while (state.trail.length && (now - state.trail[0].t) / 1000 > TRAIL_LIFE) state.trail.shift();
  }

  const smootherstep = (v) => v * v * v * (v * (v * 6 - 15) + 10);

  // Uniform values for one renderer. k = render pixels per canvas unit, canvasHeight in canvas units.
  function sample(k, canvasHeight) {
    const now = Date.now();
    step(now);
    const fx = (x) => x * k;
    const fy = (y) => (canvasHeight - y) * k;
    const trail = new Float32Array(TRAIL * 4);
    for (let i = 0; i < state.trail.length; i++) {
      const p = state.trail[state.trail.length - 1 - i];   // newest first
      trail[i * 4] = fx(p.x);
      trail[i * 4 + 1] = fy(p.y);
      trail[i * 4 + 2] = (now - p.t) / 1000;
      trail[i * 4 + 3] = p.s;
    }
    const click = state.click
      ? [fx(state.click.x), fy(state.click.y), (now - state.click.t) / 1000, 1]
      : [0, 0, 1e4, 0];
    const press = state.press || { x: state.x, y: state.y };
    const mouse = state.down
      ? [fx(state.x), fy(state.y), fx(press.x), fy(press.y)]
      : [fx(state.x), fy(state.y), -fx(press.x), -fy(press.y)];
    return { pointer: [fx(state.x), fy(state.y), smootherstep(state.presence), state.energy], click, trail, mouse };
  }

  window.WavelitPointer = {
    update,
    sample,
    /**
     * True while the cursor moved recently or its influence is still settling (renderers raise their
     * frame rate). A cursor resting on the desktop sends nothing, so the layer drops back to its normal rate.
     */
    isActive: () => Date.now() - state.lastActivity < 2500 || (state.presence > 0 && state.presence < 1)
      || state.trail.length > 0 || state.energy > 0.01,
    /** Forget everything (wallpaper changed). */
    reset() {
      state.trail = [];
      state.click = null;
      state.press = null;
      state.energy = 0;
      state.vx = 0;
      state.vy = 0;
    },
  };
})();
