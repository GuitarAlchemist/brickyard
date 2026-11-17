using Delta.SceneModules;
using SN = System.Numerics;
using static Delta.SceneModules.Widgets;

namespace Delta.AgentOffice;

// The agents' office: a station per Claude Code session on the robot's left, each a desk with a small Delta at it, in
// a round tower. The stations stand in rings, facing out, each wmux workspace's after a sign with its name (the sessions
// outside wmux last); a ring full, the next goes on a step above and further in, so the tower grows a storey as the
// sessions grow, everything smaller only when that is not enough. The rings turn slowly, one turn in Turn seconds;
// a ring with the selected robot on it holds still, so that a robot clicked stays under the pointer.
// Everything is widgets nested in widgets: a station holds a desk, a name plate, a robot fixed to the desk's seat and
// the selection's mark above it; the robot a torso, with the head and the arms fixed to its anchors; the torso Mac
// minis, each with its status light. The robot types while its session works, lights a red beacon while the session
// waits for an answer and jumps, arms up, when the session has finished; a stopped session's robot bows its head, dark,
// a stale one glows amber and an unknown one grey: poses on the instances' paths, no mesh rebuilt, as is the mark over
// the selected robot. Paths come from the sessions' and workspaces' IDs and places from the sessions' starts, so a
// refresh in any order keeps both. Two sessions with one name show their IDs' first letters. In flat quality it is a
// plain board instead, a lamp and a name per session.
//
// This assembly is loaded into a collectible context and replaced while the scene runs, so it keeps no static state.
public sealed class OfficeModule : IOfficeModule
{
    // The tower: its axis on the floor, between the bricks and Delta, and the radius of its lowest step, 0.15 m off Delta's
    // tracks and 0.15 m off the bricks. At full size a slot (a station or a sign) takes Pitch of a ring's circumference at
    // its desks' centres; a step rises Rise and goes in Step, the depth of a station and its stool; the innermost step
    // keeps Core of radius. The office's view (key 2) looks at it from the front.
    internal const float Cx = -1.35f, Cz = -0.15f, Outer = 0.78f;
    internal const float Pitch = 0.36f, Step = 0.38f, Rise = 0.42f, Inset = 0.125f, Core = 0.06f, Smallest = 0.12f;
    internal const double Turn = 150; // seconds a ring takes to go round
    private const float Spread = 1.5f; // pitches between the slots of a ring with room to spare
    private const float Bearing = 0.207f; // the office's view's eye seen from the axis, in radians from +Z toward +X
    private const double DoneHop = 2.4; // seconds of jumping after a session finishes
    private const int MaxDrawn = 48; // stations drawn at most: about 37 instances each with its enclosure, within the scene's 2048

    private OfficeState state = new("en", false, []);
    private bool initialized;
    private int layoutRevision;
    private readonly IxAtelier atelier = new(); // the IX workshop's stand in the hall, which this module also draws (IxAtelier.cs)
    private readonly IxOrigami origami = new(); // and its origami table (IxOrigami.cs)

    public string Id => "agent-office";

    public int Revision => HashCode.Combine(layoutRevision, atelier.Revision, origami.Revision);

    public void Initialize()
    {
        if (initialized) throw new InvalidOperationException("The office is already initialized.");
        initialized = true;
        atelier.Poll(0);
        origami.Poll(0);
    }

    public void Update(OfficeState next)
    {
        Ready();
        var layout = new HashCode();
        layout.Add(next.Language);
        layout.Add(next.Flat);
        foreach (var s in next.Sessions) { layout.Add(s.Id); layout.Add(s.Name); layout.Add(s.WorkspaceId); layout.Add(s.WorkspaceTitle); layout.Add(s.Started); }
        if (next.Sessions.Count > MaxDrawn) layout.Add(next.Selected); // beyond MaxDrawn, the selection chooses who is drawn
        state = next;
        layoutRevision = layout.ToHashCode();
    }

    public WidgetScene Scene()
    {
        var office = OfficeScene();
        return new([.. office.Library, .. atelier.Library(state.Language), .. origami.Library(state.Language)],
            [.. office.Roots, atelier.Root(state.Language), origami.Root(state.Language)]);
    }

    private WidgetScene OfficeScene()
    {
        Ready();
        var roots = new List<UsePiece>();
        var sessions = Ordered(Kept(state));
        var shown = Shown(sessions);
        var hidden = state.Sessions.Count - sessions.Count;
        var more = Say($"+{hidden} not shown", $"+{hidden} non montrées");
        if (state.Flat)
        {
            var lines = Math.Max(1, sessions.Count) + (hidden > 0 ? 1 : 0);
            var top = 0.48f + lines * 0.085f;
            roots.Add(new("board", Board, Place(-1.05f, 0f, -0.3f), new Dictionary<string, WidgetValue>
            {
                ["rows"] = lines,
                ["title"] = Say("Agents' office", "Bureau des agents"),
            }));
            if (sessions.Count == 0) roots.Add(new("none", Row, Place(-1.05f, top - 0.135f, -0.288f), Name(Say("No session", "Aucune session"))));
            for (var i = 0; i < sessions.Count; i++)
                roots.Add(new(Key(sessions[i]), Row, Place(-1.05f, top - 0.135f - 0.085f * i, -0.288f), Name(shown[sessions[i].Id]), sessions[i].Id));
            if (hidden > 0) roots.Add(new("more", Row, Place(-1.05f, top - 0.135f - 0.085f * sessions.Count, -0.288f), Name(more)));
            return new(Library, roots);
        }

        if (hidden > 0)
        {
            roots.Add(new("more", Plate, Place(Cx, 0.02f, Cz + Outer + 0.12f), Name(more))); // on the floor before the tower
        }

        if (sessions.Count == 0)
        {
            roots.Add(new("none", Station, Place(Cx, 0f, Cz + Outer - Inset), Name(Say("No session", "Aucune session"))));
            return new(Library, roots);
        }

        var tower = Tower();
        for (var k = 0; k < tower.Rings; k++)
        {
            var (radius, height) = (Edge(k, tower.Scale), k * Rise * tower.Scale);
            roots.Add(new($"tier-{k}", Tier, Place(Cx, 0f, Cz), new Dictionary<string, WidgetValue> { ["r"] = radius, ["h"] = Math.Max(0.02f, height) }));
        }

        // A tower of storeys has its spire on its axis, a robot's height above its highest step.
        if (tower.Rings > 1)
        {
            roots.Add(new("spire", Tier, Place(Cx, 0f, Cz), new Dictionary<string, WidgetValue>
            {
                ["r"] = 0.035f, ["h"] = ((tower.Rings - 1) * Rise * tower.Scale) + (0.95f * tower.Scale),
            }));
        }

        foreach (var slot in tower.Slots)
        {
            var at = SlotPlace(slot, tower.Scale);
            roots.Add(slot.Session is { } s
                ? new(Key(s), Station, at, Name(shown[s.Id]), s.Id)
                : new(slot.Path, Sign, at, new Dictionary<string, WidgetValue> { ["title"] = slot.Title!.Length > 40 ? slot.Title[..39] + "…" : slot.Title }));
        }

        return new(Library, roots);
    }

    // A slot of a ring: a session's station, or a workspace's sign (Title) before its stations; at Angle about the axis,
    // from +Z toward +X, facing out.
    private sealed record Slot(string Path, int Ring, float Angle, OfficeSession? Session, string? Title);

    private sealed record TowerLayout(float Scale, int Rings, IReadOnlyList<Slot> Slots);

    private TowerLayout? tower;
    private int towerRevision;
    private readonly Dictionary<int, double> spins = [];
    private double spun = double.NaN;

    // The outer edge of step k at scale s, and the radius of its desks' centres.
    internal static float Edge(int k, float s) => Outer - (k * Step * s);

    private static float Desks(int k, float s) => Edge(k, s) - (Inset * s);

    // How many slots ring k holds at scale s; 0 once its stools would reach the core.
    internal static int Capacity(int k, float s) =>
        Edge(k, s) - (Step * s) < Core ? 0 : (int)MathF.Floor(2 * MathF.PI * Desks(k, s) / (Pitch * s));

    private static SN.Matrix4x4 SlotPlace(Slot slot, float s)
    {
        var r = Desks(slot.Ring, s);
        return Place(Cx + (r * MathF.Sin(slot.Angle)), Math.Max(0.02f, slot.Ring * Rise * s), Cz + (r * MathF.Cos(slot.Angle)), slot.Angle * 180f / MathF.PI, s);
    }

    // The tower for the sessions drawn: the workspaces in the order their first sessions started, the sessions outside
    // wmux last, each a sign then its stations, filling the rings from the lowest; a workspace going on to the next ring
    // starts it with its sign again. The largest scale, to Smallest, at which they all fit; the slots of a ring in one
    // arc centred on the office's view, round the whole ring once it is full enough.
    private TowerLayout Tower()
    {
        if (tower is not null && towerRevision == layoutRevision) return tower;
        var sessions = Ordered(Kept(state));
        var groups = sessions.GroupBy(s => s.WorkspaceId).OrderBy(g => g.Key.Length == 0 ? 1 : 0).ThenBy(g => g.Min(s => s.Started)).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Key: g.Key, Title: g.Key.Length == 0 ? Say("No wmux pane", "Sans volet wmux") : g.First().WorkspaceTitle is { Length: > 0 } t ? t : g.Key, Members: g.ToList()))
            .ToList();
        List<List<(OfficeSession? Session, string Key, string? Title)>>? rings = null;
        var scale = 1f;
        for (; scale > Smallest; scale -= 0.01f)
        {
            if ((rings = Pack(groups, scale)) is not null) break;
        }

        rings ??= Pack(groups, Smallest, force: true)!;
        var slots = new List<Slot>();
        for (var k = 0; k < rings.Count; k++)
        {
            var ring = rings[k];
            for (var j = 0; j < ring.Count; j++)
            {
                var (session, key, title) = ring[j];
                // Evenly round the ring, or, for a ring with room to spare, Spread slots' width apart in one arc facing the view.
                var step = MathF.Min(2 * MathF.PI / ring.Count, Spread * Pitch * scale / Desks(k, scale));
                var angle = Bearing + ((j - ((ring.Count - 1) / 2f)) * step);
                var sign = slots.Any(x => x.Path == PenKey(key)) ? $"{PenKey(key)}-{k}" : PenKey(key); // a workspace going on to another ring
                slots.Add(session is null ? new(sign, k, angle, null, title) : new(Key(session), k, angle, session, null));
            }
        }

        (tower, towerRevision) = (new(scale, rings.Count, slots), layoutRevision);
        return tower;
    }

    // The groups' slots ring by ring at scale s, or null when they do not all fit; force puts what is left on the last.
    private static List<List<(OfficeSession? Session, string Key, string? Title)>>? Pack(
        List<(string Key, string Title, List<OfficeSession> Members)> groups, float s, bool force = false)
    {
        var rings = new List<List<(OfficeSession?, string, string?)>> { new() };
        int Room() => Capacity(rings.Count - 1, s) - rings[^1].Count;
        foreach (var (key, title, members) in groups)
        {
            var next = 0;
            while (next < members.Count)
            {
                if (Room() < 2) // a sign and at least one station
                {
                    if (Capacity(rings.Count, s) >= 2) rings.Add([]);
                    else if (!force) return null;
                }

                rings[^1].Add((null, key, title));
                var last = Capacity(rings.Count, s) < 2;
                while (next < members.Count && (Room() > 0 || (force && last))) rings[^1].Add((members[next++], key, null));
            }
        }

        return rings;
    }

    public void Pose(double seconds, IDictionary<string, Pose> poses)
    {
        Ready();
        atelier.Poll(seconds);
        origami.Poll(seconds);
        atelier.Pose(seconds, poses);
        origami.Pose(seconds, poses);
        if (!state.Flat && state.Sessions.Count > 0) Spin(seconds, poses);
        var t = (float)seconds;
        foreach (var s in Kept(state))
        {
            var age = (float)Math.Max(0, seconds - s.Since);
            var glow = Glow(s.Activity);
            var blink = (int)(t * 2.5f) % 2 == 0;
            // The selection's mark, a small light turning slowly above the selected robot alone.
            poses[Key(s) + "/mark"] = s.Id == state.Selected
                ? new(SN.Matrix4x4.CreateRotationY(t * 1.6f) * SN.Matrix4x4.CreateTranslation(0f, 0.012f * MathF.Sin(t * 2.4f), 0f), new(1f, 0.85f, 0.35f, 1.4f))
                : SceneModules.Pose.Off;
            if (state.Flat)
            {
                var lamp = s.Activity switch
                {
                    OfficeActivity.Waiting => blink ? glow : glow * 0.25f,
                    OfficeActivity.Done when age < DoneHop => glow * (0.5f + 0.5f * MathF.Abs(MathF.Sin(MathF.PI * age / 0.4f))),
                    _ => glow,
                };
                poses[Key(s) + "/lamp"] = SceneModules.Pose.Lit(lamp);
                continue;
            }

            var p = Key(s) + "/desk";
            var robot = p + "/robot";
            var torso = robot + "/torso";
            var head = torso + "/head";
            poses[p + "/screen"] = SceneModules.Pose.Lit(glow * 0.8f);
            foreach (var eye in new[] { "/eye-l", "/eye-r", "/tip" }) poses[head + eye] = SceneModules.Pose.Lit(glow);
            if (s.Activity == OfficeActivity.Waiting) poses[head + "/beacon/light"] = SceneModules.Pose.Lit(blink ? new(1f, 0.12f, 0.08f, 1.6f) : default);
            else poses[head + "/beacon"] = SceneModules.Pose.Off;

            // The status lights of the torso's Mac minis: lit low, a light running up them while it works.
            for (var k = 0; k < 4; k++)
            {
                var lit = s.Activity == OfficeActivity.Working && (int)(t * 8) % 4 == k ? 1f : 0.3f;
                foreach (var side in new[] { "l", "r" }) poses[$"{torso}/m{k}{side}/led"] = SceneModules.Pose.Lit(glow * lit);
            }

            if (s.Activity == OfficeActivity.Stopped)
            {
                poses[head] = new(SN.Matrix4x4.CreateRotationX(0.45f)); // its head bowed, its lights low
            }
            else if (s.Activity == OfficeActivity.Done && age < DoneHop)
            {
                poses[robot] = SceneModules.Pose.Moved(new(0f, 0.07f * MathF.Abs(MathF.Sin(MathF.PI * age / 0.4f)), 0f));
                foreach (var arm in new[] { "/arm-l", "/arm-r" }) poses[torso + arm] = new(SN.Matrix4x4.CreateRotationX(-2.6f));
            }
            else if (s.Activity == OfficeActivity.Working)
            {
                poses[torso + "/arm-l/hand"] = SceneModules.Pose.Moved(new(0f, 0.012f * MathF.Max(0f, MathF.Sin(t * 16f)), 0f));
                poses[torso + "/arm-r/hand"] = SceneModules.Pose.Moved(new(0f, 0.012f * MathF.Max(0f, MathF.Sin(t * 16f + MathF.PI)), 0f));
            }
        }
    }

    // Each ring turned about the tower's axis by its own angle, advanced with the time unless the selected robot is on it:
    // a slot's pose is its placement's turn about the axis, written in its own frame.
    private void Spin(double seconds, IDictionary<string, Pose> poses)
    {
        var layout = Tower();
        var dt = double.IsNaN(spun) ? 0 : Math.Clamp(seconds - spun, 0, 0.25);
        spun = seconds;
        var held = layout.Slots.Where(x => x.Session is { } s && s.Id == state.Selected).Select(x => x.Ring).ToHashSet();
        for (var k = 0; k < layout.Rings; k++)
        {
            var a = spins.GetValueOrDefault(k);
            spins[k] = held.Contains(k) ? a : (a + (dt * 2 * Math.PI / Turn)) % (2 * Math.PI);
        }

        var axis = new SN.Vector3(Cx, 0f, Cz);
        foreach (var slot in layout.Slots)
        {
            var a = (float)spins[slot.Ring];
            var place = SlotPlace(slot, layout.Scale);
            if (a == 0 || !SN.Matrix4x4.Invert(place, out var inverse)) continue;
            var about = SN.Matrix4x4.CreateTranslation(-axis) * SN.Matrix4x4.CreateRotationY(a) * SN.Matrix4x4.CreateTranslation(axis);
            poses[slot.Path] = new(place * about * inverse);
        }
    }

    public void Dispose()
    {
        initialized = false;
        state = new("en", false, []);
        tower = null;
        spins.Clear();
    }

    private void Ready()
    {
        if (!initialized) throw new InvalidOperationException("The office is not initialized.");
    }

    private string Say(string en, string fr) => state.Language == "fr" ? fr : en;

    // A path name from the session's ID, the same whatever order the sessions come in.
    private static string Key(OfficeSession s) => "s-" + new string(s.Id.Where(char.IsAsciiLetterOrDigit).Take(12).ToArray());

    // An enclosure's path name from its workspace's ID, which wmux keeps when the workspace is renamed.
    private static string PenKey(string workspace) => workspace.Length == 0 ? "w-outside" : "w-" + new string(workspace.Where(char.IsAsciiLetterOrDigit).Take(12).ToArray());

    // The sessions drawn: all up to MaxDrawn; beyond, the selected one and the latest started, the rest only counted.
    private static List<OfficeSession> Kept(OfficeState s) => s.Sessions.Count <= MaxDrawn ? [.. s.Sessions]
        : [.. s.Sessions.OrderByDescending(x => x.Id == s.Selected).ThenByDescending(x => x.Started).ThenBy(x => x.Id, StringComparer.Ordinal).Take(MaxDrawn)];

    // The sessions by start, then ID: the order desks keep, whatever order the host sends.
    private static List<OfficeSession> Ordered(IReadOnlyList<OfficeSession> sessions) =>
        [.. sessions.OrderBy(s => s.Started).ThenBy(s => s.Id, StringComparer.Ordinal)];

    // Each session's shown name: two with one name add their IDs' first four letters, kept within a plate's 18.
    private static Dictionary<string, string> Shown(IReadOnlyList<OfficeSession> sessions)
    {
        var shared = sessions.GroupBy(s => s.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return sessions.ToDictionary(s => s.Id, s =>
        {
            if (!shared.Contains(s.Name)) return s.Name;
            var tag = " ·" + new string(s.Id.Where(char.IsAsciiLetterOrDigit).Take(4).ToArray());
            var name = s.Name.Length > 18 - tag.Length ? s.Name[..(17 - tag.Length)] + "…" : s.Name;
            return name + tag;
        });
    }

    private static Dictionary<string, WidgetValue> Name(string name) =>
        new() { ["name"] = name.Length > 40 ? name[..39] + "…" : name.Length == 0 ? "?" : name };

    private static SN.Vector4 Glow(OfficeActivity activity) => activity switch
    {
        OfficeActivity.Working => new(0.35f, 0.8f, 1f, 1f),
        OfficeActivity.Waiting => new(1f, 0.12f, 0.08f, 1f),
        OfficeActivity.Done => new(0.35f, 1f, 0.5f, 1f),
        OfficeActivity.Stopped => new(0.1f, 0.1f, 0.11f, 0.3f),
        OfficeActivity.Stale => new(1f, 0.55f, 0.1f, 0.6f),
        OfficeActivity.Unknown => new(0.45f, 0.48f, 0.55f, 0.45f),
        _ => new(0.12f, 0.22f, 0.28f, 0.5f),
    };

    // The library. Sizes in metres; materials: 0 matte, 1 enamel, 2 aluminium, 3 steel, 4 rubber, 5 glass.
    private static readonly WidgetId Block = new("block", 1), MacMini = new("mac-mini", 1), Torso = new("torso", 1), Head = new("head", 1),
        Arm = new("arm", 1), Robot = new("robot", 1), Desk = new("desk", 1), Plate = new("plate", 1), Station = new("station", 1),
        Board = new("board", 1), Row = new("row", 1), Mark = new("mark", 1), Tier = new("tier", 1), Sign = new("sign", 1);

    private static readonly SN.Vector3 Steel = new(0.16f, 0.165f, 0.17f), Dark = new(0.04f, 0.04f, 0.045f), Aluminium = new(0.6f, 0.61f, 0.64f),
        Shell = new(0.7f, 0.68f, 0.62f), Orange = new(0.78f, 0.27f, 0.05f), Visor = new(0.012f, 0.014f, 0.018f), Wood = new(0.36f, 0.24f, 0.14f),
        Pale = new(0.84f, 0.86f, 0.9f), Unlit = new(0.03f, 0.03f, 0.035f);

    private static Dictionary<string, WidgetValue> Size(float w, float h, float d) => new() { ["w"] = w, ["h"] = h, ["d"] = d };

    // A drum about the vertical through its origin, standing on y = 0: its side in two bands (concrete, then steel for
    // its last Band), its top a disc; each triangle wound counter-clockwise seen from outside.
    private static List<ModuleTriangle> Drum(float r, float h)
    {
        const int Sides = 72;
        var band = Math.Min(0.015f, h / 3);
        SN.Vector3 concrete = new(0.15f, 0.15f, 0.155f), top = new(0.24f, 0.235f, 0.23f);
        var triangles = new List<ModuleTriangle>(Sides * 5);
        SN.Vector3 At(int i, float y) => new(r * MathF.Sin(2 * MathF.PI * i / Sides), y, r * MathF.Cos(2 * MathF.PI * i / Sides));
        SN.Vector3 Out(int i) => new(MathF.Sin(2 * MathF.PI * i / Sides), 0f, MathF.Cos(2 * MathF.PI * i / Sides));
        void Quad(int i, float y0, float y1, SN.Vector3 color)
        {
            ModuleVertex V(int j, float y) => new(At(j, y), Out(j), color);
            triangles.Add(new(V(i, y0), V(i + 1, y0), V(i + 1, y1)));
            triangles.Add(new(V(i, y0), V(i + 1, y1), V(i, y1)));
        }

        for (var i = 0; i < Sides; i++)
        {
            Quad(i, 0f, h - band, concrete);
            Quad(i, h - band, h, Steel);
            ModuleVertex T(SN.Vector3 p) => new(p, SN.Vector3.UnitY, top);
            triangles.Add(new(T(new(0f, h, 0f)), T(At(i, h)), T(At(i + 1, h))));
        }

        return triangles;
    }
    private static UsePiece Lamp(string name, SN.Vector3 at, float w, float h, float d) => new(name, Block, SN.Matrix4x4.CreateTranslation(at), Size(w, h, d));
    private static BoxPiece B(float x, float y, float z, float w, float h, float d, SN.Vector3 color, uint material) => new(new(x, y, z), new(w, h, d), color, material);
    private static WidgetParam Metres(string name, double fallback) => new(name, fallback, 0.001, 2);
    private static WidgetParam Text(string name) => new(name, "?", 1, 40);

    private static readonly IReadOnlyList<Widget> Library =
    [
        // An unlit glass block, centred, lit by its pose: an eye, a status light, a screen, a beacon's light; or a hand.
        new(Block, [Metres("w", 0.02), Metres("h", 0.02), Metres("d", 0.02)],
            a => new([new BoxPiece(SN.Vector3.Zero, new((float)a.Number("w"), (float)a.Number("h"), (float)a.Number("d")), Unlit, 5)])),

        // A Mac mini standing on its bottom centre: a slab on a dark foot, a port slot, and its status light.
        new(MacMini, [],
            _ => new([B(0, 0.002f, 0, 0.06f, 0.004f, 0.09f, Dark, 4), B(0, 0.0245f, 0, 0.075f, 0.041f, 0.11f, Aluminium, 2),
                B(-0.025f, 0.0245f, 0.0555f, 0.012f, 0.004f, 0.002f, Visor, 5), Lamp("led", new(0.012f, 0.0245f, 0.0565f), 0.03f, 0.006f, 0.003f)])),

        // Four layers of two Mac minis; the neck and the shoulders are anchors.
        new(Torso, [],
            _ => new([.. Enumerable.Range(0, 4).SelectMany(k => new Piece[]
                {
                    new UsePiece($"m{k}l", MacMini, Place(-0.04f, 0.1875f + 0.05f * k, 0f)),
                    new UsePiece($"m{k}r", MacMini, Place(0.04f, 0.1875f + 0.05f * k, 0f)),
                }), B(0, 0.42f, 0, 0.035f, 0.03f, 0.035f, Steel, 3)],
                new Dictionary<string, SN.Matrix4x4>
                {
                    ["neck"] = SN.Matrix4x4.CreateTranslation(0f, 0.405f, 0f),
                    ["shoulder-l"] = SN.Matrix4x4.CreateTranslation(-0.095f, 0.385f, 0f),
                    ["shoulder-r"] = SN.Matrix4x4.CreateTranslation(0.095f, 0.385f, 0f),
                })),

        // A pale shell under an enamel cap, a dark visor with two eyes, ears, an antenna and the beacon on top.
        new(Head, [],
            _ => new([
                B(0, 0.085f, 0, 0.17f, 0.115f, 0.125f, Shell, 1), B(0, 0.147f, -0.006f, 0.13f, 0.008f, 0.09f, Orange, 1),
                B(0, 0.082f, 0.064f, 0.145f, 0.08f, 0.004f, Visor, 5), B(-0.09f, 0.085f, 0, 0.01f, 0.05f, 0.055f, Steel, 3),
                B(0.09f, 0.085f, 0, 0.01f, 0.05f, 0.055f, Steel, 3), B(0.045f, 0.18f, -0.03f, 0.005f, 0.06f, 0.005f, Steel, 3),
                Lamp("eye-l", new(-0.035f, 0.085f, 0.0675f), 0.032f, 0.022f, 0.003f), Lamp("eye-r", new(0.035f, 0.085f, 0.0675f), 0.032f, 0.022f, 0.003f),
                Lamp("tip", new(0.045f, 0.213f, -0.03f), 0.016f, 0.016f, 0.016f),
                new UsePiece("beacon", new("beacon", 1), Place(-0.03f, 0.151f, 0f)),
            ])),
        new(new("beacon", 1), [],
            _ => new([B(0, 0.02f, 0, 0.008f, 0.04f, 0.008f, Steel, 3), Lamp("light", new(0f, 0.057f, 0f), 0.04f, 0.035f, 0.04f)])),

        // An arm about its shoulder, hanging, its forearm toward the keyboard; side -1 is the left.
        new(Arm, [new("side", 1.0, -1, 1)],
            a =>
            {
                var side = (float)a.Number("side");
                return new([B(0, 0, 0, 0.03f, 0.035f, 0.05f, Steel, 3), B(0, -0.04f, 0, 0.03f, 0.05f, 0.035f, Aluminium, 2),
                    B(-side * 0.025f, -0.05f, 0.06f, 0.028f, 0.024f, 0.085f, Aluminium, 2),
                    new UsePiece("hand", Block, Place(-side * 0.06f, -0.053f, 0.118f), Size(0.03f, 0.016f, 0.03f))]);
            }),

        // A small Delta: the torso, with the head and the arms fixed to its anchors, so that they follow it.
        new(Robot, [],
            _ => new([
                new UsePiece("torso", Torso, SN.Matrix4x4.Identity),
                new AttachPiece("torso", "neck", new("head", Head, Place(0f, 0.03f, 0f))),
                new AttachPiece("torso", "shoulder-l", new("arm-l", Arm, SN.Matrix4x4.Identity, new Dictionary<string, WidgetValue> { ["side"] = -1.0 })),
                new AttachPiece("torso", "shoulder-r", new("arm-r", Arm, SN.Matrix4x4.Identity, new Dictionary<string, WidgetValue> { ["side"] = 1.0 })),
            ])),

        // A desk with a keyboard, a monitor whose screen faces right, toward the camera, and a stool; the seat is an anchor.
        new(Desk, [],
            _ => new([
                B(0, 0.3f, 0, 0.3f, 0.025f, 0.18f, Wood, 0), B(-0.135f, 0.145f, 0, 0.02f, 0.29f, 0.16f, Steel, 3), B(0.135f, 0.145f, 0, 0.02f, 0.29f, 0.16f, Steel, 3),
                B(0, 0.2f, 0.086f, 0.28f, 0.17f, 0.008f, Dark, 3), B(0.01f, 0.319f, -0.02f, 0.12f, 0.012f, 0.045f, Dark, 4),
                B(-0.11f, 0.385f, -0.01f, 0.015f, 0.11f, 0.15f, Aluminium, 2), B(-0.11f, 0.322f, -0.01f, 0.05f, 0.01f, 0.06f, Aluminium, 2),
                Lamp("screen", new(-0.1015f, 0.39f, -0.01f), 0.002f, 0.09f, 0.13f),
                B(0, 0.08f, -0.14f, 0.03f, 0.16f, 0.03f, Steel, 3), B(0, 0.17f, -0.14f, 0.12f, 0.025f, 0.11f, Dark, 4),
            ], new Dictionary<string, SN.Matrix4x4> { ["seat"] = SN.Matrix4x4.CreateTranslation(0f, 0f, -0.14f) })),

        // A name on the desk's front panel.
        new(Plate, [Text("name")],
            a =>
            {
                var name = a.Text("name");
                var shown = name.Length > 18 ? name[..17] + "…" : name;
                return new([new TextPiece(shown, SN.Vector3.Zero, Math.Min(0.26f, 0.0165f * shown.Length), Pale)]);
            }),

        // A session's station: the desk, its name, a robot on the desk's seat, and the selection's mark above its head.
        new(Station, [Text("name")],
            a => new([
                new UsePiece("desk", Desk, SN.Matrix4x4.Identity),
                new UsePiece("plate", Plate, Place(0f, 0.2f, 0.0905f), new Dictionary<string, WidgetValue> { ["name"] = a.Text("name") }),
                new AttachPiece("desk", "seat", new("robot", Robot, SN.Matrix4x4.Identity)),
                new UsePiece("mark", Mark, Place(0f, 0.76f, -0.14f)),
            ])),

        // The selection's mark: a small gem of two lit blocks turned on each other, hidden until posed.
        new(Mark, [],
            _ => new([
                new UsePiece("a", Block, SN.Matrix4x4.CreateRotationX(MathF.PI / 4) * SN.Matrix4x4.CreateRotationZ(MathF.PI / 4), Size(0.03f, 0.03f, 0.03f)),
                new UsePiece("b", Block, SN.Matrix4x4.CreateRotationY(MathF.PI / 4), Size(0.026f, 0.026f, 0.026f)),
            ])),

        // A step of the tower about its axis: a drum r metres in radius and h high, its top lighter than its side, a steel
        // band round its rim. The rings of stations stand on the steps.
        new(Tier, [new("r", 0.5, 0.02, 2), new("h", 0.1, 0.005, 3)],
            a => new([new SurfacePiece(Drum((float)a.Number("r"), (float)a.Number("h")))])),

        // A workspace's sign in its ring, facing out: a dark board on a post, the workspace's name on it, as wide as a desk.
        new(Sign, [Text("title")],
            a =>
            {
                var title = a.Text("title");
                var wide = Math.Min(0.27f, 0.017f * title.Length);
                return new([
                    B(0, 0.12f, 0, 0.025f, 0.24f, 0.025f, Steel, 3), B(0, 0.005f, 0, 0.14f, 0.01f, 0.1f, Steel, 3),
                    B(0, 0.3f, 0, 0.3f, 0.13f, 0.012f, Dark, 3), B(0, 0.369f, 0, 0.3f, 0.008f, 0.014f, Orange, 1),
                    new TextPiece(title, new(0f, 0.3f, 0.0075f), Math.Max(0.02f, wide), Pale),
                ]);
            }),

        // The flat view's board on its post, the rows in front of it.
        new(Board, [new("rows", 1.0, 1, 64), Text("title")],
            a =>
            {
                var height = 0.13f + (float)a.Number("rows") * 0.085f;
                return new([B(0, 0.35f + height / 2, 0, 0.9f, height, 0.02f, Dark, 3), B(0, 0.175f, 0, 0.04f, 0.35f, 0.04f, Steel, 3),
                    new TextPiece(a.Text("title"), new(0f, 0.35f + height - 0.05f, 0.012f), 0.36f, Pale)]);
            }),
        new(Row, [Text("name")],
            a =>
            {
                var name = a.Text("name");
                var shown = name.Length > 28 ? name[..27] + "…" : name;
                var w = Math.Min(0.7f, 0.022f * shown.Length);
                return new([Lamp("lamp", new(-0.39f, 0f, 0.003f), 0.05f, 0.05f, 0.01f), new TextPiece(shown, new(-0.34f + w / 2, 0f, 0f), w, Pale),
                    B(0, 0, -0.004f, 0.88f, 0.08f, 0.002f, Dark, 0), Lamp("mark", new(0.41f, 0f, 0.003f), 0.025f, 0.025f, 0.01f)]);
            }),
    ];
}
