# Arcade Pong 0.1 — independent CPU prototype

Prepared from `a7bd4cfcc77f7d2912379cd0ceb7250b0cd4a483` in an isolated worktree. **Not installed, loaded, or displayed on the live robot.** No scene/brain restart, host edit, external game window, avatar, audio, LLM, package installation, or network service is part of this slice.

## What is ready

`modules/ArcadePong/ArcadePong.Core.csproj` builds a package-free .NET 10 library with two small components:

- `PongSimulation`: autonomous local players, ball, two-digit scores, serve delay, pause and deterministic reset. `PongOptions` sets ball speed, paddle speed and seed at construction. Creating another simulation applies a new configuration and starts a new game.
- `PongRasterizer`: court, paddles, ball, scores, serve indicator and pause symbol in a single reused **160 × 96 RGBA8** buffer (61,440 bytes). Origin is top left, stride is 640 bytes. No font, drawing framework, or GPU dependency. `Draw` overwrites the buffer; the consumer must finish copying/uploading before the next draw.

The simulation runs at 120 fixed ticks/s from caller-supplied monotonic elapsed seconds. Each call accepts at most 250 ms (30 ticks), discarding longer stall time rather than accumulating a backlog. Negative/non-finite elapsed values are rejected before changing simulation state. Pause discards fractional time; resume has no catch-up. Reset clears scores/counters and restores the seed and initial serve. Ball speed is clamped to 40–120 px/s, paddle speed to 24–140 px/s. Both players react every 100 ms and receive a seeded 30-pixel target error on roughly one in seven incoming turns. Errors are local gameplay, not model decisions. Scores wrap from 99 to 00 for indefinite play.

Steady update and raster paths allocate no managed memory. State has no history, collection, timer, thread, queue, or growing cache. Diagnostic counters use fixed-width unsigned integers; their eventual wrap does not affect play. The physics kernel is internal; the selftest links the actual CPU source following the existing `module-tests` pattern, without exposing public test hooks.

## Reproduce offline

From the worktree root, with the already installed .NET 10 SDK:

```powershell
.\check-arcade.ps1
.\check-arcade.ps1 -PreviewDirectory ..\pong-proof
```

The script uses the repository directory as the **only local NuGet source** with auditing disabled; both projects have no package references. It builds the deliverable library and runs the CPU selftests. No scene process is launched and no live files are copied. Preview output is six small PNGs at 0, 2, 6, 10, 20 and 30 simulated seconds, one 480 × 192 contact sheet (`pong-contact.png`, row-major chronological order), plus `pong-paused.png`. These are flat raster proofs, **not robot screenshots or an integrated UI**.

Coverage includes top/bottom reflection with overshoot, left/right paddle crossing, swept impact height, paddle edge inclusion/exclusion, a ball behind a paddle, one rebound only while departing, simultaneous wall/paddle contact, both scoring exits, autonomous play, deterministic mistakes/replay, score-to-serve reset, full reset, pause/resume, timestep partition equivalence, bounded stall work, invalid elapsed rejection, configured speed bounds, opaque changing raster pixels, score rollover, and six hours of high-speed play. A warmed 10,000-frame update/raster loop measures allocations using `GC.GetAllocatedBytesForCurrentThread` and requires zero bytes plus buffer identity reuse.

For an animated proof, `dotnet run --project arcade-tests/ArcadePong.SelfTest.csproj -c Release --no-restore -- --clip ../pong-proof/clip-frames` reruns the same tests and exports 240 PNGs from the same C# simulation/raster, sampling six 120 Hz ticks per 20 Hz frame. `clip.json` records seed, timing and final state. There is no second gameplay implementation.

The task's installed FFmpeg encodes those images into `pong-proof/pong-prototype.gif`: 12 seconds, 640 × 448, nearest-neighbour scale, an explicit `PROTOTYPE ISOLE - PAS DELTA LIVE` banner, and no looping extension. Encoding uses one CPU/filter thread, no audio, server, GPU, or added dependency. FFprobe verifies 240 frames and duration; a full FFmpeg decode verifies readability. The GIF is an isolated preview, never a screen in the live scene.

Proof-first observation: the new selftest initially failed with `CS0246` because the Pong namespace/core did not exist. The completed test run and build evidence belong to the task's `pong-proof` folder. The full scene/brain suite is intentionally not run: this slice changes neither, and a concurrent heavy rendering/build job was excluded by the request.

## Exact integration blocker, for the live owner

The existing hot-module contract does **not** support this use case:

1. `module-contracts/SceneModule.cs`: `ModuleState` contains only `Language`. `ModuleVisual` carries world-space boxes and text. No elapsed time, screen identity, pixel buffer, dirty revision or screen controls cross this boundary.
2. `scene/SceneModuleHost.cs`: `Advance` calls `Update` but calls `Render`/rebuilds its mesh only when the language/offer/error/validation key changes. Animating `ModuleVisual` alone would remain invisible in steady play. Forcing a rebuild by changing metadata would create geometry and GPU waits per frame.
3. `scene/SceneModuleRuntime.cs`: metadata/load validation hardcodes `GaiaPresence.Module`, `Delta.GaiaPresence.GaiaModule` and ID `gaia`. An Arcade DLL is rejected; renaming Pong as Gaia would replace that slot and misrepresent identity.
4. `scene/Robot.cs`: the chest glass is baked into torso geometry (centre `(0, 0.5, 0.2)`, size `(0.36, 0.26, 0.012)`), and the visor is baked into head geometry (local centre `(0, 0.18, 0.195)`, size `(0.44, 0.25, 0.012)`). Neither is a named hot-writable display surface in the current module API. The live owner must identify which existing small display was intended and reserve its drawable rectangle, preserving the robot's eyes/status elements.

Before live integration the owner needs a bounded screen port, separate from Gaia's static mesh slot: explicit Arcade identity/lifecycle; caller-supplied monotonic `dt`; CPU RGBA frame/revision delivery; input routing for enable/disable, pause/reset, and constructor configuration. The host must map that surface to the chosen existing robot screen and its torso/head pose, use nearest-neighbour sampling, and upload at a bounded rate (e.g. 30 Hz) into reusable GPU resources fenced against in-flight reads. It must release the port and restore existing display content when disabled/unloaded. The scene's existing overlay texture helper waits idle and recreates textures, so it is not an animation port as-is.

After that port exists, the intended call path is simply `simulation.Advance(dt)`, then at the chosen upload cadence `raster.Draw(simulation.Frame)` and copy `raster.Pixels` while owned by the render thread. All pacing, screen selection and GPU lifetime stay with the host; the library starts no background work. Budget the fixed CPU buffer and a bounded number of host staging/image buffers. Verify actual screen pose/UV orientation, small-screen legibility, pause/reset, enable/disable restoration, long-run native resource counts and unload before declaring integration complete.

**Status:** CPU prototype verified; robot-screen integration **pending an unavailable host capability**, not attempted or denied. No host API patch or live activation is included. The isolated worktree was created with Git because managed worktree tools report this delegated task type unsupported; its path and branch are recorded in the handoff.
