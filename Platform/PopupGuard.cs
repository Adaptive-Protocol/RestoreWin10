using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace RestoreWin10;

/// <summary>
/// Closes menus and flyouts when the mouse is pressed outside them. Needed because the taskbar (and the tray
/// host) never take keyboard focus, so a click on them produces none of the deactivation that normally closes
/// a popup. A low-level mouse hook is installed only while something is open, and the hook itself never
/// blocks or delays the click: it just schedules the close.
/// </summary>
public static class PopupGuard
{
    delegate nint HookProc(int code, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public Win.POINT pt; public uint mouseData, flags, time; public nint extra; }

    [DllImport("user32.dll")] static extern nint SetWindowsHookEx(int id, HookProc fn, nint mod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint h);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint h, int code, nint w, nint l);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(Win.POINT p);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);

    record Watcher(object Owner, Func<nint, bool> Inside, Action Close);

    static readonly List<Watcher> _watchers = new();
    static nint _hook;
    static HookProc? _proc;   // kept alive for the lifetime of the hook

    /// <summary>
    /// While <paramref name="owner"/> is open, a mouse press on a window for which <paramref name="insideRoot"/>
    /// (given the top-level window under the cursor) is false runs <paramref name="close"/>.
    /// </summary>
    public static void Watch(object owner, Func<nint, bool> insideRoot, Action close)
    {
        Unwatch(owner);
        _watchers.Add(new Watcher(owner, insideRoot, close));
        if (_hook == 0)
        {
            _proc = Hook;
            _hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, GetModuleHandle(null), 0);
        }
    }

    public static void Unwatch(object owner)
    {
        _watchers.RemoveAll(w => ReferenceEquals(w.Owner, owner));
        if (_watchers.Count == 0 && _hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; }
    }

    static nint Hook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && (int)wParam is 0x201 or 0x204 or 0x207 or 0x20B)   // any button going down
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var under = WindowFromPoint(info.pt);
            var root = under == 0 ? 0 : Win.GetAncestor(under, Win.GA_ROOT);
            foreach (var w in _watchers.ToArray())
                if (!w.Inside(root))
                    Application.Current?.Dispatcher.BeginInvoke(w.Close, DispatcherPriority.Input);
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>
    /// "Inside" test for a popup menu of <paramref name="ownerHwnd"/>: the menu's own windows count (its submenus too),
    /// the owner itself does not. Pass <paramref name="menu"/> so a menu whose window ended up unowned (opened without
    /// a placement target) is still recognised: otherwise a press on one of its items would count as outside and close
    /// it before the item could act.
    /// </summary>
    public static Func<nint, bool> MenuOf(nint ownerHwnd, System.Windows.Controls.ContextMenu? menu = null) => root =>
    {
        if (root == 0 || root == ownerHwnd) return false;
        var rootOwner = Win.GetAncestor(root, Win.GA_ROOTOWNER);
        if (rootOwner == ownerHwnd) return true;
        if (menu != null && PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource src && src.Handle != 0)
            return root == src.Handle || rootOwner == Win.GetAncestor(src.Handle, Win.GA_ROOTOWNER);
        return false;
    };

    /// <summary>"Inside" test for a flyout window: itself and anything it owns (its own menus).</summary>
    public static Func<nint, bool> FlyoutOf(nint hwnd) => root => root != 0 && Win.GetAncestor(root, Win.GA_ROOTOWNER) == hwnd;
}
