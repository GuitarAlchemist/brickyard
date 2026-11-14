namespace Delta.AgentOffice;

// A document on this machine that the brain rewrites whole (a rename), looked at at most once a second and read again
// when its time or its length changed: the IX workshop's knot and origami table.
internal sealed class DocumentWatch(string path, int maxBytes)
{
    private double checkedAt = double.NegativeInfinity;
    private (DateTime Time, long Length) seen = (default, -2);

    public string Path => path;

    // What changed since the last look, or null: absent (no bytes, length 0), too big (no bytes, its length) or read.
    public (bool Exists, long Length, byte[]? Bytes)? Poll(double seconds)
    {
        if (seconds - checkedAt < 1 && seconds >= checkedAt) return null;
        checkedAt = seconds;
        try
        {
            var info = new FileInfo(path);
            var now = info.Exists ? (info.LastWriteTimeUtc, info.Length) : (default(DateTime), -1L);
            if (now == seen) return null;
            (bool, long, byte[]?) read = !info.Exists ? (false, 0L, null) : info.Length > maxBytes ? (true, info.Length, null) : (true, info.Length, File.ReadAllBytes(path));
            seen = now;
            return read;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // being replaced: the next look reads it whole
        }
    }

    // Where a document lives: the variable's path when set, else %LOCALAPPDATA%\delta\file.
    public static string Place(string variable, string file) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } set ? set
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", file);
}
