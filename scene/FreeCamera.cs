using SN = System.Numerics;

// Delta's camera. At rest it drifts a little from side to side in front of the robot, at the height of its face, as it
// always has. Moved, it walks the hall as the Observatory's camera does (scripts/fly_camera.gd there): ZQSD, WASD or the
// arrows move at eye height, Shift runs, the wheel sets the pace, the right button held turns the view, F flies (Space or
// E up, C down), 2 to 5 jump to views. Home or 1 goes back to the drift. Only the front of the hall is built, so the camera stays between the wall
// and the front of the slabs. Kept apart from the window so that it can be tested alone.
internal sealed class FreeCamera
{
    internal const float WalkHeight = 1.1f;
    internal static readonly SN.Vector3 DriftTarget = new(0f, 0.7f, 0f);
    internal static readonly SN.Vector3 Min = new(-6f, 0.25f, -3.1f);
    internal static readonly SN.Vector3 Max = new(6f, 4.2f, 5.5f);
    private const float Sensitivity = 0.0025f; // radians a pixel, the Observatory's
    private const float Run = 3f;

    private SN.Vector3 position;
    private float yaw; // 0 looks towards the wall (-z), positive turns right
    private float pitch;

    // What is held this frame: -1, 0 or 1 on each axis.
    internal readonly record struct Held(float Forward, float Right, float Up, bool Fast);

    public bool Free { get; private set; }

    public bool Flying { get; private set; }

    public float Speed { get; private set; } = 1.5f; // metres a second, walking

    public static SN.Vector3 DriftEye(float time) =>
        new(0.45f * MathF.Sin(time * 0.11f), 1.08f + (0.04f * MathF.Sin(time * 0.07f)), 2.55f);

    public (SN.Vector3 Eye, SN.Vector3 Target) View(float time) =>
        Free ? (position, position + Facing()) : (DriftEye(time), DriftTarget);

    // The mouse moved by dx, dy pixels with the right button held.
    public void Look(float time, double dx, double dy)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }

        Take(time);
        yaw += (float)dx * Sensitivity;
        pitch = Math.Clamp(pitch - ((float)dy * Sensitivity), -1.5f, 1.5f);
    }

    public void Move(float time, Held held, float dt)
    {
        var up = Flying ? held.Up : 0f;
        if (held.Forward == 0 && held.Right == 0 && up == 0)
        {
            return;
        }

        Take(time);
        var forward = Flying ? Facing() : new SN.Vector3(MathF.Sin(yaw), 0f, -MathF.Cos(yaw));
        var right = new SN.Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        var direction = (forward * held.Forward) + (right * held.Right) + (SN.Vector3.UnitY * up);
        if (direction.LengthSquared() < 1e-6f)
        {
            return;
        }

        var step = SN.Vector3.Normalize(direction) * Speed * (held.Fast ? Run : 1f) * Math.Min(dt, 0.1f);
        var next = SN.Vector3.Clamp(position + step, Min, Max);
        position = Flying ? next : next with { Y = WalkHeight };
    }

    // One notch of the wheel up walks a fifth faster, as in the Observatory.
    public void Wheel(double notches) => Speed = Math.Clamp(Speed * MathF.Pow(1.2f, (float)notches), 0.3f, 8f);

    public void ToggleFlying(float time)
    {
        Take(time);
        Flying = !Flying;
        if (!Flying)
        {
            position.Y = WalkHeight;
        }
    }

    public void Reset()
    {
        Free = false;
        Flying = false;
    }

    // The views the keys 2 to 5 jump to, as the Observatory's presets; 1 is the drift. Above eye height, the camera flies.
    internal static readonly (SN.Vector3 Eye, SN.Vector3 Target)[] Views =
    [
        (new(-0.95f, 1.35f, 1.75f), new(-1.1f, 0.45f, -0.1f)), // the agents' office
        (new(1.75f, 1.2f, 1.55f), new(0.95f, 0.85f, 0.1f)), // Gaia
        (new(3.4f, 3.1f, 5.2f), new(0f, 0.6f, -0.6f)), // the whole hall
        (new(0f, 1.75f, 0.6f), new(0f, 1.78f, -3.3f)), // the clocks of Ottawa and Paris
    ];

    // 1 for the drift, 2 to 5 for the views; false for any other number.
    public bool GoTo(int view)
    {
        if (view == 1)
        {
            Reset();
            return true;
        }

        if (view < 2 || view > Views.Length + 1)
        {
            return false;
        }

        var (eye, target) = Views[view - 2];
        var d = SN.Vector3.Normalize(target - eye);
        yaw = MathF.Atan2(d.X, -d.Z);
        pitch = MathF.Asin(d.Y);
        position = SN.Vector3.Clamp(eye, Min, Max);
        Flying = MathF.Abs(position.Y - WalkHeight) > 0.01f;
        Free = true;
        return true;
    }

    private SN.Vector3 Facing() =>
        new(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));

    // Leaves the drift where it stands, looking where it looked, so that taking the camera does not jump.
    private void Take(float time)
    {
        if (Free)
        {
            return;
        }

        var eye = DriftEye(time);
        var d = SN.Vector3.Normalize(DriftTarget - eye);
        yaw = MathF.Atan2(d.X, -d.Z);
        pitch = MathF.Asin(d.Y);
        position = eye;
        Free = true;
    }

    internal static void SelfTest(Action<bool, string> Check)
    {
        var camera = new FreeCamera();
        var (driftEye, driftTarget) = camera.View(3f);
        Check(!camera.Free && driftEye == DriftEye(3f) && driftTarget == DriftTarget, "at rest the camera drifts as before");

        camera.Look(3f, 0, 0);
        Check(!camera.Free, "no movement leaves the drift alone");

        camera.Look(3f, 1e-9, 0);
        var (eye, target) = camera.View(3f);
        var expected = SN.Vector3.Normalize(DriftTarget - driftEye);
        Check(camera.Free && SN.Vector3.Distance(eye, driftEye) < 1e-4f && SN.Vector3.Distance(SN.Vector3.Normalize(target - eye), expected) < 1e-3f,
            "taking the camera starts where the drift stood, looking where it looked");

        camera = new FreeCamera();
        camera.Look(0f, 1e-9, 0); // from (0, 1.08, 2.55), nearly straight at the wall
        camera.Move(0f, new(1, 0, 0, false), 1f);
        (eye, _) = camera.View(0f);
        Check(eye.Z < 2.55f - 0.08f && MathF.Abs(eye.Y - WalkHeight) < 1e-4f, "forward walks towards the wall, at eye height");

        var before = eye;
        camera.Move(0f, new(0, 1, 0, false), 0.05f);
        (eye, _) = camera.View(0f);
        Check(eye.X > before.X, "right steps to the right");

        camera.Look(0f, 0, 400);
        camera.Move(0f, new(1, 0, 0, false), 0.05f);
        var (walked, _) = camera.View(0f);
        Check(MathF.Abs(walked.Y - WalkHeight) < 1e-4f, "looking down while walking keeps the eye height");

        for (var i = 0; i < 200; i++)
        {
            camera.Move(0f, new(1, 0, 0, true), 0.1f);
        }

        (eye, _) = camera.View(0f);
        Check(eye.Z >= Min.Z - 1e-4f, "the wall stops the camera");

        camera.ToggleFlying(0f);
        for (var i = 0; i < 200; i++)
        {
            camera.Move(0f, new(0, 0, 1, true), 0.1f);
        }

        (eye, _) = camera.View(0f);
        Check(camera.Flying && MathF.Abs(eye.Y - Max.Y) < 1e-4f, "flying rises to the roof's height and no higher");

        camera.ToggleFlying(0f);
        (eye, _) = camera.View(0f);
        Check(!camera.Flying && MathF.Abs(eye.Y - WalkHeight) < 1e-4f, "landing comes back to eye height");

        camera.Look(0f, 0, -1e6);
        (eye, target) = camera.View(0f);
        Check(SN.Vector3.Normalize(target - eye).Y < 0.9999f, "the view never turns straight up, so the look-at stays defined");

        var pace = camera.Speed;
        camera.Wheel(1);
        Check(MathF.Abs(camera.Speed - (pace * 1.2f)) < 1e-4f, "the wheel sets the pace");
        camera.Wheel(100);
        Check(camera.Speed == 8f, "the pace is bounded");

        camera.Reset();
        (eye, target) = camera.View(5f);
        Check(!camera.Free && eye == DriftEye(5f) && target == DriftTarget, "Home goes back to the drift");

        var views = true;
        for (var v = 2; v <= Views.Length + 1; v++)
        {
            views &= camera.GoTo(v);
            (eye, target) = camera.View(0f);
            var (wantEye, wantTarget) = Views[v - 2];
            views &= SN.Vector3.Distance(eye, wantEye) < 1e-4f && SN.Vector3.Distance(SN.Vector3.Normalize(target - eye), SN.Vector3.Normalize(wantTarget - wantEye)) < 1e-4f;
            views &= eye.X >= Min.X && eye.X <= Max.X && eye.Y >= Min.Y && eye.Y <= Max.Y && eye.Z >= Min.Z && eye.Z <= Max.Z;
        }

        Check(views && camera.Flying && camera.GoTo(1) && !camera.Free && !camera.GoTo(6) && !camera.GoTo(0), "the views stand where they say, inside the hall; 1 is the drift");
    }
}
