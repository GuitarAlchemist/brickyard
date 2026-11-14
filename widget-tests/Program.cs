using Delta.SceneModules;
using System.Numerics;
using System.Runtime.CompilerServices;

// CPU checks of the widget system and the agents' office, on the real office module images (v0.1 and v0.2); only the
// mesh port is simulated. Arguments: the two AgentOffice.Module.dll images. --wall runs only the wall screen's checks
// (WallSource), which need no image.
var failures = 0;
void Check(bool ok, string message)
{
    Console.WriteLine($"WIDGET TEST {(ok ? "ok" : "FAIL")} {message}");
    if (!ok) failures++;
}

try
{
    if (args is ["--wall"])
    {
        WallSource.SelfTest(Check);
        Console.WriteLine(failures == 0 ? "WIDGET TESTS PASSED" : $"WIDGET TESTS FAILED {failures}");
        return failures == 0 ? 0 : 1;
    }

    if (args.Length != 2) throw new ArgumentException("Expected the paths to the v0.1 and v0.2 office module images.");
    // The office also draws the IX workshop from a document on this machine; these checks give it one of their own.
    Environment.SetEnvironmentVariable("DELTA_IX_ATELIER", Path.Combine(Path.GetTempPath(), "delta-no-atelier-" + Guid.NewGuid().ToString("N"), "ix-atelier.json"));
    Environment.SetEnvironmentVariable("DELTA_IX_ORIGAMI", Path.Combine(Path.GetTempPath(), "delta-no-origami-" + Guid.NewGuid().ToString("N"), "ix-origami.json"));
    Expansion(Check);
    Surfaces(Check);
    Stage(Check);
    Activities(Check);
    Known(Check);
    Selection(Check);
    WidgetPicking.SelfTest(Check);
    Office(Check, args[0]);
    Enclosures(Check, args[0]);
    Aisles(Check, args[0]);
    Atelier(Check, args[0]);
    Origami(Check, args[0]);
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

// Smooth surfaces: triangles with their own normals and colours, checked as boxes are, counted against their own budget.
static void Surfaces(Action<bool, string> check)
{
    WidgetId sheet = new("sheet", 1);
    var up = Vector3.UnitZ;
    ModuleVertex V(float x, float y, Vector3? normal = null, Vector3? color = null, float z = 0) => new(new(x, y, z), normal ?? up, color ?? new(0.9f, 0.9f, 0.8f));
    ModuleTriangle Good() => new(V(0, 0), V(0.1f, 0), V(0, 0.1f));
    WidgetTree Of(List<ModuleTriangle> triangles, uint material = 0, WidgetLimits? limits = null) =>
        Widgets.Expand(One([new(sheet, [], _ => new([new SurfacePiece(triangles, material)]))], sheet), limits ?? WidgetLimits.Standard);

    List<ModuleTriangle> given = [Good(), new(V(0.1f, 0), V(0.1f, 0.1f), V(0, 0.1f))];
    var tree = Of(given);
    var root = tree.Roots.Count == 1 ? tree.Roots[0] : null;
    given.Clear();
    check(tree.Valid && tree.Triangles == 2 && root is { Mesh.Length: > 0, Surfaces: [{ Triangles.Count: 2 }] },
        $"a surface of two triangles expands into the instance's own mesh, counted ({tree.Triangles} triangles)");
    check(root?.Surfaces[0].Triangles.Count == 2, "the instance keeps a copy of the triangles it checked, whatever becomes of the module's list");

    var bad = new (string What, ModuleTriangle Triangle, uint Material)[]
    {
        ("wound against its normals", new(V(0, 0), V(0, 0.1f), V(0.1f, 0)), 0),
        ("degenerate", new(V(0, 0), V(0.1f, 0), V(0.2f, 0)), 0),
        ("a vertex 20 m away", new(V(0, 0), V(20f, 0), V(0, 0.1f)), 0),
        ("a normal that is not a unit", new(V(0, 0, new Vector3(0, 0, 2)), V(0.1f, 0), V(0, 0.1f)), 0),
        ("a colour beyond 1", new(V(0, 0, color: new Vector3(2, 0, 0)), V(0.1f, 0), V(0, 0.1f)), 0),
        ("a vertex not finite", new(V(0, 0, z: float.NaN), V(0.1f, 0), V(0, 0.1f)), 0),
        ("an unknown material", Good(), 9),
    };
    foreach (var (what, triangle, material) in bad)
    {
        var refused = Of([triangle], material);
        check(!refused.Valid && refused.Problems.Any(p => p.Kind == "bad-shape"), $"a surface's triangle {what} is refused");
    }

    var spent = Of([Good(), Good(), Good()], 0, WidgetLimits.Standard with { MaxTriangles = 2 });
    check(!spent.Valid && spent.Problems.Any(p => p.Kind == "too-many-triangles"), "more triangles than the budget stops the expansion");
}

static void Stage(Action<bool, string> check)
{
    var module = new TestModule();
    using var stage = new WidgetStage<TestSurface>(i => new TestSurface(i.Mesh), keepFrames: 3);
    check(stage.Sync(module) && stage.Instances == 6 && stage.Meshes == 2 && stage.Built == 2, "the tree is expanded and identical widgets share one mesh");
    check(stage.Changes == "+a +b", $"a first tree says the roots it adds, for the log ({stage.Changes})");
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
    check(stage.Changes == "-b", $"a new tree says the roots it removes ({stage.Changes})");
    module.Mesh = "hand-2";
    stage.Sync(module);
    check(stage.Changes == "same roots", $"a new tree with the same roots says so ({stage.Changes})");
    check(WidgetStage<TestSurface>.Diff(new HashSet<string>(), Enumerable.Range(0, 11).Select(i => $"r{i:00}").ToHashSet()) == "+r00 +r01 +r02 +r03 +r04 +r05 +r06 +r07 and 3 more",
        "a long change is cut after eight roots");
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
    office.Update(new("en", false, [.. Enumerable.Range(1, 9).Select(i => S(i, OfficeActivity.Working) with { WorkspaceId = $"ws-{i}", WorkspaceTitle = $"w{i}", Started = i })]));
    var spread = Widgets.Expand(office.Scene());
    check(spread.Valid, $"9 sessions in 9 workspaces still fit, every enclosure within its widget's sizes{string.Concat(spread.Problems.Take(2).Select(p => "; " + p))}");
    var back = spread.Roots.Where(r => r.Path.StartsWith("w-")).Min(r => r.Local.Translation.Z + All([r]).SelectMany(i => i.Boxes).Min(b => b.Centre.Z - (b.Size.Z / 2)));
    var station = spread.Roots.Where(r => r.Path.StartsWith("s-")).Max(r => r.Local.M11);
    check(back > -3.1f && station > 0.45f, $"they stay 0.2 m clear of the bricks (back at {back:F2} m), their stations near half size, the depth spent on them ({station:F2})");
    // Many sessions: a bounded office, the selected session and the latest started shown, the rest counted, never refused.
    foreach (var n in new[] { 56, 57, 60, 100 })
    {
        foreach (var (apart, what) in new[] { (true, "a workspace each"), (false, "one workspace") })
        {
            var many = Enumerable.Range(1, n).Select(i => S(i, OfficeActivity.Working) with { WorkspaceId = apart ? $"ws-{i}" : "ws", WorkspaceTitle = apart ? $"w{i}" : "w", Started = i }).ToList();
            var oldest = many[0].Id;
            var problems = "";
            var ok = true;
            foreach (var flat in new[] { false, true })
            {
                office.Update(new("en", flat, many, oldest));
                var tree = Widgets.Expand(office.Scene());
                var clicks = All(tree.Roots).Select(i => i.Click).OfType<string>().ToHashSet();
                var shown = Math.Min(n, 48);
                var counted = n <= 48 || All(tree.Roots).Any(i => i.Text.Any(t => t.Text.Contains($"+{n - shown}", StringComparison.Ordinal)));
                var latest = many.Skip(n - (shown - 1)).All(s => clicks.Contains(s.Id));
                var poses = new Dictionary<string, Pose>();
                office.Pose(1.0, poses);
                var paths = new HashSet<string>(All(tree.Roots).Select(i => i.Path));
                ok &= tree.Valid && clicks.Count == shown && clicks.Contains(oldest) && latest && counted && poses.Keys.All(paths.Contains);
                problems += string.Concat(tree.Problems.Take(1).Select(p => $"; {(flat ? "flat" : "3D")} {p}"));
            }

            check(ok, $"{n} sessions in {what}: at most 48 drawn, the selected one and the latest started, the rest counted, nothing refused{problems}");
        }
    }

    office.Update(new("en", false, []));
    check(Widgets.Expand(office.Scene()).Valid, "an office without sessions still shows its empty desk");
}

// The aisles between enclosures and their signs, from 3 to 48 workspaces with long titles: footprints apart, a useful
// aisle up to 12, each sign within its rails, the office clear of the bricks.
static void Aisles(Action<bool, string> check, string image)
{
    using var session = new WidgetModuleSession<IOfficeModule>(image, "AgentOffice.Module", "Delta.AgentOffice.OfficeModule", "agent-office");
    var office = session.Module;
    foreach (var n in new[] { 3, 6, 9, 12, 24, 48 })
    {
        office.Update(new("en", false, [.. Enumerable.Range(1, n).Select(i => new OfficeSession($"{i:x8}-aaaa", $"s{i}", OfficeActivity.Idle, 1, $"ws-{i}", $"Workspace {i} with a long forty-two char name", i))]));
        var tree = Widgets.Expand(office.Scene());
        var rects = new List<(Vector2 Min, Vector2 Max)>();
        var inside = true;
        foreach (var pen in tree.Roots.Where(r => r.Path.StartsWith("w-")))
        {
            var at = pen.Local.Translation;
            var boxes = All([pen]).SelectMany(i => i.Boxes).ToList();
            var half = boxes.First(b => b.Size.Y < 0.005f).Size.X / 2; // the floor
            inside &= boxes.All(b => MathF.Abs(b.Centre.X) + (b.Size.X / 2) <= half + 0.0085f);
            rects.Add((new(at.X + boxes.Min(b => b.Centre.X - (b.Size.X / 2)), at.Z + boxes.Min(b => b.Centre.Z - (b.Size.Z / 2))),
                new(at.X + boxes.Max(b => b.Centre.X + (b.Size.X / 2)), at.Z + boxes.Max(b => b.Centre.Z + (b.Size.Z / 2)))));
        }

        var gap = rects.SelectMany((a, i) => rects.Skip(i + 1).Select(b => Math.Max(Math.Max(b.Min.X - a.Max.X, a.Min.X - b.Max.X), Math.Max(b.Min.Y - a.Max.Y, a.Min.Y - b.Max.Y)))).Min();
        var back = rects.Min(r => r.Min.Y);
        var want = n <= 9 ? 0.15f : n <= 12 ? 0.12f : 0.005f;
        check(tree.Valid && rects.Count == n && gap >= want && inside && back > -3.1f,
            $"{n} workspaces: enclosures {gap:F3} m apart (at least {want}), every sign within its rails ({inside}), the back at {back:F2} m");
    }
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
    var gap = list.SelectMany((a, i) => list.Skip(i + 1).Select(b => Math.Max(Math.Max(b.Min.X - a.Max.X, a.Min.X - b.Max.X), Math.Max(b.Min.Y - a.Max.Y, a.Min.Y - b.Max.Y)))).Min();
    check(gap >= 0.15f, $"the enclosures stand a little apart, 0.15 m at least between their rails and signs ({gap:F3} m)");
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

// The IX workshop's stand, drawn by the office's module from the document brain -ix-knot writes (format
// delta-ix-geometry v1; testdata/ix-atelier-figure-eight.json is the brain's document for IX's figure-eight, which
// brain/ixatelier_test.go keeps equal to what the brain writes): the knot and what IX says of it, a refusal for each
// document that breaks the bounds, IX unavailable, and never an old knot once the document says otherwise.
static void Atelier(Action<bool, string> check, string image)
{
    var dir = Path.Combine(Path.GetTempPath(), "delta-atelier-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var doc = Path.Combine(dir, "ix-atelier.json");
    var before = Environment.GetEnvironmentVariable("DELTA_IX_ATELIER");
    Environment.SetEnvironmentVariable("DELTA_IX_ATELIER", doc);
    var figureEight = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "ix-atelier-figure-eight.json"));
    var stamp = DateTime.UtcNow;
    void Write(string text)
    {
        File.WriteAllText(doc, text);
        File.SetLastWriteTimeUtc(doc, stamp = stamp.AddSeconds(2)); // each document a time of its own, as a rename gives it
    }

    try
    {
        using var session = new WidgetModuleSession<IOfficeModule>(image, "AgentOffice.Module", "Delta.AgentOffice.OfficeModule", "agent-office");
        var office = session.Module;
        var clock = 100.0;
        office.Update(new("fr", false, []));
        (WidgetTree Tree, string Plaque, WidgetInstance? Knot, Dictionary<string, Pose> Poses) Look()
        {
            clock += 1.5; // past the module's second between readings
            var poses = new Dictionary<string, Pose>();
            office.Pose(clock, poses);
            var tree = Widgets.Expand(office.Scene());
            poses.Clear();
            office.Pose(clock + 0.1, poses);
            var plaque = string.Join(" | ", Find(tree.Roots, "ix-atelier")?.Text.Select(t => t.Text) ?? []);
            return (tree, plaque, Find(tree.Roots, "ix-atelier/knot"), poses);
        }

        var absent = Look();
        check(absent.Tree.Valid && absent.Plaque.Contains("En attente d'IX") && absent.Knot is null && !absent.Poses.ContainsKey("ix-atelier/knot"),
            $"without a document the stand waits for IX, with no knot ({absent.Plaque})");

        Write(figureEight);
        var revision = office.Revision;
        var shown = Look();
        var rope = shown.Knot?.Surfaces.SelectMany(s => s.Triangles).SelectMany(t => new[] { t.A, t.B, t.C }).ToList() ?? [];
        check(shown.Tree.Valid && office.Revision != revision, $"IX's figure-eight expands within the limits ({shown.Tree.Instances} instances, {shown.Tree.Boxes} boxes, {shown.Tree.Triangles} triangles){string.Concat(shown.Tree.Problems.Take(3).Select(p => "; " + p))}");
        check(shown.Plaque.Contains("Nœud en huit") && shown.Plaque.Contains("4_1 · 4 croisements · writhe 0") && shown.Plaque.Contains("Jones : t^-2 - t^-1 + 1 - t + t^2")
            && shown.Plaque.Contains("chemin calculé par IX") && shown.Plaque.Contains("pas simulée"), $"the plaque says what IX says of it, and what is drawn: {shown.Plaque}");
        check(shown.Knot is { Boxes.Count: 0 } && rope.Count > 3 * 20000 && rope.Count(v => v.Color.X > v.Color.Z * 1.4f && v.Color.Y > v.Color.Z) > rope.Count * 3 / 4
            && rope.Any(v => v.Color.X > v.Color.Y * 3),
            $"the rope is a smooth hemp surface of {rope.Count / 3} triangles, no beads, with its ends whipped in red twine");

        // The rope goes through IX's points: each of them, scaled as the stand scales the knot, has the rope's surface
        // within the rope's radius, and the rope is never further from them than that.
        var document = System.Text.Json.JsonDocument.Parse(figureEight).RootElement;
        var ix = document.GetProperty("ropes")[0].GetProperty("points").EnumerateArray().Select(p => new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle())).ToArray();
        var (low, high) = (ix.Aggregate(Vector3.Min), ix.Aggregate(Vector3.Max));
        var scale = 0.8f / MathF.Max(high.X - low.X, MathF.Max(high.Y - low.Y, high.Z - low.Z));
        var radius = document.GetProperty("radius").GetSingle() * scale;
        var near = ix.Where((_, i) => i % 6 == 0).Select(p => rope.Min(v => Vector3.Distance(v.Position, (p - ((low + high) / 2)) * scale))).Max();
        var far = rope.Where((_, i) => i % 97 == 0).Select(v => ix.Min(p => Vector3.Distance(v.Position, (p - ((low + high) / 2)) * scale))).Max();
        check(rope.Count > 0 && near < radius && far < radius * 1.6f,
            $"the rope follows IX's path: every point IX gives has the rope within {near * 1000:F1} mm, under its radius {radius * 1000:F1} mm, and no part of the rope strays more than {far * 1000:F1} mm");
        var reach = rope.Count == 0 ? 0 : rope.Max(v => MathF.Max(MathF.Abs(v.Position.X), MathF.Max(MathF.Abs(v.Position.Y), MathF.Abs(v.Position.Z))));
        check(reach is > 0.39f and < 0.44f, $"the knot stands 0.8 m at most, about its centre (reaches {reach:F3} m)");
        var turned = new Dictionary<string, Pose>();
        office.Pose(clock + 3.6, turned);
        var swing = Enumerable.Range(0, 64).Select(i => { var p = new Dictionary<string, Pose>(); office.Pose(clock + 0.25 * i, p); return MathF.Atan2(p["ix-atelier/knot"].Transform.M13, p["ix-atelier/knot"].Transform.M11); }).ToList();
        check(shown.Poses.TryGetValue("ix-atelier/knot", out var a) && turned.TryGetValue("ix-atelier/knot", out var b2) && !a.Transform.Equals(b2.Transform)
            && swing.Max() > 1.1f && swing.Min() < -1.1f && swing.All(r => MathF.Abs(r) <= 1.23f),
            $"the knot swings on its stand, 70° either side of its face ({swing.Min() * 180 / MathF.PI:F0}° to {swing.Max() * 180 / MathF.PI:F0}°)");

        office.Update(new("en", false, []));
        var english = Look();
        check(english.Plaque.Contains("Figure-eight knot") && english.Plaque.Contains("three-strand rope"), $"in English: {english.Plaque}");
        office.Update(new("fr", false, []));

        Write("""{"format":"delta-ix-geometry","v":1,"source":"ix_knot","at":"2026-11-14T07:00:00Z","error":"IX unavailable: no ix-mcp on the PATH"}""");
        var unavailable = Look();
        check(unavailable.Tree.Valid && unavailable.Plaque.Contains("IX indisponible") && unavailable.Plaque.Contains("no ix-mcp") && unavailable.Knot is null
            && !unavailable.Poses.ContainsKey("ix-atelier/knot"), $"a document that says IX was unavailable takes the knot away and says why ({unavailable.Plaque})");

        var points = figureEight.IndexOf("\"points\":[", StringComparison.Ordinal) + "\"points\":[".Length;
        var bad = new (string What, string Text)[]
        {
            ("another version", figureEight.Replace("\"v\":1", "\"v\":2")),
            ("another format", figureEight.Replace("delta-ix-geometry", "ix-geometry")),
            ("a flat point", figureEight.Insert(points, "[1,2],")),
            ("a point far away", figureEight.Insert(points, "[1,2000,3],")),
            ("a number as text", figureEight.Insert(points, "[1,\"2\",3],")),
            ("too many points", figureEight.Insert(points, string.Concat(Enumerable.Repeat("[0,0,0],", 4096)))),
            ("nine ropes", figureEight.Replace("\"ropes\":[", "\"ropes\":[" + string.Concat(Enumerable.Repeat("{\"closed\":false,\"points\":[[0,0,0],[1,1,1]]},", 8)))),
            ("a control character", figureEight.Replace("Nœud en huit", "Nœud\\nen huit")),
            ("crossings beyond", figureEight.Replace("\"crossings\":4", "\"crossings\":99")),
            ("no radius", figureEight.Replace("\"radius\":0.28", "\"radius\":0")),
            ("more than 512 KB", figureEight + new string(' ', 512 * 1024)),
            ("not JSON", "IX is down"),
        };
        foreach (var (what, text) in bad)
        {
            Write(figureEight);
            Look();
            Write(text);
            var refused = Look();
            check(refused.Tree.Valid && refused.Plaque.Contains("Géométrie refusée") && refused.Knot is null && !refused.Poses.ContainsKey("ix-atelier/knot"),
                $"{what}: refused, and the knot it replaced goes ({refused.Plaque})");
        }

        File.Delete(doc);
        var gone = Look();
        check(gone.Plaque.Contains("En attente d'IX") && gone.Knot is null, "a document taken away leaves the stand waiting");
    }
    finally
    {
        Environment.SetEnvironmentVariable("DELTA_IX_ATELIER", before);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}

// The IX workshop's origami table, on the brain's documents for IX's reports on three folds (brain/ixorigami_test.go
// keeps them equal): IX's verdict and each finding in words, the crease pattern and the folded state on two boards with
// the edges and vertices at fault in amber, IX unavailable, a refusal for each document that breaks the bounds, and the
// whole office still within the scene's budget with both stands and 48 stations.
static void Origami(Action<bool, string> check, string image)
{
    var dir = Path.Combine(Path.GetTempPath(), "delta-origami-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var doc = Path.Combine(dir, "ix-origami.json");
    var before = Environment.GetEnvironmentVariable("DELTA_IX_ORIGAMI");
    Environment.SetEnvironmentVariable("DELTA_IX_ORIGAMI", doc);
    string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", $"ix-origami-{name}.json"));
    var (flipped, half, wrong, crane) = (Fixture("flipped-crease"), Fixture("half-then-quarter"), Fixture("wrong-expectation"), Fixture("crane"));
    var stamp = DateTime.UtcNow;
    void Write(string text)
    {
        File.WriteAllText(doc, text);
        File.SetLastWriteTimeUtc(doc, stamp = stamp.AddSeconds(2));
    }

    try
    {
        using var session = new WidgetModuleSession<IOfficeModule>(image, "AgentOffice.Module", "Delta.AgentOffice.OfficeModule", "agent-office");
        var office = session.Module;
        var clock = 100.0;
        office.Update(new("fr", false, []));
        (WidgetTree Tree, string Plaque, WidgetInstance? Pattern, WidgetInstance? Folded, WidgetInstance? Model, Dictionary<string, Pose> Poses) Look()
        {
            clock += 1.5;
            office.Pose(clock, new Dictionary<string, Pose>());
            var tree = Widgets.Expand(office.Scene());
            var poses = new Dictionary<string, Pose>();
            office.Pose(clock + 0.1, poses);
            var plaque = string.Join(" | ", Find(tree.Roots, "ix-origami")?.Text.Select(t => t.Text) ?? []);
            return (tree, plaque, Find(tree.Roots, "ix-origami/pattern"), Find(tree.Roots, "ix-origami/folded"), Find(tree.Roots, "ix-origami/model"), poses);
        }

        static IEnumerable<ModuleVertex> Corners(WidgetInstance? w) => w?.Surfaces.SelectMany(s => s.Triangles).SelectMany(t => new[] { t.A, t.B, t.C }) ?? [];
        static bool Has(WidgetInstance? board, float r, float g, float b) =>
            board?.Boxes.Any(x => Near(x.Color, new(r, g, b))) == true || Corners(board).Any(v => Near(v.Color, new(r, g, b)));
        static float Reach(WidgetInstance? board) => Corners(board).Select(v => MathF.Max(MathF.Abs(v.Position.X), MathF.Abs(v.Position.Y - 0.18f))).DefaultIfEmpty(0).Max();
        static bool Cream(Vector3 c) => c.Y > c.X * 0.9f; // the sheet's back; its front, kami red, has little green
        // The model's sides seen from in front (+z) and from behind: the colour of the highest upward side and of the lowest
        // downward side, and how many layers it has.
        static (Vector3 Front, Vector3 Back, int Layers) Sides(WidgetInstance? model)
        {
            var triangles = model?.Surfaces.SelectMany(s => s.Triangles).ToList() ?? [];
            if (triangles.Count == 0) return (default, default, 0);
            var upward = triangles.Where(t => t.A.Normal.Z > 0).ToList();
            var downward = triangles.Where(t => t.A.Normal.Z < 0).ToList();
            var layers = upward.Select(t => MathF.Round(t.A.Position.Z * 10000)).Distinct().Count();
            return (upward.MaxBy(t => t.A.Position.Z)!.A.Color, downward.MinBy(t => t.A.Position.Z)!.A.Color, layers);
        }

        var absent = Look();
        check(absent.Tree.Valid && absent.Plaque.Contains("En attente d'IX") && absent.Pattern is null, $"without a document the table waits for IX ({absent.Plaque})");

        Write(flipped);
        var refused = Look();
        check(refused.Tree.Valid && refused.Plaque.Contains("Origami · flipped-crease") && refused.Plaque.Contains("Refusé par IX : adjacency")
            && refused.Plaque.Contains("Couches : l'ordre déclaré contredit le pli à l'arête 7") && refused.Plaque.Contains("Maekawa : |M − V| ≠ 2 au sommet 3")
            && refused.Plaque.Contains("pas simulé"), $"IX's refusal and each finding in words: {refused.Plaque}");
        check(refused.Pattern is not null && Has(refused.Pattern, 1f, 0.62f, 0.2f) && Has(refused.Pattern, 0.2f, 0.45f, 0.95f)
            && !Has(refused.Pattern, 0.9f, 0.22f, 0.18f), "the pattern's valleys are blue, and the edge and the vertex at fault amber (no mountain is left in it)");
        check(refused.Model is not null && refused.Folded is null && refused.Poses.ContainsKey("ix-origami/model"),
            "with its faces and their orders, the folded state stands as a paper model on the table, turning, in place of a flat board");

        // A document without the orders (as the first ones were) draws the folded state flat on a board, as before.
        var unordered = System.Text.Json.Nodes.JsonNode.Parse(flipped)!.AsObject();
        unordered.Remove("orders");
        Write(unordered.ToJsonString());
        var flat = Look();
        check(flat.Model is null && flat.Folded is not null && !flat.Poses.ContainsKey("ix-origami/model") && Reach(flat.Folded) < Reach(flat.Pattern) * 0.6f,
            $"without orders, the folded state lies on a board at the pattern's scale, the quarter it folds to ({Reach(flat.Folded):F3} m against {Reach(flat.Pattern):F3} m)");

        // A valley fold in half brings the top half down over the bottom (IX's order puts face 1 over face 0, against
        // face 1's normal, which the fold turned over): from in front the flap shows the sheet's back, and from behind
        // so does the half that stayed; the red front is inside. The orders read the other way round would show red.
        Write(wrong);
        var valley = Sides(Look().Model);
        check(valley.Layers == 2 && Cream(valley.Front) && Cream(valley.Back),
            $"a valley fold in half: two layers, the sheet's back outside on both sides (front {valley.Front}, behind {valley.Back})");

        // The crane flat, as IX checked it (its document without Delta's pose).
        Write(Damaged(crane, d => d.AsObject().Remove("pose")));
        var flatBird = Look();
        var (_, _, layers) = Sides(flatBird.Model);
        var sheet = Corners(flatBird.Model).ToList();
        var height = sheet.Count == 0 ? 0 : sheet.Max(v => v.Position.Y) - sheet.Min(v => v.Position.Y);
        var width = sheet.Count == 0 ? 0 : sheet.Max(v => v.Position.X) - sheet.Min(v => v.Position.X);
        var thin = sheet.Count == 0 ? 1 : sheet.Max(v => v.Position.Z) - sheet.Min(v => v.Position.Z);
        check(flatBird.Tree.Valid && flatBird.Plaque.Contains("Origami · crane") && flatBird.Plaque.Contains("Valide selon IX") && flatBird.Plaque.Contains("28 couches au plus")
            && flatBird.Plaque.Contains("l'ordre de couches d'IX"), $"Rabbit Ear's crane, valid according to IX: {flatBird.Plaque}");
        check(layers >= 28 && sheet.Any(v => Cream(v.Color)) && sheet.Any(v => !Cream(v.Color)) && MathF.Max(width, height) is > 0.35f and < 0.37f && thin < 0.03f,
            $"the crane flat in paper: {sheet.Count / 3} triangles on {layers} layers (IX finds 28 in one place), both sides of the sheet, {width:F3} × {height:F3} m, {thin * 1000:F0} mm thick");

        // With Delta's pose, its wings open about its back, one to each side, and the table says the pose is illustrative.
        Write(crane);
        var bird = Look();
        var paper = Corners(bird.Model).ToList();
        var (front, behind) = paper.Count == 0 ? (0f, 0f) : (paper.Max(v => v.Position.Z), paper.Min(v => v.Position.Z));
        var lifted = paper.Where(v => MathF.Abs(v.Position.Z) > 0.1f).ToList();
        check(bird.Tree.Valid && bird.Plaque.Contains("ailes ouvertes par Delta, illustratif") && front > 0.15f && behind < -0.15f
            && lifted.Count > 0 && lifted.All(v => v.Position.Y > -0.05f) && paper.Count == sheet.Count,
            $"the crane posed: its wings out {front:F2} m in front and {-behind:F2} m behind, held up, the same {paper.Count / 3} triangles, said to be illustrative");
        var pinBox = Find(bird.Tree.Roots, "ix-origami")?.Boxes.FirstOrDefault(b => MathF.Abs(b.Size.X - 0.005f) < 1e-4f);
        var mount = bird.Model?.Local.Translation ?? default;
        var lowest = paper.Count == 0 ? 0 : mount.Y + paper.Min(v => v.Position.Y);
        check(pinBox is not null && MathF.Abs(pinBox.Centre.Y + (pinBox.Size.Y / 2) - mount.Y) < 1e-4f && MathF.Abs(lowest - 0.86f) < 0.002f,
            $"the crane sits on a pin through the paper's centre, which it turns about, its lowest point 3 cm over the foot (at {lowest:F3} m)");

        // A Miura-ori with its motion (tools/miura): one sheet that folds and unfolds, each face an instance that the
        // poses turn rigidly. Wherever it is in its cycle the faces meet at their shared corners: nothing is torn apart.
        var miura = Fixture("miura-4x3");
        Write(miura);
        var sheetLook = Look();
        var faceIds = Enumerable.Range(0, 12).Select(i => $"ix-origami/model/f{i}").ToArray();
        var faceInstances = faceIds.Select(p => Find(sheetLook.Tree.Roots, p)).ToArray();
        check(sheetLook.Tree.Valid && faceInstances.All(f => f is not null && f.Surfaces.Count == 1) && Find(sheetLook.Tree.Roots, "ix-origami/model/f12") is null
            && sheetLook.Plaque.Contains("Origami · miura-ori 4x3") && sheetLook.Plaque.Contains("Valide selon IX") && sheetLook.Plaque.Contains("12 couches au plus")
            && sheetLook.Plaque.Contains("le pliage : formule exacte, de Delta") && sheetLook.Plaque.Contains("une seule feuille, pliée"),
            $"the Miura-ori stands as one sheet of 12 faces, the plaque saying the motion is Delta's and the flat fold IX's: {sheetLook.Plaque}");
        List<Vector3>[] Posed(double seconds)
        {
            var poses = new Dictionary<string, Pose>();
            office.Pose(seconds, poses);
            return [.. faceIds.Select((p, i) => Corners(faceInstances[i]).Select(v => Vector3.Transform(v.Position, poses.TryGetValue(p, out var pose) ? pose.Transform : Matrix4x4.Identity)).ToList())];
        }
        // How many of the faces' corners meet no other face's corner within 1 mm, the widest gap at those that do (the
        // paper's two sides, 0.1 mm apart, included), and the sheet's extent and depth.
        (int Alone, float Gap, float Extent, float Depth) Shape(List<Vector3>[] faces)
        {
            var (alone, gap) = (0, 0f);
            for (var f = 0; f < faces.Length; f++)
            {
                // A face's corners once each: its front and back, 0.2 mm apart, are one corner.
                var corners = new List<Vector3>();
                foreach (var c in faces[f]) if (!corners.Any(d => Vector3.Distance(c, d) < 0.001f)) corners.Add(c);
                foreach (var c in corners)
                {
                    var nearest = faces.Where((_, g) => g != f).SelectMany(g => g).Min(d => Vector3.Distance(c, d));
                    if (nearest < 0.001f) gap = MathF.Max(gap, nearest);
                    else alone++;
                }
            }

            var all = faces.SelectMany(f => f).ToList();
            return (alone, gap, MathF.Max(all.Max(v => v.X) - all.Min(v => v.X), all.Max(v => v.Y) - all.Min(v => v.Y)), all.Max(v => v.Z) - all.Min(v => v.Z));
        }
        var cycle = 24 * 100.0;
        var (flatAlone, flatGap, flatExtent, flatDepth) = Shape(Posed(cycle + 1));
        var (midAlone, midGap, midExtent, midDepth) = Shape(Posed(cycle + 6.3));
        var (foldAlone, foldGap, foldExtent, foldDepth) = Shape(Posed(cycle + 13));
        check(flatAlone == 4 && flatGap < 0.0002f && MathF.Abs(flatExtent - 0.36f) < 0.002f && flatDepth < 0.0005f,
            $"before it folds, the sheet lies flat, 0.36 m, its faces meeting at every corner but the sheet's own 4 ({flatAlone} alone, {flatGap * 1000:F2} mm apart at most, {flatDepth * 1000:F2} mm deep)");
        check(midAlone == 4 && midGap < 0.0004f && midDepth > 0.02f && midExtent < flatExtent,
            $"half folded, between two of the motion's frames, the faces still meet ({midAlone} corners alone, {midGap * 1000:F2} mm apart at most), {midDepth * 100:F1} cm deep, {midExtent:F3} m wide");
        check(foldAlone <= 4 && foldGap < 0.0004f && foldExtent < flatExtent * 0.8f,
            $"folded, the faces still meet ({foldAlone} corners alone, {foldGap * 1000:F2} mm apart at most) and the sheet has drawn in to {foldExtent:F3} m, {foldDepth * 100:F1} cm deep");
        foreach (var (what, text) in new (string, string)[]
        {
            ("a frame that stretches a face", Damaged(miura, d => d["motion"]!["frames"]![2]![6]![2] = d["motion"]!["frames"]![2]![6]![2]!.GetValue<double>() + 0.05)),
            ("a frame missing a vertex", Damaged(miura, d => d["motion"]!["frames"]![3]!.AsArray().RemoveAt(19))),
            ("a motion of one frame", Damaged(miura, d => d["motion"]!["frames"] = new System.Text.Json.Nodes.JsonArray(d["motion"]!["frames"]![0]!.DeepClone()))),
        })
        {
            Write(half);
            Look();
            Write(text);
            var no = Look();
            check(no.Tree.Valid && no.Plaque.Contains("Pli refusé") && no.Pattern is null, $"{what}: refused ({no.Plaque})");
        }

        Write(flipped);
        office.Update(new("en", false, []));
        var english = Look();
        check(english.Plaque.Contains("Rejected by IX: adjacency") && english.Plaque.Contains("Layers: the stated order contradicts the crease at edge 7"), $"in English: {english.Plaque}");
        office.Update(new("fr", false, []));

        Write(half);
        var valid = Look();
        check(valid.Plaque.Contains("Valide selon IX") && valid.Plaque.Contains("4 couches au plus") && !Has(valid.Pattern, 1f, 0.62f, 0.2f)
            && Has(valid.Pattern, 0.9f, 0.22f, 0.18f) && Has(valid.Pattern, 0.2f, 0.45f, 0.95f), $"a fold IX finds valid: mountains red, valleys blue, nothing amber ({valid.Plaque})");

        Write(wrong);
        var expectation = Look();
        check(expectation.Plaque.Contains("Pli valide, attente non tenue") && expectation.Plaque.Contains("Attendu « ply 3 », obtenu 2 (ligne 4)"),
            $"an expectation the sequence does not hold: {expectation.Plaque}");

        Write("""{"format":"delta-ix-origami","v":1,"source":"ix_origami_check","at":"2026-11-14T07:00:00Z","error":"IX unavailable: no ix-mcp on the PATH"}""");
        var unavailable = Look();
        check(unavailable.Plaque.Contains("IX indisponible") && unavailable.Pattern is null, $"IX unavailable takes the fold away and says why ({unavailable.Plaque})");

        var bad = new (string What, string Text)[]
        {
            ("another version", flipped.Replace("\"v\":1", "\"v\":2")),
            ("an edge beyond the pattern", flipped.Replace("\"ids\":[7]", "\"ids\":[99]")),
            ("a vertex far away", flipped.Replace("\"vertices\":[[0.5,0]", "\"vertices\":[[0.5,40]")),
            ("a vertex in 3D", flipped.Replace("\"vertices\":[[0.5,0]", "\"vertices\":[[0.5,0,0]")),
            ("an unknown assignment", flipped.Replace("\"assignment\":[\"B\"", "\"assignment\":[\"X\"")),
            ("an edge without its assignment", flipped.Replace("\"assignment\":[\"B\",", "\"assignment\":[")),
            ("a folded state of other vertices", flipped.Replace("\"folded\":[[0.5,0],", "\"folded\":[")),
            ("no verdict", flipped.Replace("\"ok\":false,", "")),
            ("a finding placed nowhere known", flipped.Replace("\"where\":\"edge\"", "\"where\":\"face\"")),
            ("more than 1 MB", flipped + new string(' ', 1 << 20)),
            ("not JSON", "IX is down"),
            ("an order of a face beyond", Damaged(flipped, d => d["orders"]![0] = new System.Text.Json.Nodes.JsonArray(0, 99, 1))),
            ("an order that is no side", Damaged(flipped, d => d["orders"]![0] = new System.Text.Json.Nodes.JsonArray(0, 1, 2))),
            ("an order of a face with itself", Damaged(flipped, d => d["orders"]![0] = new System.Text.Json.Nodes.JsonArray(1, 1, 1))),
            ("orders without faces", Damaged(flipped, d => d["pattern"]!.AsObject().Remove("faces"))),
            ("a face of two vertices", Damaged(flipped, d => d["pattern"]!["faces"]![0] = new System.Text.Json.Nodes.JsonArray(0, 1))),
            ("a face naming a vertex beyond", Damaged(flipped, d => d["pattern"]!["faces"]![0] = new System.Text.Json.Nodes.JsonArray(0, 1, 99))),
            ("a pose turned beyond 180°", Damaged(crane, d => d["pose"]!["parts"]![0]!["to"] = 200)),
            ("a pose naming a face beyond", Damaged(crane, d => d["pose"]!["parts"]![0]!["faces"]![0] = 59)),
            ("a pose naming a face twice", Damaged(crane, d => d["pose"]!["parts"]![1]!["faces"]![0] = 0)),
            ("a pose about one point", Damaged(crane, d => d["pose"]!["ridge"] = new System.Text.Json.Nodes.JsonArray(9, 12))),
            ("a pose without the orders", Damaged(crane, d => d.AsObject().Remove("orders"))),
        };
        static string Damaged(string text, Action<System.Text.Json.Nodes.JsonNode> damage)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(text)!;
            damage(node);
            return node.ToJsonString();
        }
        foreach (var (what, text) in bad)
        {
            Write(half);
            Look();
            Write(text);
            var no = Look();
            check(no.Tree.Valid && no.Plaque.Contains("Pli refusé") && no.Pattern is null, $"{what}: refused, and the fold it replaced goes ({no.Plaque})");
        }

        // The budget: 48 stations, the knot and the table together still expand.
        var atelierBefore = Environment.GetEnvironmentVariable("DELTA_IX_ATELIER");
        Environment.SetEnvironmentVariable("DELTA_IX_ATELIER", Path.Combine(dir, "ix-atelier.json"));
        File.WriteAllText(Path.Combine(dir, "ix-atelier.json"), File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "ix-atelier-figure-eight.json")));
        try
        {
            using var full = new WidgetModuleSession<IOfficeModule>(image, "AgentOffice.Module", "Delta.AgentOffice.OfficeModule", "agent-office");
            Write(crane);
            full.Module.Update(new("fr", false, [.. Enumerable.Range(0, 48).Select(i => new OfficeSession($"0000{i:x4}-aaaa-bbbb-cccc-{i:x12}", $"session {i}", OfficeActivity.Working, 1, $"w{i % 6}", $"ws {i % 6}", i))]));
            full.Module.Pose(500, new Dictionary<string, Pose>());
            var tree = Widgets.Expand(full.Module.Scene());
            check(tree.Valid && Find(tree.Roots, "ix-atelier/knot") is not null && Find(tree.Roots, "ix-origami/model") is not null,
                $"48 stations, the rope and the crane expand within the scene's budget ({tree.Instances} instances, {tree.Boxes} boxes, {tree.Triangles} triangles){string.Concat(tree.Problems.Take(2).Select(p => "; " + p))}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DELTA_IX_ATELIER", atelierBefore);
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable("DELTA_IX_ORIGAMI", before);
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
