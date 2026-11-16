using Delta.SceneModules;
using System.Numerics;

// Bounded offline preview of the actual validated triangles. No Vulkan device/window/process.
// This demonstrates shape/color/normals only; it is not proof of the live renderer or click UI.
internal static class CpuPreview
{
    public static void Save(ModuleTriangleVisual mesh, string path)
    {
        ModuleTriangleValidation.Validate(mesh);
        const int size = 640;
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            pixels[4 * i] = 12; pixels[4 * i + 1] = 18; pixels[4 * i + 2] = 26; pixels[4 * i + 3] = 255;
        }
        var depth = Enumerable.Repeat(float.NegativeInfinity, size * size).ToArray();
        var centre = new Vector3(.88f, .69f, .16f);
        var eye = centre + new Vector3(-.8f, .5f, 3);
        var view = Matrix4x4.CreateLookAt(eye, centre, Vector3.UnitY);
        var light = Vector3.Normalize(new Vector3(-.4f, .8f, 1));
        Vector3 Project(Vector3 position)
        {
            var p = Vector3.Transform(position, view);
            return new(size * (.5f + p.X / .74f), size * (.5f - p.Y / .74f), p.Z);
        }
        foreach (var triangle in mesh.Triangles)
        {
            var (va, vb, vc) = triangle;
            var face = Vector3.Cross(vb.Position - va.Position, vc.Position - va.Position);
            if (Vector3.Dot(face, eye - va.Position) <= 0) continue;
            var a = Project(va.Position); var b = Project(vb.Position); var c = Project(vc.Position);
            var area = Edge(b, c, a.X, a.Y);
            if (Math.Abs(area) < .0001f) continue;
            var minX = Math.Clamp((int)MathF.Floor(Math.Min(a.X, Math.Min(b.X, c.X))), 0, size - 1);
            var maxX = Math.Clamp((int)MathF.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))), 0, size - 1);
            var minY = Math.Clamp((int)MathF.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))), 0, size - 1);
            var maxY = Math.Clamp((int)MathF.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))), 0, size - 1);
            for (var y = minY; y <= maxY; y++)
                for (var x = minX; x <= maxX; x++)
                {
                    var u = Edge(b, c, x + .5f, y + .5f) / area;
                    var v = Edge(c, a, x + .5f, y + .5f) / area;
                    var w = 1 - u - v;
                    if (u < 0 || v < 0 || w < 0) continue;
                    var z = u * a.Z + v * b.Z + w * c.Z;
                    var index = y * size + x;
                    if (z <= depth[index]) continue;
                    depth[index] = z;
                    var normal = Vector3.Normalize(u * va.Normal + v * vb.Normal + w * vc.Normal);
                    var color = (u * va.Color + v * vb.Color + w * vc.Color) *
                        (.32f + .68f * Math.Max(0, Vector3.Dot(normal, light)));
                    pixels[index * 4] = Channel(color.X);
                    pixels[index * 4 + 1] = Channel(color.Y);
                    pixels[index * 4 + 2] = Channel(color.Z);
                }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Png.Save(path, pixels, size, size, bgra: false);
        Console.WriteLine($"CPU PREVIEW {Path.GetFullPath(path)}; {size}x{size}; static actual triangles, Lambert lighting; no GPU");
    }
    private static float Edge(Vector3 a, Vector3 b, float x, float y) => (x - a.X) * (b.Y - a.Y) - (y - a.Y) * (b.X - a.X);
    private static byte Channel(float linear) => (byte)Math.Clamp((int)(255 * MathF.Pow(Math.Clamp(linear, 0, 1), 1 / 2.2f)), 0, 255);
}
