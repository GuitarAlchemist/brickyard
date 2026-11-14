using System.Globalization;
using System.Numerics;
using System.Text;

namespace Delta.SceneModules;

// Widgets: composable 3D elements, after GA's Lego demo (ga-brutal, Experiments/Brutal/Lego, MIT). A widget is a pure
// function from its arguments to pieces: boxes, labels, and other widgets it uses, placed in its frame or fixed to an
// anchor of one of them. Widgets nest in widgets, fractally, within limits Expand enforces. Expanded, a scene is a tree
// of instances, each with a path ("desk-3/robot/arm-left") and a placement in its parent's frame. Paths are how a module
// handles them: it poses a path (moves it, lights it, hides it) and everything under it follows, so an animation moves
// meshes the host built once instead of rebuilding them. Instances with the same widget and arguments share one mesh.
//
// Unlike Lego's, a box is centred on its Centre (as Boxes.Add draws it), and a widget fixed to an anchor becomes a child
// of the instance that has the anchor, so that it follows that instance when it is posed.

// A widget's name and version, written desk@1. A new version is a new widget beside the old one.
public readonly record struct WidgetId(string Name, int Version)
{
    public override string ToString() => $"{Name}@{Version}";
}

// An argument: a number (metres, degrees, factors, counts) or a text (a name on a plate).
public readonly record struct WidgetValue(double Number, string? Text = null)
{
    public static implicit operator WidgetValue(double number) => new(number);
    public static implicit operator WidgetValue(string text) => new(0, text);
    public override string ToString() => Text is { } t ? Quote(t) : Number.ToString("R", CultureInfo.InvariantCulture);

    private static string Quote(string t)
    {
        var b = new StringBuilder("\"");
        foreach (var c in t) b.Append(c is '"' or '\\' ? "\\" + c : c.ToString());
        return b.Append('"').ToString();
    }
}

// A parameter: its name, its default (whose kind, number or text, is the parameter's) and its bounds, both included;
// for a text, the bounds are on its length.
public sealed record WidgetParam(string Name, WidgetValue Default, double Min, double Max);

// The arguments a widget's function receives: every parameter, checked, its default where none was given.
public sealed class WidgetArgs(IReadOnlyDictionary<string, WidgetValue> values)
{
    public double Number(string name) => values.TryGetValue(name, out var v) && v.Text is null ? v.Number
        : throw new ArgumentException($"the widget has no number parameter '{name}'", nameof(name));

    public string Text(string name) => values.TryGetValue(name, out var v) && v.Text is { } t ? t
        : throw new ArgumentException($"the widget has no text parameter '{name}'", nameof(name));
}

// What a widget is made of, in its own frame.
public abstract record Piece;

public sealed record BoxPiece(Vector3 Centre, Vector3 Size, Vector3 Color, uint Material = 0) : Piece;

public sealed record TextPiece(string Text, Vector3 Centre, float Width, Vector3 Color) : Piece;

// A smooth surface: triangles whose vertices carry their own normal and colour, such as a rope's strands or a sheet of
// paper, drawn with the boxes of the same mesh in the given material. Each triangle winds about the normals of its
// vertices (counter-clockwise seen from outside); Widgets.Expand refuses a degenerate one or one wound against them.
public sealed record SurfacePiece(IReadOnlyList<ModuleTriangle> Triangles, uint Material = 0) : Piece;

// An instance of a widget, named for its path; its arguments left out take their defaults. Click, when given, is what a
// click on it, or on anything under it without a Click of its own, asks the host to show.
public sealed record UsePiece(string Name, WidgetId Widget, Matrix4x4 Place, IReadOnlyDictionary<string, WidgetValue>? Args = null,
    string? Click = null) : Piece;

// A widget fixed to an anchor of an instance used by this same widget, where it becomes that instance's child.
public sealed record AttachPiece(string On, string Anchor, UsePiece Use) : Piece;

// A widget's output: its pieces and its named anchors, both in its own frame.
public sealed record Built(IReadOnlyList<Piece> Pieces, IReadOnlyDictionary<string, Matrix4x4>? Anchors = null);

// A widget: an identity, parameters, and a pure function from its arguments to what it is made of.
public sealed record Widget(WidgetId Id, IReadOnlyList<WidgetParam> Params, Func<WidgetArgs, Built> Build);

// How far a scene may grow: how deep instances nest (a root at 0), how many there are, how many boxes and labels they
// hold in all, and how many distinct meshes (widget and arguments) they need.
public sealed record WidgetLimits(int MaxDepth, int MaxInstances, int MaxBoxes, int MaxText, int MaxMeshes)
{
    public static WidgetLimits Standard { get; } = new(8, 2048, 40000, 256, 512);

    // The surfaces' triangles in all.
    public int MaxTriangles { get; init; } = 120_000;
}

public sealed record WidgetProblem(string Path, string Widget, string Kind, string Message)
{
    public override string ToString() => $"{Path} ({Widget}): {Kind}: {Message}";
}

// An expanded instance. Local places it in its parent's frame (in the world for a root). Mesh names its boxes and labels:
// equal for every instance of the same widget with the same arguments.
public sealed record WidgetInstance(string Path, WidgetId Widget, string Mesh, Matrix4x4 Local, string? Click,
    IReadOnlyList<BoxPiece> Boxes, IReadOnlyList<TextPiece> Text, IReadOnlyDictionary<string, Matrix4x4> Anchors,
    IReadOnlyList<WidgetInstance> Children)
{
    // Its smooth surfaces, part of the same mesh.
    public IReadOnlyList<SurfacePiece> Surfaces { get; init; } = [];
}

// What a module shows: its library and the widgets it places at the root of the world.
public sealed record WidgetScene(IReadOnlyList<Widget> Library, IReadOnlyList<UsePiece> Roots);

// An expansion: the trees, or every reason to refuse them.
public sealed record WidgetTree(IReadOnlyList<WidgetInstance> Roots, IReadOnlyList<WidgetProblem> Problems, int Instances, int Boxes, int Meshes)
{
    public bool Valid => Problems.Count == 0;

    public int Triangles { get; init; }
}

// A path's pose for a frame: Transform applies in the instance's own frame, before its placement, and moves everything
// under it too; Glow's rgb times its w lights the instance's own mesh; Hidden hides it and everything under it.
public readonly record struct Pose(Matrix4x4 Transform, Vector4 Glow = default, bool Hidden = false)
{
    public static Pose Moved(Vector3 by, Vector4 glow = default) => new(Matrix4x4.CreateTranslation(by), glow);
    public static Pose Lit(Vector4 glow) => new(Matrix4x4.Identity, glow);
    public static Pose Off => new(Matrix4x4.Identity, default, true);
}

// A module made of widgets. The host expands Scene() again only when Revision changes, and asks for poses every frame.
public interface IWidgetModule : IDisposable
{
    string Id { get; }
    void Initialize();
    int Revision { get; }
    WidgetScene Scene();

    // Fills poses (cleared by the host) with the paths that are not at rest at this moment.
    void Pose(double seconds, IDictionary<string, Pose> poses);
}

public static class Widgets
{
    // A placement as Lego writes it: scaled, then turned about +Y by yaw degrees, then moved.
    public static Matrix4x4 Place(float x, float y, float z, float yaw = 0f, float scale = 1f) =>
        Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(yaw * MathF.PI / 180f) * Matrix4x4.CreateTranslation(x, y, z);

    // The library's widgets placed by the roots, expanded and checked. It never throws: where it cannot go on (a missing
    // widget, bad arguments, a cycle, a spent budget, a failing function) it records why on that instance, expands
    // nothing below it and goes on, and the tree is then not Valid. Once a budget is spent nothing more expands.
    public static WidgetTree Expand(WidgetScene scene, WidgetLimits? limits = null) => new Expansion(scene, limits ?? WidgetLimits.Standard).Run();

    private sealed class Expansion(WidgetScene scene, WidgetLimits limits)
    {
        private readonly List<WidgetProblem> problems = [];
        private readonly HashSet<string> meshes = [];
        private Dictionary<WidgetId, Widget> library = [];
        private int instances, boxes, text, triangles;
        private bool spent;

        public WidgetTree Run()
        {
            foreach (var w in scene.Library)
            {
                if (!library.TryAdd(w.Id, w)) Problem("", w.Id, "duplicate-widget", $"{w.Id} is in the library twice");
            }

            var roots = Uses(scene.Roots.Select(u => (Piece)u).ToList(), "", 0, [], "");
            return new(roots, problems, instances, boxes, meshes.Count) { Triangles = triangles };
        }

        private List<WidgetInstance> Uses(IReadOnlyList<Piece> pieces, string parent, int depth, List<string> stack, string parentPath)
        {
            var children = new List<WidgetInstance>();
            var names = new HashSet<string>();
            foreach (var piece in pieces)
            {
                if (piece is not UsePiece use) continue;
                if (!names.Add(use.Name)) { Problem(parentPath, default, "duplicate-name", $"two instances named '{use.Name}'"); continue; }
                children.Add(Instance(use, Join(parentPath, use.Name), depth, stack));
            }

            return children;
        }

        private WidgetInstance Instance(UsePiece use, string path, int depth, List<string> stack)
        {
            WidgetInstance Empty(string? mesh = null) => new(path, use.Widget, mesh ?? "", use.Place, use.Click, [], [], new Dictionary<string, Matrix4x4>(), []);

            if (spent) return Empty();
            if (!Name(use.Name)) { Problem(path, use.Widget, "bad-name", "a name is 1 to 32 letters, digits, '-' or '_'"); return Empty(); }
            if (!Finite(use.Place)) { Problem(path, use.Widget, "bad-shape", "the placement is not finite"); return Empty(); }
            if (depth > limits.MaxDepth) { Problem(path, use.Widget, "too-deep", $"nested deeper than {limits.MaxDepth} levels"); return Empty(); }
            if (++instances > limits.MaxInstances) { Spend(path, use.Widget, "too-many-instances", $"more than {limits.MaxInstances} instances"); return Empty(); }
            if (!library.TryGetValue(use.Widget, out var widget)) { Problem(path, use.Widget, "missing-widget", $"no widget {use.Widget} in the library"); return Empty(); }
            if (Resolve(widget, use.Args, path) is not { } args) return Empty();

            var key = $"{widget.Id}({string.Join(",", widget.Params.Select(p => $"{p.Name}={args[p.Name]}"))})";
            if (stack.Contains(key)) { Problem(path, widget.Id, "cycle", $"{key} again under itself, with the same arguments"); return Empty(); }

            Built built;
            try { built = widget.Build(new WidgetArgs(args)) ?? throw new InvalidOperationException("the function gave nothing"); }
            catch (Exception e) { Problem(path, widget.Id, "build-failed", e.Message); return Empty(); }

            var ownBoxes = new List<BoxPiece>();
            var ownText = new List<TextPiece>();
            var ownSurfaces = new List<SurfacePiece>();
            foreach (var piece in built.Pieces)
            {
                switch (piece)
                {
                    case BoxPiece b when Box(b, path, widget.Id):
                        if (++boxes > limits.MaxBoxes) { Spend(path, widget.Id, "too-many-boxes", $"more than {limits.MaxBoxes} boxes"); return Empty(); }
                        ownBoxes.Add(b);
                        break;
                    case TextPiece t when Label(t, path, widget.Id):
                        if (++text > limits.MaxText) { Spend(path, widget.Id, "too-much-text", $"more than {limits.MaxText} labels"); return Empty(); }
                        ownText.Add(t);
                        break;
                    case SurfacePiece s when Surface(s, path, widget.Id):
                        triangles += s.Triangles.Count;
                        if (triangles > limits.MaxTriangles) { Spend(path, widget.Id, "too-many-triangles", $"more than {limits.MaxTriangles} triangles"); return Empty(); }
                        ownSurfaces.Add(s with { Triangles = [.. s.Triangles] }); // a copy: what is checked is what is drawn
                        break;
                }
            }

            var anchors = new Dictionary<string, Matrix4x4>();
            foreach (var (name, frame) in built.Anchors ?? new Dictionary<string, Matrix4x4>())
            {
                if (Finite(frame)) anchors[name] = frame;
                else Problem(path, widget.Id, "bad-shape", $"anchor '{name}' is not finite");
            }

            if ((ownBoxes.Count > 0 || ownText.Count > 0 || ownSurfaces.Count > 0) && meshes.Add(key) && meshes.Count > limits.MaxMeshes)
            {
                Spend(path, widget.Id, "too-many-meshes", $"more than {limits.MaxMeshes} distinct meshes");
                return Empty();
            }

            stack.Add(key);
            var children = Uses(built.Pieces, path, depth + 1, stack, path);
            stack.RemoveAt(stack.Count - 1);

            // Attached widgets join the instance whose anchor they are fixed to, in that anchor's frame.
            foreach (var attach in built.Pieces.OfType<AttachPiece>())
            {
                var at = children.FindIndex(c => c.Path == Join(path, attach.On));
                if (at < 0) { Problem(path, widget.Id, "missing-instance", $"no instance named '{attach.On}' to attach to"); continue; }
                var target = children[at];
                if (!target.Anchors.TryGetValue(attach.Anchor, out var frame))
                {
                    if (target.Mesh.Length > 0 || target.Children.Count > 0) Problem(path, widget.Id, "missing-anchor", $"'{attach.On}' has no anchor '{attach.Anchor}'");
                    continue;
                }

                stack.Add(key);
                var placed = attach.Use with { Place = attach.Use.Place * frame };
                var joined = target.Children.Any(c => c.Path == Join(target.Path, placed.Name))
                    ? null : Instance(placed, Join(target.Path, placed.Name), depth + 2, stack);
                stack.RemoveAt(stack.Count - 1);
                if (joined is null) { Problem(target.Path, target.Widget, "duplicate-name", $"two instances named '{placed.Name}'"); continue; }
                children[at] = target with { Children = [.. target.Children, joined] };
            }

            var mesh = ownBoxes.Count > 0 || ownText.Count > 0 || ownSurfaces.Count > 0 ? key : "";
            return new(path, widget.Id, mesh, use.Place, use.Click, ownBoxes, ownText, anchors, children) { Surfaces = ownSurfaces };
        }

        // A surface's triangles: finite, within 16 m of the widget's origin, unit normals, colours in [0, 1], a known
        // material, and each triangle wound about its vertices' normals and not degenerate.
        private bool Surface(SurfacePiece s, string path, WidgetId id)
        {
            if (s.Triangles is null || s.Material > 5) { Problem(path, id, "bad-shape", "a surface needs its triangles and a known material"); return false; }
            foreach (var t in s.Triangles)
            {
                var cross = Vector3.Cross(t.B.Position - t.A.Position, t.C.Position - t.A.Position);
                if (!Corner(t.A) || !Corner(t.B) || !Corner(t.C) || cross.LengthSquared() < 1e-16f ||
                    Vector3.Dot(cross, t.A.Normal) <= 0 || Vector3.Dot(cross, t.B.Normal) <= 0 || Vector3.Dot(cross, t.C.Normal) <= 0)
                {
                    Problem(path, id, "bad-shape", "a surface's triangle is degenerate, wound against its normals, or has a vertex out of bounds");
                    return false;
                }
            }

            return true;
        }

        private static bool Corner(ModuleVertex v) =>
            Finite(v.Position) && v.Position.Length() < 16 && Finite(v.Normal) && MathF.Abs(v.Normal.LengthSquared() - 1) < 0.002f && Unit(v.Color);

        // Each parameter, given or defaulted, checked; or null, with the problems recorded.
        private Dictionary<string, WidgetValue>? Resolve(Widget widget, IReadOnlyDictionary<string, WidgetValue>? given, string path)
        {
            var ok = true;
            foreach (var name in (given?.Keys ?? []).Where(n => widget.Params.All(p => p.Name != n)))
            {
                Problem(path, widget.Id, "bad-argument", $"'{name}': the widget has no such parameter");
                ok = false;
            }

            var args = new Dictionary<string, WidgetValue>();
            foreach (var p in widget.Params)
            {
                var value = given is not null && given.TryGetValue(p.Name, out var v) ? v : p.Default;
                var isText = p.Default.Text is not null;
                string? why = null;
                if (isText != (value.Text is not null)) why = isText ? "expected a text" : "expected a number";
                else if (isText && (value.Text!.Length < p.Min || value.Text.Length > p.Max || value.Text.Any(char.IsControl)))
                    why = $"a text of {p.Min} to {p.Max} characters, without control characters";
                else if (!isText && (!double.IsFinite(value.Number) || value.Number < p.Min || value.Number > p.Max))
                    why = $"{value} is outside [{p.Min.ToString(CultureInfo.InvariantCulture)}, {p.Max.ToString(CultureInfo.InvariantCulture)}]";
                if (why is not null) { Problem(path, widget.Id, "bad-argument", $"'{p.Name}': {why}"); ok = false; }
                args[p.Name] = value;
            }

            return ok ? args : null;
        }

        private bool Box(BoxPiece b, string path, WidgetId id)
        {
            if (Finite(b.Centre) && Finite(b.Size) && b.Size is { X: > 0, Y: > 0, Z: > 0 } && Unit(b.Color) && b.Material <= 5) return true;
            Problem(path, id, "bad-shape", "a box needs a finite centre, a size above zero, a colour in [0, 1] and a known material");
            return false;
        }

        private bool Label(TextPiece t, string path, WidgetId id)
        {
            if (t.Text.Length is >= 1 and <= 128 && !t.Text.Any(char.IsControl) && Finite(t.Centre) && float.IsFinite(t.Width) && t.Width is >= 0.01f and <= 2f && Unit(t.Color)) return true;
            Problem(path, id, "bad-shape", "a label is 1 to 128 characters, 0.01 to 2 m wide, with a colour in [0, 1]");
            return false;
        }

        private void Spend(string path, WidgetId id, string kind, string message)
        {
            spent = true;
            Problem(path, id, kind, message);
        }

        private void Problem(string path, WidgetId id, string kind, string message) =>
            problems.Add(new(path, id.Name is null ? "" : id.ToString(), kind, message));

        private static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;
        private static bool Name(string n) => n.Length is >= 1 and <= 32 && n.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
        private static bool Unit(Vector3 c) => Finite(c) && c.X is >= 0 and <= 1 && c.Y is >= 0 and <= 1 && c.Z is >= 0 and <= 1;
        private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        private static bool Finite(Matrix4x4 m) =>
            float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
            float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
            float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
            float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
    }
}
