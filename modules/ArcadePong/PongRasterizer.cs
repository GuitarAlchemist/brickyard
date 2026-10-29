namespace Delta.ArcadePong;

// One reusable 160 x 96 RGBA8 surface, origin top left. The owner uploads/copies it before the next Draw.
public sealed class PongRasterizer
{
    public const int Width = 160, Height = 96;
    private readonly byte[] pixels = new byte[Width * Height * 4];
    private static readonly ushort[] Digits = [
        0b111_101_101_101_111, 0b010_110_010_010_111, 0b111_001_111_100_111,
        0b111_001_111_001_111, 0b101_101_111_001_001, 0b111_100_111_001_111,
        0b111_100_111_101_111, 0b111_001_001_001_001, 0b111_101_111_101_111,
        0b111_101_111_001_111
    ];
    public ReadOnlyMemory<byte> Pixels => pixels;

    public void Draw(PongFrame frame)
    {
        for (var at = 0; at < pixels.Length; at += 4)
        {
            pixels[at] = 8; pixels[at + 1] = 12; pixels[at + 2] = 18; pixels[at + 3] = 255;
        }
        Rect(0, 14, Width, 1, 66, 103, 112);
        Rect(0, 94, Width, 1, 66, 103, 112);
        for (var y = 18; y < 94; y += 8) Rect(79, y, 2, 4, 38, 59, 68);
        Score(frame.LeftScore, 49, 95, 219, 240);
        Score(frame.RightScore, 97, 255, 187, 87);
        Rect(79, 5, 2, 2, 100, 125, 135);
        Rect(6, (int)Math.Round(frame.LeftY) - 9, 4, 18, 95, 219, 240);
        Rect(150, (int)Math.Round(frame.RightY) - 9, 4, 18, 255, 187, 87);
        Rect((int)Math.Round(frame.BallX) - 2, (int)Math.Round(frame.BallY) - 2, 4, 4, 238, 245, 244);
        if (frame.Paused)
        {
            Rect(72, 43, 5, 16, 238, 245, 244);
            Rect(83, 43, 5, 16, 238, 245, 244);
        }
        if (frame.ServeTicks > 0) Rect(70, 89, Math.Max(1, frame.ServeTicks * 20 / 90), 2, 95, 219, 240);
    }

    private void Score(int score, int x, byte r, byte g, byte b)
    {
        Digit(Math.Clamp(score, 0, 99) / 10, x, r, g, b);
        Digit(Math.Clamp(score, 0, 99) % 10, x + 8, r, g, b);
    }

    private void Digit(int digit, int x, byte r, byte g, byte b)
    {
        var bits = Digits[digit];
        for (var y = 0; y < 5; y++)
            for (var col = 0; col < 3; col++)
                if ((bits & (1 << (14 - y * 3 - col))) != 0) Rect(x + col * 2, 2 + y * 2, 2, 2, r, g, b);
    }

    private void Rect(int x, int y, int width, int height, byte r, byte g, byte b)
    {
        for (var row = Math.Max(0, y); row < Math.Min(Height, y + height); row++)
            for (var col = Math.Max(0, x); col < Math.Min(Width, x + width); col++)
            {
                var at = (row * Width + col) * 4;
                pixels[at] = r; pixels[at + 1] = g; pixels[at + 2] = b;
            }
    }
}
