using System.Text.Json;

// The shelf's conversations: this computer's Claude Code transcripts, by project, the most recently active first, each
// opening onto its last requests. A transcript can weigh hundreds of megabytes, so only its ends are read: its title,
// folder and requests from its last lines, and what they lack from its first.
internal static class Conversations
{
    private const int Edge = 256 * 1024; // the bytes read at each end of a transcript
    private const int Listed = 60; // the conversations listed in a project, the most recent
    private const int Requests = 6; // the last requests shown in a conversation

    // What a transcript's ends say: its title, the folder it ran in, and its requests, oldest first.
    internal sealed record Ends(string Title, string Cwd, List<(string Text, DateTimeOffset At)> Requests);

    public static List<Shelf.Node> Projects()
    {
        var root = Path.Combine(Shelf.ClaudeDir(), "projects");
        if (!Directory.Exists(root))
        {
            return [];
        }

        var running = Shelf.RunningSessions().Select(s => s.Id).ToHashSet();
        var projects = new List<(string Name, string Parent, string Detail, List<FileInfo> Files)>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            var files = new DirectoryInfo(dir).GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            if (files.Count == 0)
            {
                continue;
            }

            var cwd = Read(files[0].FullName).Cwd.TrimEnd('\\', '/');
            var name = Path.GetFileName(cwd);
            var live = files.Count(f => running.Contains(Path.GetFileNameWithoutExtension(f.Name)));
            var detail = $"{files.Count} conversation{(files.Count > 1 ? "s" : "")} · {When(files[0].LastWriteTime)}" +
                (live > 0 ? Words.Say($" · {live} running", $" · {live} en cours") : "");
            projects.Add((name.Length > 0 ? name : Path.GetFileName(dir), Path.GetFileName(Path.GetDirectoryName(cwd) ?? ""), detail, files));
        }

        // Two folders of the same name are told apart by the folder that holds each.
        var twice = projects.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. projects.OrderByDescending(p => p.Files[0].LastWriteTimeUtc)
            .Select(p => new Shelf.Node(twice.Contains(p.Name) && p.Parent.Length > 0 ? p.Parent + "\\" + p.Name : p.Name, p.Detail, () => List(p.Files, running)))];
    }

    private static List<Shelf.Node> List(List<FileInfo> files, HashSet<string> running)
    {
        var rows = new List<Shelf.Node>();
        foreach (var file in files.Take(Listed))
        {
            var ends = Read(file.FullName);
            var live = running.Contains(Path.GetFileNameWithoutExtension(file.Name));
            var title = ends.Title.Length > 0 ? ends.Title : Words.Say("Untitled", "Sans titre");
            rows.Add(new Shelf.Node((live ? "● " : "") + Short(title, 64), $"{When(file.LastWriteTime)} · {Size(file.Length)}", () => Details(file, ends, live)));
        }

        return rows;
    }

    // A conversation's last requests, the newest first, then where and when it ran; a running one in wmux first offers
    // its pane.
    private static List<Shelf.Node> Details(FileInfo file, Ends ends, bool live)
    {
        var id = Path.GetFileNameWithoutExtension(file.Name);
        var rows = new List<Shelf.Node>();
        if (live && Shelf.Wmux.Read().Surfaces.TryGetValue(id, out var surface))
        {
            rows.Add(new Shelf.Node(Words.Say("Go to this session", "Aller à cette session"), Words.Say("Shows its pane in wmux", "Montre son volet dans wmux"), act: () => Shelf.Wmux.Focus(surface)));
        }

        foreach (var (text, at) in Enumerable.Reverse(ends.Requests).Take(Requests))
        {
            rows.Add(new Shelf.Node(Short(text, 72), When(at.LocalDateTime)));
        }

        rows.Add(new Shelf.Node(Words.Say("Folder", "Dossier"), ends.Cwd));
        rows.Add(new Shelf.Node(Words.Say("Last activity", "Dernière activité"), When(file.LastWriteTime) + (live ? Words.Say(", running", ", en cours") : "")));
        rows.Add(new Shelf.Node(Words.Say("Size", "Taille"), Size(file.Length)));
        rows.Add(new Shelf.Node("Session", id));
        return rows;
    }

    // Reads a transcript's ends. The last title wins: one the user gave (/rename) over Claude Code's own.
    internal static Ends Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var tail = Scan(Lines(file, Math.Max(0, file.Length - Edge)));
        if (file.Length <= Edge || (tail.Title.Length > 0 && tail.Cwd.Length > 0 && tail.Requests.Count > 0))
        {
            return tail.Title.Length > 0 || tail.Requests.Count == 0 ? tail : tail with { Title = tail.Requests[0].Text };
        }

        var head = Scan(Lines(file, 0));
        var title = tail.Title.Length > 0 ? tail.Title : head.Title.Length > 0 ? head.Title : head.Requests.Concat(tail.Requests).Select(r => r.Text).FirstOrDefault() ?? "";
        return new Ends(title, tail.Cwd.Length > 0 ? tail.Cwd : head.Cwd, tail.Requests.Count > 0 ? tail.Requests : head.Requests);
    }

    // The whole lines in the Edge bytes from offset: the first is dropped unless it starts the file, and the last
    // unless it ends it.
    private static List<ReadOnlyMemory<byte>> Lines(FileStream file, long offset)
    {
        var buffer = new byte[(int)Math.Min(Edge, file.Length - offset)];
        file.Position = offset;
        file.ReadExactly(buffer);
        var lines = new List<ReadOnlyMemory<byte>>();
        var start = 0;
        if (offset > 0)
        {
            start = Array.IndexOf(buffer, (byte)'\n') + 1;
            if (start == 0)
            {
                return lines;
            }
        }

        while (start < buffer.Length)
        {
            var end = Array.IndexOf(buffer, (byte)'\n', start);
            if (end < 0)
            {
                if (offset + buffer.Length == file.Length)
                {
                    lines.Add(buffer.AsMemory(start));
                }

                break;
            }

            lines.Add(buffer.AsMemory(start, end - start));
            start = end + 1;
        }

        return lines;
    }

    private static readonly byte[] CustomTitle = "\"type\":\"custom-title\""u8.ToArray();
    private static readonly byte[] AiTitle = "\"type\":\"ai-title\""u8.ToArray();
    private static readonly byte[] User = "\"type\":\"user\""u8.ToArray();

    private static Ends Scan(List<ReadOnlyMemory<byte>> lines)
    {
        string custom = "", ai = "", cwd = "";
        var requests = new List<(string, DateTimeOffset)>();
        foreach (var line in lines)
        {
            var span = line.Span;
            var isCustom = span.IndexOf(CustomTitle) >= 0;
            var isAi = !isCustom && span.IndexOf(AiTitle) >= 0;
            var isUser = !isCustom && !isAi && span.IndexOf(User) >= 0;
            if (!isCustom && !isAi && !isUser)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (isCustom)
                {
                    custom = Text(r, "customTitle") is { Length: > 0 } t ? t : custom;
                }
                else if (isAi)
                {
                    ai = Text(r, "aiTitle") is { Length: > 0 } t ? t : ai;
                }
                else if (Text(r, "type") == "user")
                {
                    cwd = Text(r, "cwd") is { Length: > 0 } c ? c : cwd;
                    if (Request(r) is { Length: > 0 } request)
                    {
                        requests.Add((request, DateTimeOffset.TryParse(Text(r, "timestamp"), out var at) ? at : default));
                    }
                }
            }
            catch (JsonException)
            {
                // a line still being written
            }
        }

        return new Ends(custom.Length > 0 ? custom : ai, cwd, requests);
    }

    // What the user asked in a user line: typed text, or a sentence spoken to Delta, and not a tool's result, a
    // command's output, a reminder or a compaction's summary.
    private static string Request(JsonElement line)
    {
        if (Flag(line, "isCompactSummary") || Flag(line, "isVisibleInTranscriptOnly") || !line.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
        {
            return "";
        }

        var text = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? "",
            JsonValueKind.Array => string.Join(" ", content.EnumerateArray().Where(b => Text(b, "type") == "text").Select(b => Text(b, "text"))),
            _ => "",
        };
        text = text.Trim();
        if (text.StartsWith("<channel", StringComparison.Ordinal))
        {
            var open = text.IndexOf('>');
            var close = text.LastIndexOf("</channel>", StringComparison.Ordinal);
            return open > 0 && close > open ? text[(open + 1)..close].Trim() : "";
        }

        return Flag(line, "isMeta") || text.StartsWith('<') || text.StartsWith("[Request interrupted", StringComparison.Ordinal) ? "" : text;
    }

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static string Short(string text, int length)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= length ? flat : flat[..(length - 1)].TrimEnd() + "…";
    }

    private static string When(DateTime local) => Words.Date(local);

    // Part of --selftest: a small transcript, and a large one whose title is only at its start.
    internal static void SelfTest(Action<bool, string> check)
    {
        var dir = Directory.CreateTempSubdirectory("delta-conversations-");
        try
        {
            var small = Path.Combine(dir.FullName, "small.jsonl");
            File.WriteAllLines(small,
            [
                """{"type":"user","cwd":"C:\\work\\demo","timestamp":"2026-10-22T10:00:00Z","message":{"role":"user","content":"Corrige le test qui échoue"}}""",
                """{"type":"ai-title","aiTitle":"Fix the failing test"}""",
                """{"type":"user","isMeta":true,"timestamp":"2026-10-22T10:05:00Z","message":{"content":"<channel source=\"delta\" utterance_id=\"3\">\nDelta, quelle heure est-il ?\n</channel>"}}""",
                """{"type":"user","message":{"content":[{"type":"tool_result","content":"ok"}]}}""",
                """{"type":"user","isMeta":true,"message":{"content":"<system-reminder>x</system-reminder>"}}""",
                """{"type":"custom-title","customTitle":"demo-live"}""",
                """{"type":"user","isCompactSummary":true,"message":{"content":"This session is being continued"}}""",
                """{"type":"user","message":{"content":"[Request interrupted by user]"}}""",
            ]);
            var ends = Read(small);
            check(ends.Title == "demo-live" && ends.Cwd == @"C:\work\demo" && ends.Requests.Select(r => r.Text).SequenceEqual(["Corrige le test qui échoue", "Delta, quelle heure est-il ?"]),
                "a conversation's title, folder and requests, without tools' results, reminders or summaries");

            var large = Path.Combine(dir.FullName, "large.jsonl");
            using (var w = new StreamWriter(large))
            {
                w.WriteLine("""{"type":"ai-title","aiTitle":"Long work"}""");
                w.WriteLine("""{"type":"user","cwd":"C:\\old","message":{"content":"Commence"}}""");
                var filler = """{"type":"assistant","message":{"content":"<padding>"}}""".Replace("<padding>", new string('x', 1000), StringComparison.Ordinal);
                for (var i = 0; i < 600; i++)
                {
                    w.WriteLine(filler);
                }

                w.WriteLine("""{"type":"user","cwd":"C:\\new","message":{"content":"Termine"}}""");
            }

            ends = Read(large);
            check(ends.Title == "Long work" && ends.Cwd == @"C:\new" && ends.Requests.Select(r => r.Text).SequenceEqual(["Termine"]),
                "a large conversation's title from its start, its folder and requests from its end");
            check(Short("une  phrase\n bien trop longue", 12) == "une phrase…", "a long label is shortened");
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private static string Size(long bytes) => bytes < 1 << 20
        ? $"{Math.Max(1, bytes >> 10)} {Words.Say("KB", "Ko")}"
        : $"{Words.Number(bytes / 1048576.0)} {Words.Say("MB", "Mo")}";
}
