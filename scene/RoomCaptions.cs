// Whether the caption shows what is said in the room without « Delta »: in a window that is shared or streamed, other
// people's words would show too (NOTES, Known problems). T turns those captions off and on; what was said to Delta or
// Claude, and what Delta says, still shows. The choice outlives the window, as a marker file beside the one it leaves
// when the user closes it.
internal static class RoomCaptions
{
    private static readonly string Marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", "captions-room.off");

    public static bool Off { get; private set; } = File.Exists(Marker);

    // Whether a caption for whom it was heard shows: everything while room captions are on, else only Delta's own
    // conversation.
    public static bool Shows(string to, bool roomOff) => !roomOff || to is "claude" or "delta" or "said";

    public static void Toggle()
    {
        Off = !Off;
        try
        {
            if (Off)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
                File.WriteAllText(Marker, "");
            }
            else
            {
                File.Delete(Marker);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"SCENE room captions: the choice is not kept ({e.GetType().Name})");
        }

        Console.WriteLine($"SCENE room captions {(Off ? "off" : "on")}");
    }

    public static void SelfTest(Action<bool, string> check)
    {
        check(new[] { "claude", "delta", "said", "wake", "dropped", "hearing", "Gaia" }.All(to => Shows(to, false)), "with room captions on, every caption shows");
        check(Shows("claude", true) && Shows("delta", true) && Shows("said", true), "with them off, what was said to Delta or Claude, and what Delta says, still show");
        check(!Shows("wake", true) && !Shows("dropped", true) && !Shows("hearing", true) && !Shows("Gaia", true), "with them off, the room's words do not show");
    }
}
