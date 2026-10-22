using System.Runtime.InteropServices;

// BRUTAL's licence (v1.0) asks every product built with it to show the Brutal logo before its primary function,
// unaltered except for scaling and long enough to be legible (clause 2.2), and BRUTAL's copyright notice in its credits
// (2.1). The scene opens on both: Ahwoo's white logo, as published at github.com/ahwootech/brutal-branding (5d38373),
// and the notice beneath it, drawn at start-up with the system's font so that no font and no image of text ships.
internal static class Splash
{
    public const double Seconds = 2.6; // 0.3 s in, 2 s held, 0.3 s out
    public const string LogoFile = "LOGO_BRUTAL_WHT.png";
    public const string Notice = "BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.";

    public static float Opacity(double t) => (float)Math.Clamp(Math.Min(t / 0.3, (Seconds - t) / 0.3), 0.0, 1.0);

    // Scales RGBA down by averaging each output pixel's footprint, the colour weighted by alpha so that the transparent
    // background does not darken the edges.
    public static byte[] Shrink(byte[] rgba, int width, int height, int toWidth, int toHeight)
    {
        var output = new byte[toWidth * toHeight * 4];
        for (var y = 0; y < toHeight; y++)
        {
            int y0 = y * height / toHeight, y1 = Math.Max(y0 + 1, (y + 1) * height / toHeight);
            for (var x = 0; x < toWidth; x++)
            {
                int x0 = x * width / toWidth, x1 = Math.Max(x0 + 1, (x + 1) * width / toWidth);
                double r = 0, g = 0, b = 0, a = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = ((sy * width) + sx) * 4;
                        double w = rgba[i + 3];
                        r += rgba[i] * w;
                        g += rgba[i + 1] * w;
                        b += rgba[i + 2] * w;
                        a += w;
                    }
                }

                var o = ((y * toWidth) + x) * 4;
                if (a > 0)
                {
                    output[o] = (byte)Math.Round(r / a);
                    output[o + 1] = (byte)Math.Round(g / a);
                    output[o + 2] = (byte)Math.Round(b / a);
                }

                output[o + 3] = (byte)Math.Round(a / ((x1 - x0) * (y1 - y0)));
            }
        }

        return output;
    }

    // A line of white text whose alpha is GDI's antialiased coverage, in the system's Segoe UI, semibold if asked, or
    // null off Windows.
    public static byte[]? Text(string text, int pixels, out int width, out int height, bool bold = false)
    {
        width = height = 0;
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var dc = CreateCompatibleDC(0);
        var font = CreateFontW(-pixels, 0, 0, 0, bold ? 600 : 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI"); // ANTIALIASED_QUALITY, no ClearType fringes
        var oldFont = SelectObject(dc, font);
        GetTextExtentPoint32W(dc, text, text.Length, out var size);
        width = size.Cx + 2;
        height = size.Cy;
        var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 }; // top-down
        var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
        var oldBitmap = SelectObject(dc, bitmap);
        SetTextColor(dc, 0x00FFFFFF);
        SetBkColor(dc, 0);
        SetBkMode(dc, 2);
        TextOutW(dc, 1, 0, text, text.Length);
        GdiFlush();
        var bgrx = new byte[width * height * 4];
        Marshal.Copy(bits, bgrx, 0, bgrx.Length);
        SelectObject(dc, oldBitmap);
        SelectObject(dc, oldFont);
        DeleteObject(bitmap);
        DeleteObject(font);
        DeleteDC(dc);

        var rgba = new byte[bgrx.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = rgba[i + 1] = rgba[i + 2] = 255;
            rgba[i + 3] = Math.Max(bgrx[i], Math.Max(bgrx[i + 1], bgrx[i + 2]));
        }

        return rgba;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextSize
    {
        public int Cx;
        public int Cy;
    }

    // BITMAPINFO: its 40-byte header and the one colour entry, unused at 32 bits a pixel.
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
        public int Colour;
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic,
        uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(nint dc, string text, int length, out TextSize size);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(nint dc, uint color);

    [DllImport("gdi32.dll")]
    private static extern uint SetBkColor(nint dc, uint color);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(nint dc, int mode);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TextOutW(nint dc, int x, int y, string text, int length);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
}
