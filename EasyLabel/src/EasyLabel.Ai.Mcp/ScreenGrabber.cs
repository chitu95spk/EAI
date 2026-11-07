using System.Runtime.InteropServices;
using System.Text;
using OpenCvSharp;

namespace EasyLabel.Ai.Mcp;

/// <summary>
/// Grabs a rectangle of the real desktop (what is on screen now, other windows included) with GDI BitBlt; no extra package.
/// Windows only and needs the interactive desktop of the user who runs the app.
/// </summary>
internal static class ScreenGrabber
{
    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;
    private const uint SrcCopy = 0x00CC0020, CaptureBlt = 0x40000000;
    private const int DwmaExtendedFrameBounds = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rc { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rc rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rc rect, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, IntPtr bits, ref BitmapInfoHeader info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    private static bool _dpiSet;

    // Without this a non-DPI-aware process sees scaled (virtualised) screen coordinates and the region would be off on a 125%/150% display.
    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Screen capture needs Windows.");
        if (_dpiSet) return;
        SetProcessDPIAware();
        _dpiSet = true;
    }

    /// <summary>The whole virtual desktop (all monitors) in physical pixels.</summary>
    public static Rect VirtualScreen()
    {
        EnsureWindows();
        return new Rect(GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen), GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));
    }

    /// <summary>The on-screen rectangle of the first visible, not minimised top-level window whose title contains <paramref name="titlePart"/> (case-insensitive), or null.</summary>
    public static Rect? FindWindow(string titlePart)
    {
        EnsureWindows();
        Rect? found = null;
        var title = new StringBuilder(512);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            title.Clear();
            if (GetWindowTextW(hwnd, title, title.Capacity) <= 0 || title.ToString().IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) < 0) return true;
            // Extended frame bounds exclude the invisible resize border that GetWindowRect adds on Windows 10/11.
            Rc rc;
            if (DwmGetWindowAttribute(hwnd, DwmaExtendedFrameBounds, out rc, Marshal.SizeOf<Rc>()) != 0 && !GetWindowRect(hwnd, out rc)) return true;
            Rect r = new Rect(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
            if (r.Width < 16 || r.Height < 16) return true;
            found = r;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Copies <paramref name="region"/> (virtual-desktop pixels, clipped to the desktop) into a new BGR Mat.</summary>
    public static Mat Grab(Rect region)
    {
        Rect desk = VirtualScreen();
        Rect r = region.Intersect(desk);
        if (r.Width < 1 || r.Height < 1) throw new ArgumentException("The capture region is outside the screen (" + desk.X + "," + desk.Y + " " + desk.Width + "x" + desk.Height + ").");

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            mem = CreateCompatibleDC(screen);
            bmp = CreateCompatibleBitmap(screen, r.Width, r.Height);
            if (mem == IntPtr.Zero || bmp == IntPtr.Zero) throw new IOException("Could not create the capture bitmap.");
            old = SelectObject(mem, bmp);
            if (!BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, SrcCopy | CaptureBlt)) throw new IOException("BitBlt failed (is the desktop locked?).");
            // GetDIBits needs the bitmap to be out of every DC.
            SelectObject(mem, old);
            old = IntPtr.Zero;
            using var bgra = new Mat(r.Height, r.Width, MatType.CV_8UC4);
            var info = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = r.Width, Height = -r.Height, Planes = 1, BitCount = 32 };
            if (GetDIBits(mem, bmp, 0, (uint)r.Height, bgra.Data, ref info, 0) == 0) throw new IOException("GetDIBits failed.");
            var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            return bgr;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
