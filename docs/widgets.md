# Widgets: composable 3D elements for scene modules

A scene module can now describe what it shows as **widgets**: 3D elements such as a robot or a desk, each a pure function from
its arguments to pieces. The contract lives in `module-contracts/Widgets.cs`, beside Gaia's unchanged `ISceneModule`. The
first module built on it is the agents' office (`modules/AgentOffice`).

## Model

The model follows GA's Lego demo (ga-brutal, `Experiments/Brutal/Lego`, MIT). Only the ideas are taken from it, no code.

- **Widget:** `name@version`, bounded parameters (numbers, or texts bounded by length), and a function that returns `Built`:
  pieces plus named anchors (frames).
- **Pieces:**
  - `BoxPiece`: centred, as `Boxes.Add` draws it, so it has the robot's rounded edges and materials.
  - `TextPiece`: a label in shallow boxes, as Gaia's.
  - `UsePiece`: another widget, or the same one with other arguments, named and placed.
  - `AttachPiece`: a widget fixed to an anchor of an instance this widget uses.
- **Composition:**
  - Widgets nest in widgets, fractally. A station holds a desk, a plate and a robot. The robot holds a torso, with the
    head and the arms on the torso's anchors. The torso holds Mac minis, each with its status light.
  - An attached widget becomes a child of the instance whose anchor it is on, so it follows that instance when it is
    posed. Lego instead places an attached part in the current block.
- **Expansion:** `Widgets.Expand` makes a tree of instances. Each instance has:
  - a path, such as `s-…/desk/robot/torso/head/beacon`;
  - a placement in its parent's frame;
  - a mesh key (widget and arguments).

  It never throws. It refuses, with the path and the reason:
  - missing or duplicated widgets, and bad arguments;
  - cycles (a widget under itself with the same arguments);
  - depth over 8, and budgets over 2048 instances, 40000 boxes, 256 labels or 512 meshes;
  - names that are not path-safe.
- **Hierarchy, or flat:** roots are placed in the world. Nothing obliges a module to nest; a flat list of roots works.

## Animation and clicks

- **Poses:** every frame, `IWidgetModule.Pose(seconds, poses)` fills a pose per path that is not at rest. A pose has:
  - `Transform`: in the instance's own frame, before its placement; it moves everything under the instance.
  - `Glow`: the scene shader's additive glow, rgb × w, w at most 4.
  - `Hidden`: hides the subtree.
- **Meshes:** the host builds a mesh once per distinct mesh key, shared by every instance with that key. The tree is
  expanded again only when `Revision` changes.
- **Released meshes:** a mesh the tree no longer needs is freed `FramesInFlight + 1` frames later.
- **Clicks:** a `UsePiece.Click` is what a click on that instance asks the host for. An instance without one passes the
  click to its parent's. The nearest projected bounds under the cursor wins. The module never names a command.

## Hot reload

- **Watching:** `WidgetModuleSession` watches the image named by `--office-module`, at most once a second.
- **Loading:** when a build writes a new image and the file holds still for a poll, the new image is loaded into a fresh
  collectible context and initialized. Only then does it replace the running code; the stage then rebuilds every mesh.
- **Refused images:** a refused image (bad metadata, wrong identity, failing `Initialize`) keeps the running code, and is
  not tried again until the file changes.
- **No lock:** the image is read into memory, so `dotnet build` can always overwrite it.

## The agents' office

- **Stations:** one per running Claude Code session, on the robot's left (x from -1.45 to -0.69, rows from z = 0.3 going
  back). Above 12 sessions the stations come closer and smaller.
- **Activities:**

  | Activity | Source | What the robot does |
  |---|---|---|
  | working | wmux `agent-state` (or the session file's `busy` outside wmux) | its hands type, a light runs up its torso, cyan eyes and screen |
  | waiting | wmux `blocked` | a red beacon blinks on its head |
  | done | working, then no longer | it jumps, arms up, for 2.4 s, then shows green until 6 s |
  | idle | otherwise | dim |

- **Data:** read every three seconds on the thread pool: `Shelf.RunningSessions()` and one `wmux agent-state` call
  (`Shelf.Wmux.AgentStates`, about 0.6 s). A read is skipped while the last one still waits. Nothing is written.
- **Click:** a click on a station calls `Shelf.Wmux.Focus(surface)` for that session.
- **Flat quality:** a board on a post instead, a lamp and a name per session.
- **`--demo`:** six made-up sessions go through every activity; nothing is read.

```
dotnet build modules/AgentOffice/AgentOffice.Module.csproj -c Release -o <dir>
scene.exe ... --office-module <dir>/AgentOffice.Module.dll
```

Rebuilding the module into the same `<dir>` while the scene runs reloads it.

## Checks

`widget-tests/Widgets.SelfTest.csproj` runs on the real office images. Arguments: a v0.1 and a v0.2 build of
`AgentOffice.Module.dll`, built with `-p:Version=…`. It covers:

- expansion: fractal nesting, cycles, limits, arguments, anchors;
- the stage: shared meshes, poses carried down the tree, hidden subtrees, inherited clicks, deferred release, refused
  trees, reset;
- the activities;
- picking through the scene's camera;
- the office: every path it poses exists in its tree, in 3D and flat, and 24 sessions fit;
- hot reload: v0.1 to v0.2, the old context collected, a broken image refused.
