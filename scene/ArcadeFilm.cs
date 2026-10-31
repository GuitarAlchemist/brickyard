using SN = System.Numerics;

// The arcade's film (--film-arcade DIR): 45 s of a small robot, Delta's twin at six tenths of its size and standing on
// two crates, playing PARSEC at the cabinet, filmed in the hall's two manners after GTA V. First a view's flight as the
// character switch makes it, from the whole hall up and down to the cabinet; then a cut scene between bars with an
// operator's sway, five shots that keep the game's screen in sight, over or beside the robot's head; then a flight back
// out to the hall. The robot's right hand holds the stick and its left the buttons, moving with the keys the pilot
// (ParsecPilot.js) presses. The film's time is the scene's, so that every frame is recorded whatever it takes to draw.
internal sealed class ArcadeFilm(FreeCamera camera, Cinematic film)
{
    public const float Seconds = 45f;
    private const float FlyIn = 0.6f; // the hall, then the flight down to the cabinet
    private const float CutScene = 4.2f; // the bars come in over the landed view
    private const float FlyOut = 41.9f; // after the cut scene gives the view back, the flight out to the hall
    private const float Size = 0.6f; // of Delta's
    private const float Crates = 0.52f; // two crates, for the robot to reach the panel
    private const float Standing = 0.54f; // the robot's centre, in front of the cabinet, its chest clear of the panel

    private static SN.Vector3 At(float x, float y, float z) => SN.Vector3.Transform(new(x, y, z), ArcadeCabinet.World);

    private static Cinematic.Curve Path(params SN.Vector3[] points) => new([.. points.Select(p => At(p.X, p.Y, p.Z))]);

    // Where the flight lands: high and to the right, away from the steel cabinet on the left, over the robot's shoulder, the whole screen clear of its head.
    private static readonly (SN.Vector3 Eye, SN.Vector3 Target) Landing = (At(1.0f, 2.35f, 1.7f), At(0f, 1.2f, 0.2f));

    // The cut scene's shots, in the cabinet's frame: x across, y up, z towards the player.
    public static readonly Cinematic.Shot[] Shots =
    [
        new("approach", 6.5f, 0.8f, 0.25f, 38f, 34f, Path(new(1.4f, 1.5f, 1.7f), new(1.0f, 1.75f, 1.3f), new(0.7f, 1.95f, 1.05f)),
            Path(new(0f, 1.2f, 0.25f), new(0f, 1.25f, 0.12f)), DipIn: true),
        new("screen", 6.5f, 0.7f, 0.2f, 30f, 25f, Path(new(0.35f, 1.9f, 1.25f), new(0.25f, 1.75f, 0.95f)),
            Path(new(0f, 1.27f, 0.1f), new(0f, 1.27f, 0.12f))),
        new("hands", 5.5f, 0.8f, 0.3f, 38f, 34f, Path(new(0.95f, 1.35f, 0.95f), new(0.75f, 1.4f, 0.8f)),
            Path(new(0f, 1.05f, 0.35f), new(0f, 1.2f, 0.25f)), Out: "dip"),
        new("marquee", 5.9f, 0.8f, 0.2f, 34f, 30f, Path(new(0.1f, 2.15f, 1.35f), new(0.05f, 1.95f, 1.15f)),
            Path(new(0f, 1.71f, 0.14f), new(0f, 1.27f, 0.12f)), DipIn: true),
        new("orbit", 6.0f, 0.9f, 0.2f, 36f, 40f, Path(new(1.2f, 1.85f, 1.25f), new(0.5f, 2.1f, 1.6f), new(-0.35f, 2.15f, 1.6f)),
            Path(new(0f, 1.2f, 0.3f), new(0f, 1.2f, 0.3f)), Out: "dip"),
    ];

    // The pilot that plays the game by its keys, built into the scene.
    public static string Pilot()
    {
        using var stream = typeof(ArcadeFilm).Assembly.GetManifestResourceStream("ParsecPilot.js") ?? throw new InvalidDataException("No pilot in the scene");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static (string Title, string Under) Heading =>
        ("PARSEC", Words.Say("A small robot at the arcade", "Un petit robot à la borne d'arcade"));

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
    public float Time { get; private set; } = -1f;

    public bool Rolling => Time >= 0f && Time < Seconds;

    public bool Done => Time >= Seconds;

    // Starts the film: the camera cut to the whole hall.
    public void Start()
    {
        Time = 0f;
        camera.GoTo(4, cut: true);
    }

    public void Advance(float dt)
    {
        if (!Rolling)
        {
            return;
        }

        var before = Time;
        Time += dt;
        bool Crossed(float at) => before < at && Time >= at;
        if (Crossed(FlyIn))
        {
            camera.GoTo(Landing.Eye, Landing.Target);
        }

        if (Crossed(CutScene))
        {
            film.Play();
        }

        if (Crossed(FlyOut))
        {
            camera.GoTo(4);
        }
    }

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

    // The film's sound as a 16-bit mono WAV file at 44.1 kHz, for FFmpeg to put with the picture.
    public static void SaveWav(string path, ReadOnlySpan<float> samples)
    {
        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8);
        file.Write(36 + (samples.Length * 2));
        file.Write("WAVEfmt "u8);
        file.Write(16);
        file.Write((short)1);
        file.Write((short)1);
        file.Write(ScriptAudio.SampleRate);
        file.Write(ScriptAudio.SampleRate * 2);
        file.Write((short)2);
        file.Write((short)16);
        file.Write("data"u8);
        file.Write(samples.Length * 2);
        foreach (var sample in samples)
        {
            file.Write((short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
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
        check(CutScene + 0.93f + 2.4f + Shots.Sum(s => s.Seconds) + 3.2f + 0.6f <= FlyOut && FlyOut + 2.4f <= Seconds,
            "the cut scene gives the view back before the flight out, which lands before the end");
    }
}
