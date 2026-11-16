using SN = System.Numerics;

// A separate, static companion in the hall. This is only a visual presence: no transport, microphone or commands. Its
// placard says how the bridge to Gaia stands as the brain sees it (Link's "gaia"), never a state the window invents.
internal static class GaiaPresence
{
    public static readonly string[] Labels = ["Gaia — disconnected", "Gaia — non raccordée"];

    public static string Status => Words.Say(Labels[0], Labels[1]);

    // A Gaia module's placard says she is disconnected, in either language and with either dash; while the brain says
    // otherwise, the host shows the bridge's state in its place. Any other text of the module stays as it is.
    public static string Bridged(string text, string bridge, string language)
    {
        var fr = text is "Gaia — non raccordée" or "Gaia - non raccordée";
        if (bridge == "offline" || !(fr || text is "Gaia — disconnected" or "Gaia - disconnected")) return text;
        return (bridge, fr || language == "fr") switch
        {
            ("listening", true) => "Gaia — relais à l'écoute",
            ("listening", false) => "Gaia — relay listening",
            ("connected", true) => "Gaia — raccordée",
            ("connected", false) => "Gaia — connected",
            (_, true) => "Gaia — dégradée",
            _ => "Gaia — degraded",
        };
    }

    public static void SelfTest(Action<bool, string> check)
    {
        var setting = Words.Setting;
        var language = Words.Language;
        var link = new Link(() => 0);
        try
        {
            foreach (var (code, expected) in new[]
            {
                ("fr", "Gaia — non raccordée"),
                ("en", "Gaia — disconnected"),
                ("fr", "Gaia — non raccordée"),
                ("en+fr", "Gaia — disconnected"),
                ("fr+en", "Gaia — non raccordée"),
            })
            {
                var change = Link.Parse($$"""{"language":"{{code}}"}""", out _);
                link.Apply(change!, 0);
                check(Status == expected, $"Gaia's visible label follows {code} on the same scene link");
                check(Labels.Contains(Status), $"Gaia has a prepared placard for {code}");
            }

            check(Bridged("Gaia - non raccordée", "offline", "fr") == "Gaia - non raccordée" && Bridged("Gaia — non raccordée", "listening", "fr") == "Gaia — relais à l'écoute"
                && Bridged("Gaia - disconnected", "connected", "en") == "Gaia — connected" && Bridged("Gaia — disconnected", "degraded", "en") == "Gaia — degraded"
                && Bridged("v1.1 active", "connected", "fr") == "v1.1 active", "the placard says the bridge's state, and only the placard");
        }
        finally
        {
            Words.Speak(setting);
            Words.Language = language;
        }
    }

    public static Vertex[] Build(string status)
    {
        if (!Labels.Contains(status))
        {
            throw new ArgumentException("Gaia's placard must be one of the localized offline labels.", nameof(status));
        }

        List<Vertex> vertices = [];
        var violet = new Finish(new(0.48f, 0.23f, 0.78f), Material.Enamel);
        var pale = new Finish(new(0.82f, 0.72f, 0.98f), Material.Matte);
        var dark = new Finish(new(0.055f, 0.045f, 0.08f), Material.Matte);
        const float x = 0.88f, z = 0.16f;

        // A small grounded totem beside Delta, clear of the robot and the shelf controls. Nothing pulses or glows:
        // violet identifies Gaia; the grey marker and the words below explicitly say that she is not connected.
        Boxes.Add(vertices, new(x, 0.035f, z), new(0.48f, 0.07f, 0.34f), dark);
        Boxes.Add(vertices, new(x, 0.09f, z), new(0.36f, 0.04f, 0.25f), violet);
        Boxes.Add(vertices, new(x, 0.37f, z), new(0.12f, 0.54f, 0.12f), dark);
        Boxes.Add(vertices, new(x, 0.70f, z), new(0.30f, 0.40f, 0.17f), violet);
        Boxes.Add(vertices, new(x, 0.70f, z + 0.09f), new(0.25f, 0.34f, 0.015f), dark);
        Letter(vertices, "G", new(x, 0.72f, z + 0.103f), 0.19f, pale);
        Boxes.Add(vertices, new(x, 0.56f, z + 0.105f), new(0.04f, 0.014f, 0.008f), new SN.Vector3(0.40f));

        Boxes.Add(vertices, new(x, 0.27f, z + 0.16f), new(0.86f, 0.12f, 0.025f), dark);
        Letter(vertices, status, new(x, 0.27f, z + 0.177f), 0.78f, pale);
        return vertices.ToArray();
    }

    // Reuse the scene's existing Unicode font rasterizer. Contiguous covered pixels become shallow box runs, so
    // the label is part of the 3D object, depth tested in the existing pipeline, without another texture or UI card.
    internal static void Letter(List<Vertex> vertices, string text, SN.Vector3 centre, float width, Finish finish)
    {
        var rgba = Splash.Text(text, 22, out var w, out var h, true);
        if (rgba is null || w == 0 || h == 0)
        {
            throw new InvalidOperationException("Gaia's offline label could not be rasterized.");
        }

        var pixel = width / w;
        for (var row = 0; row < h; row++)
        {
            for (var col = 0; col < w;)
            {
                if (rgba[(((row * w) + col) * 4) + 3] < 96)
                {
                    col++;
                    continue;
                }

                var start = col++;
                while (col < w && rgba[(((row * w) + col) * 4) + 3] >= 96)
                {
                    col++;
                }

                var at = centre + new SN.Vector3(((start + col) * 0.5f - w * 0.5f) * pixel, (h * 0.5f - row - 0.5f) * pixel, 0f);
                Boxes.Add(vertices, at, new((col - start) * pixel, pixel, 0.002f), finish);
            }
        }
    }
}
