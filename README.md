# Delta

Delta is a small robot you talk to, in French, with your voice. It stands in a brick hall drawn with Vulkan, listens on
your microphone, answers with a language model running on your machine and speaks with a local voice. Nothing leaves
your computer.

![Delta speaking, in its brick hall](docs/delta.png)

Delta is the first step of Brickyard, a software factory you drive by voice
([GuitarAlchemist/gaia#259](https://github.com/GuitarAlchemist/gaia/issues/259)). For now it can only talk: it cannot
build, run or change anything yet, and it says so.

## What you need

- Windows 10 or 11, x64, with a GPU whose driver supports Vulkan 1.3.
- A microphone and speakers or headphones. Delta does not listen while it speaks, but headphones keep its voice out of
  the microphone.
- About 6 GB of free video memory (or memory) for the language model.

Delta does not ship the four programs below. Each is free and has its own licence. Put them next to `start-delta.ps1`,
or give their paths to it.

| What | Where to get it | Where it goes |
|---|---|---|
| Ollama and the model | [ollama.com](https://ollama.com), then `ollama pull qwen3:8b` | Ollama's own install |
| Piper, the voice engine | `piper_windows_amd64.zip` from [rhasspy/piper 2023.11.14-2](https://github.com/rhasspy/piper/releases/tag/2023.11.14-2) | unzipped here, so that `piper\piper.exe` exists |
| A French Piper voice | `fr_FR-siwis-medium.onnx` and `fr_FR-siwis-medium.onnx.json` from [rhasspy/piper-voices](https://huggingface.co/rhasspy/piper-voices/tree/main/fr/fr_FR/siwis/medium) | `voices\` |
| whisper.cpp's server, which transcribes | `whisper-blas-bin-x64.zip` from [whisper.cpp b5454](https://github.com/ggml-org/whisper.cpp/releases/tag/b5454) | `whisper\`, so that `whisper\whisper-server.exe` exists (the zip puts it in a `Release` folder: move its files up) |
| A Whisper model | `ggml-large-v3-turbo-q5_0.bin` from [ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp/blob/main/ggml-large-v3-turbo-q5_0.bin) | `whisper\` |

The versions Delta was tested with have these SHA-256 hashes: `whisper-blas-bin-x64.zip`
`b1158bd10edf92c9799149a6fc5d3e58da3f495241d6fb3423088130e88ffbf3`, `ggml-large-v3-turbo-q5_0.bin`
`394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2`.

Any French Piper voice works. Read its model card before using it in public: siwis's gives its dataset's licence as
CC BY 4.0, and says the voice was fine-tuned from the English lessac voice.

## Start

Unzip the release, add the programs above, then run this in PowerShell, in that folder:

```powershell
Get-ChildItem -Recurse | Unblock-File   # once: Windows marks files downloaded from the internet
.\start-delta.ps1
```

If PowerShell refuses to run scripts, use `powershell -ExecutionPolicy Bypass -File .\start-delta.ps1`.

The script checks every prerequisite first and says what is missing. The window opens on BRUTAL's logo for about
2.5 seconds, then shows Delta. The first answer takes longer, while the model loads. Speak to it: its eyes turn green
while it hears you, amber while it thinks, and its mouth follows its voice. Press Ctrl+C in the terminal to stop.

| Option | What it does |
|---|---|
| `-Type` | Type your sentences instead of speaking; whisper.cpp is not needed then |
| `-Voice <file.onnx>` | Another Piper voice |
| `-Model <name>` | Another Ollama model (default `qwen3:8b`) |
| `-Piper`, `-Whisper`, `-WhisperModel` | Paths, if the programs are elsewhere |
| `-Ollama <url>` | Ollama's address (default `http://127.0.0.1:11434`) |

## How it works

- `brain\` is in Go, using only the standard library. It listens on the default microphone (16 kHz). An energy
  threshold cuts what you say into sentences, and whisper.cpp's server transcribes them on 127.0.0.1:8806. The text
  goes to Ollama. The answer is cut into sentences as it streams, and each sentence goes to Piper and is played as
  soon as it is ready.
- `scene\` is in C# and draws with Vulkan through [BRUTAL](https://github.com/ahwootech/BRUTAL). The brain tells it what Delta
  is doing with one JSON line per change on 127.0.0.1:8805. The line can be `{"state":"idle"}`, `"listening"` or
  `"thinking"`, or `{"state":"speaking","seconds":S,"envelope":[...]}` with the voice's loudness every 20 ms.

The brain starts whisper.cpp's server itself and stops it when it exits.

## Build from source

You need the .NET 10 SDK and Go. `.\release.ps1` builds both parts and packs `dist\delta-<version>-win-x64.zip`. You
can also build each part on its own: `go build` in `brain\`, and `dotnet build -c Release` in `scene\`. The scene
needs no brain to run:

- `scene.exe --demo` plays the four states;
- `--shots DIR` with `--demo` saves a PNG of each;
- `--selftest` checks the protocol without a window.

## Licences and credits

Delta's own code is under the [MIT licence](LICENSE).

The scene is built with BRUTAL:

> BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.
>
> This product is built with BRUTAL, a game development framework by Ahwoo Limited, used under the BRUTAL License
> v1.0.
>
> www.ahwoo.com/brutal

BRUTAL's licence ([LICENSES/BRUTAL/LICENSE.txt](LICENSES/BRUTAL/LICENSE.txt)) asks three things of a distributed
product, and Delta does each:

| The licence asks | Delta does |
|---|---|
| The Brutal logo before the product starts | The scene opens on it. The logo is Ahwoo's, from [ahwootech/brutal-branding](https://github.com/ahwootech/brutal-branding/tree/5d38373a24fed6f8afcc8bc803c96b6163ddf4b4), unaltered except for scaling |
| BRUTAL's copyright in the credits | The notice is shown under the logo, and quoted here |
| `NOTICE.txt` alongside the product | It is in the release |

[THIRD_PARTY.md](THIRD_PARTY.md) lists the other components and their licences.
