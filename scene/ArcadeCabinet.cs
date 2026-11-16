using Brutal.GlfwApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using SN = System.Numerics;

// An upright arcade cabinet on the right of the hall, beside the pillar, turned a little towards the room: side panels
// with the classic profile and a lit trim, a coin door, a control panel with a stick and three buttons, a screen
// leaning back under a lit marquee. Its screen shows PARSEC, a web game (parsec.games.bu.app) whose script, fetched
// from its site, runs here without a browser (ScriptScreen): a click on the cabinet, or the view 6, walks up to it and
// starts the game; the keys then go to the game, and Esc gives them back. The cabinet is built in its own frame, x across, y up and z towards the player.
internal static class ArcadeCabinet
{
    public static readonly SN.Vector3 Position = new(3.2f, 0f, -1.95f);
    public const float Yaw = -0.436f; // 25 degrees, towards the room
    public const float ScreenWidth = 0.44f, ScreenHeight = 0.33f; // 4:3, as the game's 256 by 192
    public const float MarqueeWidth = 0.56f, MarqueeHeight = 0.24f;

    // The side's outline, (z, y): the front, the control panel, the screen leaning back, the marquee and the top.
    private static readonly SN.Vector2[] Side =
    [
        new(-0.40f, 0f), new(0.30f, 0f), new(0.30f, 0.82f), new(0.40f, 0.92f), new(0.22f, 1.00f), new(0.16f, 1.02f),
        new(0.04f, 1.52f), new(0.16f, 1.58f), new(0.12f, 1.84f), new(-0.40f, 1.84f),
    ];

    private static readonly SN.Vector2 SideCentre = new(-0.2f, 0.9f); // every edge of the outline is seen from it
    private const float Inner = 0.305f, Outer = 0.335f;

    public static SN.Matrix4x4 World { get; } = SN.Matrix4x4.CreateRotationY(Yaw) * SN.Matrix4x4.CreateTranslation(Position);

    // The screen's and the marquee's frames in the cabinet's: x across, y up their face, z out of it.
    public static SN.Matrix4x4 ScreenFrame { get; } = Face(new(0.16f, 1.02f), new(0.04f, 1.52f));
    public static SN.Matrix4x4 MarqueeFrame { get; } = Face(new(0.16f, 1.58f), new(0.12f, 1.84f));

    // The game's broadcast on the bricks, between the clocks of Ottawa and Paris, lit while anyone plays: a 4:3 screen
    // in a dark frame, in the hall's own frame.
    public const float WallWidth = WallSource.Width, WallHeight = WallSource.Height; // between the clocks, over Delta's head
    public const float WallMiddle = WallSource.Middle;
    public static SN.Matrix4x4 WallFrame { get; } = SN.Matrix4x4.CreateTranslation(0f, WallMiddle, WallClocks.Wall + 0.032f);

    public static Vertex[] WallBezel()
    {
        var bezel = new List<Vertex>();
        Boxes.Add(bezel, new(0f, WallMiddle, WallClocks.Wall + 0.015f), new(WallWidth + 0.09f, WallHeight + 0.09f, 0.03f), new Finish(new(0.03f, 0.03f, 0.035f), Material.Matte));
        return [.. bezel];
    }

    // Where the player stands, and the screen's middle, in the hall.
    public static (SN.Vector3 Eye, SN.Vector3 Target) View { get; } =
        (SN.Vector3.Transform(new(0f, 1.56f, 1.55f), World), SN.Vector3.Transform(new(0f, 1.4f, 0.10f), World));

    // The cabinet's box in the hall, for clicks.
    public static (SN.Vector3 Min, SN.Vector3 Max) Bounds { get; } = Box(new(-Outer, 0f, -0.40f), new(Outer, 1.84f, 0.40f));

    // The body, the lit trim along the sides' edges and the coin slots' lights, each its own mesh for its glow.
    public static (Vertex[] Body, Vertex[] Trim, Vertex[] Lights) Build()
    {
        var body = new List<Vertex>();
        var trim = new List<Vertex>();
        var lights = new List<Vertex>();
        var panel = new Finish(new(0.028f, 0.022f, 0.05f), Material.Enamel);
        var edge = new Finish(new(0.1f, 0.55f, 1f), Material.Enamel);
        var black = new Finish(new(0.012f, 0.012f, 0.014f), Material.Matte);
        var steel = new Finish(new(0.2f, 0.2f, 0.21f), Material.Steel);

        // The sides, each with three bands of colour across, from the back towards the control panel.
        foreach (var side in new[] { -1f, 1f })
        {
            var (x0, x1) = side < 0 ? (-Outer, -Inner) : (Inner, Outer);
            Extrude(body, trim, Side, SideCentre, x0, x1, panel, edge);
            var face = side * (Outer + 0.0006f);
            ReadOnlySpan<(SN.Vector3 Color, float Rise)> bands = [(new(1f, 0.42f, 0.08f), 0f), (new(1f, 0.78f, 0.12f), 0.075f), (new(0.12f, 0.75f, 0.95f), 0.15f)];
            foreach (var (color, rise) in bands)
            {
                var finish = new Finish(color, Material.Enamel);
                Quad(body, P(face, -0.38f, 0.30f + rise), P(face, 0.26f, 0.76f + rise), P(face, 0.26f, 0.805f + rise), P(face, -0.38f, 0.345f + rise), finish, side * SN.Vector3.UnitX);
            }
        }

        // Between the sides: the front, the control panel, the screen's bezel, the speaker under the marquee, the marquee's
        // back, the top and the back.
        Across(body, new(0.29f, 0f), new(0.29f, 0.82f), panel);
        Across(body, new(0.30f, 0.82f), new(0.40f, 0.92f), black);
        Across(body, new(0.40f, 0.92f), new(0.22f, 1.00f), new Finish(new(0.05f, 0.05f, 0.06f), Material.Matte));
        Across(body, new(0.22f, 1.00f), new(0.16f, 1.02f), black);
        Across(body, new(0.16f, 1.02f), new(0.04f, 1.52f), black);
        Across(body, new(0.04f, 1.52f), new(0.16f, 1.58f), new Finish(new(0.03f, 0.03f, 0.035f), Material.Matte));
        Across(body, new(0.16f, 1.58f), new(0.12f, 1.84f), black);
        Across(body, new(0.12f, 1.84f), new(-0.40f, 1.84f), panel);
        Across(body, new(-0.40f, 1.84f), new(-0.40f, 0f), panel);

        // The coin door: steel, two lit slots and two returns.
        Boxes.Add(body, new(0f, 0.48f, 0.297f), new(0.30f, 0.36f, 0.014f), steel);
        foreach (var x in new[] { -0.065f, 0.065f })
        {
            Boxes.Add(lights, new(x, 0.57f, 0.306f), new(0.045f, 0.075f, 0.006f), new Finish(new(1f, 0.25f, 0.05f), Material.Glass));
            Boxes.Add(body, new(x, 0.57f, 0.307f), new(0.006f, 0.04f, 0.006f), black);
            Boxes.Add(body, new(x, 0.40f, 0.306f), new(0.06f, 0.035f, 0.006f), black);
        }

        // The control panel: a stick on the left, three buttons on the right, standing on its slope.
        var slope = SN.Vector2.Normalize(new(-0.18f, 0.08f));
        var up = new SN.Vector2(-slope.Y, slope.X); // out of the panel, upwards and towards the player
        if (up.Y < 0) up = -up;
        var spot = new SN.Vector2(0.31f, 0.96f) + (0.004f * up);
        Boxes.Add(body, new(-0.14f, spot.Y, spot.X), new(0.07f, 0.012f, 0.07f), black);
        Boxes.Add(body, new(-0.14f, spot.Y + 0.05f, spot.X), new(0.012f, 0.1f, 0.012f), steel);
        Boxes.Add(body, new(-0.14f, spot.Y + 0.105f, spot.X), new(0.045f, 0.045f, 0.045f), new Finish(new(0.85f, 0.05f, 0.04f), Material.Enamel));
        ReadOnlySpan<(float X, SN.Vector3 Color)> buttons = [(0.03f, new(0.9f, 0.08f, 0.06f)), (0.11f, new(1f, 0.8f, 0.1f)), (0.19f, new(0.1f, 0.45f, 1f))];
        foreach (var (x, color) in buttons)
        {
            Boxes.Add(body, new(x, spot.Y + 0.008f, spot.X), new(0.045f, 0.022f, 0.045f), new Finish(color, Material.Enamel));
        }

        return ([.. body], [.. trim], [.. lights]);
    }

    // What the screen shows before the game, while it loads, or when it could not: drawn once, 256 by 192 RGBA.
    public static byte[] Card(string title, string line, string small, (byte R, byte G, byte B) tint)
    {
        const int width = 256, height = 192;
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i + 3] = 255;
        }

        // A few stars, the same each time.
        for (var k = 0; k < 70; k++)
        {
            var x = (int)(Hash(k, 1) * width);
            var y = (int)(Hash(k, 2) * (height - 40));
            var light = (byte)(80 + (150 * Hash(k, 3)));
            Put(rgba, width, x, y, (light, light, light));
        }

        Write(rgba, width, height, title, 34, 52, true, (90, 220, 255));
        Write(rgba, width, height, line, 13, 112, false, tint);
        Write(rgba, width, height, small, 10, 170, false, (120, 130, 150));
        return rgba;
    }

    // The marquee: PARSEC in a band of colour over the dark, 336 by 144 RGBA.
    public static byte[] Marquee()
    {
        const int width = 336, height = 144;
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var t = y / (float)height;
            var (r, g, b) = ((byte)(20 + (40 * t)), (byte)(8 + (10 * t)), (byte)(60 - (30 * t)));
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, 255);
            }
        }

        foreach (var (y, color) in new[] { (22, (255, 110, 30)), (28, (255, 200, 40)), (116, (255, 200, 40)), (122, (255, 110, 30)) })
        {
            for (var x = 12; x < width - 12; x++)
            {
                Put(rgba, width, x, y, ((byte)color.Item1, (byte)color.Item2, (byte)color.Item3));
                Put(rgba, width, x, y + 1, ((byte)color.Item1, (byte)color.Item2, (byte)color.Item3));
            }
        }

        Write(rgba, width, height, "PARSEC", 64, 72, true, (120, 235, 255));
        return rgba;
    }

    // The DOM code of a key the game takes, by its place on the keyboard; null for the others.
    public static string? Code(GlfwKey key) => (int)key switch
    {
        >= 'A' and <= 'Z' => "Key" + (char)(int)key,
        >= '0' and <= '9' => "Digit" + (char)(int)key,
        _ => key switch
        {
            GlfwKey.Up => "ArrowUp",
            GlfwKey.Down => "ArrowDown",
            GlfwKey.Left => "ArrowLeft",
            GlfwKey.Right => "ArrowRight",
            GlfwKey.Space => "Space",
            GlfwKey.Enter => "Enter",
            _ => null,
        },
    };

    internal static void SelfTest(Action<bool, string> check)
    {
        var (body, trim, lights) = Build();
        check(body.Length % 3 == 0 && trim.Length % 3 == 0 && lights.Length % 3 == 0 && body.Length > 300 && trim.Length > 0 && lights.Length > 0,
            "the cabinet is whole triangles: a body, a trim and lights");
        var (min, max) = Bounds;
        var inside = true;
        foreach (var v in body)
        {
            var p = SN.Vector3.Transform(v.Pos, World);
            inside &= p.X >= min.X - 1e-3f && p.X <= max.X + 1e-3f && p.Y >= -1e-3f && p.Y <= max.Y + 1e-3f && p.Z >= min.Z - 1e-3f && p.Z <= max.Z + 1e-3f;
        }

        check(inside, "the cabinet's bounds hold all of it");
        check(min.X > 2.66f && max.X < 3.7f && max.Z < -1.3f && min.Z > -2.62f,
            "the cabinet stands clear of the steel cabinet (x up to 2.65) and of the pillar (z from -2.65)");
        var (eye, target) = View;
        var facing = SN.Vector3.Normalize(SN.Vector3.TransformNormal(SN.Vector3.UnitZ, World));
        check(SN.Vector3.Dot(SN.Vector3.Normalize(eye - target), facing) > 0.9f && eye.Y > 1.3f && eye.Y < 1.7f,
            "the view stands in front of the screen, at a player's height");
        var normal = SN.Vector3.TransformNormal(SN.Vector3.UnitZ, ScreenFrame);
        check(normal.Z > 0.9f && normal.Y > 0.1f, "the screen faces the player, leaning back");
        check(Code(GlfwKey.W) == "KeyW" && Code((GlfwKey)'1') == "Digit1" && Code(GlfwKey.Up) == "ArrowUp" && Code(GlfwKey.Space) == "Space" &&
            Code(GlfwKey.Escape) is null && Code(GlfwKey.F1) is null, "the game gets the keys by their place, not Esc nor F1");
        var card = Card("PARSEC", "CLICK TO PLAY", "parsec.games.bu.app", (255, 200, 40));
        var lit = 0;
        for (var i = 0; i < card.Length; i += 4)
        {
            lit += card[i] + card[i + 1] + card[i + 2] > 300 ? 1 : 0;
        }

        check(card.Length == 256 * 192 * 4 && lit > 400, "the screen's card is 256 by 192, with its words drawn");
        check(Marquee().Length == 336 * 144 * 4, "the marquee is 336 by 144");
    }

    private static SN.Vector3 P(float x, float z, float y) => new(x, y, z);

    // A face's frame from its bottom and top edges, (z, y), across the cabinet; a few millimetres out, over the body.
    private static SN.Matrix4x4 Face(SN.Vector2 bottom, SN.Vector2 top)
    {
        var along = SN.Vector2.Normalize(top - bottom);
        var tilt = MathF.Atan2(-along.X, along.Y); // from upright, towards the back
        var middle = (bottom + top) / 2f;
        var rotation = SN.Matrix4x4.CreateRotationX(-tilt);
        var outward = SN.Vector3.TransformNormal(SN.Vector3.UnitZ, rotation);
        return rotation * SN.Matrix4x4.CreateTranslation(new SN.Vector3(0f, middle.Y, middle.X) + (0.004f * outward));
    }

    private static (SN.Vector3, SN.Vector3) Box(SN.Vector3 low, SN.Vector3 high)
    {
        var (min, max) = (new SN.Vector3(float.PositiveInfinity), new SN.Vector3(float.NegativeInfinity));
        for (var c = 0; c < 8; c++)
        {
            var p = SN.Vector3.Transform(new((c & 1) == 0 ? low.X : high.X, (c & 2) == 0 ? low.Y : high.Y, (c & 4) == 0 ? low.Z : high.Z), World);
            min = SN.Vector3.Min(min, p);
            max = SN.Vector3.Max(max, p);
        }

        return (min, max);
    }

    // The outline, (z, y), as a slab from x0 to x1: its two faces fanned from centre, its edges in the trim's finish.
    private static void Extrude(List<Vertex> faces, List<Vertex> edges, ReadOnlySpan<SN.Vector2> outline, SN.Vector2 centre, float x0, float x1, Finish face, Finish edge)
    {
        for (var i = 0; i < outline.Length; i++)
        {
            var (a, b) = (outline[i], outline[(i + 1) % outline.Length]);
            foreach (var (x, n) in new[] { (x0, -SN.Vector3.UnitX), (x1, SN.Vector3.UnitX) })
            {
                faces.AddRange([Flat(P(x, centre.X, centre.Y), n, SN.Vector3.UnitZ, face), Flat(P(x, a.X, a.Y), n, SN.Vector3.UnitZ, face), Flat(P(x, b.X, b.Y), n, SN.Vector3.UnitZ, face)]);
            }

            var d = b - a;
            var normal = SN.Vector3.Normalize(new(0f, -d.X, d.Y)); // (z, y) turned a quarter: outwards for a clockwise-in-(z, y) outline
            if (SN.Vector2.Dot(new(normal.Z, normal.Y), ((a + b) / 2f) - centre) < 0f) normal = -normal;
            Quad(edges, P(x0, a.X, a.Y), P(x1, a.X, a.Y), P(x1, b.X, b.Y), P(x0, b.X, b.Y), edge, normal);
        }
    }

    // A panel across the cabinet, between the sides, from a to b, (z, y).
    private static void Across(List<Vertex> o, SN.Vector2 a, SN.Vector2 b, Finish finish) =>
        Quad(o, P(-Inner, a.X, a.Y), P(Inner, a.X, a.Y), P(Inner, b.X, b.Y), P(-Inner, b.X, b.Y), finish);

    private static void Quad(List<Vertex> o, SN.Vector3 a, SN.Vector3 b, SN.Vector3 c, SN.Vector3 d, Finish finish, SN.Vector3? normal = null)
    {
        var n = normal ?? SN.Vector3.Normalize(SN.Vector3.Cross(b - a, d - a));
        var tangent = SN.Vector3.Normalize(b - a);
        o.AddRange([Flat(a, n, tangent, finish), Flat(b, n, tangent, finish), Flat(c, n, tangent, finish), Flat(a, n, tangent, finish), Flat(c, n, tangent, finish), Flat(d, n, tangent, finish)]);
    }

    private static Vertex Flat(SN.Vector3 p, SN.Vector3 n, SN.Vector3 tangent, Finish finish) => new(p, n, finish.Color, finish.Material, tangent, new(0f, 0f, 1f, 1f));

    private static void Write(byte[] rgba, int width, int height, string text, int pixels, int centreY, bool bold, (byte R, byte G, byte B) color)
    {
        if (Splash.Text(text, pixels, out var w, out var h, bold) is not { } glyphs)
        {
            return;
        }

        var left = (width - w) / 2;
        var top = centreY - (h / 2);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (px, py) = (left + x, top + y);
                if (px < 0 || py < 0 || px >= width || py >= height)
                {
                    continue;
                }

                var a = glyphs[(((y * w) + x) * 4) + 3] / 255f;
                var i = ((py * width) + px) * 4;
                rgba[i] = (byte)((rgba[i] * (1 - a)) + (color.R * a));
                rgba[i + 1] = (byte)((rgba[i + 1] * (1 - a)) + (color.G * a));
                rgba[i + 2] = (byte)((rgba[i + 2] * (1 - a)) + (color.B * a));
            }
        }
    }

    private static void Put(byte[] rgba, int width, int x, int y, (byte R, byte G, byte B) color)
    {
        var i = ((y * width) + x) * 4;
        (rgba[i], rgba[i + 1], rgba[i + 2]) = color;
    }

    private static float Hash(int a, int b)
    {
        var h = (uint)((a * 374761393) + (b * 668265263));
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h ^ (h >> 16)) / (float)uint.MaxValue;
    }
}

// The cabinet in the frame: its meshes, its screen and marquee, and the game behind the screen. Owned by the render
// thread; the game starts and stops on the thread pool, so that neither blocks a frame.
internal sealed class ArcadeStage : IDisposable
{
    public const string Url = "https://parsec.games.bu.app/";
    private const ulong Live = 1UL << 40; // the game's frames are shown as revisions above this, the cards' below
    private readonly Mesh body, trim, lights, bezel;
    private readonly ScreenGpuRenderer screen, marquee, wall;
    private readonly string url, storage;
    private readonly byte[] live = new byte[256 * 192 * 4];
    private readonly HashSet<string> held = [];
    private Task<ScriptScreen>? starting;
    private ScriptScreen? game;
    private ulong seen;
    private (string Key, ulong Revision, byte[] Pixels)? card;
    private ulong cards;
    private string? failure;
    private string? pilot; // a film's: the game stepped by the scene's time, silent, and played by this script
    private double owed; // a stepped game's time not yet stepped, ms
    private bool robot; // the game starting or running is the small robot's (ArcadePlayer), silent, played by the pilot

    // surface makes a screen: its pixels, width, height and revision, its rectangle in its frame, and its scanlines.
    public ArcadeStage(Func<Vertex[], Mesh> mesh, Func<byte[], int, int, ulong, SN.Vector4, float, ScreenGpuRenderer> surface, string? url = null)
    {
        this.url = url ?? Url;
        storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", "arcade-storage.json");
        var (b, t, l) = ArcadeCabinet.Build();
        body = mesh(b);
        trim = mesh(t);
        lights = mesh(l);
        var (w, h) = (ArcadeCabinet.ScreenWidth / 2f, ArcadeCabinet.ScreenHeight / 2f);
        screen = surface(Card(), 256, 192, card!.Value.Revision, new(-w, h, w, -h), 0.35f);
        var (mw, mh) = (ArcadeCabinet.MarqueeWidth / 2f, ArcadeCabinet.MarqueeHeight / 2f);
        marquee = surface(ArcadeCabinet.Marquee(), 336, 144, 1, new(-mw, mh, mw, -mh), 0f);
        var (ww, wh) = (ArcadeCabinet.WallWidth / 2f, ArcadeCabinet.WallHeight / 2f);
        wall = surface(card!.Value.Pixels, 256, 192, card.Value.Revision, new(-ww, wh, ww, -wh), 0.25f);
        bezel = mesh(ArcadeCabinet.WallBezel());
    }

    // The keys go to the game from the click that starts it until Esc: a person's game, or a film's.
    public bool Playing => (starting is not null || game is not null) && !robot;

    // The small robot plays, or its game is starting: it stands at the cabinet.
    public bool RobotPlaying => (starting is not null || game is not null) && robot;

    // Someone plays, a person or the robot: the wall shows the game, unless it shows another source (WallSource).
    public bool Broadcasting => game is not null;

    // False while the wall shows Pong or a session: the scene draws that one, in the same bezel.
    public bool WallParsec { get; set; } = true;

    public ulong Frames => game?.Frames ?? 0;

    public bool Hit(SN.Matrix4x4 viewProjection, int width, int height, double x, double y)
    {
        var (min, max) = ArcadeCabinet.Bounds;
        var (left, top, right, bottom) = (float.PositiveInfinity, float.PositiveInfinity, float.NegativeInfinity, float.NegativeInfinity);
        for (var c = 0; c < 8; c++)
        {
            var p = SN.Vector4.Transform(new SN.Vector4((c & 1) == 0 ? min.X : max.X, (c & 2) == 0 ? min.Y : max.Y, (c & 4) == 0 ? min.Z : max.Z, 1f), viewProjection);
            if (!float.IsFinite(p.W) || p.W <= 0)
            {
                return false;
            }

            var (sx, sy) = ((p.X / p.W + 1f) * width * 0.5f, (p.Y / p.W + 1f) * height * 0.5f);
            (left, right, top, bottom) = (Math.Min(left, sx), Math.Max(right, sx), Math.Min(top, sy), Math.Max(bottom, sy));
        }

        return x >= left && x <= right && y >= top && y <= bottom;
    }

    // A person's game, or a film's: the robot, if it plays, steps down first.
    public void Play()
    {
        if (Playing)
        {
            return;
        }

        if (robot)
        {
            Leave();
        }

        failure = null;
        var (address, file, script) = (url, storage, pilot);
        starting = Task.Run(() => ScriptScreen.Start(address, script is null ? file : null, "#cv", 256, 192, TimeSpan.FromSeconds(25), script, stepped: script is not null));
        Console.WriteLine($"ARCADE parsec fetching {url}");
    }

    // The small robot's game while nobody plays: live, silent, without the person's stored scores, played by the pilot.
    public void PlayRobot(string pilotScript)
    {
        if (starting is not null || game is not null)
        {
            return;
        }

        robot = true;
        failure = null;
        var address = url;
        starting = Task.Run(() => ScriptScreen.Start(address, null, "#cv", 256, 192, TimeSpan.FromSeconds(25), pilotScript, muted: true));
        Console.WriteLine($"ARCADE parsec fetching {url} for the robot");
    }

    // For a film (ArcadeFilm): the game played by a pilot script, its frames stepped by the scene's time.
    public void PlayFilmed(string pilotScript)
    {
        pilot = pilotScript;
        Play();
    }

    public bool Holding(string code) => game?.Holding(code) ?? false;

    // A filmed game's sound since the last call, at 44.1 kHz mono.
    public float[] TakeSound() => pilot is not null && game is not null ? game.TakeSound() : [];

    public void Leave()
    {
        foreach (var code in held)
        {
            game?.Key(code, false);
        }

        held.Clear();
        var (running, pending) = (game, starting);
        (game, starting) = (null, null);
        if (running is not null)
        {
            Console.WriteLine($"ARCADE parsec left after {running.Frames} frames{(robot ? " by the robot" : "")}");
        }

        robot = false;

        _ = Task.Run(() =>
        {
            running?.Dispose();
            try
            {
                pending?.Result.Dispose();
            }
            catch (AggregateException)
            {
            }
        });
    }

    public void Key(GlfwKey key, bool down)
    {
        if (ArcadeCabinet.Code(key) is not { } code || game is null || (down ? !held.Add(code) : !held.Remove(code)))
        {
            return;
        }

        game.Key(code, down);
    }

    // Once a frame, dt seconds after the last: takes the started game, or why it did not start, and steps a filmed one.
    public void Advance(float dt = 0f)
    {
        if (starting is { IsCompleted: true } done)
        {
            starting = null;
            if (done.IsCompletedSuccessfully)
            {
                game = done.Result;
                game.ClickOn("#start"); // the page's own start, which also lets it play sound
                Console.WriteLine($"ARCADE parsec running, its script in Jint{(robot ? ", played by the robot" : "")}");
            }
            else
            {
                failure = done.Exception?.InnerException?.Message ?? "?";
                Console.Error.WriteLine($"ARCADE parsec did not start: {failure}");
            }
        }

        if (pilot is not null && game is not null)
        {
            const double Frame = 1000.0 / 60.0;
            owed = Math.Min(owed + (dt * 1000.0), 8 * Frame);
            while (owed >= Frame && game.Step(Frame))
            {
                owed -= Frame;
            }
        }

        if (game is { Running: false } stopped)
        {
            failure = stopped.Failure ?? Words.Say("The game stopped", "Le jeu s'est arrêté");
            Console.Error.WriteLine($"ARCADE parsec stopped: {failure}");
            Leave();
        }
    }

    public void UploadAfterFence(CommandBuffer commands, int slot)
    {
        if (game is not null)
        {
            var revision = game.Copy(live, seen);
            if (revision != 0)
            {
                seen = revision;
                screen.UploadAfterFence(commands, slot, live, Live + revision);
                wall.UploadAfterFence(commands, slot, live, Live + revision);
                return;
            }
        }

        var pixels = Card();
        screen.UploadAfterFence(commands, slot, pixels, card!.Value.Revision);
    }

    public void DrawMeshes(Action<Mesh, SN.Matrix4x4, SN.Vector4> draw)
    {
        draw(body, ArcadeCabinet.World, default);
        draw(trim, ArcadeCabinet.World, new(0.1f, 0.55f, 1f, 0.35f));
        draw(lights, ArcadeCabinet.World, new(1f, 0.25f, 0.05f, 0.9f));
        if (Broadcasting || !WallParsec)
        {
            draw(bezel, SN.Matrix4x4.Identity, default);
        }
    }

    // After every mesh: the screens bind their own pipeline.
    public void DrawScreens(CommandBuffer commands, int slot, SN.Matrix4x4 viewProjection)
    {
        screen.Draw(commands, slot, ArcadeCabinet.ScreenFrame * ArcadeCabinet.World * viewProjection);
        marquee.Draw(commands, slot, ArcadeCabinet.MarqueeFrame * ArcadeCabinet.World * viewProjection);
        if (Broadcasting && WallParsec)
        {
            wall.Draw(commands, slot, ArcadeCabinet.WallFrame * viewProjection);
        }
    }

    public void Dispose()
    {
        Leave();
        game?.Dispose();
        screen.Dispose();
        marquee.Dispose();
        wall.Dispose();
        bezel.Dispose();
        body.Dispose();
        trim.Dispose();
        lights.Dispose();
    }

    // The card the screen shows when the game does not: inviting, loading, or why it could not.
    private byte[] Card()
    {
        var (line, tint) = failure is not null ? (Words.Say("COULD NOT START", "IMPOSSIBLE DE LANCER"), ((byte)255, (byte)90, (byte)70))
            : starting is not null ? (Words.Say("LOADING...", "CHARGEMENT..."), ((byte)255, (byte)200, (byte)40))
            : (Words.Say("CLICK TO PLAY", "CLIQUE POUR JOUER"), ((byte)255, (byte)200, (byte)40));
        var small = failure ?? "parsec.games.bu.app";
        small = small.Length > 44 ? small[..44] : small;
        if (card is not { } kept || kept.Key != line + "\n" + small)
        {
            card = (line + "\n" + small, ++cards, ArcadeCabinet.Card("PARSEC", line, small, tint));
        }

        return card.Value.Pixels;
    }
}
