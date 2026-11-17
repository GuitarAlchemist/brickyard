// The snapshot's contract as the page checks it: the same fields as tools/telemetry/contract.go, whose schema it names.
// Pure functions, tested with `node --test` (test/contract.test.js).

export const SCHEMA = 'delta-telemetry@1';
const STATUSES = new Set(['live', 'missing', 'unavailable']);
const STATES = new Set(['running', 'stopped', 'unknown']);

const isTime = (v) => typeof v === 'string' && !Number.isNaN(Date.parse(v));
const isCount = (v) => Number.isInteger(v) && v >= 0;

// validate returns the list of what is wrong with s, empty when the page can show it.
export function validate(s) {
  const errors = [];
  const need = (ok, what) => { if (!ok) errors.push(what); };
  if (s === null || typeof s !== 'object') return ['not an object'];
  need(s.schema === SCHEMA, `schema ${JSON.stringify(s.schema)}, want ${SCHEMA}`);
  need(isTime(s.generated_at), 'generated_at is not a time');
  need(isCount(s.every_ms), 'every_ms is not a count');
  need(Array.isArray(s.sources), 'sources is not a list');
  for (const part of ['brain', 'speech', 'scene', 'frame_rate', 'gpu_memory']) {
    need(s[part] && STATUSES.has(s[part].status), `${part}.status is not one of live, missing, unavailable`);
  }
  if (errors.length) return errors;
  need(STATES.has(s.brain.state), `brain.state ${JSON.stringify(s.brain.state)}`);
  need(Array.isArray(s.speech.recent) && isCount(s.speech.limit), 'speech.recent or speech.limit');
  if (Array.isArray(s.speech.recent)) {
    need(s.speech.recent.length <= s.speech.limit, 'speech.recent is over its limit');
    need(s.speech.recent.every((u) => isTime(u.at) && isCount(u.ms)), 'an utterance without its time or its ms');
  }
  if (s.scene.status === 'live') {
    need(isCount(s.scene.working_set_mb) && isCount(s.scene.private_mb) && isCount(s.scene.scene_seconds),
      'a live scene without its memory and its seconds');
  }
  for (const src of s.sources) {
    need(typeof src.name === 'string' && STATUSES.has(src.status), 'a source without its name or status');
  }
  return errors;
}

// staleAfter is how old a snapshot may get before the page calls it stale: three of the producer's periods, and at
// least 5 s; a snapshot written once is stale after a minute.
export function staleAfter(everyMs) {
  return everyMs > 0 ? Math.max(3 * everyMs, 5000) : 60000;
}

// freshness says whether s, valid, is still live at nowMs.
export function freshness(s, nowMs) {
  const age = nowMs - Date.parse(s.generated_at);
  return age > staleAfter(s.every_ms) ? 'stale' : 'live';
}

// summary gives the count, minimum, median and maximum of the utterances' recognition times; null for none.
export function summary(recent) {
  if (!recent.length) return null;
  const ms = recent.map((u) => u.ms).sort((a, b) => a - b);
  const mid = ms.length >> 1;
  const median = ms.length % 2 ? ms[mid] : Math.round((ms[mid - 1] + ms[mid]) / 2);
  return { count: ms.length, min: ms[0], median, max: ms[ms.length - 1] };
}
