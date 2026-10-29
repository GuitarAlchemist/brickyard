using Delta.ArcadePong;

var failures = 0;
void Check(bool ok, string name)
{
    Console.WriteLine($"PONG {(ok ? "ok" : "FAIL")} {name}");
    if (!ok) failures++;
}
bool Near(double a, double b) => Math.Abs(a - b) < 1e-8;
Ball BallAt(double x, double y, double vx, double vy) => new() { X = x, Y = y, Vx = vx, Vy = vy };

// Exact crossing scenarios, independent of the autonomous players.
var ball = BallAt(80, PongPhysics.Top + 0.1, 40, -40);
PongPhysics.Step(ref ball, 48, 48, 60);
Check(ball.Vy > 0 && ball.Y >= PongPhysics.Top, "top crossing reflects and keeps overshoot inside");
ball = BallAt(80, PongPhysics.Bottom - 0.1, 40, 40);
PongPhysics.Step(ref ball, 48, 48, 60);
Check(ball.Vy < 0 && ball.Y <= PongPhysics.Bottom, "bottom crossing reflects and keeps overshoot inside");
ball = BallAt(PongPhysics.LeftFace + 0.1, 48, -60, 0);
var hit = PongPhysics.Step(ref ball, 48, 48, 60);
Check(hit == BallEvent.LeftHit && ball.Vx > 0 && ball.X >= PongPhysics.LeftFace, "left paddle crossing bounces once");
Check(PongPhysics.Step(ref ball, 48, 48, 60) == BallEvent.None, "departing paddle cannot bounce again");
ball = BallAt(PongPhysics.RightFace - 0.1, 48, 60, 0);
Check(PongPhysics.Step(ref ball, 48, 48, 60) == BallEvent.RightHit && ball.Vx < 0, "right paddle crossing bounces");
ball = BallAt(PongPhysics.LeftFace + 0.1, 59, -60, 0);
Check(PongPhysics.Step(ref ball, 48, 48, 120) == BallEvent.LeftHit && Near(Math.Sqrt(ball.Vx * ball.Vx + ball.Vy * ball.Vy), 120), "paddle edge includes ball radius; rebound speed stays bounded");
ball = BallAt(PongPhysics.LeftFace + 0.1, 59.01, -60, 0);
Check(PongPhysics.Step(ref ball, 48, 48, 60) == BallEvent.None && ball.Vx < 0, "just outside paddle edge is a miss");
ball = BallAt(PongPhysics.LeftFace - 1, 48, -60, 0);
Check(PongPhysics.Step(ref ball, 48, 48, 60) == BallEvent.None && ball.Vx < 0, "ball behind a paddle cannot be rescued");
ball = BallAt(PongPhysics.RightFace - 0.1, 58.8, 60, 60);
Check(PongPhysics.Step(ref ball, 48, 48, 120) == BallEvent.RightHit, "paddle uses swept impact height, not end-of-tick height");
ball = BallAt(-1.9, 48, -60, 0);
Check(PongPhysics.Step(ref ball, 48, 48, 60) == BallEvent.RightPoint, "left exit awards right player");
ball = BallAt(161.9, 48, 60, 0);
Check(PongPhysics.Step(ref ball, 48, 48, 60) == BallEvent.LeftPoint, "right exit awards left player");
ball = BallAt(PongPhysics.RightFace - 0.1, PongPhysics.Top + 0.1, 60, -60);
PongPhysics.Step(ref ball, 23, 23, 120);
Check(ball.Vx < 0 && ball.Y >= PongPhysics.Top && ball.Y <= PongPhysics.Bottom, "wall and paddle in the same tick stay inside");

var game = new PongSimulation();
var initial = game.Frame;
for (var i = 0; i < 600; i++) game.Advance(1.0 / 120);
Check(game.Frame != initial && game.Frame.BallX != 80, "autonomous simulation leaves the serve and plays");
game.SetPaused(true);
var frozen = game.Frame;
for (var i = 0; i < 300; i++) game.Advance(0.25);
Check(game.Frame == frozen, "pause freezes ball, paddles, scores and serve");
game.SetPaused(false);
var resumed = game.Frame;
game.Advance(0);
Check(game.Frame == resumed, "resume has no accumulated catch-up");
game.Reset();
Check(game.Frame == initial, "reset restores seed, score, serve, paddles and pause");
var replay = new PongSimulation();
for (var i = 0; i < 24000; i++) { game.Advance(1.0 / 120); replay.Advance(1.0 / 120); }
Check(game.Frame == replay.Frame && game.Mistakes > 0 && game.Points > 0, "same seed replays exactly with occasional mistakes and points");
var scored = new PongSimulation();
for (var i = 0; i < 120 * 300 && scored.Points == 0; i++) scored.Advance(1.0 / 120);
var point = scored.Frame;
Check(scored.Points == 1 && point.LeftScore + point.RightScore == 1 && point.ServeTicks == 90
    && point.BallX == 80 && point.BallY == 54 && point.VelocityX == 0 && point.VelocityY == 0,
    "a real autonomous point increments once and recentres for a fresh serve");
scored.Reset();
Check(scored.Frame == initial && scored.Points == 0 && scored.Mistakes == 0, "reset after scoring clears counters and replays the initial seed");
var ticks = new PongSimulation();
var chunks = new PongSimulation();
for (var i = 0; i < 7200; i++) ticks.Advance(1.0 / 120);
for (var i = 0; i < 240; i++) chunks.Advance(0.25);
Check(ticks.Frame == chunks.Frame, "fixed-step result is independent of elapsed-time partition");
var capped = new PongSimulation();
var quarter = new PongSimulation();
capped.Advance(3600);
quarter.Advance(0.25);
Check(capped.Frame == quarter.Frame && capped.LastStepCount == 30, "long stall discards backlog and does at most 30 ticks");
var before = capped.Frame;
foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.1 })
{
    var rejected = false;
    try { capped.Advance(bad); } catch (ArgumentOutOfRangeException) { rejected = true; }
    Check(rejected && capped.Frame == before, $"invalid elapsed {bad} cannot corrupt state");
}
var configured = new PongSimulation(new(BallSpeed: 1e6, PaddleSpeed: 1e6, Seed: 7));
Check(configured.Options.BallSpeed == 120 && configured.Options.PaddleSpeed == 140, "configuration clamps speeds to safe limits");
var slow = new PongSimulation(new(BallSpeed: -1, PaddleSpeed: -1, Seed: 7));
Check(slow.Options.BallSpeed == 40 && slow.Options.PaddleSpeed == 24, "configuration also bounds minimum speeds");
var raster = new PongRasterizer();
var buffer = raster.Pixels;
raster.Draw(game.Frame);
Check(buffer.Length == 160 * 96 * 4 && buffer.Span[3] == 255 && buffer.Span[(14 * 160 + 20) * 4] > 0, "fixed RGBA surface contains opaque court pixels");
var pixelsBefore = buffer.ToArray();
game.Advance(0.25);
raster.Draw(game.Frame);
Check(!pixelsBefore.AsSpan().SequenceEqual(buffer.Span), "same raster buffer produces changing game pixels");
var bounds = true;
var rollover = false;
var previous = configured.Frame;
var maxSpeed = 0.0;
// Six simulated hours at the largest configured speeds, no real-time waiting.
for (var i = 0; i < 6 * 3600 * 4; i++)
{
    configured.Advance(0.25);
    var frame = configured.Frame;
    rollover |= previous.LeftScore == 99 && frame.LeftScore == 0 || previous.RightScore == 99 && frame.RightScore == 0;
    previous = frame;
    var speed = Math.Sqrt(frame.VelocityX * frame.VelocityX + frame.VelocityY * frame.VelocityY);
    maxSpeed = Math.Max(maxSpeed, speed);
    bounds &= double.IsFinite(frame.BallX) && frame.BallY >= PongPhysics.Top && frame.BallY <= PongPhysics.Bottom
        && frame.LeftY >= 23 && frame.LeftY <= 85 && frame.RightY >= 23 && frame.RightY <= 85
        && frame.LeftScore is >= 0 and <= 99 && frame.RightScore is >= 0 and <= 99;
}
Check(bounds && rollover && maxSpeed <= 120.000001 && configured.Points > 200, "six-hour soak: finite positions, walls, paddles, score rollover and speed bound");
for (var i = 0; i < 2000; i++) { game.Advance(1.0 / 120); raster.Draw(game.Frame); }
var allocated = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 10000; i++) { game.Advance(1.0 / 120); raster.Draw(game.Frame); }
var growth = GC.GetAllocatedBytesForCurrentThread() - allocated;
Check(growth == 0 && buffer.Equals(raster.Pixels), $"steady simulation + raster allocates {growth} bytes across 10000 frames; buffer reused");
Console.WriteLine($"PONG evidence points={configured.Points}, mistakes={configured.Mistakes}, maxSpeed={maxSpeed:F6}, RGBA={buffer.Length} bytes");

if (args.Length == 2 && args[0] == "--preview")
{
    Directory.CreateDirectory(args[1]);
    var preview = new PongSimulation();
    var sheet = new byte[160 * 3 * 96 * 2 * 4];
    int[] seconds = [0, 2, 6, 10, 20, 30];
    for (var frame = 0; frame < seconds.Length; frame++)
    {
        if (frame > 0) for (var i = 0; i < 120 * (seconds[frame] - seconds[frame - 1]); i++) preview.Advance(1.0 / 120);
        raster.Draw(preview.Frame);
        Png.Save(Path.Combine(args[1], $"pong-{seconds[frame]}s.png"), raster.Pixels.Span, 160, 96, false);
        for (var y = 0; y < 96; y++)
            raster.Pixels.Span.Slice(y * 160 * 4, 160 * 4).CopyTo(sheet.AsSpan((((frame / 3 * 96 + y) * 480) + frame % 3 * 160) * 4));
    }
    Png.Save(Path.Combine(args[1], "pong-contact.png"), sheet, 480, 192, false);
    preview.SetPaused(true);
    raster.Draw(preview.Frame);
    Png.Save(Path.Combine(args[1], "pong-paused.png"), raster.Pixels.Span, 160, 96, false);
    Console.WriteLine($"PONG CPU preview: {Path.GetFullPath(args[1])}; raster only, no robot integration");
}
if (args.Length == 2 && args[0] == "--clip")
{
    Directory.CreateDirectory(args[1]);
    var clip = new PongSimulation();
    var clipRaster = new PongRasterizer();
    // 12 seconds at 20 fps, sampled from exactly the same tested 120 Hz C# simulation.
    for (var frame = 0; frame < 240; frame++)
    {
        if (frame > 0) for (var tick = 0; tick < 6; tick++) clip.Advance(PongSimulation.StepSeconds);
        clipRaster.Draw(clip.Frame);
        Png.Save(Path.Combine(args[1], $"frame-{frame:D3}.png"), clipRaster.Pixels.Span, 160, 96, false);
    }
    File.WriteAllText(Path.Combine(args[1], "clip.json"), System.Text.Json.JsonSerializer.Serialize(new
    {
        kind = "isolated CPU prototype; not Delta live", frames = 240, fps = 20, durationSeconds = 12,
        simulationHz = 120, seed = clip.Options.Seed, points = clip.Points, lastFrame = clip.Frame
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"PONG clip: 240 C# raster frames, 20 fps, 12 seconds, {clip.Points} points; no live integration");
}
return failures == 0 ? 0 : 1;
