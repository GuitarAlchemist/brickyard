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
