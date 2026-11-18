'use strict';
// The Chromecast's page (a Cast Application Framework v3 receiver): Delta's window, live, on the television, as a guest
// of the gateway's guests' entrance sees it (remote/guest.js), and nothing else. Served by the gateway on that entrance
// at /cast/receiver, the origin the Cast application ID names, so that its requests are the gateway's own origin: the
// sender (sender.js) hands it the invitation's code on the namespace below, and the receiver trades it itself, with its
// own guest's cookie, for the relay's addresses and an answer to its offer. A prototype for Delta - remote to take in
// (the user, 2026-11-18: « Peut on caster la scène delta live en temps réel sur un device Chromecast? »).

const NAMESPACE = 'urn:x-cast:dev.brickyard.delta.cast';

// The words on the television, in French and in English, as the guest's page has them.
const words = {
  fr: {
    waiting: 'En attente de l’invitation…',
    connecting: 'Connexion à Delta…',
    watching: '',
    lost: 'Connexion perdue : nouvel essai dans quelques secondes.',
    used: 'Cette invitation a expiré : demande un nouveau lien.',
    noInvite: 'Pas d’invitation valable.',
    ended: 'Cette invitation est terminée.',
    revoked: 'La personne qui t’invite a mis fin à l’invitation.',
    window: 'La fenêtre de Delta est fermée : l’image revient dès qu’elle se rouvre.',
    away: 'Delta ne répond pas pour l’instant : nouvel essai dans quelques secondes.',
    full: 'Deux invités regardent déjà.',
    relay: 'Le relais vidéo ne répond pas.',
    refused: 'Refusé : ',
  },
  en: {
    waiting: 'Waiting for the invitation…',
    connecting: 'Connecting to Delta…',
    watching: '',
    lost: 'Connection lost: trying again in a few seconds.',
    used: 'This invitation has expired: ask for a new link.',
    noInvite: 'No valid invitation.',
    ended: 'This invitation has ended.',
    revoked: 'The person who invited you has ended the invitation.',
    window: 'Delta’s window is closed: the picture comes back once it opens again.',
    away: 'Delta is not answering right now: trying again in a few seconds.',
    full: 'Two guests are watching already.',
    relay: 'The video relay is not answering.',
    refused: 'Refused: ',
  },
};

// watchOf is the invitation's code in a sender's message, or null when the message is not a watch with a code of the
// gateway's form (32 hexadecimal digits): anything else from a sender is ignored.
function watchOf(message) {
  const m = typeof message === 'string' ? safeParse(message) : message;
  if (!m || m.type !== 'watch' || typeof m.invite !== 'string' || !/^[0-9a-f]{32}$/.test(m.invite)) return null;
  return { invite: m.invite, lang: m.lang === 'fr' ? 'fr' : 'en' };
}

function safeParse(text) {
  try { return JSON.parse(text); } catch (e) { return null; }
}

// reason is the key of what the television says when it cannot watch, from an answer's headers, as guest.js reads
// them: none of the gateway's while it is away, else its X-Delta-Refusal.
function reason(headers, ended) {
  if (!headers || headers.get('X-Delta-Gateway') !== '1') return 'away';
  const why = { ended, revoked: 'revoked', window: 'window', full: 'full', relay: 'relay' };
  return why[headers.get('X-Delta-Refusal')] || 'refused';
}

// The reasons that pass by themselves, for which the receiver tries again every retryEvery.
const again = new Set(['away', 'window', 'lost']);
const retryEvery = 10000;

function receiver() {
  const context = cast.framework.CastReceiverContext.getInstance();
  const state = document.getElementById('state');
  const video = document.getElementById('scene');
  let w = words.en;
  let invite = null;
  let pc = null;
  let retry = null;
  let lastSender = null;

  // tell shows what happened on the television and sends it to the sender that asked, as { type: 'state', key }.
  const tell = (key, status) => {
    state.textContent = key === 'refused' ? w.refused + status : w[key];
    state.hidden = state.textContent === '';
    if (lastSender) context.sendCustomMessage(NAMESPACE, lastSender, { type: 'state', key });
  };
  const ask = async (url, init) => {
    const r = await fetch(url, { credentials: 'same-origin', ...init }).catch(() => null);
    if (!r || r.headers.get('X-Delta-Gateway') !== '1') throw Object.assign(new Error('away'), { key: 'away' });
    return r;
  };
  const refusal = (r, ended) => Object.assign(new Error('refused'), { key: reason(r.headers, ended), status: r.status });

  async function watch() {
    clearTimeout(retry);
    if (!invite) return tell('waiting');
    tell('connecting');
    try {
      // An invitation serves for its whole hour: trading it again after a reload or a retry is what guest.js does too.
      const joined = await ask('/join', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ invite }),
      });
      if (joined.headers.get('X-Delta-Refusal') === 'revoked') throw refusal(joined);
      const ice = await ask('/ice');
      if (!ice.ok) throw refusal(ice, joined.ok ? 'noInvite' : 'used');
      const { iceServers } = await ice.json();
      if (pc) pc.close();
      pc = new RTCPeerConnection({ iceServers });
      pc.addTransceiver('video', { direction: 'recvonly' });
      pc.addTransceiver('audio', { direction: 'recvonly' });
      const stream = new MediaStream();
      pc.ontrack = (e) => { stream.addTrack(e.track); video.srcObject = stream; video.play().catch(() => {}); };
      pc.onconnectionstatechange = () => {
        if (pc.connectionState === 'connected') tell('watching');
        if (pc.connectionState === 'failed') {
          pc.close();
          tell('lost');
          retry = setTimeout(watch, retryEvery);
        }
      };
      await pc.setLocalDescription(await pc.createOffer());
      await new Promise((done) => {
        if (pc.iceGatheringState === 'complete') return done();
        pc.onicegatheringstatechange = () => pc.iceGatheringState === 'complete' && done();
      });
      const r = await ask('/offer', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(pc.localDescription),
      });
      if (!r.ok) throw refusal(r, 'ended');
      await pc.setRemoteDescription(await r.json());
    } catch (e) {
      const key = e.key || 'away';
      tell(key, e.status);
      if (again.has(key)) retry = setTimeout(watch, retryEvery);
    }
  }

  context.addCustomMessageListener(NAMESPACE, (event) => {
    const asked = watchOf(event.data);
    if (!asked) return;
    lastSender = event.senderId;
    w = words[asked.lang];
    document.documentElement.lang = asked.lang;
    if (asked.invite === invite && pc && pc.connectionState === 'connected') return tell('watching');
    invite = asked.invite;
    watch();
  });

  const options = new cast.framework.CastReceiverOptions();
  options.disableIdleTimeout = true; // a picture with no media session is not idle: the framework would close it
  options.skipPlayersLoad = true; // no media player: the picture is a WebRTC stream
  options.customNamespaces = { [NAMESPACE]: cast.framework.system.MessageType.JSON };
  context.start(options);
  tell('waiting');
}

if (typeof document === 'object' && typeof cast === 'object') receiver();
else if (typeof module === 'object' && module.exports) module.exports = { NAMESPACE, words, watchOf, reason, again };
