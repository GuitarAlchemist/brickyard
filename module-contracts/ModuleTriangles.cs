using System.Numerics;

namespace Delta.SceneModules;

// Optional, static, CPU-only geometry. The original SceneModule.cs ABI is unchanged.
// The host owns validation, copying, GPU allocation, fences and disposal.
public interface ISceneTriangleModule : ISceneModule
{
    ModuleTriangleVisual RenderTriangles();
}

public readonly record struct ModuleVertex(Vector3 Position, Vector3 Normal, Vector3 Color);
public readonly record struct ModuleTriangle(ModuleVertex A, ModuleVertex B, ModuleVertex C);
public sealed record ModuleTriangleVisual(IReadOnlyList<ModuleTriangle> Triangles);

public static class ModuleTriangleValidation
{
    public const int MaxTriangles = 8192;

    // Call before allocating or copying a GPU surface, including for initial load.
    public static void Validate(ModuleTriangleVisual visual)
    {
        if (visual is null || visual.Triangles is null || visual.Triangles.Count > MaxTriangles)
            throw new InvalidDataException("Module triangles exceed their budget or are missing.");
        foreach (var triangle in visual.Triangles)
        {
            Vertex(triangle.A); Vertex(triangle.B); Vertex(triangle.C);
            var cross = Vector3.Cross(triangle.B.Position - triangle.A.Position, triangle.C.Position - triangle.A.Position);
            if (cross.LengthSquared() < 1e-14f || Vector3.Dot(cross, triangle.A.Normal) <= 0 ||
                Vector3.Dot(cross, triangle.B.Normal) <= 0 || Vector3.Dot(cross, triangle.C.Normal) <= 0)
                throw new InvalidDataException("Degenerate triangle or winding opposed to normals.");
        }
    }

    private static void Vertex(ModuleVertex vertex)
    {
        var p = vertex.Position; var n = vertex.Normal; var c = vertex.Color;
        if (!Finite(p) || Math.Abs(p.X) >= 16 || Math.Abs(p.Y) >= 16 || Math.Abs(p.Z) >= 16 ||
            !Finite(n) || Math.Abs(n.LengthSquared() - 1) > .001f || !Finite(c) ||
            c.X is < 0 or > 1 || c.Y is < 0 or > 1 || c.Z is < 0 or > 1)
            throw new InvalidDataException("Invalid triangle position, unit normal or color.");
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
