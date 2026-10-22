# delta-remote

[Français](README.fr.md)

Shows Delta's window on a phone or another computer, live, over WebRTC. One device at a time.

```powershell
cd remote; go build; .\delta-remote.exe
```

It prints a link, `https://<address>:8810/#code=<code>`. Open it on the device within two minutes: that pairs the
device, which then needs no code. Each start prints a new code; `-forget-devices` unpairs every device.

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

Needs ffmpeg 8 or later with `gfxcapture` and `libx264` on PATH (or `-ffmpeg <path>`). Voice to and from Delta comes
next, through the brain's `/hear` and `/said`.
