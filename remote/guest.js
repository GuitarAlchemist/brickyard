'use strict';
// A guest's page: Delta's window, live, and nothing else. The invitation comes in the link's #fragment, which the
// browser sends to no server; it is traded once for a cookie that lasts two hours, and the fragment leaves the address
// bar. The relay's addresses come from the gateway, which asks Cloudflare for them.
const state = document.getElementById('state');
const go = document.getElementById('go');
const video = document.getElementById('scene');
const say = (text) => { state.textContent = text; };

async function join() {
  const m = location.hash.match(/invite=([0-9a-f]{32})/);
  if (!m) return;
  history.replaceState(null, '', location.pathname);
  const r = await fetch('/join', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ invite: m[1] }),
  });
  if (!r.ok) throw new Error('Cette invitation a déjà servi ou a expiré : demande un nouveau lien.');
}

async function watch() {
  go.hidden = true;
  say('Connexion à Delta…');
  try {
    await join();
    const ice = await fetch('/ice');
    if (!ice.ok) {
      throw new Error({ 401: 'Pas d’invitation valable : demande un lien à la personne qui t’invite.',
                        502: 'Le relais vidéo ne répond pas : réessaie dans un moment.' }[ice.status] || ('Refusé : ' + ice.status));
    }
    const { iceServers } = await ice.json();
    const pc = new RTCPeerConnection({ iceServers });
    pc.addTransceiver('video', { direction: 'recvonly' });
    pc.ontrack = (e) => { video.srcObject = e.streams[0] || new MediaStream([e.track]); };
    pc.onconnectionstatechange = () => {
      if (pc.connectionState === 'connected') say('Tu regardes Delta en direct. Rien de ce que tu fais ne lui parvient.');
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
      pc.close();
      throw new Error({ 401: 'Cette invitation est terminée.', 409: 'Deux invités regardent déjà : réessaie plus tard.',
                        502: 'Le relais vidéo ne répond pas : réessaie dans un moment.' }[r.status] || ('Refusé : ' + r.status));
    }
    await pc.setRemoteDescription(await r.json());
  } catch (e) {
    say(e.message);
    go.hidden = false;
  }
}

go.onclick = watch;
watch();
