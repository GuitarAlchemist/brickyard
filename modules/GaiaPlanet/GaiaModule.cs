using Delta.SceneModules;
using System.Numerics;

namespace Delta.GaiaPresence;

// Future module: requires the optional triangle contract AND a host that consumes it.
// Static procedural geometry; no native handles, timers or services. V1.3 paints the sphere with the host's Earth
// (NASA's Blue Marble, which the host keeps, not this module); its procedural colours stay for a host without it.
public sealed class GaiaModule : ISceneTriangleModule
{
    private readonly Dictionary<string, ModuleVisual> frames = [];
    private ModuleTriangleVisual? mesh;
    private string language = "en";
    private bool disposed;
    public string Id => "gaia";

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (mesh is not null) throw new InvalidOperationException("Gaia module is already initialized.");
        var built = Planet.Build();
        ModuleTriangleValidation.Validate(built);
        var dark = new Vector3(.055f, .045f, .08f);
        foreach (var code in new[] { "en", "fr" })
            frames.Add(code, new(Array.AsReadOnly(new[]
            {
                new ModuleBox(new(.88f, .27f, .32f), new(.86f, .12f, .025f), dark),
            }), Array.AsReadOnly(new[]
            {
                new ModuleText(code == "fr" ? "Gaia - non raccordée" : "Gaia - disconnected",
                    new(.88f, .27f, .337f), .78f, new(.82f, .87f, .91f)),
            })));
        mesh = built;
    }

    public void Update(ModuleState state)
    {
        Ready();
        language = state.Language == "fr" ? "fr" : "en";
    }
    public ModuleVisual Render() { Ready(); return frames[language]; }
    public ModuleTriangleVisual RenderTriangles() { Ready(); return mesh!; }
    private void Ready()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (mesh is null) throw new InvalidOperationException("Gaia module is not initialized.");
    }
    public void Dispose()
    {
        frames.Clear();
        mesh = null;
        disposed = true;
    }
}

internal static class Planet
{
    private static readonly Vector3 Centre = new(.88f, .69f, .16f);
    private const float Radius = .25f;

    public static ModuleTriangleVisual Build()
    {
        const int slices = 96, stacks = 40, ringSegments = 96;
        List<ModuleTriangle> triangles = new(7680);
        ModuleVertex Point(int row, int column)
        {
            // Exact shared poles and seam keep the surface closed.
            var latitude = MathF.PI * row / stacks;
            var longitude = MathF.Tau * (column % slices) / slices;
            var n = row == 0 ? Vector3.UnitY : row == stacks ? -Vector3.UnitY :
                Vector3.Normalize(new Vector3(MathF.Sin(latitude) * MathF.Cos(longitude),
                    MathF.Cos(latitude), MathF.Sin(latitude) * MathF.Sin(longitude)));
            return new(Centre + Radius * n, n, Surface(n));
        }
        // Where each point falls on the host's Earth (V1.3): the planet faces the hall (+Z) at 30° W, the Atlantic between
        // Ottawa's clock and Paris's. Longitude grows eastwards to the viewer's right, u from 180° W; past the column
        // where u would fall below 0 it starts again from 1, so that no triangle spans the seam.
        List<ModuleTriangleUv> uvs = new(7680);
        Vector2 Uv(int row, int column, int square) => new(
            Math.Clamp(2f / 3f - (float)column / slices + (square >= slices * 2 / 3 ? 1f : 0f), 0f, 1f), (float)row / stacks);
        for (var row = 0; row < stacks; row++)
            for (var column = 0; column < slices; column++)
            {
                var a = Point(row, column); var b = Point(row + 1, column);
                var c = Point(row + 1, column + 1); var d = Point(row, column + 1);
                Vector2 ua = Uv(row, column, column), ub = Uv(row + 1, column, column);
                Vector2 uc = Uv(row + 1, column + 1, column), ud = Uv(row, column + 1, column);
                if (row != stacks - 1) { triangles.Add(new(a, c, b)); uvs.Add(new(ua, uc, ub)); }
                if (row != 0) { triangles.Add(new(a, d, c)); uvs.Add(new(ua, ud, uc)); }
            }
        // A quiet, opaque, very thin halo. No transparency/shader extension is required.
        var tilt = Matrix4x4.CreateRotationX(.48f) * Matrix4x4.CreateRotationZ(-.16f);
        var normal = Vector3.TransformNormal(Vector3.UnitY, tilt);
        ModuleVertex Ring(int i, float radius)
        {
            var angle = MathF.Tau * (i % ringSegments) / ringSegments;
            return new(Centre + Vector3.Transform(new Vector3(MathF.Cos(angle) * radius, 0,
                MathF.Sin(angle) * radius), tilt), normal, new(.28f, .52f, .58f));
        }
        for (var i = 0; i < ringSegments; i++)
        {
            var a = Ring(i, .290f); var b = Ring(i + 1, .290f);
            var c = Ring(i + 1, .294f); var d = Ring(i, .294f);
            triangles.Add(new(a, b, c)); triangles.Add(new(a, c, d));
        }
        return new(Array.AsReadOnly(triangles.ToArray())) { Image = new("earth", Array.AsReadOnly(uvs.ToArray())) };
    }

    private static Vector3 Surface(Vector3 n)
    {
        // Broad organic land masses in 3D: no texture seam and no random seed or I/O.
        var field = MathF.Sin(3.7f * n.X + 1.8f * n.Z) + .64f * MathF.Cos(5.1f * n.Z - 2.4f * n.Y) +
            .38f * MathF.Sin(8.3f * n.Y + 3.1f * n.X) + .18f * MathF.Cos(13 * n.X - 7 * n.Z);
        var land = Smooth(.42f, .68f, field);
        var depth = Smooth(-1.2f, .5f, field);
        var ocean = Vector3.Lerp(new(.025f, .12f, .24f), new(.045f, .30f, .46f), depth);
        var vegetation = Vector3.Lerp(new(.10f, .26f, .18f), new(.40f, .48f, .25f), Smooth(.7f, 1.5f, field));
        var color = Vector3.Lerp(ocean, vegetation, land);
        var polar = Smooth(.87f, .99f, MathF.Abs(n.Y));
        color = Vector3.Lerp(color, new(.80f, .88f, .88f), polar * .9f);
        var cloudField = MathF.Sin(12 * n.Y + 3 * n.X + MathF.Sin(5 * n.Z)) + .55f * MathF.Cos(9 * n.Z - 4 * n.X);
        var cloud = Smooth(.70f, 1.50f, cloudField) * .78f;
        return Vector3.Lerp(color, new(.89f, .92f, .91f), cloud);
    }
    private static float Smooth(float low, float high, float value)
    {
        var t = Math.Clamp((value - low) / (high - low), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
