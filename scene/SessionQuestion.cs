using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// What a waiting session asks, answered from the scene (a click on its robot under the « ? », the shelf showing it).
// The question is read from the session's transcript in ~/.claude/projects: the last tool call that has no result yet.
// - An AskUserQuestion with one single-choice question shows that question and its options; option n answers with
//   the key n, as typing the number does in the pane.
// - Any other tool waits for permission: it shows the tool and what it would do (its command, file, address or
//   description, words that look like secrets masked), and answers Yes with the key 1 or No with Escape. The pane's
//   « don't ask again » stays the pane's to give.
// - When the agent told wmux its own choices (report-agent --choices), they are offered instead and answered through
//   wmux answer-agent.
// Whatever cannot be answered this way (several questions, several choices at once, a Codex session, a transcript not
// found) says so, and the row that shows the pane is always there. The transcript is only read, its tail only.
internal static partial class SessionQuestion
{
    private const int Tail = 512 * 1024;
    private const int Width = 64; // characters a row of the shelf holds

    internal sealed record Choice(string Label, string Detail, string? Key, string? WmuxChoice);

    internal sealed record Asked(string Title, string Text, IReadOnlyList<Choice> Choices, string? Note);

    // The rows the shelf shows for a waiting session: the question, its choices, then the pane.
    public static List<Shelf.Node> Rows(string sessionId, string surface, Action answered)
    {
        var asked = Ask(sessionId, surface);
        var rows = new List<Shelf.Node>();
        foreach (var line in Wrap(asked.Text, Width).Take(6))
        {
            rows.Add(new Shelf.Node(line));
        }

        foreach (var choice in asked.Choices)
        {
            rows.Add(new Shelf.Node(choice.Label, choice.Detail, act: () =>
            {
                Answer(surface, choice);
                Console.WriteLine($"WIDGETS agent-office answered session {sessionId}: {choice.Label}");
                answered();
            }));
        }

        if (asked.Note is { } note)
        {
            rows.Add(new Shelf.Node(note));
        }

        rows.Add(new Shelf.Node(Words.Say("Go to this session", "Aller à cette session"), Words.Say("Shows its pane in wmux", "Montre son volet dans wmux"), act: () =>
        {
            Shelf.Wmux.Focus(surface);
            answered();
        }));
        return rows;
    }

    // What the session asks: wmux's own choices for the pane when it has them, else the transcript's pending call.
    internal static Asked Ask(string sessionId, string surface)
    {
        var reported = Shelf.Wmux.Choices(surface);
        var pending = Transcript(sessionId) is { } file ? Pending(ReadTail(file)) : null;
        var asked = pending is { } p ? Describe(p.Name, p.Input) : new Asked(Words.Say("A question", "Une question"),
            Words.Say("Its transcript was not found: answer in its pane.", "Son transcript est introuvable : réponds dans son volet."), [], null);
        if (reported is { Count: > 0 } choices)
        {
            asked = asked with { Choices = [.. choices.Select(c => new Choice(c.Label, "", null, c.Id))], Note = null };
        }

        return asked;
    }

    // The transcript of a session: ~/.claude/projects/<project>/<id>.jsonl, the project being unknown here.
    internal static string? Transcript(string sessionId)
    {
        var projects = Path.Combine(Shelf.ClaudeDir(), "projects");
        if (!Guid.TryParse(sessionId, out _) || !Directory.Exists(projects))
        {
            return null;
        }

        return Directory.EnumerateDirectories(projects).Select(d => Path.Combine(d, sessionId + ".jsonl")).FirstOrDefault(File.Exists);
    }

    private static string ReadTail(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - Tail);
        stream.Seek(start, SeekOrigin.Begin);
        var bytes = new byte[stream.Length - start];
        stream.ReadExactly(bytes);
        var text = Encoding.UTF8.GetString(bytes);
        return start > 0 && text.IndexOf('\n') is var cut and >= 0 ? text[(cut + 1)..] : text;
    }

    // The last tool call of the transcript's lines that no later line answers: its name and input; null for none.
    internal static (string Name, JsonElement Input)? Pending(string lines)
    {
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue; // a line being written
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("message", out var message)
                    || message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                (string, JsonElement)? found = null;
                foreach (var item in content.EnumerateArray())
                {
                    var type = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "tool_result" && item.TryGetProperty("tool_use_id", out var id))
                    {
                        answered.Add(id.GetString() ?? "");
                    }
                    else if (type == "tool_use" && item.TryGetProperty("id", out var use) && !answered.Contains(use.GetString() ?? "")
                        && found is null && item.TryGetProperty("name", out var name) && item.TryGetProperty("input", out var input))
                    {
                        found = (name.GetString() ?? "", input.Clone()); // the first pending one of the message: the one asked first
                    }
                }

                if (found is { } f)
                {
                    return f;
                }
            }
        }

        return null;
    }

    internal static Asked Describe(string tool, JsonElement input)
    {
        if (tool == "AskUserQuestion" && input.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array && questions.GetArrayLength() > 0)
        {
            var q = questions[0];
            var text = Mask(Str(q, "question"));
            var header = Str(q, "header");
            var title = header.Length > 0 ? header : Words.Say("A question", "Une question");
            var several = questions.GetArrayLength() > 1;
            var multi = q.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True;
            if (several || multi || !q.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
            {
                return new Asked(title, text, [], several
                    ? Words.Say($"{questions.GetArrayLength()} questions: answer them in its pane.", $"{questions.GetArrayLength()} questions : réponds dans son volet.")
                    : Words.Say("Several answers at once: answer in its pane.", "Plusieurs réponses à la fois : réponds dans son volet."));
            }

            var choices = options.EnumerateArray().Take(9).Select((o, i) => new Choice($"{i + 1}. {Clip(Mask(Str(o, "label")), Width)}", Clip(Mask(Str(o, "description")), Width), (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), null)).ToList();
            return new Asked(title, text, choices, Words.Say("Another answer: in its pane.", "Une autre réponse : dans son volet."));
        }

        var what = new[] { "command", "file_path", "notebook_path", "url", "pattern", "description", "prompt" }
            .Select(k => Str(input, k)).FirstOrDefault(s => s.Length > 0) ?? Clip(input.GetRawText(), 300);
        return new Asked(Words.Say($"Permission: {tool}", $"Permission : {tool}"), Mask(what),
        [
            new Choice(Words.Say("Yes", "Oui"), Words.Say("Let it run, this once", "Le laisser faire, cette fois"), "1", null),
            new Choice(Words.Say("No", "Non"), Words.Say("Escape: it is told no", "Échap : il lui est dit non"), "esc", null),
        ], null);
    }

    private static void Answer(string surface, Choice choice)
    {
        if (choice.WmuxChoice is { } id)
        {
            Shelf.Wmux.AnswerAgent(surface, id);
        }
        else if (choice.Key is { } key)
        {
            Shelf.Wmux.SendKey(surface, key);
        }
    }

    // Words that look like secrets, as « name=value » or a known token's shape, replaced by dots.
    internal static string Mask(string text) =>
        Tokens().Replace(Named().Replace(Bearer().Replace(text, "$1•••"), "$1$2•••"), "•••");

    [GeneratedRegex(@"(?i)(\bbearer\s+)\S+")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?i)(\b[\w.-]*(?:token|key|secret|passw(?:or)?d|pwd)[\w.-]*)(\s*[=:]\s*)(?:""[^""]*""|'[^']*'|[^\s""']+)")]
    private static partial Regex Named();

    [GeneratedRegex(@"\b(?:sk|pk|rk|ghp|gho|ghs|github_pat|xox[abprs]|AKIA|hf)[-_]?[A-Za-z0-9_\-]{12,}")]
    private static partial Regex Tokens();

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Clip(string s, int most) => s.Length <= most ? s : s[..(most - 1)] + "…";

    // Lines of at most width characters, broken at spaces where it can.
    internal static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var rest = paragraph.Trim();
            while (rest.Length > width)
            {
                var cut = rest.LastIndexOf(' ', width);
                cut = cut <= width / 2 ? width : cut;
                yield return rest[..cut].TrimEnd();
                rest = rest[cut..].TrimStart();
            }

            if (rest.Length > 0)
            {
                yield return rest;
            }
        }
    }

    internal static void SelfTest(Action<bool, string> check)
    {
        var lines = string.Join('\n',
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"a1","name":"Bash","input":{"command":"ls"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"a1","content":"ok"}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"hm"},{"type":"tool_use","id":"a2","name":"AskUserQuestion","input":{"questions":[{"question":"Which one?","header":"Pick","multiSelect":false,"options":[{"label":"Red","description":"warm"},{"label":"Blue","description":"cold"}]}]}}]}}""",
            """{"type":"user","message":{"content":"partial""");
        var pending = Pending(lines);
        check(pending is { Name: "AskUserQuestion" }, "a session's question is its transcript's last tool call without a result");
        var asked = Describe(pending!.Value.Name, pending.Value.Input);
        check(asked is { Title: "Pick", Text: "Which one?", Choices: [{ Key: "1" }, { Key: "2", Detail: "cold" }] }, "a single question's options are answered by their numbers");
        check(Pending(string.Join('\n', lines.Split('\n')[..2])) is null, "a transcript whose calls all have results asks nothing");
        using var bash = JsonDocument.Parse("""{"command":"curl -H 'Authorization: Bearer abc' API_TOKEN=s3cr3t https://x"}""");
        var permission = Describe("Bash", bash.RootElement);
        check(permission.Choices is [{ Key: "1" }, { Key: "esc" }] && !permission.Text.Contains("s3cr3t") && !permission.Text.Contains("abc"),
            $"a permission is Yes (1) or No (Escape), and what looks like a secret is masked ({permission.Text})");
        using var two = JsonDocument.Parse("""{"questions":[{"question":"A?","options":[{"label":"x"}]},{"question":"B?","options":[{"label":"y"}]}]}""");
        check(Describe("AskUserQuestion", two.RootElement) is { Choices.Count: 0, Note: not null }, "several questions are left to the pane");
        check(Wrap("one two three four", 9).SequenceEqual(["one two", "three", "four"]), "a question is wrapped at spaces");
        check(Mask("ghp_abcdefghijklmnop1234 and sk-ant-abcdefghijklmnopq") == "••• and •••", "known token shapes are masked");
    }
}
