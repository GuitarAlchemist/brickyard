using SN = System.Numerics;

// Two clocks on the wall behind the robot, Ottawa's and Paris's, as in a film's operations room: a dial in a polished
// steel bezel whose hands keep each city's time, an aluminium plate with the city's name and, beneath, a red
// seven-segment display of its hours, minutes and seconds. All of it is geometry, sharp at any distance. The bodies are
// one mesh; the hands and the digits are meshes of their own, posed at each frame (WallClockStage).
internal static class WallClocks
{
    public const float Wall = -3.3f; // the face of the bricks
    public const float Height = 1.98f; // the dials' centre
    public const float Radius = 0.3f; // the dial, inside the bezel
    public const float PlateHeight = 1.6f;
    public const float DisplayHeight = 1.47f;
    public const float DigitPitch = 0.068f;
    public const float ColonPitch = 0.03f;

    // The cities, west to east, with the zone that keeps each one's time: its IANA name, then Windows' own.
    public static readonly (string Name, float X, string Iana, string Windows)[] Cities =
    [
        ("OTTAWA", -1.45f, "America/Toronto", "Eastern Standard Time"),
        ("PARIS", 1.45f, "Europe/Paris", "Romance Standard Time"),
    ];

    private static readonly Finish Steel = new(new(0.78f, 0.78f, 0.8f), Material.Steel);
    private static readonly Finish DarkSteel = new(new(0.16f, 0.16f, 0.18f), Material.Steel);
    private static readonly Finish Dial = new(new(0.93f, 0.91f, 0.86f), Material.Enamel);
    private static readonly Finish Ink = new(new(0.03f, 0.03f, 0.035f), Material.Enamel);
    private static readonly Finish Red = new(new(0.85f, 0.07f, 0.05f), Material.Enamel);
    private static readonly Finish Plate = new(new(0.72f, 0.72f, 0.7f), Material.Aluminium);
    private static readonly Finish Screen = new(new(0.012f, 0.008f, 0.008f), Material.Glass);

    // A display's lit segments: their colour, and the glow pushed with them.
    public static readonly Finish Lit = new(new(1f, 0.16f, 0.08f), Material.Glass);
    public static readonly SN.Vector4 Glow = new(1f, 0.09f, 0.04f, 0.85f);
    public static readonly Finish Unlit = new(new(0.07f, 0.018f, 0.015f), Material.Glass);

    // The segments each digit lights, a to g: top, upper right, lower right, bottom, lower left, upper left, middle.
    public static readonly string[] Segments = ["abcdef", "bc", "abdeg", "abcdg", "bcfg", "acdfg", "acdefg", "abc", "abcdefg", "abcdfg"];

    // The time in a city's zone at an instant, or the instant's UTC when the zone is unknown here.
    public static DateTimeOffset In(DateTimeOffset instant, string iana, string windows)
    {
        try
        {
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(iana); }
            catch (TimeZoneNotFoundException) when (OperatingSystem.IsWindows()) { zone = TimeZoneInfo.FindSystemTimeZoneById(windows); }
            return TimeZoneInfo.ConvertTime(instant, zone);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException or System.Security.SecurityException)
        {
            return instant.ToUniversalTime();
        }
    }

    // The hands' angles, clockwise from twelve, in radians. The second hand ticks: it leaves each second in a tenth of a
    // second, and rests the rest of it.
    public static (float Hour, float Minute, float Second) Angles(DateTimeOffset local)
    {
        var second = local.Second + Tick((float)(local.Millisecond / 1000.0));
        var minute = local.Minute + (second / 60f);
        var hour = (local.Hour % 12) + (minute / 60f);
        return (hour / 12f * MathF.Tau, minute / 60f * MathF.Tau, second / 60f * MathF.Tau);
    }

    private static float Tick(float t)
    {
        var x = Math.Clamp(t / 0.1f, 0f, 1f);
        return x * x * (3f - (2f * x));
    }

    // The bezels, dials, indices, caps, plates, names and display housings of every city, in the hall's coordinates.
    public static Vertex[] Bodies()
    {
        var o = new List<Vertex>();
        foreach (var (name, x, _, _) in Cities)
        {
            var c = new SN.Vector3(x, Height, Wall);

            // The bezel, a polished half-round of steel from the wall to the dial; a dark ring behind it holds it off.
            Revolve(o, c, Bezel(Radius, Radius + 0.045f, 0.05f, 0.02f), 72, Steel);
            Revolve(o, c, [(Radius + 0.045f, 0f, 1f, 0f), (Radius + 0.045f, -0.004f, 1f, 0f)], 72, DarkSteel);
            Disc(o, c + new SN.Vector3(0f, 0f, 0.02f), Radius, 72, Dial);

            // A minute track, longer and bolder at the hours, and a double index at twelve.
            for (var m = 0; m < 60; m++)
            {
                var a = m / 60f * MathF.Tau;
                var hour = m % 5 == 0;
                var (inner, width) = m == 0 ? (Radius - 0.075f, 0.0f) : hour ? (Radius - 0.07f, 0.014f) : (Radius - 0.028f, 0.0035f);
                if (m == 0)
                {
                    Bar(o, c, a, -0.011f, inner, Radius - 0.016f, 0.009f, 0.021f, 0.0025f, Ink);
                    Bar(o, c, a, 0.011f, inner, Radius - 0.016f, 0.009f, 0.021f, 0.0025f, Ink);
                    continue;
                }

                Bar(o, c, a, 0f, inner, Radius - 0.016f, width, 0.021f, hour ? 0.0025f : 0.0012f, Ink);
            }

            // The hands' pin: a red jewel on a steel cap.
            Revolve(o, c, [(0.014f, 0.036f, 0f, 1f), (0.014f, 0.043f, 0f, 1f), (0.0f, 0.043f, 0f, 1f)], 24, Steel);
            Revolve(o, c, [(0.005f, 0.043f, 0.6f, 0.8f), (0.0f, 0.047f, 0f, 1f)], 16, Red);

            // The name, engraved in black on brushed aluminium.
            Boxes.Add(o, new(x, PlateHeight, Wall + 0.005f), new(0.3f, 0.056f, 0.01f), Plate);
            Words(o, name, new(x, PlateHeight, Wall + 0.0105f), 0.03f, Ink);

            // The display: a dark steel housing, its smoked glass a little behind its lip.
            var housing = (6 * DigitPitch) + (2 * ColonPitch) + 0.08f;
            Boxes.Add(o, new(x, DisplayHeight, Wall + 0.025f), new(housing, 0.17f, 0.05f), DarkSteel);
            Boxes.Add(o, new(x, DisplayHeight, Wall + 0.0505f), new(housing - 0.036f, 0.13f, 0.002f), Screen);
        }

        return [.. o];
    }

    // A hand drawn from the pin towards twelve, to be turned about the dial's centre: hour, minute or second.
    public static Vertex[] Hand(int which)
    {
        var o = new List<Vertex>();
        switch (which)
        {
            case 0:
                Prism(o, [new(-0.004f, -0.035f), new(0.004f, -0.035f), new(0.012f, 0f), new(0.006f, 0.15f), new(0f, 0.172f), new(-0.006f, 0.15f), new(-0.012f, 0f)], 0.024f, 0.028f, Ink);
                break;
            case 1:
                Prism(o, [new(-0.003f, -0.04f), new(0.003f, -0.04f), new(0.009f, 0f), new(0.0045f, 0.235f), new(0f, 0.258f), new(-0.0045f, 0.235f), new(-0.009f, 0f)], 0.029f, 0.032f, Ink);
                break;
            default:
                Prism(o, [new(-0.0018f, -0.075f), new(0.0018f, -0.075f), new(0.0012f, 0.272f), new(-0.0012f, 0.272f)], 0.033f, 0.035f, Red);
                Prism(o, Circle(new(0f, -0.06f), 0.011f, 20), 0.033f, 0.035f, Red);
                break;
        }

        return [.. o];
    }

    // A digit's segments, or all seven of them for the unlit "8" behind, centred on the origin, a little in italic.
    public static Vertex[] Digit(string segments, Finish finish)
    {
        const float w = 0.025f, h = 0.047f, t = 0.0105f, g = 0.0016f;
        var o = new List<Vertex>();
        foreach (var s in segments)
        {
            var (a, b) = s switch
            {
                'a' => (new SN.Vector2(-w, h), new SN.Vector2(w, h)),
                'b' => (new SN.Vector2(w, h), new SN.Vector2(w, 0f)),
                'c' => (new SN.Vector2(w, 0f), new SN.Vector2(w, -h)),
                'd' => (new SN.Vector2(-w, -h), new SN.Vector2(w, -h)),
                'e' => (new SN.Vector2(-w, 0f), new SN.Vector2(-w, -h)),
                'f' => (new SN.Vector2(-w, h), new SN.Vector2(-w, 0f)),
                _ => (new SN.Vector2(-w, 0f), new SN.Vector2(w, 0f)),
            };
            var along = SN.Vector2.Normalize(b - a);
            var across = new SN.Vector2(-along.Y, along.X) * (t / 2f);
            var (p, q) = (a + (along * ((t / 2f) + g)), b - (along * ((t / 2f) + g)));
            ReadOnlySpan<SN.Vector2> hexagon = [p, p + (along * (t / 2f)) + across, q - (along * (t / 2f)) + across, q, q - (along * (t / 2f)) - across, p + (along * (t / 2f)) - across];
            Prism(o, [.. Italic(hexagon)], 0.051f, 0.0525f, finish);
        }

        return [.. o];
    }

    // The two dots between hours, minutes and seconds.
    public static Vertex[] Colon(Finish finish)
    {
        var o = new List<Vertex>();
        foreach (var y in new[] { -0.018f, 0.018f })
        {
            Prism(o, [.. Italic([new(-0.005f, y - 0.005f), new(0.005f, y - 0.005f), new(0.005f, y + 0.005f), new(-0.005f, y + 0.005f)])], 0.051f, 0.0525f, finish);
        }

        return [.. o];
    }

    // Where a city's display shows its n-th digit (0 to 5) or colon (6, 7), from the city's x.
    public static SN.Vector3 DisplaySlot(float x, int slot)
    {
        var left = x - (((6 * DigitPitch) + (2 * ColonPitch)) / 2f);
        var at = slot switch
        {
            < 2 => left + (DigitPitch * (slot + 0.5f)),
            < 4 => left + ColonPitch + (DigitPitch * (slot + 0.5f)),
            < 6 => left + (2 * ColonPitch) + (DigitPitch * (slot + 0.5f)),
            6 => left + (2 * DigitPitch) + (ColonPitch / 2f),
            _ => left + (4 * DigitPitch) + (1.5f * ColonPitch),
        };
        return new(at, DisplayHeight, Wall);
    }

    private static IEnumerable<SN.Vector2> Italic(ReadOnlySpan<SN.Vector2> points)
    {
        var slanted = new SN.Vector2[points.Length];
        for (var i = 0; i < points.Length; i++) slanted[i] = new(points[i].X + (0.1f * points[i].Y), points[i].Y);
        return slanted;
    }

    // The half-round profile of a bezel from its outer foot on the wall, over its crown, down to the dial's rim: radius,
    // height and the profile's normal, outward and forward.
    private static (float R, float Z, float Nr, float Nz)[] Bezel(float inner, float outer, float height, float dial)
    {
        const int steps = 14;
        var profile = new (float, float, float, float)[steps + 1];
        var (middle, half) = ((inner + outer) / 2f, (outer - inner) / 2f);
        for (var i = 0; i <= steps; i++)
        {
            var a = MathF.PI * i / steps;
            var n = SN.Vector2.Normalize(new(MathF.Cos(a) / half, MathF.Sin(a) / height));
            profile[i] = (middle + (half * MathF.Cos(a)), (dial * i / steps) + (height * MathF.Sin(a)), n.X, n.Y);
        }

        return profile;
    }

    // A profile turned about the dial's axis, in segments, with smooth normals.
    private static void Revolve(List<Vertex> o, SN.Vector3 c, ReadOnlySpan<(float R, float Z, float Nr, float Nz)> profile, int segments, Finish finish)
    {
        for (var s = 0; s < segments; s++)
        {
            var (a0, a1) = (MathF.Tau * s / segments, MathF.Tau * (s + 1) / segments);
            for (var i = 0; i + 1 < profile.Length; i++)
            {
                Vertex At((float R, float Z, float Nr, float Nz) p, float a)
                {
                    var (cos, sin) = (MathF.Cos(a), MathF.Sin(a));
                    var normal = SN.Vector3.Normalize(new(p.Nr * cos, p.Nr * sin, p.Nz));
                    return Flat(c + new SN.Vector3(p.R * cos, p.R * sin, p.Z), normal, new(-sin, cos, 0f), finish);
                }

                var (p00, p01, p10, p11) = (At(profile[i], a0), At(profile[i], a1), At(profile[i + 1], a0), At(profile[i + 1], a1));
                o.AddRange([p00, p01, p11, p00, p11, p10]);
            }
        }
    }

    private static void Disc(List<Vertex> o, SN.Vector3 c, float r, int segments, Finish finish) =>
        Revolve(o, c, [(r, 0f, 0f, 1f), (0f, 0f, 0f, 1f)], segments, finish);

    // A bar on the dial along the angle a (clockwise from twelve), offset sideways by side, from r0 to r1 out.
    private static void Bar(List<Vertex> o, SN.Vector3 c, float a, float side, float r0, float r1, float width, float z, float depth, Finish finish)
    {
        var turn = SN.Matrix4x4.CreateRotationZ(-a) * SN.Matrix4x4.CreateTranslation(c);
        var box = new List<Vertex>();
        Boxes.Add(box, new(side, (r0 + r1) / 2f, z + (depth / 2f)), new(width, r1 - r0, depth), finish);
        foreach (var v in box)
        {
            o.Add(v with { Pos = SN.Vector3.Transform(v.Pos, turn), Normal = SN.Vector3.TransformNormal(v.Normal, turn), Tangent = SN.Vector3.TransformNormal(v.Tangent, turn) });
        }
    }

    // A convex outline in the plane facing the room, raised from z0 to z1 off the wall's axis: its front and its sides.
    private static void Prism(List<Vertex> o, ReadOnlySpan<SN.Vector2> outline, float z0, float z1, Finish finish)
    {
        var centre = SN.Vector2.Zero;
        foreach (var p in outline) centre += p;
        centre /= outline.Length;
        for (var i = 0; i < outline.Length; i++)
        {
            var (a, b) = (outline[i], outline[(i + 1) % outline.Length]);
            o.AddRange([Flat(new(centre, z1), SN.Vector3.UnitZ, SN.Vector3.UnitX, finish), Flat(new(a, z1), SN.Vector3.UnitZ, SN.Vector3.UnitX, finish), Flat(new(b, z1), SN.Vector3.UnitZ, SN.Vector3.UnitX, finish)]);
            var out2 = new SN.Vector2(b.Y - a.Y, a.X - b.X);
            if (SN.Vector2.Dot(out2, ((a + b) / 2f) - centre) < 0f) out2 = -out2;
            var n = SN.Vector3.Normalize(new(out2, 0f));
            o.AddRange([Flat(new(a, z0), n, SN.Vector3.UnitZ, finish), Flat(new(b, z0), n, SN.Vector3.UnitZ, finish), Flat(new(b, z1), n, SN.Vector3.UnitZ, finish),
                Flat(new(a, z0), n, SN.Vector3.UnitZ, finish), Flat(new(b, z1), n, SN.Vector3.UnitZ, finish), Flat(new(a, z1), n, SN.Vector3.UnitZ, finish)]);
        }
    }

    private static SN.Vector2[] Circle(SN.Vector2 c, float r, int n)
    {
        var points = new SN.Vector2[n];
        for (var i = 0; i < n; i++) points[i] = c + (r * new SN.Vector2(MathF.Cos(MathF.Tau * i / n), MathF.Sin(MathF.Tau * i / n)));
        return points;
    }

    // A vertex of a surface with no box edges to round: the Low shader's edge term leaves it alone.
    private static Vertex Flat(SN.Vector3 p, SN.Vector3 n, SN.Vector3 tangent, Finish finish) => new(p, n, finish.Color, finish.Material, tangent, new(0f, 0f, 1f, 1f));

    // The strokes of the capitals the names use, in a unit box, as polylines.
    private static readonly Dictionary<char, SN.Vector2[][]> Strokes = new()
    {
        ['A'] = [[new(0f, 0f), new(0.5f, 1f), new(1f, 0f)], [new(0.22f, 0.4f), new(0.78f, 0.4f)]],
        ['I'] = [[new(0.5f, 0f), new(0.5f, 1f)]],
        ['O'] = [[.. Circle(new(0.5f, 0.5f), 0.5f, 28).Select(p => new SN.Vector2(0.5f + ((p.X - 0.5f) * 0.95f), p.Y)), new(0.975f, 0.5f)]],
        ['P'] = [[new(0f, 0f), new(0f, 1f), new(0.62f, 1f), new(0.84f, 0.95f), new(0.95f, 0.82f), new(0.95f, 0.66f), new(0.84f, 0.53f), new(0.62f, 0.48f), new(0f, 0.48f)]],
        ['R'] = [[new(0f, 0f), new(0f, 1f), new(0.62f, 1f), new(0.84f, 0.95f), new(0.95f, 0.82f), new(0.95f, 0.66f), new(0.84f, 0.53f), new(0.62f, 0.48f), new(0f, 0.48f)], [new(0.55f, 0.48f), new(1f, 0f)]],
        ['S'] = [[new(0.95f, 0.84f), new(0.8f, 0.97f), new(0.5f, 1f), new(0.2f, 0.97f), new(0.05f, 0.85f), new(0.05f, 0.66f), new(0.2f, 0.56f), new(0.5f, 0.5f), new(0.8f, 0.44f), new(0.95f, 0.34f), new(0.95f, 0.15f), new(0.8f, 0.03f), new(0.5f, 0f), new(0.2f, 0.03f), new(0.05f, 0.16f)]],
        ['T'] = [[new(0f, 1f), new(1f, 1f)], [new(0.5f, 1f), new(0.5f, 0f)]],
        ['W'] = [[new(0f, 1f), new(0.22f, 0f), new(0.5f, 0.62f), new(0.78f, 0f), new(1f, 1f)]],
    };

    // A word in stroked capitals, centred on c, its letters h high, raised a little from the plate.
    private static void Words(List<Vertex> o, string word, SN.Vector3 c, float h, Finish finish)
    {
        var (width, pitch, stroke) = (h * 0.72f, h * 0.98f, h * 0.15f);
        var left = c.X - ((pitch * (word.Length - 1)) + width) / 2f;
        for (var i = 0; i < word.Length; i++)
        {
            foreach (var line in Strokes[word[i]])
            {
                for (var k = 0; k + 1 < line.Length; k++)
                {
                    var a = new SN.Vector2(left + (pitch * i) + (line[k].X * width), c.Y - (h / 2f) + (line[k].Y * h));
                    var b = new SN.Vector2(left + (pitch * i) + (line[k + 1].X * width), c.Y - (h / 2f) + (line[k + 1].Y * h));
                    var along = SN.Vector2.Normalize(b - a) * (stroke / 2f);
                    var across = new SN.Vector2(-along.Y, along.X);
                    Prism(o, [a - along - across, b + along - across, b + along + across, a - along + across], c.Z, c.Z + 0.0015f, finish);
                }
            }
        }
    }

    public static void SelfTest(Action<bool, string> check)
    {
        var three = new DateTimeOffset(2026, 10, 29, 15, 0, 0, TimeSpan.Zero);
        var (hour, minute, second) = Angles(three);
        check(MathF.Abs(hour - (MathF.PI / 2f)) < 1e-4f && minute == 0f && second == 0f, "at three o'clock the hour hand points to three, the others to twelve");
        var (_, half, ticking) = Angles(three.AddMinutes(30).AddMilliseconds(50));
        check(MathF.Abs(half - MathF.PI) < 0.001f && ticking > 0f && ticking < MathF.Tau / 60f, "half past, the minute hand points to six; the second hand is leaving twelve");
        var summer = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var winter = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        check(In(summer, "America/Toronto", "Eastern Standard Time").Hour == 8 && In(summer, "Europe/Paris", "Romance Standard Time").Hour == 14
            && In(winter, "America/Toronto", "Eastern Standard Time").Hour == 7 && In(winter, "Europe/Paris", "Romance Standard Time").Hour == 13,
            "Ottawa and Paris keep their own time, summer and winter");
        check(Segments.Select(s => s.Length).SequenceEqual([6, 2, 5, 5, 4, 5, 6, 3, 7, 6]), "each digit lights the segments of a seven-segment display");
        check(Digit("abcdefg", Lit).Length == 7 * 6 * 3 * 3 && Cities.All(c => c.Name.All(Strokes.ContainsKey)), "the display and the names are drawn from what they need");
        check(DisplaySlot(0f, 0).X < DisplaySlot(0f, 6).X && DisplaySlot(0f, 6).X < DisplaySlot(0f, 2).X && DisplaySlot(0f, 7).X > DisplaySlot(0f, 3).X
            && DisplaySlot(0f, 7).X < DisplaySlot(0f, 4).X && MathF.Abs(DisplaySlot(0f, 0).X + DisplaySlot(0f, 5).X) < 1e-5f, "hours, colon, minutes, colon, seconds, centred");
    }
}

// The clocks' meshes, made once, and their drawing at each frame: the bodies still, the hands turned to each city's
// time, and each digit lit over an unlit "8"; the colons blink with the seconds.
internal sealed class WallClockStage : IDisposable
{
    private readonly Mesh bodies;
    private readonly Mesh[] hands;
    private readonly Mesh[] digits;
    private readonly Mesh ghost;
    private readonly Mesh colon;
    private readonly Mesh ghostColon;

    public WallClockStage(Func<Vertex[], Mesh> make)
    {
        bodies = make(WallClocks.Bodies());
        hands = [make(WallClocks.Hand(0)), make(WallClocks.Hand(1)), make(WallClocks.Hand(2))];
        digits = [.. WallClocks.Segments.Select(s => make(WallClocks.Digit(s, WallClocks.Lit)))];
        ghost = make(WallClocks.Digit("abcdefg", WallClocks.Unlit));
        colon = make(WallClocks.Colon(WallClocks.Lit));
        ghostColon = make(WallClocks.Colon(WallClocks.Unlit));
    }

    public bool Shown { get; set; } = true;

    public int Meshes => 3 + hands.Length + digits.Length + 1;

    public void Draw(Action<Mesh, SN.Matrix4x4, SN.Vector4> draw, DateTimeOffset now)
    {
        if (!Shown) return;
        draw(bodies, SN.Matrix4x4.Identity, default);
        foreach (var (_, x, iana, windows) in WallClocks.Cities)
        {
            var local = WallClocks.In(now, iana, windows);
            var (hour, minute, second) = WallClocks.Angles(local);
            var centre = SN.Matrix4x4.CreateTranslation(x, WallClocks.Height, WallClocks.Wall);
            draw(hands[0], SN.Matrix4x4.CreateRotationZ(-hour) * centre, default);
            draw(hands[1], SN.Matrix4x4.CreateRotationZ(-minute) * centre, default);
            draw(hands[2], SN.Matrix4x4.CreateRotationZ(-second) * centre, default);
            var shown = $"{local:HHmmss}";
            for (var slot = 0; slot < 6; slot++)
            {
                var at = SN.Matrix4x4.CreateTranslation(WallClocks.DisplaySlot(x, slot));
                draw(ghost, at, default);
                draw(digits[shown[slot] - '0'], at * SN.Matrix4x4.CreateTranslation(0f, 0f, 0.0004f), WallClocks.Glow);
            }

            for (var slot = 6; slot < 8; slot++)
            {
                var at = SN.Matrix4x4.CreateTranslation(WallClocks.DisplaySlot(x, slot));
                if (local.Millisecond < 500) draw(colon, at * SN.Matrix4x4.CreateTranslation(0f, 0f, 0.0004f), WallClocks.Glow);
                else draw(ghostColon, at, default);
            }
        }
    }

    public void Dispose()
    {
        bodies.Dispose();
        foreach (var mesh in hands) mesh.Dispose();
        foreach (var mesh in digits) mesh.Dispose();
        ghost.Dispose();
        colon.Dispose();
        ghostColon.Dispose();
    }
}
