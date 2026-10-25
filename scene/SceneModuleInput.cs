using SN = System.Numerics;

// Kept outside Stage.Run: mod actions may evolve without editing its stackalloc render loop.
internal static class SceneModuleInteraction
{
    public static string? TryClick<TSurface>(SceneModuleHost<TSurface> module, ModulePickRect target, double x, double y, bool simulated)
        where TSurface : class, IDisposable
    {
        if (!target.Contains(x, y)) return null;
        if (SceneModulePicking.IsValidationHit(target, x, y))
            return !simulated && module.RequestValidation(module.CodeHash) ? "validation" : null;
        return module.RequestReplacement() ? "activation" : null;
    }
}

internal readonly record struct ModulePickRect(float Left, float Top, float Right, float Bottom)
{
    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
}

internal static class SceneModulePicking
{
    // Upper row validates the active image; the lower row keeps the update/revert action.
    public static bool IsValidationHit(ModulePickRect target, double x, double y) =>
        target.Contains(x, y) && y < (target.Top + target.Bottom) / 2;

    public static void SelfTest(Action<bool, string> check)
    {
        var projection = SN.Matrix4x4.CreatePerspectiveFieldOfView(0.75f, 1280f / 720f, 0.1f, 40f);
        projection.M22 *= -1;
        var vp = SN.Matrix4x4.CreateLookAt(new(0, 1.08f, 2.55f), new(0, 0.7f, 0), SN.Vector3.UnitY) * projection;
        var rect = VersionRect(vp, 1280, 720);
        check(rect is { } small && small.Contains(990, 600) && !small.Contains(20, 20), "the actual 3D version plaque projects to its clickable screen area");
        check(VersionRect(vp, 2560, 1440) is { } large && large.Contains(1980, 1200), "module picking follows the existing window/framebuffer resize scale");
        check(VersionRect(default, 1280, 720) is null && VersionRect(vp, 0, 720) is null, "an invalid projection cannot request a module replacement");
        check(IsValidationHit(new(0, 0, 100, 100), 50, 25) && !IsValidationHit(new(0, 0, 100, 100), 50, 75), "validation and replacement have separate hit areas");
        check(!IsValidationHit(new(0, 0, 100, 100), 150, 25), "validation cannot be requested outside the version plaque");
    }

    // The clickable version plaque is in the world, so project its bounds through the actual, moving camera.
    public static ModulePickRect? VersionRect(SN.Matrix4x4 viewProjection, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var left = float.PositiveInfinity;
        var top = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var bottom = float.NegativeInfinity;
        for (var i = 0; i < 8; i++)
        {
            var p = SN.Vector4.Transform(new SN.Vector4((i & 1) == 0 ? 0.38f : 1.38f, (i & 2) == 0 ? 0.06f : 0.21f, (i & 4) == 0 ? 0.33f : 0.41f, 1f), viewProjection);
            if (!float.IsFinite(p.W) || p.W <= 0) return null;
            var x = (p.X / p.W + 1f) * width * 0.5f;
            var y = (p.Y / p.W + 1f) * height * 0.5f;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(right) || !float.IsFinite(bottom)) return null;
        return new(left, top, right, bottom);
    }
}
