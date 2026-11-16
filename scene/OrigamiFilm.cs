using SN = System.Numerics;

// The origami table's film (--film-origami DIR): the office module's origami table (modules/AgentOffice/IxOrigami.cs)
// playing a fold step by step, filmed close from in front, its plaque, its board and its model in sight, for Seconds of
// the scene's own time at the film's 30 frames a second, so that every frame is recorded however long it takes to draw.
// The table plays what its document holds (DELTA_IX_ORIGAMI); the film steers it through a play document of its own in
// DIR, origami-play.json (DELTA_IX_ORIGAMI_PLAY), which it writes: the first step held until the film rolls, then
// every step in turn from the first. Nothing else is touched: the live window's play document is not this one.
internal sealed class OrigamiFilm(FreeCamera camera, string playFile, float seconds)
{
    // The crane's 21 steps in turn (IxOrigami.Player: 66 s held, 43 s folding), ending in the last one's hold whether
    // the table reads the film's "auto" at once or a second later.
    public const float DefaultSeconds = 109f;

    // The table stands at (3.45, 0, 0.55) in the hall, its plaque towards +z, the board on its left, the model on the right.
    internal static readonly (SN.Vector3 Eye, SN.Vector3 Target) Table = (new(3.55f, 0.98f, 2.1f), new(3.52f, 0.7f, 0.55f));

    public float Seconds => seconds;

    public float Time { get; private set; } = -1f;

    public bool Rolling => Time >= 0f && Time < seconds;

    public bool Done => Time >= seconds;

    // Before the module reads it: the first step, held.
    public static void Prepare(string playFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(playFile)!);
        Write(playFile, "step");
    }

    public void Start()
    {
        Time = 0f;
        camera.GoTo(Table.Eye, Table.Target, cut: true);
        Write(playFile, "auto");
    }

    public void Advance(float dt)
    {
        if (Rolling)
        {
            Time += dt;
        }
    }

    private static void Write(string playFile, string go) => File.WriteAllText(playFile, $$"""{"format":"delta-origami-play","v":1,"go":"{{go}}","step":0}""");

    internal static void SelfTest(Action<bool, string> check)
    {
        check(SN.Vector3.Clamp(Table.Eye, FreeCamera.Min, FreeCamera.Max) == Table.Eye && Table.Eye.Z > Table.Target.Z + 1f,
            "the origami film's eye stands within the hall, in front of the table's plaque");
    }
}
