using SN = System.Numerics;

// The arcade's film (--film-arcade DIR): 45 s of the small robot (ArcadePlayer) playing PARSEC at the cabinet, filmed in
// the hall's two manners after GTA V. First a view's flight as the character switch makes it, from the whole hall up and
// down to the cabinet; then a cut scene between bars with an operator's sway, five shots that keep the game's screen in
// sight, over or beside the robot's head; then a flight back out to the hall. The film's time is the scene's, so that
// every frame is recorded whatever it takes to draw.
internal sealed class ArcadeFilm(FreeCamera camera, Cinematic film)
{
    public const float Seconds = 45f;
    private const float FlyIn = 0.6f; // the hall, then the flight down to the cabinet
    private const float CutScene = 4.2f; // the bars come in over the landed view
    private const float FlyOut = 41.9f; // after the cut scene gives the view back, the flight out to the hall

    private static SN.Vector3 At(float x, float y, float z) => SN.Vector3.Transform(new(x, y, z), ArcadeCabinet.World);

    private static Cinematic.Curve Path(params SN.Vector3[] points) => new([.. points.Select(p => At(p.X, p.Y, p.Z))]);

    // Where the flight lands: high and to the right, away from the steel cabinet on the left, over the robot's shoulder,
    // the whole screen clear of its head. The hall's tour (IdleTour) lands there too.
    internal static readonly (SN.Vector3 Eye, SN.Vector3 Target) Landing = (At(1.0f, 2.35f, 1.7f), At(0f, 1.2f, 0.2f));

    // The cut scene's shots, in the cabinet's frame: x across, y up, z towards the player.
    public static readonly Cinematic.Shot[] Shots =
    [
        new("approach", 6.5f, 0.8f, 0.25f, 38f, 34f, Path(new(1.4f, 1.5f, 1.7f), new(1.0f, 1.75f, 1.3f), new(0.7f, 1.95f, 1.05f)),
            Path(new(0f, 1.2f, 0.25f), new(0f, 1.25f, 0.12f)), DipIn: true),
        new("screen", 6.5f, 0.7f, 0.2f, 30f, 25f, Path(new(0.35f, 1.9f, 1.25f), new(0.25f, 1.75f, 0.95f)),
            Path(new(0f, 1.27f, 0.1f), new(0f, 1.27f, 0.12f))),
        new("hands", 5.5f, 0.8f, 0.3f, 38f, 34f, Path(new(0.95f, 1.35f, 0.95f), new(0.75f, 1.4f, 0.8f)),
            Path(new(0f, 1.05f, 0.35f), new(0f, 1.2f, 0.25f)), Out: "dip"),
        new("marquee", 5.9f, 0.8f, 0.2f, 34f, 30f, Path(new(0.1f, 2.15f, 1.35f), new(0.05f, 1.95f, 1.15f)),
            Path(new(0f, 1.71f, 0.14f), new(0f, 1.27f, 0.12f)), DipIn: true),
        new("orbit", 6.0f, 0.9f, 0.2f, 36f, 40f, Path(new(1.2f, 1.85f, 1.25f), new(0.5f, 2.1f, 1.6f), new(-0.35f, 2.15f, 1.6f)),
            Path(new(0f, 1.2f, 0.3f), new(0f, 1.2f, 0.3f)), Out: "dip"),
    ];

    // The pilot that plays the game by its keys, built into the scene.
    public static string Pilot()
    {
        using var stream = typeof(ArcadeFilm).Assembly.GetManifestResourceStream("ParsecPilot.js") ?? throw new InvalidDataException("No pilot in the scene");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static (string Title, string Under) Heading =>
        ("PARSEC", Words.Say("A small robot at the arcade", "Un petit robot à la borne d'arcade"));

    public float Time { get; private set; } = -1f;

    public bool Rolling => Time >= 0f && Time < Seconds;

    public bool Done => Time >= Seconds;

    // Starts the film: the camera cut to the whole hall.
    public void Start()
    {
        Time = 0f;
        camera.GoTo(4, cut: true);
    }

    public void Advance(float dt)
    {
        if (!Rolling)
        {
            return;
        }

        var before = Time;
        Time += dt;
        bool Crossed(float at) => before < at && Time >= at;
        if (Crossed(FlyIn))
        {
            camera.GoTo(Landing.Eye, Landing.Target);
        }

        if (Crossed(CutScene))
        {
            film.Play();
        }

        if (Crossed(FlyOut))
        {
            camera.GoTo(4);
        }
    }

    // The film's sound as a 16-bit mono WAV file at 44.1 kHz, for FFmpeg to put with the picture.
    public static void SaveWav(string path, ReadOnlySpan<float> samples)
    {
        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8);
        file.Write(36 + (samples.Length * 2));
        file.Write("WAVEfmt "u8);
        file.Write(16);
        file.Write((short)1);
        file.Write((short)1);
        file.Write(ScriptAudio.SampleRate);
        file.Write(ScriptAudio.SampleRate * 2);
        file.Write((short)2);
        file.Write((short)16);
        file.Write("data"u8);
        file.Write(samples.Length * 2);
        foreach (var sample in samples)
        {
            file.Write((short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
        }
    }

    internal static void SelfTest(Action<bool, string> check)
    {
        ArcadePlayer.SelfTest(check);
        check(CutScene + 0.93f + 2.4f + Shots.Sum(s => s.Seconds) + 3.2f + 0.6f <= FlyOut && FlyOut + 2.4f <= Seconds,
            "the cut scene gives the view back before the flight out, which lands before the end");
    }
}
