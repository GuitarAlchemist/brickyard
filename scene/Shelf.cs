using System.Diagnostics;
using System.Text.Json;

// Delta's shelf: a menu of what runs in Claude Code on this computer, one level at a time — the sessions, by wmux
// workspace when wmux runs, the installed plugins and the marketplaces, each opening onto what it holds. It is read from
// Claude Code's own files and from wmux, never written; the one action is going to a session's pane in wmux.
//
// The keys, the mouse and the brain move through it from the render loop's thread, while the slow reads (wmux answers
// in about half a second a call) run on the thread pool and land in the node they fill.
internal sealed class Shelf
{
    private const double Idle = 45.0; // seconds without a move after which the shelf closes by itself
    private readonly object gate = new();
    private readonly List<Level> path = [];
    private double touched;

    // A row: what it says, a second line in grey, and what opening it shows or does.
    internal sealed class Node(string label, string detail = "", Func<List<Node>>? children = null, Action? act = null)
    {
        public string Label { get; } = label;
        public string Detail { get; } = detail;
        public Func<List<Node>>? Children { get; } = children;
        public Action? Act { get; } = act;
    }

    // A level on screen: its title, its rows (null while they load) and the selected one.
    internal sealed class Level(string title)
    {
        public string Title { get; } = title;
        public List<Node>? Rows { get; set; }
        public int Selected { get; set; }
        public string? Error { get; set; }
    }

    public bool Open { get; private set; }

    // Changes each time what the shelf shows changes, so that its picture is drawn again.
    public int Version { get; private set; }

    // The levels from the root to the one shown; the caller reads them under no lock, on the render thread.
    public IReadOnlyList<Level> Levels
    {
        get
        {
            lock (gate)
            {
                return [.. path];
            }
        }
    }

    // Opens the shelf at its root, or at one of its sections: "sessions", "plugins" or "marketplaces".
    public void Show(double now, string section = "")
    {
        lock (gate)
        {
            path.Clear();
            var root = new Level("Delta") { Rows = Roots() };
            path.Add(root);
            var at = section switch { "sessions" => 0, "plugins" => 1, "marketplaces" => 2, _ => -1 };
            if (at >= 0)
            {
                root.Selected = at;
                Descend(root.Rows![at]);
            }

            Open = true;
            Touch(now);
        }
    }

    public void Hide()
    {
        lock (gate)
        {
            Open = false;
            Version++;
        }
    }

    // Closes the shelf when nothing moved in it for a while.
    public void Tick(double now)
    {
        if (Open && now - touched > Idle)
        {
            Hide();
        }
    }

    public void Move(double now, int by)
    {
        lock (gate)
        {
            if (!Open || path.Count == 0 || path[^1].Rows is not { Count: > 0 } rows)
            {
                return;
            }

            var level = path[^1];
            level.Selected = (level.Selected + by + rows.Count) % rows.Count;
            Touch(now);
        }
    }

    public void Select(double now, int row)
    {
        lock (gate)
        {
            if (Open && path.Count > 0 && path[^1].Rows is { } rows && row >= 0 && row < rows.Count)
            {
                path[^1].Selected = row;
                Touch(now);
            }
        }
    }

    // Opens the selected row: shows what it holds, or does what it does.
    public void Enter(double now)
    {
        lock (gate)
        {
            if (!Open || path.Count == 0 || path[^1].Rows is not { Count: > 0 } rows)
            {
                return;
            }

            var node = rows[Math.Clamp(path[^1].Selected, 0, rows.Count - 1)];
            if (node.Act is { } act)
            {
                ThreadPool.QueueUserWorkItem(_ => act());
            }
            else if (node.Children is not null)
            {
                Descend(node);
            }

            Touch(now);
        }
    }

    // Goes up a level; at the root, closes the shelf.
    public void Back(double now)
    {
        lock (gate)
        {
            if (path.Count > 1)
            {
                path.RemoveAt(path.Count - 1);
                Touch(now);
            }
            else
            {
                Open = false;
                Version++;
            }
        }
    }

    // The caller holds the gate.
    private void Descend(Node node)
    {
        var level = new Level(node.Label);
        path.Add(level);
        var load = node.Children!;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            List<Node> rows;
            string? error = null;
            try
            {
                rows = load();
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                rows = [];
                error = e.Message;
            }

            lock (gate)
            {
                level.Rows = rows;
                level.Error = error;
                Version++;
            }
        });
    }

    private void Touch(double now)
    {
        touched = now;
        Version++;
    }

    private static List<Node> Roots() =>
    [
        new("Sessions", Words.Say("This computer's Claude Code sessions", "Les sessions Claude Code de cet ordinateur"), Sessions),
        new("Plugins", Words.Say("The installed plugins", "Les plugins installés"), Plugins),
        new("Marketplaces", Words.Say("The plugin catalogs Claude Code knows", "Les catalogues de plugins connus"), Marketplaces),
    ];

    private static string ClaudeDir() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    // A running Claude Code session, from its file in ~/.claude/sessions.
    private sealed record Session(int Pid, string Id, string Name, string Cwd, string Status, string Version, long Started);

    private static List<Session> RunningSessions()
    {
        var sessions = new List<Session>();
        var dir = Path.Combine(ClaudeDir(), "sessions");
        if (!Directory.Exists(dir))
        {
            return sessions;
        }

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var pid = r.TryGetProperty("pid", out var p) && p.TryGetInt32(out var n) ? n : 0;
                if (pid == 0 || !Alive(pid))
                {
                    continue;
                }

                var cwd = Text(r, "cwd");
                var name = Text(r, "name");
                sessions.Add(new Session(pid, Text(r, "sessionId"), name.Length > 0 ? name : Path.GetFileName(cwd.TrimEnd('\\', '/')), cwd, Text(r, "status"), Text(r, "version"),
                    r.TryGetProperty("startedAt", out var s) && s.TryGetInt64(out var ms) ? ms : 0));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // a file being rewritten; it is read again next time
            }
        }

        return sessions;
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // The sessions, by wmux workspace when wmux runs, those that wait for an answer first.
    private static List<Node> Sessions()
    {
        var sessions = RunningSessions();
        var wmux = Wmux.Read();
        var outside = Words.Say("Outside wmux", "Hors de wmux");
        var groups = sessions
            .GroupBy(s => wmux.Workspaces.TryGetValue(s.Id, out var w) ? w : outside)
            .OrderBy(g => g.Key == outside ? 1 : 0).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
        var rows = new List<Node>();
        foreach (var group in groups)
        {
            var members = group.OrderBy(s => Rank(s, wmux)).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var waiting = members.Count(s => wmux.Blocked.ContainsKey(s.Id));
            var summary = $"{members.Count} session{(members.Count > 1 ? "s" : "")}" +
                (waiting > 0 ? Words.Say($", {waiting} waiting for you", $", {waiting} t'attend{(waiting > 1 ? "ent" : "")}") : "");
            rows.Add(new Node(group.Key, summary, () => members.Select(s => SessionNode(s, wmux)).ToList()));
        }

        return rows;
    }

    private static int Rank(Session s, Wmux.Snapshot wmux) => wmux.Blocked.ContainsKey(s.Id) ? 0 : s.Status == "busy" ? 1 : 2;

    private static Node SessionNode(Session s, Wmux.Snapshot wmux)
    {
        var state = wmux.Blocked.TryGetValue(s.Id, out var why)
            ? Words.Say("waiting for you" + (why.Length > 0 ? ": " + why : ""), "t'attend" + (why.Length > 0 ? " : " + why : ""))
            : s.Status == "busy" ? Words.Say("working", "travaille") : Words.Say("idle", "au repos");
        return new Node(s.Name, state, () =>
        {
            var rows = new List<Node>();
            if (wmux.Surfaces.TryGetValue(s.Id, out var surface))
            {
                rows.Add(new Node(Words.Say("Go to this session", "Aller à cette session"), Words.Say("Shows its pane in wmux", "Montre son volet dans wmux"), act: () => Wmux.Focus(surface)));
            }

            rows.Add(new Node(Words.Say("Folder", "Dossier"), s.Cwd));
            rows.Add(new Node(Words.Say("State", "État"), state));
            if (s.Started > 0)
            {
                rows.Add(new Node(Words.Say("Started", "Démarrée"), DateTimeOffset.FromUnixTimeMilliseconds(s.Started).ToLocalTime().ToString(Words.Say("MMMM d, HH:mm", "d MMMM, HH:mm"), Words.Culture)));
            }

            rows.Add(new Node("Claude Code", s.Version.Length > 0 ? "version " + s.Version : Words.Say("unknown version", "version inconnue")));
            rows.Add(new Node(Words.Say("Process", "Processus"), s.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return rows;
        });
    }

    // The installed plugins, enabled first, each opening onto its description and what it brings.
    private static List<Node> Plugins()
    {
        var dir = Path.Combine(ClaudeDir(), "plugins");
        var enabled = Enabled();
        var rows = new List<Node>();
        var installed = Path.Combine(dir, "installed_plugins.json");
        if (!File.Exists(installed))
        {
            return rows;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(installed));
        if (!doc.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Object)
        {
            return rows;
        }

        foreach (var plugin in plugins.EnumerateObject())
        {
            var key = plugin.Name; // name@marketplace
            var at = key.IndexOf('@');
            var name = at > 0 ? key[..at] : key;
            var market = at > 0 ? key[(at + 1)..] : "";
            var entry = plugin.Value.ValueKind == JsonValueKind.Array && plugin.Value.GetArrayLength() > 0 ? plugin.Value[0] : default;
            var version = entry.ValueKind == JsonValueKind.Object ? Text(entry, "version") : "";
            var installPath = entry.ValueKind == JsonValueKind.Object ? Text(entry, "installPath") : "";
            var on = enabled.TryGetValue(key, out var e) && e;
            rows.Add(new Node((on ? "● " : "○ ") + name, $"{market}{(version.Length > 0 ? " · " + version : "")} · {(on ? Words.Say("enabled", "activé") : Words.Say("disabled", "désactivé"))}", () => Contents(installPath, on)));
        }

        return [.. rows.OrderBy(r => r.Label.StartsWith('○')).ThenBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static Dictionary<string, bool> Enabled()
    {
        var on = new Dictionary<string, bool>();
        var settings = Path.Combine(ClaudeDir(), "settings.json");
        if (!File.Exists(settings))
        {
            return on;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(settings));
        if (doc.RootElement.TryGetProperty("enabledPlugins", out var plugins) && plugins.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in plugins.EnumerateObject())
            {
                on[p.Name] = p.Value.ValueKind == JsonValueKind.True;
            }
        }

        return on;
    }

    // A plugin's description, then its skills, commands, agents and hooks.
    private static List<Node> Contents(string installPath, bool on)
    {
        var rows = new List<Node>();
        var manifest = Path.Combine(installPath, ".claude-plugin", "plugin.json");
        if (File.Exists(manifest))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            rows.Add(new Node(on ? Words.Say("Enabled", "Activé") : Words.Say("Disabled", "Désactivé"), Text(doc.RootElement, "description")));
        }

        foreach (var (folder, what) in new[] { ("skills", "Skills"), ("commands", Words.Say("Commands", "Commandes")), ("agents", "Agents") })
        {
            var at = Path.Combine(installPath, folder);
            if (!Directory.Exists(at))
            {
                continue;
            }

            var names = Directory.GetDirectories(at).Select(Path.GetFileName)
                .Concat(Directory.GetFiles(at, "*.md").Select(Path.GetFileNameWithoutExtension))
                .OfType<string>().Order(StringComparer.CurrentCultureIgnoreCase).ToList();
            if (names.Count > 0)
            {
                rows.Add(new Node(what, $"{names.Count} : {string.Join(", ", names.Take(6))}{(names.Count > 6 ? "…" : "")}", () => names.Select(n => new Node(n)).ToList()));
            }
        }

        if (File.Exists(Path.Combine(installPath, "hooks", "hooks.json")))
        {
            rows.Add(new Node("Hooks", "hooks/hooks.json"));
        }

        return rows;
    }

    // The marketplaces Claude Code knows, each opening onto its plugins, the installed ones marked.
    private static List<Node> Marketplaces()
    {
        var dir = Path.Combine(ClaudeDir(), "plugins");
        var known = Path.Combine(dir, "known_marketplaces.json");
        var rows = new List<Node>();
        if (!File.Exists(known))
        {
            return rows;
        }

        var installed = new HashSet<string>();
        var list = Path.Combine(dir, "installed_plugins.json");
        if (File.Exists(list))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(list));
            if (doc.RootElement.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Object)
            {
                installed.UnionWith(plugins.EnumerateObject().Select(p => p.Name));
            }
        }

        using (var doc = JsonDocument.Parse(File.ReadAllText(known)))
        {
            foreach (var market in doc.RootElement.EnumerateObject())
            {
                var name = market.Name;
                var source = market.Value.TryGetProperty("source", out var s) ? (Text(s, "repo") is { Length: > 0 } repo ? repo : Text(s, "url")) : "";
                var location = Text(market.Value, "installLocation");
                rows.Add(new Node(name, source, () => Catalog(location, name, installed)));
            }
        }

        return [.. rows.OrderBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static List<Node> Catalog(string location, string market, HashSet<string> installed)
    {
        var file = Path.Combine(location, ".claude-plugin", "marketplace.json");
        if (!File.Exists(file))
        {
            return [new Node(Words.Say("No catalog", "Catalogue absent"), file)];
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. plugins.EnumerateArray()
            .Select(p => (Name: Text(p, "name"), Description: Text(p, "description")))
            .Where(p => p.Name.Length > 0)
            .OrderBy(p => installed.Contains(p.Name + "@" + market) ? 0 : 1).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new Node((installed.Contains(p.Name + "@" + market) ? "● " : "○ ") + p.Name, p.Description))];
    }

    private static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // What wmux says of the sessions in its panes: each one's workspace and surface, and which wait for the user.
    internal static class Wmux
    {
        internal sealed record Snapshot(Dictionary<string, string> Workspaces, Dictionary<string, string> Surfaces, Dictionary<string, string> Blocked);

        private static readonly string Exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "wmux", "resources", "cli-bin", "wmux.cmd");

        public static Snapshot Read()
        {
            var empty = new Snapshot([], [], []);
            if (!File.Exists(Exe))
            {
                return empty;
            }

            using var states = Run("agent-state");
            if (states is null || !states.RootElement.TryGetProperty("states", out var list))
            {
                return empty;
            }

            var surfaceOf = new Dictionary<string, string>(); // session to surface
            var blocked = new Dictionary<string, string>();
            foreach (var s in list.EnumerateArray())
            {
                var id = Text(s, "sessionId");
                if (id.Length == 0)
                {
                    continue;
                }

                surfaceOf[id] = Text(s, "surfaceId");
                if (Text(s, "state") == "blocked")
                {
                    blocked[id] = Text(s, "blockedReason");
                }
            }

            var workspaceOfSurface = new Dictionary<string, string>();
            using (var workspaces = Run("list-workspaces"))
            {
                if (workspaces is not null && workspaces.RootElement.TryGetProperty("workspaces", out var ws))
                {
                    foreach (var w in ws.EnumerateArray())
                    {
                        var title = Text(w, "title");
                        using var surfaces = Run("list-surfaces", "--workspace", Text(w, "id"));
                        if (surfaces is not null && surfaces.RootElement.TryGetProperty("surfaces", out var ss))
                        {
                            foreach (var surface in ss.EnumerateArray())
                            {
                                workspaceOfSurface[Text(surface, "id")] = title;
                            }
                        }
                    }
                }
            }

            var workspaceOf = new Dictionary<string, string>();
            foreach (var (session, surface) in surfaceOf)
            {
                if (workspaceOfSurface.TryGetValue(surface, out var title))
                {
                    workspaceOf[session] = title;
                }
            }

            return new Snapshot(workspaceOf, surfaceOf, blocked);
        }

        // Shows a surface's pane: its workspace first, then the surface.
        public static void Focus(string surface)
        {
            Run("focus-surface", surface)?.Dispose();
        }

        private static JsonDocument? Run(params string[] args)
        {
            var start = new ProcessStartInfo(Exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
            foreach (var a in args)
            {
                start.ArgumentList.Add(a);
            }

            try
            {
                using var process = Process.Start(start);
                if (process is null)
                {
                    return null;
                }

                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(5000))
                {
                    process.Kill();
                    return null;
                }

                return JsonDocument.Parse(output.Result);
            }
            catch (Exception e) when (e is JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return null;
            }
        }
    }
}
