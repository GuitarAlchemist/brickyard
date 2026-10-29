using Delta.SceneModules;
using SN = System.Numerics;
using static Delta.SceneModules.Widgets;

namespace Delta.AgentOffice;

// The agents' office: a station per Claude Code session on the robot's left, each a desk with a small Delta at it.
// Everything is widgets nested in widgets: a station holds a desk, a name plate and a robot fixed to the desk's seat;
// the robot a torso, with the head and the arms fixed to its anchors; the torso Mac minis, each with its status light.
// The robot types while its session works, lights a red beacon while the session waits for an answer and jumps, arms
// up, when the session has finished: poses on the instances' paths, no mesh rebuilt. In flat quality it is a plain
// board instead, a lamp and a name per session.
//
// This assembly is loaded into a collectible context and replaced while the scene runs, so it keeps no static state.
public sealed class OfficeModule : IOfficeModule
{
    private const float Pitch = 0.38f; // between stations at full size, in metres
    private const float Left = -1.45f, Right = -0.69f, Front = 0.3f;
    private const double DoneHop = 2.4; // seconds of jumping after a session finishes

    private OfficeState state = new("en", false, []);
    private bool initialized;

    public string Id => "agent-office";

    public int Revision { get; private set; }

    public void Initialize()
    {
        if (initialized) throw new InvalidOperationException("The office is already initialized.");
        initialized = true;
    }

    public void Update(OfficeState next)
    {
        Ready();
        var layout = new HashCode();
        layout.Add(next.Language);
        layout.Add(next.Flat);
        foreach (var s in next.Sessions) { layout.Add(s.Id); layout.Add(s.Name); }
        state = next;
        Revision = layout.ToHashCode();
    }

    public WidgetScene Scene()
    {
        Ready();
        var roots = new List<UsePiece>();
        var sessions = state.Sessions;
        if (state.Flat)
        {
            var top = 0.48f + Math.Max(1, sessions.Count) * 0.085f;
            roots.Add(new("board", Board, Place(-1.05f, 0f, -0.3f), new Dictionary<string, WidgetValue>
            {
                ["rows"] = Math.Max(1, sessions.Count),
                ["title"] = Say("Agents' office", "Bureau des agents"),
            }));
            if (sessions.Count == 0) roots.Add(new("none", Row, Place(-1.05f, top - 0.135f, -0.288f), Name(Say("No session", "Aucune session"))));
            for (var i = 0; i < sessions.Count; i++)
                roots.Add(new(Key(sessions[i]), Row, Place(-1.05f, top - 0.135f - 0.085f * i, -0.288f), Name(sessions[i].Name), sessions[i].Id));
            return new(Library, roots);
        }

        if (sessions.Count == 0)
        {
            roots.Add(new("none", Station, Place(-1.07f, 0f, Front), Name(Say("No session", "Aucune session"))));
            return new(Library, roots);
        }

        // Rows going back from the robot's front, three across, closer and smaller when there are many.
        var columns = Math.Max(3, (int)Math.Ceiling(sessions.Count / 4.0));
        var pitch = Math.Min(Pitch, (Right - Left) / (columns - 1));
        var scale = pitch / Pitch;
        for (var i = 0; i < sessions.Count; i++)
        {
            var at = Place(Right - (i % columns) * pitch, 0f, Front - (i / columns) * 0.45f * scale, scale: scale);
            roots.Add(new(Key(sessions[i]), Station, at, Name(sessions[i].Name), sessions[i].Id));
        }

        return new(Library, roots);
    }

    public void Pose(double seconds, IDictionary<string, Pose> poses)
    {
        Ready();
        var t = (float)seconds;
        foreach (var s in state.Sessions)
        {
            var age = (float)Math.Max(0, seconds - s.Since);
            var glow = Glow(s.Activity);
            var blink = (int)(t * 2.5f) % 2 == 0;
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

            if (s.Activity == OfficeActivity.Done && age < DoneHop)
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

    private static Dictionary<string, WidgetValue> Name(string name) =>
        new() { ["name"] = name.Length > 40 ? name[..39] + "…" : name.Length == 0 ? "?" : name };

    private static SN.Vector4 Glow(OfficeActivity activity) => activity switch
    {
        OfficeActivity.Working => new(0.35f, 0.8f, 1f, 1f),
        OfficeActivity.Waiting => new(1f, 0.12f, 0.08f, 1f),
        OfficeActivity.Done => new(0.35f, 1f, 0.5f, 1f),
        _ => new(0.12f, 0.22f, 0.28f, 0.5f),
    };

    // The library. Sizes in metres; materials: 0 matte, 1 enamel, 2 aluminium, 3 steel, 4 rubber, 5 glass.
    private static readonly WidgetId Block = new("block", 1), MacMini = new("mac-mini", 1), Torso = new("torso", 1), Head = new("head", 1),
        Arm = new("arm", 1), Robot = new("robot", 1), Desk = new("desk", 1), Plate = new("plate", 1), Station = new("station", 1),
        Board = new("board", 1), Row = new("row", 1);

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

        // A session's station: the desk, its name, and a robot on the desk's seat.
        new(Station, [Text("name")],
            a => new([
                new UsePiece("desk", Desk, SN.Matrix4x4.Identity),
                new UsePiece("plate", Plate, Place(0f, 0.2f, 0.0905f), new Dictionary<string, WidgetValue> { ["name"] = a.Text("name") }),
                new AttachPiece("desk", "seat", new("robot", Robot, SN.Matrix4x4.Identity)),
            ])),

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
                    B(0, 0, -0.004f, 0.88f, 0.08f, 0.002f, Dark, 0)]);
            }),
    ];
}
