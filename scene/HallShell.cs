using SN = System.Numerics;

// The hall closed round: the left wall and the front one, the right wall to the front, the ceiling on steel beams,
// steel-framed windows of many small panes letting in a pale daylight, a roll-up door at the front, and pendant lamps
// over the floor and the knots' floor ("il manque des murs, fenêtres, plafond, lampes"). The hall was built open on the
// left, the front and above, its light a key light from high on the left whose shadows the GPU traces (RayScene): the
// shell is drawn but kept out of the traced structure (Program.DrawUntraced), as if the roof were glazed, so that the
// hall keeps its light and its shadows. The windows' glass and the lamps' bulbs glow (drawn with Push.Glow); each lamp
// also lights a pool on the floor below it in the Low shader (its xz written there too, checked by SelfTest).
internal static class HallShell
{
    internal const float Top = 4.85f; // the ceiling's underside, level with the beam over the pillars
    // The walls' inner faces: the right one is the knots' floor's end wall (KnotFloor), the back one the brick wall's.
    internal const float Left = -6.25f, Right = 6.25f, Back = -3.45f, Front = 6.0f;
    private const float Thick = 0.16f;

    // The roll-up door in the front wall: its middle's x, its width and its height (StreetDoor).
    internal const float DoorX = -2.6f, DoorWidth = 3.6f, DoorHeight = 3.6f;

    // The lamps over the hall's floor, each hanging over (x, z); the Low shader's pools are at the same places.
    internal static readonly SN.Vector2[] FloorLamps = [new(-3.2f, -0.6f), new(0f, 1.6f), new(3.0f, -0.6f), new(-3.2f, 3.4f)];

    // Over the knots' floor's walkway, lit by its own pool (KnotFloor).
    private static readonly SN.Vector2[] UpperLamps = [new(5.07f, -2.3f), new(5.07f, -0.2f)];

    public static (Vertex[] Shell, Vertex[] Glass, Vertex[] Bulbs) Build(bool photographed)
    {
        var (o, glass, bulbs) = (new List<Vertex>(), new List<Vertex>(), new List<Vertex>());
        var concrete = photographed ? new Finish(new(0.9f), Material.ConcreteCast) : new Finish(new(0.38f, 0.37f, 0.35f), Material.Matte);
        var ceiling = photographed ? new Finish(new(0.7f), Material.ConcreteCast) : new Finish(new(0.3f, 0.29f, 0.28f), Material.Matte);
        var steel = new Finish(new(0.11f, 0.12f, 0.13f), Material.Steel);
        var paint = photographed ? new Finish(new(0.75f), Material.PaintedSteel) : new Finish(new(0.16f, 0.27f, 0.23f), Material.Enamel);
        var pane = new Finish(new(0.55f, 0.62f, 0.7f), Material.Glass);

        // The walls, each a slab of the cast concrete from the floor to the ceiling, and the strips that carry the back
        // wall and the right one (4.6 m high) up to it.
        Boxes.Add(o, new(Left - (Thick / 2), Top / 2, (Back + Front) / 2), new(Thick, Top, Front - Back + Thick), concrete);
        // The front wall round the door's opening (StreetDoor): on its left, on its right, and over it.
        var (doorLeft, doorRight) = (DoorX - (DoorWidth / 2), DoorX + (DoorWidth / 2));
        Boxes.Add(o, new((Left - Thick + doorLeft) / 2, Top / 2, Front + (Thick / 2)), new(doorLeft - Left + Thick, Top, Thick), concrete);
        Boxes.Add(o, new((doorRight + Right + Thick) / 2, Top / 2, Front + (Thick / 2)), new(Right + Thick - doorRight, Top, Thick), concrete);
        Boxes.Add(o, new(DoorX, (DoorHeight + 0.05f + Top) / 2, Front + (Thick / 2)), new(DoorWidth, Top - DoorHeight - 0.05f, Thick), concrete);
        // The right wall to the front, round the door into the gymnasium (Gymnasium): before it, after it, and over it.
        var (gymFrom, gymTo) = (Gymnasium.DoorZ - (Gymnasium.DoorWidth / 2), Gymnasium.DoorZ + (Gymnasium.DoorWidth / 2));
        Boxes.Add(o, new(Right + (Thick / 2), Top / 2, (4.15f + gymFrom) / 2), new(Thick, Top, gymFrom - 4.15f), concrete);
        Boxes.Add(o, new(Right + (Thick / 2), Top / 2, (gymTo + Front) / 2), new(Thick, Top, Front - gymTo), concrete);
        Boxes.Add(o, new(Right + (Thick / 2), (Gymnasium.DoorHeight + Top) / 2, Gymnasium.DoorZ), new(Thick, Top - Gymnasium.DoorHeight, Gymnasium.DoorWidth), concrete);
        // Its steel frame, on both faces of the wall.
        foreach (var x in new[] { Right - 0.03f, Right + Thick + 0.03f })
        {
            foreach (var z in new[] { gymFrom - 0.05f, gymTo + 0.05f })
            {
                Boxes.Add(o, new(x, Gymnasium.DoorHeight / 2, z), new(0.06f, Gymnasium.DoorHeight + 0.1f, 0.1f), paint);
            }

            Boxes.Add(o, new(x, Gymnasium.DoorHeight + 0.05f, Gymnasium.DoorZ), new(0.06f, 0.1f, Gymnasium.DoorWidth + 0.2f), paint);
        }

        Boxes.Add(o, new(0f, (4.6f + Top) / 2, Back), new(Right - Left, Top - 4.6f, 0.3f), concrete);
        Boxes.Add(o, new(Right + (Thick / 2), (4.6f + Top) / 2, (Back + 4.15f) / 2), new(Thick, Top - 4.6f, 4.15f - Back), concrete);

        // The ceiling, and the steel beams that carry it across the hall every two metres.
        Boxes.Add(o, new(0f, Top + 0.08f, (Back + Front) / 2), new(Right - Left + (2 * Thick), 0.16f, Front - Back + Thick), ceiling);
        for (var z = -1.2f; z < Front; z += 2f)
        {
            Boxes.Add(o, new(0f, Top - 0.115f, z), new(Right - Left, 0.23f, 0.12f), paint);
            Boxes.Add(o, new(0f, Top - 0.225f, z), new(Right - Left, 0.012f, 0.2f), paint); // its lower flange
        }

        // Windows: in the left wall three clerestories over the gallery (HallGallery); in the right wall two over the
        // knots' floor and the stair; in the front wall two beside the door.
        foreach (var z in new[] { -1.6f, 1.4f, 4.4f })
        {
            Window(o, glass, new(Left, 3.7f, z), SN.Vector3.UnitX, SN.Vector3.UnitZ, 2.2f, 1.2f, steel, pane);
        }

        // The right wall's two give onto the gymnasium (Gymnasium) since it was built: their panes are frosted, lit by the
        // hall like the wall and glowing with no daylight, so that nothing seems to show through them (the user,
        // 2026-11-18: « je veux que la fenêtre sur le gymnase ne soit pas transparente »).
        var frosted = new Finish(new(0.5f, 0.52f, 0.53f), Material.Matte);
        Window(o, o, new(Right, 3.75f, -1.4f), -SN.Vector3.UnitX, -SN.Vector3.UnitZ, 2.4f, 1.6f, steel, frosted);
        Window(o, o, new(Right, 3.75f, 2.6f), -SN.Vector3.UnitX, -SN.Vector3.UnitZ, 2.0f, 1.6f, steel, frosted);
        foreach (var x in new[] { 1.6f, 4.4f })
        {
            Window(o, glass, new(x, 2.6f, Front), -SN.Vector3.UnitZ, SN.Vector3.UnitX, 1.8f, 2.4f, steel, pane);
        }

        // The roll-up door's frame at the front; its steel curtain is StreetDoor's, drawn where it has rolled to.
        var (doorX, doorW, doorH) = (DoorX, DoorWidth, DoorHeight);
        foreach (var side in new[] { -1f, 1f })
        {
            Boxes.Add(o, new(doorX + (side * ((doorW / 2) + 0.06f)), doorH / 2, Front - 0.06f), new(0.12f, doorH + 0.1f, 0.12f), paint);
        }

        Boxes.Add(o, new(doorX, doorH + 0.2f, Front - 0.12f), new(doorW + 0.24f, 0.4f, 0.24f), paint); // the curtain's box

        // The lamps: an enamel shade on a cable from the ceiling, its bulb glowing under it.
        foreach (var (at, low) in FloorLamps.Select(p => (p, 3.95f)).Concat(UpperLamps.Select(p => (p, 4.3f))))
        {
            Lamp(o, bulbs, new(at.X, low, at.Y), paint, steel);
        }

        return ([.. o], [.. glass], [.. bulbs]);
    }

    // A steel window of small panes, its face on the wall's inner side at centre c, facing in (normal n), u along the
    // wall: a frame, mullions every 40 cm or so across and up, and a pane of glass in each.
    internal static void Window(List<Vertex> o, List<Vertex> glass, SN.Vector3 c, SN.Vector3 n, SN.Vector3 u, float width, float height, Finish steel, Finish pane)
    {
        var up = SN.Vector3.UnitY;
        var face = c + (n * 0.02f);
        SN.Vector3 Size(float along, float high, float deep) => (SN.Vector3.Abs(u) * along) + (up * high) + (SN.Vector3.Abs(n) * deep);
        var (across, rows) = (Math.Max(2, (int)MathF.Round(width / 0.4f)), Math.Max(2, (int)MathF.Round(height / 0.4f)));
        Boxes.Add(glass, face - (n * 0.012f), Size(width, height, 0.006f), pane);
        Boxes.Add(o, face + (up * ((height / 2) + 0.06f)), Size(width + 0.24f, 0.12f, 0.06f), steel);
        Boxes.Add(o, face - (up * ((height / 2) + 0.07f)), Size(width + 0.24f, 0.14f, 0.12f), steel); // the sill
        foreach (var side in new[] { -1f, 1f })
        {
            Boxes.Add(o, face + (u * side * ((width / 2) + 0.06f)), Size(0.12f, height + 0.24f, 0.06f), steel);
        }

        for (var i = 1; i < across; i++)
        {
            Boxes.Add(o, face + (u * ((i * width / across) - (width / 2))), Size(0.03f, height, 0.04f), steel);
        }

        for (var j = 1; j < rows; j++)
        {
            Boxes.Add(o, face + (up * ((j * height / rows) - (height / 2))), Size(width, 0.03f, 0.04f), steel);
        }
    }

    // A pendant lamp whose shade's rim is at height low over (x, z): a cable up to the ceiling, a cone of enamel, a bulb.
    private static void Lamp(List<Vertex> o, List<Vertex> bulbs, SN.Vector3 rim, Finish paint, Finish steel)
    {
        Boxes.Add(o, new(rim.X, (rim.Y + 0.3f + Top) / 2, rim.Z), new(0.012f, Top - rim.Y - 0.3f, 0.012f), steel);
        Lathe(o, rim, [(0.05f, 0.3f), (0.07f, 0.22f), (0.26f, 0.02f), (0.27f, 0f)], new Finish(new(0.13f, 0.2f, 0.17f), Material.Enamel));
        Lathe(bulbs, rim, [(0.0f, 0.14f), (0.06f, 0.1f), (0.07f, 0.05f), (0.04f, 0.0f), (0.0f, -0.01f)], new Finish(new(1f, 0.92f, 0.75f), Material.Matte));
    }

    // A surface of revolution round the vertical through base: (radius, height) from the top down, 24 sides, its
    // faces lit by their own normals.
    internal static void Lathe(List<Vertex> o, SN.Vector3 baseAt, (float R, float Y)[] profile, Finish finish)
    {
        const int sides = 24;
        var flat = new SN.Vector4(0f, 0f, 1f, 1f);
        for (var k = 0; k + 1 < profile.Length; k++)
        {
            for (var s = 0; s < sides; s++)
            {
                var (a0, a1) = (s * MathF.Tau / sides, (s + 1) * MathF.Tau / sides);
                SN.Vector3 P(int i, float a) => baseAt + new SN.Vector3(profile[i].R * MathF.Cos(a), profile[i].Y, profile[i].R * MathF.Sin(a));
                var (p0, p1, p2, p3) = (P(k, a0), P(k, a1), P(k + 1, a1), P(k + 1, a0));
                var mid = (a0 + a1) / 2;
                var outward = new SN.Vector3(MathF.Cos(mid), 0f, MathF.Sin(mid));
                var n = SN.Vector3.Cross(p1 - p0, p3 - p0);
                if (n.LengthSquared() < 1e-12f)
                {
                    n = SN.Vector3.Cross(p2 - p3, p0 - p3);
                }

                n = SN.Vector3.Normalize(n);
                if (SN.Vector3.Dot(n, outward) < 0f)
                {
                    n = -n;
                }

                var tangent = SN.Vector3.Normalize(new SN.Vector3(-MathF.Sin(mid), 0f, MathF.Cos(mid)));
                foreach (var p in SN.Vector3.Dot(SN.Vector3.Cross(p1 - p0, p2 - p0), n) >= 0f ? new[] { p0, p1, p2, p0, p2, p3 } : [p0, p2, p1, p0, p3, p2])
                {
                    o.Add(new Vertex(p, n, finish.Color, finish.Material, tangent, flat));
                }
            }
        }
    }

    public static void SelfTest(Action<bool, string> check, string lowShader)
    {
        var (shell, glass, bulbs) = Build(photographed: false);
        var all = shell.Concat(glass).Concat(bulbs).ToArray();
        check(all.All(v => float.IsFinite(v.Pos.X + v.Pos.Y + v.Pos.Z) && MathF.Abs(v.Normal.Length() - 1f) < 1e-3f) && glass.Length > 0 && bulbs.Length > 0,
            "the hall's shell, its windows' glass and its lamps' bulbs are made");
        var (min, max) = (FreeCamera.Min, FreeCamera.Max);
        check(Left < min.X && Right > max.X && Front > max.Z && Top > max.Y + 0.4f,
            "the walls stand beyond where the camera goes, and the ceiling above it");
        check(FloorLamps.All(p => lowShader.Contains($"vec2({p.X:0.0###}, {p.Y:0.0###})", StringComparison.Ordinal)),
            "the Low shader pools light under each lamp over the floor");
    }
}
