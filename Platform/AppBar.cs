using System.Runtime.InteropServices;

namespace RestoreWin10;

/// <summary>
/// App-bar requests (dock, position, auto-hide state) sent straight to Explorer's taskbar window.
///
/// Why not just SHAppBarMessage: it delivers to whichever "Shell_TrayWnd" window it finds first, and while RestoreWin10
/// hosts the notification area that is RestoreWin10's own window, on RestoreWin10's own UI thread. For a caller on that same
/// thread, Windows does not create the shared-memory handle the request normally carries: it puts a pointer into this
/// process's memory in its place. Relayed to Explorer that pointer is meaningless, so Explorer accepted our bars'
/// positions without being able to read them and never kept their strip clear of maximised windows. (Every other
/// program's requests are fine: theirs cross a thread and carry a real handle.)
///
/// This builds the very same request Windows does (the layout below was read off live traffic) with a real handle,
/// created for Explorer's process, and delivers it to Explorer's own window.
/// </summary>
static class AppBar
{
    // The shared-memory helpers SHAppBarMessage itself uses (exported by name from shlwapi on current Windows).
    [DllImport("shlwapi.dll")] static extern nint SHAllocShared(nint data, uint size, uint destPid);
    [DllImport("shlwapi.dll")] static extern nint SHLockShared(nint handle, uint pid);
    [DllImport("shlwapi.dll")] static extern bool SHUnlockShared(nint view);
    [DllImport("shlwapi.dll")] static extern bool SHFreeShared(nint handle, uint pid);
    [DllImport("user32.dll")] static extern nint SendMessageTimeout(nint h, uint msg, nint w, nint l, int flags, int timeoutMs, out nint result);

    const uint WM_COPYDATA = 0x4A;
    const int ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETTASKBARPOS = 5;

    static nint s_tray;

    /// <summary>Explorer's own taskbar window: a "Shell_TrayWnd" that is not ours. 0 when Explorer has none.</summary>
    static nint ExplorerTray()
    {
        if (s_tray != 0 && Win.IsWindow(s_tray) && Win.ClassName(s_tray) == "Shell_TrayWnd") return s_tray;
        s_tray = 0;
        uint own = (uint)Environment.ProcessId;
        Win.EnumWindows((h, _) =>
        {
            if (Win.ClassName(h) != "Shell_TrayWnd") return true;
            Win.GetWindowThreadProcessId(h, out uint pid);
            if (pid == own) return true;
            s_tray = h;
            return false;
        }, 0);
        return s_tray;
    }

    /// <summary>Same contract as SHAppBarMessage: the result, and for QUERYPOS/SETPOS/GETTASKBARPOS the rectangle
    /// (and edge) Explorer answers with, written back into <paramref name="abd"/>.</summary>
    public static nint Send(int message, ref Win.APPBARDATA abd)
    {
        try { return SendDirect(message, ref abd); }
        catch (Exception ex)
        {
            CrashLog.Write("app bar request", ex);   // never leave a bar undocked over this: the ordinary route
            return Win.SHAppBarMessage(message, ref abd);
        }
    }

    static nint SendDirect(int message, ref Win.APPBARDATA abd)
    {
        var tray = ExplorerTray();
        if (tray == 0) return Win.SHAppBarMessage(message, ref abd);   // no Explorer taskbar: the ordinary route
        Win.GetWindowThreadProcessId(tray, out uint explorer);

        // TRAYAPPBARDATA, 64 bytes: APPBARDATA3264 { cbSize=40, hWnd (32-bit), uCallbackMessage, uEdge, rc, lParam (64-bit) },
        // then the message at 40, the shared-memory handle (64-bit) at 48, and the process that handle belongs to at 56.
        nint buf = Marshal.AllocHGlobal(64), cds = Marshal.AllocHGlobal(Marshal.SizeOf<long>() * 3);
        nint shared = 0;
        try
        {
            for (int i = 0; i < 64; i += 8) Marshal.WriteInt64(buf, i, 0);
            Marshal.WriteInt32(buf, 0, 40);
            Marshal.WriteInt32(buf, 4, (int)abd.hWnd);
            Marshal.WriteInt32(buf, 8, (int)abd.uCallbackMessage);
            Marshal.WriteInt32(buf, 12, (int)abd.uEdge);
            Marshal.WriteInt32(buf, 16, abd.rc.Left); Marshal.WriteInt32(buf, 20, abd.rc.Top);
            Marshal.WriteInt32(buf, 24, abd.rc.Right); Marshal.WriteInt32(buf, 28, abd.rc.Bottom);
            Marshal.WriteInt64(buf, 32, abd.lParam);
            Marshal.WriteInt32(buf, 40, message);
            Marshal.WriteInt32(buf, 56, (int)explorer);
            bool answers = message is ABM_QUERYPOS or ABM_SETPOS or ABM_GETTASKBARPOS;
            if (answers)
            {
                shared = SHAllocShared(buf, 40, explorer);   // Explorer writes its answer into this copy
                if (shared == 0) return Win.SHAppBarMessage(message, ref abd);
                Marshal.WriteInt64(buf, 48, shared);
            }

            // COPYDATASTRUCT { ULONG_PTR dwData = 0 (app-bar request); DWORD cbData; PVOID lpData }
            Marshal.WriteInt64(cds, 0, 0);
            Marshal.WriteInt64(cds, 8, 64);
            Marshal.WriteIntPtr(cds, 16, buf);
            SendMessageTimeout(tray, WM_COPYDATA, abd.hWnd, cds, 2 /* SMTO_ABORTIFHUNG */, 5000, out var result);

            if (answers)
            {
                var view = SHLockShared(shared, explorer);
                if (view != 0)
                {
                    abd.uEdge = (uint)Marshal.ReadInt32(view, 12);
                    abd.rc = new Win.RECT
                    {
                        Left = Marshal.ReadInt32(view, 16), Top = Marshal.ReadInt32(view, 20),
                        Right = Marshal.ReadInt32(view, 24), Bottom = Marshal.ReadInt32(view, 28),
                    };
                    SHUnlockShared(view);
                }
            }
            return result;
        }
        finally
        {
            if (shared != 0) SHFreeShared(shared, explorer);
            Marshal.FreeHGlobal(buf);
            Marshal.FreeHGlobal(cds);
        }
    }
}
