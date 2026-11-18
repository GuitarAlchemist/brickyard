using SN = System.Numerics;

// Gaia's planet turned by hand (the user, 2026-11-18: « je veux pouvoir faire tourner gaia (Avec un peu d'inertie comme
// les petits robots) »): pressed on the planet and dragged sideways, it turns about its axis as if the hand held its
// surface where it faces the eye; let go, it coasts on and slows as the office's rings do (OfficeSpin). The module draws
// the planet (its triangles painted with the host's Earth); the host splits them from the labels
// (SceneModuleGeometry.Split) and turns them about their centre.
internal static class GaiaTurn
{
    public const string Key = "gaia";

    // A point's place in the window's pixels, or null behind the eye.
    public static SN.Vector2? Screen(SN.Matrix4x4 viewProj, SN.Vector3 p, int width, int height)
    {
        var c = SN.Vector4.Transform(new SN.Vector4(p, 1f), viewProj);
        if (c.W <= 1e-4f) return null;
        return new(((c.X / c.W * 0.5f) + 0.5f) * width, ((c.Y / c.W * 0.5f) + 0.5f) * height);
    }

    // Whether (x, y) is on the planet of that centre and radius.
    public static bool Hit(SN.Matrix4x4 viewProj, SN.Vector3 centre, float radius, int width, int height, double x, double y)
    {
        if (Screen(viewProj, centre, width, height) is not { } s || Screen(viewProj, centre + (radius * SN.Vector3.UnitY), width, height) is not { } top) return false;
        var r = SN.Vector2.Distance(s, top);
        return SN.Vector2.Distance(s, new((float)x, (float)y)) <= r * 1.05f;
    }

    // The radians the planet turns for a drag of dx pixels: the point of its surface facing the eye follows the hand.
    public static double Turned(SN.Matrix4x4 viewProj, SN.Vector3 eye, SN.Vector3 centre, float radius, int width, int height, double dx)
    {
        var toEye = eye - centre;
        toEye.Y = 0f;
        if (toEye.LengthSquared() < 1e-8f) return 0;
        toEye = SN.Vector3.Normalize(toEye);
        var front = centre + (radius * toEye);
        var along = SN.Vector3.Cross(SN.Vector3.UnitY, toEye); // where the front goes as the planet turns by a positive angle
        const float step = 0.05f;
        if (Screen(viewProj, front, width, height) is not { } a || Screen(viewProj, front + (radius * step * along), width, height) is not { } b) return 0;
        var perRadian = (b.X - a.X) / step;
        return Math.Abs(perRadian) < 1 ? 0 : dx / perRadian;
    }

    // The planet's world, turned by angle about its centre's vertical.
    public static SN.Matrix4x4 World(SN.Vector3 centre, double angle) =>
        SN.Matrix4x4.CreateTranslation(-centre) * SN.Matrix4x4.CreateRotationY((float)angle) * SN.Matrix4x4.CreateTranslation(centre);

    internal static void SelfTest(Action<bool, string> check)
    {
        var centre = new SN.Vector3(0.88f, 0.69f, 0.16f);
        const float radius = 0.25f;
        const int w = 1600, h = 900;
        var projection = SN.Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, w / (float)h, 0.1f, 60f);
        projection.M22 = -projection.M22;
        SN.Matrix4x4 From(SN.Vector3 eye) => SN.Matrix4x4.CreateLookAt(eye, centre, SN.Vector3.UnitY) * projection;

        var eye = centre + new SN.Vector3(0f, 0.2f, 2f);
        var view = From(eye);
        var s = Screen(view, centre, w, h)!.Value;
        check(Hit(view, centre, radius, w, h, s.X + 20, s.Y - 10) && !Hit(view, centre, radius, w, h, s.X + 400, s.Y), "a press on Gaia's planet takes it, one beside it does not");

        // Dragged right by the planet's radius on screen, from the front: the surface follows, about one radian.
        var r = SN.Vector2.Distance(s, Screen(view, centre + (radius * SN.Vector3.UnitY), w, h)!.Value);
        var turned = Turned(view, eye, centre, radius, w, h, r);
        var moved = SN.Vector3.Transform(centre + (radius * SN.Vector3.Normalize(new SN.Vector3(0f, 0f, 1f))), World(centre, turned));
        check(turned > 0.8 && turned < 1.3 && moved.X > centre.X + 0.15f, $"dragged right from the front, the planet's face goes right ({turned:F2} rad)");

        // From behind, the face toward the eye still goes the hand's way on screen.
        var behind = centre + new SN.Vector3(0f, 0.2f, -2f);
        var back = From(behind);
        var face = centre + (radius * -SN.Vector3.UnitZ);
        var fromBehind = Turned(back, behind, centre, radius, w, h, r);
        var went = Screen(back, SN.Vector3.Transform(face, World(centre, fromBehind)), w, h)!.Value.X - Screen(back, face, w, h)!.Value.X;
        check(Math.Abs(fromBehind) > 0.8 && went > 0.5f * r, $"dragged right from behind, the planet's face toward the eye goes right ({fromBehind:F2} rad, {went:F0} px)");
        check(SN.Vector3.Distance(SN.Vector3.Transform(centre, World(centre, 2.0)), centre) < 1e-5f, "the planet turns about its own centre");
    }
}
