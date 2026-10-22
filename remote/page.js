'use strict';
const state = document.getElementById('state');
const go = document.getElementById('go');
const talk = document.getElementById('talk');
const sound = document.getElementById('sound');
const log = document.getElementById('log');
const say = (text) => { state.textContent = text; };

// One line of the conversation; text is put as text, never as markup.
function line(who, text) {
  const p = document.createElement('p');
  const b = document.createElement('b');
  b.textContent = who + ' : ';
  p.append(b, text);
  log.prepend(p);
  while (log.childElementCount > 20) log.lastElementChild.remove();
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
    const video = document.getElementById('scene');
    video.srcObject = e.streams[0] || new MediaStream([e.track]);
    let frames = 0;
    const count = () => { frames++; video.requestVideoFrameCallback(count); };
    if (video.requestVideoFrameCallback) video.requestVideoFrameCallback(count);
    setInterval(() => {
      if (!frames) return;
      say('Connecté à Delta · ' + video.videoWidth + '×' + video.videoHeight);
      console.log('delta-remote frames', frames, video.videoWidth + 'x' + video.videoHeight);
    }, 1000);
  };
  pc.onconnectionstatechange = () => {
    if (pc.connectionState === 'connected') say('Connecté à Delta');
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
  talk.hidden = false;
}

// Sound. Browsers start audio only after a touch, so the context is made on the first one.
let ctx = null;
let worklet = null;
function audio() {
  if (!ctx) ctx = new AudioContext();
  if (ctx.state === 'suspended') ctx.resume();
  sound.hidden = true;
  return ctx;
}
sound.onclick = () => audio();

// Delta's sentences, as it starts saying them at the computer: shown, and played one after another.
let playAt = 0;
function listen() {
  const events = new EventSource('/said');
  events.onmessage = async (e) => {
    const s = JSON.parse(e.data);
    line('Delta', s.text);
    if (!ctx || ctx.state !== 'running') { sound.hidden = false; return; }
    const bytes = Uint8Array.from(atob(s.wav_b64), (c) => c.charCodeAt(0));
    const buffer = await ctx.decodeAudioData(bytes.buffer);
    const src = ctx.createBufferSource();
    src.buffer = buffer;
    src.connect(ctx.destination);
    playAt = Math.max(playAt, ctx.currentTime);
    src.start(playAt);
    playAt += buffer.duration;
  };
}

// Hold to talk: the microphone is open only while the button is held, and what it heard goes to Delta as a 16 kHz
// WAV, which the brain transcribes.
let rec = null;
async function startTalking() {
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
  const chunks = [];
  node.port.onmessage = (e) => chunks.push(e.data);
  input.connect(node).connect(mute).connect(c.destination);
  rec = { stream, input, node, mute, chunks, rate: c.sampleRate, t0: performance.now() };
  rec.limit = setTimeout(stopTalking, 29000);
  talk.classList.add('on');
  talk.textContent = 'Je t’écoute…';
}

async function stopTalking() {
  if (!rec) return;
  const r = rec;
  rec = null;
  clearTimeout(r.limit);
  r.input.disconnect(); r.node.disconnect(); r.mute.disconnect();
  r.stream.getTracks().forEach((t) => t.stop());
  talk.classList.remove('on');
  talk.textContent = 'Maintenir pour parler';
  if (performance.now() - r.t0 < 400) return;
  const res = await fetch('/hear', {
    method: 'POST', headers: { 'Content-Type': 'audio/wav' }, body: wav(downsample(r.chunks, r.rate, 16000), 16000),
  });
  if (res.status === 202) {
    const h = await res.json();
    if (h.text) line('Toi', h.text);
    if (h.route === 'dropped' || h.route === 'ignored') say('Delta ne l’a pas pris pour lui');
    return;
  }
  say({ 503: 'Delta n’écoute pas encore', 413: 'Trop long : 30 secondes au plus', 502: 'La transcription a échoué' }[res.status]
      || ('Refusé : ' + res.status));
}

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

talk.addEventListener('pointerdown', (e) => {
  e.preventDefault();
  talk.setPointerCapture(e.pointerId);
  startTalking().catch((err) => { say('Micro : ' + err.message); rec = null; });
});
talk.addEventListener('pointerup', () => stopTalking());
talk.addEventListener('pointercancel', () => stopTalking());
talk.addEventListener('contextmenu', (e) => e.preventDefault());

go.onclick = () => connect().catch((e) => { say('Erreur : ' + e.message); go.hidden = false; });
pair().then(connect).catch((e) => { say('Erreur : ' + e.message); go.hidden = false; });
