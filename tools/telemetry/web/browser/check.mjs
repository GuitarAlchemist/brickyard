// The panel in a real browser: a throwaway headless Chromium, driven over CDP on a local port, opens the built page
// from a temporary folder as a file, as a user would. This script stands in for the producer there, writing
// snapshot.js itself (labelled as simulated), so that it can make each state happen: live, stale, disconnected,
// reconnected, invalid. It also checks the element's lifecycle, bounds and footprint, and saves desktop and phone
// screenshots. The phone is Chromium's device emulation, not a phone.
//
//   node browser/check.mjs <chrome.exe> <folder for screenshots and the report>
//
// It starts nothing but that Chromium, with its own profile, and closes it at the end.
import { spawn } from 'node:child_process';
import { mkdtempSync, copyFileSync, writeFileSync, rmSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const [chromeExe, evidence] = process.argv.slice(2);
if (!chromeExe || !evidence) {
  console.error('usage: node browser/check.mjs <chrome.exe> <evidence folder>');
  process.exit(2);
}
mkdirSync(evidence, { recursive: true });
const web = join(dirname(fileURLToPath(import.meta.url)), '..');
const site = mkdtempSync(join(tmpdir(), 'delta-telemetry-'));
const profile = mkdtempSync(join(tmpdir(), 'delta-telemetry-chrome-'));
copyFileSync(join(web, 'index.html'), join(site, 'index.html'));
copyFileSync(join(web, 'build', 'panel.js'), join(site, 'panel.js'));
const port = 9341;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const results = [];
function check(name, ok, detail = '') {
  results.push({ name, ok, detail });
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${name}${detail ? ` (${detail})` : ''}`);
}

// A snapshot as tools/telemetry writes one; its values are made up for the test, and say so in their sources.
function snapshot(over = {}) {
  const at = new Date().toISOString().replace(/\.\d+Z$/, 'Z');
  return {
    schema: 'delta-telemetry@1', generated_at: at, every_ms: 1000,
    sources: [{ name: 'brain journal (simulated)', file: 'delta-events.jsonl', status: 'live', updated_at: at }],
    brain: { status: 'live', state: 'running', pid: 4242, version: 'test', since: at, source: 'simulated' },
    speech: { status: 'live', limit: 50, source: 'simulated',
      recent: [{ at, ms: 640, route: 'claude' }, { at, ms: 910, route: 'local' }] },
    scene: { status: 'live', working_set_mb: 3718, private_mb: 4541, scene_seconds: 12, log_updated_at: at, source: 'simulated' },
    frame_rate: { status: 'unavailable', why: 'w' }, gpu_memory: { status: 'unavailable', why: 'w' },
    producer: { read_bytes: 1024, took_ms: 1.5, heap_kb: 900 },
    ...over,
  };
}
const publish = (s) => writeFileSync(join(site, 'snapshot.js'), `deltaTelemetryReceive(${JSON.stringify(s)});\n`);
let producing = true;
const produce = async () => { while (producing) { publish(snapshot()); await sleep(1000); } };
let producer = produce();

const chrome = spawn(chromeExe, ['--headless=new', '--mute-audio', '--no-first-run', '--deny-permission-prompts',
  `--user-data-dir=${profile}`, `--remote-debugging-port=${port}`, 'about:blank'], { stdio: 'ignore' });

let ws;
let seq = 0;
const waiting = new Map();
function send(method, params = {}) {
  const id = ++seq;
  ws.send(JSON.stringify({ id, method, params }));
  return new Promise((resolve, reject) => waiting.set(id, { resolve, reject }));
}
async function value(expression) {
  const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? 'evaluate failed');
  return r.result.value;
}
const badge = () => value(`document.querySelector('delta-telemetry')?.shadowRoot?.querySelector('.badge')?.textContent ?? ''`);
async function until(what, want, ms) {
  const end = Date.now() + ms;
  let got;
  while (Date.now() < end) {
    got = await what();
    if (got === want) return { ok: true, after: ms - (end - Date.now()) };
    await sleep(200);
  }
  return { ok: false, got };
}
async function shot(name, width, height, mobile) {
  await send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: mobile ? 3 : 1, mobile });
  await sleep(400);
  const r = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  writeFileSync(join(evidence, `${name}.png`), Buffer.from(r.data, 'base64'));
  return value('({ scroll: document.documentElement.scrollWidth, inner: window.innerWidth })');
}
const metrics = async () => Object.fromEntries((await send('Performance.getMetrics')).metrics.map((m) => [m.name, m.value]));

try {
  let target;
  for (let i = 0; i < 50 && !target; i++) {
    try { target = await (await fetch(`http://127.0.0.1:${port}/json/new?about:blank`, { method: 'PUT' })).json(); }
    catch { await sleep(200); }
  }
  if (!target) throw new Error('Chromium did not answer on its debugging port');
  ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
  ws.onmessage = (m) => {
    const msg = JSON.parse(m.data);
    const w = waiting.get(msg.id);
    if (w) { waiting.delete(msg.id); msg.error ? w.reject(new Error(msg.error.message)) : w.resolve(msg.result); }
  };
  await send('Page.enable');
  await send('Runtime.enable');
  await send('Performance.enable');
  await send('Page.navigate', { url: pathToFileURL(join(site, 'index.html')).href });

  let r = await until(badge, 'en direct', 8000);
  check('live once a snapshot loads', r.ok, r.ok ? `${r.after} ms` : `badge « ${r.got} »`);

  const desktop = await shot('desktop-1280x800', 1280, 800, false);
  check('desktop: no sideways scroll', desktop.scroll <= desktop.inner, JSON.stringify(desktop));
  const phone = await shot('phone-390x844-emulated', 390, 844, true);
  check('phone (emulated): no sideways scroll', phone.scroll <= phone.inner, JSON.stringify(phone));
  await send('Emulation.clearDeviceMetricsOverride');

  // Footprint over a minute of live updates at the page's 2 s period.
  const before = await metrics();
  const received0 = await value(`document.querySelector('delta-telemetry').received`);
  const t0 = Date.now();
  await sleep(60000);
  const after = await metrics();
  const received1 = await value(`document.querySelector('delta-telemetry').received`);
  const minutes = (Date.now() - t0) / 60000;
  const rate = (received1 - received0) / minutes;
  check('update rate within its bound', rate <= 31 && rate >= 20, `${rate.toFixed(1)} snapshots a minute`);
  const scripts = await value(`document.head.querySelectorAll('script').length`);
  check('scripts do not pile up', scripts <= 1, `${scripts} in <head>`);
  const footprint = {
    heap_mb_before: +(before.JSHeapUsedSize / 1048576).toFixed(2), heap_mb_after: +(after.JSHeapUsedSize / 1048576).toFixed(2),
    nodes_before: before.Nodes, nodes_after: after.Nodes,
    script_ms_per_minute: +(((after.ScriptDuration - before.ScriptDuration) * 1000) / minutes).toFixed(1),
    task_ms_per_minute: +(((after.TaskDuration - before.TaskDuration) * 1000) / minutes).toFixed(1),
    snapshots_per_minute: +rate.toFixed(1),
  };
  check('DOM does not grow', after.Nodes <= before.Nodes + 5, `${before.Nodes} → ${after.Nodes} nodes`);
  console.log('footprint', JSON.stringify(footprint));

  // The producer stops: the last snapshot ages past three periods (5 s at least).
  producing = false;
  await producer;
  r = await until(badge, 'périmé', 9000);
  check('stale when snapshots stop coming', r.ok, r.ok ? `${r.after} ms` : `badge « ${r.got} »`);
  await shot('stale', 1280, 800, false);

  rmSync(join(site, 'snapshot.js'));
  r = await until(badge, 'déconnecté', 8000);
  check('disconnected when snapshot.js is gone', r.ok, r.ok ? `${r.after} ms` : `badge « ${r.got} »`);
  await shot('disconnected', 1280, 800, false);

  publish(snapshot());
  r = await until(badge, 'en direct', 14000);
  check('live again once it comes back', r.ok, r.ok ? `${r.after} ms` : `badge « ${r.got} »`);

  publish({ ...snapshot(), schema: 'delta-telemetry@0' });
  r = await until(badge, 'invalide', 8000);
  check('a snapshot of another schema is refused', r.ok, r.ok ? `${r.after} ms` : `badge « ${r.got} »`);
  await shot('invalid', 1280, 800, false);
  publish(snapshot());
  await until(badge, 'en direct', 8000);

  const history = await value(`(() => { const e = document.querySelector('delta-telemetry');
    for (let i = 0; i < 30; i++) e.become(i % 2 ? 'live' : 'stale');
    return e.transitions.size; })()`);
  check('the connection history is bounded', history === 20, `${history} kept of 30+`);

  producing = true; // fresh snapshots again, so that the element added back can be live
  producer = produce();
  const lifecycle = await value(`(async () => {
    const e = document.querySelector('delta-telemetry');
    const parent = e.parentNode;
    e.remove();
    await new Promise((r) => setTimeout(r, 300));
    const gone = { held: e.held, global: typeof window.deltaTelemetryReceive, scripts: document.head.querySelectorAll('script').length };
    const before = e.received;
    await new Promise((r) => setTimeout(r, 3000));
    const quiet = e.received === before;
    parent.appendChild(e);
    await new Promise((r) => setTimeout(r, 3000));
    return { gone, quiet, back: e.received > before, badge: e.shadowRoot.querySelector('.badge').textContent };
  })()`);
  check('removed: no timer, script or global left', lifecycle.gone.held.timers === 0 && lifecycle.gone.held.scripts === 0
    && lifecycle.gone.global === 'undefined' && lifecycle.gone.scripts === 0, JSON.stringify(lifecycle.gone));
  check('removed: nothing arrives', lifecycle.quiet);
  check('added again: it loads again', lifecycle.back && lifecycle.badge === 'en direct', JSON.stringify(lifecycle));

  writeFileSync(join(evidence, 'report.json'), JSON.stringify({ results, footprint, chromium: chromeExe }, null, 2));
} catch (err) {
  check('the run itself', false, err.message);
} finally {
  producing = false;
  await producer;
  try { ws?.close(); } catch { /* closing */ }
  const exited = new Promise((r) => chrome.once('exit', r));
  chrome.kill();
  await Promise.race([exited, sleep(5000)]);
  // Chromium's helper processes can hold its profile a moment longer: the temporary folders are left to the system's
  // temporary folder rather than fail a run that passed.
  for (const dir of [site, profile]) {
    try { rmSync(dir, { recursive: true, force: true, maxRetries: 10, retryDelay: 300 }); }
    catch (err) { console.log(`left ${dir}: ${err.code}`); }
  }
}
const failed = results.filter((r) => !r.ok).length;
console.log(failed ? `BROWSER CHECK FAIL ${failed} of ${results.length}` : `BROWSER CHECK ok ${results.length}`);
process.exit(failed ? 1 : 0);
