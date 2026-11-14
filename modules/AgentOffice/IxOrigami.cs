using System.Security.Cryptography;
using System.Text.Json;
using Delta.SceneModules;
using SN = System.Numerics;
using static Delta.SceneModules.Widgets;

namespace Delta.AgentOffice;

// The IX workshop's origami table, beside the knot's stand: a fold that IX checked. `brain -ix-origami FILE` asks IX's
// ix_origami_check about a .origami sequence or a FOLD object and writes the verdict as one versioned document
// (brain/ixorigami.go, format delta-ix-origami v1), read here at most once a second from DELTA_IX_ORIGAMI when set,
// else %LOCALAPPDATA%\delta\ix-origami.json. A board leans on the table with the crease pattern, mountains red and
// valleys blue; beside it the folded state, where IX's folded frame puts the faces, stands as a paper model with its
// layers in the order the document's face orders give (Sheet), or, without them, lies flat on a second board. Nothing
// is interpolated between pattern and model and nothing simulated; the plaque says so. A document with a motion (the
// sheet folding, Delta's closed form, every face kept rigid: Folds) shows instead the one sheet folding and unfolding,
// each face an instance turned rigidly between the motion's frames; the plaque says the motion is Delta's and the flat
// fold IX's. The plaque gives IX's verdict
// and each of its findings in words, and the boards mark in amber the edges and vertices a finding names. A document that breaks the
// bounds shows why, one that says IX was unavailable says so, and none at all gives the command.
//
// This assembly is replaced while the scene runs, so it keeps no static state: what it read lives in this instance.
public sealed class IxOrigami
{
    public const string Format = "delta-ix-origami";
    public const int MaxBytes = 1 << 20, MaxVertices = 1024, MaxEdges = 2048, MaxFindings = 32, MaxIds = 64, MaxText = 120;
    public const int MaxFaces = 1024, MaxFaceSize = 64, MaxOrders = 4096;
    public const double MaxCoord = 16;
    public const float Board = 0.3f; // a board's drawing, its largest extent in metres
    public const float Model = 0.36f; // the folded model's largest extent, in metres
    public const float Ply = 0.0005f; // between two layers of the folded model; the paper's two sides are a fifth of it apart
    private const float X = 3.45f, Z = 0.55f, Yaw = 0f; // right of the knot's stand, towards the whole hall's view
    private const float Top = 0.82f, Lean = -35f; // the table's top; the boards lean back by this much

    public enum Standing { Absent, Shown, Unavailable, Refused }

    public sealed record Finding(string Rule, string Where, int[] Ids, int Count, double Value, string Detail);

    public sealed record Fold(string Name, bool Ok, bool? Holds, string[] RejectedBy, int MaxLayers, SN.Vector2[] Vertices,
        (int A, int B)[] Edges, string[] Assignment, SN.Vector2[]? Folded, Finding[] Findings)
    {
        // The pattern's faces, each its vertices in order, and the folded state's face orders: S = 1 when face F lies on
        // the side face G's normal points to, -1 opposite. With both and a folded state, the table shows the model.
        public int[][] Faces { get; init; } = [];
        public (int F, int G, int S)[] Orders { get; init; } = [];

        public bool Modelled => Folded is not null && Faces.Length > 0 && Orders.Length > 0;

        // Delta's illustrative 3D pose, not IX's: parts turned about a ridge (brain/ixorigami.go, origamiPose).
        public Posing? Pose { get; init; }

        // The sheet folding, Delta's and not IX's: each frame the pattern's vertices in 3D, every face keeping its shape
        // (brain/ixorigami.go, origamiMotion). With it the table shows the one sheet folding, face by face (Folds).
        public SN.Vector3[][]? Motion { get; init; }

        public bool Moving => Motion is not null && Faces.Length > 0;
    }

    public const int MaxFrames = 64, MaxMotionFaces = 256;
    public const float Rigid = 2e-3f; // how far a face's vertices may come apart or together in a frame, in the pattern's units

    // A ridge through two vertices of the folded state, its up side to the left from A to B, and parts of the faces turned
    // about it: each to To degrees from up, towards the side of the stack its layers are on; a Spread part's two halves
    // of layers each towards their own side.
    public sealed record Posing((int A, int B) Ridge, PosePart[] Parts);

    public sealed record PosePart(int[] Faces, float To, bool Spread);

    public const int MaxParts = 8;

    public sealed record Reading(Standing Standing, Fold? Fold, string Why, string Hash);

    private readonly DocumentWatch watch = new(DocumentWatch.Place("DELTA_IX_ORIGAMI", "ix-origami.json"), MaxBytes);

    public Reading Current { get; private set; } = new(Standing.Absent, null, "", "absent");

    public int Revision { get; private set; }

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

    public static Reading Read(byte[] bytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
        if (bytes.Length > MaxBytes) return Refuse($"{bytes.Length} bytes, more than {MaxBytes}", hash);
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "format") != Format) return Refuse($"not a {Format} document", hash);
            if (!root.TryGetProperty("v"u8, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != 1)
                return Refuse($"version {(root.TryGetProperty("v"u8, out var w) ? w.ToString() : "none")}, not 1", hash);
            if (root.TryGetProperty("error"u8, out var error))
                return error.ValueKind == JsonValueKind.String && Text(error.GetString()!, 300) is { } why
                    ? new(Standing.Unavailable, null, why, hash) : Refuse("an error that is no text", hash);

            var name = root.TryGetProperty("name"u8, out var n) ? (n.ValueKind == JsonValueKind.String ? Text(n.GetString()!, MaxText) : null) : "";
            if (name is null) return Refuse("a name too long or with a control character", hash);
            if (!root.TryGetProperty("ok"u8, out var okay) || okay.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Refuse("no verdict", hash);
            bool? holds = null;
            if (root.TryGetProperty("holds"u8, out var h))
            {
                if (h.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Refuse("holds is neither true nor false", hash);
                holds = h.GetBoolean();
            }

            var rejected = new List<string>();
            if (root.TryGetProperty("rejected_by"u8, out var rb))
            {
                if (rb.ValueKind != JsonValueKind.Array || rb.GetArrayLength() > MaxFindings) return Refuse("rejected_by is not a short list", hash);
                foreach (var r in rb.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.String || Text(r.GetString()!, MaxText) is not { } rule) return Refuse("a rejecting rule that is no short text", hash);
                    rejected.Add(rule);
                }
            }

            var layers = root.TryGetProperty("max_layers"u8, out var ml) && ml.ValueKind == JsonValueKind.Number && ml.TryGetInt32(out var m) && m is >= 0 and <= 100000 ? m : 0;
            if (!root.TryGetProperty("pattern"u8, out var p) || p.ValueKind != JsonValueKind.Object) return Refuse("no crease pattern", hash);
            if (Points(p, "vertices") is not { } vertices) return Refuse($"the pattern's vertices are not 1 to {MaxVertices} points within ±{MaxCoord}", hash);
            if (!p.TryGetProperty("edges"u8, out var es) || es.ValueKind != JsonValueKind.Array || es.GetArrayLength() is < 1 or > MaxEdges
                || !p.TryGetProperty("assignment"u8, out var asg) || asg.ValueKind != JsonValueKind.Array || asg.GetArrayLength() != es.GetArrayLength())
                return Refuse($"not 1 to {MaxEdges} edges, each with its assignment", hash);
            var edges = new List<(int, int)>();
            foreach (var e in es.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 2 || Index(e[0], vertices.Length) is not { } a || Index(e[1], vertices.Length) is not { } b)
                    return Refuse($"edge {edges.Count} joins no two of the {vertices.Length} vertices", hash);
                edges.Add((a, b));
            }

            var assignment = new List<string>();
            foreach (var a in asg.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.String || a.GetString() is not ("M" or "V" or "B" or "F" or "U" or "C" or "J")) return Refuse($"edge {assignment.Count}'s assignment is not M, V, B, F, U, C or J", hash);
                assignment.Add(a.GetString()!);
            }

            var faces = new List<int[]>();
            if (p.TryGetProperty("faces"u8, out var fl))
            {
                if (fl.ValueKind != JsonValueKind.Array || fl.GetArrayLength() > MaxFaces) return Refuse($"more than {MaxFaces} faces", hash);
                foreach (var face in fl.EnumerateArray())
                {
                    if (face.ValueKind != JsonValueKind.Array || face.GetArrayLength() is < 3 or > MaxFaceSize) return Refuse($"face {faces.Count} is not 3 to {MaxFaceSize} vertices", hash);
                    var corners = new List<int>();
                    foreach (var c in face.EnumerateArray())
                    {
                        if (Index(c, vertices.Length) is not { } corner) return Refuse($"face {faces.Count} names no vertex of the {vertices.Length}", hash);
                        corners.Add(corner);
                    }

                    faces.Add([.. corners]);
                }
            }

            SN.Vector2[]? folded = null;
            if (root.TryGetProperty("folded"u8, out _))
            {
                folded = Points(root, "folded");
                if (folded is null || folded.Length != vertices.Length) return Refuse("the folded state's vertices are not the pattern's, within bounds", hash);
            }

            var orders = new List<(int, int, int)>();
            if (root.TryGetProperty("orders"u8, out var ol))
            {
                if (ol.ValueKind != JsonValueKind.Array || ol.GetArrayLength() > MaxOrders || folded is null || faces.Count == 0)
                    return Refuse($"face orders without faces and a folded state, or more than {MaxOrders}", hash);
                foreach (var o in ol.EnumerateArray())
                {
                    if (o.ValueKind != JsonValueKind.Array || o.GetArrayLength() != 3 || Index(o[0], faces.Count) is not { } f || Index(o[1], faces.Count) is not { } g || f == g
                        || o[2].ValueKind != JsonValueKind.Number || !o[2].TryGetInt32(out var s) || s is not (1 or -1))
                        return Refuse($"face order {orders.Count} is not [f, g, ±1] of the {faces.Count} faces", hash);
                    orders.Add((f, g, s));
                }
            }

            Posing? pose = null;
            if (root.TryGetProperty("pose"u8, out var po))
            {
                if (po.ValueKind != JsonValueKind.Object || orders.Count == 0 || !po.TryGetProperty("ridge"u8, out var rr) || rr.ValueKind != JsonValueKind.Array
                    || rr.GetArrayLength() != 2 || Index(rr[0], vertices.Length) is not { } ridgeA || Index(rr[1], vertices.Length) is not { } ridgeB
                    || SN.Vector2.Distance(folded![ridgeA], folded[ridgeB]) < 1e-6f)
                    return Refuse("a pose without the orders, or without a ridge through two vertices apart", hash);
                if (!po.TryGetProperty("parts"u8, out var pl) || pl.ValueKind != JsonValueKind.Array || pl.GetArrayLength() is < 1 or > MaxParts)
                    return Refuse($"a pose of not 1 to {MaxParts} parts", hash);
                var taken = new HashSet<int>();
                var parts = new List<PosePart>();
                foreach (var part in pl.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("faces"u8, out var pf) || pf.ValueKind != JsonValueKind.Array
                        || pf.GetArrayLength() is < 1 or > MaxFaces || !part.TryGetProperty("to"u8, out var to) || to.ValueKind != JsonValueKind.Number
                        || to.GetDouble() is not (>= 0 and <= 180))
                        return Refuse($"pose part {parts.Count} has no faces, or turns beyond 0 to 180°", hash);
                    var ids = new List<int>();
                    foreach (var x in pf.EnumerateArray())
                    {
                        if (Index(x, faces.Count) is not { } face || !taken.Add(face)) return Refuse($"pose part {parts.Count} names a face not there or in another part", hash);
                        ids.Add(face);
                    }

                    parts.Add(new([.. ids], (float)to.GetDouble(), part.TryGetProperty("spread"u8, out var sp) && sp.ValueKind == JsonValueKind.True));
                }

                pose = new((ridgeA, ridgeB), [.. parts]);
            }

            SN.Vector3[][]? motion = null;
            if (root.TryGetProperty("motion"u8, out var mo))
            {
                if (mo.ValueKind != JsonValueKind.Object || faces.Count is 0 or > MaxMotionFaces || !mo.TryGetProperty("frames"u8, out var fr)
                    || fr.ValueKind != JsonValueKind.Array || fr.GetArrayLength() is < 2 or > MaxFrames)
                    return Refuse($"a motion without faces (1 to {MaxMotionFaces}), or not of 2 to {MaxFrames} frames", hash);
                var frames = new List<SN.Vector3[]>();
                foreach (var frame in fr.EnumerateArray())
                {
                    if (frame.ValueKind != JsonValueKind.Array || frame.GetArrayLength() != vertices.Length) return Refuse($"motion frame {frames.Count} does not hold the pattern's {vertices.Length} vertices", hash);
                    var at = new List<SN.Vector3>();
                    foreach (var q in frame.EnumerateArray())
                    {
                        if (q.ValueKind != JsonValueKind.Array || q.GetArrayLength() != 3 || q.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.Number || !double.IsFinite(c.GetDouble()) || Math.Abs(c.GetDouble()) > MaxCoord))
                            return Refuse($"motion frame {frames.Count}: a vertex that is not 3 numbers within ±{MaxCoord}", hash);
                        at.Add(new((float)q[0].GetDouble(), (float)q[1].GetDouble(), (float)q[2].GetDouble()));
                    }

                    foreach (var face in faces)
                    {
                        for (var a = 0; a < face.Length; a++)
                        {
                            for (var b = a + 1; b < face.Length; b++)
                            {
                                if (MathF.Abs(SN.Vector3.Distance(at[face[a]], at[face[b]]) - SN.Vector2.Distance(vertices[face[a]], vertices[face[b]])) > Rigid)
                                    return Refuse($"motion frame {frames.Count} changes face {faces.IndexOf(face)}'s shape", hash);
                            }
                        }
                    }

                    frames.Add([.. at]);
                }

                motion = [.. frames];
            }

            var findings = new List<Finding>();
            if (root.TryGetProperty("findings"u8, out var fs))
            {
                if (fs.ValueKind != JsonValueKind.Array || fs.GetArrayLength() > MaxFindings) return Refuse($"more than {MaxFindings} findings", hash);
                foreach (var f in fs.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object || Str(f, "rule") is not { Length: > 0 } rule || Text(rule, MaxText) is null) return Refuse("a finding without its rule", hash);
                    var where = Str(f, "where") ?? "";
                    var limit = where switch { "edge" => edges.Count, "vertex" => vertices.Length, "" => -1, _ => -2 };
                    if (limit == -2) return Refuse($"a finding placed on {where}", hash);
                    var ids = new List<int>();
                    if (f.TryGetProperty("ids"u8, out var idList))
                    {
                        if (idList.ValueKind != JsonValueKind.Array || idList.GetArrayLength() > MaxIds || limit < 0 && idList.GetArrayLength() > 0) return Refuse($"{rule}: ids that name nothing", hash);
                        foreach (var id in idList.EnumerateArray())
                        {
                            if (Index(id, limit) is not { } i) return Refuse($"{rule} names {where} {id} of {limit}", hash);
                            ids.Add(i);
                        }
                    }

                    var count = f.TryGetProperty("count"u8, out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var k) && k >= 0 ? k : 0;
                    var value = f.TryGetProperty("value"u8, out var val) && val.ValueKind == JsonValueKind.Number && double.IsFinite(val.GetDouble()) ? val.GetDouble() : 0;
                    if (Str(f, "detail") is { } detail && Text(detail, MaxText) is null) return Refuse($"{rule}: a detail too long or with a control character", hash);
                    findings.Add(new(rule, where, [.. ids], count, value, Str(f, "detail") ?? ""));
                }
            }

            var fold = new Fold(name, okay.GetBoolean(), holds, [.. rejected], layers, vertices, [.. edges], [.. assignment], folded, [.. findings]) { Faces = [.. faces], Orders = [.. orders], Pose = pose, Motion = motion };
            return new(Standing.Shown, fold, "", hash);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return Refuse($"not JSON ({e.Message.Split('.')[0]})", hash);
        }
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;

    private static int? Index(JsonElement e, int count) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i) && i >= 0 && i < count ? i : null;

    private static SN.Vector2[]? Points(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > MaxVertices) return null;
        var points = new List<SN.Vector2>();
        foreach (var p in list.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 2 || p[0].ValueKind != JsonValueKind.Number || p[1].ValueKind != JsonValueKind.Number) return null;
            double x = p[0].GetDouble(), y = p[1].GetDouble();
            if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > MaxCoord || Math.Abs(y) > MaxCoord) return null;
            points.Add(new((float)x, (float)y));
        }

        return [.. points];
    }

    private static Reading Refuse(string why, string hash) => new(Standing.Refused, null, why, hash);

    private static string? Text(string s, int max) => s.Length <= max && !s.Any(char.IsControl) ? s : null;

    public IEnumerable<Widget> Library(string language)
    {
        var reading = Current;
        var fr = language == "fr";
        yield return new(TableId, [new("doc", "?", 1, 40)], _ => Table(reading, fr));
        yield return new(BoardId, [new("doc", "?", 1, 40), new("folded", 0, 0, 1)], a => new(Drawing(reading.Fold, a.Number("folded") > 0.5)));
        yield return new(ModelId, [new("doc", "?", 1, 40)], _ => new(reading.Fold is { Moving: true } f ? Faces(f, reading.Hash) : Sheet(reading.Fold)));
        yield return new(FaceId, [new("doc", "?", 1, 40), new("face", 0, 0, MaxMotionFaces - 1)], a => new(Face(reading.Fold, (int)a.Number("face"))));
    }

    public UsePiece Root(string language)
    {
        drawn = Current.Fold is { Modelled: true } or { Moving: true };
        folds = Current.Fold is { Moving: true } f ? Folds.Of(f) : null;
        return new("ix-origami", TableId, Place(X, 0f, Z, Yaw), new Dictionary<string, WidgetValue> { ["doc"] = $"{language}-{Current.Hash}" });
    }

    private bool drawn; // the last scene has the model: only then is it posed
    private Folds? folds; // and the faces' turns, when the sheet folds

    // The model turns on its rod, 60° either side of its face every 20 s, so that its layers show. A sheet with its
    // motion also folds and unfolds, every Cycle seconds, each face where the motion puts it.
    public void Pose(double seconds, IDictionary<string, Pose> poses)
    {
        if (!drawn) return;
        poses["ix-origami/model"] = new(SN.Matrix4x4.CreateRotationY(60f * MathF.PI / 180f * MathF.Sin((float)(seconds % 20 / 20 * 2 * Math.PI))));
        if (folds is null) return;
        var at = folds.At(HowFolded(seconds));
        for (var i = 0; i < at.Length; i++) poses[$"ix-origami/model/f{i}"] = new(at[i]);
    }

    public const float Cycle = 24f;

    // How far the sheet is folded at this moment, 0 flat to 1 at the motion's last frame: flat for 3 s, folding for 8,
    // folded for 4, unfolding for 8, flat again for 1; eased at both ends of each move.
    public static float HowFolded(double seconds)
    {
        var t = (float)(seconds % Cycle);
        static float Ease(float x) => x * x * (3 - (2 * x));
        return t switch
        {
            < 3f => 0f,
            < 11f => Ease((t - 3f) / 8f),
            < 15f => 1f,
            < 23f => 1f - Ease((t - 15f) / 8f),
            _ => 0f,
        };
    }

    private static readonly WidgetId TableId = new("ix-origami-table", 1), BoardId = new("ix-origami-board", 2), ModelId = new("ix-origami-model", 1),
        FaceId = new("ix-origami-face", 1);
    private static readonly SN.Vector3 Dark = new(0.05f, 0.05f, 0.06f), Steel = new(0.2f, 0.21f, 0.22f), Plaque = new(0.02f, 0.02f, 0.025f),
        Paper = new(0.88f, 0.86f, 0.8f), Mountain = new(0.9f, 0.22f, 0.18f), Valley = new(0.2f, 0.45f, 0.95f), Edge = new(0.25f, 0.25f, 0.27f),
        Other = new(0.55f, 0.55f, 0.55f), Amber = new(1f, 0.62f, 0.2f), Green = new(0.35f, 0.9f, 0.45f), Red = new(1f, 0.45f, 0.35f),
        Pale = new(0.84f, 0.86f, 0.9f), Grey = new(0.6f, 0.62f, 0.66f), Kami = new(0.68f, 0.1f, 0.11f), Cream = new(0.93f, 0.9f, 0.83f);

    private static Built Table(Reading reading, bool fr)
    {
        string Say(string en, string frText) => fr ? frText : en;
        var lines = new List<(string Text, SN.Vector3 Color)>();
        var title = "Origami";
        switch (reading.Standing)
        {
            case Standing.Shown when reading.Fold is { } f:
                title = f.Name.Length > 0 ? $"Origami · {f.Name}" : "Origami";
                if (!f.Ok) lines.Add((Say("Rejected by IX: ", "Refusé par IX : ") + (f.RejectedBy.Length > 0 ? string.Join(", ", f.RejectedBy) : "?"), Red));
                else if (f.Holds == false) lines.Add((Say("Valid fold, an expectation not held", "Pli valide, attente non tenue"), Amber));
                else lines.Add((Say("Valid according to IX", "Valide selon IX"), Green));
                lines.AddRange(f.Findings.Take(5).Select(x => (Explain(x, fr), Pale)));
                if (f.Findings.Length > 5) lines.Add((Say($"+{f.Findings.Length - 5} more", $"+{f.Findings.Length - 5} autres"), Grey));
                lines.Add((Say($"at most {f.MaxLayers} layers · checked by IX, not simulated", $"{f.MaxLayers} couches au plus · vérifié par IX, pas simulé"), Grey));
                if (f.Moving) lines.Add((Say("the folding: exact closed form, Delta's · IX checks the flat fold", "le pliage : formule exacte, de Delta · IX vérifie le pli à plat"), Grey));
                break;
            case Standing.Unavailable:
                lines.Add((Say("IX unavailable", "IX indisponible"), Red));
                lines.AddRange(Wrap(reading.Why, 60).Select(l => (l, Pale)));
                break;
            case Standing.Refused:
                lines.Add((Say("Fold refused", "Pli refusé"), Red));
                lines.AddRange(Wrap(reading.Why, 60).Select(l => (l, Pale)));
                break;
            default:
                lines.Add((Say("Waiting for IX", "En attente d'IX"), Pale));
                lines.Add(("brain -ix-origami <.origami | FOLD>", Grey));
                break;
        }

        var pieces = new List<Piece>
        {
            new BoxPiece(new(0, 0.02f, 0), new(0.86f, 0.04f, 0.44f), Dark, 1),
            new BoxPiece(new(0, 0.41f, 0), new(0.78f, 0.78f, 0.36f), Dark, 1),
            new BoxPiece(new(0, Top - 0.01f, 0), new(0.86f, 0.02f, 0.44f), Steel, 3),
            new BoxPiece(new(0, 0.44f, 0.183f), new(0.74f, 0.56f, 0.006f), Plaque, 0),
            new TextPiece(title.Length > 48 ? title[..47] + "…" : title, new(0, 0.66f, 0.188f), Math.Clamp(title.Length * 0.011f, 0.08f, 0.6f), Amber),
        };
        for (var i = 0; i < Math.Min(lines.Count, 8); i++)
        {
            var (text, color) = lines[i];
            pieces.Add(new TextPiece(text, new(0, 0.6f - 0.052f * i, 0.188f), Math.Clamp(text.Length * 0.0082f, 0.03f, 0.7f), color));
        }

        if (reading.Fold is { } fold)
        {
            var lean = SN.Matrix4x4.CreateRotationX(Lean * MathF.PI / 180f);
            pieces.Add(new UsePiece("pattern", BoardId, lean * SN.Matrix4x4.CreateTranslation(-0.2f, Top, 0.02f), new Dictionary<string, WidgetValue> { ["doc"] = reading.Hash, ["folded"] = 0 }));
            pieces.Add(new TextPiece(Say("crease pattern", "patron"), new(-0.2f, Top + 0.012f, 0.205f), 0.1f, Grey));
            if (fold.Modelled || fold.Moving)
            {
                // On a pin through the paper's centre, its lowest point 3 cm over the foot; the model turns about the pin.
                var pin = Clearance + Drop(fold);
                pieces.Add(new BoxPiece(new(0.2f, Top + 0.005f, 0), new(0.14f, 0.01f, 0.09f), Steel, 3));
                pieces.Add(new BoxPiece(new(0.2f, Top + 0.01f + (pin / 2), 0), new(0.005f, pin, 0.005f), Steel, 3));
                pieces.Add(new UsePiece("model", ModelId, SN.Matrix4x4.CreateTranslation(0.2f, Top + 0.01f + pin, 0), new Dictionary<string, WidgetValue> { ["doc"] = reading.Hash }));
                var said = fold.Moving ? Say("one sheet, only folded · rigid motion, Delta's", "une seule feuille, pliée · mouvement rigide de Delta")
                    : fold.Pose is null ? Say("folded in IX's layer order", "plié dans l'ordre de couches d'IX")
                    : Say("IX's layers · wings opened by Delta, illustrative", "couches d'IX · ailes ouvertes par Delta, illustratif");
                pieces.Add(new TextPiece(said, new(0.2f, Top + 0.012f, 0.205f), Math.Clamp(said.Length * 0.0068f, 0.1f, 0.36f), Grey));
            }
            else if (fold.Folded is not null)
            {
                pieces.Add(new UsePiece("folded", BoardId, lean * SN.Matrix4x4.CreateTranslation(0.2f, Top, 0.02f), new Dictionary<string, WidgetValue> { ["doc"] = reading.Hash, ["folded"] = 1 }));
                pieces.Add(new TextPiece(Say("folded by IX", "plié par IX"), new(0.2f, Top + 0.012f, 0.205f), 0.1f, Grey));
            }
        }

        return new(pieces);
    }

    // A finding in words, from IX's rule and what it names.
    public static string Explain(Finding f, bool fr)
    {
        string Say(string en, string frText) => fr ? frText : en;
        string Named(string en1, string enMany, string fr1, string frMany)
        {
            if (f.Ids.Length == 0) return "";
            var list = string.Join(", ", f.Ids.Take(4)) + (f.Ids.Length > 4 ? "…" : "");
            return " " + (f.Ids.Length == 1 ? Say(en1, fr1) : Say(enMany, frMany)) + " " + list;
        }

        var edge = Named("at edge", "at edges", "à l'arête", "aux arêtes");
        var vertex = Named("at vertex", "at vertices", "au sommet", "aux sommets");
        var count = f.Count > 0 ? $" : {f.Count}" : "";
        var text = f.Rule switch
        {
            "adjacency" => Say("Layers: the stated order contradicts the crease", "Couches : l'ordre déclaré contredit le pli") + edge,
            "crease_orientation" => Say("The crease folds the wrong way", "Le pli tourne dans le mauvais sens") + edge,
            "maekawa" => "Maekawa : |M − V| ≠ 2" + vertex,
            "kawasaki" => Say("Kawasaki: alternate angles do not sum to 180°", "Kawasaki : les angles alternés ne font pas 180°") + vertex + (f.Value > 0 ? $" ({f.Value:0.###})" : ""),
            "taco_taco" or "taco_tortilla" or "tortilla_tortilla" => Say("Layers that pass through each other", "Couches qui se traversent") + $" ({f.Rule.Replace('_', '-')})" + count,
            "cyclic" => Say("A cyclic order of layers", "Ordre des couches cyclique") + count,
            "isometry" => Say("Lengths change in the fold, up to ", "Les longueurs changent au pliage, jusqu'à ") + f.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
            "converse" => Say("Contradictory pairs of layers", "Paires de couches contradictoires") + count,
            "structure" => Say("Structure: ", "Structure : ") + f.Detail,
            "expectation" when f.Detail.Split(" → ") is [var want, var got] => Say($"Expected “{want}”, got {got}", $"Attendu « {want} », obtenu {got}") + (f.Value > 0 ? Say($" (line {f.Value:0})", $" (ligne {f.Value:0})") : ""),
            _ => f.Rule + count + edge + vertex,
        };
        return text.Length > 72 ? text[..71] + "…" : text;
    }

    // A board: the creases as fine lines on a sheet, in the pattern's place or the folded state's, about their own centre,
    // at the pattern's scale so that the folded state shows how much smaller it is. The edges and vertices a finding
    // names are amber.
    public static IReadOnlyList<Piece> Drawing(Fold? fold, bool folded)
    {
        if (fold is null || (folded && fold.Folded is null)) return [];
        var at = folded ? fold.Folded! : fold.Vertices;
        static (SN.Vector2 Low, SN.Vector2 High) Bounds(SN.Vector2[] v) => (v.Aggregate(SN.Vector2.Min), v.Aggregate(SN.Vector2.Max));
        var (low, high) = Bounds(fold.Vertices);
        var scale = Board / MathF.Max(1e-6f, MathF.Max(high.X - low.X, high.Y - low.Y));
        var (l2, h2) = Bounds(at);
        var centre = (l2 + h2) / 2;
        SN.Vector3 Place(SN.Vector2 p) => new((p.X - centre.X) * scale, ((p.Y - centre.Y) * scale) + (Board / 2) + 0.03f, 0.0034f);

        var marked = fold.Findings.Where(f => f.Where == "edge").SelectMany(f => f.Ids).ToHashSet();
        var markedVertices = fold.Findings.Where(f => f.Where == "vertex").SelectMany(f => f.Ids).ToHashSet();
        var pieces = new List<Piece> { new BoxPiece(new(0, (Board / 2) + 0.03f, 0), new(Board + 0.04f, Board + 0.04f, 0.006f), Paper, 0) };
        var lines = new List<ModuleTriangle>();
        for (var i = 0; i < fold.Edges.Length; i++)
        {
            var (a, b) = fold.Edges[i];
            var color = marked.Contains(i) ? Amber : fold.Assignment[i] switch { "M" => Mountain, "V" => Valley, "B" => Edge, _ => Other };
            var width = marked.Contains(i) ? 0.006f : fold.Assignment[i] == "B" ? 0.0025f : 0.0022f;
            Line(lines, Place(at[a]), Place(at[b]), width, color);
        }

        if (lines.Count > 0) pieces.Add(new SurfacePiece(lines));
        foreach (var v in markedVertices) pieces.Add(new BoxPiece(Place(at[v]) + new SN.Vector3(0, 0, 0.004f), new(0.02f, 0.02f, 0.01f), Amber, 1));
        return pieces;
    }

    // A line on a board, facing out of it (+z), as two triangles; none when its ends meet.
    private static void Line(List<ModuleTriangle> into, SN.Vector3 from, SN.Vector3 to, float width, SN.Vector3 color)
    {
        var along = to - from;
        if (along.LengthSquared() < 1e-10f) return;
        var d = SN.Vector3.Normalize(along);
        var side = new SN.Vector3(-d.Y, d.X, 0) * (width / 2);
        var (a, b) = (from - (d * width / 2), to + (d * width / 2)); // past each end by half the width, so that lines meet
        ModuleVertex V(SN.Vector3 p) => new(p, SN.Vector3.UnitZ, color);
        into.Add(new(V(a - side), V(b - side), V(b + side)));
        into.Add(new(V(a - side), V(b + side), V(a + side)));
    }

    private const float Clearance = 0.03f; // between the model's lowest point and its foot

    // How far the model's lowest point is below its centre, posed or flat, or at the lowest the folding sheet reaches.
    private static float Drop(Fold fold) => fold.Moving ? Folds.Of(fold).Drop
        : Folding(fold) is { Count: > 0 } t ? -t.Min(x => MathF.Min(x.A.Position.Y, MathF.Min(x.B.Position.Y, x.C.Position.Y))) : 0;

    // The folding sheet, one instance per face, each face flat where the sheet lies before it folds (Face); Pose turns
    // each to where the motion puts it.
    private static IReadOnlyList<Piece> Faces(Fold fold, string hash) =>
        [.. fold.Faces.Select((_, i) => new UsePiece($"f{i}", FaceId, SN.Matrix4x4.Identity, new Dictionary<string, WidgetValue> { ["doc"] = hash, ["face"] = i }))];

    // One face of the sheet as paper, flat in the model's frame where the unfolded sheet puts it (Folds.Flat): its front
    // kami red towards +z, its back cream, Ply / 5 apart.
    public static IReadOnlyList<Piece> Face(Fold? fold, int index)
    {
        if (fold is not { Moving: true } || index < 0 || index >= fold.Faces.Length) return [];
        var flat = Folds.Flat(fold);
        var face = fold.Faces[index];
        var area = 0f;
        for (var i = 0; i < face.Length; i++)
        {
            var (p, q) = (fold.Vertices[face[i]], fold.Vertices[face[(i + 1) % face.Length]]);
            area += (p.X * q.Y) - (q.X * p.Y);
        }

        var triangles = new List<ModuleTriangle>();
        var (up, down) = (SN.Vector3.UnitZ, -SN.Vector3.UnitZ);
        for (var k = 1; k < face.Length - 1; k++)
        {
            var (a, b, c) = (flat[face[0]], flat[face[k]], flat[face[k + 1]]);
            if (area < 0) (b, c) = (c, b); // counter-clockwise seen from +z
            if (SN.Vector3.Cross(b - a, c - a).Z < 1e-7f) continue; // a sliver: nothing to see
            var lift = new SN.Vector3(0, 0, Ply / 10);
            var (front, back) = area >= 0 ? (Kami, Cream) : (Cream, Kami);
            triangles.Add(new(new(a + lift, up, front), new(b + lift, up, front), new(c + lift, up, front)));
            triangles.Add(new(new(a - lift, down, back), new(c - lift, down, back), new(b - lift, down, back)));
        }

        return triangles.Count > 0 ? [new SurfacePiece(triangles)] : [];
    }

    // The motion as each face's rigid turn: at each frame, the turn and the centre that take the face from where the flat
    // sheet has it (Flat: the pattern about its centre, scaled so that its larger side is Model) to where the frame puts
    // it (the frame about its own centre, at the same scale). Between two frames each face turns by the shorter way
    // between the two turns and its centre moves straight: the face keeps its shape at every moment.
    public sealed class Folds
    {
        private readonly SN.Quaternion[][] turns; // [frame][face]
        private readonly SN.Vector3[][] centres;
        private readonly SN.Vector3[] flatCentres;

        private Folds(SN.Quaternion[][] turns, SN.Vector3[][] centres, SN.Vector3[] flatCentres, float drop) =>
            (this.turns, this.centres, this.flatCentres, Drop) = (turns, centres, flatCentres, drop);

        // How far the sheet's lowest point comes below its centre, flat or folded.
        public float Drop { get; }

        public int Frames => turns.Length;

        public static (float Scale, SN.Vector2 Centre) Size(Fold fold)
        {
            var used = fold.Faces.SelectMany(f => f).Select(i => fold.Vertices[i]).ToArray();
            var (low, high) = (used.Aggregate(SN.Vector2.Min), used.Aggregate(SN.Vector2.Max));
            return (Model / MathF.Max(1e-6f, MathF.Max(high.X - low.X, high.Y - low.Y)), (low + high) / 2);
        }

        // The pattern's vertices in the model's frame, the sheet flat.
        public static SN.Vector3[] Flat(Fold fold)
        {
            var (scale, centre) = Size(fold);
            return [.. fold.Vertices.Select(v => new SN.Vector3((v - centre) * scale, 0))];
        }

        private static SN.Vector3[] Placed(Fold fold, SN.Vector3[] frame, float scale)
        {
            var used = fold.Faces.SelectMany(f => f).Select(i => frame[i]).ToArray();
            var centre = (used.Aggregate(SN.Vector3.Min) + used.Aggregate(SN.Vector3.Max)) / 2;
            return [.. frame.Select(v => (v - centre) * scale)];
        }

        // An orthonormal frame of a face's plane from three of its corners: the first, the second, and the one farthest
        // from the line through them; rows x, y, z with z out of the face.
        private static SN.Matrix4x4 Basis(SN.Vector3[] at, int[] face)
        {
            var (o, a) = (at[face[0]], at[face[1]]);
            var x = SN.Vector3.Normalize(a - o);
            var far = face.Skip(2).Select(v => at[v]).MaxBy(p => SN.Vector3.Cross(x, p - o).Length());
            var z = SN.Vector3.Normalize(SN.Vector3.Cross(x, far - o));
            var y = SN.Vector3.Cross(z, x);
            return new(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        }

        private static SN.Vector3 Middle(SN.Vector3[] at, int[] face) => face.Aggregate(SN.Vector3.Zero, (s, v) => s + at[v]) / face.Length;

        public static Folds Of(Fold fold)
        {
            var (scale, _) = Size(fold);
            var flat = Flat(fold);
            var flatBases = fold.Faces.Select(f => Basis(flat, f)).ToArray();
            var turns = new SN.Quaternion[fold.Motion!.Length][];
            var centres = new SN.Vector3[fold.Motion.Length][];
            var drop = 0f;
            for (var k = 0; k < fold.Motion.Length; k++)
            {
                var at = Placed(fold, fold.Motion[k], scale);
                drop = MathF.Max(drop, -fold.Faces.SelectMany(f => f).Min(i => at[i].Y));
                turns[k] = new SN.Quaternion[fold.Faces.Length];
                centres[k] = new SN.Vector3[fold.Faces.Length];
                for (var f = 0; f < fold.Faces.Length; f++)
                {
                    // A row vector v in the flat face's frame: v · Eᵀ its coordinates on the face, those · F the folded point.
                    var turn = SN.Matrix4x4.Transpose(flatBases[f]) * Basis(at, fold.Faces[f]);
                    turns[k][f] = SN.Quaternion.Normalize(SN.Quaternion.CreateFromRotationMatrix(turn));
                    centres[k][f] = Middle(at, fold.Faces[f]);
                }
            }

            return new(turns, centres, [.. fold.Faces.Select(f => Middle(flat, f))], drop);
        }

        // Each face's placement at this much folded, 0 the first frame to 1 the last.
        public SN.Matrix4x4[] At(float folded)
        {
            var x = Math.Clamp(folded, 0f, 1f) * (Frames - 1);
            var k = Math.Min((int)x, Frames - 2);
            var s = x - k;
            var placed = new SN.Matrix4x4[flatCentres.Length];
            for (var f = 0; f < placed.Length; f++)
            {
                var turn = SN.Quaternion.Slerp(turns[k][f], turns[k + 1][f], s);
                var centre = SN.Vector3.Lerp(centres[k][f], centres[k + 1][f], s);
                placed[f] = SN.Matrix4x4.CreateTranslation(-flatCentres[f]) * SN.Matrix4x4.CreateFromQuaternion(turn) * SN.Matrix4x4.CreateTranslation(centre);
            }

            return placed;
        }
    }

    // Where the folded model sits in its own frame: the scale that makes its flat extent Model, and the paper's centre
    // (its faces' centroid, weighted by area) at the origin.
    private static (float Scale, SN.Vector2 Centre) Shape(Fold fold)
    {
        var at = fold.Folded!;
        var used = fold.Faces.SelectMany(f => f).Select(i => at[i]).ToArray();
        var (low, high) = (used.Aggregate(SN.Vector2.Min), used.Aggregate(SN.Vector2.Max));
        var scale = Model / MathF.Max(1e-6f, MathF.Max(high.X - low.X, high.Y - low.Y));
        var (sum, weight) = (SN.Vector2.Zero, 0f);
        foreach (var face in fold.Faces)
        {
            for (var k = 1; k < face.Length - 1; k++)
            {
                var (a, b, c) = (at[face[0]], at[face[k]], at[face[k + 1]]);
                var area = MathF.Abs(((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X))) / 2;
                sum += area * (a + b + c) / 3;
                weight += area;
            }
        }

        return (scale, weight > 1e-12f ? sum / weight : (low + high) / 2);
    }

    // The folded model, about the paper's centre (Shape), as paper: each face of the folded state in its layer, Ply apart in the
    // order IX checked (Layers), the sheet's front kami red and its back cream, each side where the fold turns it. The
    // folded state is flat, as IX states it; the layers' spacing and a slight tilt of each face's light are Delta's, so
    // that the layers read. With a pose, its parts turn about the ridge and the ridge is levelled, back up: an
    // illustration, which the table says. Nothing here is simulated.
    public static IReadOnlyList<Piece> Sheet(Fold? fold) => fold is { Modelled: true } ? [new SurfacePiece(Folding(fold))] : [];

    private static List<ModuleTriangle> Folding(Fold fold)
    {
        var at = fold.Folded!;
        static float Area(SN.Vector2[] v, int[] face)
        {
            var sum = 0f;
            for (var i = 0; i < face.Length; i++)
            {
                var (p, q) = (v[face[i]], v[face[(i + 1) % face.Length]]);
                sum += (p.X * q.Y) - (q.X * p.Y);
            }

            return sum / 2;
        }

        var up = fold.Faces.Select(f => Area(at, f) >= 0 ? 1 : -1).ToArray();
        var front = fold.Faces.Select((f, i) => up[i] * (Area(fold.Vertices, f) >= 0 ? 1 : -1) > 0).ToArray(); // the sheet's front faces +z
        var (layer, _) = Layers(fold.Faces.Length, fold.Orders, up);
        var top = Math.Max(1, layer.Max());
        var (scale, centre) = Shape(fold);
        var place = Posed(fold, layer, top, scale, centre);
        var triangles = new List<ModuleTriangle>();
        for (var i = 0; i < fold.Faces.Length; i++)
        {
            var m = place[i];
            var face = fold.Faces[i];
            var z = (layer[i] - (top / 2f)) * Ply;
            var lift = (float)layer[i] / top;
            var (upper, lower) = front[i] ? (Kami, Cream) : (Cream, Kami);
            upper *= 0.8f + (0.2f * lift); // a layer deep in the stack is in the shade of those over it
            lower *= 0.8f + (0.2f * (1 - lift));
            var tilt = new SN.Vector2(MathF.Sin(i * 12.9898f) * 0.08f, MathF.Sin(i * 78.233f) * 0.08f);
            var nUp = SN.Vector3.Normalize(SN.Vector3.TransformNormal(new(tilt, 1), m));
            var nDown = SN.Vector3.Normalize(SN.Vector3.TransformNormal(new(tilt, -1), m));
            SN.Vector3 P(SN.Vector2 p, float dz) => SN.Vector3.Transform(new((p.X - centre.X) * scale, (p.Y - centre.Y) * scale, z + dz), m);
            for (var k = 1; k < face.Length - 1; k++)
            {
                var (a, b, c) = (at[face[0]], at[face[k]], at[face[k + 1]]);
                if (up[i] < 0) (b, c) = (c, b); // wound counter-clockwise seen from +z
                var cross = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
                if (cross * scale * scale < 1e-7f) continue; // a sliver (under 0.05 mm²): nothing to see
                triangles.Add(new(new(P(a, Ply / 10), nUp, upper), new(P(b, Ply / 10), nUp, upper), new(P(c, Ply / 10), nUp, upper)));
                triangles.Add(new(new(P(a, -Ply / 10), nDown, lower), new(P(c, -Ply / 10), nDown, lower), new(P(b, -Ply / 10), nDown, lower)));
            }
        }

        return triangles;
    }

    // Each face's place in the model: the identity when flat; posed, its part's turn about the ridge, then the turn that
    // puts the ridge level with its up side up. The turn takes a part from the side of the ridge it stands on (up or
    // down) to To degrees from up, towards the side of the stack its layers are on (+z or -z).
    private static SN.Matrix4x4[] Posed(Fold fold, int[] layer, int top, float scale, SN.Vector2 centre)
    {
        var place = Enumerable.Repeat(SN.Matrix4x4.Identity, fold.Faces.Length).ToArray();
        if (fold.Pose is not { } pose) return place;
        var at = fold.Folded!;
        SN.Vector2 M(SN.Vector2 p) => (p - centre) * scale;
        SN.Vector2 Middle(int f) => fold.Faces[f].Aggregate(SN.Vector2.Zero, (s, v) => s + at[v]) / fold.Faces[f].Length;
        var (a, b) = (M(at[pose.Ridge.A]), M(at[pose.Ridge.B]));
        var along = SN.Vector2.Normalize(b - a);
        var up = new SN.Vector2(-along.Y, along.X); // left of the ridge, from A to B
        var axis = new SN.Vector3(up.Y, -up.X, 0); // up × z: a turn by θ takes up to cos θ up + sin θ z
        var pivot = new SN.Vector3(a, 0);
        var level = SN.Matrix4x4.CreateRotationZ((MathF.PI / 2) - MathF.Atan2(up.Y, up.X));
        foreach (var part in pose.Parts)
        {
            var side = part.Faces.Average(f => SN.Vector2.Dot(M(Middle(f)) - a, up)) >= 0 ? 1 : -1;
            var sorted = part.Faces.Select(f => layer[f]).Order().ToArray();
            var median = (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2f;
            var stack = part.Faces.Average(f => layer[f]) >= top / 2f ? 1 : -1;
            foreach (var f in part.Faces)
            {
                var toward = part.Spread ? (layer[f] > median ? 1 : -1) : stack;
                var turn = side > 0 ? toward * part.To : -toward * (180 - part.To);
                place[f] = SN.Matrix4x4.CreateTranslation(-pivot) * SN.Matrix4x4.CreateFromAxisAngle(axis, turn * MathF.PI / 180f) * SN.Matrix4x4.CreateTranslation(pivot);
            }
        }

        for (var f = 0; f < place.Length; f++) place[f] *= level;
        return place;
    }

    // Each face's layer, 0 at the bottom (-z): the longest chain of faces under it by the orders, where face F is over
    // face G when it lies on the side G's normal points to (S = 1) and G faces +z (up = 1), or the opposite of both.
    // Acyclic is false when the orders go round in a circle, which IX's layer rules refuse but a document can still say:
    // the faces caught in it go on top, in index order.
    public static (int[] Layer, bool Acyclic) Layers(int faces, (int F, int G, int S)[] orders, int[] up)
    {
        var over = Enumerable.Range(0, faces).Select(_ => new List<int>()).ToArray();
        var under = new int[faces];
        foreach (var (f, g, s) in orders)
        {
            var (low, high) = s * up[g] > 0 ? (g, f) : (f, g);
            over[low].Add(high);
            under[high]++;
        }

        var layer = new int[faces];
        var ready = new Queue<int>(Enumerable.Range(0, faces).Where(i => under[i] == 0));
        var placed = 0;
        while (ready.TryDequeue(out var u))
        {
            placed++;
            foreach (var v in over[u])
            {
                layer[v] = Math.Max(layer[v], layer[u] + 1);
                if (--under[v] == 0) ready.Enqueue(v);
            }
        }

        if (placed == faces) return (layer, true);
        var next = layer.Max() + 1;
        for (var i = 0; i < faces; i++)
        {
            if (under[i] > 0) layer[i] = next++;
        }

        return (layer, false);
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = "";
        var count = 0;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word.Length > width ? word[..(width - 1)] + "…" : word;
            if (line.Length > 0 && line.Length + w.Length + 1 > width)
            {
                yield return line;
                if (++count == 4) yield break;
                line = "";
            }

            line = line.Length == 0 ? w : line + " " + w;
        }

        if (line.Length > 0) yield return line;
    }
}
