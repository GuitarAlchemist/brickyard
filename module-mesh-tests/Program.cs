using Delta.SceneModules;
using Delta.GaiaPresence;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

var failures = 0;
void Check(bool ok, string message)
{
    Console.WriteLine($"MESH TEST {(ok ? "ok" : "FAIL")} {message}");
    if (!ok) failures++;
}
void Reject(ModuleTriangleVisual visual, string message)
{
    var rejected = false;
    try { ModuleTriangleValidation.Validate(visual); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, message);
}

using var module = new GaiaModule();
Check(module.Id == "gaia" && typeof(GaiaModule).Assembly.GetName().Name == "GaiaPresence.Module" &&
    typeof(GaiaModule).Assembly.GetName().Version == new Version(1, 3, 0, 0), "real Gaia DLL identity/version 1.3");
var uninitialized = false;
try { module.RenderTriangles(); } catch (InvalidOperationException) { uninitialized = true; }
Check(uninitialized, "uninitialized geometry is not exposed");
module.Initialize();
var mesh = module.RenderTriangles();
ModuleTriangleValidation.Validate(mesh);
Check(mesh.Triangles.Count is >= 7500 and <= 7900, "smooth sphere plus thin ring fit a modest fixed triangle budget");
var vertices = mesh.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
var centre = new Vector3(.88f, .69f, .16f);
Check(vertices.All(v => Vector3.Distance(v.Position, centre) < .31f), "planet fits the original Gaia footprint above the plaques");
var surface = vertices.Where(v => MathF.Abs(Vector3.Distance(v.Position, centre) - .25f) < .00001f).ToArray();
Check(surface.Length > 20000 && surface.All(v => Vector3.Distance(v.Normal, Vector3.Normalize(v.Position - centre)) < .00001f),
    "sphere normals are radial and smooth, independent of the triangle face");
var sphere = mesh.Triangles.Where(t => new[] { t.A, t.B, t.C }.All(v => MathF.Abs(Vector3.Distance(v.Position, centre) - .25f) < .00001f)).ToArray();
var edges = new Dictionary<(Vector3, Vector3), int>();
foreach (var t in sphere)
    foreach (var (a, b) in new[] { (t.A.Position, t.B.Position), (t.B.Position, t.C.Position), (t.C.Position, t.A.Position) })
    {
        var order = a.X.CompareTo(b.X);
        if (order == 0) order = a.Y.CompareTo(b.Y);
        if (order == 0) order = a.Z.CompareTo(b.Z);
        var edge = order < 0 ? (a, b) : (b, a);
        edges[edge] = edges.GetValueOrDefault(edge) + 1;
    }
Check(edges.Values.All(n => n == 2), "sphere is watertight, including its seam and poles");
Check(surface.Any(v => v.Color.Z > v.Color.Y * 1.3f) && surface.Any(v => v.Color.Y > v.Color.Z * 1.2f) &&
    surface.Any(v => v.Color.X > .65f && v.Color.Y > .65f), "procedural surface includes ocean, continents and pale cloud cover");
module.Update(new("fr"));
Check(module.Render().Text.Any(t => t.Text == "Gaia - non raccordée"), "French status remains explicitly disconnected");
module.Update(new("en"));
Check(module.Render().Text.Any(t => t.Text == "Gaia - disconnected"), "English status remains explicitly disconnected");
Check(ReferenceEquals(mesh, module.RenderTriangles()), "language changes reuse the static immutable mesh");
using (var duplicate = new GaiaModule())
{
    duplicate.Initialize();
    Check(mesh.Triangles.SequenceEqual(duplicate.RenderTriangles().Triangles), "procedural mesh is deterministic");
}
var triangle = mesh.Triangles[0];
ModuleTriangleValidation.Validate(new(Array.AsReadOnly(Enumerable.Repeat(triangle, ModuleTriangleValidation.MaxTriangles).ToArray())));
Reject(new(Array.AsReadOnly(Enumerable.Repeat(triangle, ModuleTriangleValidation.MaxTriangles + 1).ToArray())), "over-budget triangles are rejected before vertex work");
Reject(new(new[] { triangle with { A = triangle.A with { Position = new(float.NaN, 0, 0) } } }), "non-finite positions rejected");
Reject(new(new[] { triangle with { A = triangle.A with { Position = new(16, 0, 0) } } }), "positions outside host bounds rejected");
Reject(new(new[] { triangle with { A = triangle.A with { Normal = new(0, 2, 0) } } }), "non-unit normals rejected");
Reject(new(new[] { triangle with { A = triangle.A with { Color = new(1.01f, 0, 0) } } }), "invalid colors rejected");
Reject(new(new[] { triangle with { B = triangle.A } }), "degenerate triangles rejected");
Reject(new(new[] { new ModuleTriangle(triangle.A, triangle.C, triangle.B) }), "winding opposed to normals rejected");

// V1.3: the sphere is painted with the host's Earth, by name; its coordinates stay on the image and off the seam.
var image = mesh.Image;
Check(image is { Name: "earth" } && image.Uvs.Count == mesh.Triangles.Count - 192, "every sphere triangle, not the ring's 192, carries its place on the host's Earth");
Check(image!.Uvs.All(uv => new[] { uv.A, uv.B, uv.C }.All(p => p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1)), "the coordinates stay on the image");
Check(image.Uvs.All(uv => MathF.Max(MathF.Abs(uv.A.X - uv.B.X), MathF.Max(MathF.Abs(uv.B.X - uv.C.X), MathF.Abs(uv.A.X - uv.C.X))) < 0.02f),
    "no triangle spans the image's seam, its corners at most one column apart");
var facing = mesh.Triangles.Select((t, i) => (t, i)).Where(p => p.i < image.Uvs.Count)
    .MinBy(p => Vector3.Distance(Vector3.Normalize(p.t.A.Normal), Vector3.UnitZ)).i;
var longitude = image.Uvs[facing].A.X * 360f - 180f;
Check(longitude is > -40f and < -20f, $"the planet faces the hall at about 30° W, the Atlantic ({longitude:F1}°)");
var ring = mesh.Triangles.Count - 1;
var named = mesh with { Image = image };
Reject(named with { Image = image with { Name = "../earth" } }, "an image name that could be a path is rejected");
Reject(named with { Image = image with { Name = "Earth" } }, "an image name outside lowercase letters, digits and dashes is rejected");
Reject(named with { Image = image with { Uvs = [.. image.Uvs, .. Enumerable.Repeat(image.Uvs[0], ring)] } }, "more coordinates than triangles are rejected");
Reject(named with { Image = image with { Uvs = [image.Uvs[0] with { A = new(1.01f, 0) }] } }, "coordinates off the image are rejected");
Reject(named with { Image = image with { Uvs = [image.Uvs[0] with { B = new(0, float.NaN) }] } }, "non-finite coordinates are rejected");
ModuleTriangleValidation.Validate(mesh with { Image = null });
Check(true, "triangles without an image stay valid (V1.2 modules)");
Console.WriteLine($"MESH BUDGET {mesh.Triangles.Count} triangles; {vertices.Length} expanded vertices; {vertices.Length * 68} bytes in current 68-byte scene Vertex layout (estimate, no GPU allocation)");
if (args.Length >= 1) CpuPreview.Save(mesh, args[0]);
if (args.Length == 2)
{
    var oldHost = new OldCodeContext(args[1]);
    var oldContract = oldHost.LoadFromStream(new MemoryStream(File.ReadAllBytes(args[1])));
    Check(oldContract.GetType("Delta.SceneModules.ISceneTriangleModule") is null, "legacy contract DLL actually lacks the triangle capability");
    var refused = false;
    try
    {
        var future = oldHost.LoadFromStream(new MemoryStream(File.ReadAllBytes(typeof(GaiaModule).Assembly.Location)));
        _ = future.GetType("Delta.GaiaPresence.GaiaModule", true);
    }
    catch (TypeLoadException) { refused = true; }
    Check(refused, "old shared contract refuses V1.2: host upgrade is required before loading");
    oldHost.Unload();
}
var retired = RetireCode(typeof(GaiaModule).Assembly.Location);
for (var i = 0; i < 5 && retired.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
Check(!retired.IsAlive, "optional module code and CPU mesh release their collectible context");
module.Dispose();
var disposed = false;
try { module.RenderTriangles(); } catch (ObjectDisposedException) { disposed = true; }
Check(disposed, "disposed module releases frames and cannot render");
module.Dispose();
Check(true, "Dispose is idempotent");
return failures == 0 ? 0 : 1;

[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference RetireCode(string path)
{
    var context = new TestCodeContext();
    var reference = new WeakReference(context);
    var assembly = context.LoadFromStream(new MemoryStream(File.ReadAllBytes(path)));
    var instance = (ISceneTriangleModule)Activator.CreateInstance(assembly.GetType("Delta.GaiaPresence.GaiaModule", true)!)!;
    instance.Initialize();
    ModuleTriangleValidation.Validate(instance.RenderTriangles());
    instance.Dispose();
    context.Unload();
    return reference;
}

sealed class TestCodeContext() : AssemblyLoadContext(isCollectible: true)
{
    protected override Assembly? Load(AssemblyName name) =>
        name.Name == typeof(ISceneModule).Assembly.GetName().Name ? typeof(ISceneModule).Assembly : null;
}

sealed class OldCodeContext(string contractPath) : AssemblyLoadContext(isCollectible: true)
{
    protected override Assembly? Load(AssemblyName name) => name.Name == "Delta.SceneModules.Contracts"
        ? LoadFromStream(new MemoryStream(File.ReadAllBytes(contractPath))) : null;
}
