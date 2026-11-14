using System.Security.Cryptography;
using System.Text.Json;
using Delta.SceneModules;
using SN = System.Numerics;
using static Delta.SceneModules.Widgets;

namespace Delta.AgentOffice;

// The IX workshop's origami table, beside the knot's stand: a fold that IX checked. `brain -ix-origami FILE` asks IX's
// ix_origami_check about a .origami sequence or a FOLD object and writes the verdict as one versioned document
// (brain/ixorigami.go, format delta-ix-origami v1), read here at most once a second from DELTA_IX_ORIGAMI when set,
// else %LOCALAPPDATA%\delta\ix-origami.json. Two boards lean on the table: the crease pattern, mountains red and
// valleys blue, and the folded state where IX's folded frame puts the same creases, flat and still. Nothing is
// interpolated between them and nothing simulated; the plaque says so. The plaque gives IX's verdict and each of its
// findings in words, and the boards mark in amber the edges and vertices a finding names. A document that breaks the
// bounds shows why, one that says IX was unavailable says so, and none at all gives the command.
//
// This assembly is replaced while the scene runs, so it keeps no static state: what it read lives in this instance.
public sealed class IxOrigami
{
    public const string Format = "delta-ix-origami";
    public const int MaxBytes = 1 << 20, MaxVertices = 1024, MaxEdges = 2048, MaxFindings = 32, MaxIds = 64, MaxText = 120;
    public const double MaxCoord = 16;
    public const float Board = 0.3f; // a board's drawing, its largest extent in metres
    private const float X = 3.45f, Z = 0.55f, Yaw = 0f; // right of the knot's stand, towards the whole hall's view
    private const float Top = 0.82f, Lean = -35f; // the table's top; the boards lean back by this much
    private const int MaxTiles = 3000; // per board

    public enum Standing { Absent, Shown, Unavailable, Refused }

    public sealed record Finding(string Rule, string Where, int[] Ids, int Count, double Value, string Detail);

    public sealed record Fold(string Name, bool Ok, bool? Holds, string[] RejectedBy, int MaxLayers, SN.Vector2[] Vertices,
        (int A, int B)[] Edges, string[] Assignment, SN.Vector2[]? Folded, Finding[] Findings);

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

            SN.Vector2[]? folded = null;
            if (root.TryGetProperty("folded"u8, out _))
            {
                folded = Points(root, "folded");
                if (folded is null || folded.Length != vertices.Length) return Refuse("the folded state's vertices are not the pattern's, within bounds", hash);
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

            return new(Standing.Shown, new(name, okay.GetBoolean(), holds, [.. rejected], layers, vertices, [.. edges], [.. assignment], folded, [.. findings]), "", hash);
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
    }

    public UsePiece Root(string language) =>
        new("ix-origami", TableId, Place(X, 0f, Z, Yaw), new Dictionary<string, WidgetValue> { ["doc"] = $"{language}-{Current.Hash}" });

    private static readonly WidgetId TableId = new("ix-origami-table", 1), BoardId = new("ix-origami-board", 1);
    private static readonly SN.Vector3 Dark = new(0.05f, 0.05f, 0.06f), Steel = new(0.2f, 0.21f, 0.22f), Plaque = new(0.02f, 0.02f, 0.025f),
        Paper = new(0.88f, 0.86f, 0.8f), Mountain = new(0.9f, 0.22f, 0.18f), Valley = new(0.2f, 0.45f, 0.95f), Edge = new(0.25f, 0.25f, 0.27f),
        Other = new(0.55f, 0.55f, 0.55f), Amber = new(1f, 0.62f, 0.2f), Green = new(0.35f, 0.9f, 0.45f), Red = new(1f, 0.45f, 0.35f),
        Pale = new(0.84f, 0.86f, 0.9f), Grey = new(0.6f, 0.62f, 0.66f);

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
            if (fold.Folded is not null)
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

    // A board: the creases as tiles on a sheet, in the pattern's place or the folded state's, about their own centre, at
    // the pattern's scale so that the folded state shows how much smaller it is. The edges and vertices a finding names
    // are amber.
    public static IReadOnlyList<Piece> Drawing(Fold? fold, bool folded)
    {
        if (fold is null || (folded && fold.Folded is null)) return [];
        var at = folded ? fold.Folded! : fold.Vertices;
        static (SN.Vector2 Low, SN.Vector2 High) Bounds(SN.Vector2[] v) => (v.Aggregate(SN.Vector2.Min), v.Aggregate(SN.Vector2.Max));
        var (low, high) = Bounds(fold.Vertices);
        var scale = Board / MathF.Max(1e-6f, MathF.Max(high.X - low.X, high.Y - low.Y));
        var (l2, h2) = Bounds(at);
        var centre = (l2 + h2) / 2;
        SN.Vector3 Place(SN.Vector2 p) => new((p.X - centre.X) * scale, ((p.Y - centre.Y) * scale) + (Board / 2) + 0.03f, 0.006f);

        var marked = fold.Findings.Where(f => f.Where == "edge").SelectMany(f => f.Ids).ToHashSet();
        var markedVertices = fold.Findings.Where(f => f.Where == "vertex").SelectMany(f => f.Ids).ToHashSet();
        var pieces = new List<Piece> { new BoxPiece(new(0, (Board / 2) + 0.03f, 0), new(Board + 0.04f, Board + 0.04f, 0.006f), Paper, 0) };
        var length = fold.Edges.Sum(e => SN.Vector2.Distance(at[e.A], at[e.B])) * scale;
        var step = MathF.Max(0.004f, length / MaxTiles);
        for (var i = 0; i < fold.Edges.Length; i++)
        {
            var (a, b) = fold.Edges[i];
            var color = marked.Contains(i) ? Amber : fold.Assignment[i] switch { "M" => Mountain, "V" => Valley, "B" => Edge, _ => Other };
            var size = marked.Contains(i) ? 0.009f : fold.Assignment[i] == "B" ? 0.004f : 0.006f;
            var from = Place(at[a]);
            var to = Place(at[b]);
            var span = SN.Vector3.Distance(from, to);
            var tiles = Math.Max(1, (int)MathF.Ceiling(span / step));
            for (var t = 0; t <= tiles; t++) pieces.Add(new BoxPiece(SN.Vector3.Lerp(from, to, t / (float)tiles), new(size, size, 0.003f), color, 0));
        }

        foreach (var v in markedVertices) pieces.Add(new BoxPiece(Place(at[v]) + new SN.Vector3(0, 0, 0.004f), new(0.02f, 0.02f, 0.01f), Amber, 1));
        return pieces;
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
