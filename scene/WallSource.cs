using Delta.SceneModules;

// What the big screen on the bricks shows, and the card it shows for a session. Parsec's game by default, as before;
// Pong, Delta's chest screen, after a click on it; a session, read only, after a click on its robot, until the robot is
// let go or the cabinet is clicked. A session's card says what it is (name, ID, workspace or that it has no wmux pane),
// how it stands, how fresh the reading is and its last requests, from the office's reading and the session's transcript
// (Conversations); nothing is sent to it. A card is live only when the reading is fresh and the session runs: a stale,
// stopped or unknown session, an old reading or a missing transcript says so. Kept apart from the window so that it can
// be tested alone.
internal sealed class WallSource
{
    internal const float Width = 1.76f, Height = 1.32f, Middle = 1.78f; // the screen on the bricks, 4:3, between the clocks, its top in the first view
    internal const double Fresh = 10.0; // seconds after which the office's reading is old (it reads every 3 s)

    internal enum Kind { Parsec, Pong, Session }

    // A session's transcript as read: its title and its last requests, oldest first, and when the file was last written.
    // Unfound: the bytes read from its end without a request, when its latest requests are unknown (Conversations).
    internal sealed record Transcript(string Title, IReadOnlyList<(string Text, DateTimeOffset At)> Requests, DateTimeOffset Written, long Unfound = 0);

    public Kind Showing { get; private set; } = Kind.Parsec;

    public string? Session { get; private set; }

    public void ShowParsec() => (Showing, Session) = (Kind.Parsec, null);

    public void ShowPong() => (Showing, Session) = (Kind.Pong, null);

    public void ShowSession(string id) => (Showing, Session) = (Kind.Session, id);

    // The office let go of a robot: the wall goes back to Parsec when it showed that session.
    public void Released(string? id)
    {
        if (Showing == Kind.Session && Session == id)
        {
            ShowParsec();
        }
    }

    // The card's lines for the session shown, from the office's reading of readAgo seconds ago (null when nothing was read
    // yet) and its transcript (null when none was found): who it is, how it stands and how fresh, then its last requests.
    internal static List<string> Card(string id, IReadOnlyList<OfficeSession> sessions, double? readAgo, Transcript? transcript,
        DateTimeOffset now, Func<string, string, string> say)
    {
        var s = sessions.FirstOrDefault(k => k.Id == id);
        var shortId = id.Length > 8 ? id[..8] : id;
        var where = s is null ? "" : s.WorkspaceId.Length == 0 ? say("no wmux pane", "sans volet wmux") : s.WorkspaceTitle.Length > 0 ? s.WorkspaceTitle : s.WorkspaceId;
        var fresh = readAgo is { } r && r <= Fresh;
        var standing = s is null ? say("unavailable: not in the office's reading", "indisponible : absente de la lecture du bureau")
            : s.Activity switch
            {
                OfficeActivity.Stale => say("stale: its file outlives its process", "périmée : son fichier survit à son processus"),
                OfficeActivity.Stopped => say("stopped: gone from the reading", "arrêtée : partie de la lecture"),
                OfficeActivity.Unknown => say("unknown: it says nothing of itself", "inconnue : elle ne dit rien d'elle-même"),
                var a => a.ToString().ToLowerInvariant() + (fresh ? say(" · live", " · en direct") : ""),
            };
        var reading = readAgo is not { } ago ? say("not read yet", "pas encore lue")
            : fresh ? say($"read {Seconds(ago)} s ago", $"lue il y a {Seconds(ago)} s") : say($"reading {Seconds(ago)} s old", $"lecture vieille de {Seconds(ago)} s");
        var written = transcript is null ? say("no transcript found", "aucun transcript trouvé")
            : say($"transcript written {Age(now - transcript.Written)} ago", $"transcript écrit il y a {Age(now - transcript.Written)}");
        List<string> lines = [s?.Name ?? shortId, $"{shortId}{(where.Length > 0 ? " · " + where : "")}", standing, $"{reading} · {written}",
            say("read only", "lecture seule")];
        foreach (var (text, at) in transcript?.Requests.TakeLast(4) ?? [])
        {
            // The request's own date and time from the transcript, in UTC: never a bare time, which reads as today's.
            var when = at == default ? say("date unknown", "date inconnue") : at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
            lines.Add($"{when}  {Hidden.Replace(text, "[hidden]")}");
        }

        if (transcript is { Requests.Count: 0, Unfound: > 0 } t)
        {
            lines.Add(say($"latest requests unknown: none in its last {t.Unfound >> 20} MiB", $"dernières demandes inconnues : aucune dans ses derniers {t.Unfound >> 20} Mio"));
        }

        return lines;
    }

    // What looks like a key or a token: 24 or more letters, digits, dashes or underscores in a row, with a digit in them.
    private static readonly System.Text.RegularExpressions.Regex Hidden = new(@"(?=[A-Za-z0-9_\-]*\d)[A-Za-z0-9_\-]{24,}");

    private static int Seconds(double s) => (int)Math.Round(s);

    private static string Age(TimeSpan t) => t.TotalSeconds < 120 ? $"{Math.Max(0, (int)t.TotalSeconds)} s" : t.TotalMinutes < 120 ? $"{(int)t.TotalMinutes} min" : $"{(int)t.TotalHours} h";

    internal static void SelfTest(Action<bool, string> check)
    {
        string En(string en, string fr) => en;
        var now = new DateTimeOffset(2026, 11, 4, 14, 0, 0, TimeSpan.Zero);
        const string id = "aeea8bcf-8dca-4cff-b7d5-90a65a80498b";
        var working = new OfficeSession(id, "delta-live", OfficeActivity.Working, 0, "ws-1", "delta-live", 1793400575676);
        var transcript = new Transcript("The wall screen",[("make the wall bigger", now.AddMinutes(-3)), ("show the robot's session", now.AddMinutes(-1))], now.AddSeconds(-20));

        var wall = new WallSource();
        check(wall.Showing == Kind.Parsec && wall.Session is null, "the wall shows Parsec at first");
        wall.ShowSession(id);
        check(wall is { Showing: Kind.Session, Session: id }, "a click on a robot shows its session on the wall");
        wall.ShowPong();
        check(wall is { Showing: Kind.Pong, Session: null }, "a click on the Pong screen shows Pong");
        wall.ShowParsec();
        check(wall.Showing == Kind.Parsec, "and a click on the cabinet brings Parsec back");
        wall.ShowSession(id);
        wall.Released("another");
        check(wall.Showing == Kind.Session, "letting go of another robot keeps the session shown");
        wall.Released(id);
        check(wall is { Showing: Kind.Parsec, Session: null }, "letting go of its robot brings Parsec back");

        var live = string.Join("\n", Card(id, [working], 2, transcript, now, En));
        check(live.Contains("delta-live") && live.Contains("aeea8bcf") && live.Contains("working") && live.Contains("live") &&
            live.Contains("show the robot's session") && live.Contains("read 2 s ago") && live.Contains("transcript written 20 s ago"),
            "a fresh card names the session, its ID, state, freshness and last request, live");
        var old = string.Join("\n", Card(id, [working], 30, transcript, now, En));
        check(old.Contains("reading 30 s old") && !old.Contains(" live"), "an old reading is said, never live");
        var stale = string.Join("\n", Card(id, [working with { Activity = OfficeActivity.Stale }], 2, transcript, now, En));
        check(stale.Contains("stale") && !stale.Contains(" live"), "a stale session is said, never live");
        var gone = string.Join("\n", Card(id, [], 2, transcript, now, En));
        check(gone.Contains("unavailable") && !gone.Contains(" live"), "a session out of the reading is unavailable");
        var none = string.Join("\n", Card(id, [working], 2, null, now, En));
        check(none.Contains("no transcript found") && !none.Contains("show the robot's session"), "a missing transcript is said");
        var unread = string.Join("\n", Card(id, [working], null, transcript, now, En));
        check(unread.Contains("not read yet") && !unread.Contains(" live"), "nothing read yet is not live");
        var paneless = string.Join("\n", Card(id, [working with { WorkspaceId = "", WorkspaceTitle = "" }], 2, transcript, now, En));
        check(paneless.Contains("no wmux pane") && !paneless.Contains("outside"), "a session with no pane says so, not outside");
        check(live.Contains("2026-11-04 13:59 UTC  show the robot's session"), "a request carries its own date and time, in UTC");
        var aged = string.Join("\n", Card(id, [working], 2, new("t", [("hier soir", new DateTimeOffset(2026, 11, 3, 15, 9, 0, TimeSpan.FromHours(-5)))], now), now, En));
        check(aged.Contains("2026-11-03 20:09 UTC  hier soir") && !aged.Contains("\n15:09"), "a request from another day says that day, never only a time that looks like today's");
        var undated = string.Join("\n", Card(id, [working], 2, new("t", [("sans heure", default)], now), now, En));
        check(undated.Contains("date unknown  sans heure") && !undated.Contains("0001") && !undated.Contains(":00  sans"), "a request without a readable date says so");
        var lost = string.Join("\n", Card(id, [working], 2, new("t", [], now, 16L << 20), now, En));
        check(lost.Contains("latest requests unknown: none in its last 16 MiB") && lost.Contains("working · live"), "a long session whose latest requests lie beyond the search says they are unknown");
        var keyed = new Transcript("t", [("use sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789 now", now)], now);
        var redacted = string.Join("\n", Card(id, [working], 2, keyed, now, En));
        check(!redacted.Contains("AbCdEfGhIjKlMnOp") && redacted.Contains("use") && redacted.Contains("[hidden]"), "a token in a request is hidden");
        check(Width >= 1.6f && Math.Abs((Width / Height) - (4f / 3f)) < 0.01f && (Width / 2f) + 0.045f < 1.45f - 0.36f,
            "the wall screen is much larger, 4:3, and clear of the clocks");
    }
}
