# The agents' office, UX slice 1: report (2026-10-31)

Requested by Gaia's UX handoff (`BRICKYARD-UX-HANDOFF-REFERENCE.md`, task-6 native-relay-prototype), within the
"tout!" the user gave on 2026-10-31. Branch `feat/office-ux-slice1`, from delta-face 7ba0144, committed locally only.

## What changed

| Ask | Where | How |
|---|---|---|
| One desk per session, never per pane | `scene/OfficeActivities.cs` `Merge` | a session file, or a pane wmux names with a Claude session ID (provider `claude`) and no file; panes without a session or of another provider are not counted |
| A stopped session stays visible | `OfficeActivities.Derive` | a session shown in this run and gone since becomes `Stopped`, in its place, its head bowed and dark; the latest 8 are kept |
| Stale and unknown sessions | `Derive`, `Shelf.SessionFiles` | a file whose process is gone is `Stale` (amber); a session with no status and no wmux state is `Unknown` (grey) |
| Distinct names | `modules/AgentOffice/OfficeModule.cs` `Shown` | two sessions with one name add their IDs' first four letters, within the plate's 18 characters |
| Desks by workspace | `Shelf.Wmux.SurfaceWorkspaces`, the module's `Pen` | an enclosure per wmux workspace with its title on a sign, one more for sessions outside wmux; scaled to fit 2.9 m |
| Stable identity on refresh | the module's `Key`, `PenKey`, `Ordered` | paths from IDs, desks ordered by start then ID, enclosures by their first session's start; a renamed workspace keeps its path |
| A local selection | `scene/OfficeSelection.cs`, Program.cs `TryOfficeClick` | a click selects, with no command and nothing sent; a gem turns above the selected robot (a pose, no new mesh); a second click within 0.45 s shows the pane, the only path to a command; a click beside the desks lets it go and passes the click on |
| Selection invalidated on a new incarnation | `OfficeSelection.Validate`, each frame | the selection goes when its session leaves the office or runs in another process (its start changes); a stopped session keeps it |

The contract (`module-contracts/OfficeModule.cs`) grew compatibly: the three new activities follow `Done`, and the new
record members have defaults, so the v0.1 test image still loads. The selection is not part of the module's revision:
selecting changes poses only, never the tree.

## Checks

- `widget-tests` (both module images, v0.1.0 and v0.2.0): passed. New: `Known` (the five-pane fixture gives three
  robots; stale; unknown; stopped kept while wmux still lists the pane; the bound of 8), `Selection` (select, let go,
  double click, zero commands, a click beside, a new incarnation), `Enclosures` (distinct plates, enclosure paths and
  names, stations inside their enclosure, no overlap, places kept on a reversed or renamed refresh, the mark lit on the
  selected station only, the revision unchanged by a selection).
- Mutations, each restored after: plates without the ID suffix, the mark always lit, unstable enclosure paths, an open
  on any second click, no incarnation check, every provider's panes counted, no stale state. Each one fails the tests.
- The scene: Release build and `--selftest` passed.

## Not done, and why

- **Not seen live.** The handoff forbids hotload and deploy, so the module was not reloaded in the running window and
  nothing was installed. To see it: build the module (`docs/widgets.md`) and start a scene on this branch with
  `--office-module <dir>/AgentOffice.Module.dll`, or `--demo` for made-up sessions in two workspaces.
- **No acknowledgement sent to Gaia.** The Gaia session that wrote the handoff was gone when this work started, so no
  ACK could be sent; this report stands in for it.
- **No selection card.** A hint card naming the selected session was optional and is left out; the gem is the only
  sign.
- **Cost.** `SurfaceWorkspaces` makes a wmux call per workspace (about 0.6 s each, so about 5 s with nine), once every
  ten reads (30 s). It runs on the thread pool, but the activities wait for it.

## Choices left to the user

- Whether a double click is the right move to a pane, rather than a key (Enter) on the selected robot.
- Whether stopped sessions should go after some time rather than staying until eight newer ones stop.
- Whether to keep the workspace reading's cost, or have wmux report a surface's workspace in `agent-state`.

No hotload, deploy, push, merge, Observatory, permission, hook, credential, port or service change was made.
