using SN = System.Numerics;

// The hall's roll-up door (HallShell's front wall), which a click opens onto the streets of Ottawa (OttawaStreets) and
// closes again: its steel curtain of slats rolls up into its box over Roll seconds, gathered toward its top, and the
// camera may go out through the opening once it is open past Passable.
internal sealed class StreetDoor
{
    internal const double Roll = 3.2; // seconds from shut to open
    internal const float Passable = 0.8f; // how open before the body fits under the curtain
    private const int Slats = 36;
    private const float Gathered = 0.04f; // what is left of the curtain's height when open: the roll in its box

    private double openness, wanted;

    internal bool Open => wanted > 0.5;

    internal float Openness => (float)openness;

    internal bool Passing => openness >= Passable;

    // Whether it moves, so that the caller keeps drawing.
    internal bool Moving => openness != wanted;

    internal void Toggle() => wanted = Open ? 0 : 1;

    internal void Step(double dt) =>
        openness = wanted > openness ? Math.Min(wanted, openness + (dt / Roll)) : Math.Max(wanted, openness - (dt / Roll));

    // The curtain's slats in the door's frame: its middle at x 0, the floor at y 0, its face just inside the wall.
    internal static Vertex[] Curtain()
    {
        var o = new List<Vertex>();
        for (var k = 0; k < Slats; k++)
        {
            var tone = 0.85f + (0.15f * (k % 2));
            Boxes.Add(o, new(0f, 0.05f + ((k + 0.5f) * HallShell.DoorHeight / Slats), -0.03f), new(HallShell.DoorWidth, (HallShell.DoorHeight / Slats) - 0.008f, 0.03f),
                new Finish(new SN.Vector3(0.16f, 0.17f, 0.18f) * tone, Material.Steel));
        }

        return [.. o];
    }

    // Where the curtain is drawn: gathered up toward its top as it opens, eased, in the wall at the door's place.
    internal SN.Matrix4x4 World()
    {
        var t = (float)openness;
        var eased = t * t * (3f - (2f * t));
        var top = 0.05f + HallShell.DoorHeight;
        var height = 1f - ((1f - Gathered) * eased);
        return SN.Matrix4x4.CreateTranslation(0f, -top, 0f) * SN.Matrix4x4.CreateScale(1f, height, 1f) * SN.Matrix4x4.CreateTranslation(HallShell.DoorX, top, HallShell.Front);
    }

    // The door's opening as a rectangle (left, bottom, right, top) at z 0 in Frame, for HallGallery.OnRect.
    internal static readonly SN.Matrix4x4 Frame = SN.Matrix4x4.CreateTranslation(HallShell.DoorX, 0f, HallShell.Front - 0.05f);

    internal static readonly SN.Vector4 Rect = new(-HallShell.DoorWidth / 2, 0.05f, HallShell.DoorWidth / 2, HallShell.DoorHeight);

    internal static void SelfTest(Action<bool, string> check)
    {
        var door = new StreetDoor();
        var shut = door.World();
        var bottom = SN.Vector3.Transform(new(0f, 0.05f, -0.03f), shut);
        check(MathF.Abs(bottom.Y - 0.05f) < 1e-4f && MathF.Abs(bottom.X - HallShell.DoorX) < 1e-4f, "shut, the curtain reaches the floor at the door's place");
        door.Toggle();
        for (var i = 0; i < 100; i++) door.Step(Roll / 50);
        var open = SN.Vector3.Transform(new(0f, 0.05f, -0.03f), door.World());
        check(door.Open && door.Passing && !door.Moving && open.Y > HallShell.DoorHeight - 0.2f, $"open, it is gathered in its box, the opening clear ({open.Y:F2} m)");
        door.Toggle();
        door.Step(Roll / 2);
        check(!door.Open && door.Moving && door.Openness is > 0.45f and < 0.55f, "a second click rolls it down again, as slowly");
        var hit = HallGallery.OnRect(SN.Matrix4x4.CreateLookAt(new(HallShell.DoorX, 1.6f, 2f), new(HallShell.DoorX, 1.6f, 6f), SN.Vector3.UnitY)
            * SN.Matrix4x4.CreatePerspectiveFieldOfView(0.75f, 16f / 9f, 0.1f, 40f), Frame, Rect, 1600, 900, 800, 450);
        check(hit is { } h && MathF.Abs(h.U - 0.5f) < 0.01f, "a click at the middle of the screen, facing the door, falls on it");
    }
}
