using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RestoreWin10;

/// <summary>
/// The monitors, in physical pixels, read fresh every time they are needed.
/// WPF's SystemParameters (WorkArea, PrimaryScreenWidth...) are deliberately not used anywhere for placement: WPF converts
/// them with the DPI the process started under, and that DPI is frozen for the life of the process. Started during a
/// Remote Desktop session at 200% and then used at the console at 100%, they come out at half size (that put the Start
/// menu at the top of the screen). Monitor rectangles and each monitor's own DPI have no such problem.
/// </summary>
static class Screens
{
    public sealed record Info(nint Handle, string Device, Win.RECT Bounds, Win.RECT Work, bool Primary)
    {
        /// <summary>This monitor's scale factor (1.0 = 100%).</summary>
        public double Scale => ScaleOf(Handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize; public Win.RECT rcMonitor, rcWork; public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    delegate bool MonitorEnumProc(nint hMon, nint hdc, ref Win.RECT rc, nint data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc fn, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(nint hMon, ref MONITORINFOEX mi);
    [DllImport("user32.dll")] static extern nint MonitorFromPoint(Win.POINT pt, int flags);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint h, int flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(nint hMon, int type, out uint x, out uint y);
    const int DEFAULTTOPRIMARY = 1, DEFAULTTONEAREST = 2;

    static Info? Describe(nint h)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (h == 0 || !GetMonitorInfo(h, ref mi)) return null;
        return new Info(h, mi.szDevice, mi.rcMonitor, mi.rcWork, (mi.dwFlags & 1) != 0);
    }

    public static List<Info> All()
    {
        var list = new List<Info>();
        EnumDisplayMonitors(0, 0, (nint h, nint dc, ref Win.RECT rc, nint data) =>
        {
            if (Describe(h) is { } i) list.Add(i);
            return true;
        }, 0);
        return list;
    }

    public static Info Primary => Describe(MonitorFromPoint(new Win.POINT(), DEFAULTTOPRIMARY))!;

    public static Info FromPoint(int x, int y) => Describe(MonitorFromPoint(new Win.POINT { X = x, Y = y }, DEFAULTTONEAREST)) ?? Primary;

    public static Info FromCursor() { Win.GetCursorPos(out var p); return FromPoint(p.X, p.Y); }

    public static Info FromWindow(nint h) => Describe(MonitorFromWindow(h, DEFAULTTONEAREST)) ?? Primary;

    /// <summary>The monitor a window is on (for a minimised window: where it will be restored).</summary>
    public static nint HandleOf(nint hwnd) => MonitorFromWindow(hwnd, DEFAULTTONEAREST);

    public static Info? ByDevice(string device) => All().FirstOrDefault(m => m.Device.Equals(device, StringComparison.OrdinalIgnoreCase));

    public static double ScaleOf(nint hMon) =>
        GetDpiForMonitor(hMon, 0 /* effective DPI */, out uint x, out _) == 0 && x > 0 ? x / 96.0 : 1.0;

    // ----- Windows' own multiple-display taskbar settings (Settings > Personalization > Taskbar > Multiple displays) -----

    const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    static int ReadAdvanced(string name, int fallback)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Advanced);
            return k?.GetValue(name) is int v ? v : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>"Show my taskbar on all displays" (on unless turned off).</summary>
    public static bool TaskbarOnAllDisplays => ReadAdvanced("MMTaskbarEnabled", 1) != 0;

    /// <summary>"Show taskbar buttons on": 0 all taskbars, 1 main taskbar and taskbar where window is open, 2 taskbar where window is open.</summary>
    public static int TaskbarButtonsMode => Math.Clamp(ReadAdvanced("MMTaskbarMode", 0), 0, 2);
}
