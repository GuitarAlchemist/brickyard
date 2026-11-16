using System.Globalization;
using System.Text.Json;
using SN = System.Numerics;

// The knots' floor: a mezzanine at the back right of the hall, 2 m up against the brick wall, reached by a stair from
// the front, kept for the 21 sailors' knots of the deck « Parler en nœuds » (brain -ix-marins, ixmarins.go: each knot
// drawn by hand from the way it is tied, IX's 3D path for its rope, IX's crossings, writhe and clearance). Each knot
// lies on a planked table as the deck's Blender renders lay it on a deck: a three-strand laid rope round IX's path, its
// post or ring in turned wood, and a card with what IX found. The scene computes nothing about the knots: it draws IX's
// path as rope. Solids are what the walking body stands on and bumps into (FreeCamera): the treads, the floor's slab,
// its rails and its tables.
internal static class KnotFloor
{
    public const float Level = 2f; // the floor's top
    private const float Slab = 0.15f;
    private const float X0 = 3.9f, X1 = 6.25f, Z0 = -3.3f, Z1 = 0.9f; // the floor, from the hall's side to the wall's end
    private const float StairX0 = 5.2f, StairX1 = 6.2f; // the stair's width, along the floor's outer side
    private const int Risers = 16;
    private const float Tread = 0.19375f; // 3.1 m over 16 treads
    private const float RailHeight = 0.95f;

    // The tables: their top, and the rope every knot is shown in, 12 mm across.
    internal const float TableTop = Level + 0.8f;
    internal const float RopeRadius = 0.006f;
    private const float LedgeDepth = 0.13f; // the strip along the walkway's edge where the cards stand
    private const float BackMargin = 0.03f; // between a knot and the table's far edge
    private const float MinGap = 0.05f; // between two knots along a table

    // A table: across x from Near (the walkway's edge) to Far, along z from From to To, in the order a visitor walks.
    internal readonly record struct Table(float Near, float Far, float From, float To)
    {
        public float Facing => MathF.Sign(Near - Far); // +1: the walkway is towards +x
        public SN.Vector3 Away => new(-Facing, 0f, 0f); // from the walkway into the table
        public SN.Vector3 Right => SN.Vector3.Cross(Away, SN.Vector3.UnitY); // along the table, as one facing it reads
        public float Length => MathF.Abs(To - From);
        public float Depth => MathF.Abs(Far - Near);
        public SN.Vector3 Start => new(Near, TableTop, From); // the walkway's edge where the knots begin
        public SN.Vector3 Min => new(MathF.Min(Near, Far), Level, MathF.Min(From, To));
        public SN.Vector3 Max => new(MathF.Max(Near, Far), TableTop, MathF.Max(From, To));
    }

    // Along the rail on the hall's side from the front to the back, then along the end wall from the back to the stair's
    // landing; between them a walkway 65 cm wide.
    internal static readonly Table[] Tables =
    [
        new(X0 + 0.85f, X0 + 0.05f, Z1 - 0.05f, Z0 + 0.05f),
        new(X1 - 0.85f, X1 - 0.05f, Z0 + 0.05f, -0.05f),
    ];

    // An axis-aligned box the body stands on or bumps into.
    internal readonly record struct Solid(SN.Vector3 Min, SN.Vector3 Max);

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

        foreach (var t in Tables)
        {
            s.Add(new(t.Min, t.Max));
        }

        return [.. s];
    }

    // The mezzanine, its stair, rails and tables, whatever the document holds.
    public static Vertex[] Build(bool photographed)
    {
        var o = new List<Vertex>();
        var concrete = photographed ? new Finish(new(0.95f), Material.ConcreteCast) : new Finish(new(0.42f, 0.41f, 0.39f), Material.Matte);
        var steel = new Finish(new(0.16f, 0.17f, 0.18f), Material.Steel);
        var paint = photographed ? new Finish(new(0.9f), Material.PaintedSteel) : new Finish(new(0.18f, 0.32f, 0.26f), Material.Enamel);
        var planks = photographed ? new Finish(new(0.95f, 0.9f, 0.84f), Material.PalletWood) : new Finish(new(0.42f, 0.33f, 0.24f), Material.Matte);
        var oak = new Finish(new(0.2f, 0.12f, 0.065f), Material.Matte);
        var ledge = photographed ? new Finish(new(0.62f, 0.5f, 0.4f), Material.PalletWood) : new Finish(new(0.3f, 0.22f, 0.15f), Material.Matte);

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

        // The tables: a planked top on an oak frame and legs, and a ledge along the walkway's edge for the cards.
        foreach (var t in Tables)
        {
            var (min, max) = (t.Min, t.Max);
            var (cx, cz) = ((min.X + max.X) / 2, (min.Z + max.Z) / 2);
            Boxes.Add(o, new(cx, TableTop - 0.02f, cz), new(max.X - min.X, 0.04f, max.Z - min.Z), planks);
            Boxes.Add(o, new(cx, TableTop - 0.08f, cz), new(max.X - min.X - 0.04f, 0.08f, max.Z - min.Z - 0.04f), oak);
            var legs = Math.Max(2, (int)MathF.Ceiling(t.Length / 1.4f) + 1);
            for (var i = 0; i < legs; i++)
            {
                var z = min.Z + 0.06f + ((max.Z - min.Z - 0.12f) * i / (legs - 1));
                foreach (var x in new[] { min.X + 0.06f, max.X - 0.06f })
                {
                    Boxes.Add(o, new(x, (Level + TableTop - 0.12f) / 2, z), new(0.06f, TableTop - 0.12f - Level, 0.06f), oak);
                }
            }

            Boxes.Add(o, new(t.Near + (t.Away.X * LedgeDepth / 2), TableTop + 0.006f, cz), new(LedgeDepth, 0.012f, max.Z - min.Z), ledge);
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

    // What the document holds: the knots, or why there are none.
    internal sealed record Rope(bool Closed, SN.Vector3[] Points);

    internal sealed record Knot(string Id, string En, string Fr, string Family, string ClosureEn, string ClosureFr, int Crossings, int Writhe,
        float Clearance, bool Verified, float Radius, int[] Posts, float PostScale, Rope[] Ropes);

    internal sealed record Marins(Knot[] Knots, string Why);

    private static readonly string[] Families = ["loops", "bends", "hitches"];

    // The document brain -ix-marins wrote, checked within the bounds ixmarins.go names; a refusal says why.
    public static Marins Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new([], "absent");
            }

            if (new FileInfo(path).Length > 16 << 20)
            {
                return new([], "too large");
            }

            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.GetProperty("format").GetString() != "delta-ix-marins" || root.GetProperty("v").GetInt32() != 1)
            {
                return new([], "not the sailors' knots");
            }

            if (root.TryGetProperty("error", out var error))
            {
                return new([], Text(error.GetString(), 300));
            }

            var knots = new List<Knot>();
            foreach (var k in root.GetProperty("knots").EnumerateArray())
            {
                if (knots.Count == 32)
                {
                    throw new InvalidDataException("more than 32 knots");
                }

                var ropes = new List<Rope>();
                var points = 0;
                foreach (var r in k.GetProperty("ropes").EnumerateArray())
                {
                    var rope = r.GetProperty("points").EnumerateArray().Select(p =>
                    {
                        var v = new SN.Vector3((float)p[0].GetDouble(), (float)p[1].GetDouble(), (float)p[2].GetDouble());
                        return float.IsFinite(v.X + v.Y + v.Z) && MathF.Abs(v.X) <= 1000f && MathF.Abs(v.Y) <= 1000f && MathF.Abs(v.Z) <= 1000f
                            ? v : throw new InvalidDataException("a point beyond ±1000");
                    }).ToArray();
                    points += rope.Length;
                    if (rope.Length < 2 || ropes.Count == 8 || points > 8192)
                    {
                        throw new InvalidDataException("ropes beyond the floor's bounds");
                    }

                    ropes.Add(new(r.GetProperty("closed").GetBoolean(), rope));
                }

                var radius = (float)k.GetProperty("radius").GetDouble();
                var postScale = (float)k.GetProperty("post_scale").GetDouble();
                var posts = k.GetProperty("posts").EnumerateArray().Select(p => p.GetInt32()).ToArray();
                var family = Text(k.GetProperty("family").GetString(), 16);
                if (ropes.Count == 0 || !(radius is > 0f and <= 100f) || !(postScale is >= 0f and <= 5f) || posts.Any(p => p < 0 || p >= ropes.Count)
                    || !Families.Contains(family))
                {
                    throw new InvalidDataException("a knot without ropes, or with a radius, post or family out of bounds");
                }

                knots.Add(new(Text(k.GetProperty("id").GetString()), Text(k.GetProperty("en").GetString()), Text(k.GetProperty("fr").GetString()), family,
                    Text(k.GetProperty("closure_en").GetString()), Text(k.GetProperty("closure_fr").GetString()),
                    Count(k.GetProperty("crossings").GetInt32()), Count(k.GetProperty("writhe").GetInt32()), (float)k.GetProperty("clearance").GetDouble(),
                    k.GetProperty("verified").GetBoolean(), radius, posts, postScale > 0f ? postScale : 2.5f, [.. ropes]));
            }

            return new([.. knots], "");
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException
            or UnauthorizedAccessException or FormatException or IndexOutOfRangeException)
        {
            return new([], $"refused: {e.Message}");
        }
    }

    private static int Count(int n) => n is >= -64 and <= 64 ? n : throw new InvalidDataException("a count beyond ±64");

    private static string Text(string? s, int max = 96) =>
        s is { Length: > 0 } && s.Length <= max && !s.Any(char.IsControl) ? s : throw new InvalidDataException("a text out of bounds");

    // --marins FILE, else %LOCALAPPDATA%\delta\ix-marins.json.
    public static string Path(string? option) =>
        option ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", "ix-marins.json");

    // Where a knot lies: its table, the world directions its drawing's x and y take, already scaled, its centre in the
    // drawing and in the world, and the length of table it takes.
    // Slot: the length of table the knot has to itself, its own and half the gap to each neighbour (its card's room).
    internal readonly record struct Lay(int Table, SN.Vector3 X, SN.Vector3 Y, float Scale, SN.Vector3 Centre, SN.Vector3 Origin, float Along, float Across, float Slot)
    {
        public SN.Vector3 World(SN.Vector3 p) => Origin + (X * (p.X - Centre.X)) + (Y * (p.Y - Centre.Y)) + (SN.Vector3.UnitY * Scale * p.Z);
    }

    // How a knot is shown, in the drawing's units: each rope's path and radius. IX bends a spar or a ring where the rope
    // crosses it, as it does a rope, and passes the rope 1.1 radii from the post's axis. Wood does not bend, so the floor
    // draws a spar straight between the ends IX gives it and a ring flat at its mean height, as thick as the deck drew
    // it (post_scale), and moves IX's rope out from the post's axis just enough to lie on the wood with IX's clearance
    // to spare: each point pushed straight away from the post by the same amount within 4 radii of it (so that the
    // turns round the post keep their spacing and their order, over or under), less and less beyond, untouched past
    // where the push has faded out. Fading presses together two parts at different distances from the post, so the fade
    // is the shortest of 10, 20, 40 and 80 radii that leaves the knot no tighter than IX laid it (Measure). Which part
    // passes over which stays IX's; only the rope near a post is Delta's. A knot without a post is IX's path as it is.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Knot, Shown> Showing = new();

    private sealed record Shown(Rope[] Ropes, float Moved, float Before, float After, float Fade);

    internal static Rope[] Show(Knot k) => Showing.GetValue(k, Choose).Ropes;

    // How far the floor moved IX's rope at most, in rope radii; the least clearance between two parts of the knot's ropes,
    // IX's and as shown, from surface to surface in diameters, between points on two ropes or more than 4 diameters apart
    // along one (round it when closed); and the fade taken, in rope radii.
    internal static (float Moved, float Before, float After, float Fade) Measure(Knot k)
    {
        var s = Showing.GetValue(k, Choose);
        return (s.Moved, s.Before, s.After, s.Fade);
    }

    private static Shown Choose(Knot k)
    {
        var before = Least(k, k.Ropes);
        if (k.Posts.Length == 0)
        {
            return new(k.Ropes, 0f, before, before, 0f);
        }

        Shown? last = null;
        foreach (var fade in (ReadOnlySpan<float>)[10f, 20f, 40f, 80f])
        {
            var ropes = Shape(k, fade);
            var moved = 0f;
            for (var r = 0; r < ropes.Length; r++)
            {
                if (!k.Posts.Contains(r))
                {
                    moved = MathF.Max(moved, ropes[r].Points.Zip(k.Ropes[r].Points).Max(p => SN.Vector3.Distance(p.First, p.Second)));
                }
            }

            last = new(ropes, moved / k.Radius, before, Least(k, ropes), fade);
            if (last.After >= MathF.Min(before, 0f) - 0.005f)
            {
                break;
            }
        }

        return last!;
    }

    private static float Least(Knot k, Rope[] ropes)
    {
        var points = new List<(int Rope, float Along, float Round, SN.Vector3 P)>();
        for (var r = 0; r < ropes.Length; r++)
        {
            if (k.Posts.Contains(r))
            {
                continue;
            }

            var (path, along, first) = (ropes[r].Points, 0f, points.Count);
            for (var i = 0; i < path.Length; i++)
            {
                along += i > 0 ? SN.Vector3.Distance(path[i - 1], path[i]) : 0f;
                points.Add((r, along, 0f, path[i]));
            }

            var round = ropes[r].Closed ? along + SN.Vector3.Distance(path[^1], path[0]) : float.PositiveInfinity;
            for (var i = first; i < points.Count; i++)
            {
                points[i] = points[i] with { Round = round };
            }
        }

        var least = float.MaxValue;
        var apart = 8f * k.Radius;
        for (var i = 0; i < points.Count; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                var (a, b) = (points[i], points[j]);
                var gap = MathF.Abs(a.Along - b.Along);
                if (a.Rope != b.Rope || MathF.Min(gap, a.Round - gap) >= apart)
                {
                    least = MathF.Min(least, SN.Vector3.DistanceSquared(a.P, b.P));
                }
            }
        }

        return least == float.MaxValue ? float.PositiveInfinity : (MathF.Sqrt(least) - (2f * k.Radius)) / (2f * k.Radius);
    }

    private static Rope[] Shape(Knot k, float fadeRadii)
    {
        var ropes = k.Ropes.ToArray();
        foreach (var post in k.Posts)
        {
            var points = k.Ropes[post].Points;
            var closed = k.Ropes[post].Closed;
            SN.Vector3[] axis = closed ? [.. points.Select(p => p with { Z = points.Average(q => q.Z) })] : [points[0], points[^1]];
            ropes[post] = new(closed, axis);
            var wood = k.Radius * k.PostScale;
            var nearest = float.MaxValue;
            for (var r = 0; r < ropes.Length; r++)
            {
                if (!k.Posts.Contains(r))
                {
                    nearest = MathF.Min(nearest, ropes[r].Points.Min(p => Nearest(p, axis, closed).Distance));
                }
            }

            var push = MathF.Max(0f, wood + (1.05f * k.Radius) - nearest);
            if (push <= 0f)
            {
                continue;
            }

            var (held, fade) = (4f * k.Radius, fadeRadii * k.Radius);
            for (var r = 0; r < ropes.Length; r++)
            {
                if (k.Posts.Contains(r))
                {
                    continue;
                }

                ropes[r] = new(ropes[r].Closed, [.. ropes[r].Points.Select(p =>
                {
                    var (on, d) = Nearest(p, axis, closed);
                    var away = d > 1e-6f ? (p - on) / d : SN.Vector3.UnitZ;
                    var moved = d <= held ? push : d >= held + fade ? 0f : push * (1f - ((d - held) / fade));
                    return moved > 0f ? on + (away * (d + moved)) : p;
                })]);
            }
        }

        return ropes;
    }

    // The point of a polyline (round it when closed) nearest p, and how far it is.
    private static (SN.Vector3 Point, float Distance) Nearest(SN.Vector3 p, SN.Vector3[] path, bool closed)
    {
        var best = (Point: path[0], Distance: float.MaxValue);
        var count = closed ? path.Length : path.Length - 1;
        for (var i = 0; i < count; i++)
        {
            var (a, b) = (path[i], path[(i + 1) % path.Length]);
            var ab = b - a;
            var t = ab.LengthSquared() > 0f ? Math.Clamp(SN.Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
            var q = a + (ab * t);
            var d = SN.Vector3.Distance(p, q);
            if (d < best.Distance)
            {
                best = (q, d);
            }
        }

        return best;
    }

    private static float RadiusOf(Knot k, int rope) => k.Posts.Contains(rope) ? k.Radius * k.PostScale : k.Radius;


    // The knots in the deck's order along the tables, each lying flat on the planks, the drawing's heights up, read from
    // the walkway as the deck draws it: its longer side across the table where it fits, the knots spread evenly along
    // each table, and the next table taken when one is full.
    internal static Lay[] Layout(Marins m)
    {
        var sized = m.Knots.Select(k =>
        {
            var scale = RopeRadius / k.Radius;
            var (lo, hi) = (new SN.Vector3(float.MaxValue), new SN.Vector3(float.MinValue));
            var shown = Show(k);
            for (var r = 0; r < shown.Length; r++)
            {
                var pad = RadiusOf(k, r) + (k.Posts.Contains(r) && !shown[r].Closed ? 3 * k.Radius : 0f);
                foreach (var p in shown[r].Points)
                {
                    lo = SN.Vector3.Min(lo, p - new SN.Vector3(pad));
                    hi = SN.Vector3.Max(hi, p + new SN.Vector3(pad));
                }
            }

            var room = Tables[0].Depth - LedgeDepth - BackMargin - 0.02f;
            var (ex, ey) = ((hi.X - lo.X) * scale, (hi.Y - lo.Y) * scale);
            // Its y away from the walkway, or its x: whichever takes less of the table's length, the knot made up to
            // 15% smaller where its side across is a little deeper than the table; failing that, the one that fits best.
            var (shrinkY, shrinkX) = (MathF.Min(1f, room / ey), MathF.Min(1f, room / ex));
            var (lengthY, lengthX) = (ex * shrinkY, ey * shrinkX); // the length taken with y, or x, away
            var turned = shrinkY >= 0.85f && shrinkX >= 0.85f ? lengthX < lengthY : shrinkY >= 0.85f ? false : shrinkX >= 0.85f || shrinkX > shrinkY;
            var shrink = turned ? shrinkX : shrinkY;
            scale *= shrink;
            var (along, across) = turned ? (ey * shrink, ex * shrink) : (ex * shrink, ey * shrink);

            return (Knot: k, Scale: scale, Turned: turned, Along: along, Across: across, Centre: (lo + hi) / 2, Low: lo.Z);
        }).ToArray();

        // Where the deck's order passes to the second table: where the fuller of the two tables is least full, so that
        // neither is crowded while the other has room.
        float Used(int from, int to) => Enumerable.Range(from, to - from).Sum(i => sized[i].Along + MinGap);
        var split = -1;
        var best = float.MaxValue;
        for (var s = 0; s <= sized.Length; s++)
        {
            var fill = Tables.Length < 2
                ? s == sized.Length ? Used(0, s) / Tables[0].Length : float.MaxValue
                : MathF.Max(Used(0, s) / Tables[0].Length, Used(s, sized.Length) / Tables[1].Length);
            if (fill <= 1f && fill < best)
            {
                (split, best) = (s, fill);
            }
        }

        if (split < 0)
        {
            throw new InvalidOperationException($"the {sized.Length} knots do not fit on the tables");
        }

        Fill = best;
        var lays = new Lay[sized.Length];
        var next = 0;
        for (var t = 0; t < Tables.Length && next < sized.Length; t++)
        {
            var table = Tables[t];
            var first = next;
            next = t == 0 ? split : sized.Length;
            var used = Used(first, next);

            var gap = (table.Length - (used - ((next - first) * MinGap))) / Math.Max(1, next - first);
            var at = 0f;
            for (var i = first; i < next; i++)
            {
                var s = sized[i];
                var k = s.Knot;
                at += (gap / 2) + (s.Along / 2);
                var (x, y) = s.Turned ? (table.Away * s.Scale, -table.Right * s.Scale) : (table.Right * s.Scale, table.Away * s.Scale);
                // Its lowest point on the planks: the lowest of its ropes' undersides.
                var low = float.MaxValue;
                var shown = Show(k);
                for (var r = 0; r < shown.Length; r++)
                {
                    low = MathF.Min(low, shown[r].Points.Min(p => p.Z) - RadiusOf(k, r));
                }

                var depth = LedgeDepth + 0.02f + ((table.Depth - LedgeDepth - BackMargin - 0.02f) / 2);
                var origin = table.Start + (table.Right * at) + (table.Away * depth) + (SN.Vector3.UnitY * ((0.0005f - (low * s.Scale)) + 0f));
                lays[i] = new(t, x, y, s.Scale, s.Centre with { Z = 0f }, origin, s.Along, s.Across, s.Along + gap);
                at += (gap / 2) + (s.Along / 2);
            }
        }

        return lays;
    }

    // The box round the given vertices.
    internal static (SN.Vector3 Min, SN.Vector3 Max) Box(params Vertex[][] parts)
    {
        var (lo, hi) = (new SN.Vector3(float.MaxValue), new SN.Vector3(float.MinValue));
        foreach (var part in parts)
        {
            foreach (var v in part)
            {
                (lo, hi) = (SN.Vector3.Min(lo, v.Pos), SN.Vector3.Max(hi, v.Pos));
            }
        }

        return (lo, hi);
    }

    // Whether a picture drawn with viewProj may show some of the box: false only when all its corners lie beyond one
    // side of the view (left, right, below, above, behind or past the far plane), as Vulkan clips.
    internal static bool Seen(SN.Matrix4x4 viewProj, (SN.Vector3 Min, SN.Vector3 Max) box)
    {
        if (box.Min.X > box.Max.X)
        {
            return false;
        }

        Span<int> outside = stackalloc int[6];
        for (var c = 0; c < 8; c++)
        {
            var p = SN.Vector4.Transform(new SN.Vector4((c & 1) == 0 ? box.Min.X : box.Max.X, (c & 2) == 0 ? box.Min.Y : box.Max.Y, (c & 4) == 0 ? box.Min.Z : box.Max.Z, 1f), viewProj);
            outside[0] += p.X < -p.W ? 1 : 0;
            outside[1] += p.X > p.W ? 1 : 0;
            outside[2] += p.Y < -p.W ? 1 : 0;
            outside[3] += p.Y > p.W ? 1 : 0;
            outside[4] += p.Z < 0f ? 1 : 0;
            outside[5] += p.Z > p.W ? 1 : 0;
        }

        foreach (var n in outside)
        {
            if (n == 8)
            {
                return false;
            }
        }

        return true;
    }

    // How full the fuller table is, of its length, after the last Layout: knots and the least gap between them.
    internal static float Fill { get; private set; }

    // The knots as rope on the tables: each rope laid, each post or ring turned in wood. Language-free.
    public static Vertex[] BuildKnots(Marins m)
    {
        var o = new List<Vertex>();
        if (m.Knots.Length == 0)
        {
            return [];
        }

        var lays = Layout(m);
        for (var i = 0; i < m.Knots.Length; i++)
        {
            var (k, lay) = (m.Knots[i], lays[i]);
            var shown = Show(k);
            for (var r = 0; r < shown.Length; r++)
            {
                if (k.Posts.Contains(r))
                {
                    Turned(o, [.. shown[r].Points.Select(lay.World)], shown[r].Closed, RadiusOf(k, r) * lay.Scale, shown[r].Closed ? 0f : 3 * k.Radius * lay.Scale, (i * 8) + r);
                }
                else
                {
                    Laid(o, [.. shown[r].Points.Select(lay.World)], k.Radius * lay.Scale, (i * 8) + r);
                }
            }
        }

        return [.. o];
    }

    // The cards along the tables' ledges, each knot's name and what IX found, and the sign on the back wall, in the
    // language asked; without the document, the sign says why.
    public static Vertex[] BuildCards(Marins m, bool fr)
    {
        var o = new List<Vertex>();
        var ink = new Finish(new(0.9f, 0.88f, 0.82f), Material.Matte);
        var dim = new Finish(new(0.66f, 0.68f, 0.7f), Material.Matte);
        var warn = new Finish(new(1f, 0.55f, 0.35f), Material.Matte);
        var card = new Finish(new(0.07f, 0.07f, 0.08f), Material.Enamel);
        // The scene runs without cultures (invariant globalisation): a French decimal comma by hand.
        string Decimal(float v) => fr ? v.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',') : v.ToString("0.00", CultureInfo.InvariantCulture);

        var title = fr ? "Les nœuds marins" : "Sailors' knots";
        var under = m.Knots.Length > 0
            ? fr ? $"{m.Knots.Length} nœuds dessinés à la main d'après leur nouage ; IX y trouve les croisements, la vrille et le jeu"
                : $"{m.Knots.Length} knots drawn by hand from the way they are tied; IX finds their crossings, writhe and clearance"
            : fr ? $"IX n'a pas lu les nœuds ({m.Why}) : brain -ix-marins" : $"IX has not read the knots ({m.Why}): brain -ix-marins";
        Boxes.Add(o, new(5.07f, 3.42f, Z0 + 0.012f), new(2.1f, 0.36f, 0.02f), card);
        Label(o, title, new(5.07f, 3.5f, Z0 + 0.03f), 0.13f, 1.9f, ink);
        Label(o, under, new(5.07f, 3.33f, Z0 + 0.03f), 0.045f, 1.95f, m.Knots.Length > 0 ? dim : warn);
        if (m.Knots.Length == 0)
        {
            return [.. o];
        }

        var lays = Layout(m);
        const float tilt = 1.05f; // radians the card leans back from upright, towards the eye above it, low enough to show the knot behind
        for (var i = 0; i < m.Knots.Length; i++)
        {
            var (k, lay) = (m.Knots[i], lays[i]);
            var table = Tables[lay.Table];
            var family = k.Family switch
            {
                "loops" => fr ? "Arrêt ou boucle" : "Stopper or loop",
                "bends" => fr ? "Ajut" : "Bend",
                _ => fr ? "Amarrage" : "Hitch",
            };
            var (numbers, clearance) = !k.Verified
                ? (fr ? "IX ne trouve pas" : "IX does not find", fr ? "les chiffres du diaporama" : "the deck's numbers")
                : fr ? ($"{k.Crossings} croisements · vrille {k.Writhe}", $"jeu {Decimal(k.Clearance)} diamètre")
                : ($"{k.Crossings} crossings · writhe {k.Writhe}", $"clearance {Decimal(k.Clearance)} diameter");
            var closure = (fr ? "refermé : " : "closed: ") + SubscriptsAll(fr ? k.ClosureFr : k.ClosureEn);
            // As wide as the knot's slot allows, and tall enough to be read from the stair's top: five lines, the name
            // the largest, lettered finely (Letter's raster at 3 times its usual size).
            var width = MathF.Min(lay.Slot - 0.03f, 0.42f);
            var text = width - 0.024f;
            var local = new List<Vertex>();
            Boxes.Add(local, new(0f, 0.06f, -0.004f), new(width, 0.12f, 0.008f), card);
            Label(local, family, new(0f, 0.106f, 0.001f), 0.013f, text, dim);
            Label(local, fr ? k.Fr : k.En, new(0f, 0.083f, 0.001f), 0.026f, text, ink);
            Label(local, numbers, new(0f, 0.056f, 0.001f), 0.0155f, text, k.Verified ? dim : warn);
            Label(local, clearance, new(0f, 0.036f, 0.001f), 0.0155f, text, k.Verified ? dim : warn);
            Label(local, closure, new(0f, 0.016f, 0.001f), 0.0155f, text, dim);

            // Leaning back on the ledge, facing the walkway and up: its x along the table, its up into the table and up.
            var normal = SN.Vector3.Normalize((-table.Away * MathF.Cos(tilt)) + (SN.Vector3.UnitY * MathF.Sin(tilt)));
            var up = SN.Vector3.Cross(normal, table.Right);
            var foot = new SN.Vector3(lay.Origin.X, TableTop + 0.012f, lay.Origin.Z) - (table.Away * SN.Vector3.Dot(lay.Origin - table.Start, table.Away)) + (table.Away * 0.015f);
            var place = new SN.Matrix4x4(
                table.Right.X, table.Right.Y, table.Right.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                normal.X, normal.Y, normal.Z, 0f,
                foot.X, foot.Y, foot.Z, 1f);
            foreach (var v in local)
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
        const int size = 64;
        _ = Splash.Text(text, size, out var w, out var h, true);
        GaiaPresence.Letter(o, text, centre, h > 0 ? Math.Min(width, height * w / h) : width, finish, size);
    }

    // 6_3 as 6₃, wherever a knot's number is in the text.
    private static string SubscriptsAll(string text)
    {
        var s = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '_' && i > 0 && char.IsDigit(text[i - 1]) && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                for (i++; i < text.Length && char.IsDigit(text[i]); i++)
                {
                    s.Append((char)('₀' + (text[i] - '0')));
                }

                i--;
                continue;
            }

            s.Append(text[i]);
        }

        return s.ToString();
    }

    // The laid rope, as the deck's Blender rope (rope3d/blender_rope.py): three strands in a right-handed helix round
    // the path (Z-laid, one turn in 3.1 diameters), each strand's centre 0.42 of the radius off the path and its radius
    // 0.58, so that the strands press into each other and the rope never reaches beyond its radius. Each strand's yarns
    // and fibres are the shader's (Material.Rope: Edge holds the place along the strand and round it, and its radius);
    // the vertices carry the hemp's colour, darker where the strands meet.
    private const float LayPitch = 6.2f; // radii to a strand's full turn
    private const float StrandOffset = 0.42f, StrandRadius = 0.58f;
    private const int StrandSides = 7;
    private static readonly SN.Vector3 Hemp = new(0.72f, 0.58f, 0.4f), Oak = new(0.62f, 0.42f, 0.24f);

    internal static void Laid(List<Vertex> o, SN.Vector3[] path, float r, int seed)
    {
        var axis = Resample(Smooth(path), r * LayPitch / 9f);
        if (axis.Length < 2)
        {
            return;
        }

        var (_, n, b) = Frames(axis);
        var run = new float[axis.Length];
        for (var j = 1; j < axis.Length; j++)
        {
            run[j] = run[j - 1] + SN.Vector3.Distance(axis[j - 1], axis[j]);
        }

        var tone = 0.94f + (0.06f * MathF.Sin(seed * 2.1f));
        for (var k = 0; k < 3; k++)
        {
            var centres = new SN.Vector3[axis.Length];
            var outward = new SN.Vector3[axis.Length];
            for (var j = 0; j < axis.Length; j++)
            {
                var turn = (MathF.Tau * run[j] / (LayPitch * r)) + (k * MathF.Tau / 3);
                outward[j] = (MathF.Cos(turn) * n[j]) + (MathF.Sin(turn) * b[j]);
                centres[j] = axis[j] + (StrandOffset * r * outward[j]);
            }

            var along = new float[axis.Length];
            for (var j = 1; j < axis.Length; j++)
            {
                along[j] = along[j - 1] + SN.Vector3.Distance(centres[j - 1], centres[j]);
            }

            var rs = StrandRadius * r;
            var shade = tone * (k switch { 0 => 1f, 1 => 0.94f, _ => 1.05f });
            Sweep(o, centres, closed: false, StrandSides, _ => rs, (j, angle, normal) =>
            {
                // Darker where the strand presses into the next, lighter on the rope's outside.
                var g = Math.Clamp((SN.Vector3.Dot(normal, outward[j]) + 0.55f) / 1.3f, 0f, 1f);
                var c = Hemp * shade * (0.42f + (0.58f * g * g * (3 - (2 * g))));
                return (c, Material.Rope, new SN.Vector4(along[j], angle, rs, (seed * 3) + k));
            });
            Cap(o, centres, rs, Hemp * 0.55f, atEnd: false);
            Cap(o, centres, rs, Hemp * 0.55f, atEnd: true);
        }
    }

    // A post or ring in turned oak: a spar straight through its points, carried past its ends; a ring round its own.
    internal static void Turned(List<Vertex> o, SN.Vector3[] path, bool closed, float r, float beyond, int seed)
    {
        var points = path;
        if (!closed && path.Length >= 2 && beyond > 0f)
        {
            var (head, tail) = (SN.Vector3.Normalize(path[0] - path[1]), SN.Vector3.Normalize(path[^1] - path[^2]));
            points = [path[0] + (head * beyond), .. path, path[^1] + (tail * beyond)];
        }

        var axis = Resample(closed ? SmoothClosed(points) : Smooth(points), r * 0.5f);
        if (axis.Length < 2)
        {
            return;
        }

        var along = new float[axis.Length];
        for (var j = 1; j < axis.Length; j++)
        {
            along[j] = along[j - 1] + SN.Vector3.Distance(axis[j - 1], axis[j]);
        }

        var shade = 0.92f + (0.08f * MathF.Sin(seed * 1.7f));
        Sweep(o, axis, closed, 18, _ => r, (j, angle, _) => (Oak * shade, Material.Wood, new SN.Vector4(along[j], angle, r, seed)));
        if (!closed)
        {
            Cap(o, axis, r, Oak * 1.15f, atEnd: false);
            Cap(o, axis, r, Oak * 1.15f, atEnd: true);
        }
    }

    // A Catmull-Rom curve through every point of the path, four pieces to each segment.
    private static SN.Vector3[] Smooth(SN.Vector3[] p)
    {
        if (p.Length < 3)
        {
            return p;
        }

        var curve = new List<SN.Vector3>(p.Length * 4);
        for (var i = 0; i < p.Length - 1; i++)
        {
            var (p0, p1, p2, p3) = (p[Math.Max(0, i - 1)], p[i], p[i + 1], p[Math.Min(p.Length - 1, i + 2)]);
            for (var s = 0; s < 4; s++)
            {
                var (u, u2, u3) = (s / 4f, s * s / 16f, s * s * s / 64f);
                curve.Add(0.5f * ((2 * p1) + ((p2 - p0) * u) + (((2 * p0) - (5 * p1) + (4 * p2) - p3) * u2) + (((3 * p1) - p0 - (3 * p2) + p3) * u3)));
            }
        }

        curve.Add(p[^1]);
        return [.. curve];
    }

    // The same round a closed path, back to its first point.
    private static SN.Vector3[] SmoothClosed(SN.Vector3[] p)
    {
        var n = p.Length;
        var curve = new List<SN.Vector3>((n * 4) + 1);
        for (var i = 0; i < n; i++)
        {
            var (p0, p1, p2, p3) = (p[(i + n - 1) % n], p[i], p[(i + 1) % n], p[(i + 2) % n]);
            for (var s = 0; s < 4; s++)
            {
                var (u, u2, u3) = (s / 4f, s * s / 16f, s * s * s / 64f);
                curve.Add(0.5f * ((2 * p1) + ((p2 - p0) * u) + (((2 * p0) - (5 * p1) + (4 * p2) - p3) * u2) + (((3 * p1) - p0 - (3 * p2) + p3) * u3)));
            }
        }

        curve.Add(p[0]);
        return [.. curve];
    }

    // Points every step along a polyline, its last point included.
    private static SN.Vector3[] Resample(SN.Vector3[] p, float step)
    {
        var points = new List<SN.Vector3> { p[0] };
        var travelled = 0f;
        var next = step;
        for (var i = 1; i < p.Length; i++)
        {
            var segment = SN.Vector3.Distance(p[i - 1], p[i]);
            for (; segment > 0 && next <= travelled + segment; next += step)
            {
                points.Add(SN.Vector3.Lerp(p[i - 1], p[i], (next - travelled) / segment));
            }

            travelled += segment;
        }

        if (SN.Vector3.Distance(points[^1], p[^1]) > step / 4)
        {
            points.Add(p[^1]);
        }

        return [.. points];
    }

    // Frames along a curve that do not twist about it (parallel transport): tangent T, normal N, and B = T × N.
    private static (SN.Vector3[] T, SN.Vector3[] N, SN.Vector3[] B) Frames(SN.Vector3[] p)
    {
        var (t, n, b) = (new SN.Vector3[p.Length], new SN.Vector3[p.Length], new SN.Vector3[p.Length]);
        for (var i = 0; i < p.Length; i++)
        {
            var d = p[Math.Min(p.Length - 1, i + 1)] - p[Math.Max(0, i - 1)];
            t[i] = d.LengthSquared() > 1e-16f ? SN.Vector3.Normalize(d) : i > 0 ? t[i - 1] : SN.Vector3.UnitX;
        }

        var first = SN.Vector3.Cross(t[0], MathF.Abs(t[0].Y) < 0.9f ? SN.Vector3.UnitY : SN.Vector3.UnitX);
        for (var i = 0; i < p.Length; i++)
        {
            var carried = i == 0 ? first : n[i - 1];
            var across = carried - (t[i] * SN.Vector3.Dot(carried, t[i]));
            n[i] = across.LengthSquared() > 1e-12f ? SN.Vector3.Normalize(across) : (i == 0 ? SN.Vector3.Normalize(first) : n[i - 1]);
            b[i] = SN.Vector3.Cross(t[i], n[i]);
        }

        return (t, n, b);
    }

    // A tube along centres, sides faces round it, its frames carried along without twisting; closed, its centres end
    // where they began and the turn the frame took on the way is spread along it, so that its last ring meets its first.
    // paint(ring, angle, outward) gives each vertex's colour, material and Edge; the angle runs to 2π on the last side,
    // so that what the shader draws round it closes without a seam.
    private static void Sweep(List<Vertex> o, SN.Vector3[] centres, bool closed, int sides, Func<int, float> radius,
        Func<int, float, SN.Vector3, (SN.Vector3 Colour, Material Material, SN.Vector4 Edge)> paint)
    {
        var m = centres.Length;
        var (t, n, b) = Frames(centres);
        var twist = 0f;
        if (closed && m > 2)
        {
            // The first frame carried once more to the start: how far it turned about the curve.
            var carried = n[m - 1] - (t[0] * SN.Vector3.Dot(n[m - 1], t[0]));
            carried = SN.Vector3.Normalize(carried);
            twist = MathF.Atan2(SN.Vector3.Dot(carried, b[0]), SN.Vector3.Dot(carried, n[0]));
        }

        var rings = new (SN.Vector3 P, SN.Vector3 N, SN.Vector3 C, Material M, SN.Vector4 E)[m, sides + 1];
        for (var j = 0; j < m; j++)
        {
            var spin = closed ? -twist * j / (m - 1) : 0f;
            var r = radius(j);
            for (var s = 0; s <= sides; s++)
            {
                var angle = MathF.Tau * s / sides;
                var a = angle + spin;
                var outward = (MathF.Cos(a) * n[j]) + (MathF.Sin(a) * b[j]);
                var (c, mat, e) = paint(j, angle, outward);
                rings[j, s] = (centres[j] + (outward * r), outward, c, mat, e);
            }
        }

        for (var j = 0; j < m - 1; j++)
        {
            for (var s = 0; s < sides; s++)
            {
                var (a, bq, c, d) = (rings[j, s], rings[j + 1, s], rings[j + 1, s + 1], rings[j, s + 1]);
                // Wound so that each face points out of the tube; where a tight bend folds a face over, it is left out.
                var cross = SN.Vector3.Cross(bq.P - a.P, c.P - a.P);
                if (SN.Vector3.Dot(cross, a.N + c.N) < 0)
                {
                    (bq, d) = (d, bq);
                }

                Corner(o, a, t[j]);
                Corner(o, bq, t[j + 1]);
                Corner(o, c, t[j + 1]);
                Corner(o, a, t[j]);
                Corner(o, c, t[j + 1]);
                Corner(o, d, t[j]);
            }
        }
    }

    private static void Corner(List<Vertex> o, (SN.Vector3 P, SN.Vector3 N, SN.Vector3 C, Material M, SN.Vector4 E) v, SN.Vector3 tangent) =>
        o.Add(new(v.P, v.N, v.C, v.M, tangent, v.E));

    // An end cut square across the tube: a disc facing out along it.
    private static void Cap(List<Vertex> o, SN.Vector3[] centres, float r, SN.Vector3 colour, bool atEnd)
    {
        if (centres.Length < 2)
        {
            return;
        }

        var (_, n, b) = Frames(centres);
        var j = atEnd ? centres.Length - 1 : 0;
        var outward = SN.Vector3.Normalize(atEnd ? centres[^1] - centres[^2] : centres[0] - centres[1]);
        var flat = new SN.Vector4(0f, 0f, 1f, 1f);
        const int sides = 12;
        for (var s = 0; s < sides; s++)
        {
            var (a0, a1) = (MathF.Tau * s / sides, MathF.Tau * (s + 1) / sides);
            var p0 = centres[j] + (r * ((MathF.Cos(a0) * n[j]) + (MathF.Sin(a0) * b[j])));
            var p1 = centres[j] + (r * ((MathF.Cos(a1) * n[j]) + (MathF.Sin(a1) * b[j])));
            var (qa, qb) = SN.Vector3.Dot(SN.Vector3.Cross(p0 - centres[j], p1 - centres[j]), outward) > 0 ? (p0, p1) : (p1, p0);
            o.Add(new(centres[j], outward, colour, Material.Matte, n[j], flat));
            o.Add(new(qa, outward, colour, Material.Matte, n[j], flat));
            o.Add(new(qb, outward, colour, Material.Matte, n[j], flat));
        }
    }

    // The stair's every step is walked, the document is read within its bounds and refused beyond them, the 21 knots
    // fit the tables without overlapping, and a rope stays within its radius of IX's path.
    internal static void SelfTest(Action<bool, string> Check)
    {
        var treads = Solids.Where(s => s.Min.X == StairX0 && s.Min.Y == 0f && s.Max.X == StairX1).OrderBy(s => s.Max.Y).ToArray();
        Check(treads.Length == Risers && treads[^1].Max.Y == Level && treads.Zip(treads.Skip(1)).All(p => p.Second.Max.Y - p.First.Max.Y <= FreeCamera.StepUp && p.Second.Max.Z <= p.First.Min.Z + 1e-4f)
            && Tread > FreeCamera.SolidRadius, $"the stair climbs {Level} m in {Risers} steps no higher than the body walks onto, each deeper than it keeps clear of a step");
        Check(Tables.All(t => t.Min.X >= X0 && t.Max.X <= X1 && t.Min.Z >= Z0 && t.Max.Z <= Z1) && Tables[0].Near + 0.6f < Tables[1].Near
            && Tables[1].To < Z1 - 0.9f, "the two tables stand on the floor with a walkway between them and the stair's landing clear");

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"delta-marins-selftest-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        try
        {
            string Doc(string knots) => $$"""{"format":"delta-ix-marins","v":1,"at":"2026-11-16T07:00:00Z","source":"ix_knot","knots":[{{knots}}]}""";
            // A rope as IX gives one, a point every half radius.
            var rope = $$"""{"closed":false,"points":[{{string.Join(',', Enumerable.Range(0, 81).Select(i =>
            {
                var x = -4f + (i * 0.1f);
                return string.Create(CultureInfo.InvariantCulture, $"[{x},{0.5f * MathF.Sin(x)},{0.3f * MathF.Sin(2 * x)}]");
            }))}}]}""";
            const string spar = """{"closed":false,"points":[[0,-3,0],[0,3,0]]}""";
            string Knot(string? ropes = null, string id = "bowline", string family = "loops", string posts = "[]", string radius = "0.16") =>
                $$"""{"id":"{{id}}","en":"Bowline","fr":"Nœud de chaise","family":"{{family}}","closure_en":"Jones of 6_3","closure_fr":"Jones de 6_3","crossings":7,"writhe":0,"components":1,"clearance":1.038,"jones":"x","verified":true,"radius":{{radius}},"posts":{{posts}},"post_scale":2.5,"ropes":[{{ropes ?? rope}}]}""";
            Marins Read(string text)
            {
                var path = System.IO.Path.Combine(dir, "ix-marins.json");
                File.WriteAllText(path, text);
                return Load(path);
            }

            var good = Read(Doc(Knot() + "," + Knot(rope + "," + spar, "clove-hitch", "hitches", "[1]")));
            Check(good.Knots is [{ Id: "bowline", Verified: true, Crossings: 7, Ropes: [{ Points.Length: 81 }] }, { Posts: [1], PostScale: 2.5f }] && good.Why == "",
                "a document IX read is taken, with a hitch's spar");
            Check(Load(System.IO.Path.Combine(dir, "none.json")) is { Knots: [], Why: "absent" }, "no document says it is absent");
            Check(Read("""{"format":"delta-ix-marins","v":1,"error":"IX unavailable: no answer within 30 s"}""") is { Knots: [], Why: "IX unavailable: no answer within 30 s" },
                "a document IX could not write says why");
            var refused = new[]
            {
                Doc(Knot(rope.Replace("[-4,", "[-4000,"))), // a point beyond ±1000
                Doc(Knot(radius: "0")), // no rope
                Doc(Knot(posts: "[3]")), // a post that is not a rope
                Doc(Knot(family: "knits")), // a family the floor does not have
                Doc(Knot(id: "a\u0007b")), // a control in a text
                Doc(Knot(string.Join(',', Enumerable.Repeat(rope, 9)))), // nine ropes
                Doc(Knot()).Replace("delta-ix-marins", "delta-ix-knots"),
                "not json",
            };
            Check(refused.All(text => Read(text).Knots.Length == 0 && Read(text).Why != ""), "a document beyond its bounds is refused, saying why");

            // The worst the deck holds, 21 times: as long as the Flemish bend (6.2 × 17 at a radius of 0.16).
            var big = new Rope(false, [new(-3.1f, -8.5f, -0.35f), new(3.1f, 0f, 0.35f), new(-3.1f, 8.5f, 0f)]);
            var many = new Marins([.. Enumerable.Range(0, 21).Select(i => good.Knots[0] with { Id = $"k{i}", Ropes = [big] })], "");
            var lays = Layout(many);
            var spans = lays.Select(l => (l.Table, From: SN.Vector3.Dot(l.Origin - Tables[l.Table].Start, Tables[l.Table].Right) - (l.Along / 2), l.Along)).ToArray();
            Check(lays.All(l => l.Along > 0 && l.Across <= Tables[l.Table].Depth - LedgeDepth) && spans.All(s => s.From >= -1e-4f && s.From + s.Along <= Tables[s.Table].Length + 1e-4f)
                && spans.Zip(spans.Skip(1)).All(p => p.First.Table != p.Second.Table || p.Second.From >= p.First.From + p.First.Along),
                "21 knots as long as the Flemish bend lie on the two tables, one after the other, none over another or off its table");

            var hitch = good.Knots[1];
            var shownHitch = Show(hitch);
            var sparPath = shownHitch[1].Points;
            var nearest = shownHitch[0].Points.Min(p => Nearest(p, sparPath, closed: false).Distance);
            var wood = hitch.Radius * hitch.PostScale;
            var kept = hitch.Ropes[0].Points.Zip(shownHitch[0].Points).Where(p => Nearest(p.First, sparPath, false).Distance >= 14f * hitch.Radius).ToArray();
            var measured = Measure(hitch);
            Check(sparPath.Length == 2 && nearest >= wood + hitch.Radius - 1e-4f && kept.Length > 10 && kept.All(p => p.First == p.Second)
                && measured.After >= MathF.Min(measured.Before, 0f) - 0.005f && Measure(good.Knots[0]) is { Moved: 0f, Fade: 0f },
                $"a spar is straight and as thick as the deck drew it, the rope moved out onto it ({(nearest - wood) / hitch.Radius:F2} radii from its axis to the wood) and left as IX laid it far from it");

            var knots = BuildKnots(good);
            var one = new Marins([good.Knots[0]], "");
            var lay = Layout(one)[0];
            var axis = good.Knots[0].Ropes[0].Points.Select(lay.World).ToArray();
            var strands = BuildKnots(one).Where(v => v.Material == Material.Rope).ToArray();
            float Off(SN.Vector3 p) => Enumerable.Range(0, 400).Min(i =>
            {
                var u = i / 399f * (axis.Length - 1);
                var j = Math.Min((int)u, axis.Length - 2);
                return SN.Vector3.Distance(p, SN.Vector3.Lerp(axis[j], axis[j + 1], u - j));
            });
            var farthest = strands.Where((_, i) => i % 97 == 0).Max(v => Off(v.Pos));
            Check(strands.Length > 1000 && farthest <= RopeRadius * 1.05f, $"a rope is three strands within its radius of IX's path ({farthest * 1000:F2} mm for {RopeRadius * 1000:F0})");
            var lowest = knots.Min(v => v.Pos.Y);
            Check(knots.Any(v => v.Material == Material.Wood) && knots.All(v => float.IsFinite(v.Pos.X + v.Pos.Y + v.Pos.Z) && MathF.Abs(v.Normal.Length() - 1) < 1e-3f)
                && lowest >= TableTop - 0.001f && lowest < TableTop + 0.002f, $"a hitch's spar is wood, and each knot lies on the table's top ({(lowest - TableTop) * 1000:F2} mm)");

            var box = Box(knots);
            var projection = SN.Matrix4x4.CreatePerspectiveFieldOfView(1f, 16f / 9f, 0.05f, 60f);
            var stair = new SN.Vector3(5.05f, Level + 1.6f, 0.6f);
            Check(Seen(SN.Matrix4x4.CreateLookAt(stair, new(5.05f, Level + 0.9f, -3f), SN.Vector3.UnitY) * projection, box)
                && !Seen(SN.Matrix4x4.CreateLookAt(stair, stair + new SN.Vector3(0f, 0f, 3f), SN.Vector3.UnitY) * projection, box)
                && !Seen(SN.Matrix4x4.CreateLookAt(new(0f, 1.2f, 1.5f), new(0f, 0.9f, 4f), SN.Vector3.UnitY) * projection, box)
                && !Seen(SN.Matrix4x4.Identity, Box()),
                "the knots are drawn when the picture looks at them, not when it looks away");

            var en = BuildCards(good, fr: false);
            var empty = BuildCards(new([], "absent"), fr: false);
            Check(en.Length > empty.Length + 1000 && BuildCards(good, fr: true).Length != en.Length && en.All(v => float.IsFinite(v.Pos.X + v.Pos.Y + v.Pos.Z))
                && SubscriptsAll("Jones of 6_3 and 8_20, 3_1 # 3_1*") == "Jones of 6₃ and 8₂₀, 3₁ # 3₁*",
                "each knot has its card and the sign its title, in each language, the knots' numbers in subscript");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
