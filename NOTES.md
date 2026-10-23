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

## Known problems

- (Fixed on delta-face) A phone page whose sound had not started sent a recording of 0.00 s, and Whisper wrote a
  sentence over the silence (utterances 66 and 69, live), which Claude answered. `/hear` now hears as nothing a
  recording with fewer than 12 frames of 20 ms loud enough for speech, the endpoint's own bar, without asking
  Whisper; brickyard-remote-b2's page no longer sends one.
- With the headset and the phone both open, each microphone sends its own copy of a sentence, 0.1 to 0.5 s apart. Whisper
  writes the two recordings differently (« Aïta, on ne sait quoi ensuite ? » and « Delta, on fait quoi ensuite ? »), or
  one cuts it where the other does not, so the 4-in-5 rule drops none of them, seen live on 2026-10-23: two
  acknowledgements, and at times two answers, one on each device. The user asked that one device be heard at a time.
- Talk to people in the room without « Delta » keeps a conversation open and goes to Claude, 30 s at a time; the ring
  shows it, and a sentence for someone else, Brutal or Gaïa among them, ends it.
- A phone reloaded while one of its answers is on the way has forgotten its question and has no sound yet: it shows
  that answer as text only, and the computer stays silent too.
- The persona still over-claims: the first answer offered to « construire, lancer ou modifier des projets » although
  the persona says it cannot yet. Live, it claimed to have built a token indicator. Model and persona belong to #261.
- The first audio waits for the whole first sentence: cutting a long first sentence at a comma would start the voice
  sooner.
- A sentence from another device gets its chime, its acknowledgement and `/hear`'s 202 only once routed: up to 4 s
  for the triage and 3 s for the fact model when Ollama is slow or cold, where the chime used to come at once.
- The caption shows for five seconds what was said in the room without « Delta »: in a window that is shared or
  streamed, other people's words would show too. There is no switch to turn it off yet.
- IX's gateway makes a pairing code only when it starts, so two minutes later the card can only say that the code
  expired. IX and brutal proposed `POST /pair/new`, a new code on demand; the user has not decided.
- When a phone in the room hears a sentence without « Delta », the computer's copy is ignored and its caption shows,
  while the phone's copy, which needs no wake word, goes to Claude.
- A window that starts after the gateway posted its link never gets it: the brain keeps no pairing for a new
  connection.
- The shelf's sessions take about ten seconds to appear with nine wmux workspaces: each wmux call costs about half a
  second, and there is one per workspace.

## To verify

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
- The pairing QR live: IX's gateway posts its link, and a phone scans the window's code and pairs.
- A phone in the room: each answer heard once, on the device that asked, and one sentence heard by both sent once.
