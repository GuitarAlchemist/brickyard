using Delta.SceneModules;

// What each session is doing, for the agents' office, from what was read of it: waiting while wmux says it is blocked,
// working while it works, done for a few seconds after it stops working, idle otherwise. A session outside wmux falls
// back on its own file's busy/idle status; one that says nothing either way is unknown, and one whose file outlives its
// process is stale. A session seen in this run and gone since stays, stopped, the latest StoppedKept of them. Kept apart
// from the reading (OfficeFeed) so that it can be tested alone.
internal static class OfficeActivities
{
    internal const double DoneFor = 6.0; // seconds a finished session shows as done
    internal const int StoppedKept = 8; // stopped sessions kept, the latest to stop

    // A session's file in Claude Code's sessions folder: its ID, name, start, own status, and whether its process runs.
    internal sealed record SessionFile(string Id, string Name, long Started, string Status, bool Alive);

    // One session as read: its ID, name, start, its own status, wmux's state and surface ("" when outside wmux), whether
    // its process runs, whether it has a file, and the workspace of its pane ("" outside wmux).
    internal sealed record Seen(string Id, string Name, long Started, string Status, string State, string Surface, bool Alive = true,
        bool Filed = true, string WorkspaceId = "", string WorkspaceTitle = "");

    // The sessions to show, from their files and what wmux says of its panes (session to surface, state and provider)
    // and of its workspaces (surface to ID and title). A pane is not a session: only a Claude session wmux names by its
    // ID counts, and one without a file is kept under its ID; a pane with no session, or another provider's, is not.
    internal static List<Seen> Merge(IReadOnlyList<SessionFile> files, IReadOnlyDictionary<string, (string Surface, string State, string Provider)> states,
        IReadOnlyDictionary<string, (string Id, string Title)> workspaces)
    {
        Seen Placed(Seen s) => workspaces.TryGetValue(s.Surface, out var w) ? s with { WorkspaceId = w.Id, WorkspaceTitle = w.Title } : s;
        var seen = new List<Seen>(files.Count);
        foreach (var f in files)
        {
            var (surface, state, _) = states.TryGetValue(f.Id, out var pane) ? pane : ("", "", "");
            seen.Add(Placed(new(f.Id, f.Name, f.Started, f.Status, state, surface, f.Alive)));
        }

        foreach (var (id, (surface, _, provider)) in states)
        {
            if (id.Length > 0 && provider == "claude" && files.All(f => f.Id != id))
            {
                seen.Add(Placed(new(id, id.Length > 8 ? id[..8] : id, 0, "", "", surface, Filed: false)));
            }
        }

        return seen;
    }

    // The activities, keeping in last each session as last shown; ordered by start, so that desks keep their places. Two
    // live processes can carry one session ID (a resumed session): the one started last stands for it, one desk per ID.
    internal static List<OfficeSession> Derive(IReadOnlyList<Seen> seen, Dictionary<string, OfficeSession> last, double now)
    {
        var sessions = new List<OfficeSession>(seen.Count);
        var latest = seen.GroupBy(s => s.Id, StringComparer.Ordinal).Select(g => g.MaxBy(s => s.Started)!);
        foreach (var s in latest)
        {
            var had = last.TryGetValue(s.Id, out var before);
            if (!s.Filed && had && before!.Activity != OfficeActivity.Unknown)
            {
                continue; // its file is gone and wmux still lists its pane: stopped, below
            }

            var working = s.State.Length > 0 ? s.State == "working" : s.Status == "busy";
            var activity = !s.Alive ? OfficeActivity.Stale
                : s.State.Length == 0 && s.Status.Length == 0 ? OfficeActivity.Unknown
                : s.State == "blocked" ? OfficeActivity.Waiting
                : working ? OfficeActivity.Working : OfficeActivity.Idle;
            if (activity == OfficeActivity.Idle && had &&
                (before!.Activity == OfficeActivity.Working || (before.Activity == OfficeActivity.Done && now - before.Since < DoneFor)))
            {
                activity = OfficeActivity.Done;
            }

            var since = had && before!.Activity == activity ? before.Since : now;
            var session = new OfficeSession(s.Id, s.Name, activity, since, s.WorkspaceId, s.WorkspaceTitle, s.Started);
            last[s.Id] = session;
            sessions.Add(session);
        }

        // Those shown before and not now stay, stopped, with their name, workspace and start; the oldest stopped go.
        foreach (var gone in last.Values.Where(k => sessions.All(s => s.Id != k.Id)).ToList())
        {
            last[gone.Id] = gone.Activity == OfficeActivity.Stopped ? gone : gone with { Activity = OfficeActivity.Stopped, Since = now };
        }

        var stopped = last.Values.Where(k => k.Activity == OfficeActivity.Stopped).OrderByDescending(k => k.Since).ThenByDescending(k => k.Started).ToList();
        foreach (var old in stopped.Skip(StoppedKept))
        {
            last.Remove(old.Id);
        }

        sessions.AddRange(stopped.Take(StoppedKept));
        return [.. sessions.OrderBy(s => s.Started).ThenBy(s => s.Id, StringComparer.Ordinal)];
    }
}
