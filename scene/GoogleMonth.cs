using System.Text.Json;

// What Google's tiles have cost this calendar month, on this computer, across every run (the live window and hidden
// runs alike): the user, 2026-11-18, « Je veux pas que ça coûte 100$ !! ». Each request is counted before it is sent,
// and refused once the month's allowance is spent:
// - Photorealistic 3D Tiles: 1,000 free a month, then 6 USD a thousand; at most Dollars3D a month past them. Every
//   request counts here, the nested tilesets' too, though Google may bill only those that return a tile.
// - Street View tiles: 100,000 free a month, then 2 USD a thousand; stopped at 95,000 for a margin.
// Only the counts are written (%LOCALAPPDATA%\delta\google-month.json), never a tile, a URL or the key. Google's own
// quota in the Cloud console is the only hard guarantee: this ledger cannot see another program using the same key.
// Since « 3 - 10$ max » (2026-11-18) the 3D tiles may cost up to Dollars3D a month past their free 1,000: Free3D is then
// the month's whole allowance, 1,000 + 10 / 6 × 1,000 = 2,666 requests.
internal static class GoogleMonth
{
    internal const int Dollars3D = 10, Price3D = 6; // USD a month at most, and USD a thousand past the free 1,000
    internal const int Free3D = 1000 + (Dollars3D * 1000 / Price3D), FreeStreetView = 95_000;

    // What the month's 3D requests may have cost, in USD, counting every request as billed.
    internal static double Spent3D(int used) => Math.Max(0, used - 1000) * Price3D / 1000.0;

    internal static int Used(Kind kind)
    {
        Hold();
        try
        {
            var c = Read(DateTime.Now);
            return kind == Kind.Tiles3D ? c.Tiles3D : c.StreetView;
        }
        finally
        {
            Ledger.ReleaseMutex();
        }
    }

    internal enum Kind
    {
        Tiles3D,
        StreetView,
    }

    // The selftest swaps in its own, as it does the Path: it must not abandon the live window's.
    private static Mutex Ledger = new(false, @"Local\delta-google-month");

    // Takes the ledger's mutex. One left by a scene that was killed comes as an AbandonedMutexException, yet this thread
    // then holds it, and the finally that follows lets it go. Thrown outside the try, it was never released, and the next
    // request froze the window (Brutal's review of 0.8.0).
    private static void Hold()
    {
        try
        {
            Ledger.WaitOne();
        }
        catch (AbandonedMutexException)
        {
        }
    }

    // An inner handler for the selftest: counts the sends that reach it and answers 200.
    private sealed class Counting : HttpMessageHandler
    {
        internal int Sent;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Sent);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    internal static string Path { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", "google-month.json");

    private sealed record Counts(string Month, int Tiles3D, int StreetView);

    private static string ThisMonth(DateTime now) => now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    // Takes n requests of a kind from this month's free share; false, and nothing taken, when they would pass it.
    internal static bool Take(Kind kind, int n = 1) => Take(kind, n, DateTime.Now);

    internal static bool Take(Kind kind, int n, DateTime now)
    {
        Hold();
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
        Hold();
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
        var realLedger = Ledger;
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"delta-google-month-{Environment.ProcessId}");
        Path = System.IO.Path.Combine(dir, "google-month.json");
        Ledger = new Mutex(false, $@"Local\delta-google-month-selftest-{Environment.ProcessId}");
        try
        {
            var november = new DateTime(2026, 11, 18);
            check(Free3D == 2666 && Math.Abs(Spent3D(Free3D) - 9.996) < 1e-9, "the 3D allowance is the free 1,000 and 10 USD at 6 USD a thousand");
            check(Left(Kind.Tiles3D, november) == Free3D && Take(Kind.Tiles3D, Free3D - 10, november), "a fresh month has the whole allowance");
            check(!Take(Kind.Tiles3D, 11, november) && Left(Kind.Tiles3D, november) == 10, "a request past the allowance is refused, and nothing is taken");
            check(Take(Kind.StreetView, 32, november) && Left(Kind.Tiles3D, november) == 10, "Street View counts apart");
            check(Left(Kind.Tiles3D, new DateTime(2026, 12, 1)) == Free3D, "a new month starts again from zero");
            // A mutex abandoned by a thread that ended holding it: the ledger still answers, and lets it go after.
            var holder = new Thread(() => Ledger.WaitOne());
            holder.Start();
            holder.Join();
            check(Left(Kind.Tiles3D, november) == 10, "a mutex left by a scene that was killed is taken");
            var other = new Thread(() => Left(Kind.Tiles3D, november));
            other.Start();
            check(other.Join(TimeSpan.FromSeconds(5)), "and let go: another thread reads the ledger after it");

            // Each send of a 3D tile request is taken from this month's ledger, the retries too; other requests are not,
            // and past the allowance the request is refused before it leaves.
            File.Delete(Path);
            var inner = new Counting();
            using (var metered = new HttpMessageInvoker(new GoogleStreets.Metered(inner)))
            {
                System.Net.HttpStatusCode? refused = null;
                bool Send(string url)
                {
                    try
                    {
                        metered.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None).GetAwaiter().GetResult().Dispose();
                        return true;
                    }
                    catch (HttpRequestException e)
                    {
                        refused = e.StatusCode;
                        return false;
                    }
                }

                const string tile = "https://tile.googleapis.com/v1/3dtiles/datasets/x/files/y.glb";
                check(Send(tile) && Send(tile) && Send(tile) && Used(Kind.Tiles3D) == 3, "each try of a 3D tile is counted");
                check(Send("https://tile.googleapis.com/v1/streetview/tiles/3/0/0") && Send("https://example.com/v1/3dtiles/root.json")
                    && Used(Kind.Tiles3D) == 3 && Used(Kind.StreetView) == 0, "Street View tiles and other hosts are not metered here");
                Take(Kind.Tiles3D, Free3D - 3);
                check(!Send(tile) && inner.Sent == 5 && Used(Kind.Tiles3D) == Free3D, "past the allowance a 3D request is refused before it is sent");
                check(refused is not null, "with a status code, which GoogleTileSource does not ask again");
            }

            File.WriteAllText(Path, "{ broken");
            check(Left(Kind.Tiles3D, november) == 0 && !Take(Kind.StreetView, 1, november), "an unreadable ledger counts as spent");
        }
        finally
        {
            Path = real;
            Ledger.Dispose();
            Ledger = realLedger;
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
