'use strict';

// Music state for audio wallpapers and overlays. Wavelit analyses what the PC plays (WASAPI loopback, never
// the microphone) and sends ~60 frames a second while a visible wallpaper uses audio: 64 log bands
// (30 Hz–16 kHz, auto-gained), bass/mid/treble/level, the spectral centroid, beats and tempo. Nothing is
// recorded or stored; frames go to Wavelit's own host page only.
//
// Smoothness: frames arrive with timer jitter, so renderers never see them raw. Each band follows its
// target with a fast attack (~30 ms) and a slow release (~250 ms), peak caps hold then fall with gravity,
// and a beat is an envelope that swells and decays. After a moment of silence everything eases to a calm
// idle (presence → 0) instead of freezing or reacting to a single notification sound.
//
// Reduce flashing (on by default): beat swells are capped, slower to rise, and never retrigger more than
// three times a second (the WCAG flash threshold).
//
// Shaders read it through uniforms (see shader-runtime.js):
//   iAudio          x = bass, y = mid, z = treble, w = level (all 0..1)
//   iBeat           x = pulse 0..1, y = seconds since the beat, z = BPM (0 = unknown), w = beat count
//   iSpectrum[16]   64 bands packed as vec4s: band i = iSpectrum[i / 4][i % 4]
//   iPeaks[16]      falling peak caps, same packing
//   iCentroid       0 = bass-heavy … 1 = bright
//   iAudioPresence  0 = silence (idle) … 1 = music playing
(function () {
  const BANDS = 64;
  const ATTACK = 0.03, RELEASE = 0.25;     // seconds
  const PEAK_HOLD = 0.35, GRAVITY = 1.6;   // peak cap: hold, then fall (units/s²)
  const PRESENCE_TIME = 1.2;
  const STALE_MS = 400;                    // no frame for this long counts as silence

  const target = { bands: new Float32Array(BANDS), s: [0, 0, 0, 0], centroid: 0.5, silent: true, bpm: 0, count: 0 };
  const state = {
    bands: new Float32Array(BANDS), peaks: new Float32Array(BANDS), peakAge: new Float32Array(BANDS),
    peakVel: new Float32Array(BANDS), s: [0, 0, 0, 0], centroid: 0.5, presence: 0,
    pulse: 0, pulseTarget: 0, lastBeat: -1e12, lastTrigger: -1e12, stepped: 0, received: -1e12,
  };
  let config = { sensitivity: 0.5, reduceFlashing: true };
  let demoTimer = 0;

  const clamp01 = (v) => Math.min(1, Math.max(0, v));
  const gain = () => 0.6 + clamp01(Number(config.sensitivity)) * 0.8;   // 0.6 .. 1.4, 1 at the default

  function configure(c) {
    if (!c || typeof c !== 'object') return;
    config = {
      sensitivity: Number.isFinite(Number(c.sensitivity)) ? clamp01(Number(c.sensitivity)) : 0.5,
      reduceFlashing: c.reduceFlashing !== false,
    };
  }

  function update(msg) {
    if (!msg || !Array.isArray(msg.b)) return;
    const now = performance.now();
    step(now);
    const g = gain();
    for (let i = 0; i < BANDS; i++) target.bands[i] = clamp01((Number(msg.b[i]) || 0) * g);
    const s = Array.isArray(msg.s) ? msg.s : [];
    for (let i = 0; i < 4; i++) target.s[i] = clamp01((Number(s[i]) || 0) * g);
    target.centroid = clamp01(Number(s[4]) || 0.5);
    target.silent = !!msg.silent;
    target.bpm = Number(msg.bpm) > 0 ? Number(msg.bpm) : 0;
    target.count = Number(msg.n) | 0;
    state.received = now;
    if (msg.beat && !target.silent) beat(now);
  }

  function beat(now) {
    const calm = config.reduceFlashing;
    if (now - state.lastTrigger < (calm ? 340 : 200)) return;   // ≤ 3 swells a second when calm
    state.lastTrigger = now;
    state.lastBeat = now;
    // A stronger kick makes a bigger swell; capped when reducing flashing.
    state.pulseTarget = Math.min(calm ? 0.65 : 1, 0.45 + target.s[0] * 0.7);
  }

  // Advances smoothing to `now`. Idempotent for a given time, so several renderers can call it per frame.
  function step(now) {
    const dt = state.stepped ? Math.min(Math.max((now - state.stepped) / 1000, 0), 0.25) : 0;
    state.stepped = now;
    if (dt <= 0) return;
    const stale = now - state.received > STALE_MS;
    const silent = target.silent || stale;
    const up = 1 - Math.exp(-dt / ATTACK), down = 1 - Math.exp(-dt / RELEASE);
    for (let i = 0; i < BANDS; i++) {
      const t = silent ? 0 : target.bands[i];
      const v = state.bands[i];
      state.bands[i] = v + (t - v) * (t > v ? up : down);
      // Peak caps: pushed up by the band, held, then fall with gravity.
      if (state.bands[i] >= state.peaks[i]) {
        state.peaks[i] = state.bands[i]; state.peakAge[i] = 0; state.peakVel[i] = 0;
      } else if ((state.peakAge[i] += dt) > PEAK_HOLD) {
        state.peakVel[i] += GRAVITY * dt;
        state.peaks[i] = Math.max(state.bands[i], state.peaks[i] - state.peakVel[i] * dt);
      }
    }
    for (let i = 0; i < 4; i++) {
      const t = silent ? 0 : target.s[i];
      state.s[i] += (t - state.s[i]) * (t > state.s[i] ? up : down);
    }
    state.centroid += ((silent ? 0.5 : target.centroid) - state.centroid) * (1 - Math.exp(-dt / 0.6));
    state.presence = clamp01(state.presence + (silent ? -dt : dt) / PRESENCE_TIME);
    // Beat envelope: swells over ~40 ms (90 ms when calm), then decays over ~400 ms.
    const rise = config.reduceFlashing ? 0.09 : 0.04;
    if (state.pulseTarget > state.pulse) {
      state.pulse = Math.min(state.pulseTarget, state.pulse + dt / rise);
      if (state.pulse >= state.pulseTarget) state.pulseTarget = 0;
    } else {
      state.pulseTarget = 0;
      state.pulse *= Math.exp(-dt / 0.16);
    }
  }

  const spectrum = new Float32Array(BANDS), peaks = new Float32Array(BANDS);

  /** Uniform values for this moment (see the header). */
  function sample() {
    const now = performance.now();
    step(now);
    spectrum.set(state.bands);
    peaks.set(state.peaks);
    return {
      audio: [state.s[0], state.s[1], state.s[2], state.s[3]],
      beat: [state.pulse * state.presence, Math.min((now - state.lastBeat) / 1000, 1e4), target.bpm, target.count],
      spectrum,
      peaks,
      centroid: state.centroid,
      presence: state.presence,
    };
  }

  /** Music is playing (or fading out): audio renderers keep full rate while this is true. */
  const isActive = () => state.presence > 0 || performance.now() - state.received < STALE_MS;

  // A built-in demo signal (no audio file): a 120 BPM kick, a bass line and drifting chords, for the Studio
  // preview and the shader tests. Generated as analysed frames, so it exercises the same path as music.
  function demo(on) {
    if (demoTimer) clearInterval(demoTimer);
    demoTimer = 0;
    if (!on) return;
    const start = performance.now();
    let lastBeat = -1;
    demoTimer = setInterval(() => {
      const t = (performance.now() - start) / 1000;
      const beatIndex = Math.floor(t / 0.5);
      const ph = t - beatIndex * 0.5;
      const kick = Math.exp(-ph * 9);
      const chord = [0, 3, 7, 10][Math.floor(t / 4) % 4];
      const b = new Array(BANDS);
      for (let i = 0; i < BANDS; i++) {
        const x = i / (BANDS - 1);
        const bass = kick * Math.exp(-Math.pow((x - 0.08) / 0.07, 2));
        const tone = 0.55 * Math.exp(-Math.pow((x - 0.32 - chord * 0.012) / 0.05, 2)) * (0.75 + 0.25 * Math.sin(t * 2.1));
        const harm = 0.35 * Math.exp(-Math.pow((x - 0.5 - chord * 0.01) / 0.08, 2));
        const hats = (ph > 0.22 && ph < 0.3 ? 0.45 : 0.12) * Math.exp(-Math.pow((x - 0.85) / 0.12, 2));
        b[i] = clamp01(0.08 + bass + tone + harm + hats + 0.05 * Math.sin(i * 1.7 + t * 3));
      }
      const mean = (lo, hi) => b.slice(lo, hi).reduce((a, v) => a + v, 0) / (hi - lo);
      const beatNow = beatIndex !== lastBeat;
      lastBeat = beatIndex;
      update({ b, s: [mean(0, 16), mean(16, 43), mean(43, 64), mean(0, 64), 0.42 + 0.1 * Math.sin(t * 0.3)],
        beat: beatNow, bpm: 120, n: beatIndex, silent: false });
    }, 16);
  }

  window.WavelitAudio = { update, configure, sample, isActive, demo, bands: BANDS };
})();
