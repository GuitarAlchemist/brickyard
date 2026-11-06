// The panes' workspaces as the agents' office last knew them (surface to workspace ID and title). A reading of wmux that
// failed (no answer in time, an unreadable one, a workspace whose panes could not be listed) is not an answer: the last
// good map stays, older by the time since it was read, so that the robots do not leave their enclosures for a hiccup of
// wmux. A reading that succeeds replaces it as it is, empty or not. Kept apart from the reading (OfficeFeed) so that it
// can be tested alone.
internal sealed class WorkspaceMap
{
    private static readonly IReadOnlyDictionary<string, (string Id, string Title)> None = new Dictionary<string, (string, string)>();

    public IReadOnlyDictionary<string, (string Id, string Title)> Map { get; private set; } = None;

    // When wmux last answered, null before any answer.
    public DateTimeOffset? At { get; private set; }

    // The readings that failed since the last answer.
    public int Failures { get; private set; }

    public bool Known => At is not null;

    public bool Stale => Failures > 0;

    public double? Age(DateTimeOffset now) => At is { } at ? (now - at).TotalSeconds : null;

    // Takes a reading, null when it failed; true when the map shown changed.
    public bool Apply(IReadOnlyDictionary<string, (string Id, string Title)>? answer, DateTimeOffset now)
    {
        if (answer is null)
        {
            Failures++;
            return false;
        }

        var changed = answer.Count != Map.Count || answer.Any(p => !Map.TryGetValue(p.Key, out var w) || w != p.Value);
        (Map, At, Failures) = (answer, now, 0);
        return changed;
    }

    // What the office's log says of the map at now: nothing while wmux answers.
    public string? Said(DateTimeOffset now) => !Stale ? null
        : Age(now) is { } age ? $"workspaces unread {Failures} time(s); keeping those wmux gave {age:F0} s ago"
        : $"workspaces unread {Failures} time(s); none known yet";

    internal static void SelfTest(Action<bool, string> check)
    {
        var t0 = new DateTimeOffset(2026, 11, 5, 6, 0, 0, TimeSpan.Zero);
        var map = new WorkspaceMap();
        check(!map.Apply(null, t0) && !map.Known && map.Stale && map.Map.Count == 0 && map.Age(t0) is null && map.Said(t0)!.Contains("none known yet"),
            "a first failed reading of the workspaces knows none, and says so");
        var two = new Dictionary<string, (string, string)> { ["surface-1"] = ("ws-a", "a"), ["surface-2"] = ("ws-b", "b") };
        check(map.Apply(two, t0.AddSeconds(30)) && map.Known && !map.Stale && map.Map.Count == 2 && map.Said(t0) is null, "an answer replaces the map");
        var kept = map.Map;
        var unchanged = !map.Apply(null, t0.AddSeconds(60)) & !map.Apply(null, t0.AddSeconds(90)) & !map.Apply(null, t0.AddSeconds(120));
        check(unchanged && ReferenceEquals(map.Map, kept) && map.Failures == 3 && map.Stale && map.Age(t0.AddSeconds(120)) == 90
            && map.Said(t0.AddSeconds(120)) == "workspaces unread 3 time(s); keeping those wmux gave 90 s ago",
            "failed readings in a row keep the last answer, and age it");
        check(!map.Apply(new Dictionary<string, (string, string)>(two), t0.AddSeconds(150)) && !map.Stale && map.Age(t0.AddSeconds(150)) == 0,
            "the same answer after the failures recovers without a change");
        var moved = new Dictionary<string, (string, string)> { ["surface-1"] = ("ws-a", "a"), ["surface-2"] = ("ws-a", "a") };
        check(map.Apply(moved, t0.AddSeconds(180)) && map.Map["surface-2"].Item1 == "ws-a", "a pane that really moved to another workspace changes the map");
        check(map.Apply(new Dictionary<string, (string, string)>(), t0.AddSeconds(210)) && map.Known && !map.Stale && map.Map.Count == 0,
            "an answer that succeeds with no workspace empties the map");

        // Through the office's merge: a failed reading leaves every session in its workspace; an answer moves them.
        var files = new List<OfficeActivities.SessionFile> { new("s1", "one", 1, "busy", true), new("s2", "two", 2, "idle", true) };
        var states = new Dictionary<string, (string Surface, string State, string Provider)> { ["s1"] = ("surface-1", "working", "claude"), ["s2"] = ("surface-2", "idle", "claude") };
        var office = new WorkspaceMap();
        office.Apply(two, t0);
        var before = OfficeActivities.Merge(files, states, office.Map).Select(s => s.WorkspaceId).ToList();
        office.Apply(null, t0.AddSeconds(30));
        var during = OfficeActivities.Merge(files, states, office.Map).Select(s => s.WorkspaceId).ToList();
        office.Apply(new Dictionary<string, (string, string)>(), t0.AddSeconds(60));
        var emptied = OfficeActivities.Merge(files, states, office.Map).Select(s => s.WorkspaceId).ToList();
        check(before.SequenceEqual(["ws-a", "ws-b"]) && during.SequenceEqual(before) && emptied.All(w => w.Length == 0),
            "the office keeps its sessions' workspaces through a failed reading, and drops them on an empty answer");
    }
}
