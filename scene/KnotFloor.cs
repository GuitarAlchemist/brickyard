using System.Text.Json;
using SN = System.Numerics;

// The knots' floor: a mezzanine at the back right of the hall, 2 m up against the brick wall, reached by a stair from
// the front, where the gallery of knots that IX read stands on plinths (brain -ix-knots, ixknots.go: each exhibit's
// curve, IX's crossings, writhe and Jones polynomial, and whether it is the tables' knot). The scene draws the curves
// IX read as tubes; it computes nothing about them. Solids are what the walking body stands on and bumps into
// (FreeCamera): the treads, the floor's slab, its rails and its plinths.
internal static class KnotFloor
{
    public const float Level = 2f; // the floor's top
    private const float Slab = 0.15f;
    private const float X0 = 3.9f, X1 = 6.25f, Z0 = -3.3f, Z1 = 0.9f; // the floor, from the hall's side to the wall's end
    private const float StairX0 = 5.2f, StairX1 = 6.2f; // the stair's width, along the floor's outer side
    private const int Risers = 16;
    private const float Tread = 0.19375f; // 3.1 m over 16 treads
    private const float RailHeight = 0.95f;
    private const float Size = 0.36f; // a knot's largest extent on its plinth
    private const float Toward = 0.61f; // radians a knot turns from its plaque's way towards the stair, seen as one arrives
    private const float PlinthTop = Level + 0.7f;

    // An axis-aligned box the body stands on or bumps into.
    internal readonly record struct Solid(SN.Vector3 Min, SN.Vector3 Max);

    // The plinths: their centres on the floor, and which way their plaque faces (+1 towards +x, -1 towards -x).
    private static readonly (float X, float Z, float Facing)[] Plinths =
    [
        (5.75f, 0.0f, -1f), (4.35f, -0.4f, 1f), (5.75f, -0.8f, -1f), (4.35f, -1.2f, 1f),
        (5.75f, -1.6f, -1f), (4.35f, -2.0f, 1f), (5.75f, -2.4f, -1f), (4.35f, -2.8f, 1f),
    ];

    // The steel columns under the floor, declared before the solids made of them.
    private static readonly (float X, float Z)[] Columns = [(4.0f, 0.8f), (4.0f, -1.25f), (5.1f, 0.8f)];

    public static readonly Solid[] Solids = BuildSolids();

    private static Solid[] BuildSolids()
    {
        var s = new List<Solid>();
        // The treads, each a solid step from the hall's floor up to its top, the first at the front.
        for (var k = 1; k <= Risers; k++)
        {
            var z = Z1 + ((Risers - k) * Tread);
            s.Add(new(new(StairX0, 0f, z), new(StairX1, k * Level / Risers, z + Tread)));
        }

        s.Add(new(new(X0, Level - Slab, Z0), new(X1, Level, Z1))); // the floor's slab
        s.Add(new(new(X0 - 0.03f, Level, Z0), new(X0 + 0.03f, Level + RailHeight, Z1))); // the rail on the hall's side
        s.Add(new(new(X0, Level, Z1 - 0.03f), new(StairX0, Level + RailHeight, Z1 + 0.03f))); // the rail at the front, beside the stair's top
        s.Add(new(new(StairX0 - 0.03f, 0f, Z1), new(StairX0 + 0.03f, Level + RailHeight, Z1 + (Risers * Tread)))); // the stair's inner rail
        foreach (var (x, z) in Columns)
        {
            s.Add(new(new(x - 0.07f, 0f, z - 0.07f), new(x + 0.07f, Level - Slab, z + 0.07f)));
        }

        foreach (var (x, z, _) in Plinths)
        {
            s.Add(new(new(x - 0.18f, Level, z - 0.18f), new(x + 0.18f, PlinthTop, z + 0.18f)));
        }

        return [.. s];
    }

    // The mezzanine, its stair, rails and plinths, whatever the gallery holds.
    public static Vertex[] Build(bool photographed)
    {
        var o = new List<Vertex>();
        var concrete = photographed ? new Finish(new(0.95f), Material.ConcreteCast) : new Finish(new(0.42f, 0.41f, 0.39f), Material.Matte);
        var steel = new Finish(new(0.16f, 0.17f, 0.18f), Material.Steel);
        var paint = photographed ? new Finish(new(0.9f), Material.PaintedSteel) : new Finish(new(0.18f, 0.32f, 0.26f), Material.Enamel);
        var stone = new Finish(new(0.78f, 0.76f, 0.72f), Material.Matte);

        // The hall's end wall on the floor's side, in the brick's concrete, so that the floor stands against a wall.
        Boxes.Add(o, new(X1 + 0.08f, 2.3f, 0.35f), new(0.16f, 4.6f, 7.6f), concrete);

        // The slab and its edge beam, on steel columns.
        Boxes.Add(o, new((X0 + X1) / 2, Level - (Slab / 2), (Z0 + Z1) / 2), new(X1 - X0, Slab, Z1 - Z0), concrete);
        Boxes.Add(o, new((X0 + X1) / 2, Level - Slab - 0.09f, Z1 - 0.06f), new(X1 - X0, 0.18f, 0.12f), paint);
        Boxes.Add(o, new(X0 + 0.06f, Level - Slab - 0.09f, (Z0 + Z1) / 2), new(0.12f, 0.18f, Z1 - Z0), paint);
        foreach (var (x, z) in Columns)
        {
            Boxes.Add(o, new(x, (Level - Slab) / 2, z), new(0.14f, Level - Slab, 0.14f), paint);
        }

        // The stair: concrete steps, each a block from the floor, with a steel nosing.
        for (var k = 1; k <= Risers; k++)
        {
            var z = Z1 + ((Risers - k) * Tread);
            var top = k * Level / Risers;
            Boxes.Add(o, new((StairX0 + StairX1) / 2, top / 2, z + (Tread / 2)), new(StairX1 - StairX0, top, Tread), concrete);
            Boxes.Add(o, new((StairX0 + StairX1) / 2, top - 0.006f, z + Tread - 0.012f), new(StairX1 - StairX0, 0.012f, 0.024f), steel);
        }

        // Rails: posts, a top rail and a middle one; along the stair, sloped.
        void Rail(SN.Vector3 a, SN.Vector3 b)
        {
            var n = Math.Max(1, (int)MathF.Ceiling(SN.Vector3.Distance(a, b) / 0.6f));
            for (var i = 0; i <= n; i++)
            {
                var p = SN.Vector3.Lerp(a, b, (float)i / n);
                Boxes.Add(o, p + new SN.Vector3(0f, RailHeight / 2, 0f), new(0.03f, RailHeight, 0.03f), steel);
            }

            Beam(o, a + new SN.Vector3(0f, RailHeight, 0f), b + new SN.Vector3(0f, RailHeight, 0f), 0.04f, steel);
            Beam(o, a + new SN.Vector3(0f, RailHeight / 2, 0f), b + new SN.Vector3(0f, RailHeight / 2, 0f), 0.02f, steel);
        }

        Rail(new(X0, Level, Z0 + 0.02f), new(X0, Level, Z1));
        Rail(new(X0, Level, Z1), new(StairX0, Level, Z1));
        Rail(new(StairX0, Level, Z1), new(StairX0, Level / Risers, Z1 + ((Risers - 1) * Tread) + (Tread / 2)));
        Rail(new(X1, Level, Z0 + 0.02f), new(X1, Level, Z1));
        Rail(new(StairX1, Level, Z1 + Tread), new(StairX1, Level / Risers, Z1 + ((Risers - 1) * Tread) + (Tread / 2)));

        foreach (var (x, z, _) in Plinths)
        {
            Boxes.Add(o, new(x, (Level + PlinthTop) / 2, z), new(0.36f, PlinthTop - Level, 0.36f), stone);
            Boxes.Add(o, new(x, PlinthTop + 0.01f, z), new(0.4f, 0.02f, 0.4f), steel);
        }

        return [.. o];
    }

    // A beam from a to b, square, thick across: eight corners, twelve triangles, its sides lit by their own normals.
    private static void Beam(List<Vertex> o, SN.Vector3 a, SN.Vector3 b, float thick, Finish finish)
    {
        var along = SN.Vector3.Normalize(b - a);
        var side = SN.Vector3.Normalize(SN.Vector3.Cross(along, MathF.Abs(along.Y) > 0.9f ? SN.Vector3.UnitX : SN.Vector3.UnitY));
        var up = SN.Vector3.Cross(side, along);
        var h = thick / 2;
        var flat = new SN.Vector4(0f, 0f, 1f, 1f);
        foreach (var (n, u, v) in new[] { (side, up, along), (-side, along, up), (up, along, side), (-up, side, along) })
        {
            var c = n * h;
            SN.Vector3 P(SN.Vector3 at, float su) => at + c + (u * su * h);
            var (p0, p1, p2, p3) = (P(a, -1), P(a, 1), P(b, 1), P(b, -1));
            // Wound so that the face points along n.
            var face = SN.Vector3.Dot(SN.Vector3.Cross(p1 - p0, p2 - p0), n) > 0 ? new[] { p0, p1, p2, p0, p2, p3 } : [p0, p2, p1, p0, p3, p2];
            foreach (var p in face)
            {
                o.Add(new Vertex(p, n, finish.Color, finish.Material, along, flat));
            }
        }
    }

    // What the gallery's document holds: its exhibits, or why there are none.
    internal sealed record Exhibit(string Id, string Rolfsen, string En, string Fr, bool Verified, bool Mirror, int Crossings, string Jones, float Radius, SN.Vector3[][] Ropes);

    internal sealed record Gallery(Exhibit[] Exhibits, string Why);

    // The document brain -ix-knots wrote, checked within the bounds ixknots.go names; a refusal says why. open: the knot
    // board's (brain -ix-cordage, ixcordage.go), whose ropes have free ends, where the floor's are closed.
    public static Gallery Load(string path, bool open = false)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new([], "absent");
            }

            if (new FileInfo(path).Length > 2 << 20)
            {
                return new([], "too large");
            }

            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.GetProperty("format").GetString() != "delta-ix-knots" || root.GetProperty("v").GetInt32() != 1)
            {
                return new([], "not a gallery of knots");
            }

            if (root.TryGetProperty("error", out var error))
            {
                return new([], Text(error.GetString()));
            }

            var exhibits = new List<Exhibit>();
            foreach (var e in root.GetProperty("exhibits").EnumerateArray())
            {
                if (exhibits.Count == 16)
                {
                    return new([], "more than 16 exhibits");
                }

                var ropes = new List<SN.Vector3[]>();
                var points = 0;
                foreach (var r in e.GetProperty("ropes").EnumerateArray())
                {
                    var rope = r.GetProperty("points").EnumerateArray().Select(p =>
                    {
                        var v = new SN.Vector3((float)p[0].GetDouble(), (float)p[1].GetDouble(), (float)p[2].GetDouble());
                        return float.IsFinite(v.X + v.Y + v.Z) && MathF.Abs(v.X) <= 1f && MathF.Abs(v.Y) <= 1f && MathF.Abs(v.Z) <= 1f
                            ? v : throw new InvalidDataException("a point outside the unit cube");
                    }).ToArray();
                    points += rope.Length;
                    if (rope.Length < 4 || ropes.Count == 8 || points > 256 || r.GetProperty("closed").GetBoolean() == open)
                    {
                        throw new InvalidDataException("ropes beyond the gallery's bounds");
                    }

                    ropes.Add(rope);
                }

                var radius = (float)e.GetProperty("radius").GetDouble();
                if (ropes.Count == 0 || !(radius is > 0f and <= 0.1f))
                {
                    throw new InvalidDataException("an exhibit without ropes or with a tube out of bounds");
                }

                exhibits.Add(new(Text(e.GetProperty("id").GetString()), Text(e.GetProperty("rolfsen").GetString()), Text(e.GetProperty("en").GetString()),
                    Text(e.GetProperty("fr").GetString()), e.GetProperty("verified").GetBoolean(), e.GetProperty("mirror").GetBoolean(),
                    e.GetProperty("crossings").GetInt32(), Text(e.GetProperty("jones").GetString()), radius, [.. ropes]));
            }

            return new([.. exhibits], "");
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new([], $"refused: {e.Message}");
        }
    }

    // The stair's every step is walked, the document is read within its bounds and refused beyond them, and a gallery
    // becomes its knots, its plaques and the sign.
    internal static void SelfTest(Action<bool, string> Check)
    {
        var treads = Solids.Where(s => s.Min.X == StairX0 && s.Min.Y == 0f && s.Max.X == StairX1).OrderBy(s => s.Max.Y).ToArray();
        Check(treads.Length == Risers && treads[^1].Max.Y == Level && treads.Zip(treads.Skip(1)).All(p => p.Second.Max.Y - p.First.Max.Y <= FreeCamera.StepUp && p.Second.Max.Z <= p.First.Min.Z + 1e-4f)
            && Tread > FreeCamera.SolidRadius, $"the stair climbs {Level} m in {Risers} steps no higher than the body walks onto, each deeper than it keeps clear of a step");

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"delta-knots-selftest-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        try
        {
            string Doc(string exhibit) => $$"""{"format":"delta-ix-knots","v":1,"at":"2026-11-14T07:00:00Z","source":"ix_knot","exhibits":[{{exhibit}}]}""";
            const string ring = """{"closed":true,"points":[[0.5,0,0],[0,0.5,0],[-0.5,0,0],[0,-0.5,0.1]]}""";
            string Exhibit(string ropes = ring, string radius = "0.03", string id = "trefoil") =>
                $$"""{"id":"{{id}}","rolfsen":"3_1","en":"Trefoil","fr":"Trèfle","verified":true,"mirror":false,"crossings":3,"jones":"-t^-4 + t^-3 + t^-1","radius":{{radius}},"ropes":[{{ropes}}]}""";
            Gallery Read(string text, bool open = false)
            {
                var path = System.IO.Path.Combine(dir, "ix-knots.json");
                File.WriteAllText(path, text);
                return Load(path, open);
            }

            var good = Read(Doc(Exhibit()));
            Check(good.Exhibits is [{ Id: "trefoil", Verified: true, Crossings: 3, Ropes: [{ Length: 4 }] }] && good.Why == "", "a gallery IX read is taken");
            Check(Load(System.IO.Path.Combine(dir, "none.json")) is { Exhibits: [], Why: "absent" }, "no gallery says it is absent");
            Check(Read("""{"format":"delta-ix-knots","v":1,"error":"IX unavailable: no answer within 30 s"}""") is { Exhibits: [], Why: "IX unavailable: no answer within 30 s" },
                "a gallery IX could not read says why");
            var refused = new[]
            {
                Doc(Exhibit(ring.Replace("[0.5,0,0]", "[1.5,0,0]"))), // a point outside the cube
                Doc(Exhibit(ring.Replace("true", "false"))), // an open rope
                Doc(Exhibit(radius: "0.5")), // a tube thicker than the gallery's
                Doc(Exhibit(ring.Replace("[0.5,0,0],", ""))), // three points
                Doc(Exhibit(id: "a\u0007b")), // a control in a text
                Doc(Exhibit(string.Join(',', Enumerable.Repeat(ring, 9)))), // nine ropes
                Doc(Exhibit()).Replace("delta-ix-knots", "delta-crane"),
                "not json",
            };
            Check(refused.All(text => Read(text).Exhibits.Length == 0 && Read(text).Why != ""), "a gallery beyond its bounds is refused, saying why");

            var en = BuildGallery(good, fr: false);
            var empty = BuildGallery(new([], "absent"), fr: false);
            Check(en.Length > empty.Length + 1000 && en.All(v => float.IsFinite(v.Pos.X + v.Pos.Y + v.Pos.Z) && v.Pos.X > X0 && v.Pos.X < X1 && v.Pos.Z > Z0 - 0.01f && v.Pos.Z < Z1)
                && BuildGallery(good, fr: true).Length != en.Length,
                "a gallery becomes its knot on a plinth, its plaque and the sign, on the knots' floor, in each language");

            // The board takes only ropes with free ends, and hangs them on the end wall above the rail.
            var loose = Doc(Exhibit(ring.Replace("true", "false"), id: "overhand")).Replace("\"3_1\"", "\"3_1#3_1*\"");
            var board = Read(loose, open: true);
            Check(board.Exhibits is [{ Id: "overhand", Rolfsen: "3_1#3_1*", Ropes: [{ Length: 4 }] }] && Read(Doc(Exhibit()), open: true).Exhibits.Length == 0,
                "the knot board takes ropes with free ends and refuses closed ones");
            var hung = BuildBoard(board, fr: false);
            var bare = BuildBoard(new([], "absent"), fr: false);
            Check(hung.Length > bare.Length + 500 && hung.All(v => float.IsFinite(v.Pos.X + v.Pos.Y + v.Pos.Z) && v.Pos.X > X1 - 0.4f && v.Pos.X < X1
                    && v.Pos.Y > Level + RailHeight && v.Pos.Z > Z0 && v.Pos.Z < Z1)
                && BuildBoard(board, fr: true).Length != hung.Length,
                "the knot board becomes its ropes, their plaques and its title, on the end wall above the rail, in each language");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string Text(string? s) =>
        s is { Length: > 0 and <= 96 } && !s.Any(char.IsControl) ? s : throw new InvalidDataException("a text out of bounds");

    // --knots FILE, else %LOCALAPPDATA%\delta\ix-knots.json; for the board, --cordage FILE, else ix-cordage.json.
    public static string Path(string? option, string name = "ix-knots.json") =>
        option ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", name);

    // The knot board, on the end wall above the floor's rail: its frame and felt, between the wall and the knots.
    private const float BoardFront = X1 - 0.035f; // the felt's face, the wall being at X1
    private const float BoardZ0 = -2.95f, BoardZ1 = 0.75f, BoardY0 = 3.0f, BoardY1 = 3.92f;
    private const float BoardKnot = 0.42f; // a knot's largest extent on the board

    // The knot board: knots tied in one rope, each with its plaque, on felt in a wooden frame, in the language asked;
    // or a card saying why there are none. Laid out facing the floor (-x), the drawing's x along the wall towards the
    // stair, its y up and its heights out of the board, halved, so that no knot is seen in a mirror.
    public static Vertex[] BuildBoard(Gallery board, bool fr)
    {
        var local = new List<Vertex>();
        var felt = new Finish(new(0.05f, 0.08f, 0.17f), Material.Matte);
        var wood = new Finish(new(0.42f, 0.27f, 0.14f), Material.Matte);
        var ink = new Finish(new(0.9f, 0.88f, 0.82f), Material.Matte);
        var dim = new Finish(new(0.62f, 0.66f, 0.7f), Material.Matte);
        var warn = new Finish(new(1f, 0.55f, 0.35f), Material.Matte);
        var rope = new Finish(new(0.86f, 0.78f, 0.6f), Material.Matte);
        var (width, height) = (BoardZ1 - BoardZ0, BoardY1 - BoardY0);
        var middle = BoardY0 + (height / 2);
        Boxes.Add(local, new(0f, middle, -0.012f), new(width, height, 0.02f), felt);
        foreach (var (at, size) in new[]
        {
            (new SN.Vector3(0f, BoardY1 + 0.02f, 0f), new SN.Vector3(width + 0.08f, 0.04f, 0.04f)),
            (new SN.Vector3(0f, BoardY0 - 0.02f, 0f), new SN.Vector3(width + 0.08f, 0.04f, 0.04f)),
            (new SN.Vector3(-(width / 2) - 0.02f, middle, 0f), new SN.Vector3(0.04f, height, 0.04f)),
            (new SN.Vector3((width / 2) + 0.02f, middle, 0f), new SN.Vector3(0.04f, height, 0.04f)),
        })
        {
            Boxes.Add(local, at, size, wood);
        }

        var title = fr ? "Nœuds de cordage" : "Knots in rope";
        var under = board.Exhibits.Length > 0
            ? fr ? "Chaque corde refermée par IX : le nœud qu'elle devient, comparé aux tables"
                : "Each rope closed by IX: the knot it becomes, checked against the tables"
            : fr ? $"IX n'a pas lu la planche ({board.Why}) : brain -ix-cordage" : $"IX has not read the board ({board.Why}): brain -ix-cordage";
        Label(local, title, new(0f, BoardY1 - 0.08f, 0.004f), 0.08f, 1.6f, ink);
        Label(local, under, new(0f, BoardY1 - 0.16f, 0.004f), 0.04f, 2.6f, board.Exhibits.Length > 0 ? dim : warn);

        var count = Math.Min(board.Exhibits.Length, 8);
        for (var i = 0; i < count; i++)
        {
            var e = board.Exhibits[i];
            var x = -(width / 2) + ((i + 0.5f) * width / count);
            var centre = new SN.Vector3(x, BoardY0 + 0.4f, 0.03f + (BoardKnot / 4));
            foreach (var r in e.Ropes)
            {
                Tube(local, [.. r.Select(p => centre + new SN.Vector3(p.X, p.Y, p.Z / 2) * BoardKnot)], e.Radius * BoardKnot,
                    e.Verified ? rope : new Finish(new(0.4f), Material.Matte), closed: false);
            }

            var name = fr ? e.Fr : e.En;
            var closure = string.Join(" # ", e.Rolfsen.Split('#').Select(Subscripts));
            var verdict = !e.Verified ? fr ? "IX : pas le nœud des tables" : "IX: not the tables' knot"
                : e.Mirror ? fr ? $"fermé : {closure}, son miroir" : $"closed: {closure}, its mirror"
                : fr ? $"fermé : {closure}" : $"closed: {closure}";
            Label(local, name, new(x, BoardY0 + 0.12f, 0.004f), 0.04f, (width / count) - 0.06f, ink);
            Label(local, verdict, new(x, BoardY0 + 0.065f, 0.004f), 0.03f, (width / count) - 0.06f, e.Verified ? dim : warn);
        }

        var place = SN.Matrix4x4.CreateRotationY(-MathF.PI / 2) * SN.Matrix4x4.CreateTranslation(BoardFront, 0f, (BoardZ0 + BoardZ1) / 2);
        var o = new List<Vertex>(local.Count);
        foreach (var v in local)
        {
            var w = v;
            w.Pos = SN.Vector3.Transform(v.Pos, place);
            w.Normal = SN.Vector3.TransformNormal(v.Normal, place);
            w.Tangent = SN.Vector3.TransformNormal(v.Tangent, place);
            o.Add(w);
        }

        return [.. o];
    }

    private static readonly SN.Vector3[] Colours =
    [
        new(0.78f, 0.22f, 0.12f), new(0.1f, 0.55f, 0.58f), new(0.85f, 0.62f, 0.12f), new(0.5f, 0.28f, 0.7f),
        new(0.22f, 0.6f, 0.25f), new(0.15f, 0.35f, 0.75f), new(0.75f, 0.2f, 0.5f), new(0.8f, 0.8f, 0.78f),
    ];

    private static readonly SN.Vector3[] Rings = [new(0.8f, 0.15f, 0.12f), new(0.15f, 0.6f, 0.2f), new(0.15f, 0.3f, 0.8f)];

    // The knots on their plinths, their plaques and the wall's sign, in the language asked.
    public static Vertex[] BuildGallery(Gallery gallery, bool fr)
    {
        var o = new List<Vertex>();
        var ink = new Finish(new(0.9f, 0.88f, 0.82f), Material.Matte);
        var dim = new Finish(new(0.62f, 0.66f, 0.7f), Material.Matte);
        var warn = new Finish(new(1f, 0.55f, 0.35f), Material.Matte);
        var plaque = new Finish(new(0.08f, 0.08f, 0.09f), Material.Enamel);

        // The sign on the wall, above the floor.
        var title = fr ? "L'étage des nœuds" : "The knots' floor";
        var under = gallery.Exhibits.Length > 0
            ? fr ? "Chaque nœud lu par IX : son polynôme de Jones comparé aux tables" : "Each knot read by IX: its Jones polynomial checked against the tables"
            : fr ? $"IX n'a pas lu les nœuds ({gallery.Why}) : brain -ix-knots" : $"IX has not read the knots ({gallery.Why}): brain -ix-knots";
        Boxes.Add(o, new(5.07f, 3.42f, Z0 + 0.012f), new(2.1f, 0.36f, 0.02f), plaque);
        Label(o, title, new(5.07f, 3.5f, Z0 + 0.03f), 0.13f, 1.9f, ink);
        Label(o, under, new(5.07f, 3.33f, Z0 + 0.03f), 0.055f, 1.95f, gallery.Exhibits.Length > 0 ? dim : warn);

        for (var i = 0; i < gallery.Exhibits.Length && i < Plinths.Length; i++)
        {
            var e = gallery.Exhibits[i];
            var (x, z, facing) = Plinths[i];
            var centre = new SN.Vector3(x, PlinthTop + 0.04f + (Size / 2), z);
            // The knot shows the walkway the diagram IX read, looking down its height (over: "height"), turned a little
            // towards the stair so that one arriving sees it too.
            var turn = SN.Matrix4x4.CreateRotationY(facing * ((MathF.PI / 2) - Toward));
            for (var r = 0; r < e.Ropes.Length; r++)
            {
                var colour = !e.Verified ? new SN.Vector3(0.4f) : e.Ropes.Length > 1 ? Rings[r % Rings.Length] : Colours[i % Colours.Length];
                Tube(o, [.. e.Ropes[r].Select(p => centre + SN.Vector3.Transform(p * Size, turn))], e.Radius * Size, new Finish(colour, Material.Enamel));
            }

            // The plaque, on the plinth's face towards the walkway.
            var name = Subscripts(e.Rolfsen) + "  " + (fr ? e.Fr : e.En);
            var verdict = !e.Verified ? fr ? "IX : pas le nœud des tables" : "IX: not the tables' knot"
                : e.Mirror ? fr ? $"IX : {e.Crossings} croisements, Jones du miroir" : $"IX: {e.Crossings} crossings, the mirror's Jones"
                : fr ? $"IX : {e.Crossings} croisements, Jones des tables" : $"IX: {e.Crossings} crossings, the tables' Jones";
            var labels = new List<Vertex>();
            Boxes.Add(labels, new(0f, 0.5f, 0.001f), new(0.34f, 0.13f, 0.01f), plaque);
            Label(labels, name, new(0f, 0.53f, 0.008f), 0.04f, 0.32f, ink);
            Label(labels, verdict, new(0f, 0.473f, 0.008f), 0.026f, 0.32f, e.Verified ? dim : warn);
            var place = SN.Matrix4x4.CreateRotationY(facing > 0 ? MathF.PI / 2 : -MathF.PI / 2) * SN.Matrix4x4.CreateTranslation(x + (facing * 0.18f), Level, z);
            foreach (var v in labels)
            {
                var w = v;
                w.Pos = SN.Vector3.Transform(v.Pos, place);
                w.Normal = SN.Vector3.TransformNormal(v.Normal, place);
                w.Tangent = SN.Vector3.TransformNormal(v.Tangent, place);
                o.Add(w);
            }
        }

        return [.. o];
    }

    // A line of text lettered height high, narrowed to fit within width when longer.
    private static void Label(List<Vertex> o, string text, SN.Vector3 centre, float height, float width, Finish finish)
    {
        _ = Splash.Text(text, 22, out var w, out var h, true);
        GaiaPresence.Letter(o, text, centre, h > 0 ? Math.Min(width, height * w / h) : width, finish);
    }

    private static string Subscripts(string rolfsen)
    {
        var at = rolfsen.IndexOf('_');
        return at < 0 ? rolfsen : rolfsen[..at] + string.Concat(rolfsen[(at + 1)..].Select(c => c is >= '0' and <= '9' ? (char)('₀' + (c - '0')) : c));
    }

    // A tube of radius r along the curve through points, round it when closed, else from its first point to its last,
    // each end capped: each span a Catmull-Rom curve cut in two, a ring of ten sides at each cut, its frame carried
    // along so that the tube does not twist.
    internal static void Tube(List<Vertex> o, SN.Vector3[] points, float r, Finish finish, bool closed = true)
    {
        const int cuts = 2, sides = 10;
        var n = points.Length;
        SN.Vector3 At(int i) => closed ? points[((i % n) + n) % n] : points[Math.Clamp(i, 0, n - 1)];
        var path = new List<SN.Vector3>(n * cuts);
        for (var i = 0; i < (closed ? n : n - 1); i++)
        {
            var (p0, p1, p2, p3) = (At(i - 1), At(i), At(i + 1), At(i + 2));
            for (var c = 0; c < cuts; c++)
            {
                var t = (float)c / cuts;
                path.Add(0.5f * ((2f * p1) + ((p2 - p0) * t) + (((2f * p0) - (5f * p1) + (4f * p2) - p3) * t * t) + (((3f * p1) - p0 - (3f * p2) + p3) * t * t * t)));
            }
        }

        if (!closed)
        {
            path.Add(points[n - 1]);
        }

        var m = path.Count;
        var tangents = new SN.Vector3[m];
        for (var i = 0; i < m; i++)
        {
            tangents[i] = closed ? SN.Vector3.Normalize(path[(i + 1) % m] - path[(i + m - 1) % m])
                : SN.Vector3.Normalize(path[Math.Min(i + 1, m - 1)] - path[Math.Max(i - 1, 0)]);
        }

        var normal = SN.Vector3.Normalize(SN.Vector3.Cross(tangents[0], MathF.Abs(tangents[0].Y) < 0.9f ? SN.Vector3.UnitY : SN.Vector3.UnitX));
        var frames = new (SN.Vector3 N, SN.Vector3 B)[m + 1];
        for (var i = 0; i <= m; i++)
        {
            var t = tangents[i % m];
            normal = SN.Vector3.Normalize(normal - (t * SN.Vector3.Dot(normal, t)));
            frames[i] = (normal, SN.Vector3.Cross(t, normal));
        }

        // Closed, the frame comes back round turned by some angle: spread that turn along the tube so that its rings meet.
        var (n0, b0) = frames[0];
        var (nm, _) = frames[m];
        var twist = closed ? MathF.Atan2(SN.Vector3.Dot(nm, b0), SN.Vector3.Dot(nm, n0)) : 0f;
        SN.Vector3 Ring(int i, int s)
        {
            var (fn, fb) = frames[i];
            var a = (MathF.Tau * s / sides) - (twist * i / m);
            return (fn * MathF.Cos(a)) + (fb * MathF.Sin(a));
        }

        var flat = new SN.Vector4(0f, 0f, 1f, 1f);
        if (!closed)
        {
            // The two ends, each a disc facing out along the rope.
            foreach (var (end, outward) in new[] { (0, -tangents[0]), (m - 1, tangents[m - 1]) })
            {
                for (var s = 0; s < sides; s++)
                {
                    var (pa, pb) = (path[end] + (Ring(end, s) * r), path[end] + (Ring(end, s + 1) * r));
                    var (qa, qb) = SN.Vector3.Dot(SN.Vector3.Cross(pa - path[end], pb - path[end]), outward) > 0 ? (pa, pb) : (pb, pa);
                    o.Add(new(path[end], outward, finish.Color, finish.Material, tangents[end], flat));
                    o.Add(new(qa, outward, finish.Color, finish.Material, tangents[end], flat));
                    o.Add(new(qb, outward, finish.Color, finish.Material, tangents[end], flat));
                }
            }
        }

        for (var i = 0; i < (closed ? m : m - 1); i++)
        {
            for (var s = 0; s < sides; s++)
            {
                var (na, nb, nc, nd) = (Ring(i, s), Ring(i + 1, s), Ring(i + 1, s + 1), Ring(i, s + 1));
                var (pa, pb, pc, pd) = (path[i] + (na * r), path[(i + 1) % m] + (nb * r), path[(i + 1) % m] + (nc * r), path[i] + (nd * r));
                var t = tangents[i];
                // Wound so that each face points out of the tube.
                if (SN.Vector3.Dot(SN.Vector3.Cross(pb - pa, pc - pa), na + nc) < 0)
                {
                    (pb, pd, nb, nd) = (pd, pb, nd, nb);
                }

                o.Add(new(pa, na, finish.Color, finish.Material, t, flat));
                o.Add(new(pb, nb, finish.Color, finish.Material, t, flat));
                o.Add(new(pc, nc, finish.Color, finish.Material, t, flat));
                o.Add(new(pa, na, finish.Color, finish.Material, t, flat));
                o.Add(new(pc, nc, finish.Color, finish.Material, t, flat));
                o.Add(new(pd, nd, finish.Color, finish.Material, t, flat));
            }
        }
    }
}
