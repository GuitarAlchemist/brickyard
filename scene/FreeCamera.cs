using SN = System.Numerics;

// Delta's camera. At rest it drifts a little from side to side in front of the robot, at the height of its face, as it
// always has. Moved, it walks the hall as the Observatory's camera does (scripts/fly_camera.gd and walk_physics.gd
// there): ZQSD, WASD or the arrows move a body with inertia at eye height, Shift sprints, Space jumps, C crouches (Ctrl
// too, tapped alone), C while sprinting slides and ends crouched, Q and E (A and E on AZERTY) lean, Alt with the moving
// keys leans or peeks up and down, the body standing, Caps Lock walks at half pace; a landing dips the eye, a sprint
// widens the field, a slide and a lean roll it. The wheel sets the pace, the right button held or Ctrl and a click turn
// the view, F flies (Space or E up, C down). 2 to 5 are views, 1 or Home the drift, and going to any of them is a flight
// after Grand Theft Auto V's character switch, as the Observatory's view_move.gd: the camera lifts, looks down over the
// hall and settles, its field widening on the way. The Observatory's heights are scaled by its eye's, 1.7 m, to this
// one's, 1.1 m; its times are kept. Only the front of the hall is built, so the camera stays between the wall and the
// front of the slabs. Kept apart from the window so that it can be tested alone.
internal sealed class FreeCamera
{
    internal const float WalkHeight = 1.1f;
    internal const float Fov = 43f; // degrees, vertical: the 0.75 radian the scene has always had
    internal const float Scale = WalkHeight / 1.7f; // the Observatory's metres to the hall's
    internal static readonly SN.Vector3 DriftTarget = new(0f, 0.7f, 0f);
    internal static readonly SN.Vector3 Min = new(-6f, 0.25f, -3.1f);
    internal static readonly SN.Vector3 Max = new(6f, 4.2f, 5.5f);
    private const float Sensitivity = 0.0025f; // radians a pixel, the Observatory's
    private const float Run = 3f; // flying

    // The walking body, walk_physics.gd's numbers.
    private const float Sprint = 1.6f; // times the walking pace
    private const float CrouchPace = 0.5f;
    private const float SlowPace = 0.5f; // Caps Lock
    private const float CrouchEye = 1.05f * Scale;
    private const float SlideEye = 0.85f * Scale;
    private const float Gravity = 16f * Scale; // with JumpSpeed, 0.55 m high and 0.65 s in the air
    private const float JumpSpeed = 5.2f * Scale;
    private const float JumpBuffer = 0.12f; // s: a jump pressed this long before landing still happens
    private const float Accel = 7.5f; // walking paces a second: walking speed in 0.13 s on the floor
    private const float AirAccel = 1.67f;
    private const float SlideBoost = 1.15f; // times the sprint, at the start of a slide
    private const float SlideFriction = 1f; // walking paces a second
    private const float SlideTime = 1f;
    private const float FovKick = 6f; // degrees, sprinting; 1.4 times that sliding
    private const float SlideRoll = 0.045f; // radians
    private const float Lean = 0.38f * Scale;
    private const float LeanRoll = 0.12f;
    private const float PeekUp = 0.18f * Scale;
    private const float LandCap = 7f * Scale; // m/s: a landing dips the eye as if from no faster than this

    // The flight to a view, view_move.gd's.
    private const float Tilt = 26f; // degrees the camera looks down at the top of the flight
    private const float Widen = 9f; // degrees the field opens there
    private const int ArcSamples = 48;

    private SN.Vector3 position; // the eye
    private float yaw; // 0 looks towards the wall (-z), positive turns right
    private float pitch;

    private SN.Vector2 body; // the feet on the floor, x and z, without the lean
    private SN.Vector2 velocity;
    private float feet; // above the floor
    private float rise; // m/s up
    private float eye = WalkHeight;
    private float dip, dipSpeed, lean, peek, kick, roll, slide, jumpAsked;
    private bool crouchAsked;

    private Pose drawn;
    private bool haveDrawn;
    private Pose from;
    private float flightTime = -1f;
    private float flightSeconds;

    // What is held this frame: -1, 0 or 1 on each axis; Lean is Q and E's, Alt makes the moving keys lean and peek.
    internal readonly record struct Held(float Forward, float Right, float Up, bool Fast, float Lean = 0f, bool Alt = false);

    // What the window draws: the eye, where it looks, which way is up (a lean or a slide rolls it) and the vertical field
    // in degrees.
    internal readonly record struct Pose(SN.Vector3 Eye, SN.Vector3 Target, SN.Vector3 Up, float Fov)
    {
        public void Deconstruct(out SN.Vector3 eye, out SN.Vector3 target) => (eye, target) = (Eye, Target);
    }

    public bool Free { get; private set; }

    public bool Flying { get; private set; }

    public float Speed { get; private set; } = 1.5f; // metres a second, walking

    public bool Slow { get; private set; }

    public bool Crouched { get; private set; }

    public bool Sliding => slide > 0f;

    public bool Airborne => feet > 0f || rise > 0f;

    public bool InFlight => flightTime >= 0f;

    public bool ReducedMotion { get; set; } // --reduced-motion: cuts instead of flights, no dip, roll or wider field

    public int Jumps { get; private set; }

    public int Slides { get; private set; }

    public int Landings { get; private set; }

    public int Flights { get; private set; }

    public static SN.Vector3 DriftEye(float time) =>
        new(0.45f * MathF.Sin(time * 0.11f), 1.08f + (0.04f * MathF.Sin(time * 0.07f)), 2.55f);

    public Pose View(float time)
    {
        var truth = True(time);
        if (flightTime < 0f || !haveDrawn)
        {
            flightTime = -1f;
            return Remember(truth);
        }

        if (flightSeconds <= 0f)
        {
            flightSeconds = Math.Clamp(1.1f + (0.045f * SN.Vector3.Distance(from.Eye, truth.Eye) / Scale), 1.4f, 2.4f);
        }

        var s = flightTime / flightSeconds;
        if (s >= 1f)
        {
            flightTime = -1f;
            return Remember(truth);
        }

        return Remember(Flight(from, truth, s));
    }

    // The mouse moved by dx, dy pixels while the view is turned.
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

    // Space: a jump, remembered a little before landing.
    public void Jump(float time)
    {
        if (Flying)
        {
            return;
        }

        Take(time);
        jumpAsked = JumpBuffer;
    }

    // C, or Ctrl tapped alone: crouch or stand up; while sprinting, a slide.
    public void Crouch(float time)
    {
        if (Flying)
        {
            return;
        }

        Take(time);
        crouchAsked = true;
    }

    // Caps Lock: walk at half pace, or not.
    public void ToggleSlow() => Slow = !Slow;

    // A frame: the body or the flight moves by what is held, and a flight to a view goes on. dt is real time.
    public void Move(float time, Held held, float dt)
    {
        dt = Math.Clamp(dt, 0f, 0.1f);
        if (flightTime >= 0f)
        {
            flightTime += dt;
        }

        var up = Flying ? held.Up : 0f;
        var quiet = held.Forward == 0 && held.Right == 0 && up == 0 && held.Lean == 0;
        if (!Free && quiet)
        {
            return;
        }

        if (!quiet)
        {
            Take(time);
        }

        if (Flying)
        {
            Fly(held, up, dt);
            Ease(0f, 0f, dt);
            return;
        }

        // In steps of a 60th of a second at most, so that the dip's spring stays stable on a slow frame.
        for (var left = dt; left > 1e-6f; left -= 1f / 60f)
        {
            Walk(held, Math.Min(left, 1f / 60f));
        }
    }

    // One notch of the wheel up walks a fifth faster, as in the Observatory.
    public void Wheel(double notches) => Speed = Math.Clamp(Speed * MathF.Pow(1.2f, (float)notches), 0.3f, 8f);

    public void ToggleFlying(float time)
    {
        Take(time);
        Flying = !Flying;
        if (!Flying)
        {
            // Landing from the air falls to the floor, as a jump does.
            body = new(position.X, position.Z);
            velocity = SN.Vector2.Zero;
            eye = WalkHeight;
            feet = Math.Max(0f, position.Y - WalkHeight);
            rise = 0f;
            Crouched = false;
            slide = 0f;
        }
    }

    public void Reset()
    {
        StartFlight();
        Free = false;
        Flying = false;
        Settle();
    }

    // The views the keys 2 to 5 go to, as the Observatory's presets; 1 is the drift. Above eye height, the camera flies.
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

        StartFlight();
        var (eye, target) = Views[view - 2];
        var d = SN.Vector3.Normalize(target - eye);
        yaw = MathF.Atan2(d.X, -d.Z);
        pitch = MathF.Asin(d.Y);
        position = SN.Vector3.Clamp(eye, Min, Max);
        Flying = MathF.Abs(position.Y - WalkHeight) > 0.01f;
        Free = true;
        Settle();
        return true;
    }

    private Pose Remember(Pose pose)
    {
        drawn = pose;
        haveDrawn = true;
        return pose;
    }

    // The pose without a flight: the drift, or the body's eye with its roll and its field.
    private Pose True(float time)
    {
        if (!Free)
        {
            return new(DriftEye(time), DriftTarget, SN.Vector3.UnitY, Fov);
        }

        var facing = Facing();
        var right = SN.Vector3.Normalize(SN.Vector3.Cross(facing, SN.Vector3.UnitY));
        var up = SN.Vector3.Cross(right, facing);
        return new(position, position + facing, (up * MathF.Cos(roll)) - (right * MathF.Sin(roll)), Fov + kick);
    }

    // From wherever the picture is now, the last view or the middle of a flight; its length is measured at the next frame,
    // once the destination is known.
    private void StartFlight()
    {
        if (ReducedMotion || !haveDrawn)
        {
            flightTime = -1f;
            return;
        }

        from = drawn;
        flightTime = 0f;
        flightSeconds = 0f;
        Flights++;
    }

    // A body standing still where the camera now is.
    private void Settle()
    {
        body = new(position.X, position.Z);
        velocity = SN.Vector2.Zero;
        feet = 0f;
        rise = 0f;
        eye = WalkHeight;
        dip = dipSpeed = lean = peek = kick = roll = slide = jumpAsked = 0f;
        crouchAsked = false;
        Crouched = false;
    }

    private void Fly(Held held, float up, float dt)
    {
        var direction = (Facing() * held.Forward) + (new SN.Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw)) * held.Right) + (SN.Vector3.UnitY * up);
        if (direction.LengthSquared() < 1e-6f)
        {
            return;
        }

        var step = SN.Vector3.Normalize(direction) * Speed * (held.Fast ? Run : 1f) * dt;
        position = SN.Vector3.Clamp(position + step, Min, Max);
    }

    private void Walk(Held held, float dt)
    {
        var walk = Speed;
        var forward = new SN.Vector2(MathF.Sin(yaw), -MathF.Cos(yaw));
        var right = new SN.Vector2(MathF.Cos(yaw), MathF.Sin(yaw));
        var intent = (forward * held.Forward) + (right * held.Right);

        // Alt: the moving keys lean and peek. Q and E lean. Leaning or peeking, the body stands.
        var leanTo = held.Lean;
        var peekTo = 0f;
        if (held.Alt)
        {
            leanTo = held.Right;
            peekTo = held.Forward;
            intent = SN.Vector2.Zero;
        }

        if (leanTo != 0f)
        {
            intent = SN.Vector2.Zero;
        }

        var sprinting = held.Fast && intent.LengthSquared() > 0.01f;
        var speed = velocity.Length();
        var grounded = feet <= 0f && rise <= 0f;
        if (crouchAsked)
        {
            crouchAsked = false;
            if (!Crouched && sprinting && grounded && speed > walk * 1.2f)
            {
                slide = SlideTime;
                velocity = SN.Vector2.Normalize(velocity) * Math.Max(speed, walk * Sprint) * SlideBoost;
                Slides++;
            }
            else
            {
                slide = 0f;
            }

            Crouched = !Crouched;
        }
        else if (sprinting && Crouched && slide <= 0f)
        {
            Crouched = false; // sprinting stands up
        }

        if (jumpAsked > 0f && grounded)
        {
            jumpAsked = 0f;
            rise = JumpSpeed;
            feet = Math.Max(feet, 0.001f);
            Crouched = false; // a jump out of a slide keeps its speed
            slide = 0f;
            grounded = false;
            Jumps++;
        }

        jumpAsked = Math.Max(0f, jumpAsked - dt);

        // Across the floor: the keys say where to go, the body how fast.
        var wish = intent.LengthSquared() > 1e-4f ? SN.Vector2.Normalize(intent) : SN.Vector2.Zero;
        var pace = sprinting ? Sprint : Slow ? SlowPace : 1f;
        var target = wish * walk * pace * (Crouched ? CrouchPace : 1f);
        if (slide > 0f)
        {
            slide -= dt;
            speed = Math.Max(0f, velocity.Length() - (SlideFriction * walk * dt));
            velocity = speed > 0f ? SN.Vector2.Normalize(velocity) * speed : SN.Vector2.Zero;
            if (speed < walk || slide <= 0f)
            {
                slide = 0f;
            }
        }
        else if (grounded)
        {
            velocity = Toward(velocity, target, Accel * walk * dt);
        }
        else if (wish != SN.Vector2.Zero)
        {
            velocity = Toward(velocity, target, AirAccel * walk * dt);
        }

        var next = body + (velocity * dt);
        var inside = SN.Vector2.Clamp(next, new(Min.X, Min.Z), new(Max.X, Max.Z));
        if (inside.X != next.X)
        {
            velocity.X = 0f; // a wall stops the body
        }

        if (inside.Y != next.Y)
        {
            velocity.Y = 0f;
        }

        body = inside;

        // Up and down: a jump or a fall, then gravity; the eye lowers for a crouch or a slide.
        if (!grounded)
        {
            rise -= Gravity * dt;
            feet += rise * dt;
            if (feet <= 0f)
            {
                feet = 0f;
                if (!ReducedMotion)
                {
                    dipSpeed -= Math.Min(MathF.Abs(rise), LandCap) * 0.5f;
                }

                rise = 0f;
                Landings++;
            }
        }

        var eyeTo = slide > 0f ? SlideEye : Crouched ? CrouchEye : WalkHeight;
        eye += (eyeTo - eye) * (1f - MathF.Exp(-14f * dt));

        // The landing dip is a critically damped spring back to 0.
        dipSpeed += ((-120f * dip) - (22f * dipSpeed)) * dt;
        dip += dipSpeed * dt;

        lean += (leanTo - lean) * (1f - MathF.Exp(-12f * dt));
        peek += (peekTo - peek) * (1f - MathF.Exp(-12f * dt));
        if (MathF.Abs(lean) < 0.001f && leanTo == 0f)
        {
            lean = 0f;
        }

        if (MathF.Abs(peek) < 0.001f && peekTo == 0f)
        {
            peek = 0f;
        }

        var off = right * Lean * lean;
        var peekBy = peek > 0f ? PeekUp * peek : (WalkHeight - CrouchEye) * peek;
        position = SN.Vector3.Clamp(new(body.X + off.X, feet + eye + dip + peekBy, body.Y + off.Y), Min, Max);

        var kickTo = slide > 0f ? FovKick * 1.4f : sprinting && velocity.Length() > walk * 1.1f ? FovKick : 0f;
        Ease(kickTo, (-LeanRoll * lean) + (slide > 0f ? SlideRoll : 0f), dt);
    }

    // The field and the roll, eased; under reduced motion only the lean's roll, which the player asks for.
    private void Ease(float kickTo, float rollTo, float dt)
    {
        if (ReducedMotion)
        {
            kickTo = 0f;
            rollTo = -LeanRoll * lean;
        }

        kick += (kickTo - kick) * (1f - MathF.Exp(-8f * dt));
        if (MathF.Abs(kick - kickTo) < 0.02f)
        {
            kick = kickTo;
        }

        roll += (rollTo - roll) * (1f - MathF.Exp(-10f * dt));
        if (MathF.Abs(roll - rollTo) < 0.0005f)
        {
            roll = rollTo;
        }
    }

    private static SN.Vector2 Toward(SN.Vector2 from, SN.Vector2 to, float by)
    {
        var d = to - from;
        var length = d.Length();
        return length <= by || length < 1e-6f ? to : from + (d / length * by);
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
        Settle();
    }

    // The pose s of the way (0 to 1, in time) from a to b: a cubic lifted above the straight line, eased along its
    // length, the view turning from one to the other and looking down most at the top.
    private static Pose Flight(Pose a, Pose b, float s)
    {
        var d = SN.Vector3.Distance(a.Eye, b.Eye);
        var lift = Math.Clamp(0.3f * d, 1.5f * Scale, 6f * Scale);
        var c0 = a.Eye + (SN.Vector3.UnitY * lift);
        var c1 = b.Eye + (SN.Vector3.UnitY * lift);
        c0.Y = Math.Min(c0.Y, Math.Max(a.Eye.Y, Max.Y));
        c1.Y = Math.Min(c1.Y, Math.Max(b.Eye.Y, Max.Y));
        var p = Bezier(a.Eye, c0, c1, b.Eye, Along(a.Eye, c0, c1, b.Eye, Smoother(s)));
        var turn = Smoother(Math.Clamp((s - 0.04f) / 0.92f, 0f, 1f));
        var bow = MathF.Pow(MathF.Sin(MathF.PI * s), 2f);
        var q = SN.Quaternion.Slerp(Turn(a), Turn(b), turn) * SN.Quaternion.CreateFromAxisAngle(SN.Vector3.UnitX, -Tilt * MathF.PI / 180f * bow);
        var facing = SN.Vector3.Transform(-SN.Vector3.UnitZ, q);
        var up = SN.Vector3.Transform(SN.Vector3.UnitY, q);
        return new(p, p + facing, up, a.Fov + ((b.Fov - a.Fov) * Smoother(s)) + (Widen * bow));
    }

    // A pose's turn: the camera's own axes, x right, y up, -z ahead, as rows.
    private static SN.Quaternion Turn(Pose pose)
    {
        var f = SN.Vector3.Normalize(pose.Target - pose.Eye);
        var r = SN.Vector3.Normalize(SN.Vector3.Cross(f, pose.Up));
        var u = SN.Vector3.Cross(r, f);
        var m = new SN.Matrix4x4(r.X, r.Y, r.Z, 0f, u.X, u.Y, u.Z, 0f, -f.X, -f.Y, -f.Z, 0f, 0f, 0f, 0f, 1f);
        return SN.Quaternion.Normalize(SN.Quaternion.CreateFromRotationMatrix(m));
    }

    private static SN.Vector3 Bezier(SN.Vector3 p0, SN.Vector3 p1, SN.Vector3 p2, SN.Vector3 p3, float u)
    {
        var v = 1f - u;
        return (p0 * v * v * v) + (p1 * 3f * v * v * u) + (p2 * 3f * v * u * u) + (p3 * u * u * u);
    }

    // The cubic's parameter at the fraction f of its length: paced by its parameter alone, the flight raced through its
    // middle.
    private static float Along(SN.Vector3 p0, SN.Vector3 p1, SN.Vector3 p2, SN.Vector3 p3, float f)
    {
        Span<float> lengths = stackalloc float[ArcSamples + 1];
        var previous = p0;
        var total = 0f;
        for (var k = 1; k <= ArcSamples; k++)
        {
            var q = Bezier(p0, p1, p2, p3, (float)k / ArcSamples);
            total += SN.Vector3.Distance(previous, q);
            lengths[k] = total;
            previous = q;
        }

        if (total <= 0f)
        {
            return f;
        }

        var want = Math.Clamp(f, 0f, 1f) * total;
        for (var k = 1; k <= ArcSamples; k++)
        {
            if (lengths[k] >= want)
            {
                var span = lengths[k] - lengths[k - 1];
                var w = span > 0f ? (want - lengths[k - 1]) / span : 0f;
                return (k - 1 + w) / ArcSamples;
            }
        }

        return 1f;
    }

    internal static float Smoother(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * x * ((x * ((6f * x) - 15f)) + 10f);
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
        Check(camera.Free && SN.Vector3.Distance(eye, driftEye) < 0.03f && SN.Vector3.Distance(SN.Vector3.Normalize(target - eye), expected) < 1e-3f,
            "taking the camera starts where the drift stood, looking where it looked");

        camera = new FreeCamera();
        camera.Look(0f, 1e-9, 0); // from (0, 1.08, 2.55), nearly straight at the wall
        for (var i = 0; i < 10; i++)
        {
            camera.Move(0f, new(1, 0, 0, false), 0.1f);
        }

        (eye, _) = camera.View(0f);
        Check(eye.Z < 2.55f - 0.08f && MathF.Abs(eye.Y - WalkHeight) < 1e-3f, "forward walks towards the wall, at eye height");

        var before = eye;
        for (var i = 0; i < 6; i++)
        {
            camera.Move(0f, new(0, 1, 0, false), 0.05f);
        }

        (eye, _) = camera.View(0f);
        Check(eye.X > before.X, "right steps to the right");

        camera.Look(0f, 0, 400);
        camera.Move(0f, new(1, 0, 0, false), 0.05f);
        var (walked, _) = camera.View(0f);
        Check(MathF.Abs(walked.Y - WalkHeight) < 1e-3f, "looking down while walking keeps the eye height");

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
        for (var i = 0; i < 40; i++)
        {
            camera.Move(0f, default, 0.05f);
        }

        (eye, _) = camera.View(0f);
        Check(!camera.Flying && MathF.Abs(eye.Y - WalkHeight) < 1e-3f && camera.Landings == 1, "landing falls back to eye height");

        camera.Look(0f, 0, -1e6);
        (eye, target) = camera.View(0f);
        Check(SN.Vector3.Normalize(target - eye).Y < 0.9999f, "the view never turns straight up, so the look-at stays defined");

        var pace = camera.Speed;
        camera.Wheel(1);
        Check(MathF.Abs(camera.Speed - (pace * 1.2f)) < 1e-4f, "the wheel sets the pace");
        camera.Wheel(100);
        Check(camera.Speed == 8f, "the pace is bounded");

        camera.Reset();
        camera.View(5f);
        camera.Move(5f, default, 0.1f);
        for (var i = 0; i < 30; i++)
        {
            camera.Move(5f, default, 0.1f);
        }

        (eye, target) = camera.View(5f);
        Check(!camera.Free && eye == DriftEye(5f) && target == DriftTarget, "Home goes back to the drift");

        var views = true;
        for (var v = 2; v <= Views.Length + 1; v++)
        {
            views &= camera.GoTo(v);
            camera.View(0f);
            for (var i = 0; i < 30; i++)
            {
                camera.Move(0f, default, 0.1f);
            }

            (eye, target) = camera.View(0f);
            var (wantEye, wantTarget) = Views[v - 2];
            views &= SN.Vector3.Distance(eye, wantEye) < 1e-4f && SN.Vector3.Distance(SN.Vector3.Normalize(target - eye), SN.Vector3.Normalize(wantTarget - wantEye)) < 1e-4f;
            views &= eye.X >= Min.X && eye.X <= Max.X && eye.Y >= Min.Y && eye.Y <= Max.Y && eye.Z >= Min.Z && eye.Z <= Max.Z;
        }

        Check(views && camera.Flying && camera.GoTo(1) && !camera.Free && !camera.GoTo(6) && !camera.GoTo(0), "the views stand where they say, inside the hall; 1 is the drift");

        SelfTestBody(Check);
        SelfTestFlight(Check);
    }

    private static void SelfTestBody(Action<bool, string> Check)
    {
        // A jump: up about 0.55 m, down in about 0.65 s, and a dip on landing that springs back.
        var camera = new FreeCamera();
        camera.Look(0f, 1e-9, 0);
        for (var i = 0; i < 30; i++)
        {
            camera.Move(0f, default, 1f / 60f);
        }

        var top = 0f;
        var air = 0f;
        var deepest = 0f;
        camera.Jump(0f);
        for (var i = 0; i < 120; i++)
        {
            camera.Move(0f, default, 1f / 60f);
            var (e, _) = camera.View(0f);
            top = Math.Max(top, e.Y - WalkHeight);
            deepest = Math.Min(deepest, e.Y - WalkHeight);
            air += camera.Airborne ? 1f / 60f : 0f;
        }

        var (rest, _) = camera.View(0f);
        Check(camera.Jumps == 1 && MathF.Abs(top - (0.845f * Scale)) < 0.03f && MathF.Abs(air - 0.65f) < 0.05f,
            $"Space jumps {0.845f * Scale:F2} m high for 0.65 s, the Observatory's jump scaled (top {top:F3}, air {air:F2})");
        Check(deepest < -0.01f && MathF.Abs(rest.Y - WalkHeight) < 1e-3f, "a landing dips the eye, then it springs back");

        // A crouch lowers the eye and slows the walk; C again stands.
        camera.Crouch(0f);
        for (var i = 0; i < 60; i++)
        {
            camera.Move(0f, default, 1f / 60f);
        }

        var (low, _) = camera.View(0f);
        Check(camera.Crouched && MathF.Abs(low.Y - CrouchEye) < 0.01f, "C crouches to the crouch's eye");
        var (start, _) = camera.View(0f);
        for (var i = 0; i < 60; i++)
        {
            camera.Move(0f, new(1, 0, 0, false), 1f / 60f);
        }

        var (crept, _) = camera.View(0f);
        var creep = SN.Vector3.Distance(new(start.X, 0, start.Z), new(crept.X, 0, crept.Z));
        Check(creep > camera.Speed * CrouchPace * 0.8f && creep < camera.Speed * CrouchPace * 1.02f, $"crouched, the body walks at half pace ({creep:F2} m in 1 s)");
        camera.Crouch(0f);
        for (var i = 0; i < 60; i++)
        {
            camera.Move(0f, default, 1f / 60f);
        }

        // A slide: C while sprinting rushes on, lower, rolled and wider, then ends crouched. Back from the wall first.
        camera.GoTo(1);
        camera.Look(0f, 1e-9, 0);
        for (var i = 0; i < 30; i++)
        {
            camera.Move(0f, new(1, 0, 0, true), 1f / 60f);
        }

        var sprintPose = camera.View(0f);
        camera.Crouch(0f);
        var lowest = float.MaxValue;
        var wide = 0f;
        var rolled = 0f;
        for (var i = 0; i < 40; i++)
        {
            camera.Move(0f, new(1, 0, 0, true), 1f / 60f);
            var pose = camera.View(0f);
            lowest = Math.Min(lowest, pose.Eye.Y);
            wide = Math.Max(wide, pose.Fov);
            rolled = Math.Max(rolled, MathF.Abs(pose.Up.X) + MathF.Abs(pose.Up.Z));
        }

        Check(sprintPose.Fov > Fov + 1f, $"a sprint widens the field ({sprintPose.Fov:F1}°)");
        Check(camera.Slides == 1 && lowest < CrouchEye && wide > Fov + 6.5f && rolled > 0.01f, $"C while sprinting slides: lower, wider and rolled (eye {lowest:F2} m, field {wide:F1}°)");
        for (var i = 0; i < 90; i++)
        {
            camera.Move(0f, default, 1f / 60f);
        }

        Check(!camera.Sliding && camera.Crouched, "the slide ends crouched");

        // Leaning: Q and E, or Alt with the moving keys, the body standing still.
        camera = new FreeCamera();
        camera.Look(0f, 1e-9, 0);
        camera.Move(0f, default, 0.5f);
        var (stood, _) = camera.View(0f);
        for (var i = 0; i < 60; i++)
        {
            camera.Move(0f, new(0, 0, 0, false, Lean: 1f), 1f / 60f);
        }

        var leaned = camera.View(0f);
        Check(leaned.Eye.X - stood.X > Lean * 0.9f && leaned.Up.X > 0.05f && MathF.Abs(leaned.Eye.Z - stood.Z) < 0.01f, "E leans right, rolled, without stepping");
        for (var i = 0; i < 60; i++)
        {
            camera.Move(0f, new(1, -1, 0, false, Alt: true), 1f / 60f);
        }

        var peeked = camera.View(0f);
        Check(stood.X - peeked.Eye.X > Lean * 0.9f && peeked.Eye.Y > stood.Y + (PeekUp * 0.9f) && MathF.Abs(peeked.Eye.Z - stood.Z) < 0.01f,
            "Alt with W and A leans left and peeks up, the body standing");
        for (var i = 0; i < 90; i++)
        {
            camera.Move(0f, default, 1f / 60f);
        }

        var (back, _) = camera.View(0f);
        Check(SN.Vector3.Distance(back, stood) < 1e-3f, "letting go stands up straight again");

        camera.ToggleSlow();
        var (slowStart, _) = camera.View(0f);
        for (var i = 0; i < 60; i++)
        {
            camera.Move(0f, new(1, 0, 0, false), 1f / 60f);
        }

        var (slowEnd, _) = camera.View(0f);
        var slowWay = SN.Vector3.Distance(slowStart, slowEnd);
        Check(camera.Slow && slowWay < camera.Speed * SlowPace * 1.02f && slowWay > camera.Speed * SlowPace * 0.8f, $"Caps Lock walks at half pace ({slowWay:F2} m in 1 s)");

        camera.ReducedMotion = true;
        camera.ToggleSlow();
        for (var i = 0; i < 60; i++)
        {
            camera.Move(0f, new(-1, 0, 0, true), 1f / 60f);
        }

        Check(camera.View(0f).Fov == Fov, "under reduced motion a sprint keeps the field");
    }

    private static void SelfTestFlight(Action<bool, string> Check)
    {
        // A view is a flight: it lifts above both ends, looks down and widens at its middle, and lands exactly.
        var camera = new FreeCamera();
        var start = camera.View(0f);
        camera.GoTo(2);
        var first = camera.View(0f);
        Check(camera.InFlight && SN.Vector3.Distance(first.Eye, start.Eye) < 1e-3f, "going to a view starts from the picture as it was");
        var highest = float.MinValue;
        var widest = 0f;
        var lowestLook = 1f;
        var seconds = 0f;
        while (camera.InFlight && seconds < 5f)
        {
            camera.Move(0f, default, 1f / 60f);
            seconds += 1f / 60f;
            var pose = camera.View(0f);
            highest = Math.Max(highest, pose.Eye.Y);
            widest = Math.Max(widest, pose.Fov);
            lowestLook = Math.Min(lowestLook, SN.Vector3.Normalize(pose.Target - pose.Eye).Y);
        }

        var (eye, _) = camera.View(0f);
        Check(seconds is >= 1.39f and <= 2.45f, $"a flight lasts 1.4 to 2.4 s ({seconds:F2} s)");
        Check(highest > Math.Max(start.Eye.Y, Views[0].Eye.Y) + 0.3f && widest > Fov + 6f && lowestLook < -0.45f,
            $"it lifts, looks down and widens on the way (top {highest:F2} m, field {widest:F1}°, look {lowestLook:F2})");
        Check(SN.Vector3.Distance(eye, Views[0].Eye) < 1e-4f, "and lands on the view");

        camera.GoTo(4);
        camera.View(0f);
        camera.Move(0f, default, 0.1f);
        camera.Move(0f, default, 0.1f);
        var mid = camera.View(0f);
        camera.GoTo(3);
        var turned = camera.View(0f);
        Check(camera.InFlight && SN.Vector3.Distance(mid.Eye, turned.Eye) < 1e-3f, "a new view in mid-flight starts from where the picture is");

        camera = new FreeCamera { ReducedMotion = true };
        camera.View(0f);
        camera.GoTo(3);
        Check(!camera.InFlight && SN.Vector3.Distance(camera.View(0f).Eye, Views[1].Eye) < 1e-4f, "under reduced motion a view is a cut");
    }
}
