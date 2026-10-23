// The shelf's picture: one level at a time on a dark translucent card at the window's left edge, its path as the title,
// a few rows around the selected one, and the keys at the bottom; the caption under the robot, for a sentence Delta
// let pass; the phone icon in the top right corner, with the card it opens, the QR code that pairs a phone; and the
// ring in the bottom left corner that empties while Delta listens without its name. Drawn with GDI's text into RGBA,
// then sampled like the splash.
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

    // The caption for a sentence Delta heard and let pass for want of its name: how to call Delta, then the sentence in
    // quotes, on a card no wider than the window.
    public static Card Caption(string heard, int windowWidth, int windowHeight)
    {
        var scale = Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);
        int Px(float v) => (int)MathF.Round(v * scale);
        var room = Math.Min(Px(560), windowWidth - (2 * Px(12))) - (2 * Px(Pad));
        var hint = Fit(Words.Say("Not for me? Say “Delta” first.", "Pas pour moi ? Dis « Delta » d'abord."), Px(16), true, room);
        var (open, close) = Words.Language == "fr" ? ("« ", " »") : ("“", "”");
        Splash.Text(close, Px(14), out var closeWidth, out _);
        var quote = open + Fit(heard, Px(14), false, room - closeWidth - Px(14)) + close;
        Splash.Text(hint, Px(16), out var hintWidth, out _, true);
        Splash.Text(quote, Px(14), out var quoteWidth, out _);
        var width = Math.Max(hintWidth, quoteWidth) + (2 * Px(Pad));
        var height = Px(Pad) + Px(22) + Px(4) + Px(20) + Px(Pad);
        var rgba = new byte[width * height * 4];
        Fill(rgba, width, 0, 0, width, height, (12, 14, 18), 205);
        Fill(rgba, width, 0, 0, Px(3), height, (255, 184, 77), 255); // amber, as a word to take note of
        Text(rgba, width, height, hint, Px(16), true, Px(Pad), Px(Pad), (240, 236, 228));
        Text(rgba, width, height, quote, Px(14), false, Px(Pad), Px(Pad) + Px(26), (150, 160, 170));
        return new Card(rgba, width, height, []);
    }

    // The phone icon's size in pixels, square, for a window of that height.
    public static int PhoneSize(int windowHeight) => (int)MathF.Round(40 * Math.Clamp(windowHeight / 720f, 0.8f, 2.2f));

    // The phone icon: a phone's outline on a dark square, and a green light while a pairing code lives.
    public static Card Phone(int windowHeight, bool live)
    {
        var scale = Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);
        int Px(float v) => (int)MathF.Round(v * scale);
        var size = PhoneSize(windowHeight);
        var rgba = new byte[size * size * 4];
        Fill(rgba, size, 0, 0, size, size, (12, 14, 18), 205);
        var (w, h, line) = (Px(16), Px(26), Math.Max(2, Px(2)));
        var (x, y) = ((size - w) / 2, (size - h) / 2);
        (int, int, int) light = (214, 220, 226);
        Fill(rgba, size, x, y, w, line, light, 255);
        Fill(rgba, size, x, y + h - line, w, line, light, 255);
        Fill(rgba, size, x, y, line, h, light, 255);
        Fill(rgba, size, x + w - line, y, line, h, light, 255);
        Fill(rgba, size, x + line, y + h - (3 * line), w - (2 * line), line, light, 255); // the screen's bottom edge
        if (live)
        {
            Fill(rgba, size, size - Px(11), Px(4), Px(7), Px(7), (90, 220, 130), 255);
        }

        return new Card(rgba, size, size, []);
    }

    // The card the phone icon opens: the QR code of the link a phone opens to pair with Delta's gateway, black on white
    // in whole pixels per module with its quiet zone of four, and how long its code still lives; or why there is none.
    // rows is null when no code lives; expired says one did.
    public static Card Pairing(string[]? rows, int secondsLeft, bool expired, int windowWidth, int windowHeight)
    {
        var scale = Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);
        int Px(float v) => (int)MathF.Round(v * scale);
        var room = Math.Min(Px(520), windowWidth - (2 * Px(12))) - (2 * Px(Pad));
        var left = $"{secondsLeft / 60}:{secondsLeft % 60:00}";
        string[] lines = rows is not null
            ? [Words.Say($"Scan it with the phone's camera. The code lives {left} more.", $"Scanne-le avec l'appareil photo du téléphone. Le code vit encore {left}.")]
            : expired
                ? [Words.Say("This code has expired.", "Ce code a expiré."), Words.Say("Restart the phone gateway for a new one.", "Redémarre la passerelle du téléphone pour en avoir un autre.")]
                : [Words.Say("No pairing code yet.", "Pas encore de code de jumelage."), Words.Say("Start the phone gateway: its code shows here for two minutes.", "Démarre la passerelle du téléphone : son code s'affiche ici deux minutes.")];
        var title = Fit(Words.Say("Pair a phone", "Jumeler un téléphone"), Px(17), true, room);
        lines = [.. lines.Select(l => Fit(l, Px(14), false, room))];
        var closing = Fit(Words.Say("Click the phone or press P to close", "Clique sur le téléphone ou appuie sur P pour fermer"), Px(12), false, room);
        int Wide(string text, int pixels, bool bold) => Splash.Text(text, pixels, out var w, out _, bold) is null ? 0 : w;
        var width = Math.Max(Wide(title, Px(17), true), Math.Max(Wide(closing, Px(12), false), lines.Max(l => Wide(l, Px(14), false))));
        var modules = rows is null ? 0 : rows.Length + 8;
        var module = rows is null ? 0 : Math.Max(2, Math.Min(Math.Min(windowHeight * 55 / 100, windowWidth * 80 / 100), Px(420)) / modules);
        var side = modules * module;
        width = Math.Max(width, side) + (2 * Px(Pad));
        var top = Px(Pad) + Px(30);
        var height = top + (side > 0 ? side + Px(10) : 0) + (lines.Length * Px(22)) + Px(8) + Px(20) + Px(Pad);
        var rgba = new byte[width * height * 4];
        Fill(rgba, width, 0, 0, width, height, (12, 14, 18), 225);
        Fill(rgba, width, 0, 0, Px(3), height, (90, 220, 130), 255); // green, as the icon's light
        Text(rgba, width, height, title, Px(17), true, Px(Pad), Px(Pad), (230, 236, 242));
        if (rows is not null)
        {
            var x = (width - side) / 2;
            Fill(rgba, width, x, top, side, side, (255, 255, 255), 255);
            for (var j = 0; j < rows.Length; j++)
            {
                for (var i = 0; i < rows.Length; i++)
                {
                    if (rows[j][i] == '1')
                    {
                        Fill(rgba, width, x + ((i + 4) * module), top + ((j + 4) * module), module, module, (0, 0, 0), 255);
                    }
                }
            }

            top += side + Px(10);
        }

        foreach (var l in lines)
        {
            Text(rgba, width, height, l, Px(14), false, Px(Pad), top, (214, 220, 226));
            top += Px(22);
        }

        Text(rgba, width, height, closing, Px(12), false, Px(Pad), top + Px(8), (110, 122, 134));
        return new Card(rgba, width, height, []);
    }

    // While Delta listens without its name: a green ring that empties over the conversation's last seconds, and the
    // seconds left.
    public static Card Lease(float left, float length, int windowHeight)
    {
        var scale = Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);
        int Px(float v) => (int)MathF.Round(v * scale);
        var label = Words.Say($"Listening · {MathF.Ceiling(left):0} s", $"Je t'écoute · {MathF.Ceiling(left):0} s");
        int Wide(string text) => Splash.Text(text, Px(14), out var w, out _, true) is null ? 0 : w;
        var ring = Px(30);
        var width = Px(Pad) + ring + Px(10) + Wide(label) + Px(Pad);
        var height = ring + (2 * Px(10));
        var rgba = new byte[width * height * 4];
        Fill(rgba, width, 0, 0, width, height, (12, 14, 18), 205);
        var (cx, cy) = (Px(Pad) + (ring / 2f), height / 2f);
        var (outer, inner) = (ring / 2f, (ring / 2f) - Px(4));
        var full = Math.Clamp(length > 0f ? left / length : 0f, 0f, 1f);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (dx, dy) = (x + 0.5f - cx, y + 0.5f - cy);
                var d = MathF.Sqrt((dx * dx) + (dy * dy));
                var cover = Math.Clamp(outer + 0.5f - d, 0f, 1f) * Math.Clamp(d - inner + 0.5f, 0f, 1f);
                if (cover <= 0f)
                {
                    continue;
                }

                var turn = (MathF.Atan2(dx, -dy) / (2f * MathF.PI)) + 0.5f; // 0 at the top, clockwise
                var lit = turn >= 1f - full;
                Over(rgba, width, height, x, y, lit ? (90, 220, 130) : (60, 66, 72), (int)(cover * 255));
            }
        }

        Text(rgba, width, height, label, Px(14), true, Px(Pad) + ring + Px(10), (height - Px(18)) / 2, (230, 236, 242));
        return new Card(rgba, width, height, []);
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
