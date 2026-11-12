'use strict';
// A guest's page: Delta's window, live, and nothing else. The invitation comes in the link's #fragment, which the
// browser sends to no server; it is traded once for a cookie that lasts two hours, and the fragment leaves the address
// bar. The relay's addresses come from the gateway, which asks Cloudflare for them. Once a touch turns the sound on,
// the guest hears Delta's voice, and the microphone in the room when the user lets guests hear it.

// The page's words, in French and in English: guests are not all French speakers. guest.test.js checks that both
// languages say everything the page says.
const words = {
  fr: {
    connecting: 'Connexion à Delta…',
    watching: 'Tu regardes Delta en direct. Rien de ce que tu fais ne lui parvient.',
    lost: 'Connexion perdue',
    again: 'Se reconnecter',
    soundOn: 'Activer le son',
    used: 'Cette invitation a expiré : demande un nouveau lien.',
    noInvite: 'Pas d’invitation valable : demande un lien à la personne qui t’invite.',
    ended: 'Cette invitation est terminée.',
    revoked: 'La personne qui t’invite a mis fin à l’invitation.',
    window: 'La fenêtre de Delta est fermée : l’image revient dès qu’elle se rouvre.',
    away: 'Delta ne répond pas pour l’instant : nouvel essai dans quelques secondes.',
    full: 'Deux invités regardent déjà : réessaie plus tard.',
    relay: 'Le relais vidéo ne répond pas : réessaie dans un moment.',
    refused: 'Refusé : ',
  },
  en: {
    connecting: 'Connecting to Delta…',
    watching: 'You are watching Delta live. Nothing you do reaches it.',
    lost: 'Connection lost',
    again: 'Reconnect',
    soundOn: 'Turn sound on',
    used: 'This invitation has expired: ask for a new link.',
    noInvite: 'No valid invitation: ask the person who invited you for a link.',
    ended: 'This invitation has ended.',
    revoked: 'The person who invited you has ended the invitation.',
    window: 'Delta’s window is closed: the picture comes back once it opens again.',
    away: 'Delta is not answering right now: trying again in a few seconds.',
    full: 'Two guests are watching already: try again later.',
    relay: 'The video relay is not answering: try again in a moment.',
    refused: 'Refused: ',
  },
};

// language is the first of the browser's languages the page speaks, or English.
function language(preferred) {
  for (const tag of preferred || []) {
    const lang = String(tag).slice(0, 2).toLowerCase();
    if (words[lang]) return lang;
  }
  return 'en';
}

// reason is the key of what the page says when it cannot watch, from the headers of the answer: none, or none of the
// gateway's, while the gateway is away (stopped, or this device offline), else the gateway's X-Delta-Refusal. ended is
// what to say when the guest's time or invitation is up, which depends on the request.
function reason(headers, ended) {
  if (!headers || headers.get('X-Delta-Gateway') !== '1') return 'away';
  const why = { ended, revoked: 'revoked', window: 'window', full: 'full', relay: 'relay' };
  return why[headers.get('X-Delta-Refusal')] || 'refused';
}

// again holds the reasons that pass by themselves, for which the page tries again every retryEvery; for the others,
// the guest decides.
const again = new Set(['away', 'window']);
const retryEvery = 10000;

function guest() {
  const lang = language(navigator.languages || [navigator.language]);
  const w = words[lang];
  document.documentElement.lang = lang;
  const state = document.getElementById('state');
  const go = document.getElementById('go');
  const video = document.getElementById('scene');
  const sound = document.getElementById('sound');
  const say = (text) => { state.textContent = text; };
  // refused is the error for an answer r that does not let the guest watch, or for no answer at all.
  const refused = (r, ended) => {
    const key = reason(r && r.headers, ended);
    const e = new Error(key === 'refused' ? w.refused + r.status : w[key]);
    e.again = again.has(key);
    return e;
  };
  // ask fetches from the gateway, and throws when the answer is not the gateway's.
  const ask = async (url, init) => {
    const r = await fetch(url, init).catch(() => null);
    if (!r || r.headers.get('X-Delta-Gateway') !== '1') throw refused(r);
    return r;
  };
  let retry = null;
  go.textContent = w.again;
  sound.textContent = w.soundOn;
  let ctx = null; // Delta's voice plays in it, once a touch has turned the sound on: browsers ask for one
  let playAt = 0; // when the sentence on its way ends, so that the next one waits for it
  let said = null;

  // Delta's voice: the sound of each sentence it says aloud, as the computer says it.
  function listen() {
    if (said) return;
    said = new EventSource('/said');
    said.onmessage = async (e) => {
      if (!ctx) return;
      try {
        const bytes = Uint8Array.from(atob(JSON.parse(e.data).wav_b64), (c) => c.charCodeAt(0));
        const clip = await ctx.decodeAudioData(bytes.buffer);
        const source = ctx.createBufferSource();
        source.buffer = clip;
        source.connect(ctx.destination);
        const at = Math.max(ctx.currentTime, playAt);
        source.start(at);
        playAt = at + clip.duration;
      } catch (err) { /* a sentence that does not decode is skipped */ }
    };
  }

  sound.onclick = () => {
    ctx = ctx || new AudioContext();
    ctx.resume();
    video.muted = false; // the room's microphone comes with the picture
    video.play().catch(() => {});
    sound.hidden = true;
  };

  async function join() {
    const m = location.hash.match(/invite=([0-9a-f]{32})/);
    if (!m) return;
    const r = await ask('/join', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ invite: m[1] }),
    });
    // The fragment leaves the address bar once the gateway has had it: while the gateway is away, the next try needs it.
    history.replaceState(null, '', location.pathname);
    if (r.headers.get('X-Delta-Refusal') === 'revoked') throw refused(r);
    // A guest who opened the link before holds a cookie still: the relay's addresses tell whether it is valid.
    return r.ok;
  }

  async function watch() {
    clearTimeout(retry);
    go.hidden = true;
    say(w.connecting);
    try {
      const joined = await join();
      const ice = await ask('/ice');
      if (!ice.ok) throw refused(ice, joined === false ? 'used' : 'noInvite');
      const { iceServers } = await ice.json();
      const pc = new RTCPeerConnection({ iceServers });
      pc.addTransceiver('video', { direction: 'recvonly' });
      pc.addTransceiver('audio', { direction: 'recvonly' });
      const stream = new MediaStream();
      pc.ontrack = (e) => { stream.addTrack(e.track); video.srcObject = stream; };
      pc.onconnectionstatechange = () => {
        if (pc.connectionState === 'connected') {
          say(w.watching);
          listen();
          sound.hidden = ctx !== null;
        }
        if (pc.connectionState === 'failed') {
          say(w.lost);
          pc.close();
          go.hidden = false;
          retry = setTimeout(watch, retryEvery); // the gateway may be coming back; the next try says if not
        }
      };
      await pc.setLocalDescription(await pc.createOffer());
      await new Promise((done) => {
        if (pc.iceGatheringState === 'complete') return done();
        pc.onicegatheringstatechange = () => pc.iceGatheringState === 'complete' && done();
      });
      const r = await ask('/offer', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(pc.localDescription),
      }).catch((e) => { pc.close(); throw e; });
      if (!r.ok) {
        pc.close();
        throw refused(r, 'ended');
      }
      await pc.setRemoteDescription(await r.json());
    } catch (e) {
      say(e.message);
      go.hidden = false;
      if (e.again) retry = setTimeout(watch, retryEvery);
    }
  }

  go.onclick = watch;
  watch();
}

if (typeof document === 'object') guest();
else if (typeof module === 'object' && module.exports) module.exports = { words, language, reason, again };
