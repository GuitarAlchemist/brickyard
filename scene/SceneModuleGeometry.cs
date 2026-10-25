using Delta.SceneModules;
using SN = System.Numerics;

internal static class SceneModuleGeometry
{
    public static Vertex[] Compile(ModuleVisual visual, ModulePresentation presentation)
    {
        if (visual.Boxes.Count > 4096 || visual.Text.Count > 16) throw new InvalidDataException("Module geometry exceeds its budget.");
        List<Vertex> vertices = [];
        foreach (var box in visual.Boxes)
        {
            if (!Point(box.Centre) || !Point(box.Size) || box.Size.X <= 0 || box.Size.Y <= 0 || box.Size.Z <= 0 || !Color(box.Color) || box.Material > (uint)Material.Glass)
                throw new InvalidDataException("Invalid module box.");
            Boxes.Add(vertices, box.Centre, box.Size, new Finish(box.Color, (Material)box.Material));
        }
        foreach (var text in visual.Text) Text(vertices, text);

        // This version is read from the code image loaded by the host, not supplied as cosmetic module text.
        var dark = new SN.Vector3(0.055f, 0.045f, 0.08f);
        Boxes.Add(vertices, new(0.88f, 0.166f, 0.35f), new(0.86f, 0.078f, 0.025f), dark);
        var blue = new SN.Vector3(0.2f, 0.55f, 1f);
        var green = new SN.Vector3(0.2f, 0.9f, 0.4f);
        Boxes.Add(vertices, new(0.49f, 0.166f, 0.37f), new(0.025f), presentation.ActiveSlot == "blue" ? blue : green);
        Text(vertices, new($"Gaia v{presentation.Version.ToString(2)} · active", new(0.90f, 0.166f, 0.368f), 0.73f, new(0.84f, 0.76f, 1f)));
        if (presentation.Available is { } available)
        {
            Boxes.Add(vertices, new(0.88f, 0.095f, 0.375f), new(0.98f, 0.06f, 0.025f), dark);
            Boxes.Add(vertices, new(0.43f, 0.095f, 0.395f), new(0.022f), presentation.ActiveSlot == "blue" ? green : blue);
            var label = presentation.Language == "fr" ? $"v{available.ToString(2)} candidate — cliquer" : $"v{available.ToString(2)} candidate — click";
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
        return vertices.ToArray();
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

internal readonly record struct ModulePickRect(float Left, float Top, float Right, float Bottom)
{
    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
}

internal static class SceneModulePicking
{
    public static void SelfTest(Action<bool, string> check)
    {
        var projection = SN.Matrix4x4.CreatePerspectiveFieldOfView(0.75f, 1280f / 720f, 0.1f, 40f);
        projection.M22 *= -1;
        var vp = SN.Matrix4x4.CreateLookAt(new(0, 1.08f, 2.55f), new(0, 0.7f, 0), SN.Vector3.UnitY) * projection;
        var rect = VersionRect(vp, 1280, 720);
        check(rect is { } small && small.Contains(990, 600) && !small.Contains(20, 20), "the actual 3D version plaque projects to its clickable screen area");
        check(VersionRect(vp, 2560, 1440) is { } large && large.Contains(1980, 1200), "module picking follows the existing window/framebuffer resize scale");
        check(VersionRect(default, 1280, 720) is null && VersionRect(vp, 0, 720) is null, "an invalid projection cannot request a module replacement");
    }

    // The clickable version plaque is in the world, so project its bounds through the actual, moving camera.
    public static ModulePickRect? VersionRect(SN.Matrix4x4 viewProjection, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var left = float.PositiveInfinity;
        var top = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var bottom = float.NegativeInfinity;
        for (var i = 0; i < 8; i++)
        {
            var p = SN.Vector4.Transform(new SN.Vector4((i & 1) == 0 ? 0.38f : 1.38f, (i & 2) == 0 ? 0.06f : 0.21f, (i & 4) == 0 ? 0.33f : 0.41f, 1f), viewProjection);
            if (!float.IsFinite(p.W) || p.W <= 0) return null;
            var x = (p.X / p.W + 1f) * width * 0.5f;
            var y = (p.Y / p.W + 1f) * height * 0.5f;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(right) || !float.IsFinite(bottom)) return null;
        return new(left, top, right, bottom);
    }
}
