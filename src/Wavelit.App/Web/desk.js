'use strict';

// Desktop editor (dashboard → Desktop): the chosen monitor's real wallpaper (a captured frame) with the real
// widgets.js on top. Click to select, drag to move with snap guides (centre, thirds, safe margins, other
// widgets), Ctrl + scroll to resize, arrow keys to nudge. Positions go back to Wavelit as fractions of the
// monitor; Wavelit saves them and the desktop updates live. Text is data (textContent only).
(function () {
  const webview = window.chrome && window.chrome.webview;
  if (!webview) return;
  const post = (msg) => webview.postMessage(msg);

  const board = document.getElementById('board');
  const screen = document.getElementById('screen');
  const wallpaper = document.getElementById('wallpaper');
  const taskbar = document.getElementById('taskbar');
  const guides = document.getElementById('guides');

  const SNAP_PX = 7;          // CSS px
  const MARGIN = 0.04;        // safe margin, fraction of the monitor
  let scene = null;           // the last scene message
  let rendered = null;        // WavelitWidgets handle
  let selected = null;
  let drag = null;
  let fit = { scale: 1, k: 1 };

  const clamp01 = (v) => Math.min(1, Math.max(0, v));
  const widgetById = (id) => scene && scene.widgets.find(w => w.id === id);

  // Fits the monitor (its own pixel size) into the board, keeping its shape.
  function layout() {
    if (!scene) return;
    const m = scene.monitor;
    const pad = 22;
    const W = Math.max(1, board.clientWidth - pad * 2), H = Math.max(1, board.clientHeight - pad * 2);
    const scale = Math.min(W / m.width, H / m.height);
    const w = m.width * scale, h = m.height * scale;
    screen.style.left = ((board.clientWidth - w) / 2) + 'px';
    screen.style.top = ((board.clientHeight - h) / 2) + 'px';
    screen.style.width = w + 'px';
    screen.style.height = h + 'px';
    fit = { scale, k: 1 / scale };
    const reserved = scene.reserved || {};
    const bottom = Math.max(0, Number(reserved.bottom) || 0);
    const t = scene.taskbar || {};
    taskbar.className = '';
    if (bottom > 0 || t.autoHide) {
      taskbar.classList.add('shown');
      if (t.autoHide) taskbar.classList.add('hidden-bar');
      if (t.align === 'left') taskbar.classList.add('left');
      taskbar.style.height = (t.autoHide ? 2 : bottom * scale) + 'px';
    }
    draw();
  }

  function draw() {
    if (rendered) rendered.dispose();
    rendered = null;
    if (!scene) return;
    const m = scene.monitor;
    const area = { x: 0, y: 0, width: m.width, height: m.height };
    const list = scene.widgets.map(w => Object.assign({}, w, { area }));
    rendered = window.WavelitWidgets.render(list, screen, fit.k, { editing: true });
    for (const [id, el] of rendered.elements) {
      el.addEventListener('pointerdown', (e) => startDrag(e, id, el));
      if (id === selected) el.classList.add('selected');
    }
  }

  function select(id, notify) {
    selected = id && widgetById(id) ? id : null;
    if (rendered) for (const [wid, el] of rendered.elements) el.classList.toggle('selected', wid === selected);
    if (notify) post({ type: 'select', id: selected });
  }

  // ---------------------------------------------------------------- dragging with snap guides

  function snapTargets(axis, id) {
    const t = [0.5, 1 / 3, 2 / 3, MARGIN, 1 - MARGIN];
    for (const w of scene.widgets) if (w.id !== id) t.push(axis === 'x' ? w.x : w.y);
    return t;
  }

  function snap(value, axis, id, sizePx) {
    let best = null;
    for (const target of snapTargets(axis, id)) {
      const d = Math.abs(value - target) * sizePx;
      if (d <= SNAP_PX && (!best || d < best.d)) best = { d, target };
    }
    return best ? best.target : null;
  }

  function showGuides(gx, gy) {
    guides.replaceChildren();
    const add = (cls, prop, v) => {
      const g = document.createElement('div');
      g.className = 'guide ' + cls;
      g.style[prop] = (v * 100) + '%';
      guides.appendChild(g);
    };
    if (gx !== null) add('v', 'left', gx);
    if (gy !== null) add('h', 'top', gy);
  }

  function startDrag(e, id, el) {
    if (e.button !== 0) return;
    e.preventDefault();
    e.stopPropagation();
    select(id, true);
    const w = widgetById(id);
    const rect = screen.getBoundingClientRect();
    drag = { id, el, startX: e.clientX, startY: e.clientY, x0: w.x, y0: w.y, x: w.x, y: w.y, rect, moved: false };
    el.classList.add('dragging');
    el.setPointerCapture(e.pointerId);
    el.addEventListener('pointermove', onDragMove);
    el.addEventListener('pointerup', onDragEnd, { once: true });
    el.addEventListener('pointercancel', onDragEnd, { once: true });
  }

  function onDragMove(e) {
    if (!drag) return;
    const { rect } = drag;
    let x = clamp01(drag.x0 + (e.clientX - drag.startX) / rect.width);
    let y = clamp01(drag.y0 + (e.clientY - drag.startY) / rect.height);
    drag.moved = drag.moved || Math.abs(e.clientX - drag.startX) + Math.abs(e.clientY - drag.startY) > 2;
    const sx = e.altKey ? null : snap(x, 'x', drag.id, rect.width);
    const sy = e.altKey ? null : snap(y, 'y', drag.id, rect.height);
    if (sx !== null) x = sx;
    if (sy !== null) y = sy;
    drag.x = x; drag.y = y;
    drag.el.style.left = (x * rect.width) + 'px';
    drag.el.style.top = (y * rect.height) + 'px';
    showGuides(sx, sy);
  }

  function onDragEnd() {
    if (!drag) return;
    const d = drag;
    drag = null;
    d.el.classList.remove('dragging');
    d.el.removeEventListener('pointermove', onDragMove);
    guides.replaceChildren();
    if (!d.moved) return;
    const w = widgetById(d.id);
    if (w) { w.x = d.x; w.y = d.y; }
    post({ type: 'move', id: d.id, x: round(d.x), y: round(d.y) });
  }

  const round = (v) => Math.round(v * 10000) / 10000;

  // ---------------------------------------------------------------- keyboard and wheel

  window.addEventListener('keydown', (e) => {
    if (!selected) return;
    const step = e.shiftKey ? 0.01 : 0.002;
    const d = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] }[e.key];
    if (d) {
      e.preventDefault();
      const w = widgetById(selected);
      w.x = clamp01(w.x + d[0]); w.y = clamp01(w.y + d[1]);
      draw();
      post({ type: 'move', id: w.id, x: round(w.x), y: round(w.y) });
    } else if (e.key === 'Delete') {
      post({ type: 'delete', id: selected });
    } else if (e.key === 'Escape') {
      select(null, true);
    }
  });

  window.addEventListener('wheel', (e) => {
    if (!selected || !e.ctrlKey) return;
    e.preventDefault();
    const w = widgetById(selected);
    const factor = e.deltaY < 0 ? 1.06 : 1 / 1.06;
    w.size = Math.min(40, Math.max(0.6, Math.round(w.size * factor * 100) / 100));
    draw();
    clearTimeout(window.__sizeTimer);
    window.__sizeTimer = setTimeout(() => post({ type: 'resize', id: w.id, size: w.size }), 160);
  }, { passive: false });

  board.addEventListener('pointerdown', (e) => { if (e.target === board || e.target === screen || e.target === wallpaper) select(null, true); });
  window.addEventListener('resize', layout);
  window.addEventListener('contextmenu', (e) => e.preventDefault());

  // ---------------------------------------------------------------- messages from Wavelit

  function validScene(msg) {
    const m = msg.monitor;
    if (!m || !(m.width > 0 && m.height > 0) || !Array.isArray(msg.widgets)) return null;
    return {
      monitor: { width: m.width, height: m.height },
      widgets: msg.widgets.slice(0, 12).filter(w => w && typeof w.id === 'string'),
      reserved: msg.reserved || {},
      taskbar: msg.taskbar || {},
    };
  }

  webview.addEventListener('message', (e) => {
    const msg = e.data;
    if (!msg || typeof msg.type !== 'string') return;
    if (msg.type === 'scene') {
      const next = validScene(msg);
      if (!next) return;
      if (drag) return;   // never yank a widget from under the pointer
      scene = next;
      if (typeof msg.background === 'string' && msg.background.startsWith('data:image/')) wallpaper.src = msg.background;
      else if (msg.background === null) wallpaper.removeAttribute('src');
      screen.classList.remove('hidden');
      if (msg.selected !== undefined) selected = msg.selected;
      layout();
    }
  });

  post({ type: 'ready' });
})();
