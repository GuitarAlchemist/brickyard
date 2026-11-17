# Delta's telemetry panel

A read-only panel showing Delta's status and two measured values. Each value comes with its source and the time it
was read. A value that is missing, stale, disconnected or not measured is shown as such; nothing is estimated.

```
go run . -data <Delta's plugin data folder> -out <folder> [-every 5s] [-for 10m]
```

Then open `<folder>\index.html` in a browser, as a file.

- **What it reads.** The data folder is the one Delta's brain writes its journal in; the `delta` MCP server's
  instructions name its `delta-events.jsonl`. It holds three files:
  - `delta-events.jsonl`, the brain's journal (the same file IX's `delta@1` probe tails);
  - `brain.pid`;
  - `scene.log`, the native scene's log.
- **What it writes.** `-out` receives `index.html`, `panel.js`, `snapshot.js` and `snapshot.json`.
- **How often.** Without `-every`, it writes one snapshot and exits. With `-every`, it rewrites the snapshot at that
  period (1 s at least) in the foreground, until Ctrl+C or the end of `-for`.

## What it shows

| Value | Source | Not shown when |
|---|---|---|
| The brain: running, stopped or unknown; pid, version, since when; the microphone | `brain.pid` and the journal's `start`, `stop` and `mic` events | the files are missing |
| Speech recognition time: median, min and max of the utterances the triage kept, out of the last 50 heard; the ignored ones counted apart | the journal's `heard` events, `asr_ms` and `route` | the journal holds no `heard` event |
| The scene's memory: working set and private, at N s of the scene's run | the last `SCENE memory` line of `scene.log` | the log holds no such line |
| Frame rate, GPU memory | none | always: marked *unavailable*, with the reason |

The page's badge reads:

- **en direct**: the snapshot is younger than three of the producer's periods (5 s at least; a minute when written
  once);
- **périmé**: the snapshot is older than that;
- **déconnecté**: `snapshot.js` cannot be loaded;
- **invalide**: the snapshot fails the contract.

## Boundaries

- No port, server, service, credential or permission. The panel is a page opened as a file, and it loads
  `snapshot.js` as a script, which a file page may do; it cannot fetch the file.
- The brain's HTTP port refuses web pages on purpose (`localOnly`, `brain/hooks.go`), and this tool does not change
  that.
- Of the journal, only the fields in `read.go`'s `journalLine` are decoded: `event`, `journal_seq`, `pid`, `t_ms`,
  `version`, `asr_ms`, `route` and `on`. What was heard or said, pairing details and the rest never reach a snapshot,
  and `TestWhatWasSaidNeverReachesTheSnapshot` checks this.
- Each log is read from its end:
  - 256 KiB at first;
  - doubling, up to 4 MiB, while fewer than 50 utterances are found.
- The page keeps 20 connection changes at most. It loads one snapshot every 2 s, and waits up to 10 s between
  attempts while the snapshot is missing.
- The native scene is not in the browser; the page shows numbers from the scene's log only.

## Contract

`contract.go` defines the snapshot, schema `delta-telemetry@1`, and `web/src/contract.js` checks the same fields. A
change to one is a change to the other and to the schema's number.

## Building and testing

The page is the Lit element in `web/src/delta-telemetry.js`. It is bundled into `web/build/panel.js`, a classic script
that a file page can run, and that file is committed: the Go command embeds it, so `go run` needs no Node.

```
cd web
npm ci
npm run build          # rewrites build/panel.js
npm test               # contract, bounded history, feed: no browser
node browser/check.mjs <chrome.exe> <evidence folder>
```

`browser/check.mjs` opens the built page as a file in a throwaway headless Chromium on port 9341, with its own profile.
It stands in for the producer, writing `snapshot.js` itself; it says so, and its values are made up. It checks:

- live, stale, disconnected, back to live, and invalid;
- the bounds;
- that the element holds nothing once removed, and starts again when added back.

It also saves desktop and phone screenshots. The phone screenshot is Chromium's device emulation, not a phone.

`..\..\check.ps1` runs the Go tests and `npm test`, which needs no `npm ci`.
