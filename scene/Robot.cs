using SN = System.Numerics;

// The robot: boxes in parts that move. Each part's vertices are in its own frame, and Pose gives every part its world
// matrix and its glow for the moment, from what the brain says the robot is doing and how loud its voice is.
internal sealed class Robot
{
    public const int Base = 0, Torso = 1, Head = 2, EyeLeft = 3, EyeRight = 4, Mouth = 5, Tip = 6, ArmLeft = 7, ArmRight = 8, Chest = 9;
    public const int ChestLights = 5;
    public const int Cost = Chest + ChestLights; // a lamp above the chest panel: green when an answer is free, gold when Claude's
    public const int Spark = Cost + 1; // a star beside the head while Claude has the conversation
    public const int Leds = Spark + 1; // the status lights of the Mac minis the torso is stacked from, a part per layer
    public const int Layers = 7;
    public const int EarLeft = Leds + Layers, EarRight = EarLeft + 1; // pads on the ears, lit while Delta listens
    public const int BrowLeft = EarRight + 1, BrowRight = BrowLeft + 1; // faint until an expression needs them
    public const int CornerLeft = BrowRight + 1, CornerRight = CornerLeft + 1; // the mouth's corners, up for a smile
    public const int HeartFirst = CornerRight + 1, HeartCount = 7; // hearts that rise when someone tells Delta they love it
    public const int Parts = HeartFirst + HeartCount;
    private const float HeartLife = 2.6f; // seconds each heart rises, the next starting HeartStep after it
    private const float HeartStep = 0.2f;

    private static readonly SN.Vector3 HeadPivot = new(0f, 0.84f, 0f);
    private static readonly SN.Vector3 Cyan = new(0.35f, 0.8f, 1f);
    private static readonly SN.Vector3 Amber = new(1f, 0.52f, 0.1f);
    private static readonly SN.Vector3 Green = new(0.35f, 1f, 0.5f);
    private static readonly SN.Vector3 Free = new(0.2f, 1f, 0.35f);
    private static readonly SN.Vector3 Paid = new(1f, 0.74f, 0.08f);
    private static readonly SN.Vector3 Terracotta = new(0.85f, 0.47f, 0.34f);
    private static readonly SN.Vector3 Violet = new(0.62f, 0.38f, 1f);
    private static readonly SN.Vector3 Pale = new(0.55f, 0.85f, 1f);
    private static readonly SN.Vector3 Red = new(1f, 0.2f, 0.1f);
    private static readonly SN.Vector3 White = new(0.92f, 0.95f, 1f);
    private static readonly SN.Vector3 Pink = new(1f, 0.1f, 0.28f);
    private const float StackBottom = 0.24f;

    private float listening, thinking, speaking, level, free, paid, compact, gauge, fill;
    private readonly float[] moods = new float[Faces.Length]; // how much each expression shows, eased in and out
    private float presenting;

    // Whether the shelf is open at the window's left, which the robot then looks at and points to.
    public bool Presenting { get; set; }

    public static Vertex[][] Meshes()
    {
        var parts = Enumerable.Range(0, Parts).Select(_ => new List<Vertex>()).ToArray();
        var steel = new SN.Vector3(0.16f, 0.165f, 0.17f);
        var rubber = new SN.Vector3(0.025f, 0.025f, 0.028f);
        var enamel = new SN.Vector3(0.78f, 0.27f, 0.05f);
        var shell = new SN.Vector3(0.7f, 0.68f, 0.62f);
        var glass = new SN.Vector3(0.012f, 0.014f, 0.018f);
        var unlit = new SN.Vector3(0.03f, 0.03f, 0.035f);
        var aluminium = new SN.Vector3(0.6f, 0.61f, 0.64f);

        // A Mac mini standing on its bottom centre: an aluminium slab on a dark foot, a dark port slot on its front and,
        // when lights is given, a strip of status light along the front's outer half (side -1 or 1).
        void MacMini(List<Vertex> body, List<Vertex>? lights, SN.Vector3 bottom, SN.Vector3 size, float side)
        {
            var mid = 0.006f + ((size.Y - 0.006f) / 2f);
            Boxes.Add(body, bottom + new SN.Vector3(0f, 0.003f, 0f), new(size.X - 0.03f, 0.006f, size.Z - 0.03f), rubber);
            Boxes.Add(body, bottom + new SN.Vector3(0f, mid, 0f), new(size.X, size.Y - 0.006f, size.Z), aluminium);
            Boxes.Add(body, bottom + new SN.Vector3(-side * ((size.X / 2f) - 0.045f), mid, size.Z / 2f), new(0.04f, 0.009f, 0.004f), glass);
            if (lights is not null)
            {
                Boxes.Add(lights, bottom + new SN.Vector3(side * size.X * 0.24f, mid, (size.Z / 2f) + 0.001f), new(size.X * 0.42f, 0.018f, 0.004f), unlit);
            }
        }

        // The base: two treads with their cleats, and the plate across them.
        foreach (var side in new[] { -1f, 1f })
        {
            Boxes.Add(parts[Base], new(side * 0.2f, 0.09f, 0f), new(0.14f, 0.18f, 0.56f), rubber);
            for (var i = 0; i < 6; i++)
            {
                Boxes.Add(parts[Base], new(side * 0.274f, 0.09f, -0.225f + (0.09f * i)), new(0.012f, 0.12f, 0.05f), steel);
            }
        }

        Boxes.Add(parts[Base], new(0f, 0.21f, 0f), new(0.5f, 0.06f, 0.42f), steel);

        // The torso: Mac minis stacked seven high, two to a layer, with a dark chest panel on their front, its vents and
        // its row of lights, and the neck.
        var torso = parts[Torso];
        for (var layer = 0; layer < 7; layer++)
        {
            foreach (var side in new[] { -1f, 1f })
            {
                MacMini(torso, parts[Leds + layer], new(side * 0.1405f, StackBottom + (0.0714f * layer), 0f), new(0.275f, 0.068f, 0.4f), side);
            }
        }

        Boxes.Add(torso, new(0f, 0.5f, 0.2f), new(0.36f, 0.26f, 0.012f), glass);
        for (var i = 0; i < 3; i++)
        {
            Boxes.Add(torso, new(0f, 0.55f + (0.035f * i), 0.208f), new(0.26f, 0.012f, 0.006f), steel);
        }

        Boxes.Add(torso, new(0f, 0.8f, 0f), new(0.12f, 0.1f, 0.12f), steel);
        for (var i = 0; i < ChestLights; i++)
        {
            Boxes.Add(parts[Chest + i], new(-0.12f + (0.06f * i), 0.43f, 0.209f), new(0.045f, 0.03f, 0.006f), unlit);
        }

        Boxes.Add(torso, new(0f, 0.685f, 0.203f), new(0.13f, 0.06f, 0.008f), steel);
        Boxes.Add(parts[Cost], new(0f, 0.685f, 0.209f), new(0.1f, 0.035f, 0.006f), unlit);

        // The spark, about its centre: four thin bars crossed into an eight-pointed star, and a hub.
        for (var i = 0; i < 4; i++)
        {
            var bar = new List<Vertex>();
            Boxes.Add(bar, SN.Vector3.Zero, new(0.17f, 0.024f, 0.012f), unlit);
            var turn = SN.Matrix4x4.CreateRotationZ(i * MathF.PI / 4f);
            parts[Spark].AddRange(bar.Select(v => new Vertex(SN.Vector3.Transform(v.Pos, turn), SN.Vector3.TransformNormal(v.Normal, turn), v.Color)));
        }

        Boxes.Add(parts[Spark], SN.Vector3.Zero, new(0.05f, 0.05f, 0.016f), unlit);

        // The head, about the top of the neck: a pale shell, an enamel cap, a dark visor, ears and an antenna. The eyes,
        // the mouth and the antenna's tip are parts of their own, centred, so that they can be scaled where they sit.
        var head = parts[Head];
        Boxes.Add(head, new(0f, 0.19f, 0f), new(0.52f, 0.36f, 0.38f), shell);
        Boxes.Add(head, new(0f, 0.38f, -0.02f), new(0.4f, 0.025f, 0.28f), enamel);
        Boxes.Add(head, new(0f, 0.18f, 0.195f), new(0.44f, 0.25f, 0.012f), glass);
        foreach (var side in new[] { -1f, 1f })
        {
            Boxes.Add(head, new(side * 0.275f, 0.2f, 0f), new(0.03f, 0.15f, 0.17f), steel);
        }

        Boxes.Add(parts[EarLeft], SN.Vector3.Zero, new(0.014f, 0.11f, 0.13f), unlit);
        Boxes.Add(parts[EarRight], SN.Vector3.Zero, new(0.014f, 0.11f, 0.13f), unlit);

        Boxes.Add(head, new(0.14f, 0.48f, -0.06f), new(0.016f, 0.2f, 0.016f), steel);
        Boxes.Add(parts[Tip], SN.Vector3.Zero, new(0.05f, 0.05f, 0.05f), unlit);
        Boxes.Add(parts[BrowLeft], SN.Vector3.Zero, new(0.11f, 0.014f, 0.008f), unlit);
        Boxes.Add(parts[BrowRight], SN.Vector3.Zero, new(0.11f, 0.014f, 0.008f), unlit);
        Boxes.Add(parts[CornerLeft], new(-0.022f, 0f, 0f), new(0.044f, 0.012f, 0.008f), unlit); // from its inner end
        Boxes.Add(parts[CornerRight], new(0.022f, 0f, 0f), new(0.044f, 0.012f, 0.008f), unlit);
        Boxes.Add(parts[EyeLeft], SN.Vector3.Zero, new(0.1f, 0.07f, 0.01f), unlit);
        Boxes.Add(parts[EyeRight], SN.Vector3.Zero, new(0.1f, 0.07f, 0.01f), unlit);
        Boxes.Add(parts[Mouth], SN.Vector3.Zero, new(0.2f, 1f, 0.01f), unlit);

        // The arms, about the shoulders: a shoulder joint, four small Mac minis stacked, a wrist and a two-fingered gripper.
        foreach (var (part, side) in new[] { (ArmLeft, -1f), (ArmRight, 1f) })
        {
            var arm = parts[part];
            Boxes.Add(arm, new(side * 0.02f, 0f, 0f), new(0.08f, 0.12f, 0.15f), steel);
            for (var k = 0; k < 4; k++)
            {
                MacMini(arm, null, new(side * 0.04f, -0.35f + (0.075f * k), 0f), new(0.1f, 0.071f, 0.12f), side);
            }

            Boxes.Add(arm, new(side * 0.04f, -0.38f, 0f), new(0.06f, 0.06f, 0.06f), steel);
            foreach (var finger in new[] { -0.03f, 0.03f })
            {
                Boxes.Add(arm, new(side * 0.04f, -0.45f, finger), new(0.03f, 0.09f, 0.02f), steel);
            }
        }

        // A heart of red cubes about its centre, one for each heart that can rise at once.
        string[] heart = [".XX.XX.", "XXXXXXX", "XXXXXXX", ".XXXXX.", "..XXX..", "...X..."];
        var red = new SN.Vector3(0.55f, 0.04f, 0.1f);
        for (var i = 0; i < HeartCount; i++)
        {
            for (var row = 0; row < heart.Length; row++)
            {
                for (var col = 0; col < heart[row].Length; col++)
                {
                    if (heart[row][col] == 'X')
                    {
                        Boxes.Add(parts[HeartFirst + i], new((col - 3) * 0.032f, (2.5f - row) * 0.032f, 0f), new(0.03f, 0.03f, 0.03f), red);
                    }
                }
            }
        }

        return [.. parts.Select(p => p.ToArray())];
    }

    // Each part's world matrix and glow at time t (seconds), dt after the last pose, at the given moment: what the robot
    // is doing, how loud its voice is (0 to 1), what the answer costs, how full Claude's context is and whether Claude
    // Code is compacting it.
    public void Pose(float t, float dt, Link.Moment m, Span<SN.Matrix4x4> world, Span<SN.Vector4> glow)
    {
        var (state, heard, cost) = (m.State, m.Level, m.Cost);
        var k = 1f - MathF.Exp(-5f * dt);
        free += ((cost == "free" ? 1f : 0f) - free) * k;
        paid += ((cost == "paid" ? 1f : 0f) - paid) * k;
        listening += ((state == "listening" ? 1f : 0f) - listening) * k;
        thinking += ((state == "thinking" ? 1f : 0f) - thinking) * k;
        speaking += ((state == "speaking" ? 1f : 0f) - speaking) * k;
        compact += ((m.Compacting ? 1f : 0f) - compact) * k;
        presenting += ((Presenting ? 1f : 0f) - presenting) * k;
        gauge += ((m.Context >= 0f ? 1f : 0f) - gauge) * k;
        if (m.Context >= 0f)
        {
            fill += (Math.Clamp(m.Context, 0f, 1f) - fill) * (1f - MathF.Exp(-2f * dt)); // drains visibly after a compaction
        }

        // The mouth opens at once and closes a little slower, as a voice's loudness is heard.
        level += (heard - level) * (1f - MathF.Exp((heard > level ? -40f : -12f) * dt));
        var calm = 1f - MathF.Max(MathF.Max(listening, compact), MathF.Max(thinking, speaking));
        var voice = speaking * level;

        // While Claude Code compacts the conversation, the stack of Mac minis is pressed down in slow strokes, and the
        // head and the arms ride down with it.
        var squeeze = compact * (0.05f + (0.025f * (1f + MathF.Sin(t * 4.2f))));
        var press = SN.Matrix4x4.CreateTranslation(0f, -StackBottom, 0f) * SN.Matrix4x4.CreateScale(1f, 1f - squeeze, 1f)
            * SN.Matrix4x4.CreateTranslation(0f, StackBottom, 0f);
        var bob = SN.Matrix4x4.CreateTranslation(0f, (0.008f * MathF.Sin(t * 2.8f)) + (0.012f * voice) + (0.02f * moods[1] * MathF.Abs(MathF.Sin(t * 7f))), 0f);
        var body = press * bob;
        world[Base] = SN.Matrix4x4.Identity;
        world[Torso] = body;
        for (var i = 0; i < ChestLights; i++)
        {
            world[Chest + i] = body;
        }

        world[Cost] = body;
        for (var layer = 0; layer < Layers; layer++)
        {
            world[Leds + layer] = body;
        }

        var neck = SN.Matrix4x4.CreateTranslation(0f, -squeeze * (HeadPivot.Y - StackBottom), 0f) * bob;

        // The spark grows in when a sentence goes to Claude, turns slowly while Claude thinks or speaks, and shrinks away.
        var spin = SN.Matrix4x4.CreateRotationZ(t * 0.9f) * SN.Matrix4x4.CreateRotationY(0.35f * MathF.Sin(t * 0.7f));
        world[Spark] = SN.Matrix4x4.CreateScale(MathF.Max(paid, 0.0001f) * (1f + (0.06f * MathF.Sin(t * 3f)))) * spin
            * SN.Matrix4x4.CreateTranslation(-0.36f, 1.3f + (0.02f * MathF.Sin(t * 1.4f)), 0.05f) * neck;

        // The expression: the one asked for eases in and the others out; a long silence makes Delta sleepy.
        var asked = Array.IndexOf(Link.Moods, m.Mood);
        var drowsy = state == "idle" && m.Quiet > 240f && asked <= 0;
        for (var i = 1; i < Faces.Length; i++)
        {
            var want = i == asked || (drowsy && Link.Moods[i] == "sleepy") ? 1f : 0f;
            moods[i] += (want - moods[i]) * (1f - MathF.Exp((want > moods[i] ? -8f : -3f) * dt));
        }

        var face = Faces[0];
        for (var i = 1; i < Faces.Length; i++)
        {
            face = face.Toward(Faces[i], moods[i]);
        }

        // The head looks about while idle, faces the speaker with a tilt while listening, looks up and aside while
        // thinking, nods with the voice, looks down at its chest while compacting, and leans as the expression wants.
        var yaw = (0.14f * MathF.Sin(t * 0.37f) * calm * (1f - presenting)) + (0.3f * thinking) + (0.05f * speaking * MathF.Sin(t * 1.3f)) + face.Yaw - (0.42f * presenting);
        var pitch = (0.2f * thinking) + (0.08f * voice) + (0.05f * listening) - (0.03f * calm) - (0.22f * compact) + face.Pitch;
        var roll = (0.14f * thinking) - (0.1f * listening) + (0.03f * speaking * MathF.Sin(t * 2.1f)) + face.Roll;
        var head = SN.Matrix4x4.CreateRotationX(-pitch) * SN.Matrix4x4.CreateRotationZ(roll) * SN.Matrix4x4.CreateRotationY(yaw)
            * SN.Matrix4x4.CreateTranslation(HeadPivot) * neck;
        world[Head] = head;

        // The ear pads stand out from the ears and grow while Delta listens, so that listening shows from any angle.
        foreach (var (part, side) in new[] { (EarLeft, -1f), (EarRight, 1f) })
        {
            world[part] = SN.Matrix4x4.CreateScale(1f + (0.6f * listening), 1f + (0.35f * listening), 1f + (0.35f * listening))
                * SN.Matrix4x4.CreateTranslation(side * (0.297f + (0.035f * listening)), 0.2f, 0f) * head;
        }

        // The eyes blink, narrow while thinking and more while compacting, and glance up and aside; the mouth is a bar
        // that opens with the voice. The expression shapes them on top: how open the eyes are and how high, the brows'
        // height and slant, the smile at the mouth's corners.
        var lids = Blink(t) * (1f - (0.45f * thinking)) * (1f - (0.6f * compact)) * (1f + (0.15f * voice) + (0.2f * listening)) * face.EyeOpen;
        var glance = SN.Matrix4x4.CreateTranslation(0.012f * thinking, (0.01f * thinking) + face.EyeLift, 0f);
        world[EyeLeft] = SN.Matrix4x4.CreateScale(1f, lids * (1f - (0.92f * face.Wink)), 1f) * SN.Matrix4x4.CreateTranslation(-0.1f, 0.22f, 0.205f) * glance * head;
        world[EyeRight] = SN.Matrix4x4.CreateScale(1f, lids, 1f) * SN.Matrix4x4.CreateTranslation(0.1f, 0.22f, 0.205f) * glance * head;
        world[BrowLeft] = SN.Matrix4x4.CreateRotationZ(face.BrowTiltLeft) * SN.Matrix4x4.CreateTranslation(-0.1f, 0.284f + face.BrowLiftLeft, 0.206f) * head;
        world[BrowRight] = SN.Matrix4x4.CreateRotationZ(-face.BrowTiltRight) * SN.Matrix4x4.CreateTranslation(0.1f, 0.284f + face.BrowLiftRight, 0.206f) * head;
        var wide = face.MouthWide * (1f - (0.25f * voice));
        world[Mouth] = SN.Matrix4x4.CreateScale(wide, 0.012f + (0.065f * voice) + face.MouthOpen, 1f)
            * SN.Matrix4x4.CreateTranslation(0f, 0.1f, 0.205f) * head;
        world[CornerLeft] = SN.Matrix4x4.CreateRotationZ(-face.Smile) * SN.Matrix4x4.CreateTranslation(-0.1f * wide, 0.1f, 0.206f) * head;
        world[CornerRight] = SN.Matrix4x4.CreateRotationZ(face.Smile) * SN.Matrix4x4.CreateTranslation(0.1f * wide, 0.1f, 0.206f) * head;
        world[Tip] = SN.Matrix4x4.CreateTranslation(0.14f, 0.6f, -0.06f) * head;

        // The arms hang and sway; while thinking, the right hand comes up to the chin; speaking moves them with the voice.
        var sway = 0.06f * MathF.Sin(t * 0.8f) * calm;
        var left = -sway - (voice * 0.5f * (0.6f + (0.4f * MathF.Sin(t * 3.1f)))) - face.Arms;
        var right = sway - (1.9f * thinking) - (voice * 0.35f * (0.5f + (0.5f * MathF.Sin((t * 2.3f) + 1f)))) - face.Arms;
        var shoulders = 0.68f - (squeeze * (0.68f - StackBottom));
        world[ArmLeft] = SN.Matrix4x4.CreateRotationX(left - (0.5f * presenting)) * SN.Matrix4x4.CreateRotationZ(-1.05f * presenting)
            * SN.Matrix4x4.CreateTranslation(-0.32f, shoulders, 0f) * bob;
        world[ArmRight] = SN.Matrix4x4.CreateRotationX(right) * SN.Matrix4x4.CreateRotationY(-0.5f * thinking)
            * SN.Matrix4x4.CreateTranslation(0.32f, shoulders, 0f) * bob;

        // Cyan at rest and speaking, green while listening, amber while thinking, violet while compacting.
        var hue = SN.Vector3.Lerp(SN.Vector3.Lerp(SN.Vector3.Lerp(Cyan, Green, listening), Amber, thinking), Violet, compact);
        var churn = compact * 0.35f * MathF.Sin(t * 9f);
        glow[EyeLeft] = glow[EyeRight] = new SN.Vector4(hue, 0.9f + (0.3f * speaking) + (0.4f * voice) + churn);
        glow[Mouth] = new SN.Vector4(Cyan, 0.12f + (1.3f * voice));
        var showing = MathF.Min(1f, moods.Sum());
        glow[BrowLeft] = glow[BrowRight] = new SN.Vector4(hue, (0.06f + (0.8f * showing)) * face.Glow);
        glow[CornerLeft] = glow[CornerRight] = new SN.Vector4(Cyan, (0.1f + (1.2f * MathF.Min(1f, MathF.Abs(face.Smile) * 2.5f))) * face.Glow);
        glow[EyeLeft] = glow[EyeRight] = new SN.Vector4(hue, glow[EyeLeft].W * face.Glow);
        var flash = MathF.Sin(t * MathF.Tau * 3f) > 0f ? 1.3f : 0.12f;
        glow[Tip] = new SN.Vector4(hue, (calm * (0.3f + (0.2f * MathF.Sin(t * 1.6f)))) + (thinking * flash) + (listening * 1.1f) + (speaking * (0.3f + voice))
            + (compact * (0.7f + (0.6f * MathF.Sin(t * 4.2f)))));
        for (var i = 0; i < ChestLights; i++)
        {
            // All breathe together at rest and hold steady while listening; a light runs to and fro while thinking; a
            // level meter while speaking.
            var run = 1f - MathF.Min(MathF.Abs(i - (2f + (2f * MathF.Sin(t * 4f)))), 1f);
            var meter = voice * ChestLights > i + 0.5f ? 1.2f : 0.06f;
            glow[Chest + i] = new SN.Vector4(hue, (calm * (0.25f + (0.15f * MathF.Sin(t * 1.6f)))) + (listening * 0.7f) + (thinking * (0.08f + (1.2f * run))) + (speaking * meter));
        }

        // The cost lamp is dark at rest, green while the small model on this computer answers, gold while Claude does.
        var lit = MathF.Min(1f, free + paid);
        var coin = lit > 0.001f ? ((Free * free) + (Paid * paid)) / (free + paid) : Paid;
        glow[Cost] = new SN.Vector4(coin, 1.4f * lit);
        glow[Spark] = new SN.Vector4(Terracotta, 1.5f * paid);

        // The Mac minis' lights are a gauge of Claude's context, filled layer by layer from the bottom up to the top,
        // where Claude Code compacts it: pale blue, amber past two thirds, red near the top, and blinking at it. While
        // it compacts, a violet light runs down the stack again and again. Without a gauge, they breathe slowly and
        // flicker with the voice, as a disk would with work.
        var tone = fill < 0.66f ? Pale : fill < 0.9f ? SN.Vector3.Lerp(Pale, Amber, (fill - 0.66f) / 0.24f) : SN.Vector3.Lerp(Amber, Red, MathF.Min(1f, (fill - 0.9f) / 0.08f));
        var warn = fill >= 0.97f ? 0.55f + (0.45f * MathF.Sin(t * 5f)) : 1f;
        var idle = 0.35f + (0.15f * MathF.Sin(t * 0.9f)) + (0.9f * voice) + (0.3f * thinking);
        var falling = Layers - (t * 6f % (Layers + 2f));
        for (var layer = 0; layer < Layers; layer++)
        {
            var share = Math.Clamp((fill * Layers) - layer, 0f, 1f);
            var color = SN.Vector3.Lerp(White, tone, gauge);
            var strength = ((1f - gauge) * idle) + (gauge * (0.04f + (1.3f * share * warn)) * (1f + (0.5f * voice)));
            var wave = compact * MathF.Max(0f, 1f - MathF.Abs(layer - falling));
            glow[Leds + layer] = new SN.Vector4(SN.Vector3.Lerp(color, Violet, MathF.Min(1f, wave + (0.4f * compact))), (strength * (1f - (0.5f * compact))) + (1.8f * wave));
        }

        glow[EarLeft] = glow[EarRight] = new SN.Vector4(Green, 0.06f + (1.6f * listening));

        // Hearts rise from the chest, one after another and on either side of the face: each pops up, drifts and turns as
        // it climbs past the head, and shrinks away.
        for (var i = 0; i < HeartCount; i++)
        {
            var u = m.Hearts < 0f ? -1f : (m.Hearts - (i * HeartStep)) / HeartLife;
            if (u is <= 0f or >= 1f)
            {
                world[HeartFirst + i] = SN.Matrix4x4.CreateScale(0.0001f);
                glow[HeartFirst + i] = default;
                continue;
            }

            var size = MathF.Min(1f, u * 6f) * (1f - (u * u * u * u)) * (0.8f + (0.25f * MathF.Sin(i * 1.9f)));
            var x = ((i % 2 == 0 ? -1f : 1f) * (0.32f + (0.12f * MathF.Abs(MathF.Sin(i * 2.4f))))) + (0.06f * MathF.Sin((t * 2.2f) + i));
            world[HeartFirst + i] = SN.Matrix4x4.CreateScale(size) * SN.Matrix4x4.CreateRotationY(0.6f * MathF.Sin((t * 2.5f) + i))
                * SN.Matrix4x4.CreateTranslation(x, 0.55f + (1.15f * u), 0.3f + (0.12f * MathF.Cos(i * 1.7f)));
            glow[HeartFirst + i] = new SN.Vector4(Pink, 0.9f * size);
        }
        glow[Base] = glow[Torso] = glow[Head] = glow[ArmLeft] = glow[ArmRight] = default;
    }

    // An expression: how open the eyes are (1 as at rest) and how much higher they sit, each brow's lift and its inner
    // end's rise (radians), the smile at the mouth's corners (radians, negative for a frown), how much the mouth opens
    // and how wide it is, the head's pitch, roll and yaw, how far the arms come forward, a closed left eye, and how
    // bright the face glows.
    private readonly record struct Face(float EyeOpen, float EyeLift, float BrowLiftLeft, float BrowLiftRight, float BrowTiltLeft, float BrowTiltRight,
        float Smile, float MouthOpen, float MouthWide, float Pitch, float Roll, float Yaw, float Arms, float Wink, float Glow)
    {
        public Face Toward(Face to, float w) => w <= 0f ? this : new(
            EyeOpen + ((to.EyeOpen - Faces[0].EyeOpen) * w), EyeLift + (to.EyeLift * w),
            BrowLiftLeft + (to.BrowLiftLeft * w), BrowLiftRight + (to.BrowLiftRight * w), BrowTiltLeft + (to.BrowTiltLeft * w), BrowTiltRight + (to.BrowTiltRight * w),
            Smile + (to.Smile * w), MouthOpen + (to.MouthOpen * w), MouthWide + ((to.MouthWide - Faces[0].MouthWide) * w),
            Pitch + (to.Pitch * w), Roll + (to.Roll * w), Yaw + (to.Yaw * w), Arms + (to.Arms * w), Wink + (to.Wink * w), Glow + ((to.Glow - Faces[0].Glow) * w));
    }

    // In Link.Moods' order: neutral, joy, surprise, doubt, sorry, proud, wink, focus, sleepy.
    private static readonly Face[] Faces =
    [
        new(1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 1f),
        new(0.55f, 0.008f, 0.012f, 0.012f, 0f, 0f, 0.5f, 0f, 1.05f, 0.08f, 0f, 0f, 0.35f, 0f, 1.15f),
        new(1.5f, 0.004f, 0.03f, 0.03f, 0f, 0f, 0f, 0.05f, 0.35f, 0.1f, 0f, 0f, 0.15f, 0f, 1.2f),
        new(0.8f, 0f, 0.024f, -0.006f, 0f, -0.25f, -0.15f, 0f, 0.85f, 0f, 0.16f, -0.12f, 0f, 0f, 1f),
        new(0.7f, -0.008f, 0.006f, 0.006f, 0.38f, 0.38f, -0.45f, 0f, 0.9f, -0.2f, 0.05f, 0f, -0.08f, 0f, 0.85f),
        new(0.75f, 0f, 0.006f, 0.006f, -0.08f, -0.08f, 0.32f, 0f, 1f, 0.2f, 0f, 0f, 0f, 0f, 1.1f),
        new(1f, 0f, -0.004f, 0.016f, 0f, 0f, 0.45f, 0f, 1f, 0f, -0.1f, 0.05f, 0f, 1f, 1.1f),
        new(0.55f, 0f, -0.008f, -0.008f, -0.28f, -0.28f, 0f, 0f, 0.8f, -0.05f, 0f, 0f, 0f, 0f, 1f),
        new(0.22f, -0.012f, -0.006f, -0.006f, 0.1f, 0.1f, 0f, 0f, 0.7f, -0.22f, 0.12f, 0f, -0.05f, 0f, 0.55f),
    ];

    // How open the eyes are, from 1 down to 0.1: a blink about every 3.7 s, a double one every third time.
    private static float Blink(float t)
    {
        var p = t % 11.1f;
        float Shut(float at) => MathF.Max(0f, 1f - (MathF.Abs(p - at) / 0.07f));
        return 1f - (0.9f * MathF.Max(MathF.Max(Shut(3.7f), Shut(7.4f)), MathF.Max(Shut(10.6f), Shut(10.85f))));
    }
}
