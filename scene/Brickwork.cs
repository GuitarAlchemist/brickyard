using SN = System.Numerics;

// The hall the robot stands in: concrete slabs with a dashed yellow line around the robot, a brick wall in running bond
// between two board-marked concrete pillars under a beam, a pallet of bricks on the left and a steel cabinet on the right.
internal static class Brickwork
{
    private static readonly SN.Vector3 Brick = new(0.3f, 0.085f, 0.045f);

    public static Vertex[] Build()
    {
        var o = new List<Vertex>();

        // Slabs two metres square, a little uneven in tone, over a dark bed that shows in the joints.
        Boxes.Add(o, new(0f, -0.08f, -2f), new(18f, 0.1f, 16f), new SN.Vector3(0.015f, 0.015f, 0.015f));
        for (var i = -4; i <= 4; i++)
        {
            for (var j = -5; j <= 2; j++)
            {
                var tone = 0.07f + (0.03f * Hash(i, j));
                Boxes.Add(o, new(2f * i, -0.02f, 2f * j), new(1.975f, 0.04f, 1.975f), new SN.Vector3(tone, tone * 0.96f, tone * 0.9f));
            }
        }

        // The dashed line, 2.2 m square, 4 mm proud of the slabs.
        var yellow = new SN.Vector3(0.62f, 0.42f, 0.02f);
        for (var k = 0; k <= 10; k++)
        {
            var a = -1f + (0.2f * k);
            foreach (var s in new[] { -1.1f, 1.1f })
            {
                Boxes.Add(o, new(a, 0.002f, s), new(0.13f, 0.004f, 0.05f), yellow);
                Boxes.Add(o, new(s, 0.002f, a), new(0.05f, 0.004f, 0.13f), yellow);
            }
        }

        // The wall: bricks proud of the mortar behind them, one in ten darker, a few sandier.
        Boxes.Add(o, new(0f, 2.3f, -3.55f), new(12.5f, 4.6f, 0.2f), new SN.Vector3(0.12f, 0.11f, 0.1f));
        for (var r = 0; r < 27; r++)
        {
            for (var c = -14; c <= 14; c++)
            {
                var x = (0.44f * c) + (r % 2 == 0 ? 0f : 0.22f);
                var h = Hash(c, r + 100);
                var color = h < 0.1f ? new SN.Vector3(0.1f, 0.045f, 0.032f)
                    : h > 0.93f ? new SN.Vector3(0.4f, 0.2f, 0.09f)
                    : Brick * (0.75f + (0.4f * Hash(r, c + 50)));
                Boxes.Add(o, new(x, 0.085f + (0.17f * r), -3.37f), new(0.42f, 0.15f, 0.14f), color);
            }
        }

        // Two pillars and the beam across them, marked by the boards they were cast in.
        var concrete = new SN.Vector3(0.3f, 0.29f, 0.27f);
        var board = new SN.Vector3(0.22f, 0.21f, 0.2f);
        foreach (var x in new[] { -3.3f, 3.3f })
        {
            Boxes.Add(o, new(x, 2.4f, -3f), new(0.7f, 4.8f, 0.7f), concrete);
            for (var k = 0; k < 9; k++)
            {
                Boxes.Add(o, new(x, 0.25f + (0.5f * k), -2.648f), new(0.7f, 0.012f, 0.006f), board);
            }
        }

        Boxes.Add(o, new(0f, 4.55f, -3f), new(7.4f, 0.6f, 0.8f), concrete);

        // A pallet of bricks: three runners, a deck, four layers of four by six.
        var wood = new SN.Vector3(0.2f, 0.12f, 0.055f);
        foreach (var z in new[] { -0.33f, 0f, 0.33f })
        {
            Boxes.Add(o, new(-2.1f, 0.04f, -1.5f + z), new(1f, 0.08f, 0.1f), wood);
        }

        Boxes.Add(o, new(-2.1f, 0.0925f, -1.5f), new(1f, 0.025f, 0.8f), wood);
        for (var layer = 0; layer < 4; layer++)
        {
            for (var i = 0; i < 4; i++)
            {
                for (var j = 0; j < 6; j++)
                {
                    var tone = 0.75f + (0.4f * Hash(i + (10 * layer), j + 200));
                    Boxes.Add(o, new(-2.4675f + (0.245f * i), 0.1425f + (0.08f * layer), -1.8125f + (0.125f * j)), new(0.23f, 0.075f, 0.11f), Brick * tone);
                }
            }
        }

        // A steel cabinet with four drawer handles and a small screen.
        Boxes.Add(o, new(2.2f, 0.65f, -1.9f), new(0.9f, 1.3f, 0.5f), new Finish(new(0.045f, 0.075f, 0.06f), Material.Enamel));
        for (var k = 0; k < 4; k++)
        {
            Boxes.Add(o, new(2.2f, 0.2f + (0.2f * k), -1.645f), new(0.7f, 0.02f, 0.012f), new Finish(new(0.18f, 0.18f, 0.17f), Material.Steel));
        }

        Boxes.Add(o, new(2.2f, 1.08f, -1.646f), new(0.36f, 0.22f, 0.01f), new Finish(new(0.08f, 0.3f, 0.34f), Material.Glass));
        return [.. o];
    }

    // A fixed value in [0, 1) for a pair of whole numbers, so that the hall comes out the same every run.
    private static float Hash(int a, int b)
    {
        var h = unchecked(((uint)a * 73856093u) ^ ((uint)b * 19349663u));
        h ^= h >> 13;
        h = unchecked(h * 0x5bd1e995u);
        h ^= h >> 15;
        return (h & 0xFFFFFF) / 16777216f;
    }
}
