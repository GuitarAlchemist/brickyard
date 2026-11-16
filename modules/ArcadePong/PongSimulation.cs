namespace Delta.ArcadePong;

public sealed record PongOptions(double BallSpeed = 60, double PaddleSpeed = 50, uint Seed = 0xD317A);

public readonly record struct PongFrame(double BallX, double BallY, double VelocityX, double VelocityY,
    double LeftY, double RightY, int LeftScore, int RightScore, bool Paused, int ServeTicks);

// Single-owner CPU simulation. The host supplies elapsed monotonic seconds; no clock, tasks or I/O live here.
public sealed class PongSimulation
{
    public const double StepSeconds = 1.0 / 120;
    private Ball ball;
    private double leftY, rightY, leftTarget, rightTarget, leftError, rightError, accumulator;
    private int leftScore, rightScore, serveTicks, serveDirection, reactionTicks;
    private uint random;
    private bool paused;

    public PongSimulation(PongOptions? options = null)
    {
        options ??= new();
        if (!double.IsFinite(options.BallSpeed) || !double.IsFinite(options.PaddleSpeed))
            throw new ArgumentOutOfRangeException(nameof(options), "Speeds must be finite.");
        Options = options with { BallSpeed = Math.Clamp(options.BallSpeed, 40, 120), PaddleSpeed = Math.Clamp(options.PaddleSpeed, 24, 140) };
        Reset();
    }

    public PongOptions Options { get; }
    public PongFrame Frame => new(ball.X, ball.Y, ball.Vx, ball.Vy, leftY, rightY, leftScore, rightScore, paused, serveTicks);
    public int LastStepCount { get; private set; }
    public ulong Points { get; private set; }
    public ulong Mistakes { get; private set; }

    public void SetPaused(bool value)
    {
        paused = value;
        accumulator = 0;
        LastStepCount = 0;
    }

    public void Reset()
    {
        random = Options.Seed == 0 ? 1u : Options.Seed;
        leftY = rightY = leftTarget = rightTarget = 54;
        leftScore = rightScore = reactionTicks = LastStepCount = 0;
        leftError = rightError = accumulator = 0;
        Points = Mistakes = 0;
        paused = false;
        Serve((Next() & 1) == 0 ? -1 : 1);
    }

    public void Advance(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        LastStepCount = 0;
        if (paused) return;
        // Drop excess stall time instead of keeping an ever-growing catch-up queue.
        accumulator += Math.Min(elapsedSeconds, 0.25);
        LastStepCount = Math.Min(30, (int)Math.Floor((accumulator + 1e-10) / StepSeconds));
        accumulator = Math.Max(0, accumulator - LastStepCount * StepSeconds);
        for (var i = 0; i < LastStepCount; i++) Tick();
    }

    private void Tick()
    {
        if (serveTicks > 0)
        {
            if (--serveTicks == 0)
            {
                var angle = ((Next() % 1001) / 1000.0 - 0.5) * 1.1;
                ball.Vx = serveDirection * Options.BallSpeed * Math.Cos(angle);
                ball.Vy = Options.BallSpeed * Math.Sin(angle);
                ChooseReceiver();
            }
            return;
        }
        // Sample the target every 100 ms; both players have the same finite motion budget.
        if (reactionTicks-- <= 0)
        {
            reactionTicks = 11;
            leftTarget = ball.Vx < 0 ? ball.Y + leftError : 54;
            rightTarget = ball.Vx > 0 ? ball.Y + rightError : 54;
        }
        leftY = Move(leftY, leftTarget);
        rightY = Move(rightY, rightTarget);
        switch (PongPhysics.Step(ref ball, leftY, rightY, Options.BallSpeed))
        {
            case BallEvent.LeftHit:
            case BallEvent.RightHit:
                ChooseReceiver();
                break;
            case BallEvent.LeftPoint:
                leftScore = (leftScore + 1) % 100;
                Points = unchecked(Points + 1);
                Serve(-1);
                break;
            case BallEvent.RightPoint:
                rightScore = (rightScore + 1) % 100;
                Points = unchecked(Points + 1);
                Serve(1);
                break;
        }
    }

    private double Move(double from, double target) => Math.Clamp(from + Math.Clamp(target - from,
        -Options.PaddleSpeed * StepSeconds, Options.PaddleSpeed * StepSeconds), 23, 85);

    private void Serve(int direction)
    {
        ball = new() { X = 80, Y = 54 };
        serveTicks = 90;
        serveDirection = direction;
        reactionTicks = 0;
        leftError = rightError = 0;
    }

    private void ChooseReceiver()
    {
        var error = 0.0;
        if (Next() % 7 == 0)
        {
            error = (Next() & 1) == 0 ? -30 : 30;
            Mistakes = unchecked(Mistakes + 1);
        }
        leftError = ball.Vx < 0 ? error : 0;
        rightError = ball.Vx > 0 ? error : 0;
        reactionTicks = 0;
    }

    private uint Next()
    {
        random ^= random << 13;
        random ^= random >> 17;
        random ^= random << 5;
        return random;
    }
}

internal struct Ball { public double X, Y, Vx, Vy; }
internal enum BallEvent { None, LeftHit, RightHit, LeftPoint, RightPoint }

internal static class PongPhysics
{
    public const double Radius = 2, Top = 16, Bottom = 92, LeftFace = 12, RightFace = 148;
    public const double PaddleHalfHeight = 9;

    public static BallEvent Step(ref Ball ball, double leftY, double rightY, double speed)
    {
        var old = ball;
        ball.X += ball.Vx * PongSimulation.StepSeconds;
        ball.Y += ball.Vy * PongSimulation.StepSeconds;
        Reflect(ref ball);
        var left = old.Vx < 0 && old.X >= LeftFace && ball.X <= LeftFace;
        var right = old.Vx > 0 && old.X <= RightFace && ball.X >= RightFace;
        if (left || right)
        {
            var face = left ? LeftFace : RightFace;
            var fraction = (face - old.X) / (ball.X - old.X);
            var atFace = new Ball { Y = old.Y + old.Vy * PongSimulation.StepSeconds * fraction, Vy = old.Vy };
            Reflect(ref atFace);
            var offset = atFace.Y - (left ? leftY : rightY);
            if (Math.Abs(offset) <= PaddleHalfHeight + Radius)
            {
                // Angle varies with impact, but total speed never increases and horizontal speed stays nonzero.
                var angle = Math.Clamp(offset / PaddleHalfHeight, -1, 1) * 0.95;
                ball.Vx = (left ? 1 : -1) * speed * Math.Cos(angle);
                ball.Vy = speed * Math.Sin(angle);
                var remaining = PongSimulation.StepSeconds * (1 - fraction);
                ball.X = face + ball.Vx * remaining;
                ball.Y = atFace.Y + ball.Vy * remaining;
                Reflect(ref ball);
                return left ? BallEvent.LeftHit : BallEvent.RightHit;
            }
        }
        if (ball.X < -Radius) return BallEvent.RightPoint;
        if (ball.X > 160 + Radius) return BallEvent.LeftPoint;
        return BallEvent.None;
    }

    private static void Reflect(ref Ball ball)
    {
        if (ball.Y < Top) { ball.Y = 2 * Top - ball.Y; ball.Vy = Math.Abs(ball.Vy); }
        if (ball.Y > Bottom) { ball.Y = 2 * Bottom - ball.Y; ball.Vy = -Math.Abs(ball.Vy); }
    }
}
