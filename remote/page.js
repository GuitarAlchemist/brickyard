'use strict';
const state = document.getElementById('state');
const go = document.getElementById('go');
const say = (text) => { state.textContent = text; };

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
    if (pc.connectionState === 'failed' || pc.connectionState === 'disconnected') {
      say('Connexion perdue'); pc.close(); go.hidden = false;
    }
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
    say({ 401: 'Cet appareil n’est pas appairé : ouvre le lien que le PC affiche au démarrage.',
          409: 'Un autre appareil est déjà connecté.' }[r.status] || ('Refusé : ' + r.status));
    pc.close(); go.hidden = false; return;
  }
  await pc.setRemoteDescription(await r.json());
}

go.onclick = () => connect().catch((e) => { say('Erreur : ' + e.message); go.hidden = false; });
pair().then(connect).catch((e) => { say('Erreur : ' + e.message); go.hidden = false; });
