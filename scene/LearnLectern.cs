using Delta.LearnReader;
using Delta.SceneModules;
using System.Net;
using System.Security.Cryptography;
using System.Text;

// The lectern's book as the scene runs it. The reader that lays out its pages is a mod (modules/LearnReader): the image
// in modules\reader\LearnReader.Module.dll beside scene.exe, or --reader-module's, is loaded when there is one and
// reloaded when a build replaces it, the book then reopened at what was open; an image put there while the scene runs is
// loaded too, the file looked at once a second. Without one, or once the mod throws or draws a
// picture of the wrong size, the same code built into the scene reads, until a new image comes. The host keeps what may
// be read (LearnHost): the learn site's pages only, the text drawn by the scene, links opened in the browser.
internal sealed class LearnLectern : IDisposable
{
    private readonly ReaderModule builtIn = new();
    private readonly IReaderHost host;
    private readonly string? path;
    private ModuleSession<IReaderModule>? session;
    private (long Length, DateTime Written) tried;
    private double nextTry;
    private bool failed;
    private Action<IReaderModule>? last;
    private (object? Source, ulong Revision) seen;
    private ulong revision;

    public LearnLectern(IReaderHost host, string? path)
    {
        (this.host, this.path) = (host, path is null ? null : Path.GetFullPath(path));
        builtIn.Initialize(host);
        if (!Start()) Console.WriteLine($"READER learn-reader: no mod at {this.path ?? "(none)"}, the built-in reader reads");
    }

    // Loads the mod's image when there is one not tried yet; true when it now reads.
    private bool Start()
    {
        if (path is null || Stamp(path) is var stamp && (stamp.Length == 0 || stamp == tried)) return false;
        tried = stamp;
        try
        {
            session = new(path, "LearnReader.Module", "Delta.LearnReader.ReaderModule", "learn-reader", m => m.Id, m => m.Initialize(host), "READER");
            Console.WriteLine($"READER learn-reader v{session.Version} sha256 {session.CodeHash}: the mod reads the lectern's book");
            return true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"READER learn-reader mod refused ({e.GetType().Name}: {e.Message}); the built-in reader reads");
            return false;
        }
    }

    private static (long Length, DateTime Written) Stamp(string file)
    {
        try { return new FileInfo(file) is { Exists: true } info ? (info.Length, info.LastWriteTimeUtc) : (0, default); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (0, default); }
    }

    // Which reader lays out the book: the mod and its version, or the built-in one.
    public string Source => Mod is not null ? $"mod v{session!.Version}" : "built-in";

    public int Reloads => session?.Reloads ?? 0;

    private IReaderModule? Mod => session is not null && !failed ? session.Module : null;

    // Once a frame on the render thread: a new image of the mod replaces the running one, and the book is reopened.
    public void Poll(double now)
    {
        if (session is null)
        {
            if (now < nextTry) return;
            nextTry = now + 1;
            if (!Start()) return;
        }
        else if (!session.Poll(now))
        {
            return;
        }

        failed = false;
        Use(last);
    }

    // The picture to show, its revision the host's own: a new one whenever the reader or its picture changes.
    public ReaderPicture Picture
    {
        get
        {
            IReaderModule reader = builtIn;
            var picture = builtIn.Picture;
            if (Mod is { } mod)
            {
                try
                {
                    var p = mod.Picture;
                    if (!p.Valid) throw new InvalidDataException($"its picture is not {ReaderPicture.Width}×{ReaderPicture.Height} RGBA");
                    (reader, picture) = (mod, p);
                }
                catch (Exception e)
                {
                    Fail(e);
                    picture = builtIn.Picture;
                }
            }

            if (!ReferenceEquals(seen.Source, reader) || seen.Revision != picture.Revision)
            {
                seen = (reader, picture.Revision);
                revision++;
            }

            return new(picture.Rgba, revision);
        }
    }

    public string Title => Mod?.Title ?? builtIn.Title;

    public void OpenCourse(string name, string url) => Use(last = r => r.OpenCourse(name, url));

    public void OpenDepartment(string name, IReadOnlyList<ReaderItem> modules) => Use(last = r => r.OpenDepartment(name, modules));

    public void Click(float u, float v) => Use(r => r.Click(u, v));

    private void Use(Action<IReaderModule>? act)
    {
        if (act is null) return;
        if (Mod is { } mod)
        {
            try
            {
                act(mod);
                return;
            }
            catch (Exception e)
            {
                Fail(e);
                if (last is { } open && open != act) open(builtIn);
            }
        }

        act(builtIn);
    }

    private void Fail(Exception e)
    {
        failed = true;
        Console.Error.WriteLine($"READER learn-reader mod v{session?.Version} failed ({e.GetType().Name}: {e.Message}); the built-in reader reads until a new image");
    }

    public void Dispose()
    {
        builtIn.Dispose();
        session?.Dispose();
    }

    // Part of --selftest: the reader's own checks with the scene's host, the host's limits, and a lectern whose mod is
    // missing reading with the built-in reader.
    internal static void SelfTest(Action<bool, string> check)
    {
        var opened = new List<string>();
        var host = new LearnHost(opened.Add);
        ReaderModule.SelfTest(host, check);
        check(opened is ["https://spareilleux.github.io/learn/streeling/dept/"], "the left page's link opens the department on the site");
        host.Browse("https://claude.ai/artifact/x");
        check(opened.Count == 1 && LearnHost.Read("https://claude.ai/artifact/x").Result.Html is null, "the lectern opens and reads only the learn site's pages");
        check(LearnHost.French("https://spareilleux.github.io/learn/streeling/x/") == "https://spareilleux.github.io/learn/fr/streeling/x/"
            && LearnHost.French("https://spareilleux.github.io/learn/fr/a/") == "https://spareilleux.github.io/learn/fr/a/", "the French page of a page is under /learn/fr/");
        using var lectern = new LearnLectern(host, Path.Combine(Path.GetTempPath(), "delta-no-reader", "LearnReader.Module.dll"));
        var first = lectern.Picture;
        lectern.OpenDepartment("Department", [new ReaderItem("A module", "https://spareilleux.github.io/learn/streeling/dept/a/")]);
        check(lectern.Source == "built-in" && first.Valid && lectern.Picture.Revision > first.Revision && lectern.Title == "Department",
            "without its mod the lectern reads with the built-in reader");
    }
}

// What the lectern's reader may do outside itself: read a page of the learn site (HTTPS, no cookie, in French first when
// the window is, kept in %LOCALAPPDATA%\delta\learn-cache for a day and read from there however old when offline), draw
// a line of text, and open a page of the learn site in the browser.
internal sealed class LearnHost(Action<string> browse) : IReaderHost
{
    internal const string Site = ReaderModule.Site;
    private static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(15) };

    public string Language => Words.Language;

    public Task<ReaderPage> Fetch(string url) => Read(url);

    public byte[]? Text(string text, int pixels, bool bold, out int width, out int height) => Splash.Text(text, pixels, out width, out height, bold);

    public void Browse(string url)
    {
        if (url.StartsWith(Site, StringComparison.Ordinal)) browse(url);
    }

    internal static async Task<ReaderPage> Read(string url)
    {
        if (!url.StartsWith(Site, StringComparison.Ordinal))
        {
            return new(null, url, Words.Say("Only the learn site's pages are read here.", "Seules les pages du site learn se lisent ici."));
        }

        var tries = Words.Language == "fr" ? new[] { French(url), url }.Distinct().ToArray() : [url];
        string? why = null;
        foreach (var at in tries)
        {
            var file = CacheFile(at);
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(1))
            {
                return new(await File.ReadAllTextAsync(file), at, "");
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, at);
                request.Headers.UserAgent.ParseAdd("Delta-scene/1.0");
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (response.StatusCode == HttpStatusCode.NotFound) continue;
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 4_000_000) throw new InvalidDataException("page too large");
                var html = await response.Content.ReadAsStringAsync();
                if (html.Length > 4_000_000) throw new InvalidDataException("page too large");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, html);
                return new(html, at, "");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
            {
                why = e.Message;
                if (File.Exists(file)) return new(await File.ReadAllTextAsync(file), at, "");
            }
        }

        return new(null, url, Words.Say($"The page could not be read ({why ?? "not found"}).", $"La page n'a pas pu être lue ({why ?? "introuvable"})."));
    }

    // The French page of an English one: /learn/fr/… for /learn/….
    internal static string French(string url) =>
        url.StartsWith(Site + "fr/", StringComparison.Ordinal) ? url : Site + "fr/" + url[Site.Length..];

    private static string CacheFile(string url) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", "learn-cache",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24] + ".html");
}
