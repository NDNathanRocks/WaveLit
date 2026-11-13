// Renders the real widgets.js with motion on in headless Chrome and checks that widgets lean towards a cursor and
// swell on a beat, and that a paused handle rests them. node tools/headless/check-widgets.mjs
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const chrome = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe'].find(existsSync);
if (!chrome) { console.error('Chrome not found'); process.exit(2); }
const port = 9300 + Math.floor(Math.random() * 500);
const profile = mkdtempSync(path.join(tmpdir(), 'wavelit-headless-'));
const proc = spawn(chrome, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--allow-file-access-from-files', '--no-first-run', 'about:blank'], { stdio: 'ignore' });
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
async function targets() {
  for (let i = 0; i < 50; i++) { try { return await (await fetch(`http://127.0.0.1:${port}/json`)).json(); } catch { await sleep(200); } }
  throw new Error('Chrome did not start');
}
let failures = 0;
const expect = (name, ok, detail) => { if (!ok) failures++; console.log(`${ok ? 'ok  ' : 'FAIL'} ${name}${detail ? ' ' + detail : ''}`); };
try {
  const page = (await targets()).find(t => t.type === 'page');
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
  let id = 0; const pending = new Map();
  ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
  const send = (method, params) => new Promise(res => { const n = ++id; pending.set(n, res); ws.send(JSON.stringify({ id: n, method, params })); });
  const evaluate = async (expression) => {
    const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (r.result.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails));
    return r.result.result.value;
  };
  await send('Page.enable'); await send('Runtime.enable');
  await send('Page.navigate', { url: pathToFileURL(path.join(here, 'harness.html')).href });
  for (let i = 0; i < 50 && !(await evaluate('!!window.WavelitInteractive')); i++) await sleep(100);
  await evaluate(`new Promise(r => { const s = document.createElement('script'); s.src = '../../src/Wavelit.App/Web/widgets.js'; s.onload = r; document.body.appendChild(s); })`);
  const setup = `
    window.__view = { canvas: { width: 1920, height: 1080 }, viewport: { x: 0, y: 0, width: 1920, height: 1080 }, focus: { x: 960, y: 540, size: 1080 } };
    const list = [{ id: 'clock', kind: 'clock', x: 0.5, y: 0.4, size: 12, format: '24', area: { x: 0, y: 0, width: 1920, height: 1080 } }];
    window.__h = window.WavelitWidgets.render(list, document.body, 1, { motion: { follow: true, pulse: true }, getView: () => window.__view });
    window.__el = window.__h.elements.get('clock');
    true`;
  await evaluate(setup);
  const transform = () => evaluate('window.__el.style.transform');
  await sleep(300);
  const rest = await transform();
  expect('rests with no cursor or music', !/translate\(\d/.test(rest.replace(/translate\(-50%, -50%\)/, '')), rest);

  // Cursor far to the right of the clock: it should lean right (positive x).
  await evaluate(`window.WavelitPointer.update({ x: 1700, y: 430, over: true, down: false }); true`);
  await sleep(700);   // presence eases in over 0.5 s
  const leaned = await transform();
  const m = /translate\((-?[\d.]+)px, (-?[\d.]+)px\) scale\(([\d.]+)\)/.exec(leaned);
  expect('leans towards the cursor', m && Number(m[1]) > 2 && Number(m[1]) < 16, leaned);

  // Music for a second (presence eases in over 1.2 s), then a beat: scale should rise above 1 right after it.
  await evaluate(`window.__frame = (beat, n) => { const b = new Array(64).fill(0.6); window.WavelitAudio.update({ b, s: [0.9, 0.5, 0.4, 0.7, 0.5], beat, bpm: 120, n, silent: false }); }; true`);
  for (let i = 0; i < 26; i++) { await evaluate(`window.__frame(false, 0); true`); await sleep(50); }
  await evaluate(`window.__frame(true, 1); true`);
  await sleep(120);
  const pulsed = await transform();
  const p = /scale\(([\d.]+)\)/.exec(pulsed);
  expect('swells on a beat', p && Number(p[1]) > 1.005, pulsed);

  await evaluate('window.__h.pause(); true');
  await sleep(100);
  const paused = await transform();
  expect('pause rests the transform', paused === 'translate(-50%, -50%)', paused);
  await evaluate('window.__h.dispose(); true');
  expect('dispose removes the layer', (await evaluate('document.querySelectorAll(".widgets").length')) === 0);
  ws.close();
} finally {
  proc.kill(); await sleep(300);
  try { rmSync(profile, { recursive: true, force: true }); } catch { /* ignore */ }
}
console.log(failures ? `${failures} failure(s)` : 'widget motion ok');
process.exit(failures ? 1 : 0);
