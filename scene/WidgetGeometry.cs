using Delta.SceneModules;
using SN = System.Numerics;

// A widget instance's own boxes and labels as one mesh's vertices, in the instance's frame: the boxes as the robot's are
// drawn (Boxes.Add, with the Low shader's rounded edges and the box's material), the labels as Gaia's, in shallow boxes.
// Widgets.Expand has already checked every shape.
internal static class WidgetGeometry
{
    public static Vertex[] Compile(WidgetInstance instance)
    {
        List<Vertex> vertices = [];
        foreach (var box in instance.Boxes)
        {
            Boxes.Add(vertices, box.Centre, box.Size, new Finish(box.Color, (Material)box.Material));
        }

        foreach (var text in instance.Text)
        {
            GaiaPresence.Letter(vertices, text.Text, text.Centre, text.Width, new Finish(text.Color, Material.Matte));
        }

        // Surfaces as they are: each corner keeps its own normal and colour, so that a rope reads round and a sheet of
        // paper keeps its two sides. An edge of (0, 0, 1, 1) is far from every edge, so the shader leans no normal.
        var flat = new SN.Vector4(0f, 0f, 1f, 1f);
        foreach (var surface in instance.Surfaces)
        {
            vertices.EnsureCapacity(vertices.Count + (3 * surface.Triangles.Count));
            foreach (var t in surface.Triangles)
            {
                var tangent = SN.Vector3.Normalize(t.B.Position - t.A.Position);
                vertices.Add(new Vertex(t.A.Position, t.A.Normal, t.A.Color, (Material)surface.Material, tangent, flat));
                vertices.Add(new Vertex(t.B.Position, t.B.Normal, t.B.Color, (Material)surface.Material, tangent, flat));
                vertices.Add(new Vertex(t.C.Position, t.C.Normal, t.C.Color, (Material)surface.Material, tangent, flat));
            }
        }

        return [.. vertices];
    }
}
