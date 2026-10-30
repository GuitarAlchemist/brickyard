using SN = System.Numerics;

// The cinematic: the hall filmed as a game's cut scene, in real time, from the scene as it stands, after the
// Observatory's (scripts/cinematic.gd there): the hall from above, the agents' office along its desks, Delta's face, the
// Pong on its chest, Gaia's planet turned round, the clocks of Ottawa and Paris, and back and up from the robot to the
// whole hall. Key 0 plays it, as does --cinematic; any key or click ends it, through black. Its own camera films: the
// camera the player moved stays where it stood and has the view back at the end. Bars make the picture 2.39:1 on screens
// wider than 1.2:1, the title comes over black before the first shot and after the last, and acts fade through black.
// Under reduced motion no handheld sway, and each move shrinks to a slow drift round its middle. Kept apart from the
// window, which draws the bars, the black and the title as this says, so that it can be tested alone.
internal sealed class Cinematic
{
    internal const float Ratio = 2.39f; // the picture's width over its height between the bars
    internal const float Wide = 1.2f; // screens at least this wide get bars
    private const float BarsS = 0.8f; // the bars coming in or going
    private const float DipS = 0.45f; // a fade to or from black between acts
    private const float TitleS = 2.4f; // the title over black at the start
    private const float CardS = 3.2f; // and at the end
    private const float OutS = 0.6f; // back to the player's view

    internal enum Phase { Idle, In, Title, Shot, Card, Out }

    // A curve through points (Catmull-Rom), walked by the fraction of its length.
    internal sealed class Curve
    {
        private readonly SN.Vector3[] points;
        private readonly float[] runs;

        public Curve(params SN.Vector3[] through)
        {
            var pts = through.Length == 1 ? [through[0], through[0]] : through;
            const int Per = 16;
            var sampled = new List<SN.Vector3>();
            for (var i = 0; i < pts.Length - 1; i++)
            {
                var (a, b) = (pts[i], pts[i + 1]);
                var pre = i > 0 ? pts[i - 1] : a - (b - a);
                var post = i + 2 < pts.Length ? pts[i + 2] : b + (b - a);
                for (var j = 0; j < Per; j++)
                {
                    sampled.Add(CatmullRom(pre, a, b, post, (float)j / Per));
                }
            }

            sampled.Add(pts[^1]);
            points = [.. sampled];
            runs = new float[points.Length];
            for (var i = 1; i < points.Length; i++)
            {
                runs[i] = runs[i - 1] + SN.Vector3.Distance(points[i - 1], points[i]);
            }
        }

        public float Length => runs[^1];

        private static SN.Vector3 CatmullRom(SN.Vector3 pre, SN.Vector3 a, SN.Vector3 b, SN.Vector3 post, float t) =>
            0.5f * ((2f * a) + ((b - pre) * t) + (((2f * pre) - (5f * a) + (4f * b) - post) * t * t) + (((3f * a) - pre - (3f * b) + post) * t * t * t));

        public SN.Vector3 Along(float f)
        {
            if (Length <= 1e-5f)
            {
                return points[0];
            }

            var s = Math.Clamp(f, 0f, 1f) * Length;
            var at = Array.BinarySearch(runs, s);
            if (at >= 0)
            {
                return points[at];
            }

            var hi = Math.Min(~at, points.Length - 1);
            var lo = Math.Max(0, hi - 1);
            var span = runs[hi] - runs[lo];
            return SN.Vector3.Lerp(points[lo], points[hi], span > 0f ? (s - runs[lo]) / span : 0f);
        }
    }

    // A shot: its name and seconds, how much its move eases, the operator's sway in degrees, its field (degrees of the
    // picture between the bars) from and to, the camera's path and where it looks, and its edges: into it from black, and
    // out of it to black (a dip) or slowly (long).
    internal sealed record Shot(string Name, float Seconds, float Ease, float Sway, float FovFrom, float FovTo, Curve Camera, Curve Look,
        bool DipIn = false, string Out = "cut");

    // Where the shots look: Delta's face, its chest, the office's desks, Gaia's planet, the clocks.
    private static readonly SN.Vector3 Face = new(0f, 1.05f, 0.05f);
    private static readonly SN.Vector3 Chest = new(0f, 0.62f, 0.15f);
    private static readonly SN.Vector3 Office = new(-1.1f, 0.45f, -0.1f);
    private static readonly SN.Vector3 Planet = new(0.88f, 0.69f, 0.16f);
    private static readonly SN.Vector3 Clocks = new(0f, 1.78f, -3.3f);

    internal static readonly Shot[] Shots =
    [
        new("hall", 7f, 0.9f, 0.25f, 46f, 40f, new(new(3.6f, 3.3f, 5.3f), new(2.6f, 2.4f, 4.4f), new(1.6f, 1.7f, 3.6f)),
            new(new(0f, 0.5f, -0.8f), new(0f, 0.7f, -0.3f), new(0f, 0.8f, 0f)), DipIn: true),
        new("office", 7f, 0.85f, 0.2f, 40f, 36f, new(new(-2.6f, 1.05f, 1.9f), new(-1.8f, 0.95f, 1.55f), new(-0.75f, 0.9f, 1.35f)),
            new(new(-1.7f, 0.45f, -0.2f), new(-1.3f, 0.45f, -0.15f), Office), Out: "dip"),
        new("delta", 8f, 0.8f, 0.3f, 34f, 30f, new(new(-0.75f, 1.05f, 1.35f), new(0f, 1.0f, 1.55f), new(0.7f, 1.08f, 1.3f)),
            new(Face, Face + new SN.Vector3(0f, 0.02f, 0f)), DipIn: true),
        new("pong", 6f, 0.7f, 0.25f, 36f, 30f, new(new(0.35f, 0.8f, 1.6f), new(0.2f, 0.74f, 1.25f), new(0.1f, 0.7f, 1.05f)),
            new(Chest, Chest), Out: "dip"),
        new("gaia", 8f, 0.8f, 0.2f, 34f, 32f, new(new(2.3f, 1.05f, 1.7f), new(2.15f, 0.95f, 1.0f), new(2.0f, 0.9f, 0.3f)),
            new(Planet, Planet), DipIn: true),
        new("clocks", 6f, 0.85f, 0.2f, 38f, 30f, new(new(0f, 1.25f, 1.0f), new(0f, 1.55f, -0.3f), new(0f, 1.72f, -1.3f)),
            new(Clocks + new SN.Vector3(0f, -0.4f, 0f), Clocks), Out: "dip"),
        new("finale", 9f, 0.8f, 0.2f, 36f, 50f, new(new(0f, 1.0f, 1.5f), new(0f, 1.6f, 3.0f), new(0f, 2.7f, 5.2f)),
            new(Face, new(0f, 0.9f, -0.5f), new(0f, 1.3f, -2.6f)), DipIn: true, Out: "long"),
    ];

    private float phaseTime;
    private int shot = -1;
    private float shotTime;
    private float clock; // for the sway, which goes on through the shots

    public Phase Now { get; private set; } = Phase.Idle;

    // While the cinematic's camera films, from the black after the bars until the black at the end.
    public bool Filming { get; private set; }

    public bool ReducedMotion { get; set; }

    public float Bars { get; private set; } // 0 none to 1 full

    public float Black { get; private set; } // the black over the picture, 0 to 1

    public float Title { get; private set; } // the title's opacity

    public float Hint { get; private set; } // "Any key: skip"

    public string ShotName => shot >= 0 && shot < Shots.Length && Now == Phase.Shot ? Shots[shot].Name : "";

    public int Plays { get; private set; }

    public int Skips { get; private set; }

    public List<string> Begun { get; } = []; // the shots begun, in order, for tests

    public bool Playing => Now != Phase.Idle;

    // Plays it from the first shot (or from shot from, for captures); false while it plays.
    public bool Play(int from = 0)
    {
        if (Now != Phase.Idle)
        {
            return false;
        }

        Now = Phase.In;
        phaseTime = 0f;
        shot = Math.Clamp(from, 0, Shots.Length - 1) - 1;
        Plays++;
        Begun.Clear();
        return true;
    }

    // Ends it from wherever it is: through black, back to the player.
    public void Skip()
    {
        if (Now is Phase.Idle || (Now == Phase.Out && !Filming))
        {
            return;
        }

        if (Now == Phase.In)
        {
            Filming = false;
        }

        Now = Phase.Out;
        phaseTime = 0f;
        Skips++;
    }

    // dt seconds on, by the clock.
    public void Advance(float dt)
    {
        if (Now == Phase.Idle)
        {
            return;
        }

        dt = Math.Clamp(dt, 0f, 0.5f);
        phaseTime += dt;
        clock += dt;
        switch (Now)
        {
            case Phase.In:
                // The bars come in over the player's view, then black, then the title.
                Bars = Math.Clamp(phaseTime / BarsS, 0f, 1f);
                Black = Math.Clamp((phaseTime - (BarsS * 0.6f)) / DipS, 0f, 1f);
                Hint = Math.Clamp(phaseTime / 0.5f, 0f, 0.75f);
                if (phaseTime >= (BarsS * 0.6f) + DipS)
                {
                    Now = Phase.Title;
                    phaseTime = 0f;
                    Filming = true;
                }

                break;
            case Phase.Title:
                Bars = 1f;
                Black = 1f;
                Title = Math.Clamp(phaseTime / 0.6f, 0f, 1f);
                if (phaseTime >= TitleS)
                {
                    NextShot();
                }

                break;
            case Phase.Shot:
                Title = Math.Max(0f, Title - (dt * 2f));
                Film(dt);
                Hint = Begun.Count <= 1 ? Math.Clamp(1f - ((shotTime - 2.5f) / 0.8f), 0f, 0.75f) : 0f;
                break;
            case Phase.Card:
                Black = 1f;
                Title = Math.Clamp(Math.Min(phaseTime / 0.8f, (CardS - phaseTime) / 0.8f), 0f, 1f);
                if (phaseTime >= CardS)
                {
                    Filming = false;
                    Now = Phase.Out;
                    phaseTime = DipS; // already black
                }

                break;
            case Phase.Out:
                Hint = 0f;
                Title = Math.Max(0f, Title - (dt * 3f));
                if (Filming)
                {
                    // A skip: to black first, then the player's view.
                    Black = Math.Max(Black, Math.Clamp(phaseTime / 0.3f, 0f, 1f));
                    if (phaseTime >= 0.3f)
                    {
                        Filming = false;
                        phaseTime = DipS;
                    }
                }
                else
                {
                    var k = Math.Clamp((phaseTime - DipS) / OutS, 0f, 1f);
                    Black = Math.Min(Black, 1f - k);
                    Bars = Math.Min(Bars, 1f - k);
                    if (k >= 1f)
                    {
                        Now = Phase.Idle;
                        Bars = Black = Title = Hint = 0f;
                    }
                }

                break;
        }
    }

    // The cinematic camera's pose for a screen of the given width over height, while it films; before the first shot,
    // behind the title, it stands at the first shot's start.
    public FreeCamera.Pose? Pose(float aspect)
    {
        if (!Filming)
        {
            return null;
        }

        var s = Shots[Math.Clamp(shot, 0, Shots.Length - 1)];
        var u = Now == Phase.Shot ? Math.Clamp(shotTime / s.Seconds, 0f, 1f) : Now == Phase.Title ? 0f : 1f;
        if (ReducedMotion)
        {
            u = 0.42f + (0.16f * u); // a drift round the shot's middle
        }

        var k = float.Lerp(u, u * u * (3f - (2f * u)), s.Ease);
        var eye = s.Camera.Along(k);
        var at = s.Look.Along(k);
        var fov = float.Lerp(s.FovFrom, s.FovTo, k);
        var facing = SN.Vector3.Normalize(at - eye);
        var right = SN.Vector3.Normalize(SN.Vector3.Cross(facing, SN.Vector3.UnitY));
        var up = SN.Vector3.Cross(right, facing);
        if (!ReducedMotion && s.Sway > 0f)
        {
            // A handheld operator's slow sway, about the shot's degrees.
            var a = s.Sway * MathF.PI / 180f;
            var t = clock;
            var turn = SN.Quaternion.CreateFromAxisAngle(up, a * ((0.6f * MathF.Sin((0.71f * t) + 1.3f)) + (0.4f * MathF.Sin((1.37f * t) + 0.2f))))
                * SN.Quaternion.CreateFromAxisAngle(right, a * ((0.6f * MathF.Sin(0.53f * t)) + (0.4f * MathF.Sin((1.13f * t) + 2.1f))))
                * SN.Quaternion.CreateFromAxisAngle(facing, a * 0.5f * MathF.Sin((0.37f * t) + 0.7f));
            facing = SN.Vector3.Transform(facing, turn);
            up = SN.Vector3.Transform(up, turn);
        }

        return new(eye, eye + facing, up, ScreenFov(fov, aspect));
    }

    // The bars' height, each, as a fraction of the screen's: none on a screen narrower than Wide.
    public float BarHeight(float aspect) => aspect >= Wide ? (1f - Math.Min(1f, aspect / Ratio)) / 2f * Bars : 0f;

    // The camera's vertical field for a picture of fov degrees between the bars, the full height being taller; on an
    // upright screen, wider across as the Observatory's view_fit.gd makes it.
    public static float ScreenFov(float fov, float aspect)
    {
        var half = fov * MathF.PI / 360f;
        if (aspect >= Wide)
        {
            var band = Math.Min(1f, aspect / Ratio);
            return 2f * MathF.Atan(MathF.Tan(half) / band) * 180f / MathF.PI;
        }

        var across = 2f * MathF.Atan(MathF.Tan(half) * aspect) * 180f / MathF.PI;
        if (across >= 60f)
        {
            return fov;
        }

        return Math.Clamp(2f * MathF.Atan(MathF.Tan(MathF.PI / 6f) / aspect) * 180f / MathF.PI, fov, Math.Max(fov, 90f));
    }

    private void NextShot()
    {
        shot++;
        if (shot >= Shots.Length)
        {
            Now = Phase.Card;
            phaseTime = 0f;
            return;
        }

        Now = Phase.Shot;
        shotTime = 0f;
        Begun.Add(Shots[shot].Name);
        Film(0f);
    }

    private void Film(float dt)
    {
        var s = Shots[shot];
        shotTime += dt;
        // Into and out of black at an act's edges.
        var black = 0f;
        if (s.DipIn)
        {
            black = Math.Max(black, 1f - Math.Clamp(shotTime / DipS, 0f, 1f));
        }

        if (s.Out == "dip")
        {
            black = Math.Max(black, Math.Clamp((shotTime - (s.Seconds - DipS)) / DipS, 0f, 1f));
        }
        else if (s.Out == "long")
        {
            black = Math.Max(black, Math.Clamp((shotTime - (s.Seconds - 2.6f)) / 2.6f, 0f, 1f));
        }

        Black = black;
        if (shotTime >= s.Seconds)
        {
            NextShot();
        }
    }

    internal static void SelfTest(Action<bool, string> Check)
    {
        var film = new Cinematic();
        Check(!film.Playing && film.Pose(16f / 9f) is null && film.Play() && !film.Play(), "0 plays the cinematic, once at a time");
        var total = BarsS * 0.6f + DipS + TitleS + Shots.Sum(s => s.Seconds) + CardS + OutS;
        var seconds = 0f;
        var barsAt = -1f;
        var titled = false;
        var blackBetween = false;
        var poses = 0;
        var ends = new List<SN.Vector3>();
        var last = "";
        while (film.Playing && seconds < total + 5f)
        {
            film.Advance(1f / 30f);
            seconds += 1f / 30f;
            if (film.Bars >= 1f && barsAt < 0f)
            {
                barsAt = seconds;
            }

            titled |= film.Now == Phase.Title && film.Title > 0.9f && film.Black >= 1f;
            if (film.Pose(16f / 9f) is { } pose)
            {
                poses++;
                if (film.ShotName != last && film.ShotName != "")
                {
                    ends.Add(pose.Eye);
                    last = film.ShotName;
                }
            }

            blackBetween |= film.ShotName == "delta" && film.Black > 0.9f;
        }

        Check(barsAt is > 0.7f and < 0.9f && titled, $"the bars come in, then the title over black ({barsAt:F2} s)");
        Check(film.Begun.SequenceEqual(Shots.Select(s => s.Name)) && ends.Count == Shots.Length, $"every shot is filmed in order: {string.Join(", ", film.Begun)}");
        Check(blackBetween && poses > 0, "acts dip through black");
        Check(!film.Playing && film.Bars == 0f && film.Black == 0f && MathF.Abs(seconds - total) < 0.3f,
            $"it ends by itself and gives the view back ({seconds:F1} s of {total:F1})");

        film = new Cinematic();
        film.Play();
        for (var i = 0; i < 150; i++)
        {
            film.Advance(1f / 30f);
        }

        var shooting = film.ShotName;
        film.Skip();
        var skipped = 0f;
        while (film.Playing && skipped < 3f)
        {
            film.Advance(1f / 30f);
            skipped += 1f / 30f;
        }

        Check(shooting == "hall" && film.Skips == 1 && !film.Playing && skipped < 0.3f + OutS + 0.1f, $"any key skips it, through black ({skipped:F2} s)");

        var bars = new Cinematic { Bars = 1f };
        var high = bars.BarHeight(16f / 9f);
        Check(MathF.Abs((1f - (2f * high)) * 2.39f - (16f / 9f)) < 1e-3f && bars.BarHeight(9f / 16f) == 0f, "the bars leave a 2.39:1 picture, none on an upright screen");
        Check(ScreenFov(40f, 16f / 9f) > 40f && MathF.Abs(ScreenFov(40f, 2.39f) - 40f) < 1e-3f, "the field is the picture's between the bars");

        foreach (var s in Shots)
        {
            var inside = true;
            for (var f = 0f; f <= 1f; f += 0.05f)
            {
                var p = s.Camera.Along(f);
                inside &= p.X > -6f && p.X < 6f && p.Y > 0.3f && p.Y < 4.2f && p.Z > -3.1f && p.Z < 5.6f;
                inside &= SN.Vector3.Distance(p, s.Look.Along(f)) > 0.3f;
            }

            Check(inside, $"the shot {s.Name} stays inside the hall and keeps its subject in front");
        }

        var reduced = new Cinematic { ReducedMotion = true };
        reduced.Play();
        for (var i = 0; i < 120; i++)
        {
            reduced.Advance(1f / 30f);
        }

        var a = reduced.Pose(16f / 9f)!.Value;
        reduced.Advance(1f);
        var b = reduced.Pose(16f / 9f)!.Value;
        Check(SN.Vector3.Distance(a.Eye, b.Eye) < 0.2f, "under reduced motion a shot only drifts");
    }
}
