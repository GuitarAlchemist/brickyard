using Delta.SceneModules;

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

        return [.. vertices];
    }
}
