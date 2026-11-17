using Delta.SceneModules;
using SN = System.Numerics;

// A widget module's scene, on the render thread. Its tree is expanded again when the module's revision changes, with a
// mesh built once per distinct widget content (the same widget with the same arguments) and shared by its instances.
// Every frame, the module's poses give each instance its world matrix (pose, then placement, then the parent's) and its
// glow. A mesh the tree no longer needs is released some frames later, once no frame in flight can still draw it. The
// mesh port is the Vulkan boundary, simulated in the tests.
internal sealed class WidgetStage<TSurface> : IDisposable where TSurface : class, IDisposable
{
    internal readonly record struct Item(TSurface Surface, SN.Matrix4x4 World, SN.Vector4 Glow);

    // What a click on a drawn instance shows, and the instance's bounds in the world.
    internal readonly record struct Hit(string Click, SN.Vector3 Min, SN.Vector3 Max);

    private readonly record struct Node(string Path, int Parent, SN.Matrix4x4 Local, string Mesh, string? Click);

    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly Func<WidgetInstance, TSurface> build;
    private readonly int keepFrames;
    private readonly Dictionary<string, (TSurface Surface, SN.Vector3 Min, SN.Vector3 Max)> meshes = new(StringComparer.Ordinal);
    private readonly List<(TSurface Surface, long Frame)> retired = [];
    private readonly Dictionary<string, Pose> poses = new(StringComparer.Ordinal);
    private Node[] nodes = [];
    private SN.Matrix4x4[] worlds = [];
    private bool[] hidden = [];
    private long frame;
    private bool disposed;

    // keepFrames: how many frames a released mesh may still be drawn by, the frames in flight and one more.
    public WidgetStage(Func<WidgetInstance, TSurface> build, int keepFrames)
    {
        this.build = build;
        this.keepFrames = keepFrames;
    }

    public int? Revision { get; private set; }
    public IReadOnlyList<WidgetProblem> Problems { get; private set; } = [];
    public int Instances => nodes.Length;
    public int Meshes => meshes.Count;
    public int Built { get; private set; }

    // The roots the last new tree added and removed, for the log: "+a -b", or "same roots" when only what is under
    // them or where they stand changed.
    public string Changes { get; private set; } = "";
    public int Released { get; private set; }
    public List<Item> Items { get; } = [];
    public List<Hit> Hits { get; } = [];

    // Expands the module's scene again if its revision changed. A refused tree keeps the one shown and its problems
    // are kept for the log; true when a new tree replaced it.
    public bool Sync(IWidgetModule module)
    {
        Check();
        var revision = module.Revision;
        if (revision == Revision) return false;
        Revision = revision;
        var tree = Widgets.Expand(module.Scene());
        Problems = tree.Problems;
        if (!tree.Valid) return false;

        var next = new List<Node>(tree.Instances);
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        void Visit(WidgetInstance instance, int parent)
        {
            var at = next.Count;
            next.Add(new(instance.Path, parent, instance.Local, instance.Mesh, instance.Click));
            if (instance.Mesh.Length > 0 && wanted.Add(instance.Mesh) && !meshes.ContainsKey(instance.Mesh))
            {
                var (min, max) = Bounds(instance);
                meshes[instance.Mesh] = (build(instance), min, max);
                Built++;
            }

            foreach (var child in instance.Children) Visit(child, at);
        }

        foreach (var root in tree.Roots) Visit(root, -1);
        foreach (var key in meshes.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            retired.Add((meshes[key].Surface, frame));
            meshes.Remove(key);
        }

        Changes = Diff(Roots(nodes), Roots(next));
        nodes = [.. next];
        worlds = new SN.Matrix4x4[nodes.Length];
        hidden = new bool[nodes.Length];
        return true;
    }

    // The roots in after and not before, then in before and not after, each in order; eight at most.
    internal static string Diff(IReadOnlySet<string> before, IReadOnlySet<string> after)
    {
        var parts = after.Where(p => !before.Contains(p)).Order(StringComparer.Ordinal).Select(p => "+" + p)
            .Concat(before.Where(p => !after.Contains(p)).Order(StringComparer.Ordinal).Select(p => "-" + p)).ToList();
        return parts.Count == 0 ? "same roots" : string.Join(" ", parts.Take(8)) + (parts.Count > 8 ? $" and {parts.Count - 8} more" : "");
    }

    private static HashSet<string> Roots(IEnumerable<Node> tree) => tree.Where(n => n.Parent < 0).Select(n => n.Path).ToHashSet(StringComparer.Ordinal);

    // Forgets the tree, for a module replaced by new code: its meshes are released as any others.
    public void Reset()
    {
        Check();
        foreach (var (surface, _, _) in meshes.Values) retired.Add((surface, frame));
        meshes.Clear();
        nodes = [];
        worlds = [];
        hidden = [];
        Items.Clear();
        Hits.Clear();
        Revision = null;
        Problems = [];
    }

    // The frame's draws and click areas from the module's poses; then the meshes released long enough ago are freed.
    public void Frame(IWidgetModule module, double seconds)
    {
        Check();
        poses.Clear();
        module.Pose(seconds, poses);
        Items.Clear();
        Hits.Clear();
        var clicks = new string?[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i];
            var parentWorld = node.Parent < 0 ? SN.Matrix4x4.Identity : worlds[node.Parent];
            var pose = poses.TryGetValue(node.Path, out var p) && Finite(p.Transform) && Finite(p.Glow) ? p : new Pose(SN.Matrix4x4.Identity);
            hidden[i] = pose.Hidden || (node.Parent >= 0 && hidden[node.Parent]);
            worlds[i] = pose.Transform * node.Local * parentWorld;
            clicks[i] = node.Click ?? (node.Parent >= 0 ? clicks[node.Parent] : null);
            if (hidden[i] || node.Mesh.Length == 0 || !meshes.TryGetValue(node.Mesh, out var mesh)) continue;
            var glow = new SN.Vector4(Math.Clamp(pose.Glow.X, 0f, 1f), Math.Clamp(pose.Glow.Y, 0f, 1f), Math.Clamp(pose.Glow.Z, 0f, 1f), Math.Clamp(pose.Glow.W, 0f, 4f));
            Items.Add(new(mesh.Surface, worlds[i], glow));
            if (clicks[i] is { } click) Hits.Add(Transformed(click, mesh.Min, mesh.Max, worlds[i]));
        }

        frame++;
        for (var i = retired.Count - 1; i >= 0; i--)
        {
            if (frame - retired[i].Frame <= keepFrames) continue;
            retired[i].Surface.Dispose();
            Released++;
            retired.RemoveAt(i);
        }
    }

    // The caller has waited for the GPU.
    public void Dispose()
    {
        if (disposed) return;
        Check();
        foreach (var (surface, _, _) in meshes.Values) { surface.Dispose(); Released++; }
        foreach (var (surface, _) in retired) { surface.Dispose(); Released++; }
        meshes.Clear();
        retired.Clear();
        disposed = true;
    }

    private static (SN.Vector3 Min, SN.Vector3 Max) Bounds(WidgetInstance instance)
    {
        var min = new SN.Vector3(float.PositiveInfinity);
        var max = new SN.Vector3(float.NegativeInfinity);
        foreach (var b in instance.Boxes) { min = SN.Vector3.Min(min, b.Centre - b.Size / 2); max = SN.Vector3.Max(max, b.Centre + b.Size / 2); }
        foreach (var t in instance.Text)
        {
            var half = new SN.Vector3(t.Width / 2, t.Width / 6, 0.002f);
            min = SN.Vector3.Min(min, t.Centre - half);
            max = SN.Vector3.Max(max, t.Centre + half);
        }

        return (min, max);
    }

    private static Hit Transformed(string click, SN.Vector3 min, SN.Vector3 max, SN.Matrix4x4 world)
    {
        var lo = new SN.Vector3(float.PositiveInfinity);
        var hi = new SN.Vector3(float.NegativeInfinity);
        for (var c = 0; c < 8; c++)
        {
            var p = SN.Vector3.Transform(new((c & 1) == 0 ? min.X : max.X, (c & 2) == 0 ? min.Y : max.Y, (c & 4) == 0 ? min.Z : max.Z), world);
            lo = SN.Vector3.Min(lo, p);
            hi = SN.Vector3.Max(hi, p);
        }

        return new(click, lo, hi);
    }

    private static bool Finite(SN.Vector4 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W);

    private static bool Finite(SN.Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);

    private void Check()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (owner != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Widgets belong to the render thread.");
    }
}
