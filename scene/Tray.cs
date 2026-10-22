using System.Runtime.InteropServices;

// Delta's icon in the notification area, beside the clock, while its window is hidden there: a click on it brings the
// window back. Its clicks reach a message-only window made on the render loop's thread, whose messages GLFW's
// PollEvents dispatches, so that Clicked runs where GLFW may be called.
internal sealed unsafe class Tray : IDisposable
{
    private const uint Callback = 0x8000 + 1; // WM_APP + 1
    private const uint WmLButtonUp = 0x0202, WmRButtonUp = 0x0205;
    private const uint NimAdd = 0, NimDelete = 2, NifMessage = 1, NifIcon = 2, NifTip = 4;
    private static readonly nint MessageOnly = -3; // HWND_MESSAGE

    private readonly WndProc proc; // kept alive: Windows calls it
    private readonly nint window;
    private readonly nint icon;
    private bool shown;

    public event Action? Clicked;

    public Tray()
    {
        proc = Proc;
        var module = GetModuleHandleW(null);
        fixed (char* name = "BrickyardTray")
        {
            var wc = new WndClassEx { Size = (uint)sizeof(WndClassEx), Proc = Marshal.GetFunctionPointerForDelegate(proc), Instance = module, ClassName = name };
            RegisterClassExW(ref wc);
            window = CreateWindowExW(0, name, name, 0, 0, 0, 0, 0, MessageOnly, 0, module, 0);
        }

        icon = Face(32);
    }

    // Shows the icon with its tooltip, if it is not shown already.
    public void Show(string tip)
    {
        if (shown || window == 0)
        {
            return;
        }

        var data = Data(NifMessage | NifIcon | NifTip);
        data.Tip = tip.Length > 127 ? tip[..127] : tip;
        shown = Shell_NotifyIconW(NimAdd, ref data);
    }

    public void Hide()
    {
        if (!shown)
        {
            return;
        }

        var data = Data(0);
        Shell_NotifyIconW(NimDelete, ref data);
        shown = false;
    }

    public void Dispose()
    {
        Hide();
        if (icon != 0)
        {
            DestroyIcon(icon);
        }

        if (window != 0)
        {
            DestroyWindow(window);
        }
    }

    private NotifyIconData Data(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = window,
        Id = 1,
        Flags = flags,
        CallbackMessage = Callback,
        Icon = icon,
        Tip = "",
        Info = "",
        InfoTitle = "",
    };

    private nint Proc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == Callback && (uint)lParam is WmLButtonUp or WmRButtonUp)
        {
            Clicked?.Invoke();
            return 0;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    // Delta's face, drawn: a pale shell, a dark visor and two cyan eyes, as an icon of size by size pixels.
    private static nint Face(int size)
    {
        var info = new BitmapInfoHeader { Size = (uint)sizeof(BitmapInfoHeader), Width = size, Height = -size, Planes = 1, BitCount = 32 };
        var color = CreateDIBSection(0, ref info, 0, out var bits, 0, 0);
        if (color == 0)
        {
            return 0;
        }

        var pixels = (uint*)bits;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var u = (x + 0.5f) / size;
                var v = (y + 0.5f) / size;
                uint argb = 0;
                if (u is > 0.06f and < 0.94f && v is > 0.14f and < 0.86f)
                {
                    argb = 0xFFB3AD9E; // shell
                }

                if (u is > 0.14f and < 0.86f && v is > 0.24f and < 0.76f)
                {
                    argb = 0xFF060709; // visor
                }

                if (v is > 0.38f and < 0.54f && (u is > 0.24f and < 0.42f || u is > 0.58f and < 0.76f))
                {
                    argb = 0xFF59CCFF; // eyes
                }

                if (v is > 0.64f and < 0.68f && u is > 0.36f and < 0.64f)
                {
                    argb = 0xFF2E8FB3; // mouth
                }

                pixels[(y * size) + x] = argb;
            }
        }

        var mask = CreateBitmap(size, size, 1, 1, 0);
        var iconInfo = new IconInfo { IsIcon = 1, Mask = mask, Color = color };
        var result = CreateIconIndirect(ref iconInfo);
        DeleteObject(color);
        DeleteObject(mask);
        return result;
    }

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint Size, Style;
        public nint Proc;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public char* MenuName, ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX, HotspotY;
        public nint Mask, Color;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, char* className, char* title, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(int width, int height, uint planes, uint bitCount, nint bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);
}
