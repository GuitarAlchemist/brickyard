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
    WidgetPicking.SelfTest(Check);
    Office(Check, args[0]);
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
    var last = new Dictionary<string, (OfficeActivity, double)>();
    OfficeActivities.Seen S(string id, string state, string status = "idle", long started = 0) => new(id, id, started, status, state, "surf-" + id);
    var first = OfficeActivities.Derive([S("b", "working", started: 2), S("a", "blocked", started: 1), S("c", "", "busy", 3)], last, 10);
    check(first.Select(s => s.Id).SequenceEqual(["a", "b", "c"]) && first[0].Activity == OfficeActivity.Waiting &&
        first[1].Activity == OfficeActivity.Working && first[2].Activity == OfficeActivity.Working,
        "blocked waits, working works, a session outside wmux works while busy; desks keep their start order");
    var later = OfficeActivities.Derive([S("a", "working", started: 1), S("b", "idle", started: 2), S("c", "", "busy", 3)], last, 12);
    check(later[1].Activity == OfficeActivity.Done && later[1].Since == 12 && later[2].Since == 10, "a session that stops working is done, from then; unchanged ones keep their start");
    var after = OfficeActivities.Derive([S("a", "idle", started: 1), S("b", "idle", started: 2)], last, 12 + OfficeActivities.DoneFor);
    check(after[1].Activity == OfficeActivity.Idle && after[0].Activity == OfficeActivity.Done && !last.ContainsKey("c"),
        "done lasts a few seconds then is idle; a session gone is forgotten");
    var answered = OfficeActivities.Derive([S("w", "blocked")], [], 0);
    var dismissed = OfficeActivities.Derive([S("w", "idle")], new() { ["w"] = (OfficeActivity.Waiting, 0) }, 1);
    check(answered[0].Activity == OfficeActivity.Waiting && dismissed[0].Activity == OfficeActivity.Idle, "a wait ended without work is not shown as done");
    var resumed = OfficeActivities.Derive([new("r", "ix-51", 1, "busy", "", ""), S("q", "idle", started: 2), new("r", "ix-ad", 3, "idle", "", "")], [], 0);
    check(resumed.Count == 2 && resumed.Select(s => s.Id).SequenceEqual(["q", "r"]) && resumed[1].Name == "ix-ad" && resumed[1].Activity == OfficeActivity.Idle,
        "two processes with one session ID make one desk, the one started last");
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
