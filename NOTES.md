# Brickyard P0 receipt (GuitarAlchemist/gaia#260)

2026-10-22. The author asked to start with the human-machine interaction: « un petit avatar qui est un robot. Auquel on
puisse parler ».

## What is here

- `brain/` (Go, standard library only). A typed line goes to a local Ollama model (qwen3:8b, `think: false`, a 4096-token
  context), the answer is cut into sentences as it streams, each sentence goes to one persistent Piper process and is
  played in order. Each state change goes to the scene as one JSON line.
- `scene/` (C#, BRUTAL's Vulkan bindings, built from ga-brutal's HallSlice). The robot stands in a brick hall: 51 boxes
  in 14 parts that move, 1028 boxes of hall. It listens on 127.0.0.1:8805 and shows idle, thinking (amber eyes, head
  tilted, hand to the chin, a running light) and speaking (the mouth and a chest meter follow the voice's loudness, read
  every 20 ms from the WAV).

Run, in two terminals:

    scene\bin\Release\net10.0\scene.exe
    brain\brain.exe -piper <piper.exe> -voice <a French Piper voice .onnx> -scene 127.0.0.1:8805

`scene.exe --demo` plays the three states without the brain; `--shots DIR` with it saves one PNG of each; `--selftest`
checks the line parsing and the mouth's loudness without a window. Build with `go build` in `brain/` and
`dotnet build -c Release` in `scene/`: `global.json` pins the SDK band and `nuget.config` restores only from the
packages already on the machine.

## Measured

- Brain, muted, warm model, two turns: first token 37–47 ms, first sentence 175–240 ms, first audio ready 587–1019 ms
  after the line was read. The 1019 ms turn's first sentence was 5.9 s of speech. Earlier muted runs: 183–678 ms.
- Model load: 4.0 s when Ollama still had it cached, 55 s cold (earlier run). Unloaded after each test, to leave the
  video memory to the live scene.
- Scene, hidden, vsync and a 60 fps cap, 6–7 s demo runs with three PNG captures, beside the live Colossus stream:
  frame p50 16.67 ms, p99 17.3–17.4 ms, max 19.2–20.8 ms; CPU 0.9 % of 24 logical CPUs; peak working set 209 MB.
- End to end: the brain's events reached the scene over TCP in order (idle, thinking, speaking ×3, idle, then thinking,
  speaking ×2, idle).

## Ears (2026-10-22, later)

The author asked to reuse what IX already had rather than download anything; IX's session pointed to its whisper.cpp
b5454 CPU build and `ggml-large-v3-turbo-q5_0.bin` under `%LOCALAPPDATA%\voice-listen` (SHA-256 checked against IX's
`PINS.txt`). The brain runs that `whisper-server.exe` on 127.0.0.1:8806, tied to itself by a Windows job so that it
never outlives the brain, and keeps it warm.

- `-mic`: the default microphone through winmm's waveIn, 16 kHz; an energy endpoint measures the room for 0.5 s, starts
  on three loud frames, ends after 700 ms of quiet, keeps the 300 ms before the start and drops what has under 240 ms
  of speech. Nothing is heard while Delta answers, nor for 300 ms after. The scene shows `listening` (green eyes,
  head tilted toward the speaker).
- `-hear a.wav,...` transcribes files as if heard; `-transcribe` only times the transcription.
- Measured on three French sentences spoken by Piper (2.1–3.3 s): all three transcribed word for word ("dix" written
  10). Transcription with the model's 30 s window and 8 threads: 5.7–9.2 s; 16 threads: 4.9–7.0 s; 16 threads and
  `-ac 768` (about 15 s, the endpoint's longest utterance): 1.6–2.4 s, the same words. Whole turn with those defaults,
  from the end of the sentence heard to Delta's first audio ready: 2.1–2.3 s (muted). With the microphone, the 700 ms
  of quiet that ends an utterance comes on top.

## Live, with the author (2026-10-22, evening)

The author talked to Delta through the microphone with the scene visible (« Ok, ça marche bien »). From the brain's log:

- transcription mostly 1.7–3.9 s, one 13.5 s;
- first audio 3.0–5.9 s after the end of speech;
- the endpoint cut every utterance where the author stopped.

Before `-nf`, a mumbled « Euh... Oui. » took 31.6 s: whisper.cpp's temperature fallback retried it. whisper-server once
closed the connection mid-request (wsarecv); the cause is not established. Since then, the brain passes `-nf` and
utterances are cut at 14 s, within the 768-frame audio context.

The author asked Delta, aloud, to show token consumption in the scene. Delta answered that it had built the indicator
and updated the scene, though it can do neither. That over-claim is why Delta is to be linked to Claude Code next,
rather than answer from qwen alone.

## Splash and release (2026-10-22, night)

The author asked for a public GitHub release (« on va l'utiliser dans le public »), at GuitarAlchemist/brickyard, under
MIT, with a logo screen at start-up. BRUTAL License v1.0 asks a distributed product for three things:

- the Brutal logo before its primary function, unaltered except for scaling (2.2);
- BRUTAL's copyright in its credits (2.1);
- NOTICE.txt alongside (2.3).

What Delta does:

- `scene/brand/` holds `LOGO_BRUTAL_WHT.png` and `NOTICE.txt` from ahwootech/brutal-branding at 5d38373. Its NOTICE is
  byte-identical to the packages' NOTICE.
- The scene opens on the logo at half the window's width (640×140 from 4096×896, area-averaged, one texel a pixel).
  BRUTAL's notice sits beneath it, drawn at start-up by GDI in Segoe UI, so that no font ships.
- The splash lasts 2.6 s: 0.3 s fade-in, 2 s held, 0.3 s fade-out. It is timed from the first frame on screen: a first
  run timed it from start-up and showed the logo for only 1.6 s, the first frame coming at 1.04 s.
- The scene refuses to start without the logo file.
- `--shots` saves `splash.png`.
- Hidden demo, 60 fps cap: frame p50 16.66 ms, p99 17.2 ms.

`release.ps1` builds the brain with `-trimpath` (no local path in the binary, checked) and the scene self-contained.
The scene's build needed `Microsoft.NETCore.App.Runtime.win-x64` 10.0.13, Microsoft's signed package, from nuget.org.
`nuget.config` now names nuget.org: exact versions already cached are not downloaded again, and the lock file pins
hashes.

The release also carries every licence and notice text of the packages, the runtime and Go, each once, indexed in
`LICENSES\PACKAGES.txt`. Whisper, Piper, the voices and Ollama are not shipped: README says where to get each, with the
tested hashes.

## Claude Code plugin (2026-10-22, night)

The author asked to make Delta easy for anyone to install and connect to their own Claude Code: « fais tout ce qu'il
faut, la CI/CD, les plugins, la marketplace, les GitHub release ».

- `brain.exe -mcp` serves Claude Code over stdio with two tools, `say` and `listen`, and a channel.
  - A sentence goes to the oldest waiting `listen`.
  - With no `listen` waiting, it goes to the session as a channel event, but only if one of its first three words
    calls Delta or Claude, its brain, or if it comes within 15 s of a sentence that did, or within 20 s of Delta's
    last words.
  - Some sentences are dropped, and end the conversation:
    - one that calls another of the author's sessions first: Brutal, Gaïa, Indigo, Amber, Codex or IX;
    - one that says it is for one of them (« je parle à Brutal »);
    - one that tells Delta it is not for it (« je ne te parle pas »).
    `-no-wake` lets everything else through.
  - Otherwise it is dropped.
  - `-journal <file>` records, one JSON line each:
    - each sentence heard, with its `utterance_id`, where it went and why;
    - each `say` call, with the `utterance_id` it answers;
    - when Delta started and finished saying it.
  - stdout carries only JSON-RPC.
  - Piper and whisper-server run with no console and die with the brain (Windows job).
- The marketplace is `.claude-plugin/marketplace.json`; the plugin is `plugins/delta`.
  - Its MCP server is `${CLAUDE_PLUGIN_DATA}/delta-<version>/brain.exe`, so an update installs beside the running brain
    instead of over it.
  - `/delta:setup` runs `scripts/setup.ps1`, which installs Delta's release zip, checked against GitHub's SHA-256 for
    the asset, and Piper, the voice, whisper.cpp and the model, at pinned URLs and SHA-256s.
  - `/delta:talk` holds a spoken conversation with `listen` and `say`, for sessions without the channel.
- `check-plugin.ps1` checks that the marketplace, the manifest, the skills and the version agree, and that every script
  parses under PowerShell 5.1.
- `ci.yml` runs gofmt, vet, the Go tests, the scene's build and selftest, and that check.
- `release.yml`, on a tag `v<version>`, builds with `release.ps1`, checks the built versions and publishes the
  release with `docs/releases/<version>.md`.

Measured:

- 9 Go tests on the MCP server pass: handshake, `say`, routing, timeout, cancellation, not ready, wake gate.
- `claude plugin validate` passes on the marketplace and on the plugin (Claude Code 2.1.308).
- `check-plugin.ps1` passes under PowerShell 5.1.26100 and 7.6.7.
- The 0.2.0 zip, built locally, is 42.1 MB with 242 files.
- `setup.ps1` under PowerShell 5.1, with `-Zip` and IX's Piper and whisper.cpp named instead of downloaded:
  - `-Check` changed nothing;
  - the install wrote `delta.json` without a BOM, and a second run found everything installed;
  - the installed `brain.exe -mcp` answered `initialize` in 0.02 s and refused `listen` while its ears loaded;
  - it heard a sentence at 14.4 s and exited 0 when stdin closed.
- Silence was transcribed once as "Voirte.". Through the channel the wake gate drops such words; under `/delta:talk`
  Claude receives them.

## Local triage and the cost lamp (2026-10-22, afternoon)

Live, the wake gate let everything through once a conversation had started: « Cool. », « Ah! », sentences meant for
Gaia or for Claude in another window. The author asked for a cheaper model to read what Delta hears first and pass
only the rest to Claude, and for a small indicator of when an answer is free and when it costs.

- `-triage <ollama model>` (or `"triage"` in `delta.json`) sorts each sentence the wake gate lets through, before the
  channel:
  - two rules need no model: fillers and artefacts (« Ah! », « Hmm. », another script) are dropped, and so is a
    sentence that calls Gaia, Claude or Codex among its first three words without calling Delta among its first five;
  - when Claude's last words ended with a question mark, the next sentence goes to Claude untriaged, since a bare
    « ok » is then an answer;
  - otherwise the model answers `ignore`, `local` with a short answer that Delta speaks at no cost, or `claude`;
  - a failure, or no verdict within 4 s, sends the sentence to Claude;
  - the next channel event carries what was handled locally in its `handled_locally` meta;
  - the journal records each verdict and its time.
- The schema is written out in order: Go sorts a map's keys, Ollama generates in the schema's order, and with
  `answer` before `route` qwen3:8b answered first and routed « Delta, lance les tests » to itself.
- The scene takes an optional `"cost":"free"|"paid"` on its lines. A lamp above the chest panel is dark at rest,
  green while the local model answers, and gold while Claude thinks or speaks. The author then asked for a Claude Code
  sign when a sentence goes to Claude: a terracotta eight-pointed star grows in beside the head while the cost is
  `paid`, turns slowly, and shrinks away after.

Measured:

- qwen3:8b through Ollama: 0.2 s a verdict once loaded, 13 s cold; the brain loads it at start.
- On 21 sentences from this session, the model alone sorted 16 right. Of the 5 misses, 4 are covered by the rules
  above, and the last one sent to Claude a sentence meant for someone else, which costs tokens but loses nothing.
- 9 new Go tests: small talk, dropping, fillers, failure and slowness, Claude's question, `handled_locally`, the
  verdict's parsing, the Ollama request, the other-assistant rule. The scene's selftest checks the cost field.
- Live, after `/mcp` reconnected delta: « Salut Delta ! » got « Salut ! » from qwen3:8b, the lamp lit green, and Claude
  received it in `handled_locally` with the next sentence.
- That reconnect started the new brain but left the old one running, a child of the same claude.exe: both heard the
  microphone, both sent channel events, and two whisper-servers sharing the CPU took a transcription from about 2 s to
  8.5 s. Stopping the old brain ended it. Whether Claude Code kept its stdin open is not established.

## After the first live hour of the triage (2026-10-22, afternoon)

The journal showed the small model going wrong in four ways, and the author asked to fix the retrospective's points.
Each fix is a rule in code, not a prompt change alone:

- It parroted: « Ok cool. » answered « Ok cool. », and a whole question came back word for word. An answer that
  shares four in five of its words with the sentence is not said, except a greeting returned.
- It kept a question, « c'est toi-même qui réfléchis, c'est ça ? ». A sentence with a question mark goes to Claude.
- It promised work: « Ok, mais fais collaborer les deux. » got « D'accord, je m'en occupe. ». An answer that takes on
  work, such as « je m'en occupe » or « je vais », sends the sentence to Claude instead.
- It answered « Ok. » and « Cool. » with « De rien. ». Acknowledgement words alone are now kept silently, without the
  model.
- It kept « Delta n'entend plus rien. » and answered « D'accord. ». Only a sentence with a word of small talk, such as
  merci, salut, bonne nuit, bravo or attends, may now stay with the small model; the rest goes to Claude.
- The rule written first, that a sentence calling Claude is for another window, was wrong. « Ok, Claude, améliore la
  rétro » was dropped, and the author had to type it. « Claude » is now a wake word, like « Delta », that skips the
  triage. Gaia, Codex and IX are the other assistants. Whisper writes IX, which the author says « ixe », as « X ».
- Piper read « IX » as « neuf romain ». Delta now says « ixe », and Whisper hears its clip as « X ». Whisper's prompt
  names IX and Gaia.

Around it:

- One Delta at a time on the machine: a new brain writes its pid to `%LOCALAPPDATA%\delta\brain.pid`, whatever its
  `-home`, and an older brain that sees another pid tells its session on the channel, then stops. `/mcp` reconnects
  twice left the old brain running beside the new one, and the author asked that two Delta sessions never run at once.
  A first version kept the file beside `delta.json`, which left the installed plugin and a dev build free to run
  together; new brains still write there once, to stop brains of that version.
- While Delta listens, pads on its ears stand out and light green. The author asked for an ear sign, since green eyes
  were the only one.
- Each journal line carries `pid`, `journal_seq` and `t_ms`, for IX's planned `delta@1` probe, which tails the journal.
- The MCP instructions name the repository and, with `-journal`, the journal's path.
- `brain/testdata/triage-corpus.json` holds 34 sentences heard live with their right route. `TestTriageCorpus` runs
  them through the real model when `DELTA_TRIAGE_OLLAMA` is set. It fails on a sentence lost to Claude or answered
  for someone else.
- `.githooks/pre-commit` runs CI's checks on the parts a commit touches. `ci.yml` built the scene with
  `dotnet build --locked-mode`, which SDK 10.0.113 refuses as an unknown switch: CI never ran, being unpushed, and
  would have failed. It now restores with `--locked-mode -r win-x64`, since the lock file holds the win-x64 graph that `release.ps1`'s
  publish writes, then builds with `--no-restore`. A plain `dotnet build` rewrites the lock file without that graph.
- `dev-deploy.ps1` builds the checkout into `delta-dev-<git describe>` beside the installed Delta and points an
  `.mcp.json` at it.
- The author asked for a body of Mac minis: the torso is fourteen of them, two to a layer, each with a foot, a port
  slot and a status light that breathes and flickers with the voice; each arm is four small ones.

Measured:

- qwen3:8b on the corpus: 33 of 35 right. Both misses go to Claude, which costs tokens but loses nothing.
- After the second deploy, Delta seemed deaf for about a minute: the CPU was at 100%, com.docker.backend taking about
  10 of 24 cores, and the new brain's whisper-server took that long to load. Transcriptions then took 18 to 54 s
  instead of about 2 s.
- The Go tests pass, and so do the scene's selftest and the hook.

## Context gauge and compaction (2026-10-22, evening)

The author asked to see Claude's compactions and how full the context is on the robot. A compaction had just taken
92 s, during which Delta seemed slow and Claude heard nothing.

- The lights of the 14 Mac minis are now a gauge, one part per layer of two. Fill is used / limit. used is what
  Claude's last answer read and wrote (input, cache read, cache creation and output tokens), from the end of the
  transcript, or a compaction's postTokens. limit is the preTokens of the transcript's last automatic compaction,
  which is where this session's model and settings compact. Before one, it is CLAUDE_AUTOCOMPACT_PCT_OVERRIDE (95 %
  without it) of CLAUDE_CODE_AUTO_COMPACT_WINDOW or of a million tokens. Here the override is 40 % and the window a
  million: the first compaction came at 392,630 tokens.
- The brain finds the transcript without hooks. Its parent is claude.exe, whose `~\.claude\sessions\<pid>.json`
  names the session, and the transcript is `~\.claude\projects\<cwd with dashes>\<session>.jsonl`. It reads it when
  it grows, at most every 5 s.
- Only the start of a compaction needs a hook: nothing is written to the transcript until it ends. PreCompact,
  PostCompact and SessionStart (compact) run one curl that posts the hook's JSON to the brain. The brain answers 204,
  so that curl prints nothing, and refuses a post without the X-Delta-Hook header, which a web page cannot send. A
  hook from another session is ignored. A compaction no hook ends is over after 10 minutes.
- A sentence for Claude during a compaction still goes to Claude, and Delta also says, once per compaction, that
  Claude is compacting and will answer in about a minute.
- --demo now ends with 2.5 s of compaction, while the gauge fills; --shots saves `full` and `compacting`.
## First live session through Claude Code (2026-10-22, afternoon)

The author ran a real Claude Code session with Delta's channel (`server:delta`, the plugin's brain, journal on). The
journal of the first 12 minutes, 23 sentences heard and 14 said:

- Before the first "Delta", sentences 1 to 5 were dropped, among them a Whisper hallucination in Korean.
- Answers were tied to their questions: 10 of the 14 `say` calls carried the `utterance_id` they answered (not 7, 11,
  12 and 14).
- A question of fact was checked before it was answered. "Ok, est-ce que tout doit être transmis à Claude Code…"
  (utterance 12) got « je regarde comment Delta est configuré » 4.3 s later, then 2 min later an answer starting
  « Voici la règle, d'après les notes de commit de Delta ».
- A short answer started 4.0 s after its transcript (utterance 9). Add the transcription, which took 1.5 s to 13.9 s
  over the 23 sentences.
- The 45 s window, extended by every sentence let through, kept the channel open while the author talked to others.
  "Ok Gaia, tais-toi parce que Delta parle" and "Non, Gaia, je vais toujours à d'altar" reached the session, as did
  "Ok, c'est fait, Delta est lancé", said to the Claude session in the terminal.
- "Delta, je ne te parle pas" would have been let through as calling Delta.

The author asked for that to stop: « Quand je parle à Claude, c'est à Claude que je parle, c'est pas à Delta. Delta,
je ne te parle pas, et Gaïa, je ne te parle pas non plus. » The wake gate now ends the conversation on a sentence for
someone else, and no longer stretches on sentences that do not call Delta. `TestAddressee` holds those sentences.

The local triage, built meanwhile in the Delta Live session, had made « Claude » a wake word: « Ok, Claude, améliore
la rétro » was meant for Delta's brain. Merging the two, the author chose (2026-10-22): « Claude » calls Delta's brain,
and the Claude session in the terminal is called Brutal, which Delta ignores like Gaïa, Indigo, Amber, Codex and IX.

## Face, window, shelf and warm voice (2026-10-22, night)

The author asked, by voice and while busy, for expressions on the robot, for Delta to move its own window
(« maximise ta fenêtre », « en haut à gauche »), for a tray icon, for a 3D replica of Claude Code's plugin and
marketplace menu with the list of Claude Code sessions, « quelque chose qui n'encombre pas trop l'écran, quelque
chose de hiérarchique », and for IX's warm voice. Camera gestures were offered to IX, which answered that they are outside its current slice, that the author
decides, and that the receiving end is Delta's.

- **Face.** Brows and mouth corners join the eyes and the mouth; nine expressions are presets of the same few parts.
  A `mood` line holds one for six seconds. Claude gives `say` a mood; the brain sends it with the first sentence's
  `speaking` line, so that the face changes as the voice starts, not when the call is queued.
- **Window.** The window became resizable: the swapchain is rebuilt when the framebuffer changes, and frames are
  skipped while it is iconified or in the tray. Placement runs on the render thread, where GLFW must be called, from a
  queue the link fills. Corners and sides are computed on the work area of the monitor the window is on, with a
  12-pixel margin. Probed live: top-left, smaller, bottom-right, maximize (the swapchain rebuilt at 3840x2054),
  restore, center, tray, show and minimize.
- **Tray.** Shell_NotifyIconW with a message-only window, whose messages GLFW's PollEvents dispatches; the icon is
  drawn in code, a 32x32 face. A click brings the window back.
- **Shelf.** A card in the window's corner rather than a 3D object: text stays legible at any window size, and the
  robot turns its head and points at it. Three levels at most are shown as a breadcrumb, eight rows at a time.
  Sessions come from `~\.claude\sessions\*.json`, grouped by `wmux list-workspaces` and `list-surfaces`, those
  `agent-state` says are waiting first; each call costs about 0.45 s, so the list loads in the background. Plugins
  come from `installed_plugins.json` and `settings.json`, marketplaces from `known_marketplaces.json` and each
  catalogue. The shelf closes after 45 s without a move, and the brain forgets it then too.
- **Spoken commands.** A sentence of ten words or fewer that asks for the window or the shelf never reaches the
  triage or Claude: the brain sends the line, says a short answer at no cost, and Claude reads the exchange as
  `handled_locally` with the next event. A place needs a verb that moves Delta (« mets le fichier à gauche » is for
  Claude), a section needs a verb that shows it (« ferme toutes les sessions » is for Claude), and steps through the
  shelf count only while it is open. The shelf is checked first, so that « reviens en arrière » steps back while
  « reviens » alone brings the window back.
- **Warm voice.** IX's chain (GuitarAlchemist/claude-plugins `plugins/voice/service/fx.go` at fb2b2f5), ported to
  float samples. One effect runs through all the sentences of one `say` call, so that they flow as one signal, and
  its 0.3 s tail rings after the last; each sentence's WAV is rewritten before it plays and its envelope recomputed.
- **Gestures, the receiving end.** `POST /gesture` on the hooks' address, behind the same header, takes
  `{"gesture","hand","confidence","t_ms"}`. Nine gestures are known; an unknown one gets 400. Below 0.6 confidence a
  gesture is recorded and not acted on. The journal line keeps the camera's time as `seen_ms`, since `t_ms` is the
  journal's own, and says what the gesture did (`does`) or why not (`why`). IX's delta@1 copies every top-level field,
  so the lines reach IX without a change there. Which process watches the camera, and with what model, is the
  author's call.
- **End to end**, a muted, deaf brain with its own LOCALAPPDATA, so that the live Delta kept the lock, drove the new
  scene through JSON-RPC: `say` with `joy`, `window` top-left (8,8), maximize (zoomed), restore, bottom-right
  (1684,867), and `shelf` sessions, down and close, without an error from Piper, the effect or the scene.

## English, and French as before (2026-10-22, evening)

After the plugin's first release, the author asked: « il faut que ça marche pour les anglophones aussi », then
« Anglais par défaut, avec support/instructions français également. Bilingue. »

- **The setting.** `-lang en|fr` on the brain, `"language"` in `delta.json`, `-Language` on `setup.ps1` and `-Lang` on
  `start-delta.ps1`. English applies when nothing says otherwise (`brain/lang.go`). The setting chooses:
  - Whisper's `-l`;
  - the voice;
  - the language Claude is told to answer in, in the instructions and in the descriptions of `say` and `listen`;
  - the standalone persona.
- **The English voice** is en_US-ljspeech-medium, from piper-voices at the revision the French voice is pinned to
  (5041a7a). Its model card gives its dataset, LJ Speech, as public domain. lessac's card points to the Blizzard 2013
  licence, and amy was fine-tuned from lessac, so neither was taken.
- **Measured with Piper's English voice into the new brain** (`-lang en -transcribe`, large-v3-turbo q5_0, CPU):

  | Spoken by Piper | Heard, from sample 0 | Heard, after 0.5 s of silence |
  |---|---|---|
  | "Delta, what is the repository of I X?" | "What is the repository by x?" | "Delta, what is the repository by X?" |
  | "Hey Delta, can you hear me?" | "Hey Delter, can you hear me?" | "Hey Delter, can you hear me?" |
  | "Claude, run the tests please." | "Claude, run the tests please." | "Claude, run the tests please." |

  - The clip that starts on its first word lost "Delta". The microphone's endpoint keeps audio from before the
    speech, as the French sessions show, so this comes from the test clip.
  - "Delter" is Whisper's spelling of a non-rhotic "Delta": `addressee` now counts it as Delta.
  - Whisper heard one second of silence as "Thank you.". It is not dropped, because a person says it too.
- **Addressing in English.**
  - "I'm not talking to you", "not speaking to you" and "not for you" end the conversation.
  - "talking to", "speaking to" or "with" before another session's name gives the sentence to that session.

  The tests are in `mcp_test.go`.
- **Setup, under PowerShell 5.1.**
  - An install without a language, but with a recorded French voice, is read as French.
  - A 0.2.0 install whose French voice setup downloaded is read as French too.
  - An empty folder starts in English (693 MB).
  - `-Language en` on a French install downloaded and verified the English voice (61 MB). It dropped the recorded
    French voice from `delta.json` and kept `triage`.
- **The v0.2.0 release works from an empty folder.** Its workflow published the zip. `setup.ps1` installed it into an
  empty folder, checked it against the asset's digest, and `brain.exe -version` printed 0.2.0.
- **Delta Live's files in English too**, the same evening.
  - Requests are read in both languages whatever the setting: the window and shelf phrases, the fillers, the
    acknowledgements, the small talk and the promises a local answer must not make. A French word in an English
    session is read as before.
  - What Delta says follows the setting: the window and shelf answers, the triage's prompt and the language of its
    local answers, the sentence said during a compaction, and « IX », read « ixe » by the French voice and "I X" by
    the English one. The French prompt is word for word the one the corpus was measured with.
  - English is riskier to match than French: "move", "go", "right" and "hide" are common words. A place needs its
    preposition ("to the right"), and "hide", "open", "up" or "down" count only as the whole request. "Delta, hide
    the terminal", "go ahead, all right" and "move the file to the left folder" go to Claude.
  - The scene's shelf learns the language from a `{"language":"en"|"fr"}` line, which the brain sends first on every
    connection, so that a window started later, or by another brain, knows it too. `--lang fr` sets it for shots.

## Another device, through a gateway (2026-10-22, night)

IX is building a gateway that lets the author speak to Delta from a phone or another computer on the local network,
over WebRTC. The brain's side, as agreed with IX's session (`brain/remote.go`):

- **`POST /hear`** takes a 16 kHz mono 16-bit WAV of at most 30 s: 960,000 bytes and a header; more gets 413, another
  rate 400. The brain transcribes it with its own whisper-server and routes it as it would a sentence from the
  microphone, with three differences:
  - it needs no wake word, since pressing the device's button addressed Delta;
  - its `heard` line and its channel event carry `source: "remote"`;
  - a chime of two short notes, 880 then 1320 Hz, tells the room, unless Delta is speaking, since one sound stops the
    other. The microphone is deaf while it rings.

  It answers 202 with `{"utterance_id","text","route"}`, or 503 until the ears are ready.
- **`GET /said`** streams server-sent events, one per sentence as it starts playing here:
  `{"say_id","utterance_id","by","text","seconds","t_ms","wav_b64"}`, the sentence's WAV after the warm voice.
  `utterance_id` is the sentence it answers, as `/hear` returned it, or "" when it answers none; `by` says who wrote
  it, `claude`, `local` or `ack`. Both are the journal's `say` line for that `say_id`. A device too slow to take an
  event misses it rather than holding the voice back. A comment line every 15 s keeps the stream open. It carries what
  Delta says even when the brain is muted. A device that plays the answers to its own sentences says so with
  `X-Delta-Plays: own`.
- **Security**, from IX's review.
  - The whole hooks address now refuses a request with an `Origin` header, which browsers add, and any Host other than
    127.0.0.1, localhost or ::1, against DNS rebinding.
  - `/hear` and `/said` also need `X-Delta-Remote`: a secret of 32 random bytes the brain writes at each start to
    `%LOCALAPPDATA%\delta\remote.secret`, in the user's profile, compared in constant time. A brain without a secret
    refuses every device. The gateway reads the file at each request, so a new brain needs no restart of it.
  - A sentence from another device is never consent: Claude's instructions have the user confirm at the computer
    anything destructive or irreversible such a sentence asks for.
  - The brain still listens on 127.0.0.1 only: the local network is the gateway's.

## The window that closed by itself (2026-10-22, night)

The live window vanished at 16:35 while the brain went on speaking: `scene.log` ended with an unhandled
`VkResultError: ... vkQueuePresentKHR failed with error: VK_ERROR_OUT_OF_DATE_KHR`. BRUTAL throws Vulkan's errors
rather than returning them, so the frame loop's check for an out-of-date swapchain never ran: a window that changes
size between acquiring an image and presenting it closed the scene. Acquire and present now catch that one error and
rebuild the swapchain. Resizing the window every few milliseconds closed the old scene at the first resize; the new
one rebuilt its swapchain 443 times in 60 s and closed at its time limit.

The same evening, opening a session in the shelf closed the window too: its "Started" row asked .NET for the fr-FR
(later en-US) culture, and the scene runs with `InvariantGlobalization`, where that throws, on a thread-pool thread
that nothing catches. It had done so since the shelf's first version. `Words.Date` and `Words.Number` now write months
and decimal commas themselves, and the selftest checks both languages.

## The triage that fell asleep (2026-10-22, night)

After an hour without a transcript, every sentence went to Claude with `triage: failed: ... context deadline
exceeded`, small talk included, and Delta seemed slow. Ollama had unloaded qwen3:8b after its 30 minutes of
keep_alive; loading it again took 7.4 s, longer than the 4 s a sentence waits, and four sentences in two minutes failed
that way before one found the model loaded. After a failure the brain now loads the model in the background, without a
deadline, one load at a time, so that the next sentence finds it ready.

## Past conversations on the shelf (2026-10-22, night)

The author asked to see « toutes nos sessions Claude et toutes nos conversations » in the scene. The running sessions
were already there; a Conversations section now lists `~\.claude\projects`: 38 projects and 238 transcripts here.

- A project is named by the folder its newest conversation ran in, read from the transcript (`cwd`), since the
  project's own folder name loses its separators. Two folders of the same name show the folder that holds each.
- A transcript reaches 600 MB here, so only its last and first 256 KB are read. The title is the last `custom-title`
  (the user's /rename), else the last `ai-title`, else the first request. The requests are the user's typed lines and
  the sentences spoken to Delta, without tools' results, reminders, commands' output or compaction summaries.
- A conversation opens onto its six last requests, its folder, its last activity, its size and its id; a running one
  in wmux first offers its pane. Opening a project reads at most its 60 newest transcripts.
- "Show the conversations" and « montre-moi nos conversations » open it, and the `shelf` tool's `conversations` move.

## The window comes back (2026-10-22, night)

Twice tonight Delta's window was gone while its brain went on speaking: once crashed, once closed. Nothing brought it
back but a new brain. The brain now starts it again at the next line nobody answers, at most every 10 s, when it
crashed, or when the user asked for it: a `window` or `shelf` request, spoken or from Claude. A window the user closed
stays closed until then, and one that crashed within 30 s of its start (no graphics card, say) is not restarted in a
loop. The log of the window that ended is kept as `scene.prev.log`.

The 0.4.0 review found that a window the brain had not started, found already listening, came back after the user
closed it: the brain could not tell a close from a crash. The window now leaves
`%LOCALAPPDATA%\delta\scene-<port>.closed` when the user closes it (its button or Esc), and removes it when it starts;
the brain does not start a window again while that marker is there, and a request for the window removes it.

## Hearts (2026-10-22, night)

The author asked, through the wmux relay: « quand on dit au robot « love you » ou « je t'aime », il envoie des cœurs en
3D ». A sentence the wake gate lets through that says « je t'aime », « je t'adore », "love you" or "love ya" (and not
« je ne t'aime pas » or "I don't love you") sends `{"hearts":true,"mood":"joy"}` to the window, and is then routed as
any other: the triage or Claude still answers it. Its `heard` line carries `hearts`. Seven hearts of red cubes rise
from the chest one after another, on either side of the face, for four seconds, while Delta smiles.

## Hands, through the camera (2026-10-22, night)

The author asked to bring GA's camera experiments with the hand into Delta. GA's IxHandVoicingLab runs MediaPipe Hands
in the browser, and its hand-pose service is a stub. `hands\delta_hands.py` runs MediaPipe 1.1.1's gesture recognizer
in Python through uv, which the author accepted, asking to keep the dependencies few: two pinned packages, MediaPipe
and the OpenCV it requires anyway. Asked whether Go, Rust or Zig could replace it, I offered a C# port on ONNX Runtime
if Python gets in the way.

- The model's canned poses take the brain's names. Pointing down, which the model does not know, comes from the
  landmarks: the index stretched toward the floor, the three other fingers curled. Swipes and waves come from the
  wrist's path over the last second.
- A pose counts once held still for 0.3 s. The same gesture is not sent again within 1.5 s, nor before the pose is
  dropped. A hand moving shows no pose, so that a wave does not first send an open palm.
- The brain learnt `i_love_you`: hearts and a smile, as the words.
- A test reads `brain/gesture.go` and fails if the process can send a gesture the brain would refuse.
- Measured without a camera: 20 blank frames in 183 ms on the CPU, about 9 ms each, under Python 3.12 and 3.14. The
  model's download matched its SHA-256. A post of `thumbs_up` to the live brain was answered 204; that brain, older,
  answered 400 to `i_love_you`.
- Not yet seen with a real hand: I did not turn the camera on.

## The phone's sentences that went nowhere (2026-10-22, night)

The author talked to Delta from the phone through IX's gateway, 20:13 to 20:15, and Delta answered one sentence of
five. brutal read the journal: the four others had `source` remote and `triage` ignore, so they were dropped, though
the gate had let them through and two of them had sent hearts. The first, « Au revoir. », reached Claude only because
the triage missed its 4 s deadline. Holding the phone's button addresses Delta, so the model's ignore is now overruled
for a sentence from another device, which goes to Claude; the triage still answers its small talk, and fillers and
sentences that call another assistant are still dropped.

## Ears on the graphics card, with a fallback (2026-10-22, night)

The author found Delta slow from the phone. WMUX measured Whisper at 2 to 4 s a sentence when the machine is quiet and
11 to 13 s under load: IX's whisper.cpp b5454 runs on the CPU only, with 16 threads. brutal built whisper.cpp v1.9.5 with
CUDA 13.0 for the RTX 5080 (sm_120) in `C:\tmp\whisper-cuda`, and measured the same 3.5 s clip with Delta's arguments:
72 to 208 ms on the GPU against 1961 to 2128 ms on the CPU, with the same text. WMUX also found 72 NVIDIA driver
incidents and 93 PCIe errors in 30 days, so the GPU build should not run without the CPU one behind it.

The ears now take several servers. They run the first that starts and hears a second of silence, and when the one
running dies, the next takes over and hears the sentence again. `"whisper_fallback"` in `delta.json` names the second.
The journal gets an `ears` line with the server that hears and, if any, why another did not. A test plays three fake
servers, from the test binary itself: one that stops at once, as a GPU build without its libraries, one that dies after
its warm-up, and one that hears. Not switched on yet: that is the author's call.

## A word at once (2026-10-22, night)

With Whisper on the GPU (216 ms) and the triage warm (about 250 ms), Claude's turn is almost all of the wait: 7.5 s
from the phone's test sentence to « Test reçu. ». The author approved, through brutal, a spoken acknowledgement. A
sentence that goes to Claude now gets one of four short words in Delta's language, in turn (« Je regarde. », « Une
seconde. », « Voyons voir. », « Je réfléchis. »), spoken at no cost. Not while Delta speaks, nor after the compaction
notice. The mouth drops it, with an `unsaid` line, when something waits behind it by the time its turn comes, or when
it would start more than 1.5 s late. From the phone, it replaces the chime, which now plays only when Delta answers
nothing at once. The heard line's `ack` ties the sentence to the word; their `speaking` line times the first sound.

## Questions of fact, answered locally (2026-10-22, night)

The author also approved, through brutal, more local answers. `brain/facts.go` gives the triage's model a fact sheet
the brain writes for each question: the day and the time in Delta's language, its version as a voice can read it
(« 0.4.0, a development build 8 commits after it » rather than a git describe), where Whisper runs, the small model,
and one line each on Brickyard and IX. The model answers with JSON constrained to `known`, then `answer`. A rule picks
the questions first, by a question mark or a question word and a word the sheet covers, so that other sentences do not
wait for a second call. An answer that promises work is overruled, as the triage's are.

Measured before writing it, with qwen3:8b warm and the same prompt and schema, on nine questions: answers in 230 to
550 ms (the time, the day, the version, IX, where Whisper runs), and `known` false in 148 to 169 ms for the time in
Tokyo, reviewing a pull request, writing the time into a file, and what was done today. The end-to-end time, from the
end of speech to the first sound, is still to measure live.

## What Delta let pass, on screen (2026-10-22, night)

A sentence the wake gate let pass for want of « Delta » left no trace but the journal, so that a user who forgot the
name could not tell a deaf Delta from one that ignored them. The brain now sends the window `{"ignored": sentence}`,
cut to 200 characters, and the scene shows it for five seconds in a caption centred under the robot: « Pas pour moi ?
Dis « Delta » d'abord. » in bold, then the sentence in quotes, on a dark card with an amber edge, coming in over 0.2 s
and fading over the last 0.8 s. Only for that reason: not a filler, nor a sentence that calls another session, and it
does not bring back a window the user closed. `--demo` ends with the caption, and `--shots` saves `ignored`.

## Which question an answer answers (2026-10-22, night)

brutal proposed it, for Gaia's proof of a remote exchange: a device that asks a question could match the answer only
by timing. Each `/said` event now carries the `utterance_id` it answers and `by`, which the remote takes from the
`say` note itself, journal or not, so that the two cannot disagree. A note whose sentence is never said, such as a
dropped acknowledgement, is forgotten 64 sentences later. Left for later, as brutal noted: ids start again at 1 when
the brain restarts, a device that misses an event cannot tell, and the `channel` route says nothing of when an answer
will come.

## Ears that hang (2026-10-23)

From brutal's review of the fallback. The ears asked whisper-server without a timeout, so a GPU build that stopped
answering, as a graphics driver can, left Delta deaf for good: the fallback ran only when the server's process ended.
A transcription now has 30 s; past that, while another server can take over, the brain kills the one that hung and
hears the sentence with the next. Two more cases from the same review: a sentence that failed while another's failure
was bringing the next server now waits for it and is heard, rather than lost; and the `ears` note names each server
with its own failure, where a chain of servers that could not start gave the first the last one's error.

## The phone's pairing QR (2026-10-23)

The user asked for the phone's pairing link in Delta's window, behind an icon: « une icône et hop ça apparaît ». IX's
gateway posts the link it makes at each start, `{"url":"https://<lan-ip>:8810/#code=XXXX-XXXX"}`, to the brain's new
`POST /link`, with `/hear`'s header and secret. An https link of at most 106 bytes gets 204; anything else gets 400,
without the link repeated. The brain draws its QR code with its own encoder (`brain/qr.go`: byte mode, level M,
versions 1 to 6, the standard library only) and sends the window `{"pairing":{"rows":[…],"seconds":120}}`. The window
shows a phone icon in its top right corner, lit green while the code lives. A click on it, or P, opens a card with the
code black on white, in whole pixels per module within its quiet zone, and how long the code still lives; once it has
expired, the card says so. The code is a credential for those two minutes: the card shows only when asked, since the
window may be shared, and the link reaches no log, nor the journal, whose `pairing` line holds only the seconds.
OpenCV's QR detector read back every mask of versions 3, 4 and 6, a symbol of each version, and the scene's own
`pairing` shot, which `--shots` now saves.

## One room, two devices (2026-10-23)

The user spoke to Delta from a phone in the same room as the computer, and heard each answer twice, a moment apart:
from the computer's speakers and from the phone. An answer to a sentence from another device now plays on that device
alone while a device listening to `/said` sends `X-Delta-Plays: own`, saying that it plays the answers to its own
sentences. The computer stays silent for as long as the sentence lasts, its microphone deaf, and the journal's
`speaking` line says `on_device`. Without that header, the computer says every answer, as in 0.5.0: brutal's review
found that IX's page then in use played only after a tap on its sound button, so a phone with its sound off would have
heard nothing, and the computer nothing either. IX's next page does the other half: it plays only the answers to its own
`/hear`, after the touch that sent it, and sends the header.

The same sentence also reached Claude twice (utterances 37 and 38): the computer's microphone heard it, then the phone
sent it. A sentence whose words, but one in five, match a sentence the other device routed less than 8 s before is
now dropped, with the route `duplicate`, which `/hear` returns to the gateway, and no chime. A copy the wake gate
ignored at the computer does not count, so the phone's copy still goes through; nor does a sentence of fewer than three
words, as two people may each answer « oui ».

Writing the first half showed a bug in `/said` since 0.5.0: only the first sentence of an answer carried its
`utterance_id` and `by`, and the next ones came as if Delta spoke unprompted. The remote now keeps the attribution for
the whole say call.

## Whisper's repetition loop (2026-10-23)

Utterance 24 came from the phone as one question followed by eight copies of « Parfait, il y a un peu plus de
temps. », and utterance 76 with four copies of a sentence: Whisper's decoder loops, as over a button held on silence,
and `-nf` (ears.go) turns off whisper.cpp's temperature fallback, its own guard against loops, because it cost 31.6 s
on a mumble with the CPU build. brutal found it. The router now keeps one copy of a sentence written three times in a
row or more, the same words whatever the punctuation, and the `heard` line says `repeats_collapsed`. `/hear` answers
the same text. Two copies stay, since people repeat themselves. Left to measure: the fallback back on with the CUDA
build only, on a mumble and a long silence.

## A badge when a guest watches (2026-10-23)

brickyard-remote-b2's plan A lets a guest the user invites watch Delta's window from afar, through a tunnel, without
a microphone. So that nobody is seen without knowing it, the gateway posts `POST /guests {"watching": n}`, guests
only, at each change and every 30 s while any watch, with the hooks' header and `X-Delta-Remote`. The window shows
« Un invité regarde », or how many, at its top centre, until 60 s after the last post: a gateway that dies cannot
leave the badge up for ever, nor hide a guest for more than a minute. The journal's `guests` line says each change.

## Two languages, and a button to pick them (2026-10-23)

Looking at the pairing card, the user missed a way to change the language from the window, then asked for the whole
language, voice and hearing included, and for a bilingual mode, French with English for one: Delta answers in the
language it heard, they chose, with both voices, and they agreed to download ljspeech's English voice, whose SHA-256
matched Hugging Face's.

- **The setting.** `"language"` in `delta.json` takes `fr`, `en`, `fr+en` or `en+fr`, Delta's own language first: the
  window's words, and what it says unprompted. The window's button, left of the phone icon, or L, goes through them in
  turn. The window writes `{"language":"fr+en"}` back on the brain's connection, the first line the window ever sends;
  the brain applies it at once, echoes it, and writes it into `delta.json`, keeping the other keys.
- **Hearing.** Measured on the live whisper-server: a request's `language` field overrides `-l`, so a switch needs no
  restart, and `verbose_json` gives `language_probabilities` even when the language is forced. Bilingual, the brain
  asks with `auto` and keeps the likelier of its two; a third language, as Whisper may take a short sentence for, is
  heard again in the likelier of the two. Each heard line and channel event carries `lang`.
- **Speaking.** A say call speaks the language of the utterance it answers, or its new `language` argument; an
  acknowledgement and the window's and shelf's answers speak the sentence's. The other language's Piper voice starts
  at the switch, or at its first sentence after a start, from beside the first voice; a missing one is journaled once, and Delta says it in its first voice.
- **Claude's instructions** now say that each event's `lang` names its language and to answer in it, since the
  instructions reach Claude once, at the start, and the language can change after.

Not done: the triage's small talk and the persona still answer in Delta's own language, and the compaction notice too.

The user then asked, from the phone, for a way to mute the microphone. A button left of the language button sends
`{"mic":false}` or `{"mic":true}` the same way; the brain's microphone loop then drops what it hears, as while Delta
speaks, echoes the state to the window, which strikes the microphone through in red, and journals `mic`. It is not
saved: a new brain hears again. `/hear` from a phone is not muted, which is the point of muting the computer.

Live, from the phone, the bilingual mode heard French sentences as English (« Delta, c'est un drôle, tu ranné. »,
lang en) and Delta answered them in English; the user asked « En français, s'il te plaît » four times, and could not
click the button from the phone. Delta now changes its languages when asked aloud (languageCommand, lang.go): a
short sentence that names a language and asks for it, « continue en français », "speak English only", « passe en mode
bilingue ». Asking for a language puts it first and keeps the other one heard; asking for the one already first, or
for one only (« seulement », « ne … qu'en », "only"), keeps it alone. A sentence that says no (« ne parle plus
anglais », "stop speaking English") or only speaks about a language (« tu es bilingue ? ») goes to Claude, which has a
new language tool. The window's button, the voice and the tool share one change (mcpServer.relanguage), journaled
`language` with `by` window, voice or claude.

## A conversation that lasts while it goes on (2026-10-23)

The user forgot « Delta » and could not tell what would happen: from utterance 58 on, a dozen sentences went
unheard, among them requests, because the conversation had ended without a sign. They asked for a lease: 30 s,
renewed each time they talk to Delta, and visible. The wake gate's follow-up and answer windows are now 30 s each,
where they were 15 and 20, and a sentence let through in the conversation renews it too, where before only « Delta »
or Delta's own words did. The brain sends the window `{"lease": seconds}` at each change, and the window shows a green
ring in its bottom left corner: « Je t'écoute » beside twelve green dots that go out one by one, clockwise, until
Delta needs its name again. A first ring, drawn again four times a second with its seconds, stalled the frames: p99
27 ms and max 55 ms at 60 fps, against 18 and 22 without it, since each upload waits for the GPU. brutal asked for p99
within 2 ms of the ring-less build and max under 33 ms. The card is now drawn once for the window's size and language,
and the dots are one small texture drawn twelve times, tinted lit or dim each frame: in alternating 20 s runs, p99
17.3 to 17.6 ms and max 20.0 to 20.4 with the ring, against 17.3 to 17.9 and 19.9 to 21.9 without. A sentence for
someone else, Brutal or Gaïa among them, still ends the conversation at once, and the ring with it. The cost is the
one the old rule avoided: talk that goes on and on goes on to Claude, now in sight.

## The louder copy goes (2026-10-23)

The six live pairs (pid 117856, utterances 17/18 to 38/39) came 0.16 to 0.48 s apart, from the headset and the
phone, each misheard its own way; half came phone first. Brutal's review set the rule: one speaker in one room, so time
decides and words only check.

- **Two sentences from different devices within 1.5 s (copiesWithin) are one** if they share a word once accents are
  folded and numbers up to ten written in letters (« 1 » and « un »). The 8 s rule, 4 words in 5, stays for a phone
  copy that comes later.
- **A copy that comes while the other device is still hearing waits for that one**, at most 2 s (holdMax). The brain
  counts the sentences each device's ears are transcribing (inEars): the computer's from the endpoint's start of speech
  until the sentence is routed, a phone's from `/hear`'s arrival until its transcript is routed. Nobody waits while
  only one device hears. A count that never comes down is reset when its wait runs out.
- **The louder copy goes**, by a tenth at least: its RMS over the voiced frames (voicedLevel), since the microphone
  closer to the mouth hears it louder. Then the longer transcript, then the first. The answer then plays where the
  user spoke: on the phone when its copy went and it plays its own answers, on the computer otherwise.
- **The journal says why.** Each heard line has its `level`. The dropped copy has route `duplicate`, the reason « heard
  louder by the other device », and `copy_of` with the copy that went: its source, level and text. The copy that went
  has `beat` with the dropped one.
- When the computer's copy, said without « Delta », beats the phone's, it still goes: the phone's button addressed
  Delta.

Not known yet: whether a phone's automatic gain makes its copy louder when the user speaks into the headset. The levels
in the journal will tell, in the live test with brutal: the same sentence on both, the phone alone, the headset alone.
## The robot's look, a first level (2026-10-23)

The user asked for a better-looking robot that falls back on weaker machines. The scene now has two lighting levels,
picked at start by `--quality auto|flat|low`, and says which and why: `SCENE quality low because NVIDIA GeForce RTX
5080 is a discrete GPU, with 4x MSAA`. Auto takes Low on any GPU and Flat, the lighting as it was, when the CPU draws,
as a software renderer does. Low keeps Flat's three lights, its pool and its colours, and adds:

- rounded edges: within 8 mm of a face's edge, less on thin parts and never under a pixel, the normal leans towards
  the next face, so that the Mac minis, the head and the bricks catch the light on their edges. Each vertex carries
  its face's first axis and where it lies on the face, 68 bytes a vertex where it was 36;
- a material per surface (enamel, brushed aluminium, steel, rubber, glass, matte), with Blinn-Phong highlights of both
  lights, Schlick's Fresnel and a reflection of the hall: lit floor below, brick around, dark roof above;
- the robot's shadow on the floor from the key light, as a column 1.25 m high whose penumbra widens away from its foot,
  and the dark where the robot, the pallet, the cabinet and the wall meet the floor;
- a shoulder above 0.6, which leaves the darks and the colours tuned without a curve as they were and rolls the
  highlights off towards 1; the glow is added after it, and the cards, the buttons and the splash never pass through it;
- 4x MSAA resolved into the swapchain's image, so the edges no longer step, and `--shots` reads the resolved image.

The camera's position comes in a uniform buffer per frame in flight, in its own descriptor pool, since the push
constants are full at 128 bytes. A demo's window is now titled « Brickyard demo »: one titled « Brickyard » beside
Delta's left the tablet with no picture at 19:41, since the gateway shows the one window of that title.

Measured on the RTX 5080, alternating with the build before, `--demo --hidden --cap 60 --seconds 20`: p99 17.3 to 19.2
ms for the old build, 18.4 to 20.3 for Low and 18.0 to 19.2 for Flat, the max 21.5 to 26.3 for all three, apart from
two runs, one old and one Low, with a stall of 196 and 78 ms each. Uncapped, 2143 frames a second before and 2017
with Low, 0.37 against 0.43 ms at p50. Visible with vsync, beside Delta's own window, all three builds show p95 33.3 ms
and max about 50 ms: frames that miss a vsync came before this change. `--shots` before and after: the splash is the
same to the pixel, and the cards and the QR stay sharp.

## The agents' office, and widgets (2026-10-29)

The user asked for an office of agents beside the robot: one desk per Claude Code session, its robot typing while the
session works, a red beacon while it waits for an answer, a jump when it is done, and a click that opens the session's
wmux pane. The idea comes from agent-office (AgentSystemLabs, MIT); none of its code or assets is used. The model of
composition follows GA's Lego demo, ideas only. They asked as well that a scene module can declare 3D widgets, nested
or flat, composed fractally: `docs/widgets.md` describes the contract (`module-contracts/Widgets.cs`), the stage that
draws it, hot reload and the office module (`modules/AgentOffice`). `--office-module <dll>` loads it; without it the
scene is as before.

The office reads `~/.claude/sessions` and one `wmux agent-state` every 3 s on the thread pool, skips a read while the
last one waits, and writes nothing. Flat quality shows a board with a lamp and a name per session instead of desks.

Measured, isolated, `--demo --hidden --cap 10`, the RTX 5080:

- Low, 26 s, v0.2 of the module copied over v0.1 at 12 s: 192 instances in 19 meshes, the reload logged at about
  12 s, 38 meshes built and 38 freed (19 at the reload, 19 at shutdown), 0 native mesh buffers left, exit 0. The splash
  is unchanged.
- Flat, 14 s: 13 instances in 8 meshes, 0 buffers left, exit 0.
- `widget-tests`: 43 checks pass, the reload from v0.1 to v0.2 and the old context collected among them.

## Gaia V1.2's planet, loaded by the host (2026-10-29)

The user asked to prepare Gaia V1.2. Codex's prototype (`docs/gaia-v12-planet-prototype.md`) made it a static planet of
7680 triangles behind an optional contract, `ISceneTriangleModule`, which the scene could not load. The host now can,
as the prototype's « Mise à niveau hôte minimale » asks:

- the contract only gains types, so the Pong and office images need no rebuild;
- `LoadedSceneModule.RenderTriangles` copies a module's triangles, then checks the copy, at most 8192;
- the host carries them to its surfaces; a host built with the old two-argument factory refuses a module that gives
  triangles rather than drawing it without them;
- `SceneModuleGeometry` appends them, matte, to the module's mesh, with an edge of (0, 0, 1, 1) and a side of the
  triangle as tangent, so that the Low shader neither rounds them nor normalizes a null vector;
- the preparation card goes once the prepared version is the active one.

Measured, isolated, with V1.1 as the initial image and V1.2 offered, `--demo --hidden --cap 10 --view 3
--module-click-test`: the simulated click replaces 1.1 with 1.2 in 22 ms in Low and 23 ms in Flat, the retired context
is collected, 0 native mesh buffers are left at shutdown, exit 0. The planet shows in both, with no shading fault.
`module-tests` with V1.2's image: 73 checks, of them V1.1 -> V1.2 -> V1.1 -> V1.2 on one host and the old host's
refusal; 62 without it. `module-mesh-tests`: 23.

Nothing is installed in a live `modules/gaia`, and V1.2 is activated only by the user's click, then validated by
another. The window must restart to take the new host: the surface factory is built in `Main`.

## A validation kept on the machine (2026-10-30)

The user did not want to activate and validate Gaia's version again at every start: once a version passes its checks
and they validate it, it should stay. The validation was only held in memory, and the scene always started on
`initial/`. Now the second click also writes the exact bytes the code was loaded from, and a record of its version
and hash, into `%LOCALAPPDATA%\delta\modules\gaia\validated\` (`<module-dir>\validated` under `--module-dir`, so that
proofs never touch the user's own). Each file is written whole and then moved into place. At the next start the host
loads that image instead of the initial one when it is newer, after the same checks as a click: the hash and
version against the record, the identity, the update, the frame and its triangles. Any refusal starts on the initial
code, as before. The scene starts in green, validated, with the initial version offered as the return. An explicit
return forgets the record; activating a newer version without validating it leaves the last validation in place.

Measured: `module-tests` 83 checks, 9 of them on this; isolated Vulkan, `--demo --module-dir ... --view 3` with
V1.0 initial and V1.2 kept: the window starts on V1.2, planet shown, return to V1.0 offered, no click.

## The Observatory's camera, cinematic and wheel (2026-10-30)

The user asked for the Observatory's camera moves in the hall: its keys and its mouse, its cinematic and its weapon
wheel, left empty for now. Ported from `observatory-prototype-v1/scripts` (`walk_physics.gd`, `view_move.gd`,
`cinematic.gd`, `curio_wheel.gd`), with the Observatory's heights scaled by its eye's, 1.7 m, to this hall's, 1.1 m,
and its times kept:

- `FreeCamera` walks a body: inertia, Shift sprints (1.6 times), Space jumps 0.55 m for 0.65 s, C or Ctrl tapped alone
  crouches, C while sprinting slides and ends crouched, Q and E (A and E on AZERTY) lean, Alt with the moving keys
  leans or peeks with the body standing, Caps Lock walks at half pace; a landing dips the eye, a sprint widens the field
  6°, a slide 8.4° and rolls it. F still flies. The views 1 to 5 are flights after GTA V's character switch, 1.4 to
  2.4 s, lifting, looking down 26° and widening 9° on the way; under `--reduced-motion`, cuts.
- Ctrl and a click lock the view to the mouse, the Observatory's click, since a plain click is the desks' and Gaia's;
  locked, a dot marks the centre and a click acts there. Ctrl and a click again, the right button or a lost focus free
  it. Esc never moves the camera: it shuts the open wheel, ends the cinematic, or closes the window as before.
- `Cinematic`, key 0 or `--cinematic`: bars to 2.39:1, the title over black, seven shots (the hall, the office, Delta's
  face, the Pong, Gaia's planet, the clocks, a pull back to the whole hall) in 51 s with an operator's sway and dips
  between acts, the title again, then the player's view back. Any key or click ends it through black.
- Shift and a view's key (asked for on 2026-10-30) film that view as the cinematic's shot of it, alone: 1 Delta's face,
  2 the office, 3 Gaia, 4 the hall, 5 the clocks. The bars come, the shot comes out of black with no title, and through
  black the player stands at the view, cut there behind the black; a skip before the black leaves the camera where it
  was. `--film-view N` does it once the splash is over, for captures; one at `--film-view 3` showed Gaia's shot between
  bars, then the player at view 3.
- `ToolWheel`, Tab: held, the scene's time (the robot, the Pong, the desks) goes at a quarter and the wheel opens; let go,
  it shuts on what it points at; a tap leaves it open. Eight empty slots for now; `Items` takes things to hold.
- The shelf, which Tab opened too, keeps M. F1 shows the keys.

Measured, isolated (`--demo --hidden`, own ports, V1.2's planet and the office's demo sessions): the scene's selftest
(Debug and Release) with the new checks, among them the jump's height and time, the slide, the lean, a flight's length,
lift and landing, the cinematic's order, length and skip, and the wheel's tap and hold; `module-tests` 73,
`widget-tests` passed. Captures in Low of a flight half-way from view 4 to view 2, the title, every shot between bars,
and the open wheel; in Flat the title and the wheel. The Pong's shot shows the chest without the Pong in these
captures: the isolated bin had no arcade module.

## The picture's latency (2026-10-30)

The user found the hall slow to answer a move. Live it ran FIFO with 3 swapchain images and 2 frames in flight at
3840x2054: 60 fps, p50 16.66 ms but p95 33.2 ms, max 1.1 s, and the keys and the mouse were read before the wait for
the GPU, so a move reached the screen three or four refreshes later. Drawing is not the cost: hidden, `--immediate --cap 0
--size 3840x2054`, Release in Low with 4x MSAA 425 fps (p50 2.3 ms, p99 4.4), Flat 593, Debug 501, and with the live
bin's modules (the office, Gaia v1.2, the Pong) 1518 fps (p50 0.9, p99 2.9). So:

- Shown, the scene presents through Mailbox when the GPU offers it (no tearing, the newest picture rather than a queue),
  paced at twice the refresh of the screen the window is on, read again every 2 s as the window is placed or moved
  (`SCENE paced at N fps`), unless `--cap` says otherwise: at the refresh itself the pacing drifts against the
  screen's and a picture now and then shows twice. `--fifo` keeps FIFO, and a hidden window keeps FIFO at 60 as before.
- Each frame waits for its slot's fence before polling the keys and the mouse, not after.
- `--size WxH` sizes the window, for measuring a hidden one at a live one's size.

Live after the merge (2026-10-31, 02:05) the window ran Mailbox capped at 120, and the user saw the picture jerk. Mailbox
drew at twice the refresh against the screen's clock, so the picture each refresh showed was by turns fresh and 8 ms old;
the CPU's pace was even (hidden, `--immediate --cap 120`, Release and Debug p99 9 ms). Now, shown, the scene presents
through FIFO and each frame waits for the last to be on screen (`VK_KHR_present_id` and `VK_KHR_present_wait`, which the
RTX 5080 offers) before reading the keys and the mouse: a new picture every refresh, an even step, one refresh from the
keys to the screen. Mailbox stays where the GPU lacks them, and `--mailbox` takes it; `--fifo` keeps plain FIFO. At exit
`SCENE keys to screen` gives the time from reading the keys to the picture on screen. Hidden at 3840x2054 with
`--present-wait --cap 0`: Release 59 fps, frame p95 18.0 ms, keys to screen p50 16.5 p99 18.1; Debug 60 fps, p95 18.0,
keys to screen p50 16.5 p99 18.1, where Debug in plain FIFO had fallen to 53 fps with p95 49 ms.

## An arcade cabinet, and PARSEC on its screen (2026-10-31)

The user asked for PARSEC (https://parsec.games.bu.app/, a third party's tribute to the TI-99/4A game) on an arcade
screen in the hall, from its site, without copying it. The page is one HTML file drawing a 256x192 `#cv` canvas, with
WebAudio, `speechSynthesis` and keys read by `e.code`.

- `WebScreen` starts Edge, already on the machine, in its own profile (`%LOCALAPPDATA%\delta\arcade-edge`) as an app
  window at -32000,-32000 with the occlusion and background throttling off, so that the page draws and times as shown
  and its sound and voice play. It drives the page over the DevTools protocol on a 127.0.0.1 port Edge picks
  (`DevToolsActivePort`); a script the scene puts in the page sends the canvas's RGBA through a binding each animation
  frame (60 a second, about 260 kB of base64 each), and keys and clicks go back as `Input` events. A job object kills
  Edge with the scene, however the scene ends. The script is put in again every 250 ms until frames arrive: a script
  added for new documents does not reach the one already loading.
- `ArcadeCabinet` is the cabinet: the classic side profile with a lit trim and three bands, the coin door's lit slots,
  a stick and three buttons, the screen leaning back 13.5 degrees with scanlines, and the lit marquee; at (3.2, 0,
  -1.95), turned 25 degrees to the room, clear of the steel cabinet and the pillar. Before the game its screen shows a
  card: PARSEC and "click to play", "loading...", or why it could not start.
- A click on the cabinet (or, locked, at the centre) flies to view 6, in front of it, and starts the game: Edge in about
  6 s, then the page's own start is clicked, which also lets it play sound. Every key but Esc is then the game's, by
  its place (ZQSD on AZERTY are its WASD); the camera does not move. Esc leaves, and Edge closes on the thread pool.
  `ScreenGpuRenderer` takes any fixed size, rectangle and scanlines now, and `Texture` animates any fixed size.

Measured: the probe, Edge alone, 60 frames a second with the game started and moved by keys; hidden in the scene with
`--arcade-play`, the card, "loading...", then the page's title screen on the cabinet, 289 frames, and no Edge left
after the run. Selftest ok.

### Without a browser

The user then asked that the game depend on no browser, Edge or other, and chose an interpreter in the scene. Jint
4.17.0 (BSD-2-Clause, with Acornima 1.7.0, BSD-3-Clause; the texts in `LICENSES\`, since the packages name their
licences only) replaces Edge, and `WebScreen` is gone:

- `ScriptScreen` fetches the page each time the game starts, takes its inline script and runs it on a thread of its
  own, 60 animation frames a second, with a 2 s limit per call, 512 MB and a recursion limit. The script sees only a
  shim of the browser written in JavaScript over a few host functions: elements by id with styles and listeners,
  canvases (`fillRect`, `clearRect`, `drawImage` of another canvas, nearest pixel, `alpha: false`), CSS colours as
  `#rgb`, `#rrggbb`, `#rrggbbaa` and `rgb()`/`rgba()`, `keydown`/`keyup` by `code` with `preventDefault`, a click,
  `setTimeout`/`setInterval`, `requestAnimationFrame`, `matchMedia` (not coarse), and a `localStorage` kept in
  `%LOCALAPPDATA%\delta\arcade-storage.json` (64 kB at most). It has no network, no files and no .NET of its own. A
  frame that throws is skipped and logged once a second; 600 in a row end the game, and its card says why.
- `ScriptAudio` plays the Web Audio the game uses through `waveOut`, 44.1 kHz mono, four 512-sample blocks ahead
  (46 ms): oscillators (sine, square, sawtooth, triangle) and sample buffers, through gains and biquads (RBJ lowpass,
  highpass, bandpass), with `setValueAtTime` and linear and exponential ramps. `currentTime` is the samples mixed.
- `ScriptSpeech` is `speechSynthesis` through SAPI's `SpVoice`, by COM on an STA thread: Windows' voices (David and
  Zira here), the page's rate, pitch (SAPI XML) and volume, one utterance after another, each one's `onend` fired so
  the game's queue goes on; `cancel()` purges. The scene runs with invariant globalization, so voices' languages come
  from their LCID by a small table.
- Sound and voice open only when the page first uses them, so the selftest, on a small page of our own (a sprite over
  an opaque canvas, a key, a click, a timer and the storage), opens neither.

Measured: the probe played the real page, 60.5 frames a second, ship flown and firing by keys, score 100, the
messages of the game; the speech's end events arrive, also after a `cancel()`; the sound's clock runs. Hidden in the
scene with `--arcade-play`, the page's title screen on the cabinet, 685 frames in about 11 s. Selftest ok, and fails
when one of its pixels is changed.

### A film of a robot at the cabinet

The user asked for a 45 s video, in the hall's two GTA manners (a view's flight after the character switch, and the
cut scene), of a small robot playing PARSEC on the cabinet with the screen never hidden, and good progress in the game.
`--film-arcade DIR` (with `--hidden --size 1920x1080 --lang fr --listen <port>`) makes it:

- `ArcadeFilm` directs it: the whole hall, a flight down to a view high on the cabinet's right, a cut scene between
  bars titled PARSEC (approach, the screen over the robot's shoulder, the hands, from the marquee down, a half orbit),
  and a flight back out to the hall. Shots on the cabinet's left are behind the steel cabinet, so they keep to its right
  and above. `Cinematic` takes another film's shots and heading; `FreeCamera.GoTo` flies to any eye and target.
- The robot is Delta's, at 0.6 of its size, on two crates so that its hands reach the panel: Robot.Pose, then its arms
  turned to reach the stick and the buttons, which move with the keys held, and its head tilted up to the screen. At
  full size its head hid the screen's lower half from every shot.
- The game runs stepped (`ScriptScreen.Step`), two frames of the page for each of the film's at 30 a second, played by
  `ParsecPilot.js` (built into the scene), which reads the page's state and presses keys only. The scene's time is the
  frames' while filming, so every frame is recorded however long it takes; the buttons and cards of the window are not
  drawn. Its sound is mixed offline as the steps go (`ScriptAudio.Pull`), its voice spoken into WAV files by SAPI and
  mixed in, each utterance ending when its samples have played; FFmpeg encodes the frames (x264, CRF 17) and then puts
  the sound (AAC) with them into `DIR\parsec-film.mp4`.
- In 45 s the pilot loses no ship and scores 5200 (an extra ship at 5000), reaching the sixth and last wave of level 1.
  The game sends its enemies on fixed timers, so the pace is the game's: a level lasts about 65 s and fuel runs low only
  after about 2 min. The game's random sequence restarts with each game, so the same film comes out each time.

## Where a slow frame's time goes (2026-10-31)

The live window logged `swapchain rebuilt at 1280x720 in 7 ms`, then `slow frame 2744 ms`, just after being maximised at
3840x2054: the rebuild was not the wait. A slow frame's line now splits its time (`FrameGap`): the event pump
(`Glfw.PollEvents`), a minimised or hidden window's sleep, the swapchain's rebuild, and the rest, with what the window did
meanwhile (new framebuffer sizes, moves, minimise, maximise, requests to redraw) and when. A gap with a minimised or hidden
spell in it is logged as a `pause`, and left out of the frame times, where a long minimise used to count as one frame.

`--modal-drill` measures it on a window of its own: it posts to its own window the keyboard's move and size commands,
which enter Windows' modal loops as a hand on the title bar or the border does, and leaves them with Enter. Hidden, at
3840x2054: the title bar held 1.5 s gave a 1519 ms frame, 1502 ms of it in one `PollEvents` call; the border dragged 1.5 s
a 1531 ms frame, 1506 ms in the pump and 8 ms rebuilding, its sizes changing inside the call; a jump to 1280x720 outside
those loops, a 7 ms rebuild and no slow frame. Windows runs those loops inside the event pump, so the render loop stands
still as long as the hand holds. Dragging a maximised window by its title bar restores it (here to 1280x720) and holds
that loop until let go, which matches the live line; the live log had no split to show it was that gesture.

The hall now goes on moving in those loops: while the pump is held a frame's time (15 ms), a timer's ticks (`PumpTimer`,
10 ms, which Windows calls from inside the modal loop) and the window's move, size and refresh callbacks draw a frame
(`Frame(false)`), and each frame's split counts only the pump's time since the frame before it. The move and size
callbacks alone were not enough: a hand held still, as the drill's, sends none. The pace counts from each such frame's
start, so they come every 16 ms as the others do (from its end, they came every 30 ms), and the window's placement
(a minimise or a move the brain asked for) waits until the hand lets go. The drill again, hidden: no slow frame, 59
frames a second through the holds, p95 17.3 ms, max 47 ms (1565 ms before); a run without the drill, none drawn aside,
p95 17 ms. Not yet tried by hand on the live window, nor measured on a shown window under DWM.

## The hall on its own: a tour, a robot at the cabinet, the game on the wall (2026-10-31)

The user asked that, while nobody moves the camera, the hall chain GTA's camera flights by itself (the camera's manner,
not the film's bars), that a small robot play PARSEC at the cabinet and get far in it, and that the game be shown on the
brick wall whenever someone, a person or the robot, plays.

- `IdleTour`: 25 s after the last key, click, wheel or look, the camera flies by `FreeCamera.GoTo` from stop to stop,
  8 s at each after the landing: Delta, the whole hall, the robot over its shoulder at the cabinet and the wall, those
  two while anyone plays, Gaia, the agents' office. Any key, click or wheel gives the camera back where it is; the shelf,
  a card, the wheel, a cinematic or a person's game hold it off. Under `--reduced-motion` the flights are cuts.
- The robot (`ArcadePlayer`, taken out of `ArcadeFilm`, which still uses it) takes the cabinet after the splash while
  nobody plays: `ArcadeStage.PlayRobot` runs the game live on its own thread, muted (`ScriptScreen`'s `muted`: the sound
  mixed offline and dropped, the voice timed and not spoken, so nothing reaches the microphone), without the person's
  stored scores, played by `ParsecPilot.js`. A click on the cabinet steps the robot down for the person's game; Esc
  gives the cabinet back to the robot 15 s later. A game that ends or cannot start is tried again a minute later.
- The wall: a 1.04 by 0.78 m screen in a dark frame between the clocks of Ottawa and Paris, fed the cabinet's frames,
  shown only while a game runs, whole in the view in front of Delta.
- `--no-tour` and `--no-arcade-robot` leave them out, as `--demo` and films do; `--tour-after S` starts the tour sooner;
  `--peek DIR` with `--seconds` saves a PNG every 4 s of a plain run. Hidden, 1280x720, `--tour-after 6`, 52 s: the
  robot's game ran from the splash, the tour went Delta, hall, cabinet, wall, Gaia; frame p95 17 ms, max 33 ms.

## The agents' office by workspace, and a selection (2026-10-31)

Gaia's UX handoff (BRICKYARD-UX-HANDOFF-REFERENCE.md) asked for a first slice: one desk per session, never per pane;
a stopped session kept in view; distinct names; desks grouped by workspace; a selection that stays in the scene. The
contract grew in place: `OfficeActivity` gained Stopped, Stale and Unknown after Done, `OfficeSession` a workspace and a
start, `OfficeState` the selected session, all with defaults so that the v0.1 test image still loads.

- `OfficeActivities.Merge` reads the session files (dead processes included) with wmux's agent states and providers,
  and the workspaces of their panes; `Derive` keeps each session as last shown and turns those that went into Stopped.
- `OfficeSelection` holds the selection; Program.cs passes it in `OfficeState.Selected` and checks it each frame.
- The module draws an enclosure per workspace and the selection's mark, both from poses and IDs.

Checked: widget tests (seven mutations each fail them) and the scene's selftest. Not checked live: the office module
was not reloaded in the running window. `SurfaceWorkspaces` costs a wmux call per workspace, about 5 s with nine
workspaces, once every 30 s, during which the activities wait. Report: docs/office-ux-slice1-report.md.

## A haze in the hall (2026-11-03)

After the RTX proposal, the user chose its cheapest first step: the reference picture he sent, a city in a cold blue
haze, owes its depth to aerial perspective far more than to traced rays. The Low lighting now draws a haze: none in the
first 3 m from the eye, then 0.12 per metre, half as thick 2 m up, towards a dim steel blue (0.075, 0.09, 0.115), which
is also the clear colour beyond the hall, so that the hall's open side dissolves into it instead of ending in black. B
turns it off and on, --no-fog starts without it; Flat is unchanged. In captures at 1920x1080, view 1 (Delta) keeps the
robot as it was and cools the far wall; view 4 sends the far floor and the open side into the haze. Hidden, capped at
60 fps, the frame times did not move (p95 17.0 ms); its GPU cost was not measured on its own. Seen live on 2026-11-04,
the user found it too thick: halved to 0.06 per metre, about half the light through at 15 m instead of 9 m.

The same day the agents' office was missing from the live window: since the third build of 2026-11-01 the scene had
refused it (TargetInvocationException), because the installed image in `bin/…/modules/office` was built on 2026-10-29,
before 9c42440 changed the contract's `OfficeSession` and `OfficeState` constructors. Rebuilt from the source with
`dotnet build modules/AgentOffice/AgentOffice.Module.csproj -c Release -o <that dir>` (widget tests 62 ok on it).
Nothing rebuilds that image when the contract changes: a stale image shows only as a refused line in scene.log.

## The office's wmux readings, kept through failures (2026-11-06)

The user saw the office's robots change between two looks by themselves. The live scene.log, after the 228e865
install, has its tree alternating between 323 instances and 32 meshes and 316 and 25, about every 45 s, with no
click. An office reading whose workspaces come back empty, 12 sessions in 8 workspaces, gives exactly that
difference in widget-tests: the 8 enclosures fold into the one without a pane, and every robot moves and changes
scale. The office used to take a failed wmux call (no answer in 5 s, an unreadable one) for an empty answer. That
held for the workspaces, read every 30 s, and for the agents' states, read every 3 s, where a failure also dropped
every pane and the sessions only wmux names.

`KeptReading` now keeps each of those readings through failures. It ages what it keeps and logs it once
(« workspaces unread N time(s); keeping those wmux gave N s ago »), then once more when wmux answers again. An
answer replaces the reading as it is, empty or not (77b0087, 51111fe). After an independent review, the decoding
(`Shelf.Wmux.DecodeAgentStates`, `DecodeWorkspaces`) refuses an answer as a failed reading, not an empty one,
when:

- its root is not an object;
- it does not hold its array;
- a record is not an object;
- a workspace or a pane has no ID.

An empty array is still an answer. The office's reading is now as old as its oldest kept part, so the wall's card no
longer says « live » over what wmux gave 30 s ago; it is not read yet while a part was never answered.

Red then green in the scene's selftest, through `JsonDocument.Parse` on made-up answers. The old decoding failed 5
checks: a non-object root threw, records without an ID were accepted, an invalid answer emptied the cache, the card
said live over a kept part, and a part never read had an age.

Whether live wmux fails this way is not proven: the WMUX access had expired, and no wmux answer was read.

From 381425f each office tree's log line also names the roots it added and removed, such as « (+w-… -w-…) », or
« same roots »: one line names the enclosure that comes and goes.

## Updating the live window, scene only (2026-11-06)

`dev-deploy.ps1` rebuilds the brain too and needs delta reconnected in `/mcp`. A change to `scene/` or to a scene
module alone has a shorter route, used for 228e865 on 2026-11-05:

1. **Where the window runs.** A brain deployed with `-HotReload` runs `-scene-source <checkout>\scene`, so the
   window runs under `dotnet watch` on that checkout (brain/voicemcp.go, `launch`). Check it before anything:
   - the brain's command line names `-scene-source`;
   - its child is `dotnet watch … --project <checkout>\scene`;
   - scene.exe is under `<checkout>\scene\bin\Debug`.
2. **The source.** Run `git -C <checkout> merge --ff-only <reviewed SHA>`, by SHA rather than by branch name. Then
   `dotnet watch` rebuilds in Debug. It applies a hot reload, or, for a change it cannot apply live (new fields or
   types), restarts the window: a new scene.exe on the same port. The brain stays, so there is no `/mcp` reconnect,
   and the journal has no new `start`.
3. **The office module.** Nothing rebuilds it: build it into the window's module folder with
   `dotnet build modules/AgentOffice/AgentOffice.Module.csproj -c Release -p:Version=0.1.0 -o <checkout>\scene\bin\Debug\net10.0\win-x64\modules\office`,
   or copy a reviewed image's `AgentOffice.Module.dll`, `.pdb` and `.deps.json` there. The scene reloads it once
   the file holds still. `pwsh .\inspect-module.ps1 <image>` reads, without loading it, the commit an image was
   built at, its sources' SHA256 and the contracts it was compiled against.
4. **Checks.** scene.log, in the brain's `-home` folder, shows the watch's build, then « WIDGETS agent-office
   reloaded … sha256 <the image> », then « tree: … » with no « refused » line after it. Only one scene.exe
   listens on the port.
5. **Before**, copy the module folder aside. **Rollback:** `git -C <checkout> reset --keep <previous SHA>` (the
   window restarts again), then put the module folder back.

A tree refused between steps 2 and 3, the new scene with the old module, is expected, and step 3 ends it.

## whisper-server's routes under a path only the brain knows (2026-11-11)

whisper.cpp's server answers every request with `Access-Control-Allow-Origin: *` and checks neither `Origin` nor
`Host` (`examples/server/server.cpp`, `set_default_headers`), unlike the brain's own port, which `localOnly` keeps. A
web page reaches its loopback port, random but scannable. It could post audio to `/inference` and read the
transcript, a « simple » CORS request with no preflight. It could also post a file's path to `/load`. That route tells
whether the file exists. On a file that is no model, it frees the context and calls `exit(1)`, leaving Delta deaf.
IxForge QA saw the headers on a throwaway instance of the same binary.

The brain now starts it with `--request-path /<32 hex digits>`, drawn at each start (`earsPath`). Every route sits
under that path, `/load` and `/health` included, and a request outside it gets 404. `TestTheEarsRoutesAreUnguessable`
covers it with the test binary's fake server. Both real builds were checked on throwaway instances, with no audio and
not the live one: IX's b5454, which `setup.ps1` installs, and the CUDA build. Each answered `<path>/health` 200, and
404 to `/inference`, `/load`, `/health` and `/` at the root, and kept running after a `POST /load` at the root. The
`Access-Control-Allow-Origin: *` header stays; only the routes are out of reach.

The path is a secret, and it travelled at first. Go's request errors name the whole URL, so the reason for falling
back, the failed servers, the journal's `ears` notes, stderr and `/hear`'s 502 all carried it. So did the ready line.
They now give the address and `/…` (`hidePath`), and `TestTheEarsPathStaysOutOfTheirMessages` reads each one. The
test's fake whisper-server takes only the brain's arguments and exits 0 on any other, as whisper.cpp does. A build too
old for `--request-path` is therefore named as such (`TestAnEarsServerWithoutRequestPathIsNamed`), not as « exit status
0 ». `TestTheRealWhisperServerKeepsItsRoutesUnderThePath` runs the same checks against a real build when
`BRAIN_REAL_WHISPER` and `BRAIN_REAL_WHISPER_MODEL` name it. It uses the brain's own arguments (`earsArgs`), sends no
audio and never writes the path. It passed on b5454 and on the CUDA build, and failed with the flag left out.

To verify on the live brain once deployed and delta reconnected: `.\check-ears-flag.ps1` says, for each
whisper-server, `--request-path present (32 hex digits)`, `absent` or `present but not 32 hex digits`, and never
prints the path. Its exit is 0 only if each has it. Do not read the command line by hand: it shows the path. Then
check that Delta still hears. `check.ps1` runs `check-ears-flag.ps1 -SelfTest`, which fails if a verdict prints the
path.

## The window's link refuses a web page, and the window says when its memory climbs (2026-11-12)

On fix/link-first-line, after the ears' path (e928fcb), the last loopback port open to web pages is closed:

- The link on 127.0.0.1:8805 took any JSON line from any connection. A page's `fetch(..., {mode: "no-cors"})` posting
  to it had its body's lines applied, a pairing QR code and a caption in Delta's name among them (the selftest showed
  both before the fix). A connection is now dropped unless its first line is a JSON object, which a browser's request
  line never is; only then does it take the window's answers, so a probe that connects and says nothing, as
  start-delta.ps1's Test-Port and a second brain do, no longer cuts the brain's back channel. The brain on 8807 already
  refused any request with an Origin; whisper-server answers only under its path. Not tried from a real browser
  against the live window.
- MemoryWatch writes `SCENE memory at …`, then `SCENE memory peak: … between A s and B s` or `SCENE memory grows: …`
  when the working set's peak or the private bytes pass the last line's by a quarter and 256 MB. The live window of
  2026-11-10 peaked at 4.6 GB of working set over a day with 460 MB private, across a 7-hour sleep of the computer;
  the next peak will be placed in the log. Live on 2026-11-12, a window maximised to 3840x2054 grew from 281 to 622 MB
  private in 30 s: the 4K targets, not a leak. A hidden window resized 40 times between 1280x720 and 1920x1080 stayed
  at 468 to 476 MB private after its first 10 rebuilds (scratchpad resize-probe.ps1): the swapchain's rebuild leaks
  nothing measurable. The working set's 4.6 GB, ten times the private bytes, remains unexplained; the computer's sleep
  is the suspect, not tried.
- dev-deploy.ps1 stops before building when it would move the window from the config's -scene-source (another
  checkout, or the published scene without modules); -MoveWindow does it on purpose. check-deploy-guard.ps1 runs it on
  made-up configs, building nothing.
- The window has Delta's face as its icon (Tray.FacePixels), and a small amber brain beats in the bottom left while
  Claude thinks (Panel.Brain).
- Live on 2026-11-14, with the readings on their timer: "the working set peaked at 4938 MB between 1080 s and 27479 s;
  now working set 4921 MB, private 4912 MB, managed 4500 MB", the first reading after the computer slept from 02:27 to
  09:47; minutes later the window was back at 448 MB private. The climb is managed, at the wake, and the GC gives it
  back: something catches up on the hours asleep and allocates as it goes. Not found yet.

## The IX workshop: a knot IX computed, on a stand in the hall (2026-11-14)

IX does the mathematics, Delta draws. `brain -ix-knot figure-eight -ix-mcp <ix-mcp.exe>` starts IX's MCP server (the
executable Claude Code runs over stdio) for one conversation (initialize, initialized, tools/call ix_knot with
geometry: true), checks the answer and writes `%LOCALAPPDATA%\delta\ix-atelier.json`, format delta-ix-geometry v1
(brain/ixatelier.go): the knot's id, names, closure, crossings, writhe and Jones polynomial, the rope's radius and its
3D path, rounded to 1e-4. Bounds, checked on both sides: 8 ropes and 4096 points at most, coordinates within ±1000, the
radius in (0, 100], texts of 96 characters without control characters, crossings and writhe within ±64, 512 KB. When IX
cannot answer (no ix-mcp, a process that stops, a tool error, no answer in 30 s) the document says why instead, so the
stand never shows an old knot as new. No daemon: ix-mcp lives for the call.

The stand is drawn by the office's widget module (modules/AgentOffice/IxAtelier.cs), which reads the document at most
once a second (DELTA_IX_ATELIER overrides its place): a plinth between Gaia and the cabinet, in the whole hall's view
(key 4), the knot 0.8 m high in beads along IX's path, amber where the path passes in front of the knot's plane and
blue behind, swinging 70° either side of its face every 16 s, and a plaque with what IX says (FR/EN). A refused
document shows "Géométrie refusée" and why; an absent one the command to run. Since it is the module, a new build of
it copied over the window's modules\office\AgentOffice.Module.dll is reloaded without restarting the window; a new
document is redrawn the same way. Checks: brain/ixatelier_test.go (IX's answer recorded in
testdata/ix-knot-figure-eight.json, 14 damaged answers, a fake ix-mcp over stdio that answers, refuses, exits or stays
mute) and widget-tests' Atelier, on widget-tests/testdata/ix-atelier-figure-eight.json, which a Go test keeps equal to
the brain's document. Live on 2026-11-14: the module reloaded in the running window (reload 1, same pid), tree
+ix-atelier, and the user saw it with key 4.

The origami table stands beside it. `brain -ix-origami <file> -ix-mcp <ix-mcp.exe>` sends a `.origami` sequence (IX
replays it) or a FOLD object to ix_origami_check and writes `%LOCALAPPDATA%\delta\ix-origami.json`, format
delta-ix-origami v1 (brain/ixorigami.go): the name, IX's verdict (ok), whether a sequence's expectations held, the
rules that rejected it, the crease pattern (vertices, edges, M/V/B/F/U/C/J, faces), the folded frame's vertices, and
the findings, each IX's rule with the edges or vertices it names (adjacency, crease_orientation, maekawa, kawasaki,
taco-taco and the like, cyclic, isometry, structure, a failed expectation). The table (IxOrigami.cs) shows the
pattern and the folded state on two boards, mountains red, valleys blue, what a finding names amber, and the verdict
and findings in words. The folded state is where IX's folded frame puts the creases: still, flat, not interpolated
from the pattern and not simulated, which the plaque says. Samples in brain/testdata/origami: half-then-quarter (valid),
wrong-expectation (valid, "expect ply 3" not held), flipped-crease (one mountain made a valley: IX rejects it by
adjacency at edge 7, and Maekawa fails at vertex 3).

### A real rope, a paper crane (2026-11-14, later)

The user found the beads and the boards "lego". Widgets now take smooth surfaces: `SurfacePiece`, triangles whose
vertices carry their own normal and colour, checked by Widgets.Expand like boxes (finite, within 16 m, unit normals,
colours in [0, 1], a known material, each triangle wound about its normals and not degenerate) and counted against a
budget of their own (120 000 triangles); the scene draws them in the instance's mesh with no rounded edge
(WidgetGeometry). This changes the contract and the scene: the module that uses it does not load in a window built
before it, so it reaches the live window with one restart of the scene, and from then on by module reloads.

- The knot is a laid rope: three strands twisted at 30° round IX's path (a Catmull-Rom curve through IX's points, with
  frames that do not twist about it), hemp with its fibres and the grooves between strands darker, each end whipped
  with red twine. The rope keeps IX's radius and depth, so a crossing shows which part is over as IX places it; IX's
  figure-eight gives 34 800 triangles. Checked: every point IX gives has the rope within 3.2 mm (its radius is
  21.7 mm), and no part of the rope strays 24.2 mm from IX's points.
- The fold, when the document has its faces and the folded state's face orders, stands on the table as a paper model:
  each face in its layer, 0.5 mm apart, in the order the FOLD's faceOrders give (f over g on the side g's normal
  points to), sorted by the longest chain under each face; the sheet's front kami red and its back cream, each side
  where the fold turns it. The layers' spacing and a slight tilt of each face's light are Delta's; the folded state is
  IX's, flat. Without orders (the documents written before) the folded state stays on a board. The model turns 60°
  either side every 20 s on a pin through the paper's centre. The document keeps v 1 and gains `orders` (4096 at most,
  [f, g, ±1], refused when they name a face that is not there, a face with itself, or come without faces).
- The crane: Rabbit Ear's traditional crane (Robby Kraft, MIT at the commit IX took it from; brain/testdata/origami/
  NOTICE.md), which IX keeps as its fixture. Its FOLD holds the folded form at the top level and the crease pattern in
  file_frames[0]; the brain reads that layout from frame_classes. IX finds it valid, 28 layers at most; 59 faces, 838
  orders. A valley fold in half checks the side convention: both outsides show the sheet's back, the red inside.
- Visual proof, off the live window: hidden scene on port 8899, view 4, scratchpad captures real-104304.

Live the same morning, the window did not restart on the fast-forward: `dotnet watch` applied the contract's new type
and fields as a hot reload, and every tree was then refused « too-many-triangles: more than 0 », the static
`WidgetLimits.Standard` keeping the new property's default. Stopping scene.exe let the brain start a new `dotnet watch`,
and the window came back with the module (same brain, no `/mcp` reconnect).

The user then found the rope "plastic" and the crane "flat":

- Each strand is now four yarns laid the other way: a relief of 10% of the strand's radius that the normals follow, so
  the light catches each yarn; the grooves between yarns darker, each yarn its own tone, a grain on the fibres. 3
  strands of 16 sides, rings every 3.3 mm: 84 000 triangles for the figure-eight.
- The crane opens: a pose beside the fold (`brain/testdata/origami/crane.pose.json`, carried by the document as
  `pose`) names a ridge (the back, vertices 15 and 9) and parts of the faces: the two wings, found as the faces reached
  from each wing's tip without crossing the ridge (the lower one: 7 faces) or those reaching the upper tip (3), turned
  to 80° from up, each towards the side of the stack its layers are on (layers 4–5 and 32–35: opposite sides); the body
  under the back spread 20° apart by its two halves of layers. The ridge is then levelled. It is Delta's illustration,
  not IX's: IX checks the flat fold, the table says « ailes ouvertes par Delta, illustratif », and the flat crane is what
  a document without a pose shows. A pose that names a face not there or twice, turns beyond 0–180°, or comes without
  the orders is refused, by the brain and by the module.

## Out of the arcade, and a hall of real materials (2026-11-14)

The user could not get out of PARSEC: Esc was the only way out and nothing said so. While the game has the keys, a
hint now sits at the bottom (« Échap, ou un clic hors de la borne : quitter le jeu »), a left click away from the
cabinet leaves as Esc does, and leaving flies the camera back to the hall's first view. Enter does what Home does
(back to Delta), asked for so that one is never stuck in a corner.

The user then found the hall "Lego", "childish". It was: every surface a box of one flat colour, edges rounded by 8 mm,
and bricks of 42 × 15 cm, twice real ones. A first step towards real:

- `tools/textures` (Go, `go -C tools/textures run .`) fetches five scans from Poly Haven, all CC0 — `brick_wall_001`,
  `concrete_floor_worn_001`, `concrete_wall_003`, `rough_wood`, `green_metal_rust` — and packs each as three 1024 px
  8-bit PNGs (colour with the height in alpha, OpenGL normal, ambient occlusion/roughness/metalness) into
  `%LOCALAPPDATA%\delta\textures`, with a `textures.json` naming each source and the SHA-256 of each file used. They
  are not in git (24 MB); the scene draws its flat colours without them (`--no-textures` too; `--textures DIR`).
- `MaterialTextures` loads them as three 2D array images with mipmaps made on the CPU, sampled with 16× anisotropy.
  `Material.BrickWall` (8) and on map a scan by the surface's place in the world (each box face takes the plane it
  faces; walls keep the image upright), with parallax occlusion: the mortar 14 mm behind the bricks' face. Roughness
  and occlusion come from the scan; those surfaces lose the rounded edges. `ClayBrick` takes only the concrete's grain,
  on each pallet brick's own colour.
- The wall is one surface at the bricks' face (z −3.30, where the clocks and the screen are); its scan tiled every
  1.3 m gives 21.5 cm bricks (Poly Haven says 3 m, which drew them twice too large beside the 60 cm clocks).

Hidden at 1920 × 1080, 55 fps as before. Next, if the user keeps it: side walls, the robot's props in real materials,
and light that is computed (shadows and occlusion by ray queries on the RTX 5080) rather than painted.

## Shadows and occlusion by ray queries (2026-11-14)

The user asked for the ray tracing. On a GPU that offers `VK_KHR_acceleration_structure` and `VK_KHR_ray_query` (with
buffer device addresses), the Low quality now traces its shadows and its ambient occlusion instead of painting them:

- `RayScene` builds one bottom-level structure per `Mesh` the first time it is drawn (its vertices as they are, 68-byte
  stride, no index buffer; disposed with the mesh), and each frame a top-level structure of what the previous frame
  drew, one instance per draw with its model rows. `Draw` remembers each draw; `Record` builds at the start of the next
  frame's command buffer, behind a barrier to the fragment shader. One instance buffer, structure and scratch per
  frame in flight; meshes gone are kept a few frames before their structure goes.
- The fragment shader, compiled for Vulkan 1.3 with `GL_EXT_ray_query` (`#define RAYS`), reads the structure at binding
  4. The key light's shadow is three rays in a 0.035 cone, mixed by interleaved-gradient noise (soft edges, slight
  grain); the occlusion is five cosine rays of 0.7 m. The scan's own occlusion multiplies it.
- `--no-rays` builds no structure at all (and the scene runs on GPUs without the extensions as before); `--rays-off`
  starts with the painted light and R toggles it while running (`SCENE rays traced` / `SCENE rays off: the shadows painted`).

Seen hidden at 1920 × 1080: the Gaia stand casts on the floor, the clocks, the pillar and the cabinet on
the wall, where the painted light drew only a disc under each. 55 fps either way at 1080p. At 3840 × 2160 with
`--immediate` the frame stays at 60 fps (p50 16.68 ms traced against 16.63 ms painted, p95 19.1 against 18.1): the
present still paces it, so the trace's own cost is not isolated here, only shown to be below the frame's slack.
This is a renderer's approximation of light, not a validated light simulation. DLSS (Streamline) is not used.

## Gaia's planet painted with NASA's Earth, V1.3 (2026-11-14)

The user asked for a high-definition NASA texture on Gaia's globe. The planet is the GaiaPlanet module's sphere (V1.2,
procedural colours on 96 × 40 points), and the module contract carried colours only, at most 8192 triangles and a 2 MB
image: colouring the vertices from the Blue Marble would have given a blur of about 64 × 64 points. So:

- The contract gains, additively, `ModuleTriangleVisual.Image`: the name of an image the host keeps and each of the
  first triangles' places on it (`ModuleTriangleUv`, 0 to 1). The name is lowercase letters, digits and dashes, never a
  path; the module ships no image bytes. Validation refuses a bad name, places off the image or not finite, and more
  places than triangles. V1.0 to V1.2 images load as before (module-tests with the real V1, V1.1 and V1.2: 85 ok).
- The host keeps globes (`GlobeImages`): `<materials>\globes\<name>\west.png` and `east.png`, two square halves of an
  equirectangular map that become two layers of one mipmapped array image (binding 5), sampled clamped so that the
  halves never blend with their far edges. `--globes DIR` reads another folder. The geometry gives a triangle whose
  image the host holds `Material.Imaged`, its place in Edge; the Low shader paints it from the image, with a little
  gloss where it is water. Without the image, or in the Flat lighting, the triangles keep their colours (seen).
- GaiaPlanet 1.3 gives the sphere's places: the planet faces the hall at 30° W, the Atlantic between Ottawa's clock and
  Paris's, east to the viewer's right; past the column where u would cross 0 it starts again from 1, so no triangle
  spans the seam (module-mesh-tests: every sphere triangle, none of the ring's 192; −30.0° facing; 31 checks).
- `tools/textures -globes only` downloads NASA's July 2004 map (21600 × 10800, topography and bathymetry), shrinks it
  in linear light, each pixel the area-weighted average of those it covers, and writes the halves at 4096 (12.7 and
  16.2 MB) in 29 s. Credit in THIRD_PARTY.md.

Seen hidden (port 8899, `--module-dir` with 1.3 alone, `--cinematic-from 4`): Africa, Europe, Arabia and India the right
way round, the ring and the stand as before; 56 fps; the Earth read in 632 ms. Not live: the live window's module and
its materials folder are untouched. Going live needs the user's word: the textures tool into the live materials
folder, and GaiaPlanet 1.3 offered to the live host like 1.2 was.

## The RTX card, F10 (2026-11-14)

The user asked to see NVIDIA's features as the Observatory's RTX scene shows them. F10 (or `--rtx` at start) shows a
card on the right: the GPU, its driver (NVIDIA's numbering: 617.42 on the RTX 5080, as nvidia-smi says) and the frames
a second, then a row each, green when the scene uses it, grey when the GPU offers it and the scene does not, red when
the GPU lacks it: the traced shadows and occlusion (R), the ray tracing pipeline, shader execution reordering, opacity
micromaps, DLSS (NGX's two Vulkan extensions only; using it needs NVIDIA's SDK), Reflex (`VK_NV_low_latency2`), present
wait and 4x MSAA. It reads the device's extensions once; nothing it shows is switched on by showing it. Seen hidden at
1280 × 720: the 5080 offers every row; in use, the traced light and MSAA (present wait is off in a hidden run).

## A wall of the scan's own bricks, laid by Blender (2026-11-14)

The user asked for still better bricks, with ComfyUI or Blender. Up close the scan was good; from the hall's usual view
it read as wallpaper: one 1.3 m square of it repeated about forty times, its pale bricks in a visible lattice, and its
joints flat. ComfyUI's models (SDXL here) would make a colour without the normal and height that match it, and seamless
tiling there needs custom nodes, which the task-local ComfyUI runs without; Blender lays geometry and bakes from it, so
it is Blender:

- `tools/textures/bricks.go` finds the scan's whole bricks from its height map (Otsu's threshold, the regions above it
  eroded by 6 px to part touching bricks, kept when brick-shaped and not cut by the edge): 44 of brick_wall_001's.
- `tools/textures/wall.py`, run by Blender 5.2 in the background, lays a stretcher bond of 225 × 75 mm (joint
  included) that wraps every 2.7 m: 432 slots, each one of the 44 bricks at random (never the same as its left or
  lower neighbours), flipped or not, resized by a few millimetres, turned by up to 0.5°, set forward or back by 1.5 mm
  and leant by 1 mm. Only the scan's brick pixels are laid (its mortar left behind), by a height test, so the chipped
  corners and worn edges stay as photographed. The bed is cast concrete's grain tinted to the scan's mortar, 10 mm
  behind. Normals come from the laid height; the occlusion is baked by Cycles on the GPU (OptiX) from that height as a
  mesh, with the neighbouring periods' edges around it. 4096 px, packed at 2048.
- `textures.json` names it (`"laid"`); `go -C tools/textures run .` refetches nothing it has, and lays again only when
  wall.py, its settings or its scans change (a stamp beside the output). About 35 s of Blender.
- Every material is now packed at 2048 px from 2k scans (the array's layers share one size), into
  `%LOCALAPPDATA%\delta\materials`: a new folder, so a scene of the earlier commits, which reads `delta\textures` and
  tiles the wall every 1.3 m, keeps drawing what it expects. 90 MB on disk, about 320 MB of GPU memory with mipmaps;
  loading takes 0.83 s instead of 0.43 s. 1080p hidden: 55 fps as before.

Seen hidden: the overview's wall no longer repeats, the joints are dark and recessed, the brick edges catch the key
light; beside the 60 cm clocks the bricks measure as real ones.

## A Miura-ori that folds as one sheet (2026-11-14)

The user asked for a more complex origami, then, seeing the first try, said a real origami is only folding, not
glueing: the table's model stacked faces in layers and turned parts of them, which looks glued. The Miura-ori now
stands as one sheet whose faces stay joined at the creases while it folds and unfolds.

- `tools/miura` (Go, `go -C tools/miura run . -out X.fold`) writes a Miura-ori of m × n parallelograms (defaults 12 × 8,
  65°, sides 1:1) from Schenk & Guest's closed form (2013): vertex (i, j) at (iS, jL + [i odd]V, [j odd]H), with H, S,
  L, V functions of the fold angle θ. Its FOLD has the crease pattern, mountains and valleys read at θ = 45°, the
  flat-folded state (θ = 90°) in file_frames[0] and that state's faceOrders (from the faces' overlaps just before
  flat). Beside it, `X.motion.json`: 25 frames of every vertex from flat to 85°, each facet's sides kept to 2e-15.
- IX checks the flat fold: the 4 × 3 and the 12 × 8 are valid, isometric, Kawasaki and Maekawa hold, 12 and 48 layers
  at most, 66 and 3120 orders, 0 findings (testdata/ix-origami-miura-*.json, IX's replies as recorded).
- The brain carries the motion in the document as `motion` (v 1 kept): 2 to 64 frames, each of the pattern's vertices,
  within ±16, and refused when a frame changes any distance between a face's vertices by more than 0.001 (the pattern
  is a unit sheet): the motion must be rigid, every face a flat card that only turns about its creases. The module
  checks the same, to 0.002.
- On the table each face is its own instance (`ix-origami/model/f{i}`, 96 for the 12 × 8), its two sides 0.2 mm apart,
  and Pose moves it rigidly: the turn from the flat face to its place in each frame, slerped between frames, the face's
  centre lerped. A cycle of 24 s: flat 3 s, folding 8 s (eased), folded 4 s, unfolding 8 s, flat 1 s, while the
  model keeps its turn of 60° either side.
- Widget tests: flat, mid-way between two frames and folded, every face's corner meets another face's within 1 mm except
  the sheet's own four corners, and the widest such gap is 0.07 mm mid-way (0.1 mm is the paper's own thickness);
  a frame that stretches a face, misses a vertex, or a motion of one frame is refused.

The plaque says « une seule feuille, pliée · mouvement rigide de Delta » and « le pliage : formule exacte, de Delta · IX
vérifie le pli à plat ». The motion is the closed form's kinematics, not a physical simulation: no forces, no paper
bending; faces passing through one another while folding are not checked (the Miura-ori does not, but nothing here
proves it). Seen hidden at 3200 × 1800, view 4, scratchpad miura-live/shot24: flat, half folded in its zigzag, and
nearly flat-folded into the accordion.

## The traditional crane, step by step, each step checked by IX (2026-11-14)

The user found the Miura-ori not very interesting, chose the traditional crane folded step by step, and asked that each
step be shown and checked mathematically, the scene playable step by step, and a video of it.

- `tools/crane` (Go, `go -C tools/crane run . -out DIR`, about 4 minutes) folds Rabbit Ear's crane (brain/testdata/
  origami/crane.fold) in 21 steps (steps.go): the square, the half folds and the triangle each folded and unfolded, the
  square base, the kites, the top down and up, the petal fold, the same behind (the bird base), the legs narrowed in
  front and behind, three inside reverse folds (tail, neck, head) and the wing. The sheet is the crane's pattern cut by
  every crease any step makes (64 vertices, 138 edges, 75 faces). For each step: its flat state from the creases it has
  folded (reflections from a root face, closing round every vertex, which is Kawasaki), its faces joined where it leaves
  creases flat, and the layer order from a constraint solver (transitivity, taco-tortilla, taco-taco) whose preferences
  are, in order, the step's own word (a few mountain/valley hints, steps.go), a folded crease whose faces moved together
  keeping its side, the final crane's side by most of a crease's pieces, and two faces' order at the step before.
  Each step is written as a FOLD (crease pattern, folded frame, faceOrders).
- The motion between steps is Delta's: rigid transforms per face found by Gauss-Newton on the joints and the creases'
  fold angles, reverse folds in three phases (open the spine, turn, close). Most moves are rigid to 1e-12. The bird base
  is not rigidly foldable, nor are reverse folds: the petal folds bend the paper by up to 0.1 % of the side, the
  collapse into the square base 0.025 %, the tail 1.26 %, the neck 1.11 %, the head 0.38 % (4, 3 and 1 mm on a 30 cm
  sheet). The manifest `crane.steps.json` (format delta-origami-steps v 1) carries the sheet, each step's words,
  FOLD, resting frame, flex and each face's layer, and the 261 frames.
- `brain -ix-origami crane.steps.json` (ixorigamisteps.go) asks IX's ix_origami_check about each step's FOLD with the
  census, and refuses the manifest unless the motion rests, at every step, with every vertex of the sheet where that
  step's FOLD folds it (each sheet vertex through the face of the FOLD that holds it), and no frame bends a face by more
  than 0.02. IX finds all 21 steps valid; the census rejects every stated order flipped alone (838 of 838 for the wing);
  Kawasaki and Maekawa hold at every interior vertex (44 for the wing); 28 layers at most. The document gains `steps`.
  Tests replay IX's recorded replies (testdata/ix-origami-crane-steps.json, TestRecordIXOrigamiSteps with DELTA_IX_MCP)
  and refuse a rest off the step's FOLD, a stretched frame, steps out of order, a missing layer, a step's FOLD whose
  vertex is none of the sheet's; IX rejecting a step is a verdict the step carries.
- The module plays the steps (IxOrigami.Player): each held 3 s (the first 3, the last 6), the move to the next at 6
  frames a second, eased; the faces lifted into each step's layers, 0.5 mm apart, between two steps' stacks as they
  move. The plaque: « Étape k sur 20 : … », IX's verdict, layers and orders, « Recensement : IX rejette R des P ordres
  inversés seuls », « Kawasaki et Maekawa tenus en N sommets », « Pour y venir : le mouvement de Delta fléchit le papier
  de X % du côté » (or « rigide à 0,05 % du côté près »), and « 21 étapes sur 21 valides selon IX · chaque état à plat
  vérifié, pas simulé ». The board shows the creases folded at that step, mountains red and valleys blue (from the
  resting frame: an edge whose faces turn opposite ways, a mountain when the upper face shows the sheet's front), the
  others faint. Frames may bend up to 0.021 in a steps document, 512 frames, 64 steps.
- `brain -origami-play next|prev|auto|K` writes %LOCALAPPDATA%\delta\ix-origami-play.json, which the module watches
  (DELTA_IX_ORIGAMI_PLAY elsewhere): one command per writing, from the step shown; within a second, the last counts.
- `scene --film-origami DIR` films the table close at 30 frames a second of the scene's own time, through a play
  document of its own in DIR (first step held, then all in turn), into DIR\origami-film.mp4, silent, 109 s by default
  (`--film-seconds S`). Made hidden: `--demo --hidden --listen 8899 --lang fr --size 1920x1080 --film-origami DIR
  --office-module …` with DELTA_IX_ORIGAMI on the brain's document, 3271 frames in 4 minutes.

What is checked and what is not: IX checks each flat state, its layers and its local theorems; the brain checks that the
motion passes exactly through those states and how much it bends the paper. The motion between states is an
interpolation, not a physical simulation: no forces, and faces passing through one another while moving are not
checked.

## The knots' floor, up a stair (2026-11-14)

The user asked for a floor of the scene kept for knots, and a way up; of the proposals, mathematical knots read by IX,
and a stair.

- `brain -ix-knots` (ixknots.go) writes %LOCALAPPDATA%\delta\ix-knots.json (format delta-ix-knots v 1, `-ix-out`
  elsewhere): eight exhibits, the trefoil 3₁, figure-eight 4₁, cinquefoil 5₁, three-twist 5₂, stevedore 6₁, septafoil
  7₁, the torus knot T(3,4) = 8₁₉ and the Borromean rings L6a4, each a closed curve (torus knots, Lissajous knots,
  three ellipses) tilted a little so that no two passages meet in the drawing. IX's ix_knot reads each drawing
  (`over: "height"`): its crossings, writhe, components and Jones polynomial. An exhibit is `verified` when that
  polynomial is the Knot Atlas's for it or its mirror's (`mirror`), and the components are its ropes; IX failing for one
  exhibit fails the document, which then says why. All eight are verified, three as mirrors (5₂, 6₁, 8₁₉: the drawing is
  the mirror image of the tables'). Equal Jones polynomials do not prove two knots the same in general; among the
  tables' knots up to these crossings they tell these ones apart. The tube's radius keeps clear of the curve's own
  passages (at least twice its radius apart). Tests replay IX's recorded readings (testdata/ix-knots,
  TestRecordIXKnots with DELTA_IX_MCP): every exhibit verified, the wrong knot not, a mirror a mirror, a reading without
  its polynomial refused.
- The scene (KnotFloor) builds a mezzanine at the back right, 2 m up, 2.35 × 4.2 m, on three steel columns, against an
  end wall: a stair of 16 concrete steps of 12.5 cm along the end wall from the hall's front, rails on both sides of
  it and round the floor, and eight stone plinths in two rows. On each a knot as a tube round the curve IX read (its
  diagram faces the walkway, turned 35° towards the stair), coloured, the Borromean rings red, green and blue, grey if
  IX did not verify it; a plaque « 3₁ Trèfle · IX : 3 croisements, Jones des tables »; on the wall « L'étage des
  nœuds », or why IX has not read them. `--knots FILE` reads another document; it is refused beyond its bounds (16
  exhibits, 8 ropes, 256 points, the unit cube, a tube up to 0.1, short texts without controls).
- The walking camera stands on levels: what is under it no more than 16 cm above its feet is walked onto (the steps),
  the eye eased by the landing's spring; what rises higher across the body, below its head, stops it (rails, plinths,
  the stair's side, columns); under the floor it walks on the hall's floor. 7 is the floor's view, at its stair's top.
  Self-tests: up the stair to 2 m and down again without a frame's jump over 5 cm or a fall, the stair's side and the
  floor's rail holding, a jump at the rail landing back on the floor, under it on the hall's floor to the wall, a
  flight landing on whichever floor is under it; with the floor's solids left out, five of them fail.

Seen hidden (port 8899, view 7, and from the hall's front at the stair's foot): the floor, its rails and stair, the
eight knots with traced shadows, the sign; 52 to 55 fps at 1280 × 720 (474 312 vertices for the gallery, most of them
the plaques' letters). Not live: the live window and %LOCALAPPDATA%\delta\ix-knots.json are untouched.

## Path tracing, F9 (2026-11-14)

The user asked for path tracing in Delta and NVIDIA's other features. This first part is the path tracing, with what
the GPU's ray queries give; the ray tracing pipeline with shader execution reordering follows below, Reflex and DLSS
come after.

- `PathTracer` (F9, `--path-tracing`, with the ray scene): a compute pass traces each pixel from the eye through the
  frame's top level (RayScene), jittered within the pixel. It reads the surface it meets from its mesh's own vertices by
  their device address (RayScene's instance table: the vertices, the glow, and the instance's rows at the structure
  before): colour, material, the photographed scans with their normals and roughness (`textureLod` by the ray's width),
  the globe, the rounded edges. Up to 3 bounces, Lambert under GGX, each surface lit by the key (4° across) and the
  fill through traced shadows, shaped by the Low lighting's pool around the robot as their beams; Russian roulette after
  the first bounce; a ray leaving the hall sees the Low lighting's surroundings; what glows lights what is near it.
- Kept over frames: each pixel's surface is followed to where it was a frame ago (the camera's move and the mesh's own),
  and of the four pixels around that place those that showed the same surface (place and normal) give its past, light,
  colour and glow alike, up to 32 frames; a glow that changes (a clock's digit) starts the pixel over. The light is kept
  over the surface's colour, three passes of an edge-aware à-trous filter (normal, plane, light; the light's bar
  narrowing as frames are kept) smooth it, and the colour, the shoulder, the glow and the haze are put back.
- The picture goes over the raster pass's through the splash's pipeline; the raster pass still draws the meshes (its
  traced shadows off), so the depth stays for the Pong screens, the cabinet's and the wall's, drawn after.
- The RTX card's row: « Path tracing ON », and the traced shadows offered rather than in use while it is.

Seen hidden at 1280 × 720, views 3, 4 and 7: soft shadows and the floor's bounce, the robot's and the knots' edges
smooth, the clocks' digits readable; 54 to 56 fps paced by vsync, as the Low lighting. Two mistakes found on the way and
fixed: the colour and glow were not kept (digits and textures shimmered), and the past was looked up at the jittered
sample instead of the pixel's centre (each frame blurred the picture a little more).

Not done or not exact: the objects' places are the last frame's (the ray scene is built from the frame before); the
screens drawn by their own pipelines are not in the ray scene, so they neither light the room nor show in reflections;
light from glowing surfaces is found by bounces only, not aimed at; the robot's moving shadow lags by about the 32
frames kept.

### The ray tracing pipeline, SER and the GPU's time

- When the GPU offers `VK_KHR_ray_tracing_pipeline`, the trace runs from a ray generation shader (`TraceRaysKHR`, an
  empty miss and a hit group with no shader, the rays still traced by ray queries) built from the same source as the
  compute pass; `--pt-compute` keeps the compute pass, to compare.
- NVIDIA's shader execution reordering (`VK_NV_ray_tracing_invocation_reorder`) is there behind `--ser`: each bounce's
  rays are traced into hit objects and sorted by the instance they hit (`reorderThreadNV` with a hint; the eye's rays
  are not sorted). It is not the default because it measured slower here.
- Two timestamps around the trace and the filter give their GPU time, printed at exit (`path tracing GPU … ms p50`).

Measured hidden at 3840 × 2160 (the window 3840 × 2130), view 7, about 12 s each, GPU p50 over two runs: the
pipeline 26-34 ms, the compute pass 34-38 ms, the pipeline with SER 38-40 ms; at 1280 × 720 all about 1 ms. The rays
are few and coherent and the shading is one shader whatever is hit, so SER has nothing to regroup and costs its sort; a
plain `reorderThreadNV` on every ray was slower too. The runs vary by several milliseconds, but SER was the slowest in
every one.

A mistake found on the way: the pipeline's picture had black blots that the compute pass's had not. `fresnel` took
`pow(1 - c, 5)`, and a cosine a hair over 1 made it the power of a negative number, NaN in the ray generation shader's
build (the compute pass's evidently multiplied it out); the filter spread each NaN into a blot. The base is clamped
now, and the GGX sample's sine too; the two pictures match (views 3 and 7).

### Max out, F6

The user asked to test a max out « comme dans la scène de l'observatoire » (its F6: path traced, 3 bounces, DLSS
Quality, frame generation). Here F6 or `--max-out` turns on the path tracer with 5 bounces and 4 paths a pixel from
each pixel's first surface (`path()` in the shader, averaged), the haze and the traced rays; F6 again puts back what it
found, and F9 by hand leaves Max out. The RTX card's first row shows it. Without DLSS and frame generation, which the
scene has not, what is shown is what is drawn.

Measured hidden, view 7, GPU p50 of the trace and the filter: 1280 × 720, 3.4 ms against 10.5 ms; 1920 × 1080,
10.7 against 29.1 ms; 3840 × 2160, 28-29 against 92-94 ms, about 10 frames a second. In a still view the pictures differ
little (views 3 and 7), the 32 frames kept already converging; the extra paths should matter in motion, where fewer
frames are kept, which has not been measured.

### Reflex, --reflex

- `Reflex` (`VK_NV_low_latency2`, with the present ids and a timeline semaphore): the swapchain is made with the latency
  mode, the low latency mode is set with boost, each frame sleeps in `vkLatencySleepNV` before its keys and mouse are
  read, and the markers (simulation, input, submit, present) frame the driver's report, read every 30 frames. With it
  the frames are paced by Reflex: FIFO without the present wait, and a cap (60 when hidden) given to Reflex as its
  minimum interval instead of the scene's own sleep, which held each frame's input a frame longer (720p: 44 ms).
- `--reflex-measure` sets the same markers and reads the same report with the low latency mode off, to compare with the
  same instrument. The exit line: `SCENE reflex … keys read to GPU done ms p50 … p95 …`.
- The RTX card: Reflex ON with `--reflex`, offered otherwise. Opacity micromaps stay offered and unused, and the card
  says why: they speed up rays through alpha-tested geometry (leaves, fences), and every surface in the hall is opaque.

Measured hidden, view 7, path tracing on, about 12 s each, keys read to GPU done as the driver times it (not to the
screen): at 3840 × 2160, GPU-bound near 35 fps, 67-81 ms p50 with Reflex against 86-105 ms without over two runs (an
earlier run without the interval: 59 against 80-84), p95 close (98-119 against 95-129), 2-3 fps fewer; at 1280 × 720
capped at 60, 22.5 against 25 ms. So it helps most where the GPU is the bottleneck, as NVIDIA says, and is not the
default: shown, the present wait already reads the keys after the last frame is on screen, and Reflex has not been
measured on a shown window (that would open one on the user's screen). The remaining latency is about two frames: the
scene keeps two frames in flight.

### 30 fps on the live window, and a frame ahead (2026-11-15)

The author: "pourquoi on a que 30 fps alors que l'observatoire fait du 60 FPS en ray tracing ??" (RTX card: the rays and
4x MSAA on, path tracing off, present wait on). The live window is 3840x2054. Uncapped and hidden at that size the hall
draws 152 fps: the GPU keeps up when frames overlap. But shown, each frame waited for the last to be on screen before it
read the keys (the present wait), so a frame's CPU and GPU never overlapped another's, and one that took more than a
refresh showed at the next but one: hidden with `--present-wait` at 3840x2054, 39 fps; without the rays 56. Each frame's
GPU time is now timed (two timestamps a slot): 13.6 ms p50, 22 ms p95 at 4K with the rays and MSAA, 7 ms without the
rays. When frames miss the refresh (keys to submit plus GPU beyond 90% of it, 5 of the last 30), the next frame waits
for the one before the last instead (`SCENE pace: one frame ahead`), and comes back when 90 in a row take under 70%:
same run, 54 fps, keys to screen 33 ms p50 instead of 22. The exit line gives the GPU's times and how many frames were
ahead. The Observatory reaches 60 at 4K by drawing fewer pixels (DLSS upscales) and showing generated frames, and
Godot keeps frames in flight. *To verify*: the live window, shown; the rays' share (6.6 ms at 4K) is the next lever.

### Streamline, --dlss: the bootstrap, and the updater that reached the network (2026-11-15)

The author: "Vas-y pour DLSS et la génération d'images", with NVIDIA's SDK and DLLs. `Streamline.cs` loads Streamline
2.10.0 from a folder of NVIDIA's signed DLLs (`--streamline DIR`, else `%LOCALAPPDATA%\delta\streamline`), each DLL's
Authenticode signature checked first, in manual hooking mode (BRUTAL keeps its own `vulkan-1.dll`). `--dlss` loads
DLSS and Ray Reconstruction; `--dlss --dlss-g` adds frame generation, Reflex and PCL. The instance and the device take
the extensions, Vulkan 1.2/1.3 features and queues each feature asks for (`slGetFeatureRequirements`), the queues after
the scene's own; `slSetVulkanInfo` then says ok. Measured hidden on the RTX 5080: every feature supported; with all
five, queues graphics 0×2, compute 2×2, optical flow 5×1. `SelfTest` holds the C# structures to the sizes and offsets a
probe compiled against the SDK's headers printed. `--streamline-log` prints Streamline's verbose log. Nothing is
evaluated yet: DLSS on the path tracer and frame generation are the next slices.

**Accepted by the author the same day** ("2 - c'est acceptable"). Optional over-the-air updates (`eAllowOTA`) and downloaded plugins are off,
as in the Godot fork's `harden_streamline.py`, yet `slInit` runs the driver's NGX updater
(`DriverStore\...\nvngx_update.exe -api bootstrap|update`) for every feature loaded: Streamline's
`pluginManager.cpp` does so in every production build, whatever the flags. It costs about 1.4 s a feature (15 s to
the first frame with five), and during one run on 2026-11-15 an updater process held a TCP connection to
72.136.195.99:443. So `--dlss` contacts NVIDIA as the driver's DLSS updater does in any DLSS game; the Observatory's
fork does the same. Asked to choose between the scene's own render scale and DLSS with that contact, the author chose
DLSS.

### Ray Reconstruction on the path tracer, --dlss (2026-11-15)

The author, of Max out at 17 fps: "toujours tres peu de FPS", then "2 - c'est acceptable". With `--dlss`, the path
tracer (F9) traces at the size DLSS asks for (`slDLSSDGetOptimalSettings`; `--dlss-mode performance` by default, or
balanced, quality, ultra-performance, ultra-quality, dlaa), one path a pixel jittered by Halton's (2, 3) sequence, with
neither its kept frames nor its à-trous filter. The trace writes Ray Reconstruction's guides: the light with its glow
and haze, motion in pixels from where the jittered ray met the surface to where that surface was a frame ago, depth
along the view, diffuse colour, specular colour (the guide's EnvBRDFApprox2), the shading normal with the linear
roughness packed in w. `slSetConstants`, `slDLSSDSetOptions`, `slSetTagForFrame` (every image in the General layout,
`eValidUntilPresent`) and `slEvaluateFeature` go into the frame's command buffer; one compute pass then puts the
4K output through the shoulder, and the composite draws it. Streamline leaves the command buffer's bindings its own
(manual hooking, no state tracking): everything after binds again.

Measured hidden on the RTX 5080, 3840x2054, traced at 1920x1027:
- 2506 evaluations in one run, none failed. Trace, Ray Reconstruction and shoulder: 4.0–4.2 ms of GPU, against 7.2–7.4
  ms for the native trace and its filter.
- Max out, `--present-wait`: native 50 fps, frame p95 33.6 ms, a frame ahead for 1193 of 1339 frames; with Ray
  Reconstruction 59 fps, p95 17.8 ms, never a frame ahead. The path tracer's GPU time 13.6 → 6.5 ms.
- 2560x1440 quality: traced at 1707x960, 3.8 ms.
- The jitter's sign: DLSS takes minus the sample's offset from the pixel's centre. NVIDIA's own Vulkan sample
  (github.com/nvpro-samples/vk_denoise_dlssrr, given by the author) jitters its rays by `halton - 0.5` and passes
  `InJitterOffset = -jitter`; Streamline's DLSS-RR plugin hands `jitterOffset` to NGX unchanged. Zoomed 4× at a fixed
  view (`--view 1`), that sign keeps the drawers' thin highlights and the bricks' grain crisp; the other
  (`--dlss-jitter-flip`) goes soft. The first commit had the other sign: a whole-image Laplacian (4.0 against 3.4) took
  its noise for detail, and a comparison with the camera moving fooled the eye.

Also from that sample: the surroundings (where no surface is) move as a point at infinity along the ray (`w = 0`),
with their colour tone mapped as their albedo and a zero normal; and the first bounce's length when it is a reflection
(0 when diffuse) is tagged as the specular hit distance (`kBufferTypeSpecularHitDistance`, with
`DLSSDOptions::worldToCameraView`), which Ray Reconstruction uses for reflections in motion.

In manual hooking the host must present through Streamline, or its common plugin's once-a-frame bookkeeping and
collection never runs ("presentCommon() was not observed", logged at the first evaluation). The scene presents
through the interposer's `vkQueuePresentKHR` (its `vkGetDeviceProcAddr`, once the device is told), which runs
`presentCommon` then the device's own present; the error is gone. At shutdown NVIDIA's DLSS plugin, loaded but never
evaluated, logs `destroyKernel(ctx.mvecKernel) failed 6`: harmless so far. *To verify*: the live window (not yet
installed), and Ray Reconstruction's look on the screens and the globe, whose albedo guides are plain.

## Known problems

- (Fixed on delta-face) A phone page whose sound had not started sent a recording of 0.00 s, and Whisper wrote a
  sentence over the silence (utterances 66 and 69, live), which Claude answered. `/hear` now hears as nothing a
  recording with fewer than 12 frames of 20 ms loud enough for speech, the endpoint's own bar, without asking
  Whisper; brickyard-remote-b2's page no longer sends one.
- (Fixed on delta-face, for 0.6.1) With the headset and the phone both open, each microphone sent its own copy of a
  sentence, 0.1 to 0.5 s apart. Whisper wrote the two recordings differently (« Aïta, on ne sait quoi ensuite ? » and
  « Delta, on fait quoi ensuite ? »), or one cut it where the other did not, so the 4-in-5 rule dropped none of them,
  seen live on 2026-10-23: two acknowledgements, and at times two answers, one on each device. The user asked that one
  device be heard at a time. See « The louder copy goes ».
- Talk to people in the room without « Delta » keeps a conversation open and goes to Claude, 30 s at a time; the ring
  shows it, and a sentence for someone else, Brutal or Gaïa among them, ends it.
- A phone reloaded while one of its answers is on the way has forgotten its question and has no sound yet: it shows
  that answer as text only, and the computer stays silent too.
- The persona still over-claims: the first answer offered to « construire, lancer ou modifier des projets » although
  the persona says it cannot yet. Live, it claimed to have built a token indicator. Model and persona belong to #261.
- The first audio waits for the whole first sentence. On fix/link-first-line a first sentence over 80 characters is
  cut at its first comma with 30 characters before it and 20 after (breathFirst); the gain and how the cut sounds are
  not yet heard live.
- A sentence from another device gets its chime, its acknowledgement and `/hear`'s 202 only once routed: up to 4 s
  for the triage and 3 s for the fact model when Ollama is slow or cold, where the chime used to come at once.
- The caption shows for five seconds what was said in the room without « Delta »: in a window that is shared or
  streamed, other people's words would show too. On fix/link-first-line, T turns those captions off and on
  (RoomCaptions); what was said to Delta or Claude, and what Delta says, still show, and the choice outlives the
  window (%LOCALAPPDATA%\delta\captions-room.off).
- IX's gateway makes a pairing code only when it starts, so two minutes later the card can only say that the code
  expired. IX and brutal proposed `POST /pair/new`, a new code on demand; the user said yes on 2026-11-12. On
  fix/link-first-line, opening the pairing card with no code alive sends `{"pair":true}`; the brain posts
  127.0.0.1:8813/pair/new with the hooks header and X-Delta-Remote, at most every 10 s, and the gateway answers 202
  and posts the new link to /link as at its start, or 404 until brickyard-remote builds the entry.
- When a phone in the room hears a sentence without « Delta », the computer's copy is ignored and its caption shows,
  while the phone's copy, which needs no wake word, goes to Claude.
- (Fixed on fix/link-first-line) A window that starts after the gateway posted its link never got it: the brain kept
  no pairing for a new connection. It now keeps the last code in memory and gives it to a new connection after the
  hello, with the seconds it has left, while it lives a second more.
- The shelf's sessions took about ten seconds to appear with nine wmux workspaces: each wmux call costs about half a
  second, and there is one per workspace. On fix/link-first-line the workspaces' list-surfaces calls go at once, up to
  eight together, for the shelf and the agents' office alike; the gain is not yet measured against the live wmux.

## To verify

- The Low level on an integrated GPU and over Remote Desktop: its cost, MSAA included, and whether auto should take
  Flat there.
- The visible frames that miss a vsync, p95 33 ms with or without Low, beside Delta's window.
- The warm voice by ear, live, against Piper's own (`-fx none`).
- The spoken window and shelf commands with Whisper's real transcripts: misheard verbs fall through to Claude.

- The release on another machine: unzip, the four prerequisites, `start-delta.ps1`, a conversation.
- Transcription on the GPU: IX's CUDA 12.4 build predates the RTX 5080; a Vulkan or CUDA 12.8 build of whisper.cpp
  would need a download and a build.
- A recording of a dozen exchanges, with latency p50 and worst case.
- The plugin from the published marketplace, on a machine without IX's tools:
  - `/plugin install`, then `/delta:setup` downloading everything;
  - a session with `--dangerously-load-development-channels plugin:delta@brickyard`.
- A live English session with a human voice: the wake word, "Delter", and the latency.
- English for the local window and shelf commands, the triage and the compaction sentence.
- The gateway end to end with IX's: a sentence from a phone, the chime, and Delta's voice on the phone.
- The hands with a real camera: each pose, a swipe each way, a wave and pointing down, and the thresholds by hand.
- The caption of a sentence let pass, live: whether five seconds is enough to read it.
- The ring, live: whether 30 s suits, and whether talk to people in the room now reaches Claude too often.
- The office's wmux readings, live, once its access is renewed:
  - whether scene.log's « … unread N time(s) » lines come in step with the office's two looks;
  - whether the robots stay in their enclosures through them;
  - whether the wall's card reads « reading N s old » rather than « live » meanwhile.
- The live window over a day: on 2026-11-06 it closed after 24 h with a working set peak of 4.8 GB, and runs of frames
  near 425 ms in its log. Whether memory grows with time, and what those frames spend, is not measured.
- The pairing QR live: IX's gateway posts its link, and a phone scans the window's code and pairs.
- A phone in the room: each answer heard once, on the device that asked, and one sentence heard by both sent once.
- The agents' office with real sessions, beside Delta's window: each activity as wmux reports it, a click on a desk
  focusing the right pane, and the frame time with 20 desks.
- The camera by hand, live: the jump, the slide and the leans as they feel, Alt not opening Windows' window menu, Ctrl
  and a click with the desks under the cursor, the wheel's slowing with the Pong running, and the cinematic's shots
  with the Pong on Delta's chest.
