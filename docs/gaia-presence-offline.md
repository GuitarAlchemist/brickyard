# Gaia's offline presence — 2026-10-25

This small visual module adds a static violet totem beside Delta. Its 3D placard explicitly reads **Gaia — non raccordée**. It does not implement connectivity, activity, speech, commands or audio. The grey marker is static; it does not claim a live connection.

`scene/GaiaPresence.cs` reuses `Boxes`, `Mesh`, materials, and the existing Unicode `Splash.Text` rasterizer. The label is shallow geometry in the existing depth-tested pipeline. `scene/Program.cs` adds only construction, one draw, and disposal. The hall, robot, input handlers, descriptor pools and brain are unchanged.

## Validation

- Isolated branch `codex/gaia-presence-offline-20261025`, based on `40831d549121e4fd6bc94412fee0ddfe290b3efd`; Delta-live's active checkout is not modified.
- `dotnet restore --locked-mode -r win-x64`: success. NU1900: vulnerability metadata at api.nuget.org was unavailable; locked dependencies restored.
- `dotnet build -c Release --no-restore -nologo`: success, zero errors. Existing BRUTALCORE0011 advisories concern Conversations.cs/Shelf.cs.
- `dotnet run -c Release --no-build -- --selftest`: `SELFTEST ok`.
- Actual Vulkan preview with `--demo --hidden --quality flat --cap 10 --seconds 6 --shots <output>`: exited 0 at its time limit. The existing demo bypasses the listener and needs no brain or audio. GPU utilization before launch was 13%, with no scene process running. The hidden preview presented 39 frames, used 239 MB peak working set and 0.73 CPU seconds; the six-second limit intentionally omits later demo-state captures.
- `idle.png`, 1280×720: visually checked. Delta, hall, original controls and readable offline placard are visible. SHA-256: `4270CC9D09E6D474D8F89F235701FE83A26FC20F907AFA2CADA309BCE1FD4B7C`.
- Screenshot saved to the user's Library as `Gaia-presence-Delta-20261025.png`, Library ID `libfile_f723b769f6188191ac3f1d357b461909`, file ID `file_00000000989c81f686f75ceed036c0ac`. Local xattr persistence is unsupported by the Windows Python runtime; Library creation succeeded.

## Handoff boundary

This is built and previewed in isolation, not deployed to the user's active scene. A succeeding owner must review this exact change and coordinate the scene replacement separately. Do not rewrite the hall or robot, add a connection claim, read credentials, enable audio or restart Delta as part of this slice.

## Subsequent integration and localization candidate

The original `ee5b6cd` slice was subsequently fast-forwarded into `C:\tmp\brickyard-face` at 15:26:29 UTC. The existing `dotnet watch --non-interactive` requested and performed a scene restart; the new scene PID was 101332 at 15:26:37 UTC. Brain PID 82712 and watch PID 66940 were preserved. Stef confirmed the totem in the active scene. The active-checkout Release build and selftest passed.

The next candidate fixes the French-only placard. `Words.Say` follows the existing `Words.Language`; two meshes with localized labels are prepared once, and the render loop selects the current label's mesh on every frame. Language changes do not rebuild meshes or reset the renderer. Bilingual settings use the existing first-language rule. There is no new locale layer or change to the user's preference.

The existing `--selftest` now sends FR → EN → FR and en+fr/fr+en changes through one `Link` receiver and checks the visible label. The initial French-only implementation failed the English assertion; the candidate passes the full selftest and Release build. Logs are retained in the task workspace as `gaia-localization-red-test.log`, `gaia-localization-green-test.log` and `gaia-localization-green-build.log`. The test restores the pre-existing language setting. No second scene/GPU process was started for these tests.

This localization candidate is **not deployed**. It changes the mesh resources created during startup, so initial installation must be treated as requiring a scene reload; none was attempted. That initial installation is distinct from subsequent FR/EN changes, which select already prepared meshes without a reload. The active scene remains at the original `ee5b6cd` slice.

A runtime module-loader contract has not been established by this change. The referenced `ga-brutal` snapshot at d1c0917 exposes `Lego.Api.Describe` and `HallSlice/LegoAdapter.Vertices` for validated procedural composition. Those are data/geometry adapter seams, not evidence of a hot-swappable Delta runtime loader. Do not label this localized class as such a loader.
