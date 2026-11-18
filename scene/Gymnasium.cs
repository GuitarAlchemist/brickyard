using SN = System.Numerics;

// The gymnasium: the Brickyard building made much bigger, a wing on the hall's right through a door in its right wall
// (the user, 2026-11-18: « je pense qu'on va avoir besoin d'un batiment beaucoup plus grand, peut etre un gymnase »,
// then « le gymnase je parlais pour etendre le batiment brickyard »). 34 m by 24 m and 9 m high inside, its front flush
// with the hall's on the street: a maple floor in strips on its battens, a basketball court of FIBA's sizes painted on
// it, a hoop at each end on a wall-mounted backstop, bleachers along the front wall, wall pads under the hoops, steel
// trusses across, high-bay lamps under them, clerestory windows and a scoreboard.
//
// As the hall's shell (HallShell), its walls, roof, windows, pads, scoreboard, trusses and lamps are drawn but let the key
// light through (Program.DrawLetThrough), as if the roof were glazed: traced, the trusses and the lamps striped the floor
// with dark, grainy shadows. The floor, the court, the hoops and the bleachers are traced. The Low shader lights the whole wing as one pool (its
// box written there too, checked by SelfTest), and FreeCamera walks into it through the door (Confine's spaces).
internal static class Gymnasium
{
    private const float M = FreeCamera.Scale; // a metre, in the hall's units
    private const float Thick = 0.16f;

    // The walls' inner faces: the left one is the hall's right wall's outer face; the front one is level with the hall's.
    internal const float Left = HallShell.Right + Thick, Right = 28.4f, Back = -9.5f, Front = HallShell.Front;
    internal const float Top = 5.8f; // the roof's underside, 9 m up
    internal const float FloorTop = 0.02f; // the maple on its battens, a little over the hall's slabs

    // The door from the hall in its right wall: its middle's z, its width and its height (HallShell leaves it open).
    internal const float DoorZ = 5.05f, DoorWidth = 1.5f, DoorHeight = 2.6f;

    // The court: its middle, and FIBA's 28 m by 15 m.
    internal static readonly SN.Vector2 Court = new(17.4f, -2.3f);
    private const float HalfLength = 14f * M, HalfWidth = 7.5f * M;
    private const float Stripe = 0.05f * M; // a line's width
    internal const float RimHeight = 3.05f * M;

    // The bleachers along the front wall: from x to x, the first row's edge, four rows.
    private const float StandsFrom = 10.6f, StandsTo = 24.2f, StandsEdge = 3.4f, RowDeep = 0.6f, RowRise = 0.28f;
    private const int Rows = 4;

    // The trusses across the wing, every 3 m or so, and the high-bay lamps under them.
    private static readonly float[] Trusses = [7.4f, 10.4f, 13.4f, 16.4f, 19.4f, 22.4f, 25.4f];
    private static readonly float[] LampRows = [-5.9f, -2.3f, 1.3f];
    private const float TrussLow = 4.95f, TrussHigh = 5.68f;

    // What stands on the floor, as FreeCamera.Obstacles': the bleachers, which the walking body does not climb.
    internal static readonly (SN.Vector2 Centre, float Turn, SN.Vector2 Half)[] Obstacles =
    [
        (new((StandsFrom + StandsTo) / 2, (StandsEdge + StandsEdge + (Rows * RowDeep)) / 2), 0f, new((StandsTo - StandsFrom) / 2, Rows * RowDeep / 2)),
    ];

    // Whether (x, z) is inside the building, the hall or the wing, margin past its walls' inner faces: what the streets
    // leave out (OttawaStreets, GoogleStreets).
    internal static bool Covers(float x, float z, float margin) =>
        (x > HallShell.Left - margin && x < HallShell.Right + margin && z > HallShell.Back - margin && z < HallShell.Front + margin)
        || (x > Left - margin && x < Right + margin && z > Back - margin && z < Front + margin);

    // Whether the eye is in the wing.
    internal static bool Holds(SN.Vector3 p) => p.X > Left && p.X < Right && p.Z > Back && p.Z < Front && p.Y < Top;

    // Shell: walls, roof, window frames, pads, scoreboard, trusses and lamps (let through). Glass and Bulbs glow. Inside is traced.
    public static (Vertex[] Shell, Vertex[] Glass, Vertex[] Bulbs, Vertex[] Inside) Build(bool photographed)
    {
        var (shell, glass, bulbs, inside) = (new List<Vertex>(), new List<Vertex>(), new List<Vertex>(), new List<Vertex>());
        var steel = new Finish(new(0.11f, 0.12f, 0.13f), Material.Steel);
        var paint = photographed ? new Finish(new(0.75f), Material.PaintedSteel) : new Finish(new(0.16f, 0.27f, 0.23f), Material.Enamel);
        var pane = new Finish(new(0.55f, 0.62f, 0.7f), Material.Glass);

        Walls(shell);
        Windows(shell, glass, steel, pane);
        Pads(shell);
        Scoreboard(shell, bulbs);
        Floor(inside);
        Lines(inside);
        foreach (var side in new[] { -1f, 1f })
        {
            Hoop(inside, side, paint);
        }

        Bleachers(inside, paint);
        TrussesAndLamps(shell, bulbs, paint, steel);
        return ([.. shell], [.. glass], [.. bulbs], [.. inside]);
    }

    // The walls the hall does not already give, and the roof. The hall's right wall (HallShell, KnotFloor's end wall)
    // is the wing's left one up to the hall's ceiling; over it, and behind the hall, the wing's own.
    // Its own walls are painted concrete block (Material.Block), as a school's gymnasium is (the user, 2026-11-18: « le
    // rendu du gymnase est pas terrible »), and the hall's wall is lined with it on the wing's side; the roof's
    // underside is a painted steel deck.
    private static void Walls(List<Vertex> o)
    {
        var concrete = new Finish(new(0.8f, 0.78f, 0.72f), Material.Block);
        var x = HallShell.Right + (Thick / 2);
        var ceiling = HallShell.Top + Thick;
        // The lining over the hall's wall, a centimetre thick, round the door.
        var (lining, liningX) = (0.01f, HallShell.Right + Thick + 0.005f);
        var (doorFrom, doorTo) = (DoorZ - (DoorWidth / 2), DoorZ + (DoorWidth / 2));
        Boxes.Add(o, new(liningX, ceiling / 2, (HallShell.Back + doorFrom) / 2), new(lining, ceiling, doorFrom - HallShell.Back), concrete);
        Boxes.Add(o, new(liningX, ceiling / 2, (doorTo + Front) / 2), new(lining, ceiling, Front - doorTo), concrete);
        Boxes.Add(o, new(liningX, (DoorHeight + ceiling) / 2, DoorZ), new(lining, ceiling - DoorHeight, DoorWidth), concrete);
        Boxes.Add(o, new(x, (ceiling + Top) / 2, (HallShell.Back + Front + Thick) / 2), new(Thick, Top - ceiling, Front + Thick - HallShell.Back), concrete);
        Boxes.Add(o, new(x, Top / 2, (Back - Thick + HallShell.Back) / 2), new(Thick, Top, HallShell.Back - Back + Thick), concrete);
        Boxes.Add(o, new((HallShell.Right + Right + Thick) / 2, Top / 2, Back - (Thick / 2)), new(Right + Thick - HallShell.Right, Top, Thick), concrete);
        Boxes.Add(o, new(Right + (Thick / 2), Top / 2, (Back + Front) / 2), new(Thick, Top, Front - Back + (2 * Thick)), concrete);
        Boxes.Add(o, new((Left + Right + Thick) / 2, Top / 2, Front + (Thick / 2)), new(Right + Thick - Left, Top, Thick), concrete);
        Boxes.Add(o, new((HallShell.Right + Right + Thick) / 2, Top + (Thick / 2), (Back + Front) / 2), new(Right + Thick - HallShell.Right, Thick, Front - Back + (2 * Thick)), new Finish(new(0.46f, 0.47f, 0.48f), Material.Matte));
    }

    // Clerestories high in the back wall and the front one, and two in the right wall on either side of the scoreboard,
    // inside and out.
    private static void Windows(List<Vertex> o, List<Vertex> glass, Finish steel, Finish pane)
    {
        foreach (var x in new[] { 9.0f, 13.2f, 17.4f, 21.6f, 25.8f })
        {
            HallShell.Window(o, glass, new(x, 4.4f, Back), SN.Vector3.UnitZ, SN.Vector3.UnitX, 3.0f, 1.8f, steel, pane);
            HallShell.Window(o, glass, new(x, 4.4f, Front), -SN.Vector3.UnitZ, SN.Vector3.UnitX, 3.0f, 1.8f, steel, pane);
            // And their outer faces, so that the street and the air see the windows too.
            HallShell.Window(o, glass, new(x, 4.4f, Back - Thick), -SN.Vector3.UnitZ, SN.Vector3.UnitX, 3.0f, 1.8f, steel, pane);
            HallShell.Window(o, glass, new(x, 4.4f, Front + Thick), SN.Vector3.UnitZ, SN.Vector3.UnitX, 3.0f, 1.8f, steel, pane);
        }

        foreach (var z in new[] { -7.2f, 2.6f })
        {
            HallShell.Window(o, glass, new(Right, 4.4f, z), -SN.Vector3.UnitX, -SN.Vector3.UnitZ, 2.6f, 1.8f, steel, pane);            HallShell.Window(o, glass, new(Right + Thick, 4.4f, z), SN.Vector3.UnitX, SN.Vector3.UnitZ, 2.6f, 1.8f, steel, pane);

        }
    }

    // Blue pads on the end walls behind the hoops, 2 m high.
    private static void Pads(List<Vertex> o)
    {
        var pad = new Finish(new(0.07f, 0.13f, 0.34f), Material.Rubber);
        foreach (var (x, n) in new[] { (Left, 1f), (Right, -1f) })
        {
            for (var k = -3; k < 3; k++)
            {
                // Panels 1 m wide, a finger's gap between them.
                Boxes.Add(o, new(x + (n * 0.03f), 0.15f + (1f * M), Court.Y + ((k + 0.5f) * M)), new(0.06f, 2f * M, M - 0.01f), pad);
            }
        }
    }

    // The scoreboard high on the right wall over the hoop: a black case, "00" and "00" in amber segments either side of
    // a colon.
    private static void Scoreboard(List<Vertex> o, List<Vertex> lit)
    {
        var face = Right - 0.12f;
        Boxes.Add(o, new(Right - 0.06f, 4.6f, Court.Y), new(0.12f, 1.2f, 3.2f), new Finish(new(0.02f), Material.Enamel));
        foreach (var z in new[] { -1.15f, -0.75f, 0.75f, 1.15f })
        {
            Digit(lit, new(face, 4.6f, Court.Y + z), 0.55f);
        }

        foreach (var y in new[] { 4.48f, 4.72f })
        {
            Boxes.Add(lit, new(face - 0.005f, y, Court.Y), new(0.01f, 0.06f, 0.06f), new Finish(new(1f, 0.6f, 0.2f), Material.Matte));
        }
    }

    // A seven-segment nought facing -x, centred at c, h high.
    private static void Digit(List<Vertex> o, SN.Vector3 c, float h)
    {
        var (w, t) = (h * 0.55f, h * 0.1f);
        var amber = new Finish(new(1f, 0.6f, 0.2f), Material.Matte);
        foreach (var y in new[] { -1f, 1f })
        {
            Boxes.Add(o, new(c.X - 0.005f, c.Y + (y * ((h / 2) - (t / 2))), c.Z), new(0.01f, t, w - (2 * t)), amber); // top and bottom
            foreach (var z in new[] { -1f, 1f })
            {
                Boxes.Add(o, new(c.X - 0.005f, c.Y + (y * h / 4), c.Z + (z * ((w / 2) - (t / 2)))), new(0.01f, (h / 2) - (1.5f * t), t), amber);
            }
        }
    }

    // The floor: maple strips 12 cm wide along the court, of lengths that vary, butted with no gap (a dark joint under a
    // pixel made moiré far off), the keys stained blue, their strips cut at the keys' ends; a dark bed under them.
    private static void Floor(List<Vertex> o)
    {
        Boxes.Add(o, new((HallShell.Right + Right) / 2, -0.02f, (Back + Front) / 2), new(Right - HallShell.Right, 0.06f, Front - Back), new SN.Vector3(0.02f, 0.015f, 0.01f));
        const float strip = 0.12f;
        var keyHalf = 2.45f * M;
        var cuts = new List<float>();
        foreach (var s in new[] { -1f, 1f })
        {
            var baseline = Court.X + (s * HalfLength);
            cuts.Add(baseline);
            cuts.Add(baseline - (s * 5.8f * M));
        }

        var row = 0;
        for (var z = Back; z < Front - 1e-3f; z += strip, row++)
        {
            var z1 = MathF.Min(z + strip, Front);
            var mid = (z + z1) / 2;
            var inKeys = MathF.Abs(mid - Court.Y) < keyHalf;
            var ends = new List<float> { Left, Right };
            for (float x = Left, k = 0; x < Right; k++)
            {
                x += 1.2f + (1.2f * Hash(row, (int)k));
                if (x < Right - 0.3f) ends.Add(x);
            }

            if (inKeys) ends.AddRange(cuts);
            ends.Sort();
            for (var i = 0; i + 1 < ends.Count; i++)
            {
                var (a, b) = (ends[i], ends[i + 1]);
                if (b - a < 0.02f) continue;
                var centre = (a + b) / 2;
                var keyed = inKeys && (Between(centre, cuts[0], cuts[1]) || Between(centre, cuts[2], cuts[3]));
                var tone = 0.96f + (0.07f * Hash(row * 31, i)); // a strip's tone: maple sanded and sealed is even
                var colour = keyed ? new SN.Vector3(0.12f, 0.2f, 0.42f) * tone : new SN.Vector3(0.78f, 0.6f, 0.4f) * tone;
                Boxes.Add(o, new(centre, FloorTop - 0.02f, mid), new(b - a, 0.04f, z1 - z), new Finish(colour, Material.Oak));
            }
        }

        // An aluminium threshold in the door, over the step from the hall's slabs to the maple.
        Boxes.Add(o, new(HallShell.Right + 0.15f, 0.014f, DoorZ), new(0.36f, 0.028f, DoorWidth), new Finish(new(0.6f, 0.62f, 0.64f), Material.Aluminium));
    }

    private static bool Between(float v, float a, float b) => v > MathF.Min(a, b) && v < MathF.Max(a, b);

    // The court's lines in white, a hair over the maple: the boundary, the centre line and circle, and at each end the
    // key, its free-throw circle and the three-point line, FIBA's.
    private static void Lines(List<Vertex> o)
    {
        var white = new Finish(new(0.92f), Material.Enamel);
        var y = FloorTop + 0.004f;
        void Segment(SN.Vector2 a, SN.Vector2 b) => Ribbon(o, [a, b], y, white);
        void Arc(SN.Vector2 c, float r, float from, float to, float s)
        {
            var points = new SN.Vector2[33];
            for (var i = 0; i <= 32; i++)
            {
                var a = from + ((to - from) * i / 32f);
                points[i] = c + new SN.Vector2(-s * r * MathF.Cos(a), r * MathF.Sin(a));
            }

            Ribbon(o, points, y, white);
        }

        var (cx, cz) = (Court.X, Court.Y);
        var (hl, hw) = (HalfLength, HalfWidth);
        Segment(new(cx - hl, cz - hw), new(cx + hl, cz - hw));
        Segment(new(cx - hl, cz + hw), new(cx + hl, cz + hw));
        Segment(new(cx, cz - hw), new(cx, cz + hw));
        Arc(Court, 1.8f * M, 0f, MathF.Tau, 1f);
        foreach (var s in new[] { -1f, 1f })
        {
            var baseline = cx + (s * hl);
            Segment(new(baseline, cz - hw), new(baseline, cz + hw));
            var freeThrow = baseline - (s * 5.8f * M);
            var keyHalf = 2.45f * M;
            Segment(new(baseline, cz - keyHalf), new(freeThrow, cz - keyHalf));
            Segment(new(baseline, cz + keyHalf), new(freeThrow, cz + keyHalf));
            Segment(new(freeThrow, cz - keyHalf), new(freeThrow, cz + keyHalf));
            Arc(new(freeThrow, cz), 1.8f * M, -MathF.PI / 2, MathF.PI / 2, s);
            var basket = baseline - (s * 1.575f * M);
            var reach = MathF.Asin(6.6f / 6.75f);
            var meet = basket - (s * MathF.Cos(reach) * 6.75f * M);
            Segment(new(baseline, cz - (6.6f * M)), new(meet, cz - (6.6f * M)));
            Segment(new(baseline, cz + (6.6f * M)), new(meet, cz + (6.6f * M)));
            Arc(new(basket, cz), 6.75f * M, -reach, reach, s);
        }
    }

    // A flat white stripe Stripe wide along a path on the floor at height y, facing up.
    private static void Ribbon(List<Vertex> o, SN.Vector2[] path, float y, Finish finish)
    {
        for (var i = 0; i + 1 < path.Length; i++)
        {
            var d = path[i + 1] - path[i];
            if (d.LengthSquared() < 1e-10f) continue;
            var across = SN.Vector2.Normalize(new SN.Vector2(-d.Y, d.X)) * (Stripe / 2);
            // A little past each end, so that the joints of an arc and the corners close.
            var along = SN.Vector2.Normalize(d) * (Stripe / 2);
            var (a, b) = (path[i] - along, path[i + 1] + along);
            SN.Vector3 P(SN.Vector2 p) => new(p.X, y, p.Y);
            Quad(o, P(a - across), P(b - across), P(b + across), P(a + across), SN.Vector3.UnitY, SN.Vector3.Normalize(new SN.Vector3(d.X, 0f, d.Y)), finish);
        }
    }

    // A hoop at one end (side -1 the left, +1 the right): a glass backboard, its white border and target square, an
    // orange ring on its bracket, a white net, and the backstop's arms from the end wall.
    private static void Hoop(List<Vertex> o, float side, Finish paint)
    {
        var baseline = Court.X + (side * HalfLength);
        var board = baseline - (side * 1.2f * M);
        var (low, high) = (2.9f * M, 3.95f * M);
        var half = 0.9f * M;
        Boxes.Add(o, new(board, (low + high) / 2, Court.Y), new(0.02f, high - low, 2 * half), new Finish(new(0.75f, 0.82f, 0.86f), Material.Glass));
        var white = new Finish(new(0.95f), Material.Enamel);
        var face = board - (side * 0.012f);
        var line = 0.05f * M;
        void Frame(float y0, float y1, float zHalf)
        {
            Boxes.Add(o, new(face, y1 - (line / 2), Court.Y), new(0.004f, line, 2 * zHalf), white);
            Boxes.Add(o, new(face, y0 + (line / 2), Court.Y), new(0.004f, line, 2 * zHalf), white);
            foreach (var z in new[] { -1f, 1f })
            {
                Boxes.Add(o, new(face, (y0 + y1) / 2, Court.Y + (z * (zHalf - (line / 2)))), new(0.004f, y1 - y0, line), white);
            }
        }

        Frame(low, high, half);
        Frame(RimHeight + (0.15f * M), RimHeight + (0.6f * M), 0.295f * M);

        // The ring, 45 cm across, its middle 1.575 m in from the baseline, on a bracket from the board.
        var rim = new SN.Vector3(baseline - (side * 1.575f * M), RimHeight, Court.Y);
        var radius = 0.225f * M;
        var orange = new Finish(new(0.85f, 0.3f, 0.06f), Material.Enamel);
        var netColour = new Finish(new(0.9f), Material.Rope);
        const int around = 24;
        SN.Vector3 At(float r, int i, float drop) => rim + new SN.Vector3(r * MathF.Cos(i * MathF.Tau / around), -drop, r * MathF.Sin(i * MathF.Tau / around));
        for (var i = 0; i < around; i++)
        {
            Tube(o, At(radius, i, 0f), At(radius, i + 1, 0f), 0.009f, 6, orange);
        }

        Boxes.Add(o, new((board + rim.X + (side * radius)) / 2, RimHeight - 0.02f, Court.Y), new(MathF.Abs(rim.X + (side * radius) - board), 0.05f, 0.1f), orange);
        // The net: twelve cords down and across to a narrower ring 40 cm below.
        for (var i = 0; i < around; i += 2)
        {
            Tube(o, At(radius, i, 0f), At(radius * 0.6f, i + 2, 0.4f * M), 0.003f, 4, netColour);
            Tube(o, At(radius, i, 0f), At(radius * 0.6f, i - 2, 0.4f * M), 0.003f, 4, netColour);
            Tube(o, At(radius * 0.6f, i, 0.4f * M), At(radius * 0.6f, i + 2, 0.4f * M), 0.003f, 4, netColour);
        }

        // The backstop: two arms from the end wall to the board's back, and two braces from higher on the wall.
        var wall = side < 0 ? Left : Right;
        var back = board + (side * 0.03f);
        foreach (var z in new[] { -0.45f, 0.45f })
        {
            Tube(o, new(wall, high - 0.1f, Court.Y + z), new(back, high - 0.1f, Court.Y + z), 0.04f, 8, paint);
            Tube(o, new(wall, high + 1.3f, Court.Y + z), new(back, high - 0.08f, Court.Y + z), 0.03f, 8, paint);
            Tube(o, new(back, low + 0.05f, Court.Y + z), new(back, high - 0.05f, Court.Y + z), 0.03f, 8, paint);
            Boxes.Add(o, new(wall + (side * -0.03f), high + 0.55f, Court.Y + z), new(0.06f, 1.7f, 0.14f), paint); // the plate on the wall
        }
    }

    // The bleachers: four tiers of painted steel rising to the front wall, an oak seat plank on each, and a rail at
    // each end.
    private static void Bleachers(List<Vertex> o, Finish paint)
    {
        var oak = new Finish(new(0.62f, 0.45f, 0.28f), Material.Oak);
        for (var r = 0; r < Rows; r++)
        {
            var (z0, top) = (StandsEdge + (r * RowDeep), (r + 1) * RowRise);
            Boxes.Add(o, new((StandsFrom + StandsTo) / 2, top / 2, z0 + (RowDeep / 2)), new(StandsTo - StandsFrom, top, RowDeep), paint);
            Boxes.Add(o, new((StandsFrom + StandsTo) / 2, top + 0.025f, z0 + 0.16f), new(StandsTo - StandsFrom - 0.1f, 0.05f, 0.26f), oak);
        }

        foreach (var x in new[] { StandsFrom - 0.03f, StandsTo + 0.03f })
        {
            var (z0, z1) = (StandsEdge, StandsEdge + (Rows * RowDeep));
            Tube(o, new(x, RowRise + 0.6f, z0), new(x, (Rows * RowRise) + 0.6f, z1 - 0.1f), 0.025f, 8, paint);
            Tube(o, new(x, 0f, z0), new(x, RowRise + 0.6f, z0), 0.025f, 8, paint);
        }
    }

    // Steel trusses across the wing from the back wall to the front, a Warren of diagonals between their chords, purlins
    // along them under the roof, and under each truss three high-bay lamps.
    private static void TrussesAndLamps(List<Vertex> o, List<Vertex> bulbs, Finish paint, Finish steel)
    {
        foreach (var x in Trusses)
        {
            Tube(o, new(x, TrussHigh, Back), new(x, TrussHigh, Front), 0.05f, 8, paint);
            Tube(o, new(x, TrussLow, Back + 0.5f), new(x, TrussLow, Front - 0.5f), 0.045f, 8, paint);
            var panels = (int)MathF.Round((Front - Back - 1f) / 1f);
            var step = (Front - Back - 1f) / panels;
            Tube(o, new(x, TrussHigh, Back), new(x, TrussLow, Back + 0.5f), 0.03f, 6, paint);
            Tube(o, new(x, TrussHigh, Front), new(x, TrussLow, Front - 0.5f), 0.03f, 6, paint);
            for (var i = 0; i < panels; i++)
            {
                var (za, zb) = (Back + 0.5f + (i * step), Back + 0.5f + ((i + 1) * step));
                var (ya, yb) = i % 2 == 0 ? (TrussLow, TrussHigh) : (TrussHigh, TrussLow);
                Tube(o, new(x, ya, za), new(x, yb, zb), 0.025f, 6, paint);
            }

            foreach (var z in LampRows)
            {
                var rim = new SN.Vector3(x, 4.5f, z);
                Tube(o, rim + new SN.Vector3(0f, 0.35f, 0f), new(x, TrussLow, z), 0.008f, 4, steel);
                HallShell.Lathe(o, rim, [(0.06f, 0.35f), (0.1f, 0.28f), (0.3f, 0.06f), (0.32f, 0f)], new Finish(new(0.62f, 0.64f, 0.66f), Material.Aluminium));
                HallShell.Lathe(bulbs, rim, [(0.0f, 0.12f), (0.16f, 0.1f), (0.2f, 0.04f), (0.0f, 0.02f)], new Finish(new(1f, 0.95f, 0.85f), Material.Matte));
            }
        }

        for (var z = Back + 1f; z < Front; z += 2f)
        {
            Tube(o, new(Left, Top - 0.06f, z), new(Right, Top - 0.06f, z), 0.04f, 6, paint);
        }
    }

    // A closed tube from a to b, radius r, as Rides' cylinders.
    private static void Tube(List<Vertex> o, SN.Vector3 a, SN.Vector3 b, float r, int sides, Finish finish)
    {
        var length = SN.Vector3.Distance(a, b);
        if (length < 1e-5f) return;
        var axis = (b - a) / length;
        var u = MathF.Abs(axis.Y) > 0.9f ? SN.Vector3.UnitX : SN.Vector3.UnitY;
        var w = SN.Vector3.Normalize(SN.Vector3.Cross(axis, u));
        u = SN.Vector3.Cross(w, axis);
        for (var i = 0; i < sides; i++)
        {
            var (a0, a1) = (MathF.Tau * i / sides, MathF.Tau * (i + 1) / sides);
            var (r0, r1) = ((u * MathF.Cos(a0)) + (w * MathF.Sin(a0)), (u * MathF.Cos(a1)) + (w * MathF.Sin(a1)));
            var n = SN.Vector3.Normalize(r0 + r1);
            Quad(o, a + (r0 * r), a + (r1 * r), b + (r1 * r), b + (r0 * r), n, axis, finish);
        }
    }

    // A flat quad p0 p1 p2 p3 facing n, its two triangles wound as Boxes' are (their cross product along n).
    private static void Quad(List<Vertex> o, SN.Vector3 p0, SN.Vector3 p1, SN.Vector3 p2, SN.Vector3 p3, SN.Vector3 n, SN.Vector3 tangent, Finish finish)
    {
        var flat = new SN.Vector4(0f, 0f, 1f, 1f); // no rounded edge
        Vertex V(SN.Vector3 p) => new(p, n, finish.Color, finish.Material, tangent, flat);
        if (SN.Vector3.Dot(SN.Vector3.Cross(p1 - p0, p2 - p0), n) >= 0f)
        {
            o.AddRange([V(p0), V(p1), V(p2), V(p0), V(p2), V(p3)]);
        }
        else
        {
            o.AddRange([V(p0), V(p2), V(p1), V(p0), V(p3), V(p2)]);
        }
    }

    private static float Hash(int i, int j)
    {
        var h = (uint)((i * 73856093) ^ (j * 19349663));
        h ^= h >> 13;
        h *= 0x5bd1e995;
        h ^= h >> 15;
        return (h & 0xffff) / 65535f;
    }

    public static void SelfTest(Action<bool, string> check, string lowShader)
    {
        var (shell, glass, bulbs, inside) = Build(photographed: false);
        var all = shell.Concat(glass).Concat(bulbs).Concat(inside).ToArray();
        check(all.All(v => float.IsFinite(v.Pos.X + v.Pos.Y + v.Pos.Z) && MathF.Abs(v.Normal.Length() - 1f) < 1e-3f) && glass.Length > 0 && bulbs.Length > 0,
            $"the gymnasium's walls, windows, lamps and floor are made ({all.Length} vertices)");
        check(all.Length < 400_000, $"the gymnasium stays under 400,000 vertices ({all.Length})");

        // Its sizes, in metres: 34 m by 24 m, 9 m high, the court FIBA's within it, the rim at 3.05 m.
        var (long_, deep, high) = ((Right - Left) / M, (Front - Back) / M, Top / M);
        check(long_ > 33f && deep > 23f && high > 8.9f, $"the wing is {long_:F0} m by {deep:F0} m and {high:F1} m high inside");
        check(Court.X - HalfLength > Left + (1.5f * M) && Court.X + HalfLength < Right - (1.5f * M) && Court.Y - HalfWidth > Back + (1.5f * M),
            "the court has room round it, past its lines");
        check(StandsEdge > Court.Y + HalfWidth + (1f * M) && StandsEdge + (Rows * RowDeep) < Front,
            "the bleachers stand off the court's sideline, against the front wall");
        check(MathF.Abs((RimHeight / M) - 3.05f) < 1e-3f, "the rims are 3.05 m up");

        // Nothing of the walls stands in the door from the hall, from either side.
        var (z0, z1) = (DoorZ - (DoorWidth / 2), DoorZ + (DoorWidth / 2));
        bool InDoor(Vertex[] mesh)
        {
            for (var i = 0; i + 2 < mesh.Length; i += 3)
            {
                var c = (mesh[i].Pos + mesh[i + 1].Pos + mesh[i + 2].Pos) / 3f;
                if (c.X > HallShell.Right - 0.01f && c.X < Left + 0.01f && c.Z > z0 + 0.01f && c.Z < z1 - 0.01f && c.Y > 0.05f && c.Y < DoorHeight - 0.01f) return true;
            }

            return false;
        }

        check(!InDoor(shell) && !InDoor(HallShell.Build(photographed: false).Shell), "the door from the hall to the gymnasium is open");

        // Walking in from the hall: from beside the door, straight on at a sprint, through it and onto the court.
        var camera = new FreeCamera();
        camera.GoTo(new(5.4f, FreeCamera.WalkHeight, DoorZ), new(30f, FreeCamera.WalkHeight, DoorZ), cut: true);
        for (var i = 0; i < 300; i++) camera.Move(i / 60f, new(1, 0, 0, true), 1f / 60f);
        var eye = camera.View(5f).Eye;
        check(eye.X > Left + 3f && Holds(eye), $"one walks from the hall through the door into the gymnasium ({eye.X:F2}, {eye.Z:F2})");

        // Beside the door, the wall still stops the body.
        camera = new FreeCamera();
        camera.GoTo(new(5.4f, FreeCamera.WalkHeight, 0f), new(30f, FreeCamera.WalkHeight, 0f), cut: true);
        for (var i = 0; i < 300; i++) camera.Move(i / 60f, new(1, 0, 0, true), 1f / 60f);
        eye = camera.View(5f).Eye;
        check(eye.X <= FreeCamera.Max.X + 1e-3f, $"away from the door the hall's right wall stops the body (x {eye.X:F2})");

        // Back into the hall, from between the bleachers' end and the door.
        camera = new FreeCamera();
        camera.GoTo(new(StandsFrom - 0.5f, FreeCamera.WalkHeight, DoorZ), new(0f, FreeCamera.WalkHeight, DoorZ), cut: true);
        for (var i = 0; i < 600; i++) camera.Move(i / 60f, new(1, 0, 0, true), 1f / 60f);
        eye = camera.View(10f).Eye;
        check(eye.X < HallShell.Right, $"and back into the hall ({eye.X:F2}, {eye.Z:F2})");

        // The bleachers stop it: walking at them along the front wall from the far end.
        camera = new FreeCamera();
        camera.GoTo(new(27f, FreeCamera.WalkHeight, DoorZ), new(0f, FreeCamera.WalkHeight, DoorZ), cut: true);
        for (var i = 0; i < 300; i++) camera.Move(i / 60f, new(1, 0, 0, true), 1f / 60f);
        eye = camera.View(5f).Eye;
        check(eye.X > StandsTo + 0.15f, $"the bleachers stop the walking body (x {eye.X:F2})");
        check(lowShader.Contains("vec2(17.405, -1.75), vec2(10.995, 7.75)", StringComparison.Ordinal)
            && MathF.Abs(((Left + Right) / 2) - 17.405f) < 1e-3f && MathF.Abs(((Back + Front) / 2) + 1.75f) < 1e-3f
            && MathF.Abs(((Right - Left) / 2) - 10.995f) < 1e-3f && MathF.Abs(((Front - Back) / 2) - 7.75f) < 1e-3f,
            "the Low shader lights the gymnasium's box as one pool");
    }
}
