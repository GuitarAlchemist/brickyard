# Third-party components

Delta's own code is under the MIT licence ([LICENSE](LICENSE)). `release.ps1` copies into the release's `LICENSES\`
folder:

- every licence, notice and third-party notice file found in the NuGet packages the scene is built from, each text
  once;
- the .NET runtime's licence and third-party notices;
- Go's licence.

`LICENSES\PACKAGES.txt` in the release names the package and version of each file.

## In the release

| Component | Version | Licence | Where |
|---|---|---|---|
| BRUTAL (`Ahwoo.Brutal.*`: Core, Glfw, Vulkan, Vulkan.Abstractions, ShaderC and their dependencies Collections, Interop, Logging, Memory, Numerics, Strings) | 2026.10.x, pinned in `scene/packages.lock.json` | BRUTAL License v1.0 | `scene\` |
| Native libraries inside the BRUTAL packages (GLFW, shaderc, SPIRV-Tools and the rest) | as packaged | as listed in each package's `THIRD-PARTY-NOTICES` | `scene\` |
| The Brutal logo | [ahwootech/brutal-branding](https://github.com/ahwootech/brutal-branding/tree/5d38373a24fed6f8afcc8bc803c96b6163ddf4b4) at 5d38373, `LOGO_BRUTAL_WHT.png`, SHA-256 `59768c2ab60fc835af29b1d686e5a76604e959fb4e84adf2bfb7fd26e7355d0c` | Ahwoo's. It is shown as BRUTAL's licence requires (README) | `scene\brand\` |
| CommunityToolkit.HighPerformance | 8.4.2 | MIT | `scene\` |
| .NET runtime (self-contained) | 10.0.13 | MIT, with its third-party notices | `scene\` |
| Go runtime and standard library | as built | BSD-3-Clause | `brain.exe` |

The splash draws BRUTAL's copyright notice at start-up with Windows' Segoe UI. No font and no image of text ships.

## Not in the release

You install these yourself, or `/delta:setup` downloads them for you, from where their authors publish them, at pinned
versions checked by SHA-256 (`plugins/delta/scripts/setup.ps1`). Each comes under its own terms. With Claude Code,
Ollama and qwen3:8b are not used.

| Component | Licence |
|---|---|
| Ollama | MIT |
| qwen3:8b | Apache-2.0 |
| Piper (rhasspy/piper, archived) | MIT. Its Windows build includes espeak-ng, which is under GPL-3.0 |
| A Piper voice | See its model card. fr_FR-siwis-medium's dataset is CC BY 4.0, and the voice was fine-tuned from lessac |
| whisper.cpp | MIT |
| Whisper large-v3-turbo, ggml conversion | MIT |
