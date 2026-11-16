using System.Runtime.InteropServices;

// A timer on the window whose callback Windows calls from inside its modal move and size loops, where PollEvents stays
// while a hand holds the title bar or the border, moving or not: the window's own move and size callbacks come only
// when it moves or changes size. Outside those loops PollEvents dispatches the ticks too, so the callback says whether
// the pump has been held.
internal sealed class PumpTimer : IDisposable
{
    private delegate void TimerProc(nint hwnd, uint message, nuint id, uint time);

    private readonly TimerProc proc; // kept alive while Windows may call it
    private readonly nint hwnd;
    private nuint id;

    public PumpTimer(nint hwnd, uint milliseconds, Action tick)
    {
        this.hwnd = hwnd;
        proc = (_, _, _, _) => tick();
        id = hwnd == 0 ? 0 : SetTimer(hwnd, 0x5EED, milliseconds, proc);
    }

    public bool Running => id != 0;

    public void Dispose()
    {
        if (id != 0)
        {
            KillTimer(hwnd, id);
            id = 0;
        }
    }

    [DllImport("user32.dll")]
    private static extern nuint SetTimer(nint hwnd, nuint id, uint milliseconds, TimerProc proc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(nint hwnd, nuint id);
}
