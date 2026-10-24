'use strict';
// A guest's page: Delta's window, live, and nothing else. The invitation comes in the link's #fragment, which the
// browser sends to no server; it is traded once for a cookie that lasts two hours, and the fragment leaves the address
// bar. The relay's addresses come from the gateway, which asks Cloudflare for them.

// The page's words, in French and in English: guests are not all French speakers. guest.test.js checks that both
// languages say everything the page says.
const words = {
  fr: {
    connecting: 'Connexion à Delta…',
    watching: 'Tu regardes Delta en direct. Rien de ce que tu fais ne lui parvient.',
    lost: 'Connexion perdue',
    again: 'Se reconnecter',
    used: 'Cette invitation a déjà servi ou a expiré : demande un nouveau lien.',
    noInvite: 'Pas d’invitation valable : demande un lien à la personne qui t’invite.',
    ended: 'Cette invitation est terminée.',
    full: 'Deux invités regardent déjà : réessaie plus tard.',
    relay: 'Le relais vidéo ne répond pas : réessaie dans un moment.',
    refused: 'Refusé : ',
  },
  en: {
    connecting: 'Connecting to Delta…',
    watching: 'You are watching Delta live. Nothing you do reaches it.',
    lost: 'Connection lost',
    again: 'Reconnect',
    used: 'This invitation was used already or has expired: ask for a new link.',
    noInvite: 'No valid invitation: ask the person who invited you for a link.',
    ended: 'This invitation has ended.',
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

function guest() {
  const lang = language(navigator.languages || [navigator.language]);
  const w = words[lang];
  document.documentElement.lang = lang;
  const state = document.getElementById('state');
  const go = document.getElementById('go');
  const video = document.getElementById('scene');
  const say = (text) => { state.textContent = text; };
  const refusal = (status, reasons) => new Error(reasons[status] || (w.refused + status));
  go.textContent = w.again;

  async function join() {
    const m = location.hash.match(/invite=([0-9a-f]{32})/);
    if (!m) return;
    history.replaceState(null, '', location.pathname);
    const r = await fetch('/join', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ invite: m[1] }),
    });
    if (!r.ok) throw new Error(w.used);
  }

  async function watch() {
    go.hidden = true;
    say(w.connecting);
    try {
      await join();
      const ice = await fetch('/ice');
      if (!ice.ok) throw refusal(ice.status, { 401: w.noInvite, 502: w.relay });
      const { iceServers } = await ice.json();
      const pc = new RTCPeerConnection({ iceServers });
      pc.addTransceiver('video', { direction: 'recvonly' });
      pc.ontrack = (e) => { video.srcObject = e.streams[0] || new MediaStream([e.track]); };
      pc.onconnectionstatechange = () => {
        if (pc.connectionState === 'connected') say(w.watching);
        if (pc.connectionState === 'failed') { say(w.lost); pc.close(); go.hidden = false; }
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
        pc.close();
        throw refusal(r.status, { 401: w.ended, 409: w.full, 502: w.relay });
      }
      await pc.setRemoteDescription(await r.json());
    } catch (e) {
      say(e.message);
      go.hidden = false;
    }
  }

  go.onclick = watch;
  watch();
}

if (typeof document === 'object') guest();
else if (typeof module === 'object' && module.exports) module.exports = { words, language };
