# Brickyard

Delta, a voice for Claude Code on Windows: a Go brain (`brain/`), a BRUTAL scene (`scene/`) and the `delta` plugin
(`plugins/delta/`).

- **Check:** `.\check.ps1` runs what CI runs (the plugin and scripts, `gofmt`, `go vet`, `go test`, the scene's build and
  selftest). `.githooks/pre-commit` runs the parts a commit touches, once `git config core.hooksPath .githooks` is set.
- **Release:** bump `plugins/delta/.claude-plugin/plugin.json` (its version and the `delta-<version>` path),
  `release.ps1`'s default, and write `docs/releases/<version>.md`; `check-plugin.ps1` fails until all three agree. Push
  `main`, then the tag `v<version>`: `release.yml` builds and publishes the zip that `setup.ps1` downloads.
- **Try a build on the live Delta:** `dev-deploy.ps1`, then reconnect delta in `/mcp`.
- **Receipts:** `NOTES.md` has a dated section per change, with what was measured and what is still to verify.
- Machine-specific pointers, if any, are in `CLAUDE.local.md`, which git ignores.
