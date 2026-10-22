// The shelf's picture: one level at a time on a dark translucent card at the window's left edge, its path as the title,
// a few rows around the selected one, and the keys at the bottom. Drawn with GDI's text into RGBA, then sampled like
// the splash.
internal static class Panel
{
    private const int Pad = 14;
    private const int Visible = 8; // rows on the card at most

    // What a drawn card is: its pixels, its size, and each row's index in the level with its top and bottom in pixels.
    internal sealed record Card(byte[] Rgba, int Width, int Height, (int Row, int Top, int Bottom)[] Rows);

    public static Card Draw(IReadOnlyList<Shelf.Level> path, int windowHeight)
    {
        var scale = Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);
        int Px(float v) => (int)MathF.Round(v * scale);
        var width = Px(430);
        var rowHeight = Px(46);
        var level = path[^1];
        var rows = level.Rows;
        var count = rows is null ? 1 : Math.Max(1, Math.Min(Visible, rows.Count));
        var height = Px(Pad) + Px(30) + Px(6) + (count * rowHeight) + Px(30);
        var rgba = new byte[width * height * 4];
        Fill(rgba, width, 0, 0, width, height, (12, 14, 18), 205);
        Fill(rgba, width, 0, 0, Px(3), height, (89, 204, 255), 255); // the accent along the edge

        // The path: the levels' titles, the last one bright.
        var title = string.Join("  ›  ", path.Select(l => l.Title));
        Text(rgba, width, height, Fit(title, Px(17), true, width - (2 * Px(Pad))), Px(17), true, Px(Pad), Px(Pad), (230, 236, 242));

        var top = Px(Pad) + Px(36);
        var drawn = new List<(int, int, int)>();
        if (rows is null)
        {
            Text(rgba, width, height, Words.Say("Looking…", "Je regarde…"), Px(15), false, Px(Pad), top + Px(12), (150, 160, 170));
        }
        else if (rows.Count == 0)
        {
            Text(rgba, width, height, Fit(level.Error ?? Words.Say("Nothing here.", "Rien ici."), Px(15), false, width - (2 * Px(Pad))), Px(15), false, Px(Pad), top + Px(12), (150, 160, 170));
        }
        else
        {
            var first = Math.Clamp(level.Selected - (Visible / 2), 0, Math.Max(0, rows.Count - Visible));
            for (var i = first; i < Math.Min(rows.Count, first + Visible); i++)
            {
                var y = top + ((i - first) * rowHeight);
                var selected = i == level.Selected;
                if (selected)
                {
                    Fill(rgba, width, Px(6), y + Px(2), width - Px(12), rowHeight - Px(4), (89, 204, 255), 46);
                    Fill(rgba, width, Px(6), y + Px(2), Px(3), rowHeight - Px(4), (89, 204, 255), 255);
                }

                var node = rows[i];
                var more = node.Children is not null ? "  ›" : node.Act is not null ? "  ↵" : "";
                Text(rgba, width, height, Fit(node.Label, Px(16), selected, width - (2 * Px(Pad)) - Px(24)) + more, Px(16), selected, Px(Pad), y + Px(5), selected ? (255, 255, 255) : (214, 220, 226));
                if (node.Detail.Length > 0)
                {
                    Text(rgba, width, height, Fit(node.Detail, Px(13), false, width - (2 * Px(Pad))), Px(13), false, Px(Pad), y + Px(25), (138, 150, 162));
                }

                drawn.Add((i, y, y + rowHeight));
            }

            if (rows.Count > Visible)
            {
                Text(rgba, width, height, $"{level.Selected + 1} / {rows.Count}", Px(12), false, width - Px(Pad) - Px(52), Px(Pad) + Px(3), (120, 132, 144));
            }
        }

        Text(rgba, width, height, Words.Say("↑↓ choose   Enter open   ← back   Esc close", "↑↓ choisir   Entrée ouvrir   ← retour   Échap fermer"), Px(12), false, Px(Pad), height - Px(24), (110, 122, 134));
        return new Card(rgba, width, height, [.. drawn]);
    }

    // Shortens text with an ellipsis until it fits in width pixels.
    private static string Fit(string text, int pixels, bool bold, int width)
    {
        if (Splash.Text(text, pixels, out var w, out _, bold) is null || w <= width)
        {
            return text;
        }

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            Splash.Text(text[..mid] + "…", pixels, out w, out _, bold);
            if (w <= width)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return text[..low].TrimEnd() + "…";
    }

    private static void Text(byte[] rgba, int width, int height, string text, int pixels, bool bold, int x, int y, (int R, int G, int B) color)
    {
        var glyphs = Splash.Text(text, pixels, out var w, out var h, bold);
        if (glyphs is null)
        {
            return;
        }

        for (var j = 0; j < h; j++)
        {
            for (var i = 0; i < w; i++)
            {
                var a = glyphs[(((j * w) + i) * 4) + 3];
                if (a > 0)
                {
                    Over(rgba, width, height, x + i, y + j, color, a);
                }
            }
        }
    }

    private static void Fill(byte[] rgba, int width, int x, int y, int w, int h, (int R, int G, int B) color, int alpha)
    {
        var height = rgba.Length / 4 / width;
        for (var j = y; j < y + h; j++)
        {
            for (var i = x; i < x + w; i++)
            {
                Over(rgba, width, height, i, j, color, alpha);
            }
        }
    }

    // Lays a colour of the given alpha over a pixel, in straight alpha.
    private static void Over(byte[] rgba, int width, int height, int x, int y, (int R, int G, int B) color, int alpha)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return;
        }

        var at = ((y * width) + x) * 4;
        var a = alpha / 255f;
        var under = rgba[at + 3] / 255f;
        var outA = a + (under * (1f - a));
        if (outA <= 0f)
        {
            return;
        }

        rgba[at] = (byte)(((color.R * a) + (rgba[at] * under * (1f - a))) / outA);
        rgba[at + 1] = (byte)(((color.G * a) + (rgba[at + 1] * under * (1f - a))) / outA);
        rgba[at + 2] = (byte)(((color.B * a) + (rgba[at + 2] * under * (1f - a))) / outA);
        rgba[at + 3] = (byte)(outA * 255f);
    }
}
