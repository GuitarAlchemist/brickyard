using Delta.SceneModules;

// The agents' office's reading: the Claude Code sessions' files (Shelf.SessionFiles, a crash's leftovers included) and
// what wmux says each one is doing (Shelf.Wmux.AgentStates, one call), read every three seconds on the thread pool, a
// read skipped while the last one still waits for wmux, never written; every tenth read also asks wmux each pane's
// workspace (Shelf.Wmux.SurfaceWorkspaces). The shelf reads only when opened, so these are the only periodic wmux
// calls. OfficeActivities merges them and turns the latest reading into activities on the render thread. Under --demo
// it reads nothing: six made-up sessions in two workspaces go through every activity in turn, for the screenshots.
internal sealed class OfficeFeed(bool demo) : IDisposable
{
    private const int Every = 3000;
    private static readonly string[] DemoNames = ["gaia-2a", "ix", "delta-live", "tars-21", "music-90", "brickyard-remote"];
    private static readonly string[] DemoStates = ["working", "idle", "blocked", "working", "idle", "idle"];

    private const int WorkspacesEvery = 10; // reads between two of the workspaces, which take a wmux call each

    private readonly Dictionary<string, OfficeSession> last = new(StringComparer.Ordinal);
    private readonly KeptReading<(string Surface, string State, string Provider)> agents = new("agent states"); // a failed reading keeps the last answer
    private readonly KeptReading<(string Id, string Title)> workspaces = new("workspaces");
    private string? agentsSaid, workspacesSaid;
    private int reads;
    private volatile IReadOnlyList<OfficeActivities.Seen>? reading;
    private IReadOnlyList<OfficeActivities.Seen>? used;
    private Timer? timer;
    private int busy;
    private int demoStep = -1;
    private long readTicks; // UTC ticks of the last reading that completed, 0 before the first

    public IReadOnlyList<OfficeSession> Sessions { get; private set; } = [];

    // Seconds since the last reading completed at now, null before the first; under --demo, nothing is read.
    public double? ReadAgo(DateTimeOffset now) => Interlocked.Read(ref readTicks) is var t and > 0 ? (now.UtcTicks - t) / (double)TimeSpan.TicksPerSecond : null;

    public void Start()
    {
        if (!demo)
        {
            timer ??= new Timer(_ => Read(), null, 0, Every);
        }
    }

    // On the render thread: true when the sessions or an activity changed.
    public bool Advance(double now)
    {
        if (demo)
        {
            var step = (int)(now / 3.0);
            if (reading is null || step != demoStep)
            {
                demoStep = step;
                reading = [.. DemoNames.Select((name, i) => new OfficeActivities.Seen($"demo-{i:x2}-{name}", name, i, "idle", DemoStates[(step + i) % DemoStates.Length], "",
                    WorkspaceId: i < 3 ? "demo-ws-1" : "demo-ws-2", WorkspaceTitle: i < 3 ? "gaia" : "brickyard"))];
            }
        }

        var latest = reading;
        if (latest is null)
        {
            return false;
        }

        var expired = Sessions.Any(s => s.Activity == OfficeActivity.Done && now - s.Since >= OfficeActivities.DoneFor);
        if (ReferenceEquals(latest, used) && !expired)
        {
            return false;
        }

        used = latest;
        var next = OfficeActivities.Derive(latest, last, now);
        var changed = next.Count != Sessions.Count || next.Zip(Sessions).Any(p => p.First != p.Second);
        Sessions = next;
        return changed;
    }

    // The surface a session's pane is in, for a click on its desk.
    public string? Surface(string sessionId) =>
        reading?.FirstOrDefault(s => s.Id == sessionId) is { Surface.Length: > 0 } s ? s.Surface : null;

    public void Dispose()
    {
        timer?.Dispose();
        timer = null;
    }

    private void Read()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1)
        {
            return; // wmux answered slower than the period
        }

        try
        {
            agents.Apply(Shelf.Wmux.AgentStates(), DateTimeOffset.UtcNow);
            agentsSaid = Say(agents, agentsSaid);
            if (reads++ % WorkspacesEvery == 0)
            {
                workspaces.Apply(Shelf.Wmux.SurfaceWorkspaces(), DateTimeOffset.UtcNow);
                workspacesSaid = Say(workspaces, workspacesSaid);
            }

            var files = Shelf.SessionFiles().Select(s => new OfficeActivities.SessionFile(s.Id, s.Name, s.Started, s.Status, s.Alive)).ToList();
            reading = OfficeActivities.Merge(files, agents.Map, workspaces.Map);
            Interlocked.Exchange(ref readTicks, DateTimeOffset.UtcNow.UtcTicks);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // read again at the next period
        }
        finally
        {
            Volatile.Write(ref busy, 0);
        }
    }

    // Logs once when wmux stops answering for a reading, and once when it answers again.
    private static string? Say<TValue>(KeptReading<TValue> kept, string? before)
    {
        var said = kept.Said(DateTimeOffset.UtcNow);
        if ((said is null) != (before is null))
        {
            Console.WriteLine($"WIDGETS agent-office {said ?? kept.What + " read again from wmux"}");
        }

        return said;
    }
}
