using SN = System.Numerics;

// The rides, after the Observatory's scripts/segway.gd and scripts/drone.gd but driven by hand and fast (the user,
// 2026-11-18: « je veux pouvoir me balader en scooter ou en drone comme dans l'observatoire - je veux un scooter de
// course tres rapide, meme chose pour le drone »).
//   - V gets on a racing scooter where the camera stands, facing where it looks; V again or Esc gets off there. W (Z on
//     AZERTY) or the up arrow opens the throttle, S brakes and then backs, A and D (Q and D) steer, Shift is the turbo.
//     The mouse looks round and, once rolling, the scooter turns to where the view looks, unless it looks back. 120 km/h,
//     220 with the turbo; the tyres hold 1.3 g, so the faster it goes the wider it turns, and it leans into the bend.
//   - X takes the drone off from in front of the camera; X again or Esc lands it, the view back where it was. W, S, A
//     and D fly it along the view and across it, Space or E up, C down, Shift faster; the mouse turns it. 150 km/h, 300
//     with Shift, 3 g of thrust beside gravity's: it tilts with what it does, as a multirotor does.
//   - O: the view behind the ride, or on it.
// Out in the streets OpenStreetMap's buildings stop the scooter and take most of its speed when hit at speed; Google's
// tiles stop nothing, since nothing may be derived from them (GoogleStreets). The drone flies through the city up to
// 150 m, kept off the ground; in the hall both stay inside it (FreeCamera.Confine), the scooter stopped by what stands
// on the floor. The models are made here, at the hall's scale (FreeCamera.Scale). Kept apart from the window so that it
// can be tested alone.
internal sealed class Rides
{
    internal enum Kind { None, Scooter, Drone }

    private const float S = FreeCamera.Scale; // metres to the hall's units
    private const float G = 9.81f;
    private const float Step = 1f / 120f; // s: the longest step of the physics

    // The scooter, in metres and seconds.
    internal const float ScooterTop = 120f / 3.6f;
    internal const float ScooterTurbo = 220f / 3.6f;
    private const float ScooterBack = 15f / 3.6f;
    private const float Throttle = 9f; // m/s²: 0 to 100 km/h in about 3 s
    private const float TurboThrottle = 13f;
    private const float Brake = 16f;
    private const float Grip = 1.3f * G; // the lateral acceleration the tyres hold
    private const float SteerRate = 1.9f; // rad/s at walking pace, with A or D
    private const float Follow = 3.2f; // per second: how fast a rolling scooter turns to where the view looks
    private const float LookBack = 1.6f; // radians: a view further round than this looks, and does not steer
    private const float LeanMax = 0.8f; // radians, about 45 degrees
    private const float SeatEye = 1.42f; // the eye above the road, sitting up
    private const float SeatBack = 0.42f; // the eye behind the scooter's middle, over the seat
    private const float ScooterRadius = 0.5f;
    private const float Crash = 8f; // m/s into a wall past which most of the speed is lost

    // The drone.
    internal const float DroneTop = 150f / 3.6f;
    internal const float DroneTurbo = 300f / 3.6f;
    private const float Thrust = 3f * G; // m/s², what it can add to its velocity beside holding itself up
    private const float Respond = 2.5f; // 1/s: how fast the velocity goes to what the sticks ask
    private const float NoseRate = 4f; // rad/s: the nose turns to where the view looks
    private const float Hover = 0.25f; // m: the lowest it flies above what is under it
    private const float TakeOff = 0.8f; // m in front of the eye it starts from

    // The views.
    private const float ScooterChaseBack = 4.2f, ScooterChaseUp = 1.1f;
    private const float DroneChaseBack = 2.4f, DroneChaseUp = 0.35f;
    private const float Kick = 22f; // degrees the field widens at top speed

    public Kind Riding { get; private set; }

    public bool Chase { get; private set; } // O: behind the ride, or on it

    public bool ReducedMotion { get; set; }

    public int Crashes { get; private set; }

    public bool Outdoors => Riding != Kind.None && place.Z > HallShell.Front;

    // m/s, for the dial.
    public float Speed => Riding == Kind.Scooter ? MathF.Abs(speed) : Riding == Kind.Drone ? velocity.Length() : 0f;

    private SN.Vector3 place; // the scooter's road contact, or the drone's middle, in the hall's units
    private float ground;
    private float heading; // the scooter's or the drone's nose, as FreeCamera's yaw: 0 towards -z, positive right
    private float yaw, pitch; // the view
    private float speed; // the scooter's, m/s along its heading
    private float turn; // rad/s
    private float lean;
    private SN.Vector3 velocity; // the drone's, m/s
    private SN.Vector3 thrustUp = SN.Vector3.UnitY; // the drone's up, eased
    private SN.Vector3 chaseEye;
    private bool chaseSet;
    private float kick;

    // Gets on the scooter or takes the drone off, from the camera's picture; false for None.
    public bool Start(Kind kind, FreeCamera.Pose from)
    {
        if (kind == Kind.None) return false;
        var d = SN.Vector3.Normalize(from.Target - from.Eye);
        yaw = MathF.Atan2(d.X, -d.Z);
        pitch = Math.Clamp(MathF.Asin(Math.Clamp(d.Y, -1f, 1f)), -1.2f, 1.2f);
        heading = yaw;
        speed = turn = lean = kick = 0f;
        velocity = SN.Vector3.Zero;
        thrustUp = SN.Vector3.UnitY;
        chaseSet = false;
        var flat = new SN.Vector2(from.Eye.X, from.Eye.Z);
        ground = FreeCamera.GroundAt(flat, Math.Max(0f, from.Eye.Y - FreeCamera.WalkHeight));
        if (kind == Kind.Scooter)
        {
            place = new(from.Eye.X, ground, from.Eye.Z);
            pitch = Math.Clamp(pitch, -0.5f, 0.4f);
        }
        else
        {
            place = from.Eye + (Ahead(yaw) * (TakeOff * S)) - (SN.Vector3.UnitY * (0.2f * S));
            place.Y = Math.Max(place.Y, ground + (Hover * S));
        }

        Riding = kind;
        Chase = kind == Kind.Drone; // the drone is seen leaving the hand; the scooter is ridden
        return true;
    }

    // Off the scooter, or the drone landed: where the scooter stood, the eye at walking height looking where the view
    // looked, for the camera to stand at (null after the drone: the camera stayed where the pilot stood).
    public (SN.Vector3 Eye, SN.Vector3 Target)? Stop()
    {
        var was = Riding;
        Riding = Kind.None;
        if (was != Kind.Scooter) return null;
        var eye = new SN.Vector3(place.X, ground + FreeCamera.WalkHeight, place.Z);
        return (eye, eye + Facing(yaw, Math.Clamp(pitch, -0.6f, 0.6f)));
    }

    public void ToggleView() => Chase = !Chase;

    // The mouse moved by dx, dy pixels while the view is turned, as FreeCamera.Look.
    public void Look(double dx, double dy)
    {
        yaw = Wrap(yaw + ((float)dx * 0.0025f));
        pitch = Math.Clamp(pitch - ((float)dy * 0.0025f), Riding == Kind.Scooter ? -0.7f : -1.4f, Riding == Kind.Scooter ? 0.6f : 1.4f);
    }

    // A frame of the ride, dt in real seconds: Forward and Right from the moving keys, Up from Space, E and C, Fast from
    // Shift. The camera confines it as it does the walking body, with the streets' buildings it holds.
    public void Move(FreeCamera.Held held, float dt, FreeCamera camera)
    {
        dt = Math.Clamp(dt, 0f, 0.1f);
        for (var left = dt; left > 1e-6f; left -= Step)
        {
            var h = Math.Min(left, Step);
            if (Riding == Kind.Scooter) Ride(held, h, camera);
            else if (Riding == Kind.Drone) Fly(held, h, camera);
        }

        var top = Riding == Kind.Scooter ? ScooterTurbo : DroneTurbo;
        var kickTo = ReducedMotion ? 0f : Kick * Math.Clamp(Speed / top, 0f, 1f);
        kick += (kickTo - kick) * (1f - MathF.Exp(-4f * dt));
    }

    private void Ride(FreeCamera.Held held, float dt, FreeCamera camera)
    {
        var top = held.Fast ? ScooterTurbo : ScooterTop;
        if (held.Forward > 0f)
        {
            speed = Math.Min(speed + ((speed < 0f ? Brake : held.Fast ? TurboThrottle : Throttle) * dt), Math.Max(top, speed)); // no further than the top speed
        }
        else if (held.Forward < 0f)
        {
            speed -= (speed > 0f ? Brake : Throttle * 0.5f) * dt;
        }

        // The air and the road: about 1.2 m/s² and more with the speed, a coasting scooter slowing to a stand; past the
        // top speed (the turbo let go) it slows to it.
        var drag = (held.Forward == 0f ? 1.2f : 0f) + (0.00045f * speed * speed);
        speed = Toward(speed, 0f, drag * dt);
        if (speed > top) speed = Toward(speed, top, 6f * dt);
        speed = Math.Max(speed, -ScooterBack);

        // Turning: the keys turn the scooter and the view together; rolling, the scooter turns to the view. No faster
        // than the tyres hold.
        var most = Math.Min(SteerRate, Grip / Math.Max(MathF.Abs(speed), 0.5f));
        var keys = held.Right * most * (speed < -0.1f ? -1f : 1f);
        var off = Wrap(yaw - heading);
        var follow = MathF.Abs(speed) > 0.5f && MathF.Abs(off) < LookBack ? Math.Clamp(off * Follow, -most, most) : 0f;
        var want = Math.Clamp(keys + follow, -most, most);
        turn += (want - turn) * (1f - MathF.Exp(-10f * dt));
        heading = Wrap(heading + (turn * dt));
        yaw = Wrap(yaw + (keys * dt));
        var leanTo = Math.Clamp(MathF.Atan(speed * turn / G), -LeanMax, LeanMax);
        lean += (leanTo - lean) * (1f - MathF.Exp(-8f * dt));

        // Forward, out of what stands there and inside the spaces the camera may be in.
        var ahead = Ahead(heading);
        var wanted = new SN.Vector2(place.X, place.Z) + (new SN.Vector2(ahead.X, ahead.Z) * (speed * dt * S));
        var pushed = camera.Solid(wanted, ScooterRadius * S);
        var confined = camera.Confine(new(place.X, 1f, place.Z), new(pushed.X, 1f, pushed.Y));
        var reached = new SN.Vector2(confined.X, confined.Z);
        var by = reached - wanted;
        if (by.LengthSquared() > 1e-10f)
        {
            // A wall: the speed into it is lost, the rest slides along it; taken hard, most of it.
            var n = SN.Vector2.Normalize(by);
            var v = new SN.Vector2(ahead.X, ahead.Z) * speed;
            var into = -SN.Vector2.Dot(v, n);
            if (into > 0f)
            {
                speed = SN.Vector2.Dot(v + (n * into), new SN.Vector2(ahead.X, ahead.Z));
                if (into > Crash)
                {
                    speed *= 0.3f;
                    Crashes++;
                }
            }
        }

        ground = FreeCamera.GroundAt(reached, ground);
        place = new(reached.X, ground, reached.Y);
    }

    private void Fly(FreeCamera.Held held, float dt, FreeCamera camera)
    {
        var top = held.Fast ? DroneTurbo : DroneTop;
        var along = Ahead(yaw);
        var across = new SN.Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        var sticks = (along * held.Forward) + (across * held.Right) + (SN.Vector3.UnitY * held.Up * 0.6f);
        if (sticks.LengthSquared() > 1f) sticks = SN.Vector3.Normalize(sticks);
        var a = ((sticks * top) - velocity) * Respond;
        if (a.LengthSquared() > Thrust * Thrust) a = SN.Vector3.Normalize(a) * Thrust; // what the motors cannot do
        velocity += a * dt;

        // The nose turns to the view at a bounded rate; the thrust tilts the craft along what it does.
        heading = Wrap(heading + Math.Clamp(Wrap(yaw - heading) * 6f, -NoseRate, NoseRate) * dt);
        var up = SN.Vector3.Normalize(a + (SN.Vector3.UnitY * G));
        thrustUp = SN.Vector3.Normalize(SN.Vector3.Lerp(thrustUp, up, 1f - MathF.Exp(-10f * dt)));

        var next = place + (velocity * (dt * S));
        var under = FreeCamera.GroundAt(new(next.X, next.Z), next.Y - (Hover * S)) + (Hover * S);
        if (next.Y < under)
        {
            next.Y = under;
            velocity.Y = Math.Max(velocity.Y, 0f);
        }

        var confined = camera.Confine(place, next);
        if (confined.X != next.X) velocity.X = 0f;
        if (confined.Y != next.Y) velocity.Y = 0f;
        if (confined.Z != next.Z) velocity.Z = 0f;
        place = confined;
    }

    // What the window draws: on the ride (the scooter's seat, the drone's camera) or behind it, the field widening with
    // the speed.
    public FreeCamera.Pose Pose(float dt)
    {
        var fov = FreeCamera.Fov + kick;
        if (!Chase)
        {
            if (Riding == Kind.Scooter)
            {
                // The rider's head leans with the scooter, about the road, and the picture rolls half as much.
                var right = new SN.Vector3(MathF.Cos(heading), 0f, MathF.Sin(heading));
                var eye = place + (SN.Vector3.UnitY * (MathF.Cos(lean) * SeatEye * S)) + (right * (MathF.Sin(lean) * SeatEye * S)) - (Ahead(heading) * (SeatBack * S));
                var facing = Facing(yaw, pitch);
                return new(eye, eye + facing, Rolled(facing, ReducedMotion ? 0f : lean * 0.5f), fov);
            }
            else
            {
                // A racing drone's camera: on the nose, turning with it, rolled with the craft.
                var facing = Facing(heading, pitch);
                var roll = ReducedMotion ? 0f : MathF.Asin(Math.Clamp(SN.Vector3.Dot(thrustUp, new SN.Vector3(MathF.Cos(heading), 0f, MathF.Sin(heading))), -1f, 1f));
                var eye = place + (Ahead(heading) * (0.12f * S));
                return new(eye, eye + facing, Rolled(facing, roll), fov);
            }
        }

        // Behind it, along the view, the eye late on the ride so that the speed shows.
        var (back, upBy, lift) = Riding == Kind.Scooter ? (ScooterChaseBack, ScooterChaseUp, 0.9f) : (DroneChaseBack, DroneChaseUp, 0f);
        var pivot = place + (SN.Vector3.UnitY * (lift * S));
        var look = Facing(yaw, Math.Clamp(pitch, -1.1f, 0.5f) - 0.15f);
        var to = pivot - (look * (back * S)) + (SN.Vector3.UnitY * (upBy * S));
        to.Y = Math.Max(to.Y, FreeCamera.GroundAt(new(to.X, to.Z), to.Y) + (0.3f * S));
        chaseEye = chaseSet ? SN.Vector3.Lerp(chaseEye, to, 1f - MathF.Exp(-12f * Math.Clamp(dt, 0f, 0.1f))) : to;
        chaseSet = true;
        var target = pivot + (look * (2f * S));
        return new(chaseEye, target, SN.Vector3.UnitY, fov);
    }

    // The ride's model in the world: the scooter leaning about its road contact, the drone tilted along its thrust.
    public SN.Matrix4x4 World()
    {
        var ahead = Ahead(heading);
        SN.Vector3 up;
        if (Riding == Kind.Scooter)
        {
            var right = new SN.Vector3(MathF.Cos(heading), 0f, MathF.Sin(heading));
            up = (SN.Vector3.UnitY * MathF.Cos(lean)) + (right * MathF.Sin(lean));
        }
        else
        {
            up = thrustUp;
        }

        var forward = SN.Vector3.Normalize(ahead - (up * SN.Vector3.Dot(ahead, up)));
        var backward = -forward;
        var x = SN.Vector3.Cross(up, backward);
        return new SN.Matrix4x4(x.X, x.Y, x.Z, 0f, up.X, up.Y, up.Z, 0f, backward.X, backward.Y, backward.Z, 0f, place.X, place.Y, place.Z, 1f);
    }

    // Whether the model is in the picture: not the drone's on board, whose camera is inside it.
    public bool ModelShown => Riding == Kind.Scooter || (Riding == Kind.Drone && Chase);

    // The dial at the foot of the picture.
    public string Hint() => Riding switch
    {
        Kind.Scooter => Words.Say(
            $"SCOOTER  {Speed * 3.6f:F0} km/h   W or Up: throttle   S: brake   A and D: steer   Shift: turbo   O: view   V or Esc: get off",
            $"SCOOTER  {Speed * 3.6f:F0} km/h   Z ou Haut : gaz   S : frein   Q et D : tourner   Maj : turbo   O : vue   V ou Échap : descendre"),
        Kind.Drone => Words.Say(
            $"DRONE  {Speed * 3.6f:F0} km/h  {Height():F0} m   WASD: fly   Space or E: up   C: down   Shift: faster   Mouse: turn   O: view   X or Esc: land",
            $"DRONE  {Speed * 3.6f:F0} km/h  {Height():F0} m   ZQSD : voler   Espace ou E : monter   C : descendre   Maj : plus vite   Souris : tourner   O : vue   X ou Échap : atterrir"),
        _ => "",
    };

    private float Height() => (place.Y - FreeCamera.GroundAt(new(place.X, place.Z), place.Y)) / S;

    private static SN.Vector3 Ahead(float yaw) => new(MathF.Sin(yaw), 0f, -MathF.Cos(yaw));

    private static SN.Vector3 Facing(float yaw, float pitch) =>
        new(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));

    // The up of a picture looking along facing, rolled by roll radians (positive: the horizon tilts as a right bank).
    private static SN.Vector3 Rolled(SN.Vector3 facing, float roll)
    {
        var right = SN.Vector3.Normalize(SN.Vector3.Cross(facing, SN.Vector3.UnitY));
        var up = SN.Vector3.Cross(right, facing);
        return (up * MathF.Cos(roll)) + (right * MathF.Sin(roll));
    }

    private static float Wrap(float a) => MathF.IEEERemainder(a, 2f * MathF.PI);

    private static float Toward(float from, float to, float by) => from < to ? Math.Min(to, from + by) : Math.Max(to, from - by);

    // --- The models, in metres, then at the hall's scale: x right, y up, the nose towards -z -------------------------

    // A racing maxi-scooter, about 2 m long: red fairings with a white stripe, a black seat, a tinted windscreen, the
    // handlebar where the rider's view finds it, the fork, and two wheels on alloy rims.
    public static Vertex[] ScooterModel()
    {
        var o = new List<Vertex>();
        var red = new Finish(new(0.72f, 0.06f, 0.05f), Material.Enamel);
        var white = new Finish(new(0.92f, 0.92f, 0.9f), Material.Enamel);
        var black = new Finish(new(0.04f, 0.04f, 0.045f), Material.Rubber);
        var metal = new Finish(new(0.62f, 0.64f, 0.68f), Material.Aluminium);
        var steel = new Finish(new(0.3f, 0.31f, 0.33f), Material.Steel);
        var glass = new Finish(new(0.12f, 0.14f, 0.17f), Material.Glass);
        var lamp = new Finish(new(1f, 0.97f, 0.9f), Material.Glass);
        foreach (var z in new[] { -0.72f, 0.72f })
        {
            Cylinder(o, new(0f, 0.3f, z), SN.Vector3.UnitX, 0.3f, 0.08f, 28, black); // the tyre
            Cylinder(o, new(0f, 0.3f, z), SN.Vector3.UnitX, 0.19f, 0.085f, 20, metal); // the rim
        }

        Box(o, new(0f, 0.55f, 0.12f), new(0.42f, 0.42f, 1.05f), red); // the body over the engine
        Box(o, new(0f, 0.58f, 0.12f), new(0.425f, 0.06f, 0.95f), white); // its stripe
        Box(o, new(0f, 0.34f, -0.18f), new(0.5f, 0.06f, 0.62f), black); // the floorboard
        Box(o, new(0f, 0.68f, -0.56f), new(0.46f, 0.6f, 0.2f), red); // the apron
        Box(o, new(0f, 0.86f, -0.67f), new(0.2f, 0.08f, 0.03f), lamp); // the headlamp
        Box(o, new(0f, 0.84f, 0.38f), new(0.36f, 0.12f, 0.62f), black); // the seat
        Box(o, new(0f, 0.82f, 0.76f), new(0.3f, 0.22f, 0.3f), red); // the tail
        Box(o, new(0f, 0.8f, 0.92f), new(0.22f, 0.06f, 0.02f), new(new(0.8f, 0.05f, 0.04f), Material.Glass)); // its lamp
        Box(o, new(0f, 1.06f, -0.66f), new(0.4f, 0.16f, 0.02f), glass); // the windscreen, low: a racer tucks behind it
        Box(o, new(0f, 0.98f, -0.48f), new(0.62f, 0.035f, 0.035f), steel); // the handlebar
        Box(o, new(0.29f, 0.98f, -0.48f), new(0.1f, 0.045f, 0.045f), black); // its grips
        Box(o, new(-0.29f, 0.98f, -0.48f), new(0.1f, 0.045f, 0.045f), black);
        Box(o, new(0f, 1.0f, -0.54f), new(0.2f, 0.05f, 0.1f), new(new(0.05f, 0.06f, 0.07f), Material.Glass)); // the dash
        foreach (var x in new[] { -0.1f, 0.1f })
        {
            Box(o, new(x, 0.62f, -0.7f), new(0.04f, 0.62f, 0.04f), metal); // the fork
        }

        Box(o, new(0.14f, 0.42f, 0.62f), new(0.08f, 0.1f, 0.5f), steel); // the exhaust
        return Scaled(o);
    }

    // A camera drone 0.75 m across (half a metre read too small from behind at speed): a dark body and canopy, four arms in an X, four motors, four rotors' pale
    // discs (the front pair orange, as the front props are marked), a lens under the nose, and its position lights:
    // green to starboard, red to port.
    public static Vertex[] DroneModel()
    {
        var o = new List<Vertex>();
        var body = new Finish(new(0.16f, 0.17f, 0.19f), Material.Enamel);
        var arm = new Finish(new(0.1f, 0.1f, 0.11f), Material.Matte);
        var motor = new Finish(new(0.55f, 0.57f, 0.6f), Material.Aluminium);
        var disc = new Finish(new(0.62f, 0.64f, 0.68f), Material.Matte);
        var front = new Finish(new(0.95f, 0.45f, 0.08f), Material.Matte);
        Box(o, new(0f, 0f, 0f), new(0.13f, 0.06f, 0.24f), body);
        Box(o, new(0f, 0.04f, -0.01f), new(0.1f, 0.03f, 0.16f), new(new(0.28f, 0.3f, 0.34f), Material.Enamel)); // the canopy
        Box(o, new(0f, -0.04f, -0.11f), new(0.05f, 0.04f, 0.04f), new(new(0.05f, 0.07f, 0.1f), Material.Glass)); // the lens
        foreach (var (sx, sz) in new[] { (1f, -1f), (-1f, -1f), (1f, 1f), (-1f, 1f) })
        {
            var end = new SN.Vector3(sx * 0.18f, 0.01f, sz * 0.18f);
            var turn = SN.Matrix4x4.CreateRotationY(MathF.Atan2(sx, sz)) * SN.Matrix4x4.CreateTranslation(end * 0.5f);
            Box(o, SN.Vector3.Zero, new(0.025f, 0.018f, end.Length()), arm, turn); // an arm
            Cylinder(o, end + new SN.Vector3(0f, 0.015f, 0f), SN.Vector3.UnitY, 0.022f, 0.02f, 14, motor);
            Cylinder(o, end + new SN.Vector3(0f, 0.04f, 0f), SN.Vector3.UnitY, 0.12f, 0.003f, 28, sz < 0f ? front : disc); // the spinning rotor
            var light = new Finish(sz > 0f ? new(0.9f, 0.9f, 0.85f) : sx > 0f ? new(0.1f, 1f, 0.3f) : new(1f, 0.12f, 0.12f), Material.Glass);
            Box(o, end + new SN.Vector3(0f, -0.012f, 0f), new(0.015f, 0.01f, 0.015f), light);
        }

        return Scaled(o, 1.5f);
    }

    private static Vertex[] Scaled(List<Vertex> o, float k = 1f)
    {
        var scaled = o.ToArray();
        for (var i = 0; i < scaled.Length; i++)
        {
            scaled[i].Pos *= S * k;
            scaled[i].Edge = new(scaled[i].Edge.X, scaled[i].Edge.Y, scaled[i].Edge.Z * S * k, scaled[i].Edge.W * S * k); // the rounded edges' half sizes
        }

        return scaled;
    }

    private static void Box(List<Vertex> o, SN.Vector3 c, SN.Vector3 size, Finish finish, SN.Matrix4x4? turn = null)
    {
        var from = o.Count;
        Boxes.Add(o, c, size, finish);
        if (turn is not { } m) return;
        for (var i = from; i < o.Count; i++)
        {
            var v = o[i];
            v.Pos = SN.Vector3.Transform(v.Pos, m);
            v.Normal = SN.Vector3.Normalize(SN.Vector3.TransformNormal(v.Normal, m));
            v.Tangent = SN.Vector3.Normalize(SN.Vector3.TransformNormal(v.Tangent, m));
            o[i] = v;
        }
    }

    // A closed cylinder about axis (a unit vector along x, y or z), radius r, half length h.
    private static void Cylinder(List<Vertex> o, SN.Vector3 c, SN.Vector3 axis, float r, float h, int sides, Finish finish)
    {
        var u = MathF.Abs(axis.Y) > 0.9f ? SN.Vector3.UnitX : SN.Vector3.UnitY;
        var w = SN.Vector3.Normalize(SN.Vector3.Cross(axis, u));
        u = SN.Vector3.Cross(w, axis);
        var flat = new SN.Vector4(0f, 0f, 1f, 1f); // no rounded edge
        Vertex V(SN.Vector3 p, SN.Vector3 n) => new(p, n, finish.Color, finish.Material, axis, flat);
        for (var i = 0; i < sides; i++)
        {
            var (a0, a1) = (2f * MathF.PI * i / sides, 2f * MathF.PI * (i + 1) / sides);
            var (r0, r1) = ((u * MathF.Cos(a0)) + (w * MathF.Sin(a0)), (u * MathF.Cos(a1)) + (w * MathF.Sin(a1)));
            var (p0, p1, q0, q1) = (c + (r0 * r) - (axis * h), c + (r1 * r) - (axis * h), c + (r0 * r) + (axis * h), c + (r1 * r) + (axis * h));
            o.AddRange([V(p0, r0), V(q0, r0), V(q1, r1), V(p0, r0), V(q1, r1), V(p1, r1)]);
            o.AddRange([V(c + (axis * h), axis), V(q1, axis), V(q0, axis)]);
            o.AddRange([V(c - (axis * h), -axis), V(p0, -axis), V(p1, -axis)]);
        }
    }

    internal static void SelfTest(Action<bool, string> Check)
    {
        static FreeCamera Streets() => new() { DoorOpen = true, StreetsOverride = FreeCamera.WideStreets }; // room to run at speed
        var outside = new FreeCamera.Pose(new(HallShell.DoorX, FreeCamera.WalkHeight, HallShell.Front + 20f), new(HallShell.DoorX, FreeCamera.WalkHeight, HallShell.Front + 40f), SN.Vector3.UnitY, FreeCamera.Fov);
        float Run(Rides ride, FreeCamera camera, FreeCamera.Held held, float seconds)
        {
            var t = 0f;
            for (; t < seconds; t += 1f / 60f) ride.Move(held, 1f / 60f, camera);
            return t;
        }

        // The throttle: 100 km/h in about 3 s, 120 at most, 220 with the turbo.
        var ride = new Rides();
        var camera = Streets();
        Check(ride.Start(Kind.Scooter, outside) && ride.Riding == Kind.Scooter && !ride.Chase && ride.Outdoors, "V gets on the scooter where the camera stands, ridden from its seat");
        var seconds = 0f;
        while (ride.Speed < 100f / 3.6f && seconds < 10f) seconds += Run(ride, camera, new(1, 0, 0, false), 1f / 60f);
        Check(seconds is > 2.5f and < 4.5f, $"0 to 100 km/h in {seconds:F1} s");
        Run(ride, camera, new(1, 0, 0, false), 3f);
        var cruise = ride.Speed * 3.6f;
        Run(ride, camera, new(1, 0, 0, true), 5f);
        var turbo = ride.Speed * 3.6f;
        Check(MathF.Abs(cruise - 120f) < 1f && MathF.Abs(turbo - 220f) < 1f, $"120 km/h at the throttle, 220 with the turbo ({cruise:F0}, {turbo:F0})");

        // A bend at speed is wide and leans; at walking pace it is tight.
        var before = ride.World().Translation;
        Run(ride, camera, new(1, 1, 0, true), 0.5f);
        var leaned = ride.lean; // towards the right
        Check(leaned > 0.5f, $"in a bend at 220 km/h it leans into it ({leaned * 180f / MathF.PI:F0}°)");
        var pose = ride.Pose(1f / 60f);
        Check(pose.Fov > FreeCamera.Fov + 15f, $"the field widens with the speed ({pose.Fov:F0}°)");
        Check(SN.Vector3.Distance(before, ride.World().Translation) > 25f * S, "and it covered ground");

        // Braking to a stand, then the brake backs it.
        Run(ride, camera, new(-1, 0, 0, false), 6f);
        Check(ride.speed < 0f && ride.speed >= -ScooterBack, $"S brakes to a stand, then backs at {ScooterBack * 3.6f:F0} km/h at most ({ride.speed * 3.6f:F1})");

        // A building's wall stops it and takes most of the speed hit at speed.
        var wall = new FreeCamera.Footprint[] { FreeCamera.Footprint.Of([new(HallShell.DoorX - 20f, HallShell.Front + 45f), new(HallShell.DoorX + 20f, HallShell.Front + 45f), new(HallShell.DoorX + 20f, HallShell.Front + 60f), new(HallShell.DoorX - 20f, HallShell.Front + 60f)]) };
        camera = new FreeCamera { DoorOpen = true, Footprints = wall, StreetsOverride = FreeCamera.WideStreets };
        ride = new Rides();
        ride.Start(Kind.Scooter, outside);
        Run(ride, camera, new(1, 0, 0, true), 6f);
        var z = ride.World().Translation.Z;
        Check(z <= HallShell.Front + 45f - (ScooterRadius * S) + 0.01f && ride.Crashes >= 1, $"OpenStreetMap's wall stops the scooter, crashed into at speed (z {z - HallShell.Front:F1}, {ride.Crashes} crash)");
        Check(ride.Stop() is { } off && MathF.Abs(off.Eye.Y - FreeCamera.WalkHeight) < 1e-3f && ride.Riding == Kind.None, "getting off stands where the scooter stood, at walking height");

        // The drone: up from in front of the eye, 150 km/h, 300 with Shift, tilted forward as it goes.
        camera = Streets();
        ride = new Rides();
        Check(ride.Start(Kind.Drone, outside) && ride.Chase && ride.ModelShown, "X takes the drone off, seen from behind");
        Run(ride, camera, new(0, 0, 1, false), 3f);
        var high = ride.Height();
        Run(ride, camera, new(1, 0, 0, false), 6f);
        var fast = ride.Speed * 3.6f;
        var tilt = MathF.Acos(Math.Clamp(ride.thrustUp.Y, -1f, 1f));
        Run(ride, camera, new(0, 1, 0, true), 5f); // across: the streets are as wide as they are deep
        var faster = ride.Speed * 3.6f;
        Check(high > 10f, $"Space climbs ({high:F0} m in 3 s)");
        Check(MathF.Abs(fast - 150f) < 3f && MathF.Abs(faster - 300f) < 3f, $"150 km/h, 300 with Shift ({fast:F0}, {faster:F0})");
        Run(ride, camera, new(-1, 0, 0, true), 0.3f);
        tilt = Math.Max(tilt, MathF.Acos(Math.Clamp(ride.thrustUp.Y, -1f, 1f)));
        Check(tilt > 0.6f, $"braking hard tilts it back ({tilt * 180f / MathF.PI:F0}°)");
        Run(ride, camera, new(0, 0, -1, false), 20f);
        Check(ride.Height() >= Hover - 0.01f && ride.Height() < (FreeCamera.Min.Y / S) + 0.02f, $"C lowers it near the ground, no lower ({ride.Height():F2} m)");
        Run(ride, camera, new(0, 0, 1, true), 40f);
        Check(ride.Height() <= 150.5f && ride.Height() > 140f, $"it climbs to 150 m and no higher ({ride.Height():F0} m)");
        ride.ToggleView();
        Check(!ride.Chase && !ride.ModelShown, "O: the view on board, inside the drone");
        Check(ride.Stop() is null && ride.Riding == Kind.None, "landing gives the view back where the pilot stood");

        // In the city, it stays where Google's tiles are: the turbo held for 20 s stops at the far edge.
        ride = new Rides();
        camera = new FreeCamera { DoorOpen = true };
        ride.Start(Kind.Scooter, outside);
        Run(ride, camera, new(1, 0, 0, true), 20f);
        var far = ride.World().Translation.Z;
        Check(far <= FreeCamera.StreetsFar + 1e-3f && far > FreeCamera.StreetsFar - 2f, $"the city's edge stops the scooter, where Google's tiles end (z {far - HallShell.Front:F0} of {FreeCamera.StreetsFar - HallShell.Front:F0})");

        // In the hall, it stays inside.
        ride = new Rides();
        camera = new FreeCamera();
        ride.Start(Kind.Scooter, new(new(0f, FreeCamera.WalkHeight, 2.5f), new(3f, FreeCamera.WalkHeight, 2.5f), SN.Vector3.UnitY, FreeCamera.Fov));
        Run(ride, camera, new(1, 0, 0, true), 4f);
        var at = ride.World().Translation;
        Check(at.X <= FreeCamera.Max.X + 1e-3f, $"the hall's wall stops the scooter (x {at.X:F2})");

        Check(ScooterModel().Length > 300 && DroneModel().Length > 300, "the scooter's and the drone's models are made");
    }
}
