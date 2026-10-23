'use strict';
const state = document.getElementById('state');
const go = document.getElementById('go');
const controls = document.getElementById('controls');
const talk = document.getElementById('talk');
const call = document.getElementById('call');
const sound = document.getElementById('sound');
const log = document.getElementById('log');
const video = document.getElementById('scene');
const say = (text) => { state.textContent = text; };
let online = ''; // what the state line says of the connection, shown again once other news is stale

// The conversation, as bubbles: the user's on the right, Delta's on the left. Text is put as text, never as markup.
// Each answer goes under the sentence it answers; Delta's « Une seconde » shows small, and goes when the answer comes.
const threads = new Map();
function bubble(kind, text, note) {
  const p = document.createElement('p');
  p.className = 'bubble ' + kind;
  p.textContent = text;
  if (note) {
    const small = document.createElement('small');
    small.textContent = note;
    p.append(small);
  }
  return p;
}

function add(el, after) {
  const bottom = log.scrollHeight - log.scrollTop - log.clientHeight < 80;
  if (after && after.isConnected) after.after(el); else log.append(el);
  while (log.childElementCount > 100) log.firstElementChild.remove();
  if (bottom) log.scrollTop = log.scrollHeight;
}

function mine(id, text, route) {
  const el = bubble('me', text, route === 'duplicate' ? 'déjà entendu à l’ordinateur' : '');
  add(el);
  if (id && route !== 'duplicate') threads.set(id, { last: el, ack: null });
  while (threads.size > 100) threads.delete(threads.keys().next().value);
}

// where is 'play' (heard here) or 'silent' (heard at the computer, so a button plays it here on demand).
function answer(s, where) {
  const t = s.utterance_id ? threads.get(s.utterance_id) : null;
  if (t && t.ack && s.by !== 'ack') {
    if (t.last === t.ack) t.last = t.ack.previousElementSibling;
    t.ack.remove();
    t.ack = null;
  }
  const kind = 'delta' + (s.by === 'ack' ? ' ack' : '') + (where === 'silent' ? ' away' : '');
  const el = bubble(kind, s.text, where === 'silent' && s.by !== 'ack' ? 'à l’ordinateur' : '');
  if (where === 'silent' && s.by !== 'ack' && s.wav_b64) {
    const replay = document.createElement('button');
    replay.textContent = '▶';
    replay.setAttribute('aria-label', 'Écouter ici');
    replay.onclick = () => { audio(); play(s.wav_b64); };
    el.append(replay);
  }
  add(el, t && t.last);
  if (t) {
    t.last = el;
    if (s.by === 'ack') t.ack = el;
  }
  if (where === 'play') play(s.wav_b64);
}

// A first visit carries the pairing code in the link's #fragment, which the browser sends to no server. It is traded
// once for this device's cookie, then wiped from the address bar and the history.
async function pair() {
  const code = new URLSearchParams(location.hash.slice(1)).get('code');
  if (!code) return;
  history.replaceState(null, '', location.pathname);
  const r = await fetch('/pair', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code }),
  });
  if (!r.ok) throw new Error('code refusé (' + (await r.text()).trim() + ')');
}

async function connect() {
  go.hidden = true;
  const pc = new RTCPeerConnection();
  pc.addTransceiver('video', { direction: 'recvonly' });
  pc.ontrack = (e) => {
    video.srcObject = e.streams[0] || new MediaStream([e.track]);
    let frames = 0;
    const count = () => { frames++; video.requestVideoFrameCallback(count); };
    if (video.requestVideoFrameCallback) video.requestVideoFrameCallback(count);
    setInterval(() => {
      if (!frames) return;
      const now = 'Connecté à Delta · ' + video.videoWidth + '×' + video.videoHeight;
      if (now !== online) say(online = now); // only on a change, so that other news stays shown
      console.log('delta-remote frames', frames, video.videoWidth + 'x' + video.videoHeight);
    }, 1000);
  };
  pc.onconnectionstatechange = () => {
    if (pc.connectionState === 'connected') say(online = 'Connecté à Delta');
    if (pc.connectionState === 'failed') { say('Connexion perdue'); pc.close(); go.hidden = false; }
  };
  await pc.setLocalDescription(await pc.createOffer());
  await new Promise((done) => {
    if (pc.iceGatheringState === 'complete') return done();
    pc.onicegatheringstatechange = () => pc.iceGatheringState === 'complete' && done();
  });
  const r = await fetch('/offer', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(pc.localDescription),
  });
  if (!r.ok) {
    say({ 401: 'Cet appareil n’est pas appairé : scanne le QR dans la fenêtre de Delta.',
          409: 'Un autre appareil est déjà connecté.' }[r.status] || ('Refusé : ' + r.status));
    pc.close(); go.hidden = false; return;
  }
  await pc.setRemoteDescription(await r.json());
  listen();
  controls.hidden = false;
  if (!ctx || ctx.state !== 'running') sound.hidden = false; // a first touch, before talking, unlocks sound and microphone
}

// A touch on the scene makes it big, another makes it small again, so the conversation keeps the screen. On a tablet
// (page.css: both sides at least 600 px), the page also goes full screen, without the browser's bars, and comes back.
const tablet = matchMedia('(min-width: 600px) and (min-height: 600px)');
video.onclick = () => {
  const big = video.classList.toggle('big');
  if (!tablet.matches) return;
  if (big && !document.fullscreenElement) document.documentElement.requestFullscreen?.().catch(() => {});
  if (!big && document.fullscreenElement) document.exitFullscreen().catch(() => {});
};

// Sound. Browsers start audio only after a touch, so the context is made on the first one. A finger that touches the
// screen does not allow it yet, lifting it does (HTML's activation-triggering events): so each lift, or a key, starts
// the sound if it does not run, and hold-to-talk's press alone never leaves it off.
let ctx = null;
let worklet = null;
function audio() {
  if (!ctx) {
    ctx = new AudioContext();
    ctx.onstatechange = () => { if (events) listen(); };
  }
  if (ctx.state === 'suspended') ctx.resume();
  sound.hidden = true;
  return ctx;
}
sound.onclick = () => audio();
for (const type of ['pointerup', 'keydown']) {
  document.addEventListener(type, () => { if (!ctx || ctx.state !== 'running') audio(); }, true);
}

// Delta's sentences play one after another. While one plays, a call does not listen, so it never hears Delta.
let playAt = 0;
async function play(b64) {
  if (!b64) return;
  if (!ctx || ctx.state !== 'running') { sound.hidden = false; return; }
  const bytes = Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
  const buffer = await ctx.decodeAudioData(bytes.buffer);
  const src = ctx.createBufferSource();
  src.buffer = buffer;
  src.connect(ctx.destination);
  playAt = Math.max(playAt, ctx.currentTime);
  src.start(playAt);
  playAt += buffer.duration;
}
const speaking = () => !!ctx && ctx.currentTime < playAt + 0.3;

// Each answer plays where its sentence was said: here for this device's, at the computer for the computer's (the
// brain plays only those). Delta speaking on his own plays here during a call. See Router in talk.js.
const router = new Router();
let held = [];
function route(s, arrivedAt) {
  const where = router.decide(s, arrivedAt, performance.now(), !!calling);
  if (where !== 'hold') return answer(s, where);
  held.push({ s, arrivedAt });
  setTimeout(release, router.holdMs + 10);
}
function release() {
  const waiting = held;
  held = [];
  waiting.forEach((h) => route(h.s, h.arrivedAt));
}

// What Delta says, one stream per page. The brain leaves this device's answers out at the computer only while the page
// says it plays them, which it does only while its sound runs: until a touch unlocks it, the computer says them. So
// the stream opens again when the sound starts or stops; the moment between the two loses at most an event.
let events = null;
function listen() {
  const plays = !!ctx && ctx.state === 'running';
  if (events && events.plays === plays) return;
  if (events) events.close();
  events = new EventSource(plays ? '/said?plays=own' : '/said');
  events.plays = plays;
  events.onmessage = (e) => route(router.attribute(JSON.parse(e.data)), performance.now());
}

// One sentence to Delta, as a 16 kHz WAV, which the brain transcribes; its bubble comes back with the transcript.
async function send(chunks, rate) {
  const token = router.sent();
  try {
    const res = await fetch('/hear', {
      method: 'POST', headers: { 'Content-Type': 'audio/wav' }, body: wav(downsample(chunks, rate, 16000), 16000),
    });
    if (res.status === 202) {
      const h = await res.json();
      router.heard(token, h.route === 'duplicate' ? '' : h.utterance_id);
      if (h.text) mine(h.utterance_id, h.text, h.route);
      if (h.route === 'dropped' || h.route === 'ignored') say('Delta ne l’a pas pris pour lui');
    } else {
      router.failed(token);
      say({ 503: 'Delta n’écoute pas encore', 413: 'Trop long : 30 secondes au plus',
            502: 'La transcription a échoué' }[res.status] || ('Refusé : ' + res.status));
    }
  } catch (err) {
    router.failed(token);
    say('Envoi : ' + err.message);
  }
  release();
}

// The microphone, through the recorder worklet, which hands over each block of samples.
async function microphone(onBlock) {
  const c = audio();
  if (!worklet) worklet = c.audioWorklet.addModule('recorder.js');
  await worklet;
  const stream = await navigator.mediaDevices.getUserMedia({
    audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
  });
  const input = c.createMediaStreamSource(stream);
  const node = new AudioWorkletNode(c, 'recorder');
  const mute = c.createGain();
  mute.gain.value = 0;
  node.port.onmessage = (e) => onBlock(e.data);
  input.connect(node).connect(mute).connect(c.destination);
  return {
    rate: c.sampleRate,
    close() {
      input.disconnect(); node.disconnect(); mute.disconnect();
      stream.getTracks().forEach((t) => t.stop());
    },
  };
}

// Hold to talk: the microphone is open only while the button is held. A release before the microphone opened closes
// it as soon as it opens.
let rec = null;
let holding = false;
async function startTalking() {
  holding = true;
  const chunks = [];
  const mic = await microphone((block) => chunks.push(block));
  if (!holding) { mic.close(); return; }
  rec = { mic, chunks, t0: performance.now() };
  say(online);
  rec.limit = setTimeout(stopTalking, 29000);
  talk.classList.add('on');
  talk.textContent = 'Je t’écoute…';
}

async function stopTalking() {
  holding = false;
  if (!rec) return;
  const r = rec;
  rec = null;
  clearTimeout(r.limit);
  r.mic.close();
  talk.classList.remove('on');
  talk.textContent = 'Maintenir pour parler';
  if (performance.now() - r.t0 < 400) return;
  // Until a touch has unlocked the sound, the microphone records nothing: silence would reach Whisper, which then
  // writes a sentence nobody said, and Claude would answer it.
  const samples = r.chunks.reduce((n, c) => n + c.length, 0);
  if (samples < r.mic.rate * 0.3) { say('Le son est activé : maintiens de nouveau pour parler'); return; }
  await send(r.chunks, r.mic.rate);
}

talk.addEventListener('pointerdown', (e) => {
  e.preventDefault();
  talk.setPointerCapture(e.pointerId);
  startTalking().catch((err) => { say('Micro : ' + err.message); rec = null; holding = false; });
});
talk.addEventListener('pointerup', () => stopTalking());
talk.addEventListener('pointercancel', () => stopTalking());
talk.addEventListener('contextmenu', (e) => e.preventDefault());

// A call: the microphone stays open, each sentence goes to Delta when the user stops speaking, and nothing is heard
// while Delta speaks. The screen stays on until the user hangs up. See Segmenter in talk.js.
let calling = null;
async function startCall() {
  let segmenter = null;
  const mic = await microphone((block) => {
    if (!segmenter) return;
    segmenter.paused = speaking();
    for (const sentence of segmenter.push(block)) send([sentence], mic.rate);
  });
  segmenter = new Segmenter({ rate: mic.rate });
  let lock = null;
  try { lock = await navigator.wakeLock.request('screen'); } catch { lock = null; }
  calling = { mic, lock, timer: setInterval(showCall, 250) };
  talk.hidden = true;
  call.classList.add('on');
  showCall();
}

function showCall() {
  call.textContent = 'Raccrocher · ' + (speaking() ? 'Delta parle' : 'je t’écoute');
}

function endCall() {
  const c = calling;
  calling = null;
  clearInterval(c.timer);
  c.mic.close();
  if (c.lock) c.lock.release().catch(() => {});
  talk.hidden = false;
  call.classList.remove('on');
  call.textContent = 'Appeler';
}

call.onclick = () => {
  if (calling) return endCall();
  call.disabled = true;
  startCall().catch((err) => say('Micro : ' + err.message)).finally(() => { call.disabled = false; });
};

// The average over each output sample's span: a plain low-pass before taking one sample in three from 48 kHz.
function downsample(chunks, from, to) {
  const n = chunks.reduce((a, c) => a + c.length, 0);
  const all = new Float32Array(n);
  let at = 0;
  for (const c of chunks) { all.set(c, at); at += c.length; }
  const ratio = from / to;
  const out = new Float32Array(Math.floor(n / ratio));
  for (let i = 0; i < out.length; i++) {
    const a = Math.floor(i * ratio), b = Math.min(n, Math.floor((i + 1) * ratio));
    let sum = 0;
    for (let j = a; j < b; j++) sum += all[j];
    out[i] = sum / Math.max(1, b - a);
  }
  return out;
}

function wav(samples, rate) {
  const view = new DataView(new ArrayBuffer(44 + samples.length * 2));
  const text = (at, s) => { for (let i = 0; i < s.length; i++) view.setUint8(at + i, s.charCodeAt(i)); };
  text(0, 'RIFF'); view.setUint32(4, 36 + samples.length * 2, true); text(8, 'WAVEfmt ');
  view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
  view.setUint32(24, rate, true); view.setUint32(28, rate * 2, true); view.setUint16(32, 2, true);
  view.setUint16(34, 16, true); text(36, 'data'); view.setUint32(40, samples.length * 2, true);
  for (let i = 0; i < samples.length; i++) {
    view.setInt16(44 + i * 2, Math.max(-1, Math.min(1, samples[i])) * 0x7fff, true);
  }
  return new Blob([view], { type: 'audio/wav' });
}

go.onclick = () => connect().catch((e) => { say('Erreur : ' + e.message); go.hidden = false; });
pair().then(connect).catch((e) => { say('Erreur : ' + e.message); go.hidden = false; });
