using SN = System.Numerics;

// Which widget a click lands on: each drawn instance's world bounds projected through the frame's camera, the nearest
// one under the cursor winning, as the shelf's cards and Gaia's plaque are hit in window pixels scaled to the framebuffer.
internal static class WidgetPicking
{
    public static string? Pick<TSurface>(IEnumerable<WidgetStage<TSurface>.Hit> hits, SN.Matrix4x4 viewProjection, int width, int height, double x, double y)
        where TSurface : class, IDisposable
    {
        if (width <= 0 || height <= 0) return null;
        string? best = null;
        var nearest = float.PositiveInfinity;
        foreach (var hit in hits)
        {
            var (left, top, right, bottom, depth) = (float.PositiveInfinity, float.PositiveInfinity, float.NegativeInfinity, float.NegativeInfinity, float.PositiveInfinity);
            var behind = false;
            for (var c = 0; c < 8; c++)
            {
                var p = SN.Vector4.Transform(new SN.Vector4((c & 1) == 0 ? hit.Min.X : hit.Max.X, (c & 2) == 0 ? hit.Min.Y : hit.Max.Y, (c & 4) == 0 ? hit.Min.Z : hit.Max.Z, 1f), viewProjection);
                if (!float.IsFinite(p.W) || p.W <= 0) { behind = true; break; }
                var sx = (p.X / p.W + 1f) * width * 0.5f;
                var sy = (p.Y / p.W + 1f) * height * 0.5f;
                left = Math.Min(left, sx); right = Math.Max(right, sx);
                top = Math.Min(top, sy); bottom = Math.Max(bottom, sy);
                depth = Math.Min(depth, p.W);
            }

            if (behind || x < left || x > right || y < top || y > bottom || depth >= nearest) continue;
            nearest = depth;
            best = hit.Click;
        }

        return best;
    }

    public static void SelfTest(Action<bool, string> check)
    {
        // The scene's camera at rest, as Stage.Run makes it.
        var projection = SN.Matrix4x4.CreatePerspectiveFieldOfView(0.75f, 1280f / 720f, 0.1f, 40f);
        projection.M22 = -projection.M22;
        var vp = SN.Matrix4x4.CreateLookAt(new(0, 1.08f, 2.55f), new(0, 0.7f, 0), SN.Vector3.UnitY) * projection;
        WidgetStage<NoSurface>.Hit Desk(string click, float x, float z) => new(click, new(x - 0.15f, 0, z - 0.2f), new(x + 0.15f, 0.62f, z + 0.1f));
        var front = Desk("front", -0.69f, 0.3f);
        var back = Desk("back", -0.69f, -0.15f);
        var (fx, fy) = Centre(front, vp, 1280, 720);
        check(Pick([front, back], vp, 1280, 720, fx, fy) == "front", "a click on a desk in front of another picks the nearer one");
        check(fx < 640 && Pick([front, back], vp, 1280, 720, 1200, 40) is null, "the office projects on the robot's left and a click elsewhere picks nothing");
        check(Pick([front], vp, 2560, 1440, fx * 2, fy * 2) == "front", "picking follows the framebuffer's scale");
        check(Pick([front], default, 1280, 720, fx, fy) is null && Pick([front], vp, 0, 720, fx, fy) is null, "an invalid projection picks nothing");
    }

    private static (float X, float Y) Centre(WidgetStage<NoSurface>.Hit hit, SN.Matrix4x4 vp, int width, int height)
    {
        var p = SN.Vector4.Transform(new SN.Vector4((hit.Min + hit.Max) / 2, 1f), vp);
        return ((p.X / p.W + 1f) * width * 0.5f, (p.Y / p.W + 1f) * height * 0.5f);
    }

    private sealed class NoSurface : IDisposable
    {
        public void Dispose() { }
    }
}
