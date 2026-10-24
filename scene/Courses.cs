using System.Diagnostics;

// The shelf's courses: those of Guitar Alchemist Learn (spareilleux.github.io/learn), read from the site's source when
// this computer has a clone of it, in the window's language. Each course, by its title, opens onto its lessons in the
// site's order, and each lesson onto its sections, with a row that opens it on the site.
internal static class Courses
{
    private const string Site = "https://spareilleux.github.io/learn/";

    // A page's front matter: its title, its description and its place in the course, when it has them.
    internal sealed record Front(string Title, string Description, int Order);

    // The site's pages: under DELTA_LEARN, or source\repos\learn in the user's folder.
    internal static string Docs(string? clone = null)
    {
        clone ??= Environment.GetEnvironmentVariable("DELTA_LEARN") is { Length: > 0 } set ? set : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos", "learn");
        return Path.Combine(clone, "src", "content", "docs");
    }

    public static List<Shelf.Node> List() => List(Docs(), Words.Language == "fr" ? "fr" : "");

    // The courses under docs in a locale ("" for English), by title.
    internal static List<Shelf.Node> List(string docs, string locale)
    {
        if (!Directory.Exists(docs))
        {
            throw new InvalidOperationException(Words.Say("No clone of learn on this computer: set DELTA_LEARN to one.", "Pas de clone de learn sur cet ordinateur : DELTA_LEARN peut en nommer un."));
        }

        var root = locale.Length > 0 && Directory.Exists(Path.Combine(docs, locale)) ? Path.Combine(docs, locale) : docs;
        var prefix = root == docs ? "" : locale + "/";
        var courses = new List<(string Title, Shelf.Node Node)>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (root == docs && name is "fr" or "es")
            {
                continue;
            }

            var lessons = Pages(dir).Where(p => Path.GetFileNameWithoutExtension(p) != "index").ToList();
            if (lessons.Count == 0)
            {
                continue;
            }

            var index = Pages(dir).FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == "index");
            var title = index is null ? name : Read(index).Title is { Length: > 0 } t ? t : name;
            var url = Site + prefix + name + "/";
            courses.Add((title, new Shelf.Node(title, Words.Say($"{lessons.Count} lessons", $"{lessons.Count} leçons"), () => Lessons(lessons, url))));
        }

        return [.. courses.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).Select(c => c.Node)];
    }

    private static IEnumerable<string> Pages(string dir) =>
        Directory.GetFiles(dir).Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase));

    // A course's lessons in the site's order: by their sidebar order, then by file name.
    private static List<Shelf.Node> Lessons(List<string> pages, string course) =>
    [
        .. pages.Select(p => (Page: p, Front: Read(p)))
            .OrderBy(l => l.Front.Order).ThenBy(l => Path.GetFileName(l.Page), StringComparer.Ordinal)
            .Select(l =>
            {
                var url = course + Path.GetFileNameWithoutExtension(l.Page).ToLowerInvariant() + "/";
                var title = l.Front.Title.Length > 0 ? l.Front.Title : Path.GetFileNameWithoutExtension(l.Page);
                return new Shelf.Node(title, l.Front.Description, () => Sections(l.Page, url));
            }),
    ];

    // A lesson's rows: one that opens it on the site, then its sections, its "## " headings.
    private static List<Shelf.Node> Sections(string page, string url)
    {
        var rows = new List<Shelf.Node> { new(Words.Say("Open on the site", "Ouvrir sur le site"), url, act: () => Open(url)) };
        var fenced = false;
        foreach (var line in File.ReadLines(page))
        {
            fenced ^= line.TrimStart().StartsWith("```", StringComparison.Ordinal);
            if (!fenced && line.StartsWith("## ", StringComparison.Ordinal))
            {
                rows.Add(new Shelf.Node(line[3..].Trim().Replace("`", "")));
            }
        }

        return rows;
    }

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"scene: cannot open {url}: {e.Message}");
        }
    }

    // The front matter between the page's first two "---" lines: title, description and sidebar order.
    internal static Front Read(string page)
    {
        var (title, description, order) = ("", "", int.MaxValue);
        using var lines = File.ReadLines(page).GetEnumerator();
        if (!lines.MoveNext() || lines.Current.Trim() != "---")
        {
            return new Front(title, description, order);
        }

        while (lines.MoveNext() && lines.Current.Trim() != "---")
        {
            var line = lines.Current;
            var value = line.Contains(':') ? line[(line.IndexOf(':') + 1)..].Trim().Trim('"', '\'') : "";
            if (line.StartsWith("title:", StringComparison.Ordinal))
            {
                title = value;
            }
            else if (line.StartsWith("description:", StringComparison.Ordinal))
            {
                description = value;
            }
            else if (line.TrimStart().StartsWith("order:", StringComparison.Ordinal) && int.TryParse(value, out var n))
            {
                order = n;
            }
        }

        return new Front(title, description, order);
    }

    // Part of --selftest: a small site with a course in English and in French.
    internal static void SelfTest(Action<bool, string> check)
    {
        var dir = Directory.CreateTempSubdirectory("delta-courses-");
        try
        {
            var docs = Path.Combine(dir.FullName, "src", "content", "docs");
            foreach (var (folder, title, lesson) in new[] { ("guitar", "Guitar", "Scales"), (Path.Combine("fr", "guitar"), "Guitare", "Gammes") })
            {
                Directory.CreateDirectory(Path.Combine(docs, folder));
                File.WriteAllText(Path.Combine(docs, folder, "index.md"), $"---\ntitle: {title}\n---\n");
                File.WriteAllText(Path.Combine(docs, folder, "02-scales.md"), $"---\ntitle: \"2. {lesson}\"\ndescription: Modes\nsidebar:\n  order: 2\n---\n\n## One\n\n```\n## not a heading\n```\n\n## Two\n");
                File.WriteAllText(Path.Combine(docs, folder, "01-notes.mdx"), "---\ntitle: 1. Notes\nsidebar:\n  order: 1\n---\n");
            }

            Directory.CreateDirectory(Path.Combine(docs, "empty"));
            var english = List(docs, "");
            check(english is [{ Label: "Guitar", Detail: "2 lessons" or "2 leçons" }], "the courses are the folders with lessons, by their titles");
            var lessons = english[0].Children!();
            check(lessons is [{ Label: "1. Notes" }, { Label: "2. Scales", Detail: "Modes" }], "a course's lessons are in the site's order");
            check(lessons[1].Children!() is [{ Detail: "https://spareilleux.github.io/learn/guitar/02-scales/", Act: not null }, { Label: "One" }, { Label: "Two" }],
                "a lesson opens on the site and lists its sections, not its code's");
            check(List(docs, "fr") is [{ Label: "Guitare" } course] && course.Children!()[1].Children!()[0].Detail == "https://spareilleux.github.io/learn/fr/guitar/02-scales/",
                "the French courses are the site's French pages");
            var missing = false;
            try
            {
                List(Path.Combine(dir.FullName, "nowhere"), "");
            }
            catch (InvalidOperationException)
            {
                missing = true;
            }

            check(missing, "without a clone the shelf says how to name one");
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
