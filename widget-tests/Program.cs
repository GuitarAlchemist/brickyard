using Delta.SceneModules;
using System.Numerics;
using System.Runtime.CompilerServices;

// CPU checks of the widget system and the agents' office, on the real office module images (v0.1 and v0.2); only the
// mesh port is simulated. Arguments: the two AgentOffice.Module.dll images.
var failures = 0;
void Check(bool ok, string message)
{
    Console.WriteLine($"WIDGET TEST {(ok ? "ok" : "FAIL")} {message}");
    if (!ok) failures++;
}

try
{
    if (args.Length != 2) throw new ArgumentException("Expected the paths to the v0.1 and v0.2 office module images.");
    Expansion(Check);
    Stage(Check);
    Activities(Check);
    Known(Check);
    Selection(Check);
    WidgetPicking.SelfTest(Check);
    Office(Check, args[0]);
    Enclosures(Check, args[0]);
    Reload(Check, args[0], args[1]);
}
catch (Exception e)
{
    Check(false, $"unexpected {e.GetType().Name}: {e.Message}");
}

Console.WriteLine(failures == 0 ? "WIDGET TESTS PASSED" : $"WIDGET TESTS FAILED {failures}");
return failures == 0 ? 0 : 1;

static WidgetScene One(IReadOnlyList<Widget> library, WidgetId root, Dictionary<string, WidgetValue>? args = null) =>
    new(library, [new UsePiece("root", root, Matrix4x4.Identity, args)]);

static void Expansion(Action<bool, string> check)
{
    WidgetId cube = new("cube", 1), tower = new("tower", 1), loop = new("loop", 1), arm = new("arm", 1), body = new("body", 1);
    var red = new Vector3(1, 0, 0);
    List<Widget> library =
    [
        new(cube, [new("size", 0.1, 0.001, 1)], a => new([new BoxPiece(Vector3.Zero, new((float)a.Number("size")), red)])),
        // A fractal: a cube with two half-size towers on it, down to level 0.
        new(tower, [new("level", 3.0, 0, 8), new("size", 0.4, 0.001, 1)], a =>
        {
            var level = a.Number("level");
            var size = (float)a.Number("size");
            List<Piece> pieces = [new UsePiece("cube", cube, Matrix4x4.Identity, new Dictionary<string, WidgetValue> { ["size"] = size })];
            if (level > 0)
                foreach (var (name, x) in new[] { ("l", -size / 4), ("r", size / 4) })
                    pieces.Add(new UsePiece(name, tower, Widgets.Place(x, size, 0f), new Dictionary<string, WidgetValue> { ["level"] = level - 1, ["size"] = size / 2 }));
            return new(pieces);
        }),
        new(loop, [], _ => new([new UsePiece("again", loop, Matrix4x4.Identity)])),
        new(arm, [], _ => new([new BoxPiece(Vector3.Zero, new(0.02f), red)], new Dictionary<string, Matrix4x4> { ["hand"] = Matrix4x4.CreateTranslation(0, -0.3f, 0) })),
        new(body, [], _ => new([
            new UsePiece("arm", arm, Matrix4x4.CreateTranslation(0.2f, 1f, 0)),
            new AttachPiece("arm", "hand", new("glove", cube, Matrix4x4.Identity, new Dictionary<string, WidgetValue> { ["size"] = 0.05 })),
        ])),
    ];

    var fractal = Widgets.Expand(One(library, tower));
    check(fractal.Valid && fractal.Instances == 30 && fractal.Meshes == 4,
        $"a widget nests itself fractally: 15 towers and 15 cubes, the 4 cube sizes one mesh each ({fractal.Instances} instances, {fractal.Meshes} meshes)");
    check(Find(fractal.Roots, "root/l/r/l/cube") is { } leaf && leaf.Mesh.Contains("size=0.05"), "paths name each instance through its parents");

    var cycle = Widgets.Expand(One(library, loop));
    check(!cycle.Valid && cycle.Problems.Any(p => p.Kind == "cycle"), "a widget under itself with the same arguments is refused as a cycle");
    var deep = Widgets.Expand(One(library, tower, new() { ["level"] = 8.0 }), WidgetLimits.Standard with { MaxDepth = 4 });
    check(!deep.Valid && deep.Problems.Any(p => p.Kind == "too-deep"), "nesting deeper than the limit is refused");
    var big = Widgets.Expand(One(library, tower, new() { ["level"] = 8.0 }), WidgetLimits.Standard with { MaxInstances = 100 });
    check(!big.Valid && big.Problems.Count(p => p.Kind == "too-many-instances") == 1 && big.Instances <= 101, "a spent instance budget stops all expansion");
    check(!Widgets.Expand(One(library, tower, new() { ["level"] = 9.0 })).Valid && !Widgets.Expand(One(library, tower, new() { ["speed"] = 1.0 })).Valid &&
        !Widgets.Expand(One(library, tower, new() { ["level"] = "trois" })).Valid, "arguments out of bounds, unknown or of the wrong kind are refused");
    check(!Widgets.Expand(One(library, new("absent", 1))).Valid, "a widget missing from the library is refused");
    check(!Widgets.Expand(new([.. library, library[0]], [new UsePiece("c", cube, Matrix4x4.Identity)])).Valid, "a library naming a widget twice is refused");

    var attached = Widgets.Expand(One(library, body));
    var glove = Find(attached.Roots, "root/arm/glove");
    check(attached.Valid && glove is not null && Matrix4x4.Decompose(glove.Local, out _, out _, out var at) && Near(at, new(0, -0.3f, 0)),
        "a widget fixed to an anchor becomes the anchored instance's child, in the anchor's frame");
}

static void Stage(Action<bool, string> check)
{
    var module = new TestModule();
    using var stage = new WidgetStage<TestSurface>(i => new TestSurface(i.Mesh), keepFrames: 3);
    check(stage.Sync(module) && stage.Instances == 6 && stage.Meshes == 2 && stage.Built == 2, "the tree is expanded and identical widgets share one mesh");
    check(!stage.Sync(module), "an unchanged revision expands nothing again");
    stage.Frame(module, 0);
    check(stage.Items.Count == 4 && stage.Hits.Count(h => h.Click == "desk-a") == 2, "every drawn instance under a click target takes its parent's target");

    module.Poses["a"] = Pose.Moved(new(0, 1, 0));
    module.Poses["a/arm"] = new(Matrix4x4.CreateRotationZ(MathF.PI / 2));
    module.Poses["b/arm"] = Pose.Off;
    stage.Frame(module, 1);
    var hand = stage.Items.Single(i => i.Surface.Mesh.StartsWith("hand", StringComparison.Ordinal) && i.World.M42 > 1.5f);
    check(Near(Vector3.Transform(Vector3.Zero, hand.World), new(-0.5f, 2f, 0)),
        "a pose moves everything under it: the parent's move and the arm's turn carry the hand");
    check(stage.Items.Count == 2, "a hidden instance hides everything under it");
    module.Poses.Clear();
    module.Poses["b/arm/hand"] = Pose.Lit(new(1, 0, 0, 9));
    stage.Frame(module, 2);
    check(stage.Items.Any(i => i.Glow.W == 4f), "a pose's glow lights its own mesh, its strength bounded");

    module.Count = 1;
    stage.Sync(module);
    check(stage.Released == 0 && stage.Meshes == 2, "a mesh still used stays");
    module.Mesh = "hand-2";
    stage.Sync(module);
    for (var f = 0; f < 3; f++) stage.Frame(module, 3 + f);
    check(stage.Released == 0, "a released mesh outlives the frames in flight that may still draw it");
    stage.Frame(module, 6);
    check(stage.Released == 1 && stage.Meshes == 2, "then it is freed");

    module.Broken = true;
    check(!stage.Sync(module) && stage.Problems.Count > 0 && stage.Instances > 0, "a refused tree keeps the one shown and says why");

    // New code: every mesh goes, but none is freed while a frame in flight may still draw it.
    var before = stage.Released;
    stage.Reset();
    var freed = new List<int>();
    for (var f = 0; f < 5; f++)
    {
        stage.Frame(module, 10 + f);
        freed.Add(stage.Released - before);
    }

    check(freed.SequenceEqual([0, 0, 0, 2, 2]) && stage.Instances == 0,
        $"a reset for new code releases every mesh, none before keepFrames frames ({string.Join(",", freed)} freed after each frame)");
}

static void Activities(Action<bool, string> check)
{
    var last = new Dictionary<string, OfficeSession>();
    OfficeActivities.Seen S(string id, string state, string status = "idle", long started = 0) => new(id, id, started, status, state, "surf-" + id);
    var first = OfficeActivities.Derive([S("b", "working", started: 2), S("a", "blocked", started: 1), S("c", "", "busy", 3)], last, 10);
    check(first.Select(s => s.Id).SequenceEqual(["a", "b", "c"]) && first[0].Activity == OfficeActivity.Waiting &&
        first[1].Activity == OfficeActivity.Working && first[2].Activity == OfficeActivity.Working,
        "blocked waits, working works, a session outside wmux works while busy; desks keep their start order");
    var later = OfficeActivities.Derive([S("a", "working", started: 1), S("b", "idle", started: 2), S("c", "", "busy", 3)], last, 12);
    check(later[1].Activity == OfficeActivity.Done && later[1].Since == 12 && later[2].Since == 10, "a session that stops working is done, from then; unchanged ones keep their start");
    var after = OfficeActivities.Derive([S("a", "idle", started: 1), S("b", "idle", started: 2)], last, 12 + OfficeActivities.DoneFor);
    check(after[1].Activity == OfficeActivity.Idle && after[0].Activity == OfficeActivity.Done && after[2] is { Id: "c", Activity: OfficeActivity.Stopped },
        "done lasts a few seconds then is idle; a session gone stays, stopped");
    var answered = OfficeActivities.Derive([S("w", "blocked")], [], 0);
    var dismissed = OfficeActivities.Derive([S("w", "idle")], new() { ["w"] = new("w", "w", OfficeActivity.Waiting, 0) }, 1);
    check(answered[0].Activity == OfficeActivity.Waiting && dismissed[0].Activity == OfficeActivity.Idle, "a wait ended without work is not shown as done");
    var resumed = OfficeActivities.Derive([new("r", "ix-51", 1, "busy", "", ""), S("q", "idle", started: 2), new("r", "ix-ad", 3, "idle", "", "")], [], 0);
    check(resumed.Count == 2 && resumed.Select(s => s.Id).SequenceEqual(["q", "r"]) && resumed[1].Name == "ix-ad" && resumed[1].Activity == OfficeActivity.Idle,
        "two processes with one session ID make one desk, the one started last");
}

// The office's first UX slice (BRICKYARD-UX-HANDOFF-REFERENCE.md): who is known, how the panes map to sessions, and
// what a selection does.
static void Known(Action<bool, string> check)
{
    // Five wmux panes: two Claude sessions with their files, one Claude session wmux knows without a file, a Codex pane
    // and a pane with no session. Three robots, the third unknown; no robot for the others.
    List<OfficeActivities.SessionFile> files = [new("a1", "ix", 1, "busy", true), new("b2", "ix", 2, "idle", true)];
    var states = new Dictionary<string, (string, string, string)>
    {
        ["a1"] = ("surf-1", "working", "claude"), ["b2"] = ("surf-2", "idle", "claude"), ["c3"] = ("surf-3", "idle", "claude"),
        ["x4"] = ("surf-4", "working", "codex"), [""] = ("surf-5", "idle", ""),
    };
    var workspaces = new Dictionary<string, (string, string)> { ["surf-1"] = ("ws-1", "ix"), ["surf-2"] = ("ws-1", "ix"), ["surf-3"] = ("ws-2", "ga") };
    var five = OfficeActivities.Derive(OfficeActivities.Merge(files, states, workspaces), [], 0);
    check(five.Count == 3 && five.Select(s => s.Id).ToHashSet().SetEquals(["a1", "b2", "c3"]) && five.Single(s => s.Id == "c3").Activity == OfficeActivity.Unknown,
        $"five panes make three robots: two sessions with their files and one wmux knows, unknown; none for Codex or an empty pane ({five.Count})");
    check(five.Single(s => s.Id == "a1") is { WorkspaceId: "ws-1", WorkspaceTitle: "ix", Started: 1 } && five.Single(s => s.Id == "c3").WorkspaceId == "ws-2",
        "each session carries its pane's workspace and its start");

    // A file whose process is gone is stale; a session with no status and no wmux state is unknown.
    var none = new Dictionary<string, (string, string, string)>();
    var odd = OfficeActivities.Derive(OfficeActivities.Merge([new("d", "old", 1, "busy", false), new("e", "new", 2, "", true)], none, new Dictionary<string, (string, string)>()), [], 0);
    check(odd[0].Activity == OfficeActivity.Stale && odd[1].Activity == OfficeActivity.Unknown, "a file without its process is stale; a session that says nothing is unknown");

    // A known session that stops stays, stopped, with its name, place and start; past StoppedKept, the oldest goes.
    var last = new Dictionary<string, OfficeSession>();
    OfficeActivities.Derive(OfficeActivities.Merge(files, states, workspaces), last, 0);
    var stopped = OfficeActivities.Derive(OfficeActivities.Merge([files[1]], states, workspaces), last, 5);
    var a1 = stopped.FindIndex(s => s.Id == "a1");
    check(stopped.Count == 3 && a1 >= 0 && stopped[a1] is { Name: "ix", Activity: OfficeActivity.Stopped, WorkspaceId: "ws-1", Since: 5, Started: 1 } && a1 < stopped.FindIndex(s => s.Id == "b2"),
        "a known session that stops stays visible, stopped, in its place, even while wmux still lists its pane");
    var many = new Dictionary<string, OfficeSession>();
    OfficeActivities.Seen Z(int k) => new($"z{k}", $"z{k}", k, "idle", "", "");
    OfficeActivities.Derive([.. Enumerable.Range(0, OfficeActivities.StoppedKept + 2).Select(Z)], many, 0);
    for (var i = 0; i < OfficeActivities.StoppedKept + 2; i++)
    {
        OfficeActivities.Derive([.. Enumerable.Range(i + 1, OfficeActivities.StoppedKept + 1 - i).Select(Z)], many, 1 + i);
    }

    var kept = OfficeActivities.Derive([], many, 100);
    check(kept.Count == OfficeActivities.StoppedKept && kept.All(s => s.Activity == OfficeActivity.Stopped) && !kept.Any(s => s.Id is "z0" or "z1"),
        $"at most {OfficeActivities.StoppedKept} stopped sessions stay, the latest to stop ({kept.Count})");
}

// A click on a robot selects it and does nothing else; a second click soon after on the same robot is the explicit
// move to its pane; the selection is the scene's own and goes when its session is replaced.
static void Selection(Action<bool, string> check)
{
    var selection = new OfficeSelection();
    var first = selection.Click("a1", 7, 10.0);
    var other = selection.Click("b2", 8, 12.0);
    var again = selection.Click("b2", 8, 13.0);
    check(first == OfficeSelection.Outcome.Selected && other == OfficeSelection.Outcome.Selected && again == OfficeSelection.Outcome.Cleared && selection.Selected is null,
        "a click selects a robot, another click on it later lets it go; no command");
    selection.Click("a1", 7, 20.0);
    var opened = selection.Click("a1", 7, 20.0 + (OfficeSelection.DoubleClick * 0.5));
    check(opened == OfficeSelection.Outcome.Open && selection.Selected == "a1", "two quick clicks on a robot are the explicit move to its pane, and it stays selected");
    var commands = 0;
    foreach (var (id, t) in new[] { ("a1", 30.0), ("b2", 31.0), ("b2", 33.0), ("c3", 34.0) })
    {
        commands += selection.Click(id, 1, t) == OfficeSelection.Outcome.Open ? 1 : 0;
    }

    check(commands == 0, "selecting robots, however many, calls no command and no model");
    selection.Click(null, 0, 40.0);
    check(selection.Selected is null, "a click beside the desks lets the selection go");

    selection.Click("a1", 7, 50.0);
    var same = selection.Validate([new("a1", "ix", OfficeActivity.Stopped, 1, Started: 7)]);
    var replaced = selection.Validate([new("a1", "ix", OfficeActivity.Working, 2, Started: 9)]);
    check(!same && replaced && selection.Selected is null, "the selection stays on a stopped session and goes when the session runs again in a new process");
    selection.Click("b2", 8, 60.0);
    check(selection.Validate([]) && selection.Selected is null, "and goes with its session");
}

static void Office(Action<bool, string> check, string image)
{
    using var session = new WidgetModuleSession<IOfficeModule>(image, "AgentOffice.Module", "Delta.AgentOffice.OfficeModule", "agent-office");
    var office = session.Module;
    OfficeSession S(int i, OfficeActivity a) => new($"0000{i:x4}-aaaa-bbbb-cccc-{i:x12}", $"session {i}", a, 1);
    List<OfficeSession> sessions = [S(1, OfficeActivity.Working), S(2, OfficeActivity.Waiting), S(3, OfficeActivity.Done), S(4, OfficeActivity.Idle)];
    foreach (var (flat, what) in new[] { (false, "3D"), (true, "flat") })
    {
        office.Update(new("fr", flat, sessions));
        var tree = Widgets.Expand(office.Scene());
        check(tree.Valid, $"the office's {what} view expands within the limits ({tree.Instances} instances, {tree.Meshes} meshes){string.Concat(tree.Problems.Take(3).Select(p => "; " + p))}");
        var paths = new HashSet<string>(All(tree.Roots).Select(i => i.Path));
        var poses = new Dictionary<string, Pose>();
        foreach (var t in new[] { 1.0, 1.2, 1.9, 2.3 }) office.Pose(t, poses);
        var unknown = poses.Keys.Where(p => !paths.Contains(p)).ToList();
        check(poses.Count > 0 && unknown.Count == 0, $"every path the {what} view poses is an instance of its tree{string.Concat(unknown.Take(3).Select(p => " " + p))}");
        var clicks = All(tree.Roots).Select(i => i.Click).OfType<string>().ToHashSet();
        check(clicks.SetEquals(sessions.Select(s => s.Id)), $"in the {what} view each session's widget asks to show that session");
    }

    office.Update(new("en", false, sessions));
    var tree3d = Widgets.Expand(office.Scene());
    check(tree3d.Meshes < tree3d.Instances / 4, $"the stations share their meshes: only the name plates differ ({tree3d.Meshes} meshes for {tree3d.Instances} instances)");
    check(Find(tree3d.Roots, "s-00000002aaaa/desk/robot/torso/head/beacon/light") is not null, "the beacon is on the head, fixed through the robot's anchors");
    var revision = office.Revision;
    office.Update(new("en", false, [.. sessions.Select(s => s with { Activity = OfficeActivity.Idle })]));
    check(office.Revision == revision, "an activity change poses, it does not change the tree");
    office.Update(new("en", false, [.. Enumerable.Range(1, 24).Select(i => S(i, OfficeActivity.Working))]));
    var crowd = Widgets.Expand(office.Scene());
    check(crowd.Valid && office.Revision != revision, $"24 sessions still fit ({crowd.Instances} instances)");
    office.Update(new("en", false, []));
    check(Widgets.Expand(office.Scene()).Valid, "an office without sessions still shows its empty desk");
}

// Readable names, an enclosure per workspace, stable identities and the selection's mark, on the real module.
static void Enclosures(Action<bool, string> check, string image)
{
    using var session = new WidgetModuleSession<IOfficeModule>(image, "AgentOffice.Module", "Delta.AgentOffice.OfficeModule", "agent-office");
    var office = session.Module;
    OfficeSession S(string id, string name, string ws, string title, OfficeActivity a = OfficeActivity.Idle) => new(id, name, a, 1, ws, title, 1);
    List<OfficeSession> sessions =
    [
        S("aaaa1111", "ix", "ws-1", "ix"), S("bbbb2222", "ix", "ws-1", "ix"), S("cccc3333", "gaia", "ws-1", "ix"),
        S("dddd4444", "ga", "ws-2", "ga", OfficeActivity.Stopped), S("eeee5555", "tars", "ws-2", "ga", OfficeActivity.Stale),
        S("ffff6666", "brickyard", "", "", OfficeActivity.Unknown),
    ];
    office.Update(new("fr", false, sessions));
    var tree = Widgets.Expand(office.Scene());
    check(tree.Valid, $"the office with workspaces expands within the limits{string.Concat(tree.Problems.Take(3).Select(p => "; " + p))}");
    string Plate(string id) => Find(tree.Roots, "s-" + id + "/plate")?.Text.FirstOrDefault()?.Text ?? "";
    check(Plate("aaaa1111") != Plate("bbbb2222") && Plate("aaaa1111").StartsWith("ix") && Plate("bbbb2222").StartsWith("ix") && Plate("cccc3333") == "gaia",
        $"two sessions with one name keep distinct plates ({Plate("aaaa1111")}, {Plate("bbbb2222")}); a unique name stays as it is");

    // An enclosure per workspace, the sessions outside wmux in one of their own; each station inside its own, none overlapping.
    var pens = tree.Roots.Where(r => r.Path.StartsWith("w-")).ToList();
    (Vector2 Min, Vector2 Max) Rect(WidgetInstance pen)
    {
        var at = pen.Local.Translation;
        var boxes = All([pen]).SelectMany(i => i.Boxes).ToList();
        var min = new Vector2(at.X + boxes.Min(b => b.Centre.X - (b.Size.X / 2)), at.Z + boxes.Min(b => b.Centre.Z - (b.Size.Z / 2)));
        var max = new Vector2(at.X + boxes.Max(b => b.Centre.X + (b.Size.X / 2)), at.Z + boxes.Max(b => b.Centre.Z + (b.Size.Z / 2)));
        return (min, max);
    }

    bool Inside(Vector3 p, (Vector2 Min, Vector2 Max) r) => p.X > r.Min.X && p.X < r.Max.X && p.Z > r.Min.Y && p.Z < r.Max.Y;
    bool Apart((Vector2 Min, Vector2 Max) a, (Vector2 Min, Vector2 Max) b) => a.Max.X <= b.Min.X || b.Max.X <= a.Min.X || a.Max.Y <= b.Min.Y || b.Max.Y <= a.Min.Y;
    var rects = pens.ToDictionary(p => p.Path, Rect);
    var homes = new Dictionary<string, string> { ["aaaa1111"] = "w-ws1", ["bbbb2222"] = "w-ws1", ["cccc3333"] = "w-ws1", ["dddd4444"] = "w-ws2", ["eeee5555"] = "w-ws2", ["ffff6666"] = "w-outside" };
    var housed = homes.All(h => rects.ContainsKey(h.Value) && tree.Roots.FirstOrDefault(r => r.Path == "s-" + h.Key) is { } st && Inside(st.Local.Translation, rects[h.Value]));
    var list = rects.Values.ToList();
    var apart = list.SelectMany((a, i) => list.Skip(i + 1).Select(b => Apart(a, b))).All(x => x);
    check(pens.Count == 3 && housed && apart, $"an enclosure per workspace and one for the sessions outside wmux, each station in its own, none overlapping ({pens.Count})");
    check(pens.All(p => All([p]).Any(i => i.Text.Count > 0)), "each enclosure carries its workspace's name");

    // A refresh in another order, a workspace renamed: the same paths, the stations where they were.
    var before = tree.Roots.ToDictionary(r => r.Path, r => r.Local.Translation);
    office.Update(new("fr", false, [.. sessions.AsEnumerable().Reverse().Select(s => s.WorkspaceId == "ws-2" ? s with { WorkspaceTitle = "ga-renamed" } : s)]));
    var after = Widgets.Expand(office.Scene()).Roots.ToDictionary(r => r.Path, r => r.Local.Translation);
    check(before.Keys.ToHashSet().SetEquals(after.Keys) && homes.Keys.All(id => Near(before["s-" + id], after["s-" + id])),
        "a refresh in another order or a renamed workspace keeps every path and every station's place");

    // The selection's mark: posed over the selected robot only, the tree untouched.
    office.Update(new("fr", false, sessions));
    var revision = office.Revision;
    office.Update(new("fr", false, sessions, "cccc3333"));
    var poses = new Dictionary<string, Pose>();
    office.Pose(2.0, poses);
    var marks = homes.Keys.Select(id => (Id: id, Pose: poses.TryGetValue("s-" + id + "/mark", out var p) ? p : (Pose?)null)).ToList();
    check(office.Revision == revision && marks.All(m => m.Pose is { } p && (m.Id == "cccc3333" ? !p.Hidden && p.Glow.W > 0 : p.Hidden)),
        "selecting a robot lights a mark over it alone and rebuilds nothing");
    var mark = Find(Widgets.Expand(office.Scene()).Roots, "s-cccc3333/mark");
    check(mark is not null && mark.Local.Translation.Y > 0.68f && All([mark]).SelectMany(i => i.Boxes).All(b => b.Size.X <= 0.05f),
        "the mark floats above the robot's head, small enough to hide nothing");
}

static void Reload(Action<bool, string> check, string v1, string v2)
{
    var dir = Path.Combine(Path.GetTempPath(), "widget-reload-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var live = Path.Combine(dir, "AgentOffice.Module.dll");
    try
    {
        File.Copy(v1, live);
        var session = new WidgetModuleSession<IOfficeModule>(live, "AgentOffice.Module", "Delta.AgentOffice.OfficeModule", "agent-office");
        var first = session.Version;
        File.Copy(v2, live, overwrite: true);
        File.SetLastWriteTimeUtc(live, DateTime.UtcNow.AddSeconds(5));
        var reloaded = session.Poll(1) || session.Poll(2) || session.Poll(3);
        check(reloaded && first == new Version(0, 1, 0, 0) && session.Version == new Version(0, 2, 0, 0) && session.Reloads == 1,
            $"a new image written over the module replaces it once the file holds still ({first} -> {session.Version})");
        check(Collected(session.CodeContextReference), "the replaced code's context is collected");
        session.Module.Update(new("en", false, []));
        check(Widgets.Expand(session.Module.Scene()).Valid, "the new code runs");

        File.WriteAllBytes(live, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(live, DateTime.UtcNow.AddSeconds(10));
        var refused = session.Poll(4) || session.Poll(5) || session.Poll(6);
        check(!refused && session.Version == new Version(0, 2, 0, 0) && session.LastError is not null, "a broken image is refused and the running code stays");
        session.Dispose();
    }
    finally
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}

[MethodImpl(MethodImplOptions.NoInlining)]
static bool Collected(WeakReference reference)
{
    for (var i = 0; i < 10 && reference.IsAlive; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    return !reference.IsAlive;
}

static IEnumerable<WidgetInstance> All(IEnumerable<WidgetInstance> roots) => roots.SelectMany(r => All(r.Children).Prepend(r));
static WidgetInstance? Find(IEnumerable<WidgetInstance> roots, string path) => All(roots).FirstOrDefault(i => i.Path == path);
static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;

internal sealed class TestSurface(string mesh) : IDisposable
{
    public string Mesh { get; } = mesh;
    public void Dispose() { }
}

// Two desks, each with an arm (a mesh) holding a hand (another); one click target per desk.
internal sealed class TestModule : IWidgetModule
{
    public Dictionary<string, Pose> Poses { get; } = [];
    public int Count { get; set; } = 2;
    public string Mesh { get; set; } = "hand";
    public bool Broken { get; set; }
    public string Id => "test";
    public int Revision => HashCode.Combine(Count, Mesh, Broken);
    public void Initialize() { }
    public void Dispose() { }

    public WidgetScene Scene()
    {
        WidgetId desk = new("desk", 1), arm = new("arm", 1), hand = new("hand", 1);
        List<Widget> library =
        [
            new(desk, [], _ => new([new UsePiece("arm", arm, Matrix4x4.CreateTranslation(0, 1, 0))])),
            new(arm, [], _ => new([new BoxPiece(Vector3.Zero, new(0.1f), Vector3.One), new UsePiece("hand", hand, Matrix4x4.CreateTranslation(0, 0.5f, 0))])),
            new(hand, [new("n", Mesh, 1, 10)], a => new([new BoxPiece(Vector3.Zero, new(0.05f), Vector3.One)])),
        ];
        List<UsePiece> roots = [new("a", desk, Matrix4x4.Identity, Click: "desk-a")];
        if (Count > 1) roots.Add(new("b", desk, Matrix4x4.CreateTranslation(2, 0, 0)));
        if (Broken) roots.Add(new("c", new("absent", 1), Matrix4x4.Identity));
        return new(library, roots);
    }

    public void Pose(double seconds, IDictionary<string, Pose> poses)
    {
        foreach (var (path, pose) in Poses) poses[path] = pose;
    }
}
