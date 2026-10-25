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
