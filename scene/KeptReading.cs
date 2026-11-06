using System.Text.Json;

// What the agents' office last read of wmux, by key: each pane's workspace (surface to workspace ID and title), or each
// session's surface, state and provider. A reading of wmux that failed (no answer in time, an unreadable one, a
// workspace whose panes could not be listed) is not an answer: the last good one stays, older by the time since it was
// read, so that the robots do not leave their enclosures, nor their activities, for a hiccup of wmux. A reading that
// succeeds replaces it as it is, empty or not. Kept apart from the reading (OfficeFeed) so that it can be tested alone.
internal sealed class KeptReading<TValue>(string what)
{
    private static readonly IReadOnlyDictionary<string, TValue> None = new Dictionary<string, TValue>();

    public string What => what;

    public IReadOnlyDictionary<string, TValue> Map { get; private set; } = None;

    // When wmux last answered, null before any answer.
    public DateTimeOffset? At { get; private set; }

    // The readings that failed since the last answer.
    public int Failures { get; private set; }

    public bool Known => At is not null;

    public bool Stale => Failures > 0;

    public double? Age(DateTimeOffset now) => At is { } at ? (now - at).TotalSeconds : null;

    // Takes a reading, null when it failed; true when the map shown changed.
    public bool Apply(IReadOnlyDictionary<string, TValue>? answer, DateTimeOffset now)
    {
        if (answer is null)
        {
            Failures++;
            return false;
        }

        var changed = answer.Count != Map.Count || answer.Any(p => !Map.TryGetValue(p.Key, out var v) || !EqualityComparer<TValue>.Default.Equals(v, p.Value));
        (Map, At, Failures) = (answer, now, 0);
        return changed;
    }

    // What the office's log says of the reading at now: nothing while wmux answers.
    public string? Said(DateTimeOffset now) => !Stale ? null
        : Age(now) is { } age ? $"{what} unread {Failures} time(s); keeping those wmux gave {age:F0} s ago"
        : $"{what} unread {Failures} time(s); none known yet";
}

internal static class KeptReadings
{
    internal static void SelfTest(Action<bool, string> check)
    {
        var t0 = new DateTimeOffset(2026, 11, 5, 6, 0, 0, TimeSpan.Zero);
        var map = new KeptReading<(string Id, string Title)>("workspaces");
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
        var office = new KeptReading<(string Id, string Title)>("workspaces");
        office.Apply(two, t0);
        var before = OfficeActivities.Merge(files, states, office.Map).Select(s => s.WorkspaceId).ToList();
        office.Apply(null, t0.AddSeconds(30));
        var during = OfficeActivities.Merge(files, states, office.Map).Select(s => s.WorkspaceId).ToList();
        office.Apply(new Dictionary<string, (string, string)>(), t0.AddSeconds(60));
        var emptied = OfficeActivities.Merge(files, states, office.Map).Select(s => s.WorkspaceId).ToList();
        check(before.SequenceEqual(["ws-a", "ws-b"]) && during.SequenceEqual(before) && emptied.All(w => w.Length == 0),
            "the office keeps its sessions' workspaces through a failed reading, and drops them on an empty answer");

        // The agents' states, read every 3 s: a failure keeps the panes, states and the sessions only wmux names; a
        // successful answer applies, a real change of state and an answer with no agent included.
        var agents = new KeptReading<(string Surface, string State, string Provider)>("agent states");
        var pane = new Dictionary<string, (string Surface, string State, string Provider)>(states) { ["s3"] = ("surface-2", "blocked", "claude") };
        string Seen() => string.Join(",", OfficeActivities.Merge(files, agents.Map, two).Select(s => $"{s.Id}:{s.State}:{s.Surface}:{s.WorkspaceId}"));
        check(!agents.Apply(null, t0) && !agents.Known && Seen() == "s1:::,s2:::" && agents.Said(t0) == "agent states unread 1 time(s); none known yet",
            "a first failed reading of the agents' states knows none: the sessions stand on their files alone, and it says so");
        agents.Apply(pane, t0.AddSeconds(3));
        var read = Seen();
        var held = !agents.Apply(null, t0.AddSeconds(6)) & !agents.Apply(null, t0.AddSeconds(9));
        check(read == "s1:working:surface-1:ws-a,s2:idle:surface-2:ws-b,s3::surface-2:ws-b" && held && Seen() == read && agents.Stale
            && agents.Said(t0.AddSeconds(9)) == "agent states unread 2 time(s); keeping those wmux gave 6 s ago",
            "failed readings of the agents' states keep their panes, states and the session only wmux names, and age them");
        check(!agents.Apply(new Dictionary<string, (string, string, string)>(pane), t0.AddSeconds(12)) && !agents.Stale && Seen() == read,
            "the same states after the failures recover without a change");
        check(agents.Apply(new Dictionary<string, (string, string, string)>(pane) { ["s1"] = ("surface-1", "idle", "claude") }, t0.AddSeconds(15))
            && Seen().StartsWith("s1:idle:", StringComparison.Ordinal), "a real change of a session's state applies");
        check(agents.Apply(new Dictionary<string, (string, string, string)>(), t0.AddSeconds(18)) && agents.Known && !agents.Stale && Seen() == "s1:::,s2:::",
            "an answer that succeeds with no agent leaves the sessions on their files, the one only wmux named gone");

        // wmux's answers through their real decoding: a root that is not an object, a record that is not one, a
        // workspace or a pane without an ID are failed readings, which keep the last answer; an empty array is an answer.
        var thrown = new Dictionary<string, (string, string, string)> { ["threw"] = default };
        Dictionary<string, (string Surface, string State, string Provider)>? States(string json)
        {
            try { return Shelf.Wmux.DecodeAgentStates(JsonDocument.Parse(json)); }
            catch (InvalidOperationException) { return thrown; }
        }

        Dictionary<string, (string Id, string Title)>? Workspaces(string json, Dictionary<string, string>? surfaces = null)
        {
            try { return Shelf.Wmux.DecodeWorkspaces(JsonDocument.Parse(json), id => surfaces?.TryGetValue(id, out var s) == true ? JsonDocument.Parse(s) : null); }
            catch (InvalidOperationException) { return new() { ["threw"] = default }; }
        }

        check(States("""{"states":[]}""") is { Count: 0 }, "agent-state's answer with no state is an answer: no agent");
        check(States("[]") is null && States("\"busy\"") is null && States("""{"panes":[]}""") is null && States("""{"states":{}}""") is null && States("""{"states":[1]}""") is null,
            "agent-state's answer is a failed reading unless its root is an object holding an array of objects");
        check(States("""{"states":[{"sessionId":"s1","surfaceId":"surface-1","state":"working","sessionProvider":"claude"},{"surfaceId":"surface-9","state":"idle"}]}""")
            is { Count: 1 } decoded && decoded["s1"] == ("surface-1", "working", "claude"), "a pane with no session is left out of agent-state's answer");
        var a = """{"workspaces":[{"id":"ws-a","title":"a"},{"id":"ws-b","title":"b"}]}""";
        var panes = new Dictionary<string, string> { ["ws-a"] = """{"surfaces":[]}""", ["ws-b"] = """{"surfaces":[{"id":"surface-2"}]}""" };
        check(Workspaces("""{"workspaces":[]}""") is { Count: 0 } && Workspaces(a, panes) is { Count: 1 } placed && placed["surface-2"] == ("ws-b", "b"),
            "an empty list of workspaces, or of a workspace's panes, is an answer");
        check(Workspaces("[]") is null && Workspaces("\"ws\"") is null && Workspaces("""{"workspaces":[{"title":"no ID"}]}""") is null
            && Workspaces(a, new(panes) { ["ws-a"] = """{"surfaces":[{"title":"no ID"}]}""" }) is null && Workspaces(a, new(panes) { ["ws-a"] = "[]" }) is null
            && Workspaces(a, new(panes) { ["ws-b"] = """{"surfaces":[3]}""" }) is null && Workspaces(a, new() { ["ws-b"] = panes["ws-b"] }) is null,
            "list-workspaces and list-surfaces are failed readings unless objects holding their arrays, every workspace and pane with an ID");
        var cache = new KeptReading<(string Id, string Title)>("workspaces");
        cache.Apply(Workspaces(a, panes), t0);
        cache.Apply(Workspaces("""{"workspaces":[{"title":"no ID"}]}"""), t0.AddSeconds(30));
        var survived = cache.Map.Count == 1 && cache.Failures == 1;
        cache.Apply(Workspaces("""{"workspaces":[]}"""), t0.AddSeconds(60));
        check(survived && cache.Map.Count == 0 && !cache.Stale, "a workspace without an ID keeps the last answer; a valid empty one then empties it");

        // How old the office's reading looks: from its oldest part kept through failures, never fresher than wmux's answer.
        var feedAgents = new KeptReading<(string Surface, string State, string Provider)>("agent states");
        var feedPanes = new KeptReading<(string Id, string Title)>("workspaces");
        feedAgents.Apply(new Dictionary<string, (string, string, string)>(), t0.AddSeconds(3));
        feedPanes.Apply(new Dictionary<string, (string, string)>(), t0);
        var fresh = OfficeFeed.AsOf(t0.AddSeconds(3), feedAgents, feedPanes) == t0.AddSeconds(3);
        feedAgents.Apply(null, t0.AddSeconds(33));
        var asOf = OfficeFeed.AsOf(t0.AddSeconds(33), feedAgents, feedPanes);
        var card = WallSource.Card("s1", [new("s1", "one", Delta.SceneModules.OfficeActivity.Working, 0)], (t0.AddSeconds(33) - asOf!.Value).TotalSeconds, null, t0.AddSeconds(33), (en, _) => en);
        check(fresh && asOf == t0.AddSeconds(3) && card.Any(l => l.StartsWith("reading 30 s old", StringComparison.Ordinal)) && !card.Any(l => l.Contains("· live", StringComparison.Ordinal)),
            "a reading whose agents' states are kept from 30 s ago reads 30 s old, not live, on the wall's card");
        var never = new KeptReading<(string Id, string Title)>("workspaces");
        never.Apply(null, t0);
        check(OfficeFeed.AsOf(t0.AddSeconds(33), feedAgents, never) is null, "a reading with a part wmux never gave has no age: it is not read yet");
    }
}
