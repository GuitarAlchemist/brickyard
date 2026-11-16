using System.Diagnostics;

// When the window's memory climbs, so that the log says when: a working set that peaked 4.6 GB over a day while its
// private bytes stayed near 460 MB left no line to say at what time, or after what. Every SampleSeconds, on a timer of
// its own rather than on frames, since a minimised or hidden window presents none and the peak came then, the window
// reads its working set, the peak Windows keeps of it, its private bytes and the managed heap (about 14 ms: Windows lists
// every process for it), and writes a line when the peak or the private bytes passed the last line's by both a quarter
// and Step: the lines before it in the log say what the window was doing.
internal sealed class MemoryWatch
{
    public const double SampleSeconds = 30;
    private const long Step = 256L << 20;

    private double lastAt = double.NegativeInfinity;
    private long peak, privateBytes; // as the last line gave them, 0 before the first

    public readonly record struct Reading(long WorkingSet, long PeakWorkingSet, long Private, long Managed);

    // Reads every SampleSeconds on the thread pool, from the first reading at once, timed by clock in seconds, and
    // writes each line it gives; disposing the timer stops it.
    public static Timer Start(Func<double> clock, Func<Reading> read, Action<string> write)
    {
        var watch = new MemoryWatch();
        var gate = new Lock();
        return new Timer(_ =>
        {
            lock (gate) // a reading slower than the period waits for the last
            {
                if (watch.Sample(clock(), read()) is { } line)
                {
                    write(line);
                }
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(SampleSeconds));
    }

    public static Reading Read()
    {
        using var self = Process.GetCurrentProcess();
        return new Reading(self.WorkingSet64, self.PeakWorkingSet64, self.PrivateMemorySize64, GC.GetTotalMemory(false));
    }

    public bool Due(double at) => at - lastAt >= SampleSeconds;

    // The line to write for a reading at that time, or null.
    public string? Sample(double at, Reading r)
    {
        var since = lastAt;
        lastAt = at;
        static long Mb(long bytes) => bytes >> 20;
        static bool Climbed(long now, long before) => now - before >= Step && now >= before + (before / 4);
        var now = $"working set {Mb(r.WorkingSet)} MB, private {Mb(r.Private)} MB, managed {Mb(r.Managed)} MB";
        string? line = null;
        if (peak == 0)
        {
            line = $"SCENE memory at {at:F0} s: {now}, peak {Mb(r.PeakWorkingSet)} MB";
        }
        else if (Climbed(r.PeakWorkingSet, peak))
        {
            line = $"SCENE memory peak: the working set peaked at {Mb(r.PeakWorkingSet)} MB between {since:F0} s and {at:F0} s; now {now}";
        }
        else if (Climbed(r.Private, privateBytes))
        {
            line = $"SCENE memory grows: {now} at {at:F0} s";
        }

        if (line is not null)
        {
            peak = r.PeakWorkingSet;
            privateBytes = r.Private;
        }

        return line;
    }

    public static void SelfTest(Action<bool, string> check)
    {
        const long M = 1L << 20;
        var watch = new MemoryWatch();
        check(watch.Due(0), "the first frame reads the memory");
        var first = watch.Sample(0.2, new Reading(380 * M, 381 * M, 300 * M, 40 * M));
        check(first == "SCENE memory at 0 s: working set 380 MB, private 300 MB, managed 40 MB, peak 381 MB", "the first reading is written");
        check(!watch.Due(20) && watch.Due(31), "then every 30 s of frames");
        check(watch.Sample(31, new Reading(400 * M, 500 * M, 350 * M, 40 * M)) is null, "a little more is not written");
        var spike = watch.Sample(40000, new Reading(170 * M, 4592 * M, 460 * M, 50 * M));
        check(spike == "SCENE memory peak: the working set peaked at 4592 MB between 31 s and 40000 s; now working set 170 MB, private 460 MB, managed 50 MB",
            "a peak between two readings, as over a sleep, is placed between them");
        check(watch.Sample(40030, new Reading(170 * M, 4700 * M, 470 * M, 50 * M)) is null, "the same peak again is not written twice");
        check(watch.Sample(40060, new Reading(900 * M, 4700 * M, 800 * M, 600 * M)) == "SCENE memory grows: working set 900 MB, private 800 MB, managed 600 MB at 40060 s",
            "private bytes that grow are written, with the managed heap");

        // On its own timer, with no frame at all, as in a minimised window: the first reading comes at once.
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using (Start(() => 7.0, () => new Reading(300 * M, 300 * M, 300 * M, 30 * M), lines.Enqueue))
        {
            SpinWait.SpinUntil(() => !lines.IsEmpty, 2000);
        }

        check(lines.TryPeek(out var timed) && timed.StartsWith("SCENE memory at 7 s:", StringComparison.Ordinal), "the readings run on a timer of their own, without frames");
    }
}
