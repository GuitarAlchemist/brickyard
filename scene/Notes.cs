using System.Reflection;
using System.Text.RegularExpressions;

// The release notes the window shows when its version is clicked (Panel.ReleaseNotes): docs/releases/<version>.md,
// embedded in the scene when it is built, read down to its « What changed » list. A development build such as
// dev-v0.6.0-42-g8fa47f3 shows the notes of the release it grew from, 0.6.0; a version the scene has no notes for, the
// latest it has.
internal static class Notes
{
    // One change: its bold lead, and the rest of its item, both without markdown.
    internal sealed record Change(string Lead, string Text);

    // The notes for version: the release they belong to and its changes, or null when the scene carries none.
    public static (string Release, Change[] Changes)? For(string version)
    {
        var releases = Releases();
        if (releases.Count == 0)
        {
            return null;
        }

        var named = Regex.Match(version, @"\d+\.\d+\.\d+").Value;
        var release = releases.ContainsKey(named) ? named : releases.Keys.MaxBy(Version.Parse)!;
        return (release, Changes(releases[release]));
    }

    // The embedded notes by release, such as "0.6.0".
    private static Dictionary<string, string> Releases()
    {
        var scene = Assembly.GetExecutingAssembly();
        var releases = new Dictionary<string, string>();
        foreach (var name in scene.GetManifestResourceNames())
        {
            if (name.StartsWith("releases/", StringComparison.Ordinal) && name.EndsWith(".md", StringComparison.Ordinal) && Version.TryParse(name["releases/".Length..^3], out _))
            {
                using var reader = new StreamReader(scene.GetManifestResourceStream(name)!);
                releases[name["releases/".Length..^3]] = reader.ReadToEnd();
            }
        }

        return releases;
    }

    // The items of the « What changed » list, each a line "- " and its lines indented under it, until the next heading
    // or paragraph.
    internal static Change[] Changes(string markdown)
    {
        var items = new List<string>();
        var within = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("## ", StringComparison.Ordinal))
            {
                if (within)
                {
                    break;
                }

                within = raw.Trim() == "## What changed";
            }
            else if (within && raw.StartsWith("- ", StringComparison.Ordinal))
            {
                items.Add(raw[2..].Trim());
            }
            else if (within && items.Count > 0 && raw.StartsWith("  ", StringComparison.Ordinal))
            {
                items[^1] += " " + raw.Trim();
            }
            else if (within && items.Count > 0 && raw.Trim().Length > 0)
            {
                break;
            }
        }

        return [.. items.Select(item =>
        {
            var lead = Regex.Match(item, @"^\*\*(.+?)\*\*\s*");
            return lead.Success ? new Change(Plain(lead.Groups[1].Value), Plain(item[lead.Length..])) : new Change("", Plain(item));
        })];
    }

    // Markdown as words: links as their text, without emphasis or code marks.
    private static string Plain(string text) => Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1").Replace("**", "").Replace("`", "").Trim();

    // Part of --selftest: the notes this scene carries, and a list read from markdown.
    internal static void SelfTest(Action<bool, string> check)
    {
        check(For("dev-v0.6.0-42-g8fa47f3") is { Release: "0.6.0", Changes: [{ Lead: "A conversation lasts while it goes on, and shows it." }, ..] changes } &&
            changes.Length == 5 && changes.All(c => !c.Text.Contains('`') && !c.Text.Contains("**")), "a development build shows the notes of its release");
        check(For("v0.5.0") is { Release: "0.5.0" } && For("dev-v99.0.0-1-gabcdef0") is { Release: var latest } && Version.Parse(latest) >= new Version(0, 6, 0),
            "a version without notes shows the latest");
        check(Changes("# x\n\n## What changed\n\n- **Lead.** Its [text](https://example.com)\n  goes `on`.\n- No lead.\n\nAfter.\n- not an item\n") is
            [{ Lead: "Lead.", Text: "Its text goes on." }, { Lead: "", Text: "No lead." }], "a list of changes reads as words");
    }
}
