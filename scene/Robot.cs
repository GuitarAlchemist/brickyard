using SN = System.Numerics;

// The robot: boxes in parts that move. Each part's vertices are in its own frame, and Pose gives every part its world
// matrix and its glow for the moment, from what the brain says the robot is doing and how loud its voice is.
internal sealed class Robot
{
    public const int Base = 0, Torso = 1, Head = 2, EyeLeft = 3, EyeRight = 4, Mouth = 5, Tip = 6, ArmLeft = 7, ArmRight = 8, Chest = 9;
    public const int ChestLights = 5;
    public const int Parts = Chest + ChestLights;

    private static readonly SN.Vector3 HeadPivot = new(0f, 0.84f, 0f);
    private static readonly SN.Vector3 Cyan = new(0.35f, 0.8f, 1f);
    private static readonly SN.Vector3 Amber = new(1f, 0.52f, 0.1f);

    private float thinking, speaking, level;

    public static Vertex[][] Meshes()
    {
        var parts = Enumerable.Range(0, Parts).Select(_ => new List<Vertex>()).ToArray();
        var steel = new SN.Vector3(0.16f, 0.165f, 0.17f);
        var rubber = new SN.Vector3(0.025f, 0.025f, 0.028f);
        var enamel = new SN.Vector3(0.78f, 0.27f, 0.05f);
        var shell = new SN.Vector3(0.7f, 0.68f, 0.62f);
        var glass = new SN.Vector3(0.012f, 0.014f, 0.018f);
        var unlit = new SN.Vector3(0.03f, 0.03f, 0.035f);

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

        // The torso: enamel, a dark chest panel with vents, four bolts and a row of lights, and the neck.
        var torso = parts[Torso];
        Boxes.Add(torso, new(0f, 0.49f, 0f), new(0.56f, 0.5f, 0.4f), enamel);
        Boxes.Add(torso, new(0f, 0.75f, 0f), new(0.48f, 0.02f, 0.32f), enamel * 0.6f);
        Boxes.Add(torso, new(0f, 0.5f, 0.2f), new(0.36f, 0.26f, 0.012f), glass);
        for (var i = 0; i < 3; i++)
        {
            Boxes.Add(torso, new(0f, 0.55f + (0.035f * i), 0.208f), new(0.26f, 0.012f, 0.006f), steel);
        }

        foreach (var (x, y) in new[] { (-0.245f, 0.28f), (0.245f, 0.28f), (-0.245f, 0.7f), (0.245f, 0.7f) })
        {
            Boxes.Add(torso, new(x, y, 0.203f), new(0.024f, 0.024f, 0.01f), steel);
        }

        Boxes.Add(torso, new(0f, 0.8f, 0f), new(0.12f, 0.1f, 0.12f), steel);
        for (var i = 0; i < ChestLights; i++)
        {
            Boxes.Add(parts[Chest + i], new(-0.12f + (0.06f * i), 0.43f, 0.209f), new(0.045f, 0.03f, 0.006f), unlit);
        }

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

        Boxes.Add(head, new(0.14f, 0.48f, -0.06f), new(0.016f, 0.2f, 0.016f), steel);
        Boxes.Add(parts[Tip], SN.Vector3.Zero, new(0.05f, 0.05f, 0.05f), unlit);
        Boxes.Add(parts[EyeLeft], SN.Vector3.Zero, new(0.1f, 0.07f, 0.01f), unlit);
        Boxes.Add(parts[EyeRight], SN.Vector3.Zero, new(0.1f, 0.07f, 0.01f), unlit);
        Boxes.Add(parts[Mouth], SN.Vector3.Zero, new(0.2f, 1f, 0.01f), unlit);

        // The arms, about the shoulders: a shoulder joint, the enamel arm, a wrist and a two-fingered gripper.
        foreach (var (part, side) in new[] { (ArmLeft, -1f), (ArmRight, 1f) })
        {
            var arm = parts[part];
            Boxes.Add(arm, new(side * 0.02f, 0f, 0f), new(0.08f, 0.12f, 0.15f), steel);
            Boxes.Add(arm, new(side * 0.04f, -0.2f, 0f), new(0.08f, 0.3f, 0.1f), enamel);
            Boxes.Add(arm, new(side * 0.04f, -0.38f, 0f), new(0.06f, 0.06f, 0.06f), steel);
            foreach (var finger in new[] { -0.03f, 0.03f })
            {
                Boxes.Add(arm, new(side * 0.04f, -0.45f, finger), new(0.03f, 0.09f, 0.02f), steel);
            }
        }

        return [.. parts.Select(p => p.ToArray())];
    }

    // Each part's world matrix and glow at time t (seconds), dt after the last pose, in the given state, with the voice
    // at the given loudness (0 to 1).
    public void Pose(float t, float dt, string state, float heard, Span<SN.Matrix4x4> world, Span<SN.Vector4> glow)
    {
        var k = 1f - MathF.Exp(-5f * dt);
        thinking += ((state == "thinking" ? 1f : 0f) - thinking) * k;
        speaking += ((state == "speaking" ? 1f : 0f) - speaking) * k;
        // The mouth opens at once and closes a little slower, as a voice's loudness is heard.
        level += (heard - level) * (1f - MathF.Exp((heard > level ? -40f : -12f) * dt));
        var calm = 1f - MathF.Max(thinking, speaking);
        var voice = speaking * level;

        var bob = SN.Matrix4x4.CreateTranslation(0f, (0.008f * MathF.Sin(t * 2.8f)) + (0.012f * voice), 0f);
        world[Base] = SN.Matrix4x4.Identity;
        world[Torso] = bob;
        for (var i = 0; i < ChestLights; i++)
        {
            world[Chest + i] = bob;
        }

        // The head looks about while idle, up and aside while thinking, and nods with the voice.
        var yaw = (0.14f * MathF.Sin(t * 0.37f) * calm) + (0.3f * thinking) + (0.05f * speaking * MathF.Sin(t * 1.3f));
        var pitch = (0.2f * thinking) + (0.08f * voice) - (0.03f * calm);
        var roll = (0.14f * thinking) + (0.03f * speaking * MathF.Sin(t * 2.1f));
        var head = SN.Matrix4x4.CreateRotationX(-pitch) * SN.Matrix4x4.CreateRotationZ(roll) * SN.Matrix4x4.CreateRotationY(yaw)
            * SN.Matrix4x4.CreateTranslation(HeadPivot) * bob;
        world[Head] = head;

        // The eyes blink, narrow while thinking and glance up and aside; the mouth is a bar that opens with the voice.
        var open = SN.Matrix4x4.CreateScale(1f, Blink(t) * (1f - (0.45f * thinking)) * (1f + (0.15f * voice)), 1f);
        var glance = SN.Matrix4x4.CreateTranslation(0.012f * thinking, 0.01f * thinking, 0f);
        world[EyeLeft] = open * SN.Matrix4x4.CreateTranslation(-0.1f, 0.22f, 0.205f) * glance * head;
        world[EyeRight] = open * SN.Matrix4x4.CreateTranslation(0.1f, 0.22f, 0.205f) * glance * head;
        world[Mouth] = SN.Matrix4x4.CreateScale(1f - (0.25f * voice), 0.012f + (0.065f * voice), 1f)
            * SN.Matrix4x4.CreateTranslation(0f, 0.1f, 0.205f) * head;
        world[Tip] = SN.Matrix4x4.CreateTranslation(0.14f, 0.6f, -0.06f) * head;

        // The arms hang and sway; while thinking, the right hand comes up to the chin; speaking moves them with the voice.
        var sway = 0.06f * MathF.Sin(t * 0.8f) * calm;
        var left = -sway - (voice * 0.5f * (0.6f + (0.4f * MathF.Sin(t * 3.1f))));
        var right = sway - (1.9f * thinking) - (voice * 0.35f * (0.5f + (0.5f * MathF.Sin((t * 2.3f) + 1f))));
        world[ArmLeft] = SN.Matrix4x4.CreateRotationX(left) * SN.Matrix4x4.CreateTranslation(-0.32f, 0.68f, 0f) * bob;
        world[ArmRight] = SN.Matrix4x4.CreateRotationX(right) * SN.Matrix4x4.CreateRotationY(-0.5f * thinking)
            * SN.Matrix4x4.CreateTranslation(0.32f, 0.68f, 0f) * bob;

        // Cyan at rest and speaking, amber while thinking.
        var hue = SN.Vector3.Lerp(Cyan, Amber, thinking);
        glow[EyeLeft] = glow[EyeRight] = new SN.Vector4(hue, 0.9f + (0.3f * speaking) + (0.4f * voice));
        glow[Mouth] = new SN.Vector4(Cyan, 0.12f + (1.3f * voice));
        var flash = MathF.Sin(t * MathF.Tau * 3f) > 0f ? 1.3f : 0.12f;
        glow[Tip] = new SN.Vector4(hue, (calm * (0.3f + (0.2f * MathF.Sin(t * 1.6f)))) + (thinking * flash) + (speaking * (0.3f + voice)));
        for (var i = 0; i < ChestLights; i++)
        {
            // All breathe together at rest; a light runs to and fro while thinking; a level meter while speaking.
            var run = 1f - MathF.Min(MathF.Abs(i - (2f + (2f * MathF.Sin(t * 4f)))), 1f);
            var meter = voice * ChestLights > i + 0.5f ? 1.2f : 0.06f;
            glow[Chest + i] = new SN.Vector4(hue, (calm * (0.25f + (0.15f * MathF.Sin(t * 1.6f)))) + (thinking * (0.08f + (1.2f * run))) + (speaking * meter));
        }

        glow[Base] = glow[Torso] = glow[Head] = glow[ArmLeft] = glow[ArmRight] = default;
    }

    // How open the eyes are, from 1 down to 0.1: a blink about every 3.7 s, a double one every third time.
    private static float Blink(float t)
    {
        var p = t % 11.1f;
        float Shut(float at) => MathF.Max(0f, 1f - (MathF.Abs(p - at) / 0.07f));
        return 1f - (0.9f * MathF.Max(MathF.Max(Shut(3.7f), Shut(7.4f)), MathF.Max(Shut(10.6f), Shut(10.85f))));
    }
}
