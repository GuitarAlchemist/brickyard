using Delta.SceneModules;
using System.Net;
using System.Text.RegularExpressions;

namespace Delta.LearnReader;

// The book open on the gallery's lectern (scene/HallGallery.cs): a course of the learn site, or a department of
// Streeling University, read in the hall. The left page is the outline: a course's lessons, as the site's sidebar lists
// them, or a department's modules. The right page is the page being read, its text taken from the site. A click on the
// outline opens an entry, the arrows turn the pages, and a link opens the page on the site in the browser. The pages
// are drawn into one RGBA picture, two pages side by side, that the scene shows on the book; each new picture has a new
// revision. This is a mod (module-contracts/ReaderModule.cs): the scene runs it from modules\reader\LearnReader.Module.dll
// when one is there, and reloads it when a build replaces it, else the same code built into the scene. The host reads
// the pages (the learn site only) and draws the text.
public sealed partial class ReaderModule : IReaderModule
{
    public const int Page = 1000, Width = ReaderPicture.Width, Height = ReaderPicture.Height;
    public const string Site = "https://spareilleux.github.io/learn/";
    private const int Margin = 70, Footer = 1210;

    // A line of the right page: a heading (2 or 3), a bullet, or text, as the page has it.
    internal readonly record struct Block(int Kind, string Text);

    private readonly record struct Region(int X0, int Y0, int X1, int Y1, Action<ReaderModule> Act);

    private readonly object gate = new();
    private sealed record Shown(byte[] Pixels, ulong Revision, Region[] Regions);

    private IReaderHost host = null!;
    private Shown shown = new([], 0, []);
    private long request;

    // What is open: the book's title and link, its outline and which entry is read, the text read and how far in.
    private string title = "", link = Site;
    private List<ReaderItem> outline = [];
    private int outlinePage, reading = -1, readingPage;
    private string readingTitle = "", readingLink = "", status = "";
    private List<Block> body = [];

    public string Id => "learn-reader";

    public void Initialize(IReaderHost host)
    {
        this.host = host;
        status = Say("Click a course's book or a Streeling drawer to read it here.", "Clique sur le livre d'un cours ou sur un tiroir de Streeling pour le lire ici.");
        readingTitle = Say("The reading desk", "Le lutrin");
        Publish();
    }

    public void Dispose() => Interlocked.Increment(ref request); // a page still being read is not shown

    public ReaderPicture Picture => Volatile.Read(ref shown) is var s ? new(s.Pixels, s.Revision) : default;

    public string Title => title;

    private string Say(string english, string french) => host.Language == "fr" ? french : english;

    // A course: its lessons from the site's sidebar, its own page on the right.
    public void OpenCourse(string name, string url)
    {
        lock (gate)
        {
            (title, link, outline, outlinePage, reading, readingTitle, readingLink, body, readingPage) = (name, url, [], 0, -1, name, url, [], 0);
            status = Say("Loading…", "Chargement…");
            Publish();
        }

        var id = Interlocked.Increment(ref request);
        _ = Task.Run(async () =>
        {
            var page = await host.Fetch(url);
            lock (gate)
            {
                if (id != Interlocked.Read(ref request)) return;
                if (page.Html is not { } html)
                {
                    status = page.Why;
                }
                else
                {
                    outline = Lessons(html, page.Url);
                    body = Extract(html);
                    readingTitle = TitleOf(html) ?? name;
                    title = Course(readingTitle);
                    status = outline.Count == 0 ? Say("This course lists no lessons.", "Ce cours ne liste aucune leçon.") : "";
                }

                Publish();
            }
        });
    }

    // A department of Streeling: its modules as the gallery lists them, the first one on the right.
    public void OpenDepartment(string name, IReadOnlyList<ReaderItem> modules)
    {
        lock (gate)
        {
            (title, link, outline, outlinePage, reading, readingPage, body) = (name, modules.Count > 0 ? Parent(modules[0].Url) : Site, [.. modules], 0, -1, 0, []);
            (readingTitle, readingLink) = (name, link);
            status = Say($"{modules.Count} modules. Click one to read it.", $"{modules.Count} modules. Clique sur l'un d'eux pour le lire.");
            Publish();
        }
    }

    // The entry at index of the outline, read on the right page.
    public void Read(int index)
    {
        ReaderItem item;
        lock (gate)
        {
            if (index < 0 || index >= outline.Count) return;
            (reading, item) = (index, outline[index]);
            (readingTitle, readingLink, body, readingPage) = (item.Title, item.Url, [], 0);
            status = Say("Loading…", "Chargement…");
            Publish();
        }

        var id = Interlocked.Increment(ref request);
        _ = Task.Run(async () =>
        {
            var page = await host.Fetch(item.Url);
            lock (gate)
            {
                if (id != Interlocked.Read(ref request)) return;
                (body, status) = page.Html is { } html ? (Extract(html), "") : ([], page.Why);
                if (page.Html is not null) readingTitle = TitleOf(page.Html) ?? item.Title;
                Publish();
            }
        });
    }

    // A click at u, v across the picture (0 to 1 each, from its top left): the region there acts.
    public void Click(float u, float v)
    {
        var (x, y) = ((int)(u * Width), (int)(v * Height));
        var region = Volatile.Read(ref shown).Regions.FirstOrDefault(r => x >= r.X0 && x < r.X1 && y >= r.Y0 && y < r.Y1);
        region.Act?.Invoke(this);
    }

    private void Turn(ref int page, int by, int pages)
    {
        lock (gate)
        {
            page = Math.Clamp(page + by, 0, Math.Max(0, pages - 1));
            Publish();
        }
    }

    // Draws both pages and their regions as they now stand, as the next revision. Called with gate held, or alone.
    private void Publish()
    {
        var rgba = new byte[Width * Height * 4];
        var regions = new List<Region>();
        Paper(rgba);

        // The left page: the title, then the outline, a page of it at a time.
        var y = Margin;
        foreach (var line in Wrap(title, 40, true, Page - (2 * Margin)))
        {
            y += Write(rgba, line, 40, true, Margin, y, (60, 30, 20)) + 4;
        }

        y += 10;
        Rule(rgba, Margin, Page - Margin, y);
        y += 22;
        const int entry = 38;
        var perPage = Math.Max(1, (Footer - 100 - y) / entry);
        var outlinePages = Math.Max(1, (outline.Count + perPage - 1) / perPage);
        outlinePage = Math.Clamp(outlinePage, 0, outlinePages - 1);
        for (var i = outlinePage * perPage; i < Math.Min(outline.Count, (outlinePage + 1) * perPage); i++)
        {
            var current = i == reading;
            var text = Fit(outline[i].Title, 25, current, Page - (2 * Margin) - 20);
            Write(rgba, text, 25, current, Margin + 10, y, current ? ((byte)140, (byte)30, (byte)20) : ((byte)40, (byte)36, (byte)32));
            var index = i;
            regions.Add(new Region(Margin, y - 4, Page - Margin, y + entry - 4, r => r.Read(index)));
            y += entry;
        }

        if (outline.Count == 0 && status.Length > 0 && reading < 0 && body.Count == 0)
        {
            foreach (var line in Wrap(status, 26, false, Page - (2 * Margin)))
            {
                y += Write(rgba, line, 26, false, Margin, y, (90, 80, 70)) + 6;
            }
        }

        if (outlinePages > 1)
        {
            Arrows(rgba, regions, 0, $"{outlinePage + 1} / {outlinePages}", r => r.Turn(ref r.outlinePage, -1, outlinePages), r => r.Turn(ref r.outlinePage, 1, outlinePages));
        }

        Link(rgba, regions, Margin, Say("Open on the site ↗", "Ouvrir sur le site ↗"), link);

        // The right page: what is read, its text a page at a time.
        var right = Page + Margin;
        var width = Page - (2 * Margin);
        y = Margin;
        foreach (var line in Wrap(readingTitle, 34, true, width))
        {
            y += Write(rgba, line, 34, true, right, y, (60, 30, 20)) + 4;
        }

        y += 8;
        Rule(rgba, right, Width - Margin, y);
        y += 20;
        var lines = Lay(body.Count > 0 ? body : [new Block(0, status)], width);
        var top = y;
        var pages = Paginate(lines, Footer - 100 - top);
        readingPage = Math.Clamp(readingPage, 0, pages.Count - 1);
        foreach (var (text, size, bold, indent, gapBefore) in pages[readingPage])
        {
            y += gapBefore;
            Write(rgba, text, size, bold, right + indent, y, bold ? ((byte)70, (byte)35, (byte)25) : ((byte)35, (byte)32, (byte)28));
            y += size + 10;
        }

        if (pages.Count > 1)
        {
            Arrows(rgba, regions, Page, $"{readingPage + 1} / {pages.Count}", r => r.Turn(ref r.readingPage, -1, pages.Count), r => r.Turn(ref r.readingPage, 1, pages.Count));
        }

        if (readingLink.Length > 0)
        {
            Link(rgba, regions, right, Say("Open this page on the site ↗", "Ouvrir cette page sur le site ↗"), readingLink);
        }

        var previous = Volatile.Read(ref shown);
        Volatile.Write(ref shown, new Shown(rgba, previous.Revision + 1, regions.ToArray()));
    }

    // A line laid out: its text, size, weight, indent and the space above it.
    private List<(string Text, int Size, bool Bold, int Indent, int Gap)> Lay(List<Block> blocks, int width)
    {
        var laid = new List<(string, int, bool, int, int)>();
        foreach (var block in blocks)
        {
            var (size, bold, indent, gap) = block.Kind switch
            {
                2 => (30, true, 0, 18),
                3 => (27, true, 0, 12),
                1 => (25, false, 26, 2),
                _ => (25, false, 0, 8),
            };
            var first = true;
            foreach (var line in Wrap(block.Text, size, bold, width - indent))
            {
                laid.Add((first && block.Kind == 1 ? "• " + line : line, size, bold, first && block.Kind == 1 ? 0 : indent, first ? gap : 0));
                first = false;
            }
        }
        return laid;
    }

    private static List<List<(string Text, int Size, bool Bold, int Indent, int Gap)>> Paginate(List<(string Text, int Size, bool Bold, int Indent, int Gap)> lines, int room)
    {
        var pages = new List<List<(string, int, bool, int, int)>> { new() };
        var used = 0;
        foreach (var line in lines)
        {
            var need = line.Gap + line.Size + 10;
            if (used + need > room && pages[^1].Count > 0)
            {
                pages.Add([]);
                used = 0;
                pages[^1].Add(line with { Gap = 0 });
                used += line.Size + 10;
                continue;
            }

            pages[^1].Add(line);
            used += need;
        }

        return pages;
    }

    private void Arrows(byte[] rgba, List<Region> regions, int left, string label, Action<ReaderModule> back, Action<ReaderModule> on)
    {
        var y = Footer - 60;
        Write(rgba, "‹", 40, true, left + Margin, y - 10, (110, 40, 25));
        Write(rgba, label, 22, false, left + (Page / 2) - 30, y, (110, 100, 90));
        Write(rgba, "›", 40, true, left + Page - Margin - 20, y - 10, (110, 40, 25));
        regions.Add(new Region(left + 20, y - 30, left + 200, y + 50, back));
        regions.Add(new Region(left + Page - 200, y - 30, left + Page - 20, y + 50, on));
    }

    private void Link(byte[] rgba, List<Region> regions, int x, string label, string url)
    {
        var h = Write(rgba, label, 23, true, x, Footer, (30, 70, 140));
        regions.Add(new Region(x - 10, Footer - 8, x + Page - (2 * Margin), Footer + h + 8, r => r.host.Browse(url)));
    }

    // Warm paper, darker toward the gutter and the edges.
    private static void Paper(byte[] rgba)
    {
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var gutter = Math.Abs(x - Page) / (float)Page;
                var shade = 0.78f + (0.22f * MathF.Min(1f, gutter * 9f)) - (0.06f * MathF.Max(0f, gutter - 0.92f) / 0.08f);
                var i = ((y * Width) + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)(214 * shade), (byte)(204 * shade), (byte)(182 * shade), 255);
            }
        }
    }

    private static void Rule(byte[] rgba, int x0, int x1, int y)
    {
        for (var x = x0; x < x1; x++)
        {
            for (var t = 0; t < 2; t++)
            {
                var i = (((y + t) * Width) + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2]) = (150, 110, 60);
            }
        }
    }

    // Writes text at x, y in the picture; returns its height.
    private int Write(byte[] rgba, string text, int pixels, bool bold, int x, int y, (byte R, byte G, byte B) color)
    {
        var glyphs = host.Text(text, pixels, bold, out var w, out var h);
        if (glyphs is null)
        {
            return pixels;
        }

        for (var j = 0; j < h && y + j < Height; j++)
        {
            for (var i = 0; i < w && x + i < Width; i++)
            {
                var a = glyphs[(((j * w) + i) * 4) + 3] / 255f;
                if (a > 0 && y + j >= 0)
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

    // How wide text is drawn at that size, each word measured once.
    private static readonly Dictionary<(string, int, bool), int> Widths = [];

    private int Measure(string text, int size, bool bold)
    {
        lock (Widths)
        {
            if (Widths.TryGetValue((text, size, bold), out var known)) return known;
        }

        _ = host.Text(text, size, bold, out var w, out _);
        lock (Widths)
        {
            if (Widths.Count > 20_000) Widths.Clear();
            Widths[(text, size, bold)] = w;
        }

        return w;
    }

    // Text in lines no wider than width, by words; a word wider than a line is cut.
    internal List<string> Wrap(string text, int size, bool bold, int width)
    {
        var lines = new List<string>();
        var space = Math.Max(1, Measure("a a", size, bold) - Measure("aa", size, bold));
        var (line, used) = ("", 0);
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = Measure(word, size, bold);
            if (line.Length > 0 && used + space + w > width)
            {
                lines.Add(line);
                (line, used) = ("", 0);
            }

            if (line.Length == 0 && w > width)
            {
                lines.Add(Fit(word, size, bold, width));
                continue;
            }

            (line, used) = line.Length == 0 ? (word, w) : (line + " " + word, used + space + w);
        }

        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    // Text cut with an ellipsis to fit width.
    private string Fit(string text, int size, bool bold, int width)
    {
        if (Measure(text, size, bold) <= width) return text;
        for (var cut = text.Length - 1; cut > 1; cut--)
        {
            var t = text[..cut].TrimEnd() + "…";
            if (Measure(t, size, bold) <= width) return t;
        }

        return "…";
    }

    private static string Parent(string url)
    {
        var trimmed = url.TrimEnd('/');
        return trimmed[..(trimmed.LastIndexOf('/') + 1)];
    }

    // The page's own title: its h1, else its <title> before " | ".
    internal static string? TitleOf(string html)
    {
        var h1 = H1().Match(html);
        var text = h1.Success ? Clean(h1.Groups[1].Value) : TitleTag().Match(html) is { Success: true } t ? Clean(t.Groups[1].Value).Split(" | ")[0] : null;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // A course's lessons: the sidebar's links under the course's own path, in its order, the course page itself left out.
    internal static List<ReaderItem> Lessons(string html, string url)
    {
        var path = new Uri(url).AbsolutePath; // /learn/[fr/]course/
        var english = path.StartsWith("/learn/fr/", StringComparison.Ordinal) ? "/learn/" + path["/learn/fr/".Length..] : path; // a lesson not yet translated links there
        var lessons = new List<ReaderItem>();
        var seen = new HashSet<string>();
        var article = html.IndexOf("sl-markdown-content", StringComparison.Ordinal); // the sidebar comes before the article
        foreach (Match m in Anchor().Matches(article > 0 ? html[..article] : html))
        {
            var href = WebUtility.HtmlDecode(m.Groups[1].Value);
            var under = href.StartsWith(path, StringComparison.Ordinal) ? href[path.Length..] : href.StartsWith(english, StringComparison.Ordinal) ? href[english.Length..] : null;
            if (under is not { Length: > 0 } || href.Contains('#') || !seen.Add(under)) continue;
            var text = Clean(m.Groups[2].Value);
            if (text.Length > 0) lessons.Add(new ReaderItem(text, new Uri(new Uri(url), href).AbsoluteUri));
        }

        return lessons;
    }

    // The page's article as lines: its headings, list items and paragraphs, its code's lines, without the site's
    // navigation, scripts or pictures.
    internal static List<Block> Extract(string html)
    {
        var start = html.IndexOf("sl-markdown-content", StringComparison.Ordinal);
        start = start >= 0 ? html.IndexOf('>', start) + 1 : Math.Max(0, html.IndexOf("<main", StringComparison.Ordinal));
        var end = html.IndexOf("<footer", start, StringComparison.Ordinal);
        if (end < 0) end = html.Length;
        var part = html[start..end];
        part = Drop().Replace(part, " ");
        part = ScreenReaders().Replace(part, " ");
        part = Heading().Replace(part, m => $"\n\u0001{m.Groups[1].Value}");
        part = Item_().Replace(part, "\n\u0002");
        part = Breaks().Replace(part, "\n");
        part = Tag().Replace(part, "");
        var blocks = new List<Block>();
        foreach (var raw in WebUtility.HtmlDecode(part).Split('\n'))
        {
            var kind = 0;
            var line = raw;
            if (line.StartsWith('\u0001') && line.Length > 1)
            {
                kind = line[1] == '2' ? 2 : 3;
                line = line[2..];
            }
            else if (line.StartsWith('\u0002'))
            {
                kind = 1;
                line = line[1..];
            }

            line = Spaces().Replace(line, " ").Trim();
            if (line.Length == 0 || line.StartsWith("sl-markdown-content", StringComparison.Ordinal) || line.StartsWith('"')) continue;
            if (blocks.Count > 0 && blocks[^1].Kind == 0 && kind == 0 && line.Length < 2) continue;
            blocks.Add(new Block(kind, line.Length > 2000 ? line[..2000] + "…" : line));
        }

        return blocks;
    }

    // A course page's title without its page's own name: "F# for C#/Java developers — Mission" is the course's.
    internal static string Course(string title) =>
        title.LastIndexOf(" — ", StringComparison.Ordinal) is > 0 and var dash && !title[(dash + 3)..].Contains(' ') ? title[..dash] : title;

    private static string Clean(string html) => Spaces().Replace(WebUtility.HtmlDecode(Tag().Replace(html, " ")), " ").Trim();

    [GeneratedRegex(@"<h1[^>]*>(.*?)</h1>", RegexOptions.Singleline)]
    private static partial Regex H1();

    [GeneratedRegex(@"<title>(.*?)</title>", RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<a\s[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex Anchor();

    [GeneratedRegex(@"<(script|style|svg|button|template|nav|aside|figure|picture|select)\b.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Drop();

    [GeneratedRegex(@"<span[^>]*\bsr-only\b[^>]*>.*?</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScreenReaders();

    [GeneratedRegex(@"<h([23])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Heading();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Item_();

    [GeneratedRegex(@"<(br|/p|/h[1-6]|/li|/tr|/pre|/div|/blockquote|/dt|/dd)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\r ]+")]
    private static partial Regex Spaces();

    // Part of the scene's --selftest, with the scene's host: a page of the site as Starlight writes it, read without the
    // network, and a department opened, clicked and turned. Links opened go to opened instead of the browser.
    public static void SelfTest(IReaderHost host, Action<bool, string> check)
    {
        const string html = """
            <html><head><title>Variables | learn</title></head><body>
            <nav aria-label="Main"><a href="/learn/csharp-beginner/">Overview</a><a href="/learn/csharp-beginner/01-hello/"><span>1. Hello</span></a>
            <a href="/learn/csharp-beginner/02-variables/">2. Variables</a><a href="/learn/fsharp/01-x/">F# 1</a><a href="/learn/csharp-beginner/02-variables/#a">anchor</a></nav>
            <main><h1 id="_top">Variables &amp; types</h1><div class="sl-markdown-content"><p>A <strong>variable</strong> holds a value.</p>
            <h2 id="why">Why<a class="sl-anchor-link" href="#why"><span class="sr-only">Section titled « Why »</span></a></h2><ul><li>one</li><li>two <code>int</code></li></ul><script>alert(1)</script>
            <h3>Deeper</h3><pre><code><div class="ec-line">var x = 1;</div><div class="ec-line">x++;</div></code></pre></div>
            <footer><a href="/learn/csharp-beginner/03-loops/">Next</a></footer></main></body></html>
            """;
        var blocks = Extract(html);
        check(blocks.Any(b => b is { Kind: 0, Text: "A variable holds a value." }) && blocks.Any(b => b is { Kind: 2, Text: "Why" }) && blocks.Any(b => b is { Kind: 3, Text: "Deeper" })
            && blocks.Count(b => b.Kind == 1) == 2 && blocks.Any(b => b.Text == "var x = 1;") && !blocks.Any(b => b.Text.Contains("alert") || b.Text.Contains("Next") || b.Text.Contains("Section titled")),
            "the reader takes a page's paragraphs, headings, list items and code, without its scripts or its footer");
        check(TitleOf(html) == "Variables & types" && Course("F# pour développeurs C#/Java — Mission") == "F# pour développeurs C#/Java" && Course("A — b c") == "A — b c",
            "the reader titles a page by its h1, and a course by its page's title without the page's own name");
        var french = html.Replace("/learn/csharp-beginner/01", "/learn/fr/csharp-beginner/01");
        check(Lessons(french, "https://spareilleux.github.io/learn/fr/csharp-beginner/") is [{ Title: "1. Hello", Url: "https://spareilleux.github.io/learn/fr/csharp-beginner/01-hello/" }, { Url: "https://spareilleux.github.io/learn/csharp-beginner/02-variables/" }],
            "a French course keeps its lessons not yet translated, linked in English");
        var lessons = Lessons(html, "https://spareilleux.github.io/learn/csharp-beginner/");
        check(lessons is [{ Title: "1. Hello", Url: "https://spareilleux.github.io/learn/csharp-beginner/01-hello/" }, { Title: "2. Variables" }],
            "a course's lessons are the sidebar's links under its path, in order, the course page and other courses left out");

        var reader = new ReaderModule();
        reader.Initialize(host);
        check(reader.Wrap("one two three four five six seven eight nine ten", 25, false, 200) is { Count: > 1 } wrapped && wrapped.All(l => reader.Measure(l, 25, false) <= 200),
            "the reader's lines fit the page");
        var modules = Enumerable.Range(1, 60).Select(i => new ReaderItem($"MOD-{i:000} · A module", $"https://spareilleux.github.io/learn/streeling/dept/mod-{i:000}/")).ToList();
        var before = reader.Picture.Revision;
        reader.OpenDepartment("Department", modules);
        check(reader.Picture is { Valid: true } shown && shown.Revision > before && reader.Title == "Department", "a department opens on the book as a new picture");
        reader.Click((Margin + 20f) / Width, (Footer + 10f) / Height);
        var page = reader.outlinePage;
        reader.Click((Page - 100f) / Width, (Footer - 40f) / Height);
        check(reader.outlinePage == page + 1, "the left page's arrow turns the outline's page");
    }
}