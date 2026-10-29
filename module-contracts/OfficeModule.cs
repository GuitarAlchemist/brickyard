namespace Delta.SceneModules;

// The agents' office: the host reads the Claude Code sessions (as the shelf does) and tells a widget module what each
// one is doing. The module answers with widgets whose Target is a session's Id; the host alone turns a click on one into
// wmux's focus-surface, so the module never names a command.
public enum OfficeActivity { Idle, Working, Waiting, Done }

// Since is the host's clock (the seconds Place is given) when the session entered its activity.
public sealed record OfficeSession(string Id, string Name, OfficeActivity Activity, double Since);

public sealed record OfficeState(string Language, bool Flat, IReadOnlyList<OfficeSession> Sessions);

public interface IOfficeModule : IWidgetModule
{
    void Update(OfficeState state);
}
