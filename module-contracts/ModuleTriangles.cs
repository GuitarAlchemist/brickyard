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
public sealed record ModuleTriangleVisual(IReadOnlyList<ModuleTriangle> Triangles)
{
    // Optional (V1.3): one of the host's images that the first Uvs.Count triangles are painted with instead of their
    // colours. A host without that image draws them in their colours, so the colours stay a fair likeness.
    public ModuleTriangleImage? Image { get; init; }
}

// Where each corner of a triangle falls on the image, (0, 0) its top left and (1, 1) its bottom right. A globe's
// image is equirectangular: u from 180° W to 180° E, v from the north pole to the south.
public readonly record struct ModuleTriangleUv(Vector2 A, Vector2 B, Vector2 C);

// Name: an image the host keeps (such as "earth"), never a path; the module ships no image bytes.
public sealed record ModuleTriangleImage(string Name, IReadOnlyList<ModuleTriangleUv> Uvs);

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
        if (visual.Image is { } image)
        {
            if (image.Name is null || image.Uvs is null || !ImageName(image.Name) || image.Uvs.Count > visual.Triangles.Count)
                throw new InvalidDataException("Invalid triangle image name or more coordinates than triangles.");
            foreach (var uv in image.Uvs)
                if (!Uv(uv.A) || !Uv(uv.B) || !Uv(uv.C)) throw new InvalidDataException("Image coordinates outside 0 to 1.");
        }
    }

    // A lowercase name of 1 to 32 letters, digits and dashes, starting with a letter: nothing a path could hide in.
    private static bool ImageName(string name) =>
        name.Length is >= 1 and <= 32 && name[0] is >= 'a' and <= 'z' && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool Uv(Vector2 uv) => float.IsFinite(uv.X) && float.IsFinite(uv.Y) && uv.X is >= 0 and <= 1 && uv.Y is >= 0 and <= 1;

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
