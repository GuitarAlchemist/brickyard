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
| Jint, the JavaScript interpreter that runs the arcade game's script | 4.17.0 | BSD-2-Clause; its package names the licence only, so its text is kept in `LICENSES\Jint\` | `scene\` |
| Acornima, Jint's JavaScript parser | 1.7.0 | BSD-3-Clause; text kept in `LICENSES\Acornima\` | `scene\` |
| .NET runtime (self-contained) | 10.0.13 | MIT, with its third-party notices | `scene\` |
| Go runtime and standard library | as built | BSD-3-Clause | `brain.exe` |

The splash draws BRUTAL's copyright notice at start-up with Windows' Segoe UI. No font and no image of text ships.

The brain draws the phone's pairing QR code with its own encoder (`brain/qr.go`). QR Code is a registered trademark of
DENSO WAVE INCORPORATED.

## Not in the release

You install these yourself, or `/delta:setup` downloads them for you, from where their authors publish them, at pinned
versions checked by SHA-256 (`plugins/delta/scripts/setup.ps1`). Each comes under its own terms. With Claude Code,
Ollama and qwen3:8b are not used.

| Component | Licence |
|---|---|
| Ollama | MIT |
| qwen3:8b | Apache-2.0 |
| Piper (rhasspy/piper, archived) | MIT. Its Windows build includes espeak-ng, which is under GPL-3.0 |
| A Piper voice | See its model card. en_US-ljspeech-medium's dataset, LJ Speech, is public domain. fr_FR-siwis-medium's dataset is CC BY 4.0, and the voice was fine-tuned from lessac |
| whisper.cpp | MIT |
| Whisper large-v3-turbo, ggml conversion | MIT |
| MediaPipe, for `hands\` | Apache-2.0 |
| OpenCV (`opencv-contrib-python`, which MediaPipe requires) | Apache-2.0. Its wheel includes FFmpeg, under the LGPL |
| MediaPipe's gesture recognizer model | Google's. Its model card names no licence; MediaPipe's code is Apache-2.0 |

`/delta:setup` does not install the camera process: uv fetches MediaPipe and OpenCV when you first start it, and the
process downloads its model.

## Fetched, never shipped

The arcade cabinet plays PARSEC, a third party's game at https://parsec.games.bu.app/, only when you ask for it: a
click on the cabinet, the view 6, `--arcade-play` or a film of it. Each time, the scene fetches its page from the
site, and its script runs in the scene with no network and nothing beyond the screen, the sound, the keys and its high
score. This repository holds no licence for it: the game is played from its site, as in a browser. `--arcade-robot`
lets the small robot play it while nobody does, which fetches the page when the window starts and again a minute after
each game. Nothing of it is copied into this repository or the release, and the scene keeps only its high score
(`%LOCALAPPDATA%\delta\arcade-storage.json`).

The hall's photographed materials are Poly Haven's scans, CC0 1.0 (https://polyhaven.com/license):
`brick_wall_001` (Rob Tuytel, Dimitrios Savva), `concrete_floor_worn_001` and `concrete_wall_003` (Dimitrios Savva, Rico
Cilliers), `rough_wood` and `green_metal_rust` (Rob Tuytel). `tools/textures` downloads and packs them into
`%LOCALAPPDATA%\delta\materials`, recording each file's SHA-256; neither this repository nor the release holds them yet.
The brick wall is not the scan as photographed: Blender (GPL; run as a tool, nothing of it is copied) lays the scan's
own bricks afresh with `tools/textures/wall.py`, and what it writes is packed in the scan's place.

Gaia's planet is painted with NASA's Blue Marble: Next Generation, with topography and bathymetry (July 2004, 21600 ×
10800), credited as NASA asks: **NASA Earth Observatory** (https://earthobservatory.nasa.gov/features/BlueMarble).
NASA's imagery is not copyrighted in the United States; NASA's guidelines ask that it be credited and not used to imply
NASA's endorsement. `tools/textures` (`-globes only` for the globe alone) downloads it and writes it at 8192 × 4096, in
two halves, into `%LOCALAPPDATA%\delta\materials\globes\earth`, with `globe.json` holding its source, its credit and
the download's SHA-256; neither this repository nor the release holds it.

The streets of Ottawa behind the hall's roll-up door are OpenStreetMap's: © OpenStreetMap contributors, under the Open
Database License (https://www.openstreetmap.org/copyright). The scene fetches them from the public Overpass server
(overpass-api.de) the first time the door is opened, one request, and keeps the answer in
`%LOCALAPPDATA%\delta\ottawa\overpass.json`, as the licence allows. Neither this repository nor the release holds it.
The extrusion follows GeoTiles' `OsmBuildings.cs` (GuitarAlchemist/ga, `Experiments/Brutal/GeoTiles`, MIT, same
authors).

With G, the streets are instead Google's Photorealistic 3D Tiles (Map Tiles API), a paid service under the Google
Maps Platform Terms of Service and the Map Tiles API policies, through the user's own key (`GOOGLE_MAPS_API_KEY`, never
stored or printed). They are fetched at run time, kept in memory only, never written to disk nor committed, nothing
is derived from them, nothing of OpenStreetMap is drawn beside them, and "Google Maps" with the tiles' data providers
shows at the bottom of the picture while they are drawn. `scene/GeoTiles/` is a copy of GeoTiles' core
(`GeoTiles.Core` at fcef8b5: geodesy, tileset tree, selection, GLB; same authors), its selection changed to refine best
first. It decodes the tiles' Draco geometry with Openize.Drako 1.4.4 (MIT) and their
JPEG images with StbImageSharp 2.30.15 (public domain), both from NuGet, as GeoTiles does.

## Ideas, not code

The agents' office (`modules/AgentOffice`) takes its idea from
[agent-office](https://github.com/AgentSystemLabs/agent-office) (MIT), and its widgets compose as GA's Lego demo
(ga-brutal, MIT) does. Neither's code nor assets are copied.
