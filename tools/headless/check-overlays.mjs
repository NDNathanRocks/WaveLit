// Compiles and renders every built-in overlay and (optionally) every Overlay Designer variant in headless Chrome
// with the real Web/*.js, over CDP. Works even when Smart App Control blocks the app's own DLLs.
//   node tools/headless/check-overlays.mjs [designs.json]
import { spawn } from 'node:child_process';
import { readFileSync, existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const chrome = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe']
  .find(existsSync);
if (!chrome) { console.error('Chrome not found'); process.exit(2); }
const designs = process.argv[2] ? JSON.parse(readFileSync(process.argv[2], 'utf8')) : [];
const port = 9300 + Math.floor(Math.random() * 500);
const profile = mkdtempSync(path.join(tmpdir(), 'wavelit-headless-'));
const proc = spawn(chrome, [
  '--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
  '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--allow-file-access-from-files', '--disable-web-security',
  '--no-first-run', '--disable-gpu-watchdog', 'about:blank',
], { stdio: 'ignore' });

const sleep = (ms) => new Promise(r => setTimeout(r, ms));
async function targets() {
  for (let i = 0; i < 50; i++) {
    try { return await (await fetch(`http://127.0.0.1:${port}/json`)).json(); } catch { await sleep(200); }
  }
  throw new Error('Chrome did not start');
}

let failures = 0;
try {
  const page = (await targets()).find(t => t.type === 'page');
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
  let id = 0;
  const pending = new Map();
  ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
  const send = (method, params) => new Promise(res => { const n = ++id; pending.set(n, res); ws.send(JSON.stringify({ id: n, method, params })); });
  const evaluate = async (expression) => {
    const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (r.result.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails));
    return r.result.result.value;
  };
  await send('Page.enable');
  await send('Runtime.enable');
  await send('Page.navigate', { url: pathToFileURL(path.join(here, 'harness.html')).href });
  for (let i = 0; i < 50 && !(await evaluate('typeof window.checkOverlay === "function" && !!window.WavelitInteractive')); i++) await sleep(100);

  const styles = await evaluate('window.WavelitInteractive.styles');
  const check = async (name, codeExpr, expectAudio) => {
    const r = await evaluate(`window.checkOverlay(${codeExpr}, 96)`);
    const blank = r.ok && r.alpha < 3;
    const audioWrong = r.ok && expectAudio !== undefined && r.usesAudio !== expectAudio;
    const bad = !r.ok || blank || audioWrong;
    if (bad) failures++;
    console.log(`${bad ? 'FAIL' : 'ok  '} ${name}${r.ok ? ` alpha=${r.alpha}${audioWrong ? ' (usesAudio mismatch)' : ''}` : ' ' + r.error}`);
  };
  for (const s of styles) await check(`built-in ${s}`, `window.WavelitInteractive.code(${JSON.stringify(s)}).slice(window.WavelitInteractive.common.length)`);
  for (const d of designs) await check(`design ${d.name}`, JSON.stringify(d.code));
  ws.close();
} finally {
  proc.kill();
  await sleep(300);
  try { rmSync(profile, { recursive: true, force: true }); } catch { /* Chrome may still hold a file */ }
}
console.log(failures ? `${failures} failure(s)` : 'all overlays compiled and drew something');
process.exit(failures ? 1 : 0);
