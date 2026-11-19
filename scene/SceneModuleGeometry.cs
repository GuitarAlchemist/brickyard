using Delta.SceneModules;
using SN = System.Numerics;

// A module's meshes on the GPU: its labels and boxes, and apart from them the triangles the hand may turn (GaiaTurn).
internal sealed class ModuleSurface(Mesh rest, Mesh? turning, SN.Vector3 centre, float radius) : IDisposable
{
    public Mesh Rest => rest;
    public Mesh? Turning => turning;
    public SN.Vector3 Centre => centre;
    public float Radius => radius;

    public static ModuleSurface Make(Vertex[] all, Func<Vertex[], Mesh> upload)
    {
        var (r, t, c, radius) = SceneModuleGeometry.Split(all);
        var restMesh = upload(r);
        try
        {
            return new(restMesh, t.Length > 0 ? upload(t) : null, c, radius);
        }
        catch
        {
            restMesh.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { rest.Dispose(); }
        finally { turning?.Dispose(); }
    }
}

internal static class SceneModuleGeometry
{
    public static Vertex[] Compile(ModuleVisual visual, ModulePresentation presentation) => Compile(visual, null, presentation);

    // triangles is the host's checked copy (LoadedSceneModule.RenderTriangles), drawn matte after the boxes and labels.
    public static Vertex[] Compile(ModuleVisual visual, ModuleTriangleVisual? triangles, ModulePresentation presentation)
    {
        if (visual.Boxes.Count > 4096 || visual.Text.Count > 16) throw new InvalidDataException("Module geometry exceeds its budget.");
        List<Vertex> vertices = [];
        foreach (var box in visual.Boxes)
        {
            if (!Point(box.Centre) || !Point(box.Size) || box.Size.X <= 0 || box.Size.Y <= 0 || box.Size.Z <= 0 || !Color(box.Color) || box.Material > (uint)Material.Glass)
                throw new InvalidDataException("Invalid module box.");
            Boxes.Add(vertices, box.Centre, box.Size, new Finish(box.Color, (Material)box.Material));
        }
        foreach (var text in visual.Text) Text(vertices, text with { Text = GaiaPresence.Bridged(text.Text, presentation.Bridge, presentation.Language) });

        // This version is read from the code image loaded by the host, not supplied as cosmetic module text.
        var dark = new SN.Vector3(0.055f, 0.045f, 0.08f);
        Boxes.Add(vertices, new(0.88f, 0.166f, 0.35f), new(0.86f, 0.078f, 0.025f), dark);
        var blue = new SN.Vector3(0.2f, 0.55f, 1f);
        var green = new SN.Vector3(0.2f, 0.9f, 0.4f);
        Boxes.Add(vertices, new(0.49f, 0.166f, 0.37f), new(0.025f), presentation.ActiveSlot == "blue" ? blue : green);
        Text(vertices, new(presentation.ActiveLabel, new(0.90f, 0.166f, 0.368f), 0.73f, new(0.84f, 0.76f, 1f)));
        if (presentation.Available is { } available)
        {
            Boxes.Add(vertices, new(0.88f, 0.095f, 0.375f), new(0.98f, 0.06f, 0.025f), dark);
            Boxes.Add(vertices, new(0.43f, 0.095f, 0.395f), new(0.022f), presentation.ActiveSlot == "blue" ? green : blue);
            var label = available < presentation.Version
                ? (presentation.Language == "fr" ? $"Revenir à v{available.ToString(2)}" : $"Revert to v{available.ToString(2)}")
                : (presentation.Language == "fr" ? $"Gaia v{available.ToString(2)} disponible" : $"Gaia v{available.ToString(2)} available");
            Text(vertices, new(label, new(0.91f, 0.095f, 0.395f), 0.86f, new(1f, 0.76f, 0.35f)));
        }
        else if (presentation.Error is not null)
        {
            var label = presentation.Language == "fr" ? "Mise à jour refusée" : "Update rejected";
            Text(vertices, new(label, new(0.88f, 0.095f, 0.395f), 0.80f, new(1f, 0.5f, 0.4f)));
        }
        else if (presentation.Retired is { } retired)
        {
            Boxes.Add(vertices, new(0.49f, 0.095f, 0.395f), new(0.022f), presentation.ActiveSlot == "blue" ? green : blue);
            var label = presentation.Language == "fr" ? $"v{retired.ToString(2)} retirée" : $"v{retired.ToString(2)} retired";
            Text(vertices, new(label, new(0.91f, 0.095f, 0.395f), 0.60f, new(0.6f)));
        }
        var preparation = presentation.PreparationLines;
        if (preparation.Length > 0)
        {
            var height = 0.48f + Math.Max(0, preparation.Length - 6) * 0.07f;
            Boxes.Add(vertices, new(0.82f, 1.49f - height / 2, 0.35f), new(1.18f, height, 0.025f), dark);
            for (var line = 0; line < preparation.Length; line++)
            {
                var width = Math.Min(1.08f, preparation[line].Length * 0.028f);
                Text(vertices, new(preparation[line], new(0.28f + width / 2, 1.43f - 0.07f * line, 0.37f), width, new(0.84f, 0.78f, 1f)));
            }
        }
        if (triangles is not null) Triangles(vertices, triangles);
        return vertices.ToArray();
    }

    // The module's triangles painted with an image (Gaia's planet) apart from the rest, so that the host can turn them by
    // hand (GaiaTurn); their centre and radius. Without such triangles Turning is empty.
    public static (Vertex[] Others, Vertex[] Turning, SN.Vector3 Centre, float Radius) Split(Vertex[] all)
    {
        var turning = all.Where(v => v.Material == Material.Imaged).ToArray();
        var rest = all.Where(v => v.Material != Material.Imaged).ToArray();
        if (turning.Length == 0) return (rest, turning, default, 0f);
        var (lo, hi) = (turning[0].Pos, turning[0].Pos);
        foreach (var v in turning)
        {
            lo = SN.Vector3.Min(lo, v.Pos);
            hi = SN.Vector3.Max(hi, v.Pos);
        }

        return (rest, turning, (lo + hi) / 2, (hi.X - lo.X) / 2);
    }

    // The images the host holds for module triangles, by name, each its first layer in the Low shader's globe images
    // (Material.Imaged); set once they are loaded. A name it does not hold leaves those triangles in their colours.
    public static IReadOnlyDictionary<string, int> Images { get; set; } = new Dictionary<string, int>();

    // A triangle has no face edge to round: Edge (0, 0, 1, 1) keeps the Low shader's lean at zero, and the tangent, one of
    // the triangle's sides, is never parallel to its normals, so the shader's normalize stays defined. A triangle painted
    // with an image carries its corner's place on it in Edge instead, (u, v, the image's first layer, 0).
    private static void Triangles(List<Vertex> vertices, ModuleTriangleVisual triangles)
    {
        if (triangles.Triangles.Count > ModuleTriangleValidation.MaxTriangles) throw new InvalidDataException("Module triangles exceed their budget.");
        var flat = new SN.Vector4(0f, 0f, 1f, 1f);
        var uvs = triangles.Image is { } image && Images.TryGetValue(image.Name, out var layer) ? (image.Uvs, Layer: layer) : default;
        vertices.EnsureCapacity(vertices.Count + (3 * triangles.Triangles.Count));
        for (var i = 0; i < triangles.Triangles.Count; i++)
        {
            var t = triangles.Triangles[i];
            var tangent = SN.Vector3.Normalize(t.B.Position - t.A.Position);
            if (uvs.Uvs is not null && i < uvs.Uvs.Count)
            {
                var uv = uvs.Uvs[i];
                vertices.Add(new Vertex(t.A.Position, t.A.Normal, t.A.Color, Material.Imaged, tangent, new(uv.A, uvs.Layer, 0f)));
                vertices.Add(new Vertex(t.B.Position, t.B.Normal, t.B.Color, Material.Imaged, tangent, new(uv.B, uvs.Layer, 0f)));
                vertices.Add(new Vertex(t.C.Position, t.C.Normal, t.C.Color, Material.Imaged, tangent, new(uv.C, uvs.Layer, 0f)));
                continue;
            }

            foreach (var v in (ReadOnlySpan<ModuleVertex>)[t.A, t.B, t.C])
                vertices.Add(new Vertex(v.Position, v.Normal, v.Color, Material.Matte, tangent, flat));
        }
    }

    private static void Text(List<Vertex> vertices, ModuleText text)
    {
        if (text.Text.Length is < 1 or > 128 || text.Text.Any(char.IsControl) || !Point(text.Centre) || !float.IsFinite(text.Width) || text.Width is < 0.02f or > 2f || !Color(text.Color))
            throw new InvalidDataException("Invalid module label.");
        GaiaPresence.Letter(vertices, text.Text, text.Centre, text.Width, new Finish(text.Color, Material.Matte));
    }

    private static bool Point(SN.Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) && Math.Abs(p.X) < 16 && Math.Abs(p.Y) < 16 && Math.Abs(p.Z) < 16;
    private static bool Color(SN.Vector3 p) => Point(p) && p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1 && p.Z is >= 0 and <= 1;
}
