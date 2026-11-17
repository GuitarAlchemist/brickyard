import test from 'node:test';
import assert from 'node:assert/strict';
import { validate, freshness, staleAfter, summary, SCHEMA } from '../src/contract.js';

const at = '2026-11-17T09:00:00Z';
const good = () => ({
  schema: SCHEMA,
  generated_at: at,
  every_ms: 5000,
  sources: [{ name: 'brain journal', file: 'delta-events.jsonl', status: 'live', updated_at: at }],
  brain: { status: 'live', state: 'running', pid: 42, source: 'brain pid file and journal' },
  speech: { status: 'live', recent: [{ at, ms: 812, route: 'claude' }], limit: 50, source: 'brain journal' },
  scene: { status: 'live', working_set_mb: 3718, private_mb: 4541, scene_seconds: 0, source: 'scene log' },
  frame_rate: { status: 'unavailable', why: 'w' },
  gpu_memory: { status: 'unavailable', why: 'w' },
  producer: { read_bytes: 1, took_ms: 1, heap_kb: 1 },
});

test('a snapshot as the producer writes it is valid', () => {
  assert.deepEqual(validate(good()), []);
});

test('what the page relies on is checked', () => {
  const cases = {
    'another schema': (s) => { s.schema = 'delta-telemetry@2'; },
    'no time': (s) => { delete s.generated_at; },
    'a made-up status': (s) => { s.scene.status = 'ok'; },
    'a made-up state': (s) => { s.brain.state = 'busy'; },
    'over its limit': (s) => { s.speech.limit = 0; },
    'an utterance without its ms': (s) => { s.speech.recent = [{ at }]; },
    'no frame rate part': (s) => { delete s.frame_rate; },
    'a negative period': (s) => { s.every_ms = -1; },
  };
  for (const [name, change] of Object.entries(cases)) {
    const s = good();
    change(s);
    assert.notDeepEqual(validate(s), [], name);
  }
  assert.deepEqual(validate(null), ['not an object']);
});

test('a snapshot turns stale after three periods, or a minute when written once', () => {
  assert.equal(staleAfter(5000), 15000);
  assert.equal(staleAfter(1000), 5000);
  assert.equal(staleAfter(0), 60000);
  const s = good();
  const t = Date.parse(at);
  assert.equal(freshness(s, t + 14000), 'live');
  assert.equal(freshness(s, t + 16000), 'stale');
  s.every_ms = 0;
  assert.equal(freshness(s, t + 59000), 'live');
  assert.equal(freshness(s, t + 61000), 'stale');
});

test('the summary is computed from the utterances only', () => {
  assert.equal(summary([]), null);
  const u = (ms) => ({ at, ms });
  assert.deepEqual(summary([u(900), u(100), u(500)]), { count: 3, min: 100, median: 500, max: 900 });
  assert.deepEqual(summary([u(100), u(200)]), { count: 2, min: 100, median: 150, max: 200 });
});
