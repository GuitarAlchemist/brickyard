// The shelf's picture: one level at a time on a dark translucent card at the window's left edge, its path as the title,
// a few rows around the selected one, and the keys at the bottom; the caption under the robot, for each sentence Delta
// heard; the phone icon in the top right corner, with the card it opens, the QR code that pairs a phone; and the
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

    // The caption for a sentence Delta heard: for whom it was (Link's "heard"), with how to call Delta when it let the
    // sentence pass for want of its name, then the whole sentence in quotes on up to four lines, on a card no wider than
    // the window.
    public static Card Caption(string heard, string to, int windowWidth, int windowHeight)
    {
        var scale = Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);
        int Px(float v) => (int)MathF.Round(v * scale);
        var room = Math.Min(Px(720), windowWidth - (2 * Px(12))) - (2 * Px(Pad));
        var (said, edge) = to switch
        {
            "wake" => (Words.Say("Not for me? Say “Delta” first.", "Pas pour moi ? Dis « Delta » d'abord."), (255, 184, 77)), // amber, a word to take note of
            "claude" => (Words.Say("For Claude", "Pour Claude"), (89, 204, 255)),
            "delta" => (Words.Say("Delta answers", "Delta répond"), (90, 220, 130)),
            "said" => (Words.Say("Delta says", "Delta dit"), (120, 230, 200)),
            "hearing" => (Words.Say("You are saying…", "Tu dis…"), (214, 220, 226)),
            "dropped" or "someone else" => (Words.Say("Not for Delta, it seems", "Pas pour Delta, semble-t-il"), (150, 160, 170)),
            _ => (Words.Say($"For {to}", $"Pour {to}"), (150, 160, 170)),
        };
        var hint = Fit(said, Px(16), true, room);
        var (open, close) = Words.Language == "fr" ? ("« ", " »") : ("“", "”");
        var lines = Wrap(open + heard + close, Px(15), room, 4);
        int Wide(string text, int pixels, bool bold) => Splash.Text(text, pixels, out var w, out _, bold) is null ? 0 : w;
        var width = Math.Max(Wide(hint, Px(16), true), lines.Max(l => Wide(l, Px(15), false))) + (2 * Px(Pad));
        var height = Px(Pad) + Px(22) + Px(6) + (lines.Length * Px(21)) + Px(Pad);
        var rgba = new byte[width * height * 4];
        Fill(rgba, width, 0, 0, width, height, (12, 14, 18), 215);
        Fill(rgba, width, 0, 0, Px(3), height, edge, 255);
        Text(rgba, width, height, hint, Px(16), true, Px(Pad), Px(Pad), (240, 236, 228));
        for (var i = 0; i < lines.Length; i++)
        {
            Text(rgba, width, height, lines[i], Px(15), false, Px(Pad), Px(Pad) + Px(28) + (i * Px(21)), (214, 220, 226));
        }

        return new Card(rgba, width, height, []);
    }

    // Breaks text into lines of whole words no wider than width pixels, at most max of them, the last one shortened with
    // an ellipsis when the text goes on.
    private static string[] Wrap(string text, int pixels, int width, int max)
    {
        var lines = new List<string>();
        var line = "";
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var longer = line.Length == 0 ? words[i] : line + " " + words[i];
            if (line.Length == 0 || (Splash.Text(longer, pixels, out var w, out _) is not null && w <= width))
            {
                line = longer;
                continue;
            }

            if (lines.Count == max - 1)
            {
                lines.Add(Fit(string.Join(' ', [line, .. words[i..]]), pixels, false, width));
                return [.. lines];
            }

            lines.Add(Fit(line, pixels, false, width));
            line = words[i];
        }

        lines.Add(Fit(line, pixels, false, width));
        return [.. lines];
    }

    // The phone icon's size in pixels, square, for a window of that height.
    public static int PhoneSize(int windowHeight) => (int)MathF.Round(40 * Math.Clamp(windowHeight / 720f, 0.8f, 2.2f));

    // The phone icon: a phone's outline on a dark square, its screen lit in blue while a device listens to Delta, and a
    // green light while a pairing code lives.
    public static Card Phone(int windowHeight, bool live, bool connected)
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
        if (connected)
        {
            Fill(rgba, size, x + line, y + line, w - (2 * line), h - (4 * line), (89, 204, 255), 255);
        }
        if (live)
        {
            Fill(rgba, size, size - Px(11), Px(4), Px(7), Px(7), (90, 220, 130), 255);
        }

        return new Card(rgba, size, size, []);
    }

    // While guests the user invited watch the window from afar: a red light, how many, whether they hear the room, and
    // what a click on the badge does, so that nobody is seen or heard without knowing it.
    public static Card GuestBadge(int watching, bool hear, int windowHeight)
    {
        var scale = Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);
        int Px(float v) => (int)MathF.Round(v * scale);
        var label = (watching == 1, hear) switch
        {
            (true, false) => Words.Say("A guest is watching · click to let them hear you", "Un invité regarde · clique pour qu'il t'entende"),
            (true, true) => Words.Say("A guest is watching and listening · click to stop", "Un invité regarde et écoute · clique pour couper"),
            (false, false) => Words.Say($"{watching} guests are watching · click to let them hear you", $"{watching} invités regardent · clique pour qu'ils t'entendent"),
            (false, true) => Words.Say($"{watching} guests are watching and listening · click to stop", $"{watching} invités regardent et écoutent · clique pour couper"),
        };
        var wide = Splash.Text(label, Px(14), out var w, out _, true) is null ? 0 : w;
        var (height, light) = (PhoneSize(windowHeight), Px(10));
        var width = Px(Pad) + light + Px(8) + wide + Px(Pad);
        var rgba = new byte[width * height * 4];
        Fill(rgba, width, 0, 0, width, height, (12, 14, 18), 205);
        Fill(rgba, width, Px(Pad), (height - light) / 2, light, light, (230, 70, 70), 255);
        Text(rgba, width, height, label, Px(14), true, Px(Pad) + light + Px(8), (height - Px(18)) / 2, (230, 236, 242));
        return new Card(rgba, width, height, []);
    }

    // The microphone button, left of the language button: a microphone, struck through in red while it is off.
    public static Card MicButton(bool off, int windowHeight)
    {
        var scale = Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);
        int Px(float v) => (int)MathF.Round(v * scale);
        var size = PhoneSize(windowHeight);
        var rgba = new byte[size * size * 4];
        Fill(rgba, size, 0, 0, size, size, (12, 14, 18), 205);
        (int, int, int) light = (214, 220, 226);
        var (line, cx) = (Math.Max(2, Px(2)), size / 2);
        Fill(rgba, size, cx - Px(5), Px(8), Px(10), Px(15), light, 255); // the capsule
        Fill(rgba, size, cx - Px(9), Px(18), line, Px(7), light, 255); // the stand's cup
        Fill(rgba, size, cx + Px(9) - line, Px(18), line, Px(7), light, 255);
        Fill(rgba, size, cx - Px(9), Px(25) - line, Px(18), line, light, 255);
        Fill(rgba, size, cx - (line / 2), Px(25), line, Px(6), light, 255); // its foot
        Fill(rgba, size, cx - Px(6), Px(31), Px(12), line, light, 255);
        if (off)
        {
            for (var i = Px(6); i < size - Px(6); i++)
            {
                Fill(rgba, size, i - line, i - line, 2 * line, 2 * line, (230, 70, 70), 255);
            }
        }

        return new Card(rgba, size, size, []);
    }

    // The language button, left of the phone icon: what Delta hears and speaks, such as FR, or FR·EN when bilingual.
    public static Card LanguageButton(string setting, int windowHeight)
    {
        var scale = Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);
        int Px(float v) => (int)MathF.Round(v * scale);
        var label = setting.ToUpperInvariant().Replace("+", "·");
        var size = PhoneSize(windowHeight);
        var wide = Splash.Text(label, Px(14), out var w, out _, true) is null ? 0 : w;
        var width = Math.Max(size, wide + (2 * Px(10)));
        var rgba = new byte[width * size * 4];
        Fill(rgba, width, 0, 0, width, size, (12, 14, 18), 205);
        Text(rgba, width, size, label, Px(14), true, (width - wide) / 2, (size - Px(18)) / 2, (214, 220, 226));
        return new Card(rgba, width, size, []);
    }

    // The card the phone icon opens: the QR code of the link a phone opens to pair with Delta's gateway, and how long its
    // code still lives; or why there is none. rows is null when no code lives; expired says one did.
    public static Card Pairing(string[]? rows, int secondsLeft, bool expired, int windowWidth, int windowHeight)
    {
        var left = $"{secondsLeft / 60}:{secondsLeft % 60:00}";
        string[] lines = rows is not null
            ? [Words.Say($"Scan it with the phone's camera. The code lives {left} more.", $"Scanne-le avec l'appareil photo du téléphone. Le code vit encore {left}.")]
            : expired
                ? [Words.Say("This code has expired: a new one is asked for.", "Ce code a expiré : un autre est demandé."), Words.Say("If none comes, restart the phone gateway.", "S'il n'en vient pas, redémarre la passerelle du téléphone.")]
                : [Words.Say("No pairing code yet.", "Pas encore de code de jumelage."), Words.Say("Start the phone gateway: its code shows here for two minutes.", "Démarre la passerelle du téléphone : son code s'affiche ici deux minutes.")];
        return QrCard(Words.Say("Pair a phone", "Jumeler un téléphone"), rows, lines, Words.Say("Click the phone or press P to close", "Clique sur le téléphone ou appuie sur P pour fermer"), (90, 220, 130), windowWidth, windowHeight); // green, as the icon's light
    }

    // The Inviter button, left of the microphone button.
    public static Card InviteButton(int windowHeight)
    {
        var scale = Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);
        int Px(float v) => (int)MathF.Round(v * scale);
        var label = Words.Say("Invite", "Inviter");
        var size = PhoneSize(windowHeight);
        var wide = Splash.Text(label, Px(14), out var w, out _, true) is null ? 0 : w;
        var width = Math.Max(size, wide + (2 * Px(10)));
        var rgba = new byte[width * size * 4];
        Fill(rgba, width, 0, 0, width, size, (12, 14, 18), 205);
        Text(rgba, width, size, label, Px(14), true, (width - wide) / 2, (size - Px(18)) / 2, (214, 220, 226));
        return new Card(rgba, width, size, []);
    }

    // The brain's version in the bottom right corner, small and dim, such as « Delta dev-v0.6.0-40-gc31a28e · nouveautés »:
    // a click opens its release notes (ReleaseNotes).
    public static Card VersionLabel(string version, int windowHeight)
    {
        var scale = Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);
        int Px(float v) => (int)MathF.Round(v * scale);
        var label = $"Delta {version} · {Words.Say("what's new", "nouveautés")}";
        var wide = Splash.Text(label, Px(11), out var w, out _) is null ? 0 : w;
        var (width, height) = (wide + (2 * Px(6)), Px(20));
        var rgba = new byte[width * height * 4];
        Fill(rgba, width, 0, 0, width, height, (12, 14, 18), 160);
        Text(rgba, width, height, label, Px(11), false, Px(6), (height - Px(14)) / 2, (130, 140, 150));
        return new Card(rgba, width, height, []);
    }

    // The release notes of version (Notes), centred: each change's lead in bold and up to three lines of the rest, as
    // many changes as fit in the window, then how to close it; or null when the scene carries no notes.
    public static Card? ReleaseNotes(string version, int windowWidth, int windowHeight)
    {
        if (Notes.For(version) is not { } notes)
        {
            return null;
        }

        var scale = Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);
        int Px(float v) => (int)MathF.Round(v * scale);
        var room = Math.Min(Px(640), windowWidth - (2 * Px(12))) - (2 * Px(Pad));
        var title = Fit(Words.Say($"What's new in Delta {notes.Release}", $"Nouveautés de Delta {notes.Release}"), Px(17), true, room);
        var built = version.TrimStart('v') != notes.Release;
        var note = Fit((built, Words.Language == "fr") switch
        {
            (true, true) => $"Cette fenêtre tourne en {version}, construite depuis. Notes en anglais.",
            (false, true) => "Notes en anglais.",
            (true, false) => $"This window runs {version}, built since.",
            _ => "",
        }, Px(12), false, room);
        var closing = Fit(Words.Say("Click the card or press Esc to close", "Clique sur la carte ou appuie sur Échap pour fermer"), Px(12), false, room);
        var budget = (windowHeight * 88 / 100) - (Px(Pad) + Px(30) + (note.Length > 0 ? Px(20) : 0) + Px(20) + Px(28) + Px(Pad));
        var shown = new List<(string Lead, string[] Lines)>();
        var used = 0;
        foreach (var change in notes.Changes)
        {
            var lead = Fit(change.Lead, Px(14), true, room);
            var lines = change.Text.Length > 0 ? Wrap(change.Text, Px(13), room, 3) : [];
            var tall = (lead.Length > 0 ? Px(20) : 0) + (lines.Length * Px(18)) + Px(8);
            if (used + tall > budget)
            {
                break;
            }

            shown.Add((lead, lines));
            used += tall;
        }

        var more = notes.Changes.Length - shown.Count;
        var rest = more > 0 ? Fit(Words.Say($"and {more} more on GitHub", $"et {more} de plus sur GitHub"), Px(13), false, room) : "";
        int Wide(string text, int pixels, bool bold) => Splash.Text(text, pixels, out var w, out _, bold) is null ? 0 : w;
        var width = new[] { Wide(title, Px(17), true), Wide(note, Px(12), false), Wide(closing, Px(12), false), Wide(rest, Px(13), false) }
            .Concat(shown.SelectMany(s => s.Lines.Select(l => Wide(l, Px(13), false)).Append(Wide(s.Lead, Px(14), true)))).Max() + (2 * Px(Pad));
        var height = Px(Pad) + Px(30) + (note.Length > 0 ? Px(20) : 0) + used + (rest.Length > 0 ? Px(20) : 0) + Px(28) + Px(Pad);
        var rgba = new byte[width * height * 4];
        Fill(rgba, width, 0, 0, width, height, (12, 14, 18), 230);
        Fill(rgba, width, 0, 0, Px(3), height, (120, 230, 200), 255);
        Text(rgba, width, height, title, Px(17), true, Px(Pad), Px(Pad), (230, 236, 242));
        var top = Px(Pad) + Px(30);
        if (note.Length > 0)
        {
            Text(rgba, width, height, note, Px(12), false, Px(Pad), top, (130, 140, 150));
            top += Px(20);
        }

        foreach (var (lead, lines) in shown)
        {
            if (lead.Length > 0)
            {
                Text(rgba, width, height, lead, Px(14), true, Px(Pad), top, (240, 236, 228));
                top += Px(20);
            }

            foreach (var l in lines)
            {
                Text(rgba, width, height, l, Px(13), false, Px(Pad), top, (200, 208, 216));
                top += Px(18);
            }

            top += Px(8);
        }

        if (rest.Length > 0)
        {
            Text(rgba, width, height, rest, Px(13), false, Px(Pad), top, (150, 160, 170));
            top += Px(20);
        }

        Text(rgba, width, height, closing, Px(12), false, Px(Pad), top + Px(8), (110, 122, 134));
        return new Card(rgba, width, height, []);
    }

    // The wheel (ToolWheel), drawn as the Observatory's curio wheel is: slots of dark lacquer edged in gilt round a hole,
    // the picked one gilt, its title above, the held thing's name in the middle and a line about it below. The ring's
    // centre is the card's, so that the window centres it on the screen; slots without a name are empty.
    public static Card Wheel(int slots, int pick, string? name, int windowWidth, int windowHeight)
    {
        var (inner, outer) = ToolWheel.Radii(windowWidth, windowHeight);
        var fsTitle = (int)MathF.Round(outer * 0.07f);
        var fsName = (int)MathF.Round(outer * 0.11f);
        var fsAmmo = (int)MathF.Round(outer * 0.072f);
        var fsLine = (int)MathF.Round(outer * 0.078f);
        var title = Words.Say("THE WHEEL", "LA ROUE");
        var shown = name ?? Words.Say("Empty", "Vide");
        var ammo = name is null ? Words.Say("nothing to hold yet", "rien à tenir pour l'instant") : "";
        var line = Words.Say("Hold Tab, point, let go. Things to hold will come.", "Maintiens Tab, vise, relâche. Les objets viendront.");
        int Wide(string text, int pixels, bool bold) => Splash.Text(text, pixels, out var w, out _, bold) is null ? 0 : w;
        var band = (int)MathF.Ceiling(fsLine * 2.9f);
        var rim = Math.Max((int)MathF.Ceiling(fsTitle * 2.4f), band + (int)MathF.Ceiling(fsLine * 0.5f));
        var width = Math.Max((int)MathF.Ceiling(2 * outer) + 8, Wide(line, fsLine, false) + 40);
        var height = (int)MathF.Ceiling(2 * (outer + rim));
        var (cx, cy) = (width / 2f, height / 2f);
        var rgba = new byte[width * height * 4];
        (int, int, int) gilt = (219, 179, 107);
        (int, int, int) lacquer = (9, 8, 7);
        (int, int, int) picked = (142, 116, 70);
        var sector = 2f * MathF.PI / slots;
        const float Gap = 0.025f;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (dx, dy) = (x + 0.5f - cx, y + 0.5f - cy);
                var r = MathF.Sqrt((dx * dx) + (dy * dy));
                if (r < inner - 1f || r > outer + 1f)
                {
                    continue;
                }

                var slot = ToolWheel.Slot(dx, dy, slots);
                var mid = (-MathF.PI * 0.5f) + (slot * sector);
                var off = MathF.Atan2(dy, dx) - mid;
                off = MathF.Abs(off - (2f * MathF.PI * MathF.Round(off / (2f * MathF.PI))));
                var toSide = r * ((sector / 2f) - Gap - off);
                var edge = Math.Min(Math.Min(r - inner, outer - r), toSide); // pixels inside the slot's outline
                var cover = Math.Clamp(edge + 0.5f, 0f, 1f);
                if (cover <= 0f)
                {
                    continue;
                }

                var chosen = slot == pick;
                Over(rgba, width, height, x, y, chosen ? picked : lacquer, (int)(cover * (chosen ? 0.9f : 0.92f) * 255));
                var stroke = Math.Max(1.5f, outer * (chosen ? 0.012f : 0.006f));
                var outline = Math.Clamp(stroke - edge + 0.5f, 0f, 1f) * cover;
                if (outline > 0f)
                {
                    Over(rgba, width, height, x, y, gilt, (int)(outline * (chosen ? 0.95f : 0.55f) * 255));
                }
            }
        }

        // The centre: a dark disc, the name and what there is of it; the title above, a line below.
        var hole = inner * 0.96f;
        for (var y = (int)(cy - hole - 1); y <= cy + hole + 1; y++)
        {
            for (var x = (int)(cx - hole - 1); x <= cx + hole + 1; x++)
            {
                var (dx, dy) = (x + 0.5f - cx, y + 0.5f - cy);
                var cover = Math.Clamp(hole + 0.5f - MathF.Sqrt((dx * dx) + (dy * dy)), 0f, 1f);
                if (cover > 0f)
                {
                    Over(rgba, width, height, x, y, (5, 5, 4), (int)(cover * 0.94f * 255));
                }
            }
        }

        void Centred(string text, int pixels, bool bold, float middleY, (int, int, int) color)
        {
            var fitted = Fit(text, pixels, bold, (int)(inner * 1.75f));
            Text(rgba, width, height, fitted, pixels, bold, (int)(cx - (Wide(fitted, pixels, bold) / 2f)), (int)(middleY - (pixels * 0.65f)), color);
        }

        Centred(title, fsTitle, true, cy - outer - (fsTitle * 1.4f), gilt);
        Centred(shown, fsName, true, cy - (fsName * 0.45f), (247, 240, 222));
        if (ammo.Length > 0)
        {
            Centred(ammo, fsAmmo, false, cy + fsAmmo, gilt);
        }

        var bandTop = (int)(cy + outer + (fsLine * 0.5f));
        Fill(rgba, width, 0, bandTop, width, Math.Min(band, height - bandTop), (5, 5, 4), 224);
        Text(rgba, width, height, line, fsLine, false, (int)(cx - (Wide(line, fsLine, false) / 2f)), bandTop + (int)(fsLine * 0.9f), (224, 217, 199));
        return new Card(rgba, width, height, []);
    }

    // The cinematic's title over black (Cinematic): the robot's name in gilt, and under it where it stands; or another
    // film's heading.
    public static Card CinemaTitle(int windowHeight, (string Title, string Under)? heading = null)
    {
        var big = Math.Max(24, windowHeight / 12);
        var small = Math.Max(12, big * 2 / 7);
        var (Title, under) = heading ?? ("DELTA", Words.Say("The brick hall, where the agents work", "La halle de briques, où travaillent les agents"));
        int Wide(string text, int pixels, bool bold) => Splash.Text(text, pixels, out var w, out _, bold) is null ? 0 : w;
        var width = Math.Max(Wide(Title, big, true), Wide(under, small, false)) + 16;
        var height = big + (small * 3);
        var rgba = new byte[width * height * 4];
        Text(rgba, width, height, Title, big, true, (width - Wide(Title, big, true)) / 2, 0, (242, 214, 153));
        Text(rgba, width, height, under, small, false, (width - Wide(under, small, false)) / 2, big + small, (214, 206, 186));
        return new Card(rgba, width, height, []);
    }

    // A line of small text on nothing, such as the cinematic's "Any key: skip".
    public static Card Hint(string text, int windowHeight)
    {
        var scale = Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);
        var pixels = (int)MathF.Round(13 * scale);
        var width = Math.Max(1, (Splash.Text(text, pixels, out var w, out _) is null ? 0 : w) + 4);
        var height = (int)MathF.Round(pixels * 1.6f);
        var rgba = new byte[width * height * 4];
        Text(rgba, width, height, text, pixels, false, 2, 0, (230, 226, 216));
        return new Card(rgba, width, height, []);
    }

    // The camera's keys (FreeCamera, Cinematic, ToolWheel), as the Observatory's controls bar has them; F1 shows it.
    public static Card Keys(bool flying, int windowWidth, int windowHeight)
    {
        var scale = Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);
        int Px(float v) => (int)MathF.Round(v * scale);
        var look = Words.Say("Right button held, or Ctrl + click: look   Home or Enter: back to Delta   B: haze   T: room captions   F6: max out   F7: DLSS   F9: path tracing   F10: RTX   F1: hide", "Clic droit maintenu, ou Ctrl + clic : regarder   Origine ou Entrée : revenir à Delta   B : brume   T : légendes de la pièce   F6 : max out   F7 : DLSS   F9 : path tracing   F10 : RTX   F1 : masquer");
        string[] lines = flying
            ? [Words.Say("FLYING", "EN VOL"),
               Words.Say("ZQSD, WASD or arrows: move   Space or E: rise   C: descend   Shift: faster", "ZQSD, WASD ou flèches : avancer   Espace ou E : monter   C : descendre   Maj : plus vite"),
               Words.Say("F: walk   Wheel: pace   1 to 6: views (Shift and 1 to 5: filmed)   0: the cinematic   Tab: the wheel", "F : marcher   Molette : allure   1 à 6 : vues (Maj + 1 à 5 : filmées)   0 : la cinématique   Tab : la roue"),
               look]
            : [Words.Say("WALKING", "À PIED"),
               Words.Say("ZQSD, WASD or arrows: move   Shift: sprint   Space: jump   C or Ctrl: crouch (sprinting: slide)", "ZQSD, WASD ou flèches : avancer   Maj : sprint   Espace : sauter   C ou Ctrl : s'accroupir (en sprint : glissade)"),
               Words.Say("Q and E (AZERTY: A and E): lean   Alt + moving keys: lean or peek   Caps Lock: walk slowly", "A et E (QWERTY : Q et E) : se pencher   Alt + déplacement : se pencher ou se hausser   Verr. Maj : marcher lentement"),
               Words.Say("F: fly   Wheel: pace   1 to 6: views (Shift and 1 to 5: filmed)   0: the cinematic   Tab: the wheel", "F : voler   Molette : allure   1 à 6 : vues (Maj + 1 à 5 : filmées)   0 : la cinématique   Tab : la roue"),
               look];
        var room = windowWidth - (2 * Px(12)) - (2 * Px(Pad));
        var fitted = lines.Select((l, i) => Fit(l, Px(i == 0 ? 14 : 13), i == 0, room)).ToArray();
        int Wide(string text, int pixels, bool bold) => Splash.Text(text, pixels, out var w, out _, bold) is null ? 0 : w;
        var width = fitted.Select((l, i) => Wide(l, Px(i == 0 ? 14 : 13), i == 0)).Max() + (2 * Px(Pad));
        var height = (2 * Px(Pad)) + Px(22) + ((fitted.Length - 1) * Px(19));
        var rgba = new byte[width * height * 4];
        Fill(rgba, width, 0, 0, width, height, (12, 14, 18), 215);
        Fill(rgba, width, 0, 0, Px(3), height, (219, 179, 107), 255);
        Text(rgba, width, height, fitted[0], Px(14), true, Px(Pad), Px(Pad), (242, 214, 153));
        for (var i = 1; i < fitted.Length; i++)
        {
            Text(rgba, width, height, fitted[i], Px(13), false, Px(Pad), Px(Pad) + Px(22) + ((i - 1) * Px(19)), (214, 220, 226));
        }

        return new Card(rgba, width, height, []);
    }

    // What the GPU offers of NVIDIA's RTX and what the scene does with it, as the Observatory's RTX column has it; F10
    // shows it. Use: in use now; Offered: the GPU has it, the scene does not use it yet; Absent: the GPU lacks it.
    public enum Rtx { Use, Offered, Absent }

    public sealed record RtxRow(string Name, Rtx State, string Detail);

    public static Card RtxFeatures(string gpu, string driver, IReadOnlyList<RtxRow> rows, int fps, int windowHeight)
    {
        var scale = Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);
        int Px(float v) => (int)MathF.Round(v * scale);
        int Wide(string text, int pixels, bool bold) => Splash.Text(text, pixels, out var w, out _, bold) is null ? 0 : w;
        var title = "RTX · F10";
        var under = $"{gpu} · {driver}{(fps > 0 ? $" · {fps} fps" : "")}";
        string State(Rtx s) => s switch
        {
            Rtx.Use => Words.Say("ON", "ACTIF"),
            Rtx.Offered => Words.Say("offered, unused", "offert, inutilisé"),
            _ => Words.Say("not on this GPU", "absent de ce GPU"),
        };
        var dot = Px(9);
        var width = new[] { Wide(title, Px(15), true), Wide(under, Px(12), false) }
            .Concat(rows.Select(r => dot + Px(8) + Wide($"{r.Name}  {State(r.State)}", Px(13), true)))
            .Concat(rows.Select(r => dot + Px(8) + Wide(r.Detail, Px(12), false)))
            .Max() + (2 * Px(Pad));
        var height = (2 * Px(Pad)) + Px(22) + Px(20) + (rows.Count * Px(38));
        var rgba = new byte[width * height * 4];
        Fill(rgba, width, 0, 0, width, height, (12, 14, 18), 215);
        Fill(rgba, width, 0, 0, Px(3), height, (118, 185, 0), 255); // NVIDIA's green
        Text(rgba, width, height, title, Px(15), true, Px(Pad), Px(Pad), (242, 214, 153));
        Text(rgba, width, height, under, Px(12), false, Px(Pad), Px(Pad) + Px(22), (160, 170, 180));
        var y = Px(Pad) + Px(22) + Px(20);
        foreach (var row in rows)
        {
            var (color, label) = row.State switch
            {
                Rtx.Use => ((118, 185, 0), (232, 240, 222)),
                Rtx.Offered => ((140, 146, 152), (200, 206, 212)),
                _ => ((150, 70, 60), (130, 136, 142)),
            };
            Fill(rgba, width, Px(Pad), y + Px(4), dot, dot, color, 255);
            Text(rgba, width, height, $"{row.Name}  {State(row.State)}", Px(13), true, Px(Pad) + dot + Px(8), y, label);
            Text(rgba, width, height, row.Detail, Px(12), false, Px(Pad) + dot + Px(8), y + Px(17), (138, 150, 162));
            y += Px(38);
        }

        return new Card(rgba, width, height, []);
    }

    // A white square that the window stretches and tints, such as the microphone's meter.
    public static Card Blank() => new([.. Enumerable.Repeat((byte)255, 4 * 4 * 4)], 4, 4, []);

    // The card the Inviter button opens: the QR code of a link that lets guests watch the window, until when it serves
    // and whether the clipboard has it; or why there is none.
    public static Card Invitation(Link.Invite invite, DateTime until, int windowWidth, int windowHeight)
    {
        var at = until.ToString("HH:mm");
        string[] lines = (invite.Rows, invite.Refused) switch
        {
            ({ }, _) =>
            [
                invite.Copied ? Words.Say("The link is on the clipboard: paste it into a message.", "Le lien est dans le presse-papier : colle-le dans un message.") : Words.Say("Scan it with a phone to send the link on.", "Scanne-le avec un téléphone pour envoyer le lien."),
                Words.Say($"It serves until {at}, two guests at a time, who only watch.", $"Il sert jusqu'à {at}, pour deux invités à la fois, qui ne font que regarder."),
            ],
            (_, "watching") => [Words.Say("A guest is watching: the window would show them the link.", "Un invité regarde : la fenêtre lui montrerait le lien."), Words.Say("Invite from the paired phone instead.", "Invite plutôt depuis le téléphone jumelé.")],
            (_, "off") => [Words.Say("The phone gateway lets no guest in.", "La passerelle du téléphone ne laisse entrer aucun invité."), Words.Say("Start it with -guest-host to invite.", "Démarre-la avec -guest-host pour inviter.")],
            (_, "gateway") => [Words.Say("No phone gateway answers on this computer.", "Aucune passerelle du téléphone ne répond sur cet ordinateur."), Words.Say("Start it, then try again.", "Démarre-la, puis réessaie.")],
            _ => [Words.Say("This invitation has expired.", "Cette invitation a expiré."), Words.Say("Click Invite for a new one.", "Clique sur Inviter pour en avoir une autre.")],
        };
        return QrCard(Words.Say("Invite someone", "Inviter quelqu'un"), invite.Rows, lines, Words.Say("Click the card or press Esc to close", "Clique sur la carte ou appuie sur Échap pour fermer"), (89, 204, 255), windowWidth, windowHeight);
    }

    // A card with a title, a QR code black on white in whole pixels per module with its quiet zone of four, when rows has
    // one, then lines and how to close it, edged on the left in edge.
    private static Card QrCard(string title, string[]? rows, string[] lines, string closing, (int, int, int) edge, int windowWidth, int windowHeight)
    {
        var scale = Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);
        int Px(float v) => (int)MathF.Round(v * scale);
        var room = Math.Min(Px(520), windowWidth - (2 * Px(12))) - (2 * Px(Pad));
        title = Fit(title, Px(17), true, room);
        lines = [.. lines.Select(l => Fit(l, Px(14), false, room))];
        closing = Fit(closing, Px(12), false, room);
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
        Fill(rgba, width, 0, 0, Px(3), height, edge, 255);
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

    // While Delta listens without its name: a card in the bottom left corner, « Je t'écoute » beside a ring of LeaseDots
    // dots. The card is drawn once for the window's size; the window lays the dots over it (LeaseDot, Dot), lit for the
    // seconds left, so that the ring empties without a texture drawn again, which would stall a frame.
    public const int LeaseDots = 12;

    public static Card Lease(int windowHeight)
    {
        var scale = LeaseScale(windowHeight);
        int Px(float v) => (int)MathF.Round(v * scale);
        var label = Words.Say("Listening", "Je t'écoute");
        int Wide(string text) => Splash.Text(text, Px(14), out var w, out _, true) is null ? 0 : w;
        var ring = Px(30);
        var width = Px(Pad) + ring + Px(10) + Wide(label) + Px(Pad);
        var height = ring + (2 * Px(10));
        var rgba = new byte[width * height * 4];
        Fill(rgba, width, 0, 0, width, height, (12, 14, 18), 205);
        Text(rgba, width, height, label, Px(14), true, Px(Pad) + ring + Px(10), (height - Px(18)) / 2, (230, 236, 242));
        return new Card(rgba, width, height, []);
    }

    // The centre of the ring's dot i in the lease card, clockwise from the top.
    public static (float X, float Y) LeaseDot(int i, int windowHeight)
    {
        var scale = LeaseScale(windowHeight);
        int Px(float v) => (int)MathF.Round(v * scale);
        var ring = Px(30);
        var (cx, cy) = (Px(Pad) + (ring / 2f), (ring + (2 * Px(10))) / 2f);
        var radius = (ring - DotSize(windowHeight)) / 2f;
        var turn = 2f * MathF.PI * i / LeaseDots;
        return (cx + (radius * MathF.Sin(turn)), cy - (radius * MathF.Cos(turn)));
    }

    // A white disc the size of the ring's dots, which the window tints lit or dim.
    public static Card Dot(int windowHeight)
    {
        var size = DotSize(windowHeight);
        var rgba = new byte[size * size * 4];
        var r = size / 2f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var (dx, dy) = (x + 0.5f - r, y + 0.5f - r);
                var cover = Math.Clamp(r + 0.5f - MathF.Sqrt((dx * dx) + (dy * dy)), 0f, 1f);
                Over(rgba, size, size, x, y, (255, 255, 255), (int)(cover * 255));
            }
        }

        return new Card(rgba, size, size, []);
    }

    // While Claude thinks: a small amber brain with dark folds, which the window lays in the bottom left corner, beside
    // the listening ring when that shows, and makes beat (BrainBeat), as the user asked for on 2026-10-23.
    public static Card Brain(int windowHeight)
    {
        var scale = LeaseScale(windowHeight);
        var (width, height) = ((int)MathF.Round(42 * scale), (int)MathF.Round(34 * scale));
        var rgba = new byte[width * height * 4];
        const int Sub = 4; // samples per pixel on each axis, for smooth edges
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (halo, fill) = (0, 0);
                for (var j = 0; j < Sub; j++)
                {
                    for (var i = 0; i < Sub; i++)
                    {
                        var (u, v) = ((x + ((i + 0.5f) / Sub)) / width, (y + ((j + 0.5f) / Sub)) / height);
                        halo += InBrain(u, v, 0.06f) ? 1 : 0;
                        fill += InBrain(u, v, 0f) && !OnFold(u, v) ? 1 : 0;
                    }
                }

                Over(rgba, width, height, x, y, (24, 18, 10), halo * 230 / (Sub * Sub));
                Over(rgba, width, height, x, y, (255, 190, 90), fill * 255 / (Sub * Sub));
            }
        }

        return new Card(rgba, width, height, []);
    }

    // Beside the brain, how long Claude has worked: one dot of the ring's (Dot) per ten seconds, up to nine, laid by the
    // window like the ring's, so that no texture is drawn again while Claude works. There is no estimate of the time
    // left: Claude Code gives none.
    public static int ElapsedDots(double seconds) => Math.Clamp((int)(seconds / 10), 0, 9);

    // How opaque the brain is, t seconds after Claude began to think: full at once, then beating every 1.2 s.
    public static float BrainBeat(double t) => 0.55f + (0.45f * (0.5f + (0.5f * MathF.Cos(2f * MathF.PI * (float)(t / 1.2)))));

    // The brain's lobes, as circles in the card's unit square, grown by grow for its dark halo.
    private static bool InBrain(float u, float v, float grow)
    {
        ReadOnlySpan<(float X, float Y, float R)> lobes = [(0.30f, 0.40f, 0.22f), (0.50f, 0.30f, 0.22f), (0.70f, 0.40f, 0.22f), (0.28f, 0.62f, 0.20f), (0.72f, 0.62f, 0.20f), (0.50f, 0.60f, 0.24f)];
        foreach (var (cx, cy, r) in lobes)
        {
            if (((u - cx) * (u - cx)) + ((v - cy) * (v - cy)) < (r + grow) * (r + grow))
            {
                return true;
            }
        }

        return false;
    }

    // The line between the halves and a fold in each of their quarters.
    private static bool OnFold(float u, float v)
    {
        static bool Arc(float u, float v, float cx, float cy, float r) => MathF.Abs(MathF.Sqrt(((u - cx) * (u - cx)) + ((v - cy) * (v - cy))) - r) < 0.022f;
        return (MathF.Abs(u - 0.5f) < 0.022f && v is > 0.10f and < 0.80f) ||
            (v < 0.47f && (Arc(u, v, 0.30f, 0.50f, 0.11f) || Arc(u, v, 0.70f, 0.50f, 0.11f))) ||
            (v > 0.68f && (Arc(u, v, 0.34f, 0.62f, 0.10f) || Arc(u, v, 0.66f, 0.62f, 0.10f)));
    }

    internal static void BrainSelfTest(Action<bool, string> check)
    {
        var brain = Brain(720);
        byte[] At(int x, int y) => brain.Rgba[(((y * brain.Width) + x) * 4)..((((y * brain.Width) + x) * 4) + 4)];
        check(brain is { Width: 42, Height: 34 } && At(0, 0)[3] == 0, "the thinking brain is small, clear around it");
        check(At(8, 21) is [255, 190, 90, 255], "its lobes are amber");
        check(At(21, 17) is [< 60, < 60, < 60, > 200], "the line between its halves is dark");
        check(BrainBeat(0) == 1f && MathF.Abs(BrainBeat(0.6) - 0.55f) < 1e-4f && BrainBeat(1.2) > 0.99f, "it beats every 1.2 s, from full");
        check(ElapsedDots(9.9) == 0 && ElapsedDots(10) == 1 && ElapsedDots(42) == 4 && ElapsedDots(300) == 9 && ElapsedDots(-1) == 0, "beside it, a dot per ten seconds Claude has worked, up to nine");
    }

    private static float LeaseScale(int windowHeight) => Math.Clamp(windowHeight / 720f, 0.8f, 2.2f);

    private static int DotSize(int windowHeight) => Math.Max(3, (int)MathF.Round(5f * LeaseScale(windowHeight)));

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
