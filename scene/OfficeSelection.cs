using Delta.SceneModules;

// The robot the user picked in the agents' office: the scene's own, never sent anywhere. A click on a robot selects it
// and does nothing else; a second click on it soon after is the explicit move to its pane, the only one that leads to a
// command (the host's wmux focus); a later click on it, or one beside the desks, lets it go. The selection goes with its
// session, and when the session runs again in a new process. Kept apart from the window so that it can be tested alone.
internal sealed class OfficeSelection
{
    internal const double DoubleClick = 0.45; // seconds between the two clicks that open a robot's pane

    internal enum Outcome { None, Selected, Cleared, Open }

    private long incarnation;
    private double clickedAt = double.NegativeInfinity;

    public string? Selected { get; private set; }

    // A click at now on the session's robot, its process started at started; null for a click beside the desks.
    public Outcome Click(string? session, long started, double now)
    {
        if (session is null)
        {
            var had = Selected is not null;
            Selected = null;
            return had ? Outcome.Cleared : Outcome.None;
        }

        if (session != Selected)
        {
            (Selected, incarnation, clickedAt) = (session, started, now);
            return Outcome.Selected;
        }

        if (now - clickedAt <= DoubleClick)
        {
            clickedAt = double.NegativeInfinity; // a third quick click starts over
            return Outcome.Open;
        }

        Selected = null;
        return Outcome.Cleared;
    }

    // True when the selection went: its session is no longer shown, or runs in another process than when picked.
    public bool Validate(IReadOnlyList<OfficeSession> sessions)
    {
        if (Selected is null)
        {
            return false;
        }

        var session = sessions.FirstOrDefault(s => s.Id == Selected);
        if (session is not null && (session.Started == incarnation || session.Activity == OfficeActivity.Stopped))
        {
            return false;
        }

        Selected = null;
        return true;
    }
}
