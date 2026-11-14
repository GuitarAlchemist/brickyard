using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Delta.SceneModules;
using SN = System.Numerics;
using static Delta.SceneModules.Widgets;

namespace Delta.AgentOffice;

// The IX workshop ("Atelier IX"): a stand in the hall, between Gaia and the arcade cabinet, in the whole hall's view
// (key 4), with a knot that IX computed turning on it and a plaque that says what IX says of it. IX does the
// mathematics and Delta draws: `brain -ix-knot figure-eight` asks IX's ix_knot for the knot and its 3D path and writes
// them as one versioned document (brain/ixatelier.go, format delta-ix-geometry v1), which this reads at most once a
// second, from DELTA_IX_ATELIER when set, else %LOCALAPPDATA%\delta\ix-atelier.json. A new document is redrawn while the
// window runs, as is this module's own code. The rope follows IX's path in 3D, so that each crossing shows which part
// is over as IX places it; its three strands and whipped ends are drawn on it (Laid), and nothing of the knot is
// computed here. The document is checked again against the brain's bounds: one that breaks them shows why on
// the plaque and no knot, as does a document that says IX was unavailable, or none at all.
//
// This assembly is replaced while the scene runs, so it keeps no static state: what it read lives in this instance.
public sealed class IxAtelier
{
    public const string Format = "delta-ix-geometry";
    public const int MaxBytes = 512 * 1024, MaxRopes = 8, MaxPoints = 4096, MaxText = 96, MaxCrossings = 64;
    public const double MaxCoord = 1000, MaxRadius = 100;
    public const float Size = 0.8f; // the knot's largest extent on the stand, in metres
    private const float X = 2.7f, Z = 0.2f, Yaw = 10f; // the stand's place in the hall, its plaque turned towards the whole hall's view
    private const float Top = 0.92f; // the stand's top

    public enum Standing { Absent, Shown, Unavailable, Refused }

    public sealed record Knot(string Id, string En, string Fr, string Closure, int Crossings, int Writhe, string Jones, float Radius, SN.Vector3[][] Ropes);

    // What the stand shows: a knot, or why there is none. Hash names the document's content, for the widgets' meshes.
    public sealed record Reading(Standing Standing, Knot? Knot, string Why, string Hash);

    private readonly DocumentWatch watch = new(DocumentWatch.Place("DELTA_IX_ATELIER", "ix-atelier.json"), MaxBytes);

    public string Path => watch.Path;

    public Reading Current { get; private set; } = new(Standing.Absent, null, "", "absent");

    public int Revision { get; private set; }

    // At most once a second: reads the document again when its time or length changed; true when what it shows changed.
    public bool Poll(double seconds)
    {
        if (watch.Poll(seconds) is not var (exists, length, bytes)) return false;
        var next = !exists ? new(Standing.Absent, null, "", "absent")
            : bytes is null ? Refuse($"{length} bytes, more than {MaxBytes}", $"big-{length}")
            : Read(bytes);
        if (next.Hash == Current.Hash) return false;
        Current = next;
        Revision++;
        return true;
    }

    // A document's content, checked: the knot it holds, or why it shows none.
    public static Reading Read(byte[] bytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
        if (bytes.Length > MaxBytes) return Refuse($"{bytes.Length} bytes, more than {MaxBytes}", hash);
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format"u8, out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != Format)
                return Refuse($"not a {Format} document", hash);
            if (!root.TryGetProperty("v"u8, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != 1)
                return Refuse($"version {(root.TryGetProperty("v"u8, out var w) ? w.ToString() : "none")}, not 1", hash);
            if (root.TryGetProperty("error"u8, out var error))
                return error.ValueKind == JsonValueKind.String && Text(error.GetString()!, 300) is { } why
                    ? new(Standing.Unavailable, null, why, hash) : Refuse("an error that is no text", hash);

            if (!root.TryGetProperty("knot"u8, out var k) || k.ValueKind != JsonValueKind.Object) return Refuse("no knot", hash);
            string? Field(string name) => k.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.String ? Text(f.GetString()!, MaxText) : null;
            int? Whole(string name) => k.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt32(out var n) ? n : null;
            var (id, en, fr, closure, jones) = (Field("id"), Field("en"), Field("fr"), Field("closure"), Field("jones"));
            if (id is not { Length: > 0 } || en is null || fr is null || closure is null || jones is null)
                return Refuse("the knot's id, names, closure or Jones polynomial is missing, too long or holds a control character", hash);
            if (Whole("crossings") is not { } crossings || Whole("writhe") is not { } writhe || crossings is < 0 or > MaxCrossings || Math.Abs(writhe) > MaxCrossings)
                return Refuse($"crossings or writhe missing or beyond ±{MaxCrossings}", hash);
            if (!root.TryGetProperty("radius"u8, out var r) || r.ValueKind != JsonValueKind.Number || r.GetDouble() is not (> 0 and <= MaxRadius))
                return Refuse($"a rope radius outside (0, {MaxRadius}]", hash);
            if (!root.TryGetProperty("ropes"u8, out var ropes) || ropes.ValueKind != JsonValueKind.Array || ropes.GetArrayLength() is < 1 or > MaxRopes)
                return Refuse($"not 1 to {MaxRopes} ropes", hash);

            var kept = new List<SN.Vector3[]>();
            var total = 0;
            foreach (var rope in ropes.EnumerateArray())
            {
                if (rope.ValueKind != JsonValueKind.Object || !rope.TryGetProperty("points"u8, out var points) || points.ValueKind != JsonValueKind.Array)
                    return Refuse("a rope without points", hash);
                var count = points.GetArrayLength();
                total += count;
                if (total > MaxPoints) return Refuse($"more than {MaxPoints} points", hash);
                if (count < 2) return Refuse("a rope of fewer than 2 points", hash);
                var path = new SN.Vector3[count];
                var i = 0;
                foreach (var p in points.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 3) return Refuse($"point {i} is not [x, y, z]", hash);
                    var c = new float[3];
                    var j = 0;
                    foreach (var n in p.EnumerateArray())
                    {
                        if (n.ValueKind != JsonValueKind.Number) return Refuse($"point {i} is not three numbers", hash);
                        if (n.GetDouble() is var d && (!double.IsFinite(d) || Math.Abs(d) > MaxCoord)) return Refuse($"point {i} lies beyond ±{MaxCoord}", hash);
                        c[j++] = (float)n.GetDouble();
                    }

                    path[i++] = new(c[0], c[1], c[2]);
                }

                kept.Add(path);
            }

            return new(Standing.Shown, new(id, en, fr, closure, crossings, writhe, jones, (float)r.GetDouble(), [.. kept]), "", hash);
        }
        catch (JsonException e)
        {
            return Refuse($"not JSON ({e.Message.Split('.')[0]})", hash);
        }
    }

    private static Reading Refuse(string why, string hash) => new(Standing.Refused, null, why, hash);

    private static string? Text(string s, int max) => s.Length <= max && !s.Any(char.IsControl) ? s : null;

    // The stand's and the knot's widgets for what is read now, in this language.
    public IEnumerable<Widget> Library(string language)
    {
        var reading = Current;
        var fr = language == "fr";
        yield return new(StandId, [new("doc", "?", 1, 40)], _ => Stand(reading, fr));
        yield return new(KnotId, [new("doc", "?", 1, 40)], _ => new(Laid(reading.Knot)));
    }

    public UsePiece Root(string language)
    {
        drawn = Current.Knot is not null;
        var doc = new Dictionary<string, WidgetValue> { ["doc"] = $"{language}-{Current.Hash}" };
        return new("ix-atelier", StandId, Place(X, 0f, Z, Yaw), doc);
    }

    private bool drawn; // the last scene has the knot: only then is its path posed, whatever was read since

    // The knot swings on its stand, 70° either side of its face every 16 s: its crossings face the hall most of the time,
    // and the swing shows which strand passes in front. A visitor walks round it for the rest.
    public void Pose(double seconds, IDictionary<string, Pose> poses)
    {
        if (drawn) poses["ix-atelier/knot"] = new(SN.Matrix4x4.CreateRotationY(70f * MathF.PI / 180f * MathF.Sin((float)(seconds % 16 / 16 * 2 * Math.PI))));
    }

    private static readonly WidgetId StandId = new("ix-stand", 1), KnotId = new("ix-knot", 1);
    private static readonly SN.Vector3 Dark = new(0.05f, 0.05f, 0.06f), Steel = new(0.2f, 0.21f, 0.22f), Board = new(0.02f, 0.02f, 0.025f),
        Amber = new(1f, 0.62f, 0.2f), Pale = new(0.84f, 0.86f, 0.9f), Red = new(1f, 0.45f, 0.35f);

    private Built Stand(Reading reading, bool fr)
    {
        string Say(string en, string frText) => fr ? frText : en;
        var lines = new List<(string Text, SN.Vector3 Color)>();
        switch (reading.Standing)
        {
            case Standing.Shown when reading.Knot is { } k:
                lines.Add((fr ? k.Fr : k.En, Pale));
                lines.Add(($"{k.Closure} · {k.Crossings} {Say("crossings", "croisements")} · writhe {k.Writhe}", Pale));
                lines.Add(("Jones : " + k.Jones, Pale));
                lines.Add((Say("path computed by IX (ix_knot)", "chemin calculé par IX (ix_knot)"), Amber));
                lines.Add((Say("three-strand rope drawn on it, not simulated", "corde à 3 torons dessinée dessus, pas simulée"), new(0.6f, 0.62f, 0.66f)));
                break;
            case Standing.Unavailable:
                lines.Add((Say("IX unavailable", "IX indisponible"), Red));
                lines.AddRange(Wrap(reading.Why).Select(l => (l, Pale)));
                break;
            case Standing.Refused:
                lines.Add((Say("Geometry refused", "Géométrie refusée"), Red));
                lines.AddRange(Wrap(reading.Why).Select(l => (l, Pale)));
                break;
            default:
                lines.Add((Say("Waiting for IX", "En attente d'IX"), Pale));
                lines.Add(("brain -ix-knot figure-eight", new(0.6f, 0.62f, 0.66f)));
                break;
        }

        var pieces = new List<Piece>
        {
            new BoxPiece(new(0, 0.02f, 0), new(0.5f, 0.04f, 0.5f), Dark, 1),
            new BoxPiece(new(0, 0.47f, 0), new(0.42f, 0.86f, 0.42f), Dark, 1),
            new BoxPiece(new(0, Top - 0.01f, 0), new(0.5f, 0.02f, 0.5f), Steel, 3),
            new BoxPiece(new(0, 0.55f, 0.213f), new(0.4f, 0.5f, 0.006f), Board, 0),
            new TextPiece("Atelier IX", new(0, 0.74f, 0.218f), 0.2f, Amber),
        };
        for (var i = 0; i < Math.Min(lines.Count, 6); i++)
        {
            var (text, color) = lines[i];
            pieces.Add(new TextPiece(text, new(0, 0.66f - 0.06f * i, 0.218f), Math.Clamp(text.Length * 0.0095f, 0.03f, 0.37f), color));
        }

        if (reading.Knot is not null)
            pieces.Add(new UsePiece("knot", KnotId, SN.Matrix4x4.CreateTranslation(0, Top + 0.04f + Size / 2, 0), new Dictionary<string, WidgetValue> { ["doc"] = reading.Hash }));
        return new(pieces);
    }

    // At most three lines of 46 characters.
    private static IEnumerable<string> Wrap(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = "";
        var count = 0;
        foreach (var word in words)
        {
            var w = word.Length > 46 ? word[..45] + "…" : word;
            if (line.Length + w.Length + 1 > 46 && line.Length > 0)
            {
                yield return line;
                if (++count == 3) yield break;
                line = "";
            }

            line = line.Length == 0 ? w : line + " " + w;
        }

        if (line.Length > 0) yield return line;
    }

    // IX's path as a laid rope about the knot's centre, scaled so that its largest extent is Size and its radius IX's:
    // three strands twisted round IX's path (the path smoothed through IX's points, never moved off them), the hemp's
    // fibres and the grooves between strands in their colour, and each end whipped with red twine. Where two parts of
    // the rope cross, IX's depth puts one in front, as a rope is. The twist and the whipping are drawn, not simulated.
    public static IReadOnlyList<Piece> Laid(Knot? knot)
    {
        if (knot is null) return [];
        var all = knot.Ropes.SelectMany(r => r).ToArray();
        var low = all.Aggregate(SN.Vector3.Min);
        var high = all.Aggregate(SN.Vector3.Max);
        var extent = high - low;
        var scale = Size / MathF.Max(1e-6f, MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)));
        var centre = (low + high) / 2;
        var radius = Math.Clamp(knot.Radius * scale, 0.004f, 0.05f);
        var (strand, offset) = (radius * 0.464f, radius * 0.536f); // three round strands touching, within the rope's radius
        var pitch = 2 * MathF.PI * offset / MathF.Tan(Lay * MathF.PI / 180f);
        var triangles = new List<ModuleTriangle>();
        var length = knot.Ropes.Sum(r => r.Zip(r.Skip(1), (a, b) => SN.Vector3.Distance(a, b)).Sum()) * scale;
        var step = MathF.Max(pitch / 24, length / MaxRings);
        foreach (var path in knot.Ropes)
        {
            var axis = Resample(Smooth([.. path.Select(p => (p - centre) * scale)]), step);
            if (axis.Length < 2) continue;
            var (t, n, b) = Frames(axis);
            for (var k = 0; k < 3; k++)
            {
                var centres = new SN.Vector3[axis.Length];
                var outward = new SN.Vector3[axis.Length];
                for (var j = 0; j < axis.Length; j++)
                {
                    var turn = (2 * MathF.PI * j * step / pitch) + (k * 2 * MathF.PI / 3);
                    outward[j] = (MathF.Cos(turn) * n[j]) + (MathF.Sin(turn) * b[j]);
                    centres[j] = axis[j] + (offset * outward[j]);
                }

                var shade = k switch { 0 => 1f, 1 => 0.95f, _ => 1.04f };
                Tube(triangles, centres, strand, 10, (j, normal, around) =>
                {
                    var groove = SN.Vector3.Dot(normal, outward[j]); // 1 on the rope's outside, -1 where the strands press together
                    var fibre = 0.92f + (0.08f * MathF.Sin((3 * around) + (j * 1.7f)));
                    return Hemp * (shade * fibre * (0.5f + (0.5f * Smooth01((groove + 0.7f) / 1.4f))));
                });
            }

            Whip(triangles, axis, t, n, b, radius, step, atEnd: false);
            Whip(triangles, axis, t, n, b, radius, step, atEnd: true);
        }

        return triangles.Count == 0 ? [] : [new SurfacePiece(triangles)];
    }

    public const float Lay = 30f; // the strands' angle to the rope, in degrees
    private const int MaxRings = 1500; // along all the ropes, for the strands

    private static readonly SN.Vector3 Hemp = new(0.76f, 0.63f, 0.43f), Twine = new(0.62f, 0.13f, 0.1f);

    private static float Smooth01(float x) => Math.Clamp(x, 0, 1) is var c ? c * c * (3 - (2 * c)) : 0;

    // A Catmull-Rom curve through every point of the path, four pieces to each segment.
    private static SN.Vector3[] Smooth(SN.Vector3[] p)
    {
        if (p.Length < 3) return p;
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

    // Points every step along a polyline, its last point included.
    private static SN.Vector3[] Resample(SN.Vector3[] p, float step)
    {
        var points = new List<SN.Vector3> { p[0] };
        var travelled = 0f;
        var next = step;
        for (var i = 1; i < p.Length; i++)
        {
            var segment = SN.Vector3.Distance(p[i - 1], p[i]);
            for (; segment > 0 && next <= travelled + segment; next += step) points.Add(SN.Vector3.Lerp(p[i - 1], p[i], (next - travelled) / segment));
            travelled += segment;
        }

        if (SN.Vector3.Distance(points[^1], p[^1]) > step / 4) points.Add(p[^1]);
        return [.. points];
    }

    // Frames along a curve that do not twist about it (parallel transport): tangent T, normal N, and B = T × N.
    private static (SN.Vector3[] T, SN.Vector3[] N, SN.Vector3[] B) Frames(SN.Vector3[] p)
    {
        var (t, n, b) = (new SN.Vector3[p.Length], new SN.Vector3[p.Length], new SN.Vector3[p.Length]);
        for (var i = 0; i < p.Length; i++) t[i] = SN.Vector3.Normalize(p[Math.Min(p.Length - 1, i + 1)] - p[Math.Max(0, i - 1)]);
        var first = SN.Vector3.Cross(t[0], MathF.Abs(t[0].Y) < 0.9f ? SN.Vector3.UnitY : SN.Vector3.UnitX);
        for (var i = 0; i < p.Length; i++)
        {
            var carried = i == 0 ? first : n[i - 1];
            var along = carried - (t[i] * SN.Vector3.Dot(carried, t[i]));
            n[i] = along.LengthSquared() > 1e-12f ? SN.Vector3.Normalize(along) : (i == 0 ? SN.Vector3.Normalize(first) : n[i - 1]);
            b[i] = SN.Vector3.Cross(t[i], n[i]);
        }

        return (t, n, b);
    }

    // A tube of the given radius along centres, its triangles wound about its outward normals; colour(ring, normal,
    // angle) colours each vertex.
    private static void Tube(List<ModuleTriangle> into, SN.Vector3[] centres, float radius, int sides, Func<int, SN.Vector3, float, SN.Vector3> colour)
    {
        var (_, n, b) = Frames(centres);
        ModuleVertex At(int j, int k)
        {
            var around = 2 * MathF.PI * k / sides;
            var normal = (MathF.Cos(around) * n[j]) + (MathF.Sin(around) * b[j]);
            return new(centres[j] + (radius * normal), normal, colour(j, normal, around));
        }

        for (var j = 0; j < centres.Length - 1; j++)
        {
            for (var k = 0; k < sides; k++)
            {
                var (p00, p01, p10, p11) = (At(j, k), At(j, k + 1), At(j + 1, k), At(j + 1, k + 1));
                Add(into, new(p00, p01, p11));
                Add(into, new(p00, p11, p10));
            }
        }
    }

    // A triangle as Widgets.Expand takes it, or none: where a tight bend folds the inside of a tube over itself, the
    // triangles turned inside out are left out rather than refusing the rope.
    private static void Add(List<ModuleTriangle> into, ModuleTriangle t)
    {
        var cross = SN.Vector3.Cross(t.B.Position - t.A.Position, t.C.Position - t.A.Position);
        if (cross.LengthSquared() > 1e-14f && SN.Vector3.Dot(cross, t.A.Normal) > 0 && SN.Vector3.Dot(cross, t.B.Normal) > 0 && SN.Vector3.Dot(cross, t.C.Normal) > 0)
            into.Add(t);
    }

    // A whipping: red twine wound tight over the rope's last two diameters, in turns, closed at the rope's end.
    private static void Whip(List<ModuleTriangle> into, SN.Vector3[] axis, SN.Vector3[] t, SN.Vector3[] n, SN.Vector3[] b, float radius, float step, bool atEnd)
    {
        var reach = Math.Min(axis.Length - 1, Math.Max(1, (int)MathF.Ceiling(4 * radius / step)));
        var (from, to) = atEnd ? (axis.Length - 1 - reach, axis.Length - 1) : (0, reach);
        var count = (to - from) * 6;
        var centres = new SN.Vector3[count + 1];
        var span = SN.Vector3.Distance(axis[from], axis[to]);
        for (var i = 0; i <= count; i++)
        {
            var at = from + ((to - from) * i / (float)count);
            var j = Math.Min(to - 1, (int)at);
            centres[i] = SN.Vector3.Lerp(axis[j], axis[j + 1], at - j);
        }

        var turns = Math.Max(4, (int)(span / 0.0018f));
        var wrap = radius * 1.05f;
        Tube(into, centres, wrap, 12, (i, _, _) => Twine * (0.8f + (0.2f * MathF.Abs(MathF.Sin(MathF.PI * turns * i / count)))));
        var (end, tangent) = atEnd ? (centres[^1], t[to]) : (centres[0], -t[from]);
        var (cn, cb) = (n[atEnd ? to : from], b[atEnd ? to : from]);
        for (var k = 0; k < 12; k++)
        {
            var (a0, a1) = (2 * MathF.PI * k / 12, 2 * MathF.PI * (k + 1) / 12);
            var p0 = end + (wrap * ((MathF.Cos(a0) * cn) + (MathF.Sin(a0) * cb)));
            var p1 = end + (wrap * ((MathF.Cos(a1) * cn) + (MathF.Sin(a1) * cb)));
            var colour = Twine * 0.7f; // the cut end, the strands under the twine
            ModuleVertex V(SN.Vector3 p) => new(p, tangent, colour);
            Add(into, atEnd ? new(V(end), V(p0), V(p1)) : new(V(end), V(p1), V(p0)));
        }
    }
}
