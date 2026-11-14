using Delta.SceneModules;
using SN = System.Numerics;
using static Delta.SceneModules.Widgets;

namespace Delta.AgentOffice;

// The agents' office: a station per Claude Code session on the robot's left, each a desk with a small Delta at it, in
// an enclosure per wmux workspace (one more for the sessions outside wmux) that carries the workspace's name.
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
    private const float Pitch = 0.38f; // between stations at full size, in metres
    private const float Left = -1.45f, Right = -0.69f, Front = 0.3f;
    private const float RowDepth = 0.45f, Deep = 3.3f; // a row of stations; the depth the enclosures may take from the front, to 0.3 m off the bricks
    private const float PenFront = 0.17f, PenBack = 0.3f, PenSide = 0.2f; // an enclosure's margins round its stations, scaled with them
    private const float Aisle = 0.17f; // between two enclosures' rails, whatever their scale, while the aisles take half the depth at most
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
            roots.Add(new("more", Plate, Place(-1.07f, 0.02f, Front + 0.12f), Name(more))); // on the floor before the first enclosure
        }

        if (sessions.Count == 0)
        {
            roots.Add(new("none", Station, Place(-1.07f, 0f, Front), Name(Say("No session", "Aucune session"))));
            return new(Library, roots);
        }

        // An enclosure per workspace, going back from the robot's front in the order the workspaces' first sessions
        // started, the sessions outside wmux last; in each, rows of three across, closer and smaller when there are many.
        var groups = sessions.GroupBy(s => s.WorkspaceId).OrderBy(g => g.Key.Length == 0 ? 1 : 0).ThenBy(g => g.Min(s => s.Started)).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        var columns = Math.Max(3, (int)Math.Ceiling(sessions.Count / 4.0));
        var pitch = Math.Min(Pitch, (Right - Left) / (columns - 1));
        var rows = groups.Sum(g => (g.Count() + columns - 1) / columns);
        var aisle = groups.Count > 1 ? Math.Min(Aisle, Deep / 2 / (groups.Count - 1)) : 0f; // 0.17 m up to 10 enclosures, narrower beyond, never none
        var scale = Math.Min(pitch / Pitch, (Deep - (aisle * (groups.Count - 1))) / (((rows - groups.Count) * RowDepth) + (groups.Count * (PenFront + PenBack)))); // an enclosure's first row lies in its margins
        pitch = Pitch * scale;
        var front = Front + (PenFront * scale);
        foreach (var group in groups)
        {
            var members = group.ToList();
            var first = front - (PenFront * scale);
            for (var i = 0; i < members.Count; i++)
            {
                var at = Place(Right - (i % columns) * pitch, 0f, first - (i / columns) * RowDepth * scale, scale: scale);
                roots.Add(new(Key(members[i]), Station, at, Name(shown[members[i].Id]), members[i].Id));
            }

            var back = first - (((members.Count - 1) / columns) * RowDepth * scale) - (PenBack * scale);
            var width = ((columns - 1) * pitch) + (2 * PenSide * scale);
            var title = group.Key.Length == 0 ? Say("No wmux pane", "Sans volet wmux") : group.First().WorkspaceTitle is { Length: > 0 } t ? t : group.Key;
            roots.Add(new(PenKey(group.Key), Pen, Place(Right - ((columns - 1) * pitch / 2), 0f, (front + back) / 2), new Dictionary<string, WidgetValue>
            {
                ["w"] = width, ["d"] = front - back, ["s"] = scale, ["title"] = title.Length > 40 ? title[..39] + "…" : title,
            }));
            front = back - aisle;
        }

        return new(Library, roots);
    }

    public void Pose(double seconds, IDictionary<string, Pose> poses)
    {
        Ready();
        atelier.Poll(seconds);
        origami.Poll(seconds);
        atelier.Pose(seconds, poses);
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

    public void Dispose()
    {
        initialized = false;
        state = new("en", false, []);
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
        Board = new("board", 1), Row = new("row", 1), Mark = new("mark", 1), Pen = new("pen", 1);

    private static readonly SN.Vector3 Steel = new(0.16f, 0.165f, 0.17f), Dark = new(0.04f, 0.04f, 0.045f), Aluminium = new(0.6f, 0.61f, 0.64f),
        Shell = new(0.7f, 0.68f, 0.62f), Orange = new(0.78f, 0.27f, 0.05f), Visor = new(0.012f, 0.014f, 0.018f), Wood = new(0.36f, 0.24f, 0.14f),
        Pale = new(0.84f, 0.86f, 0.9f), Unlit = new(0.03f, 0.03f, 0.035f);

    private static Dictionary<string, WidgetValue> Size(float w, float h, float d) => new() { ["w"] = w, ["h"] = h, ["d"] = d };
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

        // A workspace's enclosure, centred: low rails round w by d metres, a dark floor, and its name on a sign at the front.
        // As small as the layout makes it when many workspaces share the depth (a station's margins at a third of full size
        // or less), else the scene refuses the whole office and keeps the tree it showed before.
        // The sign stands just inside the front rail, at the stations' scale s, and never wider than the rails.
        new(Pen, [new("w", 1.0, 0.02, 4), new("d", 1.0, 0.02, 4), new("s", 1.0, 0.02, 1), Text("title")],
            a =>
            {
                var (w, d, s) = ((float)a.Number("w"), (float)a.Number("d"), (float)a.Number("s"));
                var title = a.Text("title");
                var wide = Math.Max(0.01f, Math.Min(Math.Min(0.42f, 0.02f * title.Length) * s, w - (0.08f * s) - 0.016f));
                var (x, y, z) = (-w / 2 + 0.008f + (0.02f * s) + (wide / 2) + (0.02f * s), 0.04f + (0.035f * s), (d / 2) - 0.016f); // its foot above the rail
                return new([
                    B(0, 0.002f, 0, w, 0.004f, d, new(0.07f, 0.075f, 0.08f), 0),
                    B(0, 0.02f, d / 2, w, 0.03f, 0.016f, Steel, 3), B(0, 0.02f, -d / 2, w, 0.03f, 0.016f, Steel, 3),
                    B(-w / 2, 0.02f, 0, 0.016f, 0.03f, d, Steel, 3), B(w / 2, 0.02f, 0, 0.016f, 0.03f, d, Steel, 3),
                    B(x, y, z, wide + (0.04f * s), 0.07f * s, 0.008f, Dark, 3),
                    new TextPiece(title, new(x, y, z + 0.006f), wide, Pale),
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
