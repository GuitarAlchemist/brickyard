using SN = System.Numerics;

// While nobody moves the camera, the hall shows itself: after IdleSeconds without a key, a click or the wheel, the camera
// goes from stop to stop by the flights of GTA V's character switch (FreeCamera.GoTo), the camera's own manner, not the
// film's bars, and stays a while at each: Delta, the whole hall, the small robot at the cabinet over its shoulder and the
// game on the wall while anyone plays, Gaia, the agents' office. A key, a click or the wheel gives the camera back where
// the tour left it. Nothing else may hold the view meanwhile (the caller says when it may tour).
internal sealed class IdleTour(FreeCamera camera, Func<bool> broadcasting)
{
    public const double IdleSeconds = 25;
    private const double Stay = 8; // at each stop, after the flight's landing
    private const double Flight = 2.4; // a flight's longest

    // The wall's broadcast, between the clocks, seen over Delta's head.
    private static readonly (SN.Vector3 Eye, SN.Vector3 Target) Wall = (new(0.3f, 1.85f, -0.4f), new(0f, ArcadeCabinet.WallMiddle, WallClocks.Wall));

    private readonly (string Name, Action Go, Func<bool> Open)[] stops =
    [
        ("Delta", () => camera.GoTo(1), () => true),
        ("the hall", () => camera.GoTo(4), () => true),
        ("the cabinet", () => camera.GoTo(ArcadeFilm.Landing.Eye, ArcadeFilm.Landing.Target), broadcasting),
        ("the wall", () => camera.GoTo(Wall.Eye, Wall.Target), broadcasting),
        ("Gaia", () => camera.GoTo(3), () => true),
        ("the office", () => camera.GoTo(2), () => true),
    ];

    private double touched;
    private double next = double.PositiveInfinity;
    private int stop = -1;

    public bool Touring => stop >= 0;

    public double After { get; init; } = IdleSeconds;

    public string? Stop => Touring ? stops[stop].Name : null;

    // A hand on the keys, the mouse or the wheel: the camera is the person's again.
    public void Touch(double now)
    {
        touched = now;
        if (Touring)
        {
            stop = -1;
            Console.WriteLine("SCENE tour handed the camera back");
        }
    }

    // Each frame; may says whether the view is free to tour (no shelf, card, wheel, film or game of the person's).
    public void Advance(double now, bool may)
    {
        if (!may)
        {
            touched = Math.Max(touched, now);
            stop = -1;
            return;
        }

        if (!Touring && now - touched >= After)
        {
            stop = -1;
            Go(now);
        }
        else if (Touring && now >= next)
        {
            Go(now);
        }
    }

    // To the next stop that is open, after the one the tour is at.
    private void Go(double now)
    {
        for (var i = 1; i <= stops.Length; i++)
        {
            var at = (stop + i + stops.Length) % stops.Length;
            if (stop < 0)
            {
                at = i - 1;
            }

            if (stops[at].Open())
            {
                stop = at;
                stops[at].Go();
                next = now + Flight + Stay;
                Console.WriteLine($"SCENE tour to {stops[at].Name}");
                return;
            }
        }
    }

    internal static void SelfTest(Action<bool, string> check)
    {
        var lit = false;
        var camera = new FreeCamera();
        camera.View(0f);
        var tour = new IdleTour(camera, () => lit);
        tour.Touch(0);
        tour.Advance(IdleSeconds - 1, true);
        check(!tour.Touring, "the camera stays the person's before the idle time");
        var now = (double)IdleSeconds;
        tour.Advance(now, true);
        check(tour.Stop == "Delta", "after the idle time the tour starts with Delta");
        string? Next()
        {
            now += Flight + Stay + 0.001;
            tour.Advance(now, true);
            return tour.Stop;
        }

        check(Next() == "the hall" && camera.InFlight, "then flies to the hall, by a view's flight");
        check(Next() == "Gaia", "the cabinet and the wall are left out while nobody plays");
        lit = true;
        check(Next() == "the office" && Next() == "Delta" && Next() == "the hall" && Next() == "the cabinet", "the tour goes round, to the robot at the cabinet while it plays");
        check(Next() == "the wall", "and to the game on the wall");
        now += 1;
        tour.Touch(now);
        tour.Advance(now + 1, true);
        check(!tour.Touring, "a hand ends the tour");
        tour.Advance(now + IdleSeconds - 1, false);
        tour.Advance(now + IdleSeconds + 1, true);
        check(!tour.Touring, "a shelf or a game open meanwhile counts as a hand");
    }
}
