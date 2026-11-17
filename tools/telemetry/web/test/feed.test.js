import test from 'node:test';
import assert from 'node:assert/strict';
import { SnapshotFeed } from '../src/feed.js';

// A window with a clock moved by hand and a head whose scripts "load" as the test says: `files` maps a script's path
// to the snapshot it carries, or to null when the file is not there.
function fakeWindow(files) {
  let now = 0;
  let nextId = 1;
  const timers = new Map();
  const head = { scripts: new Set() };
  const win = {
    files,
    setTimeout(fn, ms) { const id = nextId++; timers.set(id, { at: now + ms, fn }); return id; },
    clearTimeout(id) { timers.delete(id); },
    document: {
      head: {
        appendChild(script) {
          head.scripts.add(script);
          queueMicrotask(() => {
            if (!head.scripts.has(script)) return;
            const path = script.src.split('?')[0];
            const snap = win.files[path];
            if (snap === null || snap === undefined) script.onerror?.();
            else { win.deltaTelemetryReceive?.(snap); script.onload?.(); }
          });
        },
      },
      createElement() {
        const s = { remove() { head.scripts.delete(s); } };
        return s;
      },
    },
    async advance(ms) {
      const end = now + ms;
      for (;;) {
        await new Promise((r) => setImmediate(r));
        const due = [...timers.entries()].filter(([, t]) => t.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
        if (!due) break;
        timers.delete(due[0]);
        now = due[1].at;
        due[1].fn();
      }
      now = end;
      await new Promise((r) => setImmediate(r));
    },
    get scripts() { return head.scripts.size; },
    get pending() { return timers.size; },
  };
  return win;
}

function feedOn(win, log) {
  return new SnapshotFeed({
    src: 'snapshot.js', everyMs: 1000, maxMs: 8000, timeoutMs: 500, win,
    onSnapshot: (s) => log.push(`snap ${s.n}`),
    onError: () => log.push('error'),
  });
}

test('it loads a snapshot at its period, one script at a time', async () => {
  const win = fakeWindow({ 'snapshot.js': { n: 1 } });
  const log = [];
  const feed = feedOn(win, log);
  feed.start();
  await win.advance(3500);
  assert.deepEqual(log, ['snap 1', 'snap 1', 'snap 1', 'snap 1']);
  assert.equal(win.scripts, 0, 'each script is removed once it has run');
  feed.stop();
});

test('a missing file backs off, and the first load that works brings the period back', async () => {
  const win = fakeWindow({ 'snapshot.js': null });
  const log = [];
  const feed = feedOn(win, log);
  feed.start();
  await win.advance(100);
  assert.equal(feed.wait, 2000);
  await win.advance(2000); // second failure
  await win.advance(4000); // third
  await win.advance(8000); // fourth: the wait stays at its maximum
  assert.equal(feed.wait, 8000);
  assert.deepEqual(log, ['error', 'error', 'error', 'error']);
  win.files['snapshot.js'] = { n: 2 };
  await win.advance(8000);
  assert.equal(log.at(-1), 'snap 2');
  assert.equal(feed.wait, 1000);
  feed.stop();
});

test('a script that runs without a snapshot is an error', async () => {
  const win = fakeWindow({ 'snapshot.js': { n: 1 } });
  win.document.head.appendChild = (script) => queueMicrotask(() => script.onload?.());
  const log = [];
  const feed = feedOn(win, log);
  feed.start();
  await win.advance(10);
  assert.deepEqual(log, ['error']);
  feed.stop();
});

test('stopping leaves no timer, no script and no global behind', async () => {
  const win = fakeWindow({ 'snapshot.js': { n: 1 } });
  const log = [];
  const feed = feedOn(win, log);
  feed.start();
  await win.advance(1500);
  feed.stop();
  assert.deepEqual(feed.held, { timers: 0, scripts: 0 });
  assert.equal(win.scripts, 0);
  assert.equal(win.deltaTelemetryReceive, undefined);
  const before = log.length;
  await win.advance(10000);
  assert.equal(log.length, before, 'nothing arrives after stop');
});
