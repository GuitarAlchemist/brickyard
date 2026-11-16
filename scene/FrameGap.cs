using System.Text;

// What filled the time between two presented frames, so that a slow frame's line says where it went: the event pump
// (Glfw.PollEvents, inside which Windows runs its modal loop while a window's title bar or border is held), the sleep of
// a minimised or hidden window, the swapchain's rebuild, and the rest, the frame's own work and its waits. What the window
// did in that time is noted with when it did it. A gap with a minimised or hidden spell in it is a pause, not a frame.
internal sealed class FrameGap
{
    private readonly List<(string What, double First, double Last, int Count)> seen = [];
    private double since, events, longest, idle, rebuild;
    private int rebuilds;

    public bool Paused => idle > 0;

    public void Begin(double at)
    {
        since = at;
        events = longest = idle = rebuild = 0;
        rebuilds = 0;
        seen.Clear();
    }

    public void Pumped(double seconds)
    {
        events += seconds;
        longest = Math.Max(longest, seconds);
    }

    public void Slept(double seconds) => idle += seconds;

    public void Rebuilt(double seconds)
    {
        rebuild += seconds;
        rebuilds++;
    }

    // The same thing again in a row, as the moves of a dragged window, is counted rather than listed.
    public void Saw(double at, string what)
    {
        if (seen.Count > 0 && seen[^1].What == what)
        {
            seen[^1] = seen[^1] with { Last = at, Count = seen[^1].Count + 1 };
        }
        else if (seen.Count < 12)
        {
            seen.Add((what, at, at, 1));
        }
    }

    public string Describe(double gap)
    {
        static string Ms(double seconds) => $"{seconds * 1000:F0}";
        var text = new StringBuilder($"events {Ms(events)} ms (longest call {Ms(longest)}), minimised or hidden {Ms(idle)} ms, ");
        text.Append($"rebuild {Ms(rebuild)} ms ({rebuilds}), the rest {Ms(gap - events - idle - rebuild)} ms");
        foreach (var (what, first, last, count) in seen)
        {
            text.Append(count == 1 ? $"; {what} at {Ms(first - since)} ms" : $"; {what} x{count} from {Ms(first - since)} to {Ms(last - since)} ms");
        }

        return text.ToString();
    }

    public static void SelfTest(Action<bool, string> check)
    {
        var gap = new FrameGap();
        gap.Begin(10.0);
        gap.Pumped(0.001);
        gap.Saw(10.002, "moved");
        gap.Saw(10.5, "moved");
        gap.Saw(10.9, "framebuffer 1280x720");
        gap.Pumped(2.7);
        gap.Rebuilt(0.007);
        var line = gap.Describe(2.744);
        check(line.StartsWith("events 2701 ms (longest call 2700), minimised or hidden 0 ms, rebuild 7 ms (1), the rest 36 ms", StringComparison.Ordinal), "a slow frame's time is split between the event pump, the rebuild and the rest");
        check(line.Contains("moved x2 from 2 to 500 ms; framebuffer 1280x720 at 900 ms", StringComparison.Ordinal), "the window's moves in a row are counted, and its new size is placed in time");
        check(!gap.Paused, "a frame held up in the event pump is still a frame");
        gap.Slept(0.05);
        check(gap.Paused, "a gap with a minimised spell in it is a pause");
        gap.Begin(13.0);
        check(!gap.Paused && gap.Describe(0.016).Contains("the rest 16 ms", StringComparison.Ordinal) && !gap.Describe(0.016).Contains("moved", StringComparison.Ordinal), "each frame starts its count again");
    }
}
