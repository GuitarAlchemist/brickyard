# Where this copy of Lego comes from

`Lego/` and `Lego.Cli/` are a copy of the "fractal Lego" library from GuitarAlchemist/ga, taken with `git archive`
at commit 644e25db9 on the branch `claude/brutal-lego` (pull request #840, a draft not yet merged when copied,
2026-11-16). Another writer owns that code: change it there, then copy it again here, rather than editing it in
place.

Two things differ from the source:

- The `packages.lock.json` files are left out: the scene restores these projects for win-x64, which their lock files,
  made without a runtime, refuse in locked mode. `Directory.Build.targets` turns them off; the scene's lock file
  records FSharp.Core instead.
- `Directory.Build.props`, which is ours, pins FSharp.Core to an exact version, so that a locked restore gives the
  same answer whatever SDK feature band builds it.

`dotnet run -c Release --project Lego.Cli -- test` runs the library's own tests: 21 pass on this copy.

The hall's gallery (`../gallery`) builds its blocks on this library. The scene draws them (`scene/HallGallery.cs`).
