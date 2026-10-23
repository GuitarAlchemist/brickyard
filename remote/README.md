# delta-remote

[Français](README.fr.md)

Shows Delta's window on a phone or another computer, live, over WebRTC, and lets you talk with Delta from it. One
device at a time.

```powershell
cd remote; go build; .\delta-remote.exe
```

It prints a link, `https://<address>:8810/#code=<code>`, and hands it to Delta's window for its QR card (the phone
icon). Scanning the QR pairs the device in one step; it then needs no code for 90 days. The code is renewed every
100 s, and as soon as a device uses it, so the card always holds a QR that works while the gateway runs;
`%LOCALAPPDATA%\delta-remote\link.txt` holds the same link. `-forget-devices` unpairs every device.

- **Talking:** the page shows the conversation as bubbles, each answer under the sentence it answers. There are two
  ways to speak:
  - **Hold** the button and speak; what the microphone heard goes when you let go.
  - **Appeler** starts a call. The microphone stays open, and the page cuts what it hears into sentences by loudness,
    sending each one when you stop speaking. It does not listen while Delta speaks, so it never hears Delta. The
    screen stays on until **Raccrocher**.

  Each sentence goes to Delta's brain as a 16 kHz WAV (30 s at most), which transcribes it and takes it as said to
  Delta. A chime plays in the room. The device plays the answers to its own sentences. It shows the answers to the
  computer's sentences without sound, with a ▶ to play one here, and plays Delta speaking on his own only during a
  call. The brain's `/hear`, `/said` and `/link` need the secret it writes at each start to
  `%LOCALAPPDATA%\delta\remote.secret`; the gateway reads it on each request. The page's logic that needs no browser
  is tested by `node --test` (`talk.test.js`).

- **What it shows:** the one visible window titled `Brickyard` of `scene.exe`, through ffmpeg 8's `gfxcapture`
  (Windows Graphics Capture, so the window shows even when covered). None or several such windows: nothing is shown.
  ffmpeg runs only while a device is connected.
- **Where it listens:** one address (default: this PC's LAN address) on TCP 8810 and UDP 8811. Windows lets a device
  in only on a network marked Private, or with a rule for those two ports.
- **Its certificate** is its own by default: the browser warns once, and while you accept that warning a device on the
  same network could stand in for the gateway. To rule that out, give a certificate the device trusts, such as the one
  `tailscale cert` makes for this machine's tailnet name: `-ip <tailnet address> -cert <crt> -key <key> -name <name>`.
- **What it refuses:** a request that names another host (DNS rebinding), a POST from another origin, an offer from an
  unpaired device, a second device. The page runs its own script only.

Needs ffmpeg 8 or later with `gfxcapture` and `libx264` on PATH (or `-ffmpeg <path>`), and a brain with `/hear`,
`/said` and `/link` for voice and the QR.
