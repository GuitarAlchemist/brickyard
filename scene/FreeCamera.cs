using SN = System.Numerics;

// Delta's camera. At rest it drifts a little from side to side in front of the robot, at the height of its face, as it
// always has. Moved, it walks the hall as the Observatory's camera does (scripts/fly_camera.gd and walk_physics.gd
// there): ZQSD, WASD or the arrows move a body with inertia at eye height, Shift sprints, Space jumps, C crouches (Ctrl
// too, tapped alone), C while sprinting slides and ends crouched, Q and E (A and E on AZERTY) lean, Alt with the moving
// keys leans or peeks up and down, the body standing, Caps Lock walks at half pace; a landing dips the eye, a sprint
// widens the field, a slide and a lean roll it. The wheel sets the pace, the right button held or Ctrl and a click turn
// the view, F flies (Space or E up, C down). 2 to 7 are views, 1 or Home the drift, and going to any of them is a flight
// after Grand Theft Auto V's character switch, as the Observatory's view_move.gd: the camera lifts, looks down over the
// hall and settles, its field widening on the way. The Observatory's heights are scaled by its eye's, 1.7 m, to this
// one's, 1.1 m; its times are kept. Only the front of the hall is built, so the camera stays between the wall and the
// front of the slabs. The body walks up the stair to the knots' floor and stands there (KnotFloor.Solids, GroundAt).
// Kept apart from the window so that it can be tested alone.
internal sealed class FreeCamera
{
    internal const float WalkHeight = 1.1f;
    internal const float Fov = 43f; // degrees, vertical: the 0.75 radian the scene has always had
    internal const float Scale = WalkHeight / 1.7f; // the Observatory's metres to the hall's
    internal static readonly SN.Vector3 DriftTarget = new(0f, 0.7f, 0f);
    internal static readonly SN.Vector3 Min = new(-6f, 0.25f, -3.1f);
    internal static readonly SN.Vector3 Max = new(6f, 4.2f, 5.5f);

    // Out through the roll-up door when it is open (StreetDoor): the opening, overlapping the hall and the street, and
    // the streets of Ottawa beyond it (OttawaStreets), where one may fly up to see the city.
    private static readonly (SN.Vector3 Min, SN.Vector3 Max) Doorway =
        (new(HallShell.DoorX - (HallShell.DoorWidth / 2) + 0.35f, Min.Y, Max.Z - 0.5f), new(HallShell.DoorX + (HallShell.DoorWidth / 2) - 0.35f, HallShell.DoorHeight - 0.3f, HallShell.Front + 0.9f));

    // Up to 150 m over them, where the drone may climb (Rides).
    private static readonly (SN.Vector3 Min, SN.Vector3 Max) Streets = (new(-400f, Min.Y, HallShell.Front + 0.6f), new(400f, 150f * Scale, 400f));

    // Whether the door lets the body through; set by the scene each frame.
    internal bool DoorOpen { get; set; }

    // A building of the streets, its footprint (x, z) and the box round it; the walking body stays Radius out of it.
    internal sealed record Footprint(SN.Vector2[] Ring, SN.Vector2 Min, SN.Vector2 Max)
    {
        internal static Footprint Of(SN.Vector2[] ring) =>
            new(ring, new(ring.Min(p => p.X), ring.Min(p => p.Y)), new(ring.Max(p => p.X), ring.Max(p => p.Y)));
    }

    // The streets' buildings, once they are loaded (OttawaStreets).
    internal IReadOnlyList<Footprint> Footprints { get; set; } = [];

    // Whether the eye is out in the streets.
    internal bool Outdoors => position.Z > HallShell.Front;

    // Where a move from one place to another ends: anywhere in a space that holds both (the hall, the doorway, the
    // streets when the door is open); else at the nearest place in the space it started from; anywhere it is to go when
    // it started outside them all (a view's cut).
    internal SN.Vector3 Confine(SN.Vector3 from, SN.Vector3 to)
    {
        if (!DoorOpen) return SN.Vector3.Clamp(to, Min, Max);
        ReadOnlySpan<(SN.Vector3 Min, SN.Vector3 Max)> spaces = [(Min, Max), Doorway, Streets];
        static bool In((SN.Vector3 Min, SN.Vector3 Max) s, SN.Vector3 p) => SN.Vector3.Clamp(p, s.Min, s.Max) == p;
        foreach (var s in spaces)
        {
            if (In(s, from) && In(s, to)) return to;
        }

        foreach (var s in spaces)
        {
            if (In(s, from)) return SN.Vector3.Clamp(to, s.Min, s.Max);
        }

        foreach (var s in spaces)
        {
            if (In(s, to)) return to;
        }

        return SN.Vector3.Clamp(to, Min, Max);
    }
    internal const float Radius = 0.3f * Scale; // the walking body's, against what stands on the floor
    internal const float SolidRadius = 0.15f; // the walking body's, against the knots' floor's solids (Collide)
    internal const float StepUp = 0.16f; // the highest step the body walks onto, above a stair's 0.125 m
    internal const float Height = 1.25f; // what passes over the body's head stops nothing (the knots' floor's slab)

    // What stands on the hall's floor, as walk_physics.gd's boxes: a centre, a turn about the vertical and half sizes
    // across and deep, the walking body kept Radius out of each. The office's desks and rails are low, stepped over.
    internal static readonly (SN.Vector2 Centre, float Turn, SN.Vector2 Half)[] Obstacles =
    [
        (new(0f, 0f), 0f, new(0.42f, 0.3f)), // Delta: its tracks, its torso and its arms (Robot)
        (new(0.88f, 0.16f), 0f, new(0.43f, 0.17f)), // Gaia's placard on its stand (modules/GaiaPresence)
        (new(-2.1f, -1.5f), 0f, new(0.5f, 0.4f)), // the pallet of bricks (Brickwork)
        (new(2.2f, -1.9f), 0f, new(0.45f, 0.25f)), // the steel cabinet
        (new(-3.3f, -3f), 0f, new(0.35f, 0.35f)), // the pillars
        (new(3.3f, -3f), 0f, new(0.35f, 0.35f)),
        (new(ArcadeCabinet.Position.X, ArcadeCabinet.Position.Z), ArcadeCabinet.Yaw, new(0.335f, 0.4f)), // the arcade cabinet, turned to the room
        (HallGallery.Footprint.Centre, 0f, HallGallery.Footprint.Half), // the gallery's bookcase and counter along the left wall
        (HallGallery.CatalogueFootprint.Centre, 0f, HallGallery.CatalogueFootprint.Half), // Streeling's card catalogue on the back wall
        (HallGallery.LecternFootprint.Centre, 0f, HallGallery.LecternFootprint.Half), // the lectern
    ];

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
    private float feet; // above the hall's floor
    private float ground; // what the feet stand on, or fall to: the floor's 0, a stair's tread or the knots' floor
    private float rise; // m/s up
    private float eye = WalkHeight;
    private float dip, dipSpeed, lean, peek, kick, roll, slide, jumpAsked;
    private bool crouchAsked;
    private bool framed; // at a view's eye off the floor, kept until the next step, jump or crouch walks from it

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

    public bool Airborne => feet > ground + 1e-4f || rise > 0f;

    public float Feet => feet;

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
        framed = false;
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
        framed = false;
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
            framed = false;
        }
        else if (framed)
        {
            return;
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
        framed = false;
        Flying = !Flying;
        if (!Flying)
        {
            // Landing from the air falls to the floor, as a jump does.
            body = new(position.X, position.Z);
            velocity = SN.Vector2.Zero;
            eye = WalkHeight;
            feet = Math.Max(0f, position.Y - WalkHeight);
            ground = GroundAt(body, feet);
            feet = Math.Max(feet, ground);
            rise = 0f;
            Crouched = false;
            slide = 0f;
        }
    }

    public void Reset(bool cut = false)
    {
        StartFlight(cut);
        Free = false;
        Flying = false;
        framed = false;
        Settle();
    }

    internal const int ArcadeView = 6; // the arcade cabinet's, where a click on it walks to

    // The views the keys 2 to 7 go to, as the Observatory's presets; 1 is the drift. A view frames from its own eye, even
    // above eye height, without flying: the first step walks from there, its eye easing down to the floor's, as
    // fly_camera.gd's presets and walk_physics.gd's first step do. Only F flies.
    internal static readonly (SN.Vector3 Eye, SN.Vector3 Target)[] Views =
    [
        (new(-0.95f, 1.35f, 1.75f), new(-1.1f, 0.45f, -0.1f)), // the agents' office
        (new(1.75f, 1.2f, 1.55f), new(0.95f, 0.85f, 0.1f)), // Gaia
        (new(3.4f, 3.1f, 5.2f), new(0f, 0.6f, -0.6f)), // the whole hall
        (new(0f, 1.75f, 0.6f), new(0f, 1.78f, -3.3f)), // the clocks of Ottawa and Paris
        ArcadeCabinet.View, // in front of the arcade cabinet, at a player's height
        (new(5.05f, KnotFloor.Level + WalkHeight, 0.6f), new(5.05f, KnotFloor.Level + 0.9f, -3f)), // the knots' floor, at the stair's top
        (new(-2.4f, WalkHeight, 1.2f), new(-6.25f, 1.45f, 1.2f)), // the gallery on the left wall: courses, demos and artifacts
        (new(-4.04f, 1.5f, -1.0f), new(-4.734f, 1.099f, -1.0f)), // the lectern's open book, read from in front of it (LearnReader)
    ];

    // 1 for the drift, 2 to 7 for the views; false for any other number. A cut lands at once, as behind the cinematic's
    // black after Shift and a view's key.
    public bool GoTo(int view, bool cut = false)
    {
        if (view == 1)
        {
            Reset(cut);
            return true;
        }

        if (view < 2 || view > Views.Length + 1)
        {
            return false;
        }

        var (eye, target) = Views[view - 2];
        GoTo(eye, target, cut);
        return true;
    }

    // To any eye and target, as a view's flight or cut, for films (ArcadeFilm).
    public void GoTo(SN.Vector3 eye, SN.Vector3 target, bool cut = false)
    {
        StartFlight(cut);
        var d = SN.Vector3.Normalize(target - eye);
        yaw = MathF.Atan2(d.X, -d.Z);
        pitch = MathF.Asin(d.Y);
        position = Confine(eye, eye); // a view's eye anywhere the camera may be, else the nearest place in the hall
        var standing = GroundAt(new(position.X, position.Z), position.Y - WalkHeight) + WalkHeight;
        framed = !Flying && MathF.Abs(position.Y - standing) > 0.01f; // a view frames; it never takes off (F does)
        Free = true;
        Settle();
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
    private void StartFlight(bool cut)
    {
        if (cut || ReducedMotion || !haveDrawn)
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
        feet = ground = GroundAt(body, position.Y - WalkHeight); // on the floor, a stair or the knots' floor under the eye
        rise = 0f;
        eye = framed ? position.Y - feet : WalkHeight; // from a view's eye, the first step eases down to the floor's
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
        position = Confine(position, position + step);
    }

    // The body, a circle of Radius at p, out of what stands on the floor by the nearest way, its speed into it lost so that
    // it slides along: walk_physics.gd's _push_box and _block, twice over for corners.
    // On the knots' floor too: what rises across the body, from above a step to above its head, stops it as a wall (the
    // floor's rails, its plinths, the stair's side and its columns); a step no higher than StepUp is walked onto (GroundAt).
    // Those are kept SolidRadius out, under a tread's depth, so that the next step up does not stop the body on the stair.
    private SN.Vector2 Collide(SN.Vector2 p)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            if (feet < KnotFloor.Level - 0.5f)
            {
                foreach (var (centre, turn, half) in Obstacles)
                {
                    p = Push(p, centre, turn, half, Radius);
                }
            }

            foreach (var s in KnotFloor.Solids)
            {
                if (s.Min.Y < feet + Height && s.Max.Y > feet + StepUp)
                {
                    var (lo, hi) = (new SN.Vector2(s.Min.X, s.Min.Z), new SN.Vector2(s.Max.X, s.Max.Z));
                    p = Push(p, (lo + hi) / 2f, 0f, (hi - lo) / 2f, SolidRadius);
                }
            }

            // Out in the streets, the buildings' walls.
            if (p.Y > HallShell.Front)
            {
                foreach (var f in Footprints)
                {
                    p = PushOut(p, f, Radius);
                }
            }
        }

        return p;
    }

    // A circle of the given radius at p, out of what stands on the hall's floor and the knots' floor's walls, or out of
    // the streets' buildings beyond the front wall, as the walking body is kept: for the scooter (Rides).
    internal SN.Vector2 Solid(SN.Vector2 p, float radius)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            if (p.Y > HallShell.Front)
            {
                foreach (var f in Footprints) p = PushRing(p, f, radius);
                continue;
            }

            foreach (var (centre, turn, half) in Obstacles) p = PushBox(p, centre, turn, half, radius);
            foreach (var s in KnotFloor.Solids)
            {
                if (s.Min.Y < Height && s.Max.Y > StepUp)
                {
                    var (lo, hi) = (new SN.Vector2(s.Min.X, s.Min.Z), new SN.Vector2(s.Max.X, s.Max.Z));
                    p = PushBox(p, (lo + hi) / 2f, 0f, (hi - lo) / 2f, radius);
                }
            }
        }

        return p;
    }

    // The body at p out of a building's footprint by the nearest wall, its speed into the wall lost: from inside, out
    // through the nearest wall; from outside, Radius off it.
    private SN.Vector2 PushOut(SN.Vector2 p, Footprint f, float radius)
    {
        var pushed = PushRing(p, f, radius);
        Lose(p, pushed);
        return pushed;
    }

    // The speed into what pushed the body from p to pushed is lost, so that it slides along.
    private void Lose(SN.Vector2 p, SN.Vector2 pushed)
    {
        if (SN.Vector2.DistanceSquared(pushed, p) > 1e-12f)
        {
            var n = SN.Vector2.Normalize(pushed - p);
            velocity -= n * Math.Min(0f, SN.Vector2.Dot(velocity, n));
        }
    }

    private static SN.Vector2 PushRing(SN.Vector2 p, Footprint f, float radius)
    {
        if (p.X < f.Min.X - radius || p.X > f.Max.X + radius || p.Y < f.Min.Y - radius || p.Y > f.Max.Y + radius)
        {
            return p;
        }

        var (inside, best, nearest) = (false, float.MaxValue, p);
        for (int i = 0, j = f.Ring.Length - 1; i < f.Ring.Length; j = i++)
        {
            var (a, b) = (f.Ring[j], f.Ring[i]);
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < a.X + ((p.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X)))
            {
                inside = !inside;
            }

            var ab = b - a;
            var t = ab.LengthSquared() < 1e-8f ? 0f : Math.Clamp(SN.Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
            var q = a + (ab * t);
            var d = SN.Vector2.DistanceSquared(p, q);
            if (d < best)
            {
                (best, nearest) = (d, q);
            }
        }

        var distance = MathF.Sqrt(best);
        if (!inside && distance >= radius)
        {
            return p;
        }

        var away = distance > 1e-5f ? (p - nearest) / distance : SN.Vector2.UnitX;
        return nearest + ((inside ? -away : away) * radius);
    }

    private SN.Vector2 Push(SN.Vector2 p, SN.Vector2 centre, float turn, SN.Vector2 half, float radius)
    {
        var pushed = PushBox(p, centre, turn, half, radius);
        if (pushed != p) Lose(p, pushed);
        return pushed;
    }

    private static SN.Vector2 PushBox(SN.Vector2 p, SN.Vector2 centre, float turn, SN.Vector2 half, float radius)
    {
        var (u, v) = (new SN.Vector2(MathF.Cos(turn), -MathF.Sin(turn)), new SN.Vector2(MathF.Sin(turn), MathF.Cos(turn)));
        var d = p - centre;
        var local = new SN.Vector2(SN.Vector2.Dot(d, u), SN.Vector2.Dot(d, v));
        if (MathF.Abs(local.X) > half.X + radius || MathF.Abs(local.Y) > half.Y + radius)
        {
            return p;
        }

        var q = SN.Vector2.Clamp(local, -half, half);
        var outward = local - q;
        var distance = outward.Length();
        if (distance >= radius)
        {
            return p;
        }

        if (distance > 1e-5f)
        {
            local = q + (outward / distance * radius);
        }
        else if (half.X - MathF.Abs(local.X) < half.Y - MathF.Abs(local.Y))
        {
            local.X = (half.X + radius) * (local.X >= 0f ? 1f : -1f); // the centre inside: out by the nearest side
        }
        else
        {
            local.Y = (half.Y + radius) * (local.Y >= 0f ? 1f : -1f);
        }

        return centre + (u * local.X) + (v * local.Y);
    }

    // The height the body at p stands on, its feet at from: the highest top under it no more than a step above them, the
    // hall's floor otherwise.
    internal static float GroundAt(SN.Vector2 p, float from)
    {
        var ground = 0f;
        foreach (var s in KnotFloor.Solids)
        {
            if (p.X >= s.Min.X && p.X <= s.Max.X && p.Y >= s.Min.Z && p.Y <= s.Max.Z && s.Max.Y <= from + StepUp && s.Max.Y > ground)
            {
                ground = s.Max.Y;
            }
        }

        return ground;
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
        var grounded = feet <= ground + 1e-4f && rise <= 0f;
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
            feet = Math.Max(feet, ground + 0.001f);
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

        var next = Collide(body + (velocity * dt));
        var confined = Confine(new(body.X, 1f, body.Y), new(next.X, 1f, next.Y));
        var inside = new SN.Vector2(confined.X, confined.Z);
        if (inside.X != next.X)
        {
            velocity.X = 0f; // a wall stops the body
        }

        if (inside.Y != next.Y)
        {
            velocity.Y = 0f;
        }

        body = inside;

        // A step up or down the stair is walked, the eye kept where it was and eased to its height by the dip's spring;
        // beyond a step down, a fall.
        var under = GroundAt(body, feet);
        if (grounded && MathF.Abs(under - feet) > 1e-5f)
        {
            if (under > feet || feet - under <= StepUp)
            {
                dip += ReducedMotion ? 0f : feet - under;
                feet = under;
            }
            else
            {
                grounded = false;
            }
        }

        ground = under;

        // Up and down: a jump or a fall, then gravity; the eye lowers for a crouch or a slide.
        if (!grounded)
        {
            rise -= Gravity * dt;
            feet += rise * dt;
            if (feet <= ground)
            {
                feet = ground;
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
        position = Confine(position, new(body.X + off.X, feet + eye + dip + peekBy, body.Y + off.Y));

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
        // The streets: through the open door and out, a building's wall stops the body, from outside or from within.
        var walker = new FreeCamera { DoorOpen = true, Footprints = [Footprint.Of([new(-1f, 10f), new(1f, 10f), new(1f, 12f), new(-1f, 12f)])] };
        SN.Vector3 WalkTo(float x, float z)
        {
            var at = new SN.Vector3(x, 1.1f, 4f);
            for (var i = 0; i < 200; i++) at = walker.Confine(at, at + new SN.Vector3(0f, 0f, 0.05f));
            return at;
        }

        var (through, beside) = (WalkTo(HallShell.DoorX, 14f), WalkTo(3f, 14f));
        Check(through.Z > 13f && beside.Z <= Max.Z, $"the open door lets the body out through it, and only through it ({through.Z:F2}, {beside.Z:F2})");
        var (stopped, within) = (walker.Collide(new(0f, 10f - (Radius / 2))), walker.Collide(new(0.2f, 10.4f)));
        Check(stopped.Y <= 10f - Radius + 1e-4f && within.Y <= 10f - Radius + 1e-4f, $"a building's wall stops the body Radius off it, and puts back out one inside ({stopped.Y:F3}, {within.Y:F3})");

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

        Check(views && !camera.Flying && camera.GoTo(1) && !camera.Free && !camera.GoTo(Views.Length + 2) && !camera.GoTo(0), "the views stand where they say, inside the hall, without flying; 1 is the drift");

        // From the whole hall's view, 3.1 m up, looking down: the first steps walk, down to the floor's eye, level.
        camera.GoTo(4, cut: true);
        camera.Look(0f, 0, 300);
        for (var i = 0; i < 60; i++)
        {
            camera.Move(0f, new(1, 0, 0, false), 1f / 60f);
        }

        (eye, _) = camera.View(0f);
        Check(!camera.Flying && MathF.Abs(eye.Y - WalkHeight) < 1e-3f, $"walking from a view above eye height walks on the floor, though looking down (eye at {eye.Y:F3} m)");
        camera.Reset(cut: true);

        SelfTestBody(Check);
        SelfTestObstacles(Check);
        SelfTestFlight(Check);
        SelfTestKnotFloor(Check);
    }

    // The stair walks up to the knots' floor and down again, the eye never jumping; its rails stop the body; under it the
    // body walks on the hall's floor; landing from a flight lands on whichever floor is under it.
    private static void SelfTestKnotFloor(Action<bool, string> Check)
    {
        static FreeCamera At(SN.Vector2 body, float yaw)
        {
            var camera = new FreeCamera();
            camera.Look(0f, 1e-9, 0);
            camera.Move(0f, default, 1f / 60f);
            camera.body = body;
            camera.yaw = yaw;
            camera.pitch = 0f;
            camera.Move(0f, default, 1f / 60f);
            return camera;
        }

        float Walk(FreeCamera camera, float seconds, FreeCamera.Held held)
        {
            var jump = 0f;
            var (last, _) = camera.View(0f);
            for (var t = 0f; t < seconds; t += 1f / 60f)
            {
                camera.Move(0f, held, 1f / 60f);
                var (e, _) = camera.View(0f);
                jump = Math.Max(jump, MathF.Abs(e.Y - last.Y));
                last = e;
            }

            return jump;
        }

        // Up: from in front of the stair's foot, straight on towards the wall.
        var camera = At(new(5.7f, 4.6f), 0f);
        var jumpUp = Walk(camera, 5f, new(1, 0, 0, false));
        Walk(camera, 1f, default);
        var (eye, _) = camera.View(0f);
        Check(MathF.Abs(camera.feet - KnotFloor.Level) < 1e-4f && MathF.Abs(eye.Y - (KnotFloor.Level + WalkHeight)) < 2e-3f && camera.body.Y < 0.9f && !camera.Airborne,
            $"forward up the stair stands on the knots' floor, {KnotFloor.Level} m up (feet {camera.feet:F3}, eye {eye.Y:F3}, z {camera.body.Y:F2})");
        Check(jumpUp < 0.05f, $"the eye climbs the stair smoothly, never more than 5 cm a frame ({jumpUp:F3} m)");

        // Down again, turned round.
        camera.yaw = MathF.PI;
        var jumpDown = Walk(camera, 5f, new(1, 0, 0, false));
        Walk(camera, 1f, default);
        (eye, _) = camera.View(0f);
        Check(camera.feet == 0f && MathF.Abs(eye.Y - WalkHeight) < 2e-3f && camera.body.Y > 4f && camera.Landings == 0 && jumpDown < 0.05f,
            $"back down the stair to the hall's floor, step by step, without a fall (feet {camera.feet:F3}, z {camera.body.Y:F2}, a frame's {jumpDown:F3} m)");

        // The stair's side is a wall from the hall.
        camera = At(new(4.6f, 2.5f), MathF.PI / 2f);
        Walk(camera, 3f, new(1, 0, 0, true));
        Check(camera.feet == 0f && camera.body.X < 5.2f - SolidRadius + 1e-3f, $"the stair's side stops the body walking at it from the hall (x {camera.body.X:F2})");

        // Under the floor: the hall's floor all the way to the wall, between the columns.
        camera = At(new(4.75f, 2.2f), 0f);
        var highest = 0f;
        for (var i = 0; i < 300; i++)
        {
            camera.Move(0f, new(1, 0, 0, false), 1f / 60f);
            highest = Math.Max(highest, camera.feet);
        }

        Check(highest == 0f && camera.body.Y < -2.9f, $"under the knots' floor the body walks on the hall's floor to the wall (z {camera.body.Y:F2})");

        // On the floor: its rail on the hall's side holds, and a jump lands on the floor, not in the hall.
        camera = At(new(5.05f, -1.6f), -MathF.PI / 2f);
        camera.feet = camera.ground = KnotFloor.Level;
        Walk(camera, 3f, new(1, 0, 0, true));
        Check(camera.feet == KnotFloor.Level && camera.body.X > 3.93f + SolidRadius - 1e-3f, $"the rail stops the body at the floor's edge (x {camera.body.X:F2})");
        camera.Jump(0f);
        Walk(camera, 1.5f, new(1, 0, 0, false));
        Check(camera.Jumps == 1 && camera.Landings == 1 && camera.feet == KnotFloor.Level, "a jump at the rail lands back on the knots' floor");

        // Flying off the floor over the hall and landing falls to the hall's floor; landing above the floor stays on it.
        camera.ToggleFlying(0f);
        Walk(camera, 1.5f, new(1, 0, 0, false));
        camera.ToggleFlying(0f);
        Walk(camera, 2f, default);
        Check(camera.feet == 0f && camera.Landings == 2 && camera.body.X < 3.9f, $"landing from a flight over the hall falls to its floor (x {camera.body.X:F2})");
        Check(camera.GoTo(7, cut: true) && camera.feet == KnotFloor.Level && !camera.framed, "view 7 stands on the knots' floor, at walking height");
        camera.ToggleFlying(0f);
        camera.ToggleFlying(0f);
        Walk(camera, 1f, default);
        Check(camera.feet == KnotFloor.Level && !camera.Airborne, "landing from a flight above the knots' floor stays on it");
    }

    // What stands on the floor stops the walking body, which slides along it, and lets a flight pass over.
    private static void SelfTestObstacles(Action<bool, string> Check)
    {
        static float Clearance(SN.Vector2 p) => Obstacles.Min(o =>
        {
            var (u, v) = (new SN.Vector2(MathF.Cos(o.Turn), -MathF.Sin(o.Turn)), new SN.Vector2(MathF.Sin(o.Turn), MathF.Cos(o.Turn)));
            var d = p - o.Centre;
            var local = new SN.Vector2(SN.Vector2.Dot(d, u), SN.Vector2.Dot(d, v));
            return (local - SN.Vector2.Clamp(local, -o.Half, o.Half)).Length();
        });

        var camera = new FreeCamera();
        camera.Look(0f, 1e-9, 0); // from the drift's eye, straight at Delta
        var closest = float.MaxValue;
        for (var i = 0; i < 240; i++)
        {
            camera.Move(0f, new(1, 0, 0, true), 1f / 60f);
            closest = Math.Min(closest, Clearance(camera.body));
        }

        Check(camera.body.Y > 0.3f && closest > Radius - 0.005f, $"walking at Delta stops in front of it, the body {Radius:F2} m off ({camera.body.Y:F2} m, {closest:F3} m)");
        for (var i = 0; i < 120; i++)
        {
            camera.Move(0f, new(1, 1, 0, false), 1f / 60f);
            closest = Math.Min(closest, Clearance(camera.body));
        }

        Check(camera.body.X > 0.42f + Radius && camera.body.Y < 0.3f && closest > Radius - 0.005f, $"forward and right slides along it and past it ({camera.body.X:F2}, {camera.body.Y:F2})");

        // Every way from four places in the hall, for 4 s at a sprint: never into anything.
        var into = 0;
        foreach (var start in new SN.Vector2[] { new(0f, 2.5f), new(-2.1f, 0f), new(2.2f, -0.8f), new(1.6f, -2.6f) })
        {
            for (var way = 0; way < 16; way++)
            {
                camera = new FreeCamera();
                camera.Look(0f, 1e-9, 0);
                camera.Move(0f, default, 1f / 60f);
                camera.body = start;
                camera.Look(0f, way * MathF.PI / 8f / Sensitivity, 0);
                for (var i = 0; i < 240; i++)
                {
                    camera.Move(0f, new(1, 0, 0, true), 1f / 60f);
                    into += Clearance(camera.body) < Radius - 0.005f ? 1 : 0;
                }
            }
        }

        Check(into == 0, $"walked every way at a sprint, the body never enters the pallet, the cabinets, the pillars, Gaia or Delta ({into} frames in)");
        camera = new FreeCamera();
        camera.Look(0f, 1e-9, 0);
        camera.ToggleFlying(0f);
        for (var i = 0; i < 240; i++)
        {
            camera.Move(0f, new(1, 0, 0, false), 1f / 60f);
        }

        Check(camera.View(0f).Eye.Z < -0.5f, "free flight is left as it was, through them (walk_physics.gd's)");
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

        // A slide: C while sprinting rushes on, lower, rolled and wider, then ends crouched. Back from the wall first, and
        // across the hall, clear of Delta.
        camera.GoTo(1);
        camera.Look(0f, MathF.PI / 2f / Sensitivity, 0);
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

        camera = new FreeCamera();
        camera.View(0f);
        camera.GoTo(5, cut: true);
        var landed = camera.View(0f);
        camera.GoTo(1, cut: true);
        Check(!camera.InFlight && SN.Vector3.Distance(landed.Eye, Views[3].Eye) < 1e-4f && !camera.Free,
            "behind the cinematic's black a view is a cut, the drift too");
    }
}
