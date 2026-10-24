'use strict';
// The page's logic that needs no browser, kept apart so that `node --test` checks it: talk.test.js.

// Cuts a call's microphone into sentences by loudness, in 20 ms frames. A sentence starts after onsetMs of sound
// louder than the background, keeps prerollMs before that, and ends after hangMs of quiet. Shorter than minMs, it was
// a click; at maxMs it is cut, so that the brain, which takes 30 s at most, never refuses it. While paused, Delta is
// speaking: the sound is dropped, with any half-heard sentence.
class Segmenter {
  constructor({ rate, onsetMs = 120, prerollMs = 300, hangMs = 800, minMs = 400, maxMs = 29000, floor = 0.02,
    ratio = 3 } = {}) {
    const frames = (ms) => Math.max(1, Math.round(ms / 20));
    this.frameLen = Math.round(rate * 0.02);
    this.onset = frames(onsetMs);
    this.preroll = frames(prerollMs);
    this.hang = frames(hangMs);
    this.min = frames(minMs);
    this.max = frames(maxMs);
    this.floor = floor;
    this.ratio = ratio;
    this.noise = 0;
    this.paused = false;
    this.frame = new Float32Array(this.frameLen);
    this.filled = 0;
    this.reset();
  }

  reset() {
    this.ring = [];
    this.loud = 0;
    this.speech = null;
  }

  // Takes one block of samples and returns the sentences it completed, each a Float32Array.
  push(block) {
    if (this.paused) {
      this.reset();
      this.filled = 0;
      return [];
    }
    const out = [];
    for (let i = 0; i < block.length; i++) {
      this.frame[this.filled++] = block[i];
      if (this.filled === this.frameLen) {
        const done = this.step(this.frame.slice());
        if (done) out.push(done);
        this.filled = 0;
      }
    }
    return out;
  }

  step(frame) {
    let sum = 0;
    for (const x of frame) sum += x * x;
    const rms = Math.sqrt(sum / frame.length);
    const threshold = Math.max(this.floor, this.noise * this.ratio);
    const loud = rms > threshold;
    if (!this.speech) {
      if (!loud) this.noise = this.noise * 0.95 + rms * 0.05;
      this.ring.push(frame);
      if (this.ring.length > this.preroll) this.ring.shift();
      this.loud = loud ? this.loud + 1 : 0;
      if (this.loud >= this.onset) {
        this.speech = { frames: this.ring, voiced: this.loud, quiet: 0 };
        this.ring = [];
      }
      return null;
    }
    const s = this.speech;
    s.frames.push(frame);
    if (loud) {
      s.voiced += s.quiet + 1;
      s.quiet = 0;
    } else {
      s.quiet++;
    }
    if (s.frames.length >= this.max) return this.end(true);
    if (s.quiet >= this.hang) return this.end(s.voiced >= this.min);
    return null;
  }

  end(keep) {
    const frames = this.speech.frames;
    this.reset();
    if (!keep) return null;
    const out = new Float32Array(frames.length * this.frameLen);
    frames.forEach((f, i) => out.set(f, i * this.frameLen));
    return out;
  }
}

// Chooses where each of Delta's sentences plays: here when it answers a sentence this device said, or when Delta
// speaks on his own during a call; else at the computer, so that it is shown here without sound. An answer can reach
// /said before its sentence's /hear reply reaches the page: while a /hear is in flight, an unknown answer waits, at
// most holdMs.
class Router {
  constructor({ holdMs = 2000, keep = 200 } = {}) {
    this.holdMs = holdMs;
    this.keep = keep;
    this.mine = new Set();
    this.inflight = new Set();
    this.says = new Map();
    this.next = 0;
  }

  // Every sentence of one answer shares its say_id, and the brain may name the sentence answered and the speaker on
  // the first one only (v0.5.0): the later ones get them from it.
  attribute(event) {
    const known = this.says.get(event.say_id);
    if (!event.utterance_id && known) return { ...event, utterance_id: known.utterance_id, by: event.by || known.by };
    if (event.say_id && event.utterance_id) {
      this.says.set(event.say_id, { utterance_id: event.utterance_id, by: event.by });
      if (this.says.size > this.keep) this.says.delete(this.says.keys().next().value);
    }
    return event;
  }

  sent() {
    const token = ++this.next;
    this.inflight.add(token);
    return token;
  }

  heard(token, utteranceId) {
    this.inflight.delete(token);
    if (!utteranceId) return;
    this.mine.add(utteranceId);
    if (this.mine.size > this.keep) this.mine.delete(this.mine.values().next().value);
  }

  failed(token) {
    this.inflight.delete(token);
  }

  // 'play', 'silent', or 'hold' to ask again once a /hear answers or holdMs have passed since arrivedAt.
  decide(event, arrivedAt, now, inCall) {
    const id = event.utterance_id || '';
    if (id === '') return inCall ? 'play' : 'silent';
    if (this.mine.has(id)) return 'play';
    if (this.inflight.size > 0 && now - arrivedAt < this.holdMs) return 'hold';
    return 'silent';
  }
}

if (typeof module === 'object' && module.exports) module.exports = { Segmenter, Router };
