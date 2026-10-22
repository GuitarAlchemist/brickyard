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

## Known problems

- The persona still over-claims: the first answer offered to « construire, lancer ou modifier des projets » although
  the persona says it cannot yet. Model and persona belong to #261.
- The first audio waits for the whole first sentence: cutting a long first sentence at a comma would start the voice
  sooner.
- The robot's name is not settled; the persona says Pinion for now.

## To verify

- Speech recognition: none yet, typed lines stand in for it (#261).
- A visible window with the voice playing, not muted, and the lip sync judged by eye.
- A recording of a dozen exchanges, with latency p50 and worst case.
