using System.Runtime.InteropServices;

// --modal-drill: the window's own measuring run of what a person's hand does to it, for the frame gaps it leaves
// (FrameGap.cs). From a thread of its own, after a quiet spell: the title bar held still for 1.5 s (Windows' modal move
// loop, entered by the keyboard's move and left by Enter), the border dragged for 1.5 s (the modal size loop, the right
// edge moved by the arrow keys), then a jump in size outside any modal loop, as a maximised window restored. Messages go
// to this process's own window only, posted to its queue: no other window and no real input is touched.
internal static class ModalDrill
{
    private const uint WmSysCommand = 0x0112, WmKeyDown = 0x0100, WmKeyUp = 0x0101;
    private const int ScMove = 0xF010, ScSize = 0xF000, VkReturn = 0x0D, VkRight = 0x27;

    public static void Start(Action<string> say, Action<int, int> resize)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        new Thread(() =>
        {
            var hwnd = Own();
            if (hwnd == 0)
            {
                say("no window of this process found");
                return;
            }

            Thread.Sleep(2000);
            say("title bar held 1500 ms");
            PostMessageW(hwnd, WmSysCommand, ScMove, 0);
            Thread.Sleep(1500);
            Key(hwnd, VkReturn);

            Thread.Sleep(2000);
            say("border dragged 1500 ms");
            PostMessageW(hwnd, WmSysCommand, ScSize, 0);
            for (var step = 0; step < 15; step++)
            {
                Thread.Sleep(100);
                Key(hwnd, VkRight);
            }

            Key(hwnd, VkReturn);

            Thread.Sleep(2000);
            say("size jump to 1280x720");
            resize(1280, 720);
            Thread.Sleep(2000);
            say("done");
        }) { IsBackground = true, Name = "modal drill" }.Start();
    }

    private static void Key(nint hwnd, int key)
    {
        PostMessageW(hwnd, WmKeyDown, key, 1);
        PostMessageW(hwnd, WmKeyUp, key, unchecked((nint)0xC0000001));
    }

    // The GLFW window of this process (its class is GLFW30), for the drill and for PumpTimer.
    internal static nint Own()
    {
        nint found = 0;
        var pid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            var name = new char[16];
            var length = GetClassNameW(hwnd, name, name.Length);
            if (owner == pid && new string(name, 0, length) == "GLFW30")
            {
                found = hwnd;
                return false;
            }

            return true;
        }, 0);
        return found;
    }

    private delegate bool EnumProc(nint hwnd, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc proc, nint data);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, char[] name, int count);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
}
