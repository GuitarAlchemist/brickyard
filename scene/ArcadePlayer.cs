using SN = System.Numerics;

// The small robot that plays PARSEC at the cabinet: Delta's twin at six tenths of its size, standing on two crates so
// that its hands reach the panel. Its right hand holds the stick and its left the buttons, moving with the keys the pilot
// (ParsecPilot.js) holds, and its head looks up at the screen. The film (ArcadeFilm) and the hall's own game, played
// while nobody else does (ArcadeStage), pose it alike.
internal sealed class ArcadePlayer
{
    private const float Size = 0.6f; // of Delta's
    internal const float Crates = 0.52f; // two crates, for the robot to reach the panel
    private const float Standing = 0.54f; // the robot's centre, in front of the cabinet, its chest clear of the panel

    // The robot's frame in the cabinet's: smaller, turned to face it, on its crates.
    private static readonly SN.Matrix4x4 Stand =
        SN.Matrix4x4.CreateScale(Size) * SN.Matrix4x4.CreateRotationY(MathF.PI) * SN.Matrix4x4.CreateTranslation(0f, Crates, Standing);

    private static readonly SN.Matrix4x4 FromCabinet = SN.Matrix4x4.Invert(Stand, out var inverse) ? inverse : SN.Matrix4x4.Identity;

    // The robot's frame in the hall.
    public static SN.Matrix4x4 Place { get; } = Stand * ArcadeCabinet.World;

    // A point of the cabinet's in the robot's frame.
    private static SN.Vector3 Local(float x, float y, float z) => SN.Vector3.Transform(new(x, y, z), FromCabinet);

    public static Vertex[] CrateMesh()
    {
        var crates = new List<Vertex>();
        var wood = new Finish(new(0.42f, 0.27f, 0.13f), Material.Matte);
        var slat = new Finish(new(0.3f, 0.18f, 0.08f), Material.Matte);
        var each = Crates / 2f;
        for (var k = 0; k < 2; k++)
        {
            // The upper crate a little smaller and turned, as stacked by hand.
            var (width, turn) = k == 0 ? (0.5f, 0f) : (0.44f, 0.12f);
            var box = new List<Vertex>();
            Boxes.Add(box, new(0f, each / 2f, 0f), new(width, each - 0.004f, width), wood);
            foreach (var y in new[] { 0.25f, 0.75f })
            {
                Boxes.Add(box, new(0f, each * y, 0f), new(width + 0.01f, 0.03f, width + 0.01f), slat);
            }

            var frame = SN.Matrix4x4.CreateRotationY(turn) * SN.Matrix4x4.CreateTranslation(0f, k * each, Standing) * ArcadeCabinet.World;
            crates.AddRange(box.Select(v => new Vertex(SN.Vector3.Transform(v.Pos, frame), SN.Vector3.TransformNormal(v.Normal, frame), v.Color, v.Material, SN.Vector3.TransformNormal(v.Tangent, frame), v.Edge)));
        }

        return [.. crates];
    }

    private float stick, push, press, button; // the hands' moves, eased

    // The robot at play, posed by Robot.Pose in its own frame, then its arms reach for the stick and the buttons, its
    // head looks up at the screen, and it all stands on the crates. holding says whether a key is held, by its code.
    public void Pose(float dt, Func<string, bool> holding, Span<SN.Matrix4x4> world)
    {
        var k = 1f - MathF.Exp(-25f * dt);
        float Axis(string a, string b) => (holding(a) ? 1f : 0f) - (holding(b) ? 1f : 0f);
        stick += (Axis("ArrowRight", "ArrowLeft") - stick) * k;
        push += (Axis("ArrowDown", "ArrowUp") - push) * k;
        press += ((holding("Space") ? 1f : 0f) - press) * k;
        button += ((holding("Digit1") ? -1f : holding("Digit3") ? 1f : 0f) - button) * (1f - MathF.Exp(-6f * dt));

        // The stick's knob, leaning as the player pushes it (right is the cabinet's +x, up away from the player), and
        // the middle button, pressed with the fire key.
        var knob = Local(-0.14f + (0.02f * stick), 1.06f, 0.31f + (0.02f * push));
        var key = Local(0.11f + (0.08f * button), 0.99f - (0.012f * press), 0.31f);
        var bob = world[Robot.Torso];
        world[Robot.ArmRight] = Reach(new(0.32f, 0.68f, 0f), knob) * bob;
        world[Robot.ArmLeft] = Reach(new(-0.32f, 0.68f, 0f), key) * bob;

        // The head looks up at the screen, and a little after the ship.
        var tilt = SN.Matrix4x4.CreateTranslation(0f, -0.86f, 0f) * SN.Matrix4x4.CreateRotationX(-0.3f)
            * SN.Matrix4x4.CreateRotationY(0.05f * stick) * SN.Matrix4x4.CreateTranslation(0f, 0.86f, 0f);
        foreach (var part in new[] { Robot.Head, Robot.EyeLeft, Robot.EyeRight, Robot.Mouth, Robot.Tip, Robot.BrowLeft, Robot.BrowRight,
            Robot.CornerLeft, Robot.CornerRight, Robot.EarLeft, Robot.EarRight })
        {
            world[part] *= tilt;
        }

        for (var i = 0; i < world.Length; i++)
        {
            world[i] *= Place;
        }
    }

    // An arm hung from its shoulder, turned so that its gripper, 0.45 m down, points at the target.
    private static SN.Matrix4x4 Reach(SN.Vector3 shoulder, SN.Vector3 target)
    {
        var to = SN.Vector3.Normalize(target - shoulder);
        var down = -SN.Vector3.UnitY;
        var axis = SN.Vector3.Cross(down, to);
        var turn = axis.LengthSquared() < 1e-8f ? SN.Quaternion.Identity
            : SN.Quaternion.CreateFromAxisAngle(SN.Vector3.Normalize(axis), MathF.Acos(Math.Clamp(SN.Vector3.Dot(down, to), -1f, 1f)));
        return SN.Matrix4x4.CreateFromQuaternion(turn) * SN.Matrix4x4.CreateTranslation(shoulder);
    }

    internal static void SelfTest(Action<bool, string> check)
    {
        // In the robot's frame the arms are 0.45 long, with fingers to 0.495: each hand reaches its control.
        foreach (var (shoulder, target, what) in new[] { (new SN.Vector3(0.32f, 0.68f, 0f), Local(-0.14f, 1.06f, 0.31f), "stick"), (new(-0.32f, 0.68f, 0f), Local(0.11f, 0.99f, 0.31f), "buttons") })
        {
            var hand = SN.Vector3.Transform(new(0f, -0.45f, 0f), Reach(shoulder, target));
            var gap = SN.Vector3.Distance(shoulder, target) - 0.45f;
            check(gap is > -0.1f and < 0.05f && SN.Vector3.Distance(SN.Vector3.Normalize(hand - shoulder), SN.Vector3.Normalize(target - shoulder)) < 1e-3f,
                $"the robot's hand reaches the {what} ({gap * 100:F1} cm short)");
        }

        var chest = SN.Vector3.Transform(new(0f, 0.6f, 0.2f), Stand);
        var top = SN.Vector3.Transform(new(0f, 1.23f, 0f), Stand);
        check(chest.Z > 0.4f && top.Y < 1.3f, $"the robot's chest is clear of the panel ({chest.Z:F2}) and its head below the screen's middle ({top.Y:F2})");
    }
}
