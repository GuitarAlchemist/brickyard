using System.Diagnostics;
using System.Text.Json;
using SN = System.Numerics;

// The hall's gallery on its left wall: the learn site's courses face out on a bookcase, its demos on a wall of screens,
// its artifacts in frames; on the back wall Streeling University's card catalogue, a drawer a department; and a lectern
// whose open book reads a course or a department in the hall (LearnReader). A click on a course's book or on a drawer
// opens it on the lectern; one on a demo or an artifact opens its page in the browser. Lego describes the furniture and where each
// title goes (gallery/Blocks.fs, generated then validated); this draws it, letters the titles, colours the covers by
// their group and answers clicks. What is shown comes from gallery.json: %LOCALAPPDATA%\delta\gallery.json when there
// is one, else the copy beside the scene (gallery/gallery.json, the site's on 2026-11-16). Saving the file redraws the
// gallery within a second, as Lit redraws a component whose properties change: the file read, Lego asked again, the
// meshes made apart from the frame and swapped in, the old ones freed once no frame in flight can use them.
internal sealed class HallGallery : IDisposable
{
    internal sealed record Entry(string Title, string Url, string Group);

    internal sealed record Content(Entry[] Courses, Entry[] Demos, Entry[] Artifacts, Entry[] Streeling, string From)
    {
        // Streeling's departments, in the catalogue's order, each with its modules (an entry's group is its department).
        public List<(string Name, Entry[] Modules)> Departments { get; } =
            [.. Streeling.GroupBy(e => e.Group).Select(g => (g.Key, g.ToArray()))];
    }

    // What one gallery draws, made apart from the frame: the furniture in its materials, the glowing parts by their glow
    // (Push.Glow), the titles in English and in French (only the headings differ), and the slots a click opens.
    internal sealed record Built(Vertex[] Furniture, (SN.Vector4 Glow, Vertex[] Vertices)[] Glowing, Vertex[] English, Vertex[] French,
        (string Click, SN.Vector3 Min, SN.Vector3 Max)[] Hits, int Boxes, string From, Content Content, SN.Matrix4x4 Book);

    // The lectern's book, in its own frame: the open pages' rectangle (left, top, right, bottom) at z 0, as the
    // reader's picture is drawn (ScreenGpuRenderer), and how the desk slopes toward the reader.
    internal static readonly SN.Vector4 BookRect = new(-0.36f, 0.234f, 0.36f, -0.234f);
    private const float Slope = -MathF.PI / 6f;

    // The most a gallery.json may hold: its size, and what Lego's blocks take (gallery/Blocks.fs, galerie's bounds).
    internal const int MaxBytes = 512 * 1024;
    internal const int MaxCourses = 120, MaxDemos = 60, MaxArtifacts = 60, MaxStreeling = 1000;

    // The picture lights' warm wash on the wall, which the Low shader adds (Stage.LowFragmentShader): along the wall at
    // this x and height, from the back of the gallery to its front.
    internal const string Wash = "vec3(-5.5, 2.45, clamp(vWorld.z, -3.3, 5.9))";
    internal const string BackWash = "vec3(clamp(vWorld.x, -5.7, -3.8), 2.4, -2.85)";

    // What a walker cannot step into: the bookcase and the demos' counter stand on the floor along the wall
    // (FreeCamera.Obstacles), x from the wall to 0.38 m out and z from the back to the counter's end.
    internal static readonly (SN.Vector2 Centre, SN.Vector2 Half) Footprint = (new(-6.06f, 0.29f), new(0.19f, 3.64f));

    // The card catalogue against the back wall, from 0.42 m off the left wall to x -3.69, and the lectern with its desk.
    internal static readonly (SN.Vector2 Centre, SN.Vector2 Half) CatalogueFootprint = (new(-4.76f, -3.22f), new(1.08f, 0.24f));
    internal static readonly (SN.Vector2 Centre, SN.Vector2 Half) LecternFootprint = (new(-4.75f, -1.0f), new(0.22f, 0.41f));

    public static string UserPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", "gallery.json");

    public static string ShippedPath => Path.Combine(AppContext.BaseDirectory, "gallery.json");

    private readonly Func<Vertex[], Mesh> upload;
    private readonly string? chosen; // --gallery FILE, else the user's file over the shipped one
    private readonly FileSystemWatcher? watcher;
    private readonly List<(Mesh Mesh, int Frames)> graveyard = [];
    private readonly int keep;
    private long pendingSince; // Environment.TickCount64 of the file's last change not yet read; 0 for none
    private Task<Built>? rebuilding;
    private Shown? shown;

    private sealed record Shown(Mesh Furniture, (SN.Vector4 Glow, Mesh Mesh)[] Glowing, Mesh English, Mesh French,
        (string Click, SN.Vector3 Min, SN.Vector3 Max)[] Hits, (SN.Vector3 Min, SN.Vector3 Max) Box, Content Content, SN.Matrix4x4 Book);

    // What the gallery shows, and where the lectern's book lies (its frame, BookRect in it); none before the first build.
    public Content? Shelved => shown?.Content;

    public SN.Matrix4x4? Book => shown?.Book;

    public HallGallery(Func<Vertex[], Mesh> upload, int framesInFlight, string? path)
    {
        this.upload = upload;
        keep = framesInFlight + 1;
        chosen = path;
        try
        {
            Swap(Build(Read()));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"SCENE gallery: not shown ({e.Message})");
        }

        var watched = path ?? UserPath;
        if (Path.GetDirectoryName(Path.GetFullPath(watched)) is { } dir && Directory.Exists(dir))
        {
            watcher = new FileSystemWatcher(dir, Path.GetFileName(watched)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler changed = (_, _) => Interlocked.Exchange(ref pendingSince, Math.Max(1, Environment.TickCount64));
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, _) => Interlocked.Exchange(ref pendingSince, Math.Max(1, Environment.TickCount64));
            watcher.EnableRaisingEvents = true;
        }
    }

    public string Summary { get; private set; } = "";

    // The content to show: --gallery's file, else the user's when it reads, else the shipped one.
    private Content Read()
    {
        if (chosen is not null)
        {
            return Load(chosen);
        }

        if (File.Exists(UserPath))
        {
            try
            {
                return Load(UserPath);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"SCENE gallery: {UserPath} not used ({e.Message}); the shipped gallery instead");
            }
        }

        return Load(ShippedPath);
    }

    // Once a frame: frees the meshes no frame can still use, starts a rebuild once the file has been quiet 0.4 s, and
    // swaps in a finished one.
    public void Frame()
    {
        for (var i = graveyard.Count - 1; i >= 0; i--)
        {
            if (graveyard[i].Frames <= 0)
            {
                graveyard[i].Mesh.Dispose();
                graveyard.RemoveAt(i);
            }
            else
            {
                graveyard[i] = (graveyard[i].Mesh, graveyard[i].Frames - 1);
            }
        }

        var since = Interlocked.Read(ref pendingSince);
        if (rebuilding is null && since != 0 && Environment.TickCount64 - since > 400)
        {
            Interlocked.CompareExchange(ref pendingSince, 0, since);
            rebuilding = Task.Run(() => Build(Read()));
        }

        if (rebuilding is { IsCompleted: true } done)
        {
            rebuilding = null;
            if (done.IsCompletedSuccessfully)
            {
                var clock = Stopwatch.StartNew();
                Swap(done.Result);
                Console.WriteLine($"SCENE gallery: redrawn from {done.Result.From}, swapped in {clock.Elapsed.TotalMilliseconds:F0} ms; {Summary}");
            }
            else
            {
                Console.WriteLine($"SCENE gallery: kept as it was ({done.Exception?.GetBaseException().Message})");
            }
        }
    }

    private void Swap(Built built)
    {
        var meshes = (Furniture: upload(built.Furniture), Glowing: built.Glowing.Select(g => (g.Glow, upload(g.Vertices))).ToArray(),
            English: upload(built.English), French: upload(built.French));
        if (shown is { } old)
        {
            foreach (var mesh in new[] { old.Furniture, old.English, old.French }.Concat(old.Glowing.Select(g => g.Mesh)))
            {
                graveyard.Add((mesh, keep));
            }
        }

        shown = new Shown(meshes.Furniture, meshes.Glowing, meshes.English, meshes.French, built.Hits,
            KnotFloor.Box([built.Furniture, built.English, built.French, .. built.Glowing.Select(g => g.Vertices)]), built.Content, built.Book);
        Summary = $"{built.Hits.Length} slots, {built.Boxes} Lego boxes, {built.Furniture.Length + built.English.Length + built.Glowing.Sum(g => g.Vertices.Length)} vertices";
    }

    // The gallery's meshes, when the picture shows some of it, through draw (the hall's untraced drawing).
    public void Draw(Action<Mesh, SN.Vector4> draw, SN.Matrix4x4 viewProj, bool french)
    {
        if (shown is not { } s || !KnotFloor.Seen(viewProj, s.Box))
        {
            return;
        }

        draw(s.Furniture, default);
        draw(french ? s.French : s.English, default);
        foreach (var (glow, mesh) in s.Glowing)
        {
            draw(mesh, glow);
        }
    }

    // What the slot a click at x, y falls on does: "course:N" (the Nth course, read on the lectern), "department:N"
    // (Streeling's Nth department, likewise) or "open:URL" (a page for the browser); null when it falls on none.
    public string? Pick(SN.Matrix4x4 viewProj, int width, int height, double x, double y) =>
        shown is { } s ? WidgetPicking.Pick(s.Hits, viewProj, width, height, x, y) : null;

    // Opens url in the browser.
    public static void Browse(string url)
    {
        Console.WriteLine($"GALLERY opens {url}");
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"scene: cannot open {url}: {e.Message}");
        }
    }

    // Where a click at x, y in the window falls on a rectangle (left, top, right, bottom) lying at z 0 in frame: u and v
    // from its top left, 0 to 1 each, or null when it misses it or lies behind the eye.
    internal static (float U, float V)? OnRect(SN.Matrix4x4 viewProj, SN.Matrix4x4 frame, SN.Vector4 rect, int width, int height, double x, double y)
    {
        if (width <= 0 || height <= 0 || !SN.Matrix4x4.Invert(viewProj, out var unproject) || !SN.Matrix4x4.Invert(frame, out var into))
        {
            return null;
        }

        var (nx, ny) = ((float)((2 * x / width) - 1), (float)((2 * y / height) - 1));
        SN.Vector3 At(float z)
        {
            var p = SN.Vector4.Transform(new SN.Vector4(nx, ny, z, 1f), unproject);
            return SN.Vector3.Transform(new SN.Vector3(p.X, p.Y, p.Z) / p.W, into);
        }

        var (near, far) = (At(0f), At(1f));
        var dz = far.Z - near.Z;
        if (MathF.Abs(dz) < 1e-7f)
        {
            return null;
        }

        var t = -near.Z / dz;
        var q = near + ((far - near) * t);
        var (u, v) = ((q.X - rect.X) / (rect.Z - rect.X), (q.Y - rect.Y) / (rect.W - rect.Y));
        return t >= 0f && u is >= 0f and <= 1f && v is >= 0f and <= 1f ? (u, v) : null;
    }

    public void Dispose()
    {
        watcher?.Dispose();
        try
        {
            rebuilding?.Wait();
        }
        catch (AggregateException)
        {
        }

        foreach (var (mesh, _) in graveyard)
        {
            mesh.Dispose();
        }

        graveyard.Clear();
        if (shown is { } s)
        {
            s.Furniture.Dispose();
            s.English.Dispose();
            s.French.Dispose();
            foreach (var (_, mesh) in s.Glowing)
            {
                mesh.Dispose();
            }
        }

        shown = null;
    }

    // A gallery.json, read and checked: at most MaxBytes; courses, demos and artifacts each a list of at most so many
    // entries, each with a title (1 to 160 characters), an https URL and, for courses and artifacts, a group. A file
    // that breaks any of it is refused whole, with the reason.
    internal static Content Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new InvalidDataException($"no file {path}");
        }

        if (info.Length > MaxBytes)
        {
            throw new InvalidDataException($"{path} is {info.Length} bytes, more than {MaxBytes}");
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{path} is not a JSON object");
            }

            Entry[] List(string name, int max)
            {
                if (!doc.RootElement.TryGetProperty(name, out var list))
                {
                    return [];
                }

                if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > max)
                {
                    throw new InvalidDataException($"{name} must be a list of at most {max} entries");
                }

                return [.. list.EnumerateArray().Select((e, i) => Checked(e, $"{name}[{i}]"))];
            }

            return new Content(List("courses", MaxCourses), List("demos", MaxDemos), List("artifacts", MaxArtifacts), List("streeling", MaxStreeling), path);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path} is not valid JSON: {e.Message}");
        }
    }

    private static Entry Checked(JsonElement e, string where)
    {
        string? Text(string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
        var title = Text("title");
        if (title is not { Length: > 0 and <= 160 })
        {
            throw new InvalidDataException($"{where} needs a title of 1 to 160 characters");
        }

        if (Text("url") is not { Length: <= 400 } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"{where} ({title}) needs an https URL");
        }

        var group = Text("group") ?? "";
        return new Entry(title, uri.AbsoluteUri, group.Length > 80 ? group[..80] : group);
    }

    // The gallery for that content: Lego's scene (refused, it says why), its boxes as vertices in the hall's materials,
    // and on each slot's anchor its title, lettered and coloured, with the box a click on it hits.
    internal static Built Build(Content content)
    {
        var departments = content.Departments;
        var described = Gallery.Blocks.describe(content.Courses.Length, content.Demos.Length, content.Artifacts.Length, departments.Count);
        if (!described.Valid)
        {
            throw new InvalidDataException("Lego refused the gallery: " + string.Join("; ", described.Problems.Select(p => p.Message)));
        }

        var scene = described.Scene;
        var furniture = new List<Vertex>();
        var glowing = new Dictionary<SN.Vector4, List<Vertex>>();
        var local = new List<Vertex>();
        foreach (var box in scene.Boxes)
        {
            local.Clear();
            var finish = box.Surface.Code switch
            {
                1 => new Finish(box.Color, Material.Steel),
                2 => new Finish(box.Color, Material.Glass),
                4 => new Finish(box.Color, Material.Enamel),
                _ => new Finish(box.Color, Material.Matte),
            };
            Boxes.Add(local, new SN.Vector3(0f, box.Size.Y / 2f, 0f), box.Size, finish);
            var into = furniture;
            if (box.Surface.Code == 3)
            {
                var glow = new SN.Vector4(box.Color, (float)box.Surface.Strength);
                if (!glowing.TryGetValue(glow, out into))
                {
                    glowing[glow] = into = [];
                }
            }

            Place(local, box.World, into);
        }

        // Each section's node, by its name under the root.
        var nodes = scene.Draft.Root.Children.ToDictionary(n => n.Path[(n.Path.LastIndexOf('/') + 1)..]);
        var english = new List<Vertex>();
        var french = new List<Vertex>();
        var hits = new List<(string, SN.Vector3, SN.Vector3)>();
        var groups = new List<string>();
        var lists = new Dictionary<string, Entry[]>
        {
            ["cours"] = content.Courses, ["demos"] = content.Demos, ["artifacts"] = content.Artifacts,
            ["streeling"] = [.. departments.Select(d => new Entry(d.Name, "", d.Modules.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)))],
        };
        var headings = new Dictionary<string, (string En, string Fr)>
        {
            ["cours"] = ("COURSES", "COURS"), ["demos"] = ("DEMOS", "DÉMOS"), ["artifacts"] = ("ARTIFACTS", "ARTIFACTS"),
            ["streeling"] = ("STREELING UNIVERSITY", "UNIVERSITÉ STREELING"),
        };
        foreach (var section in Gallery.Blocks.sections)
        {
            var entries = lists[section.Name];
            if (entries.Length == 0 || !nodes.TryGetValue(section.Name, out var node))
            {
                continue;
            }

            var anchors = node.Anchors.ToDictionary(a => a.Item1, a => a.Item2);
            var (w, h) = ((float)section.Width, (float)section.Height);
            for (var i = 0; i < entries.Length; i++)
            {
                var world = anchors[$"{section.Anchor}-{i}"];
                local.Clear();
                Slot(local, section.Name, entries[i], w, h, groups);
                Place(local, world, english);
                Place(local, world, french);
                var (lo, hi) = (new SN.Vector3(float.MaxValue), new SN.Vector3(float.MinValue));
                for (var c = 0; c < 8; c++)
                {
                    var p = SN.Vector3.Transform(new SN.Vector3((c & 1) == 0 ? -w / 2 : w / 2, (c & 2) == 0 ? -h / 2 : h / 2, (c & 4) == 0 ? -0.01f : 0.01f), world);
                    (lo, hi) = (SN.Vector3.Min(lo, p), SN.Vector3.Max(hi, p));
                }

                hits.Add((section.Name switch { "cours" => $"course:{i}", "streeling" => $"department:{i}", _ => $"open:{entries[i].Url}" }, lo, hi));
            }

            var title = anchors["titre"];
            var ink = new SN.Vector3(0.85f, 0.68f, 0.38f);
            foreach (var (text, into) in new[] { (headings[section.Name].En, english), (headings[section.Name].Fr, french) })
            {
                local.Clear();
                Text(local, text, new SN.Vector3(0f, 0f, 0.002f), 0.07f, 1.5f, ink, 40);
                Place(local, title, into);
            }
        }

        // The lectern's sloping desk and the book's boards, under the pages the reader draws (LearnReader).
        var book = SN.Matrix4x4.Identity;
        if (nodes.TryGetValue("lutrin", out var lectern) && lectern.Anchors.FirstOrDefault(a => a.Item1 == "pupitre") is { Item2: var cap })
        {
            var desk = SN.Matrix4x4.CreateRotationX(Slope) * SN.Matrix4x4.CreateTranslation(0f, 0.24f, 0f) * cap;
            local.Clear();
            Boxes.Add(local, new SN.Vector3(0f, 0f, -0.012f), new SN.Vector3(0.8f, 0.54f, 0.024f), new Finish(new(0.11f, 0.07f, 0.04f), Material.Matte));
            Boxes.Add(local, new SN.Vector3(0f, -0.285f, 0.01f), new SN.Vector3(0.8f, 0.03f, 0.03f), new Finish(new(0.11f, 0.07f, 0.04f), Material.Matte)); // the ledge the book rests on
            Boxes.Add(local, new SN.Vector3(0f, 0f, 0.006f), new SN.Vector3(0.76f, 0.5f, 0.012f), new Finish(new(0.16f, 0.05f, 0.04f), Material.Enamel)); // the book's boards
            Boxes.Add(local, new SN.Vector3(0f, 0f, 0.015f), new SN.Vector3(0.73f, 0.475f, 0.006f), new Finish(new(0.7f, 0.66f, 0.58f), Material.Matte)); // its page block
            Place(local, desk, furniture);
            book = SN.Matrix4x4.CreateTranslation(0f, 0f, 0.0185f) * desk;
        }

        return new Built([.. furniture], [.. glowing.Select(g => (g.Key, g.Value.ToArray()))], [.. english], [.. french], [.. hits], scene.Boxes.Count, content.From, content, book);
    }

    // The colours of the courses' covers and the artifacts' groups, in the order the groups come.
    private static readonly SN.Vector3[] Covers =
    [
        new(0.30f, 0.07f, 0.06f), new(0.05f, 0.12f, 0.26f), new(0.06f, 0.2f, 0.1f), new(0.3f, 0.19f, 0.04f), new(0.17f, 0.07f, 0.25f),
        new(0.04f, 0.2f, 0.22f), new(0.27f, 0.1f, 0.17f), new(0.17f, 0.17f, 0.19f), new(0.22f, 0.12f, 0.05f),
    ];

    // A slot's face, w by h, facing +Z at z 0: a book's cover in its group's colour with its title and group, a screen's
    // title, or a frame's mat lettered in ink.
    private static void Slot(List<Vertex> o, string section, Entry entry, float w, float h, List<string> groups)
    {
        var at = groups.IndexOf(entry.Group);
        if (at < 0)
        {
            groups.Add(entry.Group);
            at = groups.Count - 1;
        }

        var colour = Covers[at % Covers.Length];
        switch (section)
        {
            case "cours":
                Boxes.Add(o, new SN.Vector3(0f, 0f, 0.0015f), new SN.Vector3(w - 0.01f, h - 0.01f, 0.003f), new Finish(colour, Material.Enamel));
                Boxes.Add(o, new SN.Vector3(0f, (h / 2f) - 0.03f, 0.0032f), new SN.Vector3(w - 0.06f, 0.003f, 0.0005f), new Finish(new(0.7f, 0.55f, 0.25f), Material.Steel));
                Lines(o, entry.Title, 16, 4, 0.03f, 0.04f, 0.02f, w - 0.04f, 0.0035f, new SN.Vector3(0.92f, 0.88f, 0.78f));
                Text(o, entry.Group, new SN.Vector3(0f, (-h / 2f) + 0.03f, 0.0035f), 0.017f, w - 0.05f, new SN.Vector3(0.72f, 0.66f, 0.55f), 28);
                break;
            case "streeling":
                // A drawer's card in its brass holder: the department, and how many modules it files (the entry's group).
                Boxes.Add(o, new SN.Vector3(0f, 0.025f, 0.001f), new SN.Vector3(0.25f, 0.115f, 0.002f), new Finish(new(0.5f, 0.37f, 0.16f), Material.Steel));
                Boxes.Add(o, new SN.Vector3(0f, 0.025f, 0.0025f), new SN.Vector3(0.235f, 0.1f, 0.001f), new Finish(new(0.78f, 0.74f, 0.64f), Material.Matte));
                Lines(o, entry.Title, 20, 2, 0.022f, 0.03f, 0.04f, 0.22f, 0.0031f, new SN.Vector3(0.1f, 0.08f, 0.06f));
                Text(o, $"{entry.Group} modules", new SN.Vector3(0f, -0.005f, 0.0031f), 0.016f, 0.2f, new SN.Vector3(0.35f, 0.2f, 0.1f), 28);
                break;
            case "demos":
                Lines(o, entry.Title, 22, 3, 0.042f, 0.054f, 0f, w - 0.06f, 0.0015f, new SN.Vector3(0.92f, 0.96f, 1f));
                break;
            default:
                Boxes.Add(o, new SN.Vector3(0f, (-h / 2f) + 0.012f, 0.0006f), new SN.Vector3(w - 0.04f, 0.006f, 0.0008f), new Finish(colour, Material.Enamel));
                Lines(o, entry.Title, 18, 4, 0.027f, 0.036f, 0.015f, w - 0.04f, 0.0012f, new SN.Vector3(0.08f, 0.07f, 0.06f));
                Text(o, entry.Group, new SN.Vector3(0f, (-h / 2f) + 0.035f, 0.0012f), 0.015f, w - 0.05f, new SN.Vector3(0.3f, 0.28f, 0.25f), 28);
                break;
        }
    }

    // A title over at most lines lines of about chars characters each, height tall a line and step apart, the block
    // centred lift above the face's middle.
    private static void Lines(List<Vertex> o, string title, int chars, int lines, float height, float step, float lift, float width, float z, SN.Vector3 colour)
    {
        var wrapped = Wrap(title, chars, lines);
        for (var i = 0; i < wrapped.Count; i++)
        {
            Text(o, wrapped[i], new SN.Vector3(0f, lift + (((wrapped.Count - 1) / 2f) - i) * step, z), height, width, colour, 32);
        }
    }

    // Words on lines of at most chars characters (a longer word alone on its own), at most lines of them, the last
    // ending in an ellipsis when the title goes on.
    internal static List<string> Wrap(string text, int chars, int lines)
    {
        var result = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > chars)
            {
                result.Add(line);
                line = word;
            }
            else
            {
                line = line.Length == 0 ? word : line + " " + word;
            }
        }

        if (line.Length > 0)
        {
            result.Add(line);
        }

        if (result.Count > lines)
        {
            result.RemoveRange(lines, result.Count - lines);
            result[^1] += "…";
        }

        return result;
    }

    // A line of text centred at c on a face looking down +Z, height tall (narrowed to fit width), as flat quads, one for
    // each run of covered pixels, runs that repeat row after row merged into one: a few thousand vertices a line.
    internal static void Text(List<Vertex> o, string text, SN.Vector3 c, float height, float width, SN.Vector3 colour, int size)
    {
        var rgba = Splash.Text(text, size, out var w, out var h, true);
        if (rgba is null || w == 0 || h == 0)
        {
            return;
        }

        var pixel = Math.Min(width, height * w / h) / w;
        var open = new Dictionary<(int Start, int End), int>(); // a run and the row it began on
        var row = new List<(int, int)>();
        void Quad((int Start, int End) run, int first, int last)
        {
            var (x0, x1) = (c.X + ((run.Start - (w / 2f)) * pixel), c.X + ((run.End - (w / 2f)) * pixel));
            var (y0, y1) = (c.Y + (((h / 2f) - last - 1) * pixel), c.Y + (((h / 2f) - first) * pixel));
            Vertex V(float x, float y) => new(new(x, y, c.Z), SN.Vector3.UnitZ, colour, Material.Matte, SN.Vector3.UnitX, new(0f, 0f, 1f, 1f));
            o.AddRange([V(x0, y0), V(x1, y0), V(x1, y1), V(x0, y0), V(x1, y1), V(x0, y1)]);
        }

        for (var y = 0; y <= h; y++)
        {
            row.Clear();
            for (var x = 0; y < h && x < w;)
            {
                if (rgba[(((y * w) + x) * 4) + 3] < 96)
                {
                    x++;
                    continue;
                }

                var start = x++;
                while (x < w && rgba[(((y * w) + x) * 4) + 3] >= 96)
                {
                    x++;
                }

                row.Add((start, x));
            }

            foreach (var run in open.Keys.Where(k => !row.Contains(k)).ToList())
            {
                Quad(run, open[run], y - 1);
                open.Remove(run);
            }

            foreach (var run in row)
            {
                open.TryAdd(run, y);
            }
        }
    }

    // Vertices made in a frame placed by world: positions moved, normals and tangents turned (Lego's frames only turn and
    // move, at scale 1).
    private static void Place(List<Vertex> from, SN.Matrix4x4 world, List<Vertex> into)
    {
        foreach (var v in from)
        {
            into.Add(new Vertex(SN.Vector3.Transform(v.Pos, world), SN.Vector3.Normalize(SN.Vector3.TransformNormal(v.Normal, world)), v.Color, v.Material,
                SN.Vector3.Normalize(SN.Vector3.TransformNormal(v.Tangent, world)), v.Edge));
        }
    }

    // Part of --selftest: the shipped gallery as Lego makes it, against the hall it stands in and the shader that lights it.
    internal static void SelfTest(Action<bool, string> check, string lowShader)
    {
        var content = Load(ShippedPath);
        check(content is { Courses.Length: 41, Demos.Length: 11, Artifacts.Length: 7, Streeling.Length: 482, Departments.Count: 22 }
            && content.Courses.Concat(content.Streeling).All(c => c.Url.StartsWith(LearnReader.Site, StringComparison.Ordinal)),
            "the shipped gallery holds the site's 41 courses, 11 demos, 7 artifacts and Streeling's 482 modules in 22 departments, each course and module on the site");
        var built = Build(content);
        var all = built.Furniture.Concat(built.English).Concat(built.French).Concat(built.Glowing.SelectMany(g => g.Vertices)).ToArray();
        check(all.All(v => float.IsFinite(v.Pos.X + v.Pos.Y + v.Pos.Z) && MathF.Abs(v.Normal.Length() - 1f) < 1e-3f), "the gallery's vertices are finite and its normals unit");
        var onLeft = built.Hits.Where(h => !h.Click.StartsWith("department:", StringComparison.Ordinal)).ToArray();
        var drawers = built.Hits.Where(h => h.Click.StartsWith("department:", StringComparison.Ordinal)).ToArray();
        check(onLeft.Length == 59 && onLeft.All(h => h.Min.X >= HallShell.Left - 0.01f && h.Max.X <= HallShell.Left + 0.5f && h.Min.Z >= HallShell.Back && h.Max.Z <= HallShell.Front && h.Max.Y < 2.6f)
            && built.Hits.Select(h => h.Click).Distinct().Count() == 81 && onLeft.Count(h => h.Click.StartsWith("course:", StringComparison.Ordinal)) == 41,
            "each course, demo and artifact is a slot on the left wall, below the clerestory, each its own; a course is read on the lectern");
        check(drawers.Length == 22 && drawers.All(h => h.Min.Z >= HallShell.Back && h.Max.Z <= HallShell.Back + 0.5f && h.Min.X > HallShell.Left + 0.4f && h.Max.X < -3.65f && h.Max.Y < 2f),
            "each Streeling department is a drawer of the catalogue on the back wall, clear of the bookcase and the pillar");
        bool Within((SN.Vector2 Centre, SN.Vector2 Half) f, SN.Vector3 p) =>
            p.X >= f.Centre.X - f.Half.X - 0.01f && p.X <= f.Centre.X + f.Half.X + 0.01f && p.Z >= f.Centre.Y - f.Half.Y - 0.01f && p.Z <= f.Centre.Y + f.Half.Y + 0.01f;
        var footprints = new[] { Footprint, CatalogueFootprint, LecternFootprint };
        check(built.Furniture.Where(v => v.Pos.Y < 1f).All(v => footprints.Any(f => Within(f, v.Pos)))
            && footprints.All(f => FreeCamera.Obstacles.Any(o => o.Centre == f.Centre && o.Half == f.Half)),
            "what stands on the floor lies within the gallery's footprints, which the walker keeps out of");
        var bookCentre = SN.Vector3.Transform(SN.Vector3.Zero, built.Book);
        var facing = SN.Vector3.Normalize(SN.Vector3.TransformNormal(SN.Vector3.UnitZ, built.Book));
        var (eye, target) = FreeCamera.Views[^1];
        check(Within(LecternFootprint, bookCentre) && bookCentre.Y is > 0.9f and < 1.25f && facing.X > 0.8f && facing.Y > 0.4f
            && SN.Vector3.Distance(target, bookCentre) < 0.05f && SN.Vector3.Dot(SN.Vector3.Normalize(eye - bookCentre), facing) > 0.85f,
            "the lectern's book lies over its foot, sloping toward the room, and the last view looks straight at it");
        var projection = SN.Matrix4x4.CreatePerspectiveFieldOfView(0.75f, 16f / 9f, 0.1f, 40f);
        var vp = SN.Matrix4x4.CreateLookAt(eye, target, SN.Vector3.UnitY) * projection;
        var corner = SN.Vector3.Transform(new SN.Vector3(BookRect.X + (0.25f * (BookRect.Z - BookRect.X)), BookRect.Y + (0.75f * (BookRect.W - BookRect.Y)), 0f), built.Book);
        var clip = SN.Vector4.Transform(new SN.Vector4(corner, 1f), vp);
        var hitAt = OnRect(vp, built.Book, BookRect, 1600, 900, (clip.X / clip.W + 1f) * 800f, (clip.Y / clip.W + 1f) * 450f);
        check(hitAt is { } uv && MathF.Abs(uv.U - 0.25f) < 0.01f && MathF.Abs(uv.V - 0.75f) < 0.01f && OnRect(vp, built.Book, BookRect, 1600, 900, 2, 2) is null,
            "a click on the book is found on its pages, where it was drawn; one beside it misses");
        check(built.Glowing.Length == 2 && built.Glowing.Any(g => g.Glow.W >= 1f) && built.Glowing.Any(g => g.Glow.W < 1f) && lowShader.Contains(Wash, StringComparison.Ordinal) && lowShader.Contains(BackWash, StringComparison.Ordinal),
            "the picture lights and the screens glow, and the Low shader washes the wall from the lights");
        check(built.English.Length > 50_000 && built.English.Length < 3_000_000 && built.French.Length != built.English.Length,
            $"the titles are lettered in a bounded number of vertices ({built.English.Length}), the headings in each language");
        check(Wrap("Spring Boot, Spring Cloud and Reactor for C# developers", 16, 4) is ["Spring Boot,", "Spring Cloud and", "Reactor for C#", "developers"]
            && Wrap("one two three four five six seven", 7, 2) is ["one two", "three…"], "titles wrap by words, and a long one ends in an ellipsis");
        check(Gallery.Blocks.describe(41, 11, 60, 22).Problems.Any(p => p.Kind == "build-failed") && Gallery.Blocks.describe(80, 11, 7, 22).Problems.Any(p => p.Kind == "build-failed")
            && Gallery.Blocks.describe(55, 16, 7, 40).Valid,
            "Lego refuses a gallery longer than the wall or taller than its sill, and takes one that fits");

        var dir = Directory.CreateTempSubdirectory("delta-gallery-");
        try
        {
            string Refused(string json)
            {
                var file = Path.Combine(dir.FullName, "gallery.json");
                File.WriteAllText(file, json);
                try
                {
                    Load(file);
                    return "";
                }
                catch (InvalidDataException e)
                {
                    return e.Message;
                }
            }

            check(Refused("""{ "demos": [ { "title": "x", "url": "http://example.com/" } ] }""").Contains("https") && Refused("""{ "demos": [ { "title": "", "url": "https://a.b/" } ] }""").Contains("title")
                && Refused("[1]").Contains("object") && Refused("{ nope").Contains("JSON") && Refused($$"""{ "demos": [{{string.Join(",", Enumerable.Repeat("""{ "title": "x", "url": "https://a.b/" }""", MaxDemos + 1))}}] }""").Contains("at most")
                && Refused("""{ "courses": [ { "title": "x", "url": "https://a.b/", "group": "g" } ] }""") == "",
                "a gallery.json without https links, titles or its shape, or longer than the blocks take, is refused whole");
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
