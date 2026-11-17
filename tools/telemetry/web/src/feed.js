// SnapshotFeed loads snapshot.js again and again from the page's own folder. A page opened from a file may not fetch
// another file, but it may run one as a script; the script calls window.deltaTelemetryReceive with the snapshot.
//
// Each load adds one <script> and removes it once it has run or failed, so the document does not grow. A load that
// fails (the file is gone, the producer has stopped writing it) is retried after twice the wait, up to maxMs; the first
// load that succeeds brings the wait back to everyMs.
export class SnapshotFeed {
  constructor({ src, everyMs = 2000, maxMs = 10000, timeoutMs = 4000, onSnapshot, onError, win = window }) {
    this.src = src;
    this.everyMs = everyMs;
    this.maxMs = maxMs;
    this.timeoutMs = timeoutMs;
    this.onSnapshot = onSnapshot;
    this.onError = onError;
    this.win = win;
    this.wait = everyMs;
    this.timer = null;
    this.script = null;
    this.loads = 0;
    this.running = false;
  }

  start() {
    if (this.running) return;
    this.running = true;
    this.win.deltaTelemetryReceive = (s) => this.received(s);
    this.load();
  }

  stop() {
    this.running = false;
    this.win.clearTimeout(this.timer);
    this.timer = null;
    this.drop();
    if (this.win.deltaTelemetryReceive) delete this.win.deltaTelemetryReceive;
  }

  // Timers and scripts the feed holds now: both 0 once stopped (the panel's lifecycle test reads it).
  get held() {
    return { timers: this.timer === null ? 0 : 1, scripts: this.script ? 1 : 0 };
  }

  load() {
    if (!this.running) return;
    const doc = this.win.document;
    const script = doc.createElement('script');
    this.script = script;
    this.got = false;
    this.loads += 1;
    const guard = this.win.setTimeout(() => this.done(script, new Error('no snapshot within the time allowed')), this.timeoutMs);
    script.onload = () => {
      this.win.clearTimeout(guard);
      this.done(script, this.got ? null : new Error('snapshot.js ran without a snapshot'));
    };
    script.onerror = () => {
      this.win.clearTimeout(guard);
      this.done(script, new Error('snapshot.js could not be loaded'));
    };
    script.src = `${this.src}?n=${this.loads}`;
    doc.head.appendChild(script);
  }

  received(s) {
    this.got = true;
    if (this.running) this.onSnapshot(s);
  }

  done(script, err) {
    if (script !== this.script) return; // a late answer from a script already given up
    this.drop();
    if (!this.running) return;
    if (err) {
      this.wait = Math.min(this.wait * 2, this.maxMs);
      this.onError(err);
    } else {
      this.wait = this.everyMs;
    }
    this.timer = this.win.setTimeout(() => { this.timer = null; this.load(); }, this.wait);
  }

  drop() {
    if (this.script) {
      this.script.onload = this.script.onerror = null;
      this.script.remove();
      this.script = null;
    }
  }
}
