using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using SN = System.Numerics;

// The streets of Ottawa behind the hall's roll-up door (« quand je clique sur le garage, puis-je me promener dans les
// rues d'Ottawa »): OpenStreetMap's buildings round Parliament Hill, extruded, and its streets as ribbons on the ground,
// after GeoTiles' OsmBuildings (GuitarAlchemist/ga, Experiments/Brutal/GeoTiles, MIT). OpenStreetMap's data is open
// (ODbL): it may be kept with its attribution, so the Overpass answer is fetched once and kept in
// %LOCALAPPDATA%\delta\ottawa; Google's tiles never are.
//
// The hall stands on the south side of Wellington Street, its door on the sidewalk opposite the Centre Block: the
// point of Wellington nearest the Centre Block, Setback metres further from it. The hall's +Z, out through the door, is
// toward the Centre Block, and +X to its left (a right-handed frame with y up). Buildings whose footprint reaches the
// hall's own are left out, so that nothing stands inside it.
internal static class OttawaStreets
{
    internal const string Attribution = "© OpenStreetMap contributors";
    internal const double Lat = 45.4236, Lon = -75.7009, Radius = 450; // Parliament Hill, as GeoTiles' default place
    internal const float Setback = 14f; // metres from Wellington's middle to the door: half the street and its sidewalk
    private const double LevelHeight = 3, DefaultHeight = 8;

    // The hall's footprint, from HallShell's walls and their thickness, with a margin.
    private static readonly SN.Vector2 HallMin = new(HallShell.Left - 1f, HallShell.Back - 1f), HallMax = new(HallShell.Right + 1f, HallShell.Front + 0.5f);

    internal static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", "ottawa", "overpass.json");

    // The buildings and the streets within Radius of Parliament Hill, with their geometry.
    internal static string Query()
    {
        var around = string.Create(CultureInfo.InvariantCulture, $"(around:{Radius:F0},{Lat:F6},{Lon:F6})");
        return $"[out:json][timeout:60];(way[\"building\"]{around};way[\"building:part\"]{around};relation[\"building\"]{around};way[\"highway\"]{around};);out geom;";
    }

    // The kept answer, or one fetched now from the public Overpass server and kept; null when neither is to be had.
    internal static async Task<string?> Answer(CancellationToken cancel)
    {
        var path = CachePath;
        if (File.Exists(path)) return await File.ReadAllTextAsync(path, cancel);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("delta-scene/1.0");
        using var body = new FormUrlEncodedContent([new("data", Query())]);
        using var response = await http.PostAsync("https://overpass-api.de/api/interpreter", body, cancel);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(cancel);
        using (var doc = JsonDocument.Parse(json))
        {
            if (!doc.RootElement.TryGetProperty("elements", out var elements) || elements.GetArrayLength() == 0) return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path + ".part", json, cancel);
        File.Move(path + ".part", path, overwrite: true);
        return json;
    }

    // The city, off the render thread: the kept answer or a fetched one, built; null without an answer.
    internal static Task<City?> Load() => Task.Run(async () =>
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        return await Answer(cancel.Token) is { } json ? Build(json) : null;
    });

    // East and north metres from (Lat, Lon): an equirectangular map, within millimetres over a kilometre here.
    internal static SN.Vector2 Local(double lat, double lon) =>
        new((float)((lon - Lon) * 111_320.0 * Math.Cos(Lat * Math.PI / 180)), (float)((lat - Lat) * 110_574.0));

    // Where the hall stands: its door's point in east-north metres and the way out of it, a unit vector; and a map
    // from east-north metres to the hall's frame.
    internal sealed record Placement(SN.Vector2 Door, SN.Vector2 Out)
    {
        internal SN.Vector2 Left => new(-Out.Y, Out.X);

        // Out is the hall's +Z and Left its +X: facing +Z with y up, +X is on the left. Metres become the hall's, whose
        // eye stands 1.1 high (FreeCamera.Scale); y is in real metres too.
        internal SN.Vector3 ToHall(SN.Vector2 en, float y)
        {
            var d = (en - Door) * FreeCamera.Scale;
            return new(HallShell.DoorX + SN.Vector2.Dot(d, Left), y * FreeCamera.Scale, HallShell.Front + 0.16f + SN.Vector2.Dot(d, Out));
        }
    }

    private sealed record Way(Dictionary<string, string> Tags, List<SN.Vector2> Points);

    private static List<Way> Ways(JsonElement root)
    {
        var ways = new List<Way>();
        foreach (var element in root.GetProperty("elements").EnumerateArray())
        {
            var tags = new Dictionary<string, string>();
            if (element.TryGetProperty("tags", out var t))
            {
                foreach (var p in t.EnumerateObject()) tags[p.Name] = p.Value.GetString() ?? "";
            }

            List<SN.Vector2> Ring(JsonElement geometry) => [.. geometry.EnumerateArray().Select(n => Local(n.GetProperty("lat").GetDouble(), n.GetProperty("lon").GetDouble()))];
            if (element.TryGetProperty("geometry", out var g)) ways.Add(new(tags, Ring(g)));
            else if (element.TryGetProperty("members", out var members))
            {
                foreach (var m in members.EnumerateArray())
                {
                    if (m.TryGetProperty("role", out var role) && role.GetString() == "outer" && m.TryGetProperty("geometry", out var outer)) ways.Add(new(tags, Ring(outer)));
                }
            }
        }

        return ways;
    }

    // The door's place from the answer: opposite the Centre Block across Wellington Street; at the query's centre,
    // facing north, when either is missing.
    internal static Placement Place(IReadOnlyList<(Dictionary<string, string> Tags, List<SN.Vector2> Points)> ways)
    {
        var block = ways.FirstOrDefault(w => w.Tags.GetValueOrDefault("name") is "Centre Block" or "Édifice du Centre" && w.Tags.ContainsKey("building"));
        var wellington = ways.Where(w => w.Tags.ContainsKey("highway") && w.Tags.GetValueOrDefault("name") is "Wellington Street" or "rue Wellington").ToList();
        if (block.Points is not { Count: > 2 } || wellington.Count == 0) return new(new(0f, -60f), SN.Vector2.UnitY);
        var corners = SN.Vector2.Distance(block.Points[0], block.Points[^1]) < 0.01f ? block.Points.SkipLast(1).ToList() : block.Points; // a closed ring repeats its first point
        var centre = corners.Aggregate(SN.Vector2.Zero, (a, p) => a + p) / corners.Count;
        var nearest = wellington.SelectMany(w => w.Points.Zip(w.Points.Skip(1), (a, b) => Nearest(a, b, centre))).MinBy(p => SN.Vector2.Distance(p, centre));
        var toward = SN.Vector2.Normalize(centre - nearest);
        return new(nearest - (toward * Setback), toward);
    }

    private static SN.Vector2 Nearest(SN.Vector2 a, SN.Vector2 b, SN.Vector2 p)
    {
        var ab = b - a;
        var t = ab.LengthSquared() < 1e-6f ? 0f : Math.Clamp(SN.Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
        return a + (t * ab);
    }

    internal sealed record City(Vertex[] Vertices, Placement Placement, int Buildings, int Streets, int Left);

    // The city of an Overpass answer in the hall's frame: each building's walls and roof, each street a ribbon on the
    // ground; Left counts the buildings left out because they reach the hall.
    internal static City Build(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var ways = Ways(doc.RootElement);
        var placement = Place([.. ways.Select(w => (w.Tags, w.Points))]);
        var o = new List<Vertex>();
        var (buildings, streets, left) = (0, 0, 0);
        foreach (var way in ways.Where(w => w.Tags.ContainsKey("highway")))
        {
            var (width, colour, y) = Street(way.Tags["highway"]);
            if (width <= 0) continue;
            for (var i = 0; i + 1 < way.Points.Count; i++)
            {
                var (a, b) = (placement.ToHall(way.Points[i], y), placement.ToHall(way.Points[i + 1], y));
                if (InHall(new((a.X + b.X) / 2, (a.Z + b.Z) / 2)) || InHall(new(a.X, a.Z)) || InHall(new(b.X, b.Z))) continue;
                Ribbon(o, a, b, width * FreeCamera.Scale, colour);
            }

            streets++;
        }

        foreach (var way in ways.Where(w => w.Tags.ContainsKey("building") || w.Tags.ContainsKey("building:part")))
        {
            var ring = way.Points.Select(p => placement.ToHall(p, 0f)).Select(p => new SN.Vector2(p.X, p.Z)).ToList();
            if (ring.Count < 4 || SN.Vector2.Distance(ring[0], ring[^1]) > 0.01f) continue;
            ring.RemoveAt(ring.Count - 1);
            if (Reaches(ring)) { left++; continue; }
            var (bottom, top) = Heights(way.Tags);
            Extrude(o, ring, (float)bottom * FreeCamera.Scale, (float)top * FreeCamera.Scale, Stone(way.Points[0]));
            buildings++;
        }

        // The ground: a square of pavement a kilometre across, just under the hall's floor.
        var g = new Finish(new(0.3f, 0.3f, 0.29f), Material.Matte);
        Boxes.Add(o, new(HallShell.DoorX, -0.06f, HallShell.Front), new(1200f, 0.1f, 1200f), g);
        return new([.. o], placement, buildings, streets, left);
    }

    // A street's width, colour and height above the ground by its kind; 0 wide for what is not drawn.
    private static (float Width, SN.Vector3 Colour, float Y) Street(string kind)
    {
        SN.Vector3 asphalt = new(0.08f, 0.08f, 0.085f), walk = new(0.45f, 0.43f, 0.4f);
        return kind switch
        {
            "motorway" or "trunk" or "primary" => (14f, asphalt, 0.02f),
            "secondary" => (11f, asphalt, 0.02f),
            "tertiary" => (9f, asphalt, 0.02f),
            "residential" or "unclassified" or "living_street" => (7f, asphalt, 0.02f),
            "service" => (4.5f, asphalt, 0.02f),
            "pedestrian" => (6f, walk, 0.03f),
            "footway" or "path" or "cycleway" or "steps" => (2.4f, walk, 0.03f),
            _ => (0f, asphalt, 0f),
        };
    }

    private static (double Bottom, double Top) Heights(Dictionary<string, string> tags)
    {
        double? Tag(string name)
        {
            if (!tags.TryGetValue(name, out var text)) return null;
            var end = 0;
            text = text.Trim();
            while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
            return double.TryParse(text.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
        }

        var top = Tag("height") ?? (Tag("building:levels") is { } levels ? levels * LevelHeight : DefaultHeight);
        var bottom = Tag("min_height") ?? (Tag("building:min_level") is { } min ? min * LevelHeight : 0);
        return top > bottom ? (bottom, top) : (0, DefaultHeight);
    }

    // A building's stone, one of a few pale tones, from where it stands.
    private static SN.Vector3 Stone(SN.Vector2 at)
    {
        SN.Vector3[] tones = [new(0.62f, 0.58f, 0.5f), new(0.55f, 0.52f, 0.48f), new(0.66f, 0.62f, 0.55f), new(0.5f, 0.47f, 0.44f), new(0.58f, 0.5f, 0.42f)];
        var h = (uint)HashCode.Combine(MathF.Round(at.X), MathF.Round(at.Y));
        return tones[(int)(((h * 2654435761u) >> 16) % (uint)tones.Length)];
    }

    private static bool InHall(SN.Vector2 p) => p.X > HallMin.X && p.X < HallMax.X && p.Y > HallMin.Y && p.Y < HallMax.Y;

    // Whether a footprint (x, z) reaches the hall's: its box overlaps the hall's.
    private static bool Reaches(List<SN.Vector2> ring) =>
        ring.Max(p => p.X) > HallMin.X && ring.Min(p => p.X) < HallMax.X && ring.Max(p => p.Y) > HallMin.Y && ring.Min(p => p.Y) < HallMax.Y;

    private static readonly SN.Vector4 Flat = new(0f, 0f, 1f, 1f);

    private static void Triangle(List<Vertex> o, SN.Vector3 a, SN.Vector3 b, SN.Vector3 c, SN.Vector3 n, SN.Vector3 colour)
    {
        // Wound counter-clockwise about n, as the boxes are.
        if (SN.Vector3.Dot(SN.Vector3.Cross(b - a, c - a), n) < 0) (b, c) = (c, b);
        var tangent = SN.Vector3.Normalize(b - a);
        o.AddRange([new(a, n, colour, Material.Matte, tangent, Flat), new(b, n, colour, Material.Matte, tangent, Flat), new(c, n, colour, Material.Matte, tangent, Flat)]);
    }

    private static void Ribbon(List<Vertex> o, SN.Vector3 a, SN.Vector3 b, float width, SN.Vector3 colour)
    {
        var along = b - a;
        if (along.LengthSquared() < 1e-4f) return;
        var side = SN.Vector3.Normalize(new SN.Vector3(-along.Z, 0f, along.X)) * (width / 2);
        // Each end extended by half the width, so that a street's segments meet at its bends.
        var ext = SN.Vector3.Normalize(along) * (width / 2);
        var (p, q) = (a - ext, b + ext);
        Triangle(o, p - side, q - side, q + side, SN.Vector3.UnitY, colour);
        Triangle(o, p - side, q + side, p + side, SN.Vector3.UnitY, colour);
    }

    // A footprint (x, z in the hall's frame) raised from bottom to top: its walls facing out, its roof.
    private static void Extrude(List<Vertex> o, List<SN.Vector2> ring, float bottom, float top, SN.Vector3 stone)
    {
        // Its signed area made positive in (x, z): the inside is then on the left of each edge, the outside on its right.
        if (Area(ring) < 0) ring.Reverse();
        var roof = new SN.Vector3(0.36f, 0.37f, 0.38f);
        for (var i = 0; i < ring.Count; i++)
        {
            var (p, q) = (ring[i], ring[(i + 1) % ring.Count]);
            var e = q - p;
            if (e.LengthSquared() < 1e-6f) continue;
            var outward = SN.Vector2.Normalize(new(e.Y, -e.X));
            var n = new SN.Vector3(outward.X, 0f, outward.Y);
            SN.Vector3 P(SN.Vector2 v, float y) => new(v.X, y, v.Y);
            Triangle(o, P(p, bottom), P(q, bottom), P(q, top), n, stone);
            Triangle(o, P(p, bottom), P(q, top), P(p, top), n, stone);
        }

        foreach (var (a, b, c) in Triangulate(ring))
        {
            Triangle(o, new(ring[a].X, top, ring[a].Y), new(ring[b].X, top, ring[b].Y), new(ring[c].X, top, ring[c].Y), SN.Vector3.UnitY, roof);
        }
    }

    private static float Area(List<SN.Vector2> ring)
    {
        var twice = 0f;
        for (var i = 0; i < ring.Count; i++)
        {
            var (p, q) = (ring[i], ring[(i + 1) % ring.Count]);
            twice += (p.X * q.Y) - (q.X * p.Y);
        }

        return twice / 2;
    }

    // Ear clipping of a polygon whose signed area is positive: n - 2 triangles, as indices; a ring that crosses itself
    // keeps what could be clipped.
    internal static List<(int A, int B, int C)> Triangulate(IReadOnlyList<SN.Vector2> ring)
    {
        static float Cross(SN.Vector2 a, SN.Vector2 b, SN.Vector2 c) => ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
        var triangles = new List<(int, int, int)>();
        var left = Enumerable.Range(0, ring.Count).ToList();
        var guard = ring.Count * ring.Count;
        while (left.Count > 3 && guard-- > 0)
        {
            var clipped = false;
            for (var i = 0; i < left.Count && !clipped; i++)
            {
                int a = left[(i + left.Count - 1) % left.Count], b = left[i], c = left[(i + 1) % left.Count];
                if (Cross(ring[a], ring[b], ring[c]) <= 0) continue;
                if (left.Any(k => k != a && k != b && k != c && Cross(ring[a], ring[b], ring[k]) >= 0 && Cross(ring[b], ring[c], ring[k]) >= 0 && Cross(ring[c], ring[a], ring[k]) >= 0)) continue;
                triangles.Add((a, b, c));
                left.RemoveAt(i);
                clipped = true;
            }

            if (!clipped) break;
        }

        if (left.Count == 3) triangles.Add((left[0], left[1], left[2]));
        return triangles;
    }

    // Checks without the network: a made-up answer with the Centre Block, Wellington Street, a building beside the hall
    // and one standing where the hall stands.
    internal static void SelfTest(Action<bool, string> check)
    {
        static string Node(SN.Vector2 en) => string.Create(CultureInfo.InvariantCulture,
            $"{{\"lat\":{Lat + (en.Y / 110_574.0):F8},\"lon\":{Lon + (en.X / (111_320.0 * Math.Cos(Lat * Math.PI / 180))):F8}}}");
        static string Ring(params SN.Vector2[] points) => "[" + string.Join(",", points.Append(points[0]).Select(Node)) + "]";
        static SN.Vector2 V(float e, float n) => new(e, n);
        var json = "{\"elements\":[" +
            $"{{\"type\":\"way\",\"tags\":{{\"building\":\"yes\",\"name\":\"Centre Block\",\"height\":\"30\"}},\"geometry\":{Ring(V(-40, 40), V(40, 40), V(40, 90), V(-40, 90))}}}," +
            $"{{\"type\":\"way\",\"tags\":{{\"highway\":\"primary\",\"name\":\"Wellington Street\"}},\"geometry\":[{Node(V(-200, 0))},{Node(V(200, 0))}]}}," +
            $"{{\"type\":\"way\",\"tags\":{{\"building\":\"yes\",\"building:levels\":\"4\"}},\"geometry\":{Ring(V(30, -40), V(50, -40), V(50, -25), V(30, -25))}}}," +
            $"{{\"type\":\"way\",\"tags\":{{\"building\":\"yes\"}},\"geometry\":{Ring(V(-6, -25), V(6, -25), V(6, -18), V(-6, -18))}}}" +
            "]}";
        var city = Build(json);
        var p = city.Placement;
        check(SN.Vector2.Distance(p.Door, V(0, -Setback)) < 0.05f && SN.Vector2.Distance(p.Out, SN.Vector2.UnitY) < 1e-3f,
            $"the door stands across Wellington from the Centre Block, facing it ({p.Door.X:F2}, {p.Door.Y:F2})");
        var block = p.ToHall(V(0, 65), 0f);
        check(MathF.Abs(block.X - HallShell.DoorX) < 0.05f && MathF.Abs(block.Z - (HallShell.Front + 0.16f + (79f * FreeCamera.Scale))) < 0.05f,
            $"out through the door, straight ahead, is the Centre Block, 79 m on at the hall's scale ({block.X:F2}, {block.Z:F2})");
        var east = p.ToHall(V(40, -32), 0f);
        check(east.X < HallShell.DoorX - (30f * FreeCamera.Scale), $"east is on the right going out: x falls ({east.X:F1})");
        check(city.Buildings == 2 && city.Left == 1 && city.Streets == 1, $"two buildings stand, the one where the hall is is left out ({city.Buildings}, {city.Left}), one street");
        var tall = city.Vertices.Where(v => v.Normal.Y > 0.9f && v.Pos.Y > 1f).Max(v => v.Pos.Y);
        check(MathF.Abs(tall - (30f * FreeCamera.Scale)) < 0.01f, $"a building's height is its tag's, at the hall's scale ({tall:F2})");
        var walls = city.Vertices.Where(v => MathF.Abs(v.Normal.Y) < 0.1f).ToList();
        var centreOfBlock = new SN.Vector3(block.X, 0f, block.Z);
        check(walls.Where(v => SN.Vector3.Distance(new(v.Pos.X, 0f, v.Pos.Z), centreOfBlock) < 60f).All(v =>
                SN.Vector3.Dot(v.Normal, new SN.Vector3(v.Pos.X, 0f, v.Pos.Z) - centreOfBlock) > 0f),
            "the walls face out of their building");
        var triangles = city.Vertices.Chunk(3).ToList();
        check(triangles.All(t => SN.Vector3.Dot(SN.Vector3.Cross(t[1].Pos - t[0].Pos, t[2].Pos - t[0].Pos), t[0].Normal) > 0f),
            "every triangle winds about its normal");
        var inHall = city.Vertices.Any(v => v.Pos.Y > 0.1f && InHall(new(v.Pos.X, v.Pos.Z)));
        check(!inHall, "nothing stands inside the hall");
        check(Triangulate([V(0, 0), V(4, 0), V(4, 1), V(1, 1), V(1, 4), V(0, 4)]).Count == 4, "an L-shaped roof is four triangles");
    }
}
