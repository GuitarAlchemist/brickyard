using System.Text.Json;

// What Google's tiles have cost this calendar month, on this computer, across every run (the live window and hidden
// runs alike): the user, 2026-11-18, « Je veux pas que ça coûte 100$ !! ». Each request is counted before it is sent,
// and refused once the month's free share is spent, so the bill stays at zero:
// - Photorealistic 3D Tiles: 1,000 free a month, then 6 USD a thousand. Every request counts here, the nested
//   tilesets' too, though Google may bill only those that return a tile.
// - Street View tiles: 100,000 free a month, then 2 USD a thousand; stopped at 95,000 for a margin.
// Only the counts are written (%LOCALAPPDATA%\delta\google-month.json), never a tile, a URL or the key. Google's own
// quota in the Cloud console is the only hard guarantee: this ledger cannot see another program using the same key.
internal static class GoogleMonth
{
    internal const int Free3D = 1000, FreeStreetView = 95_000;

    internal enum Kind
    {
        Tiles3D,
        StreetView,
    }

    private static readonly Mutex Ledger = new(false, @"Local\delta-google-month");

    internal static string Path { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", "google-month.json");

    private sealed record Counts(string Month, int Tiles3D, int StreetView);

    private static string ThisMonth(DateTime now) => now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    // Takes n requests of a kind from this month's free share; false, and nothing taken, when they would pass it.
    internal static bool Take(Kind kind, int n = 1) => Take(kind, n, DateTime.Now);

    internal static bool Take(Kind kind, int n, DateTime now)
    {
        Ledger.WaitOne();
        try
        {
            var c = Read(now);
            var (used, free) = kind == Kind.Tiles3D ? (c.Tiles3D, Free3D) : (c.StreetView, FreeStreetView);
            if (used + n > free) return false;
            Write(kind == Kind.Tiles3D ? c with { Tiles3D = used + n } : c with { StreetView = used + n });
            return true;
        }
        finally
        {
            Ledger.ReleaseMutex();
        }
    }

    // How many requests of a kind are left this month.
    internal static int Left(Kind kind) => Left(kind, DateTime.Now);

    internal static int Left(Kind kind, DateTime now)
    {
        Ledger.WaitOne();
        try
        {
            var c = Read(now);
            return kind == Kind.Tiles3D ? Math.Max(0, Free3D - c.Tiles3D) : Math.Max(0, FreeStreetView - c.StreetView);
        }
        finally
        {
            Ledger.ReleaseMutex();
        }
    }

    // A month whose file is unreadable counts as spent: an error must not reopen the tap.
    private static Counts Read(DateTime now)
    {
        var month = ThisMonth(now);
        try
        {
            if (!File.Exists(Path)) return new Counts(month, 0, 0);
            var c = JsonSerializer.Deserialize<Counts>(File.ReadAllText(Path));
            return c is null ? new Counts(month, Free3D, FreeStreetView) : c.Month == month ? c : new Counts(month, 0, 0);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Counts(month, Free3D, FreeStreetView);
        }
    }

    private static void Write(Counts c)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(c));
        File.Move(temporary, Path, overwrite: true);
    }

    internal static void SelfTest(Action<bool, string> check)
    {
        var real = Path;
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"delta-google-month-{Environment.ProcessId}");
        Path = System.IO.Path.Combine(dir, "google-month.json");
        try
        {
            var november = new DateTime(2026, 11, 18);
            check(Left(Kind.Tiles3D, november) == Free3D && Take(Kind.Tiles3D, 990, november), "a fresh month has the whole free share");
            check(!Take(Kind.Tiles3D, 11, november) && Left(Kind.Tiles3D, november) == 10, "a request past the free 1,000 is refused, and nothing is taken");
            check(Take(Kind.StreetView, 32, november) && Left(Kind.Tiles3D, november) == 10, "Street View counts apart");
            check(Left(Kind.Tiles3D, new DateTime(2026, 12, 1)) == Free3D, "a new month starts again from zero");
            File.WriteAllText(Path, "{ broken");
            check(Left(Kind.Tiles3D, november) == 0 && !Take(Kind.StreetView, 1, november), "an unreadable ledger counts as spent");
        }
        finally
        {
            Path = real;
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
