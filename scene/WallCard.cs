// The wall screen's card for a session (WallSource.Card): its lines on a dark screen, the name large, then who it is,
// how it stands and how fresh, then its last requests, and at the bottom how to change what the wall shows. 512 by 384
// RGBA, drawn with the splash's text.
internal static class WallCard
{
    internal const int Width = 512, Height = 384;

    public static byte[] Render(IReadOnlyList<string> lines, string hint)
    {
        var rgba = new byte[Width * Height * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (8, 12, 18, 255);
        }

        var y = 18;
        for (var k = 0; k < lines.Count && y < Height - 40; k++)
        {
            var (size, bold, color) = k switch
            {
                0 => (30, true, ((byte)110, (byte)220, (byte)255)),
                < 4 => (16, false, ((byte)220, (byte)225, (byte)235)),
                4 => (13, true, ((byte)255, (byte)200, (byte)60)),
                _ => (15, false, ((byte)180, (byte)190, (byte)205)),
            };
            y += Write(rgba, lines[k], size, bold, 20, y, color) + (k == 0 || k == 4 ? 12 : 6);
            if (k == 4 || (k == lines.Count - 1 && k < 4))
            {
                y += Write(rgba, hint, 12, false, 20, y - 6, (110, 120, 140)) + 10; // up here, not at the bottom: Delta's head hides the screen's lower middle from the first view
            }
        }

        return rgba;
    }

    // Writes text at x, y, cut with an ellipsis to the card's width; returns the height it took.
    private static int Write(byte[] rgba, string text, int pixels, bool bold, int x, int y, (byte R, byte G, byte B) color)
    {
        var glyphs = Splash.Text(text, pixels, out var w, out var h, bold);
        for (var cut = text.Length; glyphs is not null && w > Width - (2 * x) && cut > 1; cut--)
        {
            glyphs = Splash.Text(text[..cut].TrimEnd() + "…", pixels, out w, out h, bold);
        }

        if (glyphs is null)
        {
            return pixels;
        }

        for (var j = 0; j < h && y + j < Height; j++)
        {
            for (var i = 0; i < w && x + i < Width; i++)
            {
                var a = glyphs[(((j * w) + i) * 4) + 3] / 255f;
                if (a > 0)
                {
                    var at = (((y + j) * Width) + x + i) * 4;
                    rgba[at] = (byte)((rgba[at] * (1 - a)) + (color.R * a));
                    rgba[at + 1] = (byte)((rgba[at + 1] * (1 - a)) + (color.G * a));
                    rgba[at + 2] = (byte)((rgba[at + 2] * (1 - a)) + (color.B * a));
                }
            }
        }

        return h;
    }
}
