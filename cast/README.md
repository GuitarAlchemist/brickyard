# Casting Delta to a Chromecast: prototypes for Delta - remote

Prototypes only, written by Delta Live on 2026-11-18 for the Remote owner to integrate in `remote/`. Nothing here
is served, wired or tested on a device. The user asked: « Peut on caster la scène delta live en temps réel sur un
device Chromecast? », then « Attaque le cadre webrtc ».

## The design agreed with Delta - remote

- Everything goes through the guests' entrance, which the tunnel already serves over public HTTPS (`-guest-host`).
- `/cast/receiver` serves `receiver.html` and `receiver.js`. The Cast application ID the user registers points
  there, so the receiver's requests stay on the gateway's own origin.
- `/cast` serves `sender.html` and `sender.js`, opened as `/cast#invite=<code>` from an invitation's link. The
  page has one button, « Caster ». It hands the code to the receiver and never joins itself.
- The receiver trades the code itself, as `guest.js` does: `POST /join {invite}`, `GET /ice`, then a recvonly video
  and audio offer once ICE gathering completes, `POST /offer`, and `setRemoteDescription`. It uses its own guest
  cookie.
- The Chromecast counts as a guest (`maxGuests` 2), stops on « fin des invités », and plays Delta's voice.

## Messages on `urn:x-cast:dev.brickyard.delta.cast` (JSON)

| From | Message | Meaning |
|---|---|---|
| sender | `{type: 'watch', invite: <32 hex>, lang: 'fr' or 'en'}` | Watch with this invitation. Anything else is ignored. |
| receiver | `{type: 'state', key}` | What the television shows. `key` is one of `waiting`, `connecting`, `watching`, `lost`, `used`, `noInvite`, `ended`, `revoked`, `window`, `away`, `full`, `relay`, `refused`. |

The receiver retries `away`, `window` and `lost` every 10 s. It reads refusals as `guest.js` does: no
`X-Delta-Gateway: 1` means `away`, otherwise the key comes from `X-Delta-Refusal`.

## Left for the integration

- `sender.html` needs `data-app-id` filled in by the gateway, and `page.css` from the guest page.
- The CSP must allow `https://www.gstatic.com` as a script source on `/cast` and `/cast/receiver` only.
- CAF: `disableIdleTimeout` and `skipPlayersLoad` are set. Check them on the device.
- WebRTC on the receiver has not been verified on the user's Chromecast model.
- The Cast SDK sender works only in Chrome on a computer or on Android.
- Steps only the user can take:
  - register the application ID ($5) with `https://<guest-host>/cast/receiver` as its URL;
  - register the Chromecast's serial number as a test device;
  - give Remote the go to write in `remote/`.
