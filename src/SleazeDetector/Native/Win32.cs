using System.Runtime.InteropServices;
using System.Text;

namespace SleazeDetector.Native;

internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount;
        public uint biCompression; public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    public const int SRCCOPY = 0x00CC0020;
    public const int CAPTUREBLT = 0x40000000;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    public const uint GA_ROOT = 2;
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    private const int DWMWA_CLOAKED = 14;

    /// <summary>UWP windows can be "visible" yet cloaked (suspended or on another virtual desktop).</summary>
    public static bool IsCloaked(IntPtr hWnd) => DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out var v, 4) == 0 && v != 0;

    public static string ClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static uint ProcessId(IntPtr hWnd) { GetWindowThreadProcessId(hWnd, out var pid); return pid; }

    /// <summary>Process owning the top-level window that is actually on screen at a point (click-through overlays are skipped).</summary>
    public static IntPtr RootAt(int x, int y) => GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), GA_ROOT);

    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int idx);
    // Undocumented (Windows 8+): the z-order band a window lives in (1 = normal desktop windows).
    [DllImport("user32.dll")] public static extern bool GetWindowBand(IntPtr hWnd, out uint band);

    /// <summary>Diagnostics: the top visible windows in z-order (class, pid, topmost, band, cloaked, rect).</summary>
    public static string ZOrder(int max, IntPtr mark)
    {
        var lines = new List<string>();
        int i = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowRect(h, out var r);
            if (r.Width <= 0 || r.Height <= 0) return true;
            GetWindowBand(h, out var band);
            bool top = (GetWindowLong(h, GWL_EXSTYLE) & 0x8) != 0;
            lines.Add($"#{i++}{(h == mark ? " [OVERLAY]" : "")} {ClassName(h)} pid{ProcessId(h)} top={top} band={band} cloaked={IsCloaked(h)} {r.Left},{r.Top},{r.Right},{r.Bottom}");
            return lines.Count < max || h != mark && lines.All(l => !l.Contains("[OVERLAY]"));
        }, IntPtr.Zero);
        return string.Join(" | ", lines.Take(max).Concat(lines.Skip(max).Where(l => l.Contains("[OVERLAY]"))));
    }
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int idx, int v);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);

    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_NOZORDER = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    /// <summary>Work area (screen minus taskbar) of the monitor a window is mostly on, or of the monitor the mouse
    /// pointer is on when no window is given, in physical pixels, and that monitor's scale (1 at 96 dpi). Null if
    /// Windows does not answer.</summary>
    public static (RECT Work, double Scale)? MonitorOf(IntPtr hwnd)
    {
        IntPtr mon;
        if (hwnd != IntPtr.Zero) mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        else if (GetCursorPos(out var p)) mon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        else return null;
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref info)) return null;
        double scale = GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        return (info.Work, scale);
    }

    /// <summary>Makes a window ignore mouse input and never take focus, so the overlay does not interfere with the game.</summary>
    public static void MakeClickThrough(IntPtr hwnd)
    {
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }
}
