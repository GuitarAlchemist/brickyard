namespace Delta.SceneModules;

// The book open on the hall's lectern: a module lays out its two pages, the host keeps the network, the text and the
// screen. The module is the part that changes as it is worked on (how a page is read and laid out), so it is a mod the
// scene reloads while it runs; the host's part, what may be read and from where, stays in the scene.

// What the host offers the module. Fetch reads a page of the learn site only (spareilleux.github.io/learn, HTTPS, no
// cookie, a day's cache, the cache however old offline), in French first when the window is; Text draws a line of
// text, RGBA with its coverage in alpha, null when it cannot; Browse opens a page of the learn site in the browser.
// Fetch and Text may be called from any thread.
public interface IReaderHost
{
    string Language { get; }
    Task<ReaderPage> Fetch(string url);
    byte[]? Text(string text, int pixels, bool bold, out int width, out int height);
    void Browse(string url);
}

// A page read: its HTML and the address it was read at, or no HTML and why, in the window's language.
public sealed record ReaderPage(string? Html, string Url, string Why);

// An entry of a book's outline: a lesson of a course, a module of a department.
public sealed record ReaderItem(string Title, string Url);

// The two pages side by side, RGBA8 from the top left; each new picture has a new revision.
public readonly record struct ReaderPicture(byte[] Rgba, ulong Revision)
{
    public const int Width = 2000, Height = 1300, ByteCount = Width * Height * 4;

    public bool Valid => Rgba is { Length: ByteCount } && Revision > 0;
}

public interface IReaderModule : IDisposable
{
    string Id { get; }
    void Initialize(IReaderHost host);

    // Read from the render thread; the module may publish a new one from any thread.
    ReaderPicture Picture { get; }
    string Title { get; }

    // A course: its own page, and its lessons as the site lists them.
    void OpenCourse(string name, string url);

    // A department of Streeling University: its modules as the gallery lists them.
    void OpenDepartment(string name, IReadOnlyList<ReaderItem> modules);

    // A click at u, v across the picture, 0 to 1 each from its top left.
    void Click(float u, float v);
}
