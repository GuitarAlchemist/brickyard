import { LitElement, html, css, nothing } from 'lit';
import { validate, freshness, summary, staleAfter } from './contract.js';
import { Bounded } from './bounded.js';
import { SnapshotFeed } from './feed.js';

// The page's own words for what the snapshot withholds; the producer's reason is shown when it gives another.
const WITHHELD = {
  frame_rate: ['Fréquence d’images', 'la scène en direct n’écrit sa fréquence d’images qu’à sa fermeture'],
  gpu_memory: ['Mémoire GPU', 'ni le cerveau ni la scène n’enregistrent la mémoire du GPU'],
};

const CONNECTION = {
  waiting: 'en attente',
  live: 'en direct',
  stale: 'périmé',
  disconnected: 'déconnecté',
  invalid: 'invalide',
};

const STATE = { running: 'en marche', stopped: 'arrêté', unknown: 'inconnu' };
const STATUS = { live: 'lu', missing: 'absent', unavailable: 'indisponible' };

const clock = (iso) => (iso ? new Date(iso).toLocaleTimeString('fr-CA', { hour12: false }) : '—');
const day = (iso) => (iso ? new Date(iso).toLocaleString('fr-CA', { hour12: false }) : '—');
// The time alone when it is today, else the date with it: an old value must not pass for a recent one.
const when = (iso, now) => (iso && new Date(iso).toDateString() === new Date(now).toDateString() ? clock(iso) : day(iso));

// <delta-telemetry src="snapshot.js" every="2000">: Delta's status and two measured values, each with its source and
// its time; what is missing, stale, disconnected or not measured at all is said as such.
export class DeltaTelemetry extends LitElement {
  static properties = {
    src: { type: String },
    every: { type: Number },
    snapshot: { state: true },
    connection: { state: true },
    errors: { state: true },
    now: { state: true },
  };

  static TRANSITIONS = 20;

  constructor() {
    super();
    this.src = 'snapshot.js';
    this.every = 2000;
    this.snapshot = null;
    this.connection = 'waiting';
    this.errors = [];
    this.now = Date.now();
    this.transitions = new Bounded(DeltaTelemetry.TRANSITIONS);
    this.received = 0;
    this.feed = null;
    this.clockTimer = null;
  }

  connectedCallback() {
    super.connectedCallback();
    this.feed = new SnapshotFeed({
      src: this.src,
      everyMs: Math.max(1000, this.every || 2000),
      onSnapshot: (s) => this.take(s),
      onError: () => this.become('disconnected'),
    });
    this.feed.start();
    // Once a second, so that a snapshot that stops coming turns stale without waiting for the next load.
    this.clockTimer = setInterval(() => this.tick(), 1000);
  }

  disconnectedCallback() {
    super.disconnectedCallback();
    this.feed?.stop();
    this.feed = null;
    clearInterval(this.clockTimer);
    this.clockTimer = null;
  }

  // What the element still holds once removed: nothing (test/browser.mjs).
  get held() {
    const feed = this.feed ? this.feed.held : { timers: 0, scripts: 0 };
    return { timers: feed.timers + (this.clockTimer === null ? 0 : 1), scripts: feed.scripts };
  }

  take(s) {
    this.received += 1;
    const errors = validate(s);
    if (errors.length) {
      this.errors = errors;
      this.become('invalid');
      return;
    }
    this.errors = [];
    this.snapshot = s;
    this.now = Date.now();
    this.become(freshness(s, this.now));
  }

  tick() {
    this.now = Date.now();
    if (this.snapshot && (this.connection === 'live' || this.connection === 'stale')) {
      this.become(freshness(this.snapshot, this.now));
    }
  }

  become(connection) {
    if (connection === this.connection) return;
    this.transitions.push({ at: new Date().toISOString(), from: this.connection, to: connection });
    this.connection = connection;
    this.dispatchEvent(new CustomEvent('connection', { detail: connection }));
  }

  static styles = css`
    :host {
      --bg: #f6f4ef; --card: #ffffff; --ink: #1d1b18; --muted: #6b665d; --line: #e2ddd3;
      --live: #1f7a4a; --stale: #9a6400; --down: #a3312a; --off: #6b665d;
      display: block; color: var(--ink); font: 15px/1.45 system-ui, sans-serif;
    }
    @media (prefers-color-scheme: dark) {
      :host { --bg: #171614; --card: #211f1c; --ink: #ece8e0; --muted: #a39d92; --line: #36332e;
        --live: #5cc28b; --stale: #e0a645; --down: #ef7b72; --off: #a39d92; }
    }
    header { display: flex; flex-wrap: wrap; gap: 8px 16px; align-items: baseline; margin-bottom: 12px; }
    h1 { font-size: 20px; margin: 0; }
    .badge { font-weight: 600; padding: 2px 10px; border-radius: 999px; border: 1px solid currentColor; }
    .live { color: var(--live); } .stale { color: var(--stale); } .disconnected, .invalid { color: var(--down); }
    .waiting { color: var(--off); }
    .when { color: var(--muted); font-size: 13px; }
    .grid { display: grid; gap: 12px; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); }
    section { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 12px 14px; min-width: 0; }
    h2 { font-size: 15px; margin: 0 0 8px; }
    dl { display: grid; grid-template-columns: auto 1fr; gap: 2px 12px; margin: 0; }
    dt { color: var(--muted); } dd { margin: 0; overflow-wrap: anywhere; }
    .big { font-size: 26px; font-weight: 600; font-variant-numeric: tabular-nums; }
    .src { color: var(--muted); font-size: 12px; margin-top: 8px; }
    table { width: 100%; border-collapse: collapse; font-variant-numeric: tabular-nums; }
    td, th { text-align: left; padding: 2px 4px; border-bottom: 1px solid var(--line); font-size: 13px; }
    th { color: var(--muted); font-weight: 500; }
    .na { color: var(--off); }
    ul { margin: 0; padding-left: 18px; }
  `;

  render() {
    const s = this.snapshot;
    const age = s ? Math.max(0, Math.round((this.now - Date.parse(s.generated_at)) / 1000)) : null;
    return html`
      <header>
        <h1>Delta — télémétrie</h1>
        <span class="badge ${this.connection}" role="status">${CONNECTION[this.connection]}</span>
        <span class="when">${s
          ? html`instantané de ${clock(s.generated_at)}, il y a ${age} s
              (périmé après ${Math.round(staleAfter(s.every_ms) / 1000)} s)`
          : 'aucun instantané reçu'}</span>
      </header>
      ${this.connection === 'disconnected'
        ? html`<p class="disconnected">snapshot.js ne se charge plus : le producteur ne l’écrit pas, ou le dossier a
            changé. ${s ? 'Les valeurs ci-dessous sont les dernières reçues.' : ''}</p>`
        : nothing}
      ${this.errors.length
        ? html`<p class="invalid">Instantané refusé : ${this.errors.join(' ; ')}</p>`
        : nothing}
      ${s ? this.cards(s) : nothing}
      ${this.history()}
    `;
  }

  cards(s) {
    const b = s.brain;
    const sp = s.speech;
    // The triage's ignored sounds go through recognition too, but are mostly noise: they would make the median
    // a noise figure, so it is taken over the utterances routed somewhere, and the ignored ones are counted apart.
    const kept = sp.recent.filter((u) => u.route !== 'ignored');
    const ignored = sp.recent.length - kept.length;
    const sum = summary(kept);
    const sc = s.scene;
    return html`<div class="grid">
      <section aria-label="Cerveau">
        <h2>Cerveau de Delta</h2>
        ${b.status === 'missing'
          ? html`<p class="na">Ni journal ni fichier de pid.</p>`
          : html`<div class="big">${STATE[b.state]}</div>
            <dl>
              <dt>pid</dt><dd>${b.pid ?? '—'}</dd>
              <dt>version</dt><dd>${b.version || '—'}</dd>
              <dt>depuis</dt><dd>${day(b.since)}</dd>
              <dt>micro</dt><dd>${b.mic ? `${b.mic.on ? 'ouvert' : 'fermé'} (${when(b.mic.at, this.now)})` : '—'}</dd>
            </dl>`}
        <div class="src">source : ${b.source}</div>
      </section>

      <section aria-label="Reconnaissance vocale">
        <h2>Reconnaissance vocale</h2>
        ${sp.status === 'missing'
          ? html`<p class="na">Journal absent.</p>`
          : sp.recent.length
            ? html`${sum
                ? html`<div class="big">${sum.median} ms</div>
                  <dl>
                    <dt>médiane</dt><dd>${sum.count === 1 ? 'd’une seule phrase retenue' : `des ${sum.count} phrases retenues`}
                      par le tri, parmi les ${sp.recent.length} dernières entendues (au plus ${sp.limit})</dd>
                    <dt>min / max</dt><dd>${sum.min} ms / ${sum.max} ms</dd>
                    <dt>ignorées</dt><dd>${ignored}, hors de la médiane</dd>
                  </dl>`
                : html`<p class="na">Les ${sp.recent.length} dernières phrases entendues ont toutes été ignorées par le
                  tri : pas de médiane.</p>`}
              <table>
                <thead><tr><th>entendue</th><th>ms</th><th>route</th></tr></thead>
                <tbody>${sp.recent.slice(-5).reverse().map((u) => html`
                  <tr><td>${when(u.at, this.now)}</td><td>${u.ms}</td><td>${u.route || '—'}</td></tr>`)}</tbody>
              </table>`
            : html`<p class="na">Aucune phrase entendue dans la fin du journal.</p>`}
        <div class="src">source : ${sp.source}</div>
      </section>

      <section aria-label="Scène">
        <h2>Scène native (BRUTAL)</h2>
        ${sc.status === 'live'
          ? html`<div class="big">${sc.working_set_mb} Mo</div>
            <dl>
              <dt>privée</dt><dd>${sc.private_mb} Mo</dd>
              <dt>mesurée</dt><dd>à ${sc.scene_seconds} s de la course de la scène</dd>
              <dt>journal écrit</dt><dd>${day(sc.log_updated_at)} (la mesure n’est pas plus récente)</dd>
            </dl>`
          : html`<p class="na">${sc.status === 'missing' ? 'Journal de la scène absent.' : 'Aucune mesure de mémoire dans la fin du journal.'}</p>`}
        <div class="src">source : ${sc.source}</div>
      </section>

      <section aria-label="Non mesuré">
        <h2>Non mesuré</h2>
        <dl>${Object.entries(WITHHELD).map(([key, [label, why]]) => html`
          <dt>${label}</dt><dd class="na">indisponible : ${s[key].status === 'unavailable' ? why : s[key].why}</dd>`)}
        </dl>
      </section>

      <section aria-label="Sources">
        <h2>Sources</h2>
        <table>
          <thead><tr><th>source</th><th>état</th><th>écrite</th></tr></thead>
          <tbody>${s.sources.map((src) => html`
            <tr><td>${src.name}<br><span class="when">${src.file}</span></td>
              <td class=${src.status === 'live' ? 'live' : 'na'}>${STATUS[src.status]}</td>
              <td>${day(src.updated_at)}</td></tr>`)}</tbody>
        </table>
        <div class="src">producteur : ${s.producer.took_ms} ms, ${Math.round(s.producer.read_bytes / 1024)} Kio lus,
          tas ${s.producer.heap_kb} Kio</div>
      </section>
    </div>`;
  }

  history() {
    const list = this.transitions.items().reverse();
    if (!list.length) return nothing;
    return html`<section aria-label="Historique" style="margin-top:12px">
      <h2>Historique de la connexion (${list.length} au plus ${DeltaTelemetry.TRANSITIONS})</h2>
      <ul>${list.map((t) => html`<li>${clock(t.at)} : ${CONNECTION[t.from]} → ${CONNECTION[t.to]}</li>`)}</ul>
    </section>`;
  }
}

customElements.define('delta-telemetry', DeltaTelemetry);
