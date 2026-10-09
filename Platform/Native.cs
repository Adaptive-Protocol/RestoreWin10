using System.Runtime.InteropServices;
using System.Text;

namespace RestoreWin10;

/// <summary>Win32 declarations used by the taskbar.</summary>
static class Win
{
    public const int GWL_EXSTYLE = -20, GW_OWNER = 4, GA_ROOT = 2, GA_ROOTOWNER = 3;
    public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
    public const int SW_HIDE = 0, SW_SHOW = 5, SW_MINIMIZE = 6, SW_RESTORE = 9;
    public const uint WM_CLOSE = 0x10, SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public const int ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    public const int ABN_POSCHANGED = 1, ABS_AUTOHIDE = 1;
    public const uint ABE_TOP = 1, ABE_BOTTOM = 3;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA { public int cbSize; public nint hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public nint lParam; }

    public delegate bool EnumProc(nint hwnd, nint lParam);

    [DllImport("shell32.dll")] public static extern nint SHAppBarMessage(int msg, ref APPBARDATA data);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc fn, nint lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] public static extern nint GetWindow(nint h, int cmd);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint h, int flags);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(nint h);
    [DllImport("user32.dll")] public static extern nint SendMessage(nint h, uint msg, nint w, nint l);
    [DllImport("user32.dll")] public static extern bool PostMessage(nint h, uint msg, nint w, nint l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint h, StringBuilder sb, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(nint h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] public static extern int SetWindowLong(nint h, int idx, int val);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsZoomed(nint h);

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT minPos, maxPos; public RECT normal; }
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(nint h, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPlacement(nint h, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] public static extern ushort CascadeWindows(nint parent, uint how, ref RECT area, uint count, nint[] kids);
    [DllImport("user32.dll")] public static extern ushort TileWindows(nint parent, uint how, ref RECT area, uint count, nint[] kids);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int idx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindow(string cls, string? title);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] public static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(nint h, int flags, StringBuilder sb, ref int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint h, int attr, out int value, int size);

    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags; public RECT rcDestination, rcSource; public byte opacity; public int fVisible, fSourceClientAreaOnly;
    }
    public const uint DWM_TNP_RECTDESTINATION = 1, DWM_TNP_OPACITY = 4, DWM_TNP_VISIBLE = 8;

    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(nint dest, nint src, out nint thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(nint thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(nint thumb, ref DWM_THUMBNAIL_PROPERTIES p);
    [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(nint thumb, out SIZE size);

    [DllImport("user32.dll")] public static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] public static extern bool RegisterShellHookWindow(nint h);
    [DllImport("user32.dll")] public static extern bool DeregisterShellHookWindow(nint h);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(nint h);
    [DllImport("user32.dll")] public static extern bool EndTask(nint h, bool shutDown, bool force);
    [DllImport("user32.dll")] static extern nint HungWindowFromGhostWindow(nint ghost);
    [DllImport("user32.dll")] static extern nint GhostWindowFromHungWindow(nint hung);

    public const int HSHELL_FLASH = 0x8006;

    // Windows stands a "ghost" window in for a hung one. These two user32 exports map between them; they
    // have shipped unchanged since Vista but are undocumented, so a missing export just means "no ghost".
    public static nint HungFromGhost(nint ghost) { try { return HungWindowFromGhostWindow(ghost); } catch { return 0; } }
    public static nint GhostFromHung(nint hung) { try { return GhostWindowFromHungWindow(hung); } catch { return 0; } }

    /// <summary>Asks a window to close. A hung window gets Windows' own "is not responding" dialog (EndTask) instead.
    /// A program running as administrator refuses RestoreWin10's message (UIPI); Windows is then asked to close it
    /// (EndTask without force, as Explorer and Task Manager do), so it can still ask to save.</summary>
    public static void CloseWindow(nint h)
    {
        var hung = ClassName(h) == "Ghost" ? HungFromGhost(h) : (IsHungAppWindow(h) ? h : 0);
        if (hung != 0) EndTask(hung, false, false);
        else if (!PostMessage(h, WM_CLOSE, 0, 0)) EndTask(h, false, false);
    }

    public static string Text(nint h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(nint h)
    {
        var sb = new StringBuilder(128);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string? ExePath(uint pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try
        {
            var sb = new StringBuilder(1024); int n = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Bring a window to the front, restoring it if minimised.</summary>
    public static void Activate(nint h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        if (SetForegroundWindow(h)) return;
        // Not allowed to take focus: borrow the foreground thread's input state briefly.
        var fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _), me = GetCurrentThreadId();
        AttachThreadInput(me, fgThread, true);
        BringWindowToTop(h); SetForegroundWindow(h);
        AttachThreadInput(me, fgThread, false);
    }

    /// <summary>Press a Windows-key shortcut (e.g. Win+D) on the user's behalf.</summary>
    public static void WinCombo(byte key)
    {
        keybd_event(0x5B, 0, 0, 0); keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, 2, 0); keybd_event(0x5B, 0, 2, 0);
    }
}
