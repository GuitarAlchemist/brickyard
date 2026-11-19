// The office tower's rings keep turning a little once let go, as a heavy wheel would: while a robot is held, its ring's
// speed is followed, smoothed over Smooth seconds so that the last frames' jitter does not decide it; let go, the ring
// goes on at that speed (at most Most rad/s), slowing by e every Coast seconds, and stops under Rest. Holding a ring
// again stops it. The turns go into OfficeState.Turns as the hand's do.
internal sealed class OfficeSpin
{
    public const double Smooth = 0.06, Coast = 0.7, Most = 8.0, Rest = 0.02;

    private readonly Dictionary<string, double> coasting = new(StringComparer.Ordinal);
    private double held; // the held ring's smoothed speed, rad/s

    public int Coasting => coasting.Count;

    // A ring taken: whatever it was doing, it stops.
    public void Take(string session)
    {
        coasting.Remove(session);
        held = 0;
    }

    // While held, each frame: the ring turned by turned radians over dt seconds.
    public void Follow(double turned, double dt)
    {
        if (dt > 0)
        {
            held += (1 - Math.Exp(-dt / Smooth)) * ((turned / dt) - held);
        }
    }

    // Let go: the ring coasts on from its speed.
    public void Release(string session)
    {
        var speed = Math.Clamp(held, -Most, Most);
        if (Math.Abs(speed) >= Rest)
        {
            coasting[session] = speed;
        }

        held = 0;
    }

    // Each frame, dt seconds on: the coasting rings turn on into turns and slow.
    public void Step(IDictionary<string, double> turns, double dt)
    {
        foreach (var (session, speed) in coasting.ToList())
        {
            var slowed = speed * Math.Exp(-dt / Coast);
            // The exact distance under exponential slowing, so that a slow frame turns it as far as several quick ones.
            turns[session] = turns.TryGetValue(session, out var t) ? t + ((speed - slowed) * Coast) : (speed - slowed) * Coast;
            if (Math.Abs(slowed) < Rest)
            {
                coasting.Remove(session);
            }
            else
            {
                coasting[session] = slowed;
            }
        }
    }

    internal static void SelfTest(Action<bool, string> check)
    {
        var spin = new OfficeSpin();
        var turns = new Dictionary<string, double>();
        for (var i = 0; i < 30; i++)
        {
            spin.Follow(0.05, 1 / 60.0); // 3 rad/s for half a second
        }

        spin.Release("a");
        for (var i = 0; i < 600; i++)
        {
            spin.Step(turns, 1 / 60.0);
        }

        var coast = turns.GetValueOrDefault("a");
        check(coast > 1.9 && coast < 2.15 && spin.Coasting == 0, $"a ring let go at 3 rad/s coasts about 2.1 rad and stops ({coast:F2})");

        var still = new OfficeSpin();
        for (var i = 0; i < 30; i++)
        {
            still.Follow(i < 10 ? 0.05 : 0.0, 1 / 60.0); // turned, then held still a third of a second
        }

        still.Release("b");
        check(still.Coasting == 0, "a ring held still before it is let go does not coast");

        var once = new OfficeSpin();
        var t1 = new Dictionary<string, double>();
        var t2 = new Dictionary<string, double>();
        once.Follow(0.05, 1 / 60.0);
        for (var i = 0; i < 40; i++) once.Follow(0.05, 1 / 60.0);
        once.Release("c");
        var twice = new OfficeSpin();
        for (var i = 0; i < 41; i++) twice.Follow(0.05, 1 / 60.0);
        twice.Release("c");
        once.Step(t1, 0.5);
        for (var i = 0; i < 5; i++) twice.Step(t2, 0.1);
        check(Math.Abs(t1["c"] - t2["c"]) < 1e-9, "a coasting ring turns as far in one slow frame as in several quick ones");
        once.Take("c");
        check(once.Coasting == 0, "taking a coasting ring stops it");
    }
}
