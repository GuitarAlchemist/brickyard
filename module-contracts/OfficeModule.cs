namespace Delta.SceneModules;

// The agents' office: the host reads the Claude Code sessions (as the shelf does) and tells a widget module what each
// one is doing. The module answers with widgets whose Target is a session's Id; the host alone turns a click on one into
// wmux's focus-surface, so the module never names a command.
// Stopped: seen in this run and gone since; Stale: its file is there but its process is not; Unknown: it says nothing
// of itself and wmux nothing of it. Added after Done, so that images built before keep their values.
public enum OfficeActivity { Idle, Working, Waiting, Done, Stopped, Stale, Unknown }

// Since is the host's clock (the seconds Place is given) when the session entered its activity. WorkspaceId is wmux's
// stable ID for the workspace of the session's pane, "" outside wmux, and WorkspaceTitle its name as shown; Started is
// when the session's process started, which a resumed session changes.
public sealed record OfficeSession(string Id, string Name, OfficeActivity Activity, double Since, string WorkspaceId = "",
    string WorkspaceTitle = "", long Started = 0);

// Selected is the session the user picked in the scene, which only the module's poses show; null for none.
// Turns: how far, in radians about the module's own axis, the user has turned each session's place by dragging its
// robot with the mouse, summed over the run; Turning: the session being dragged now, whose ring stands still meanwhile.
// Both are init properties, not constructor parameters, so that module images built before keep their constructor.
public sealed record OfficeState(string Language, bool Flat, IReadOnlyList<OfficeSession> Sessions, string? Selected = null)
{
    public IReadOnlyDictionary<string, double>? Turns { get; init; }

    public string? Turning { get; init; }
}

public interface IOfficeModule : IWidgetModule
{
    void Update(OfficeState state);
}
