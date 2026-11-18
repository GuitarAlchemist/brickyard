'use strict';
// The page that casts Delta to a Chromecast (a Web Sender v1 page), served by the gateway on its guests' entrance at
// /cast and opened from an invitation's link (/cast#invite=<code>): one button, « Caster », which starts the receiver
// (receiver.js) on a Chromecast the browser finds and hands it the invitation's code. It never joins: the receiver
// trades the code itself, with its own guest's cookie. Works in Chrome on a computer or on Android only, where the Cast
// SDK runs. A prototype for Delta - remote to take in.

const NAMESPACE = 'urn:x-cast:dev.brickyard.delta.cast';

const words = {
  fr: {
    cast: 'Caster',
    noInvite: 'Ouvre cette page depuis un lien d’invitation.',
    noCast: 'Caster demande Chrome sur un ordinateur ou sur Android.',
    ready: 'Choisis un Chromecast avec le bouton.',
    sent: 'Invitation transmise au Chromecast…',
    connecting: 'Le Chromecast se connecte à Delta…',
    watching: 'Delta est à l’écran.',
    lost: 'Le Chromecast a perdu la connexion : il réessaie.',
    stopped: 'Diffusion arrêtée.',
    other: 'Le Chromecast dit : ',
  },
  en: {
    cast: 'Cast',
    noInvite: 'Open this page from an invitation’s link.',
    noCast: 'Casting needs Chrome on a computer or on Android.',
    ready: 'Pick a Chromecast with the button.',
    sent: 'Invitation handed to the Chromecast…',
    connecting: 'The Chromecast is connecting to Delta…',
    watching: 'Delta is on the screen.',
    lost: 'The Chromecast lost the connection: it is trying again.',
    stopped: 'Casting stopped.',
    other: 'The Chromecast says: ',
  },
};

function language(preferred) {
  for (const tag of preferred || []) {
    const lang = String(tag).slice(0, 2).toLowerCase();
    if (words[lang]) return lang;
  }
  return 'en';
}

// inviteOf is the invitation's code in the page's #fragment, or null.
function inviteOf(hash) {
  const m = String(hash || '').match(/invite=([0-9a-f]{32})/);
  return m ? m[1] : null;
}

function sender() {
  const lang = language(navigator.languages || [navigator.language]);
  const w = words[lang];
  document.documentElement.lang = lang;
  const state = document.getElementById('state');
  const go = document.getElementById('go');
  const say = (text) => { state.textContent = text; };
  // The code stays in this page only: it leaves the address bar at once, as guest.js does once it has traded it.
  const invite = inviteOf(location.hash);
  history.replaceState(null, '', location.pathname);
  go.textContent = w.cast;
  if (!invite) return say(w.noInvite);
  // The gateway writes its application's ID into the page (data-app-id), the one the user registered for /cast/receiver.
  const appId = document.documentElement.dataset.appId;

  window.__onGCastApiAvailable = (available) => {
    if (!available || !appId) return say(w.noCast);
    const context = cast.framework.CastContext.getInstance();
    context.setOptions({ receiverApplicationId: appId, autoJoinPolicy: chrome.cast.AutoJoinPolicy.ORIGIN_SCOPED });
    const hand = (session) => {
      session.addMessageListener(NAMESPACE, (_, data) => {
        let m = data;
        try { if (typeof data === 'string') m = JSON.parse(data); } catch (e) { return; }
        if (m && m.type === 'state') say(w[m.key] || w.other + m.key);
      });
      session.sendMessage(NAMESPACE, { type: 'watch', invite, lang }).then(() => say(w.sent), () => say(w.lost));
    };
    context.addEventListener(cast.framework.CastContextEventType.SESSION_STATE_CHANGED, (e) => {
      if (e.sessionState === cast.framework.SessionState.SESSION_STARTED
        || e.sessionState === cast.framework.SessionState.SESSION_RESUMED) hand(context.getCurrentSession());
      if (e.sessionState === cast.framework.SessionState.SESSION_ENDED) say(w.stopped);
    });
    go.hidden = false;
    go.onclick = () => context.requestSession().catch(() => {}); // the browser's own picker; cancelling is no error
    say(w.ready);
  };
}

if (typeof document === 'object') sender();
else if (typeof module === 'object' && module.exports) module.exports = { NAMESPACE, words, language, inviteOf };
