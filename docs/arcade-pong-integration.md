# Pong chest screen: isolated integration slice

This worktree imports CPU prototype `2ee69aba6d44cbf40292742c2c64c4abda548705` and export changes `79caa6cbf8da34a3400ef810dd117a4d9a751e91`. It also preserves the installed clock correction byte-for-byte in `SceneModuleHost.cs` and the existing clock tests. **Nothing in this slice is installed in the live Delta checkout.**

## Interface and ownership

`IScreenModule` is an additive contract, separate from Gaia's unchanged `ISceneModule`. The host supplies elapsed monotonic seconds and pause state; it receives borrowed top-left RGBA8 pixels, stride 640, exactly 160 x 96 (61,440 bytes), and a dirty revision. The caller finishes copying before the next render. Invalid dimensions, stride, size or zero revision fail before GPU upload. No graphics handles cross the interface.

`ArcadePong.Module` v0.1 loads the actual existing simulation/raster sources into a collectible context with ID `arcade-pong`. The module has no timers, background threads, audio, model calls or network activity. The loader accepts only its expected assembly/type/ID, a local image no larger than 2 MiB, and shares only the contract assembly. This is trusted local code, not a sandbox for untrusted plugins.

`ScreenModuleSession` owns the code on the render thread. Rendering is paced at 30 Hz maximum with excess stall time discarded; simulation keeps the prototype's 120 Hz steps and 250 ms work cap. Pause and reset are exposed at the interface and tested; no live input controls have been added. Pixel storage is reused and unchanged frames retain their revision.

## Robot and GPU

The reserved rectangle is on the existing chest, x -0.15..0.15, y 0.45..0.63, z 0.218, following `worlds[Robot.Torso]`. It sits over decorative vents and above the original status lamps. The eyes, lamps, torso geometry and Gaia presence remain intact. When the optional screen is absent, the original chest content is drawn unchanged.

The screen is a perspective-transformed, depth-tested textured quad, with nearest sampling. There is one image and reusable transfer buffer per existing frame-in-flight slot: two of each in this renderer. Each logical image/buffer is 61,440 bytes; driver alignment is additional. Slot upload happens after the slot's fence and before its render pass. Other slots never sample that image. No per-frame image creation, descriptor recreation or global device-idle wait is added by Pong. Existing startup/shutdown waits remain.

The first real Vulkan preview used one PID, a 10 fps cap and a 13-second limit. It recorded 99 updates, two screen images, zero native mesh buffers and zero texture images remaining after cleanup. These are the renderer's ownership counters, not a driver-wide leak audit. Screenshots show upright scores/players, movement and a changed score while the screen follows the torso.

## Reproduce without live access

Build `modules/ArcadePongModule/ArcadePong.Module.csproj` in Release, then `scene/Scene.csproj -r win-x64`. Use the repository's .NET 10 SDK and existing dependencies. CPU checks are `check-arcade.ps1`, `screen-tests/ScreenModules.SelfTest.csproj` (arguments: real Pong module DLL, real Gaia module DLL), and the unchanged Gaia/clock module tests.

The isolated renderer accepts the explicit opt-in:

```
--demo --hidden --quality flat --cap 10 --seconds 13 --lang fr
--module-dir <isolated-Gaia-images>
--arcade-module <ArcadePong.Module.dll>
--shots <isolated-proof-directory>
```

The demo never listens to the live brain. Its thinking/speaking/paid labels are local scenario data, not LLM/audio activity. Omitting `--arcade-module` allocates no Arcade resources and preserves the original display. The shared prototype script restores only package-free projects from a local source; no dependency is installed.

The GIF proof is five real Vulkan screenshots sampled from the same run, not continuous video or a frame-rate benchmark. The capture log retains their actual times.

## Remaining before live delivery

The currently running renderer lacks this screen port. Installing the host/contract requires a separately approved initial renderer bootstrap; none is attempted here. The first slice loads Pong explicitly at startup and unloads on shutdown. UI activation/disable, pause/reset controls, hot replacement of Pong code, and live QA are **not delivered**. Gaia's existing version UI is not repurposed. No extra avatar, connection, audio, LLM, credential, service or automatic start is added.
