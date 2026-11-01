// The frame times behind the percentiles at exit (Program's "SCENE frames" and "SCENE keys to screen"), in memory that
// does not grow with the run: bins of a microsecond up to 100 ms, and the rarer longer frames, a window minimised or
// held, kept as they were. A run of 7.7 h kept 1.7 million doubles before, and sorted them at exit.
internal sealed class FrameStats
{
    private const int Bins = 100_000; // a microsecond each, up to 100 ms

    private readonly int[] bins = new int[Bins];
    private readonly List<double> longer = [];
    private bool sorted = true;

    public int Count { get; private set; }

    public double Max { get; private set; } = double.NaN;

    public void Add(double ms)
    {
        Max = Count == 0 ? ms : Math.Max(Max, ms);
        Count++;
        var bin = Math.Max(0, (int)(ms * 1000.0));
        if (bin < Bins)
        {
            bins[bin]++;
        }
        else
        {
            longer.Add(ms);
            sorted = false;
        }
    }

    // The time at fraction q of the frames sorted, as Program took it from its sorted list: within half a microsecond.
    public double Percentile(double q)
    {
        if (Count == 0)
        {
            return double.NaN;
        }

        var rank = Math.Min(Count - 1, (int)(q * Count));
        for (var bin = 0; bin < Bins; bin++)
        {
            rank -= bins[bin];
            if (rank < 0)
            {
                return (bin + 0.5) / 1000.0;
            }
        }

        if (!sorted)
        {
            longer.Sort();
            sorted = true;
        }

        return longer[Math.Min(rank, longer.Count - 1)];
    }

    internal static void SelfTest(Action<bool, string> Check)
    {
        var random = new Random(31);
        var stats = new FrameStats();
        var all = new List<double>();
        for (var i = 0; i < 50_000; i++)
        {
            var ms = i % 997 == 0 ? 100 + (random.NextDouble() * 30_000) : 8 + (random.NextDouble() * 20);
            stats.Add(ms);
            all.Add(ms);
        }

        all.Sort();
        double Exact(double q) => all[Math.Min(all.Count - 1, (int)(q * all.Count))];
        var close = new[] { 0.5, 0.95, 0.99, 0.999, 1.0 }.All(q => Math.Abs(stats.Percentile(q) - Exact(q)) < 0.001);
        Check(close && stats.Count == all.Count && stats.Max == all[^1], "the frames' percentiles are the sorted list's, to a microsecond");

        var empty = new FrameStats();
        Check(empty.Count == 0 && double.IsNaN(empty.Percentile(0.5)) && double.IsNaN(empty.Max), "no frame, no percentile");
    }
}
