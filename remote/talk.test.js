'use strict';
// The page's logic that needs no browser: cutting a call's sound into sentences, and choosing where an answer plays.
// Run with `node --test` in this folder.
const test = require('node:test');
const assert = require('node:assert/strict');
const { Segmenter, Router } = require('./talk.js');

const rate = 16000;

// Sound in 128-sample blocks, as the recorder worklet hands it over: a tone of the given amplitude, or silence.
function feed(seg, seconds, amplitude) {
  const out = [];
  const n = Math.round(seconds * rate);
  for (let at = 0; at < n; at += 128) {
    const block = new Float32Array(Math.min(128, n - at));
    for (let i = 0; i < block.length; i++) block[i] = amplitude * Math.sin(2 * Math.PI * 220 * (at + i) / rate);
    out.push(...seg.push(block));
  }
  return out;
}

test('silence makes no sentence', () => {
  assert.deepEqual(feed(new Segmenter({ rate }), 3, 0), []);
});

test('a sentence between two silences comes out once, with a little sound around it', () => {
  const seg = new Segmenter({ rate });
  const got = [...feed(seg, 1, 0), ...feed(seg, 1.2, 0.3), ...feed(seg, 1.5, 0)];
  assert.equal(got.length, 1);
  const seconds = got[0].length / rate;
  assert.ok(seconds >= 1.2 && seconds <= 1.2 + 0.3 + 0.8 + 0.1, `sentence of ${seconds} s`);
});

test('a click shorter than a word is dropped', () => {
  const seg = new Segmenter({ rate });
  assert.deepEqual([...feed(seg, 1, 0), ...feed(seg, 0.2, 0.3), ...feed(seg, 2, 0)], []);
});

test('a long speech is cut before the brain refuses it', () => {
  const seg = new Segmenter({ rate });
  const got = [...feed(seg, 35, 0.3), ...feed(seg, 2, 0)];
  assert.equal(got.length, 2);
  assert.ok(got[0].length <= 29 * rate && got[0].length >= 28 * rate, `first part of ${got[0].length / rate} s`);
});

test('nothing is heard while Delta speaks, and the next sentence is', () => {
  const seg = new Segmenter({ rate });
  seg.paused = true;
  assert.deepEqual([...feed(seg, 1, 0), ...feed(seg, 1.2, 0.3)], []);
  seg.paused = false;
  assert.equal([...feed(seg, 1, 0), ...feed(seg, 1.2, 0.3), ...feed(seg, 1.5, 0)].length, 1);
});

test('a pause in the middle of the sound drops the half heard', () => {
  const seg = new Segmenter({ rate });
  feed(seg, 1, 0);
  feed(seg, 0.8, 0.3);
  seg.paused = true;
  feed(seg, 1, 0.3);
  seg.paused = false;
  assert.deepEqual(feed(seg, 1.5, 0), []);
});

test('an answer to this phone plays here', () => {
  const r = new Router();
  const t = r.sent();
  r.heard(t, 'u7');
  assert.equal(r.decide({ utterance_id: 'u7' }, 0, 0, false), 'play');
});

test('an answer to the computer stays silent here', () => {
  const r = new Router();
  assert.equal(r.decide({ utterance_id: 'u3' }, 0, 0, false), 'silent');
});

test('an answer that overtakes its own sentence waits for it, at most two seconds', () => {
  const r = new Router();
  const t = r.sent();
  assert.equal(r.decide({ utterance_id: 'u8' }, 0, 100, false), 'hold');
  r.heard(t, 'u8');
  assert.equal(r.decide({ utterance_id: 'u8' }, 0, 200, false), 'play');

  const r2 = new Router();
  r2.sent();
  assert.equal(r2.decide({ utterance_id: 'u9' }, 0, 2001, false), 'silent');
});

test('a sentence that failed stops the wait', () => {
  const r = new Router();
  const t = r.sent();
  r.failed(t);
  assert.equal(r.decide({ utterance_id: 'u4' }, 0, 10, false), 'silent');
});

test('the later sentences of one answer belong to it, even when the brain names only the first', () => {
  const r = new Router();
  const t = r.sent();
  r.heard(t, 'u5');
  const first = r.attribute({ say_id: 's1', utterance_id: 'u5', by: 'claude' });
  const next = r.attribute({ say_id: 's1', utterance_id: '', by: '' });
  assert.deepEqual([first.utterance_id, first.by], ['u5', 'claude']);
  assert.deepEqual([next.utterance_id, next.by], ['u5', 'claude']);
  assert.equal(r.decide(next, 0, 0, false), 'play');
  const alone = r.attribute({ say_id: 's2', utterance_id: '', by: '' });
  assert.equal(alone.utterance_id, '');
});

test('Delta speaking on his own plays here only during a call', () => {
  const r = new Router();
  assert.equal(r.decide({ utterance_id: '' }, 0, 0, true), 'play');
  assert.equal(r.decide({ utterance_id: '' }, 0, 0, false), 'silent');
});
