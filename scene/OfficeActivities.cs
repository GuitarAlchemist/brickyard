using Delta.SceneModules;

// What each session is doing, for the agents' office, from what was read of it: waiting while wmux says it is blocked,
// working while it works, done for a few seconds after it stops working, idle otherwise. A session outside wmux falls
// back on its own file's busy/idle status. Kept apart from the reading (OfficeFeed) so that it can be tested alone.
internal static class OfficeActivities
{
    internal const double DoneFor = 6.0; // seconds a finished session shows as done

    // One session as read: its ID, name, start, its own status and wmux's state and surface ("" when outside wmux).
    internal sealed record Seen(string Id, string Name, long Started, string Status, string State, string Surface);

    // The activities, keeping in last when each began; ordered by start, so that desks keep their places. Two live
    // processes can carry one session ID (a resumed session): the one started last stands for it, one desk per ID.
    internal static List<OfficeSession> Derive(IReadOnlyList<Seen> seen, Dictionary<string, (OfficeActivity Activity, double Since)> last, double now)
    {
        var sessions = new List<OfficeSession>(seen.Count);
        var latest = seen.GroupBy(s => s.Id, StringComparer.Ordinal).Select(g => g.MaxBy(s => s.Started)!);
        foreach (var s in latest.OrderBy(s => s.Started).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            var working = s.State.Length > 0 ? s.State == "working" : s.Status == "busy";
            var activity = s.State == "blocked" ? OfficeActivity.Waiting : working ? OfficeActivity.Working : OfficeActivity.Idle;
            var had = last.TryGetValue(s.Id, out var before);
            if (activity == OfficeActivity.Idle && had &&
                (before.Activity == OfficeActivity.Working || (before.Activity == OfficeActivity.Done && now - before.Since < DoneFor)))
            {
                activity = OfficeActivity.Done;
            }

            var since = had && before.Activity == activity ? before.Since : now;
            last[s.Id] = (activity, since);
            sessions.Add(new(s.Id, s.Name, activity, since));
        }

        foreach (var gone in last.Keys.Where(k => seen.All(s => s.Id != k)).ToList())
        {
            last.Remove(gone);
        }

        return sessions;
    }
}
