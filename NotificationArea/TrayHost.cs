using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RestoreWin10;

/// <summary>One notification-area icon registered by some program.</summary>
public sealed class TrayIcon : Notifier
{
    string _tip = "", _owner = "";
    string? _ownerName;
    BitmapSource? _icon;

    public nint Hwnd { get; init; }
    /// <summary>The process that owned <see cref="Hwnd"/> when the icon was registered (window handles get reused).</summary>
    public uint Pid { get; init; }
    public uint Uid { get; init; }
    public string Key { get; init; } = "";
    public string Owner { get => _owner; set { _owner = value; _ownerName = null; Raise(nameof(DisplayTip)); } }   // executable path of the program that owns the icon
    public uint Callback { get; set; }               // message to post back to the owner on mouse events
    public int Version { get; set; }
    public bool Hidden { get; set; }

    public string Tooltip { get => _tip; set { Set(ref _tip, value); Raise(nameof(DisplayTip)); } }
    public BitmapSource? Icon { get => _icon; set => Set(ref _icon, value); }

    /// <summary>
    /// What hovering the icon shows: the program's own tooltip, or when it gave none, the program's name ("Claude", from
    /// its file description). Some programs send no tooltip when they register again after a taskbar (re)starts:
    /// Electron apps re-add their icon with only the icon and the callback, so after RestoreWin10 is updated or
    /// restarted they would otherwise show nothing until they change their tooltip.
    /// </summary>
    public string DisplayTip => _tip.Length > 0 ? _tip : _ownerName ??= ProgramName(_owner);

    internal static string ProgramName(string exe)
    {
        if (exe.Length == 0) return "";
        try
        {
            var d = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(d)) return d;
        }
        catch { }
        return Path.GetFileNameWithoutExtension(exe);
    }
}

/// <summary>
/// Acts as the notification area's host. Programs locate the tray by looking up a top-level window of class
/// "Shell_TrayWnd" and sending it WM_COPYDATA; we own such a window, so (after asking everyone to re-register
/// with the TaskbarCreated broadcast) their icons come to us. Anything that is not a tray-icon message is
/// forwarded to Explorer's real taskbar window so appbar docking and the like keep working.
/// <para>
/// What other programs can do here (threat model F-06). Windows' UIPI stops programs at a lower integrity level from
/// sending anything at all: RestoreWin10 deliberately adds no exception (no ChangeWindowMessageFilter), and must not.
/// From the rest, icons are accepted only for a live window, and a click is only ever posted to the process that owned
/// that window when the icon was registered, with an application-range message (WM_USER to 0xFFFF), never a system one
/// such as WM_CLOSE. Of the WM_COPYDATA requests, only app-bar requests and icon-position queries are relayed to
/// Explorer; the obsolete "load this object inside Explorer" request (SHLoadInProc) and anything unknown are dropped
/// and noted in tray.log. WM_COMMAND and private (WM_USER and up) messages are still relayed, because Windows' own
/// components and some programs send them to whatever window is called Shell_TrayWnd; a sender can reach Explorer's
/// window directly anyway, so the relay gives it nothing new. tray.log notes each kind once, to tighten this later.
/// </para>
/// </summary>
public sealed class TrayHost : IDisposable
{
    const uint WM_COPYDATA = 0x4A, WM_COMMAND = 0x111;
    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_STATE = 8;
    const int WS_POPUP = unchecked((int)0x80000000), WS_EX_TOPMOST = 8, SMTO_ABORTIFHUNG = 2;

    delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize; public uint style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public nint hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] struct COPYDATASTRUCT { public nint dwData; public int cbData; public nint lpData; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClass(string name, nint hInstance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint CreateWindowEx(int exStyle, string cls, string title, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint h);
    [DllImport("user32.dll")] static extern nint DefWindowProc(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll")] static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    static extern nint SendMessageTimeout(nint h, uint msg, nint w, nint l, int flags, int timeoutMs, out nint result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);

    static WndProcDelegate? s_proc;   // must stay alive for the lifetime of the window

    [DllImport("user32.dll")] static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    static System.Windows.Interop.HwndSource? s_fg;

    /// <summary>Makes this process the foreground process for a moment (what Explorer's taskbar is when it forwards a click).</summary>
    static void ClaimForeground()
    {
        if (s_fg == null)
        {
            s_fg = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("RestoreWin10Focus")
            {
                Width = 1, Height = 1, PositionX = -32000, PositionY = -32000,
                WindowStyle = unchecked((int)0x80000000) | 0x10000000 /* WS_POPUP | WS_VISIBLE */, ExtendedWindowStyle = Win.WS_EX_TOOLWINDOW,
            });
        }
        Win.SetForegroundWindow(s_fg.Handle);
    }

    readonly State _state;
    readonly List<TrayIcon> _all = new();
    nint _hwnd, _native;
    uint _taskbarCreated;

    /// <summary>Icons the user pinned to the taskbar itself.</summary>
    public ObservableCollection<TrayIcon> Inline { get; } = new();
    /// <summary>Everything else - shown in the "^" flyout.</summary>
    public ObservableCollection<TrayIcon> Overflow { get; } = new();

    public TrayHost(State state) => _state = state;

    // What programs register, for support: %APPDATA%\RestoreWin10\tray.log (started afresh each run, capped). Program names
    // and protocol details only: the icons' tooltip text can be private and is never written (threat model F-11).
    static readonly string LogPath = Identity.DataFile("tray.log");
    static int _logLines;
    static void Log(string line)
    {
        if (_logLines++ > 400) return;
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}"); } catch { }
    }

    public nint Handle => _hwnd;

    public void Start()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); File.WriteAllText(LogPath, ""); } catch { }
        _native = Win.FindWindow("Shell_TrayWnd", null);          // Explorer's own, before ours exists
        _taskbarCreated = Win.RegisterWindowMessage("TaskbarCreated");
        s_proc = WndProc;
        var inst = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
            hInstance = inst, lpszClassName = "Shell_TrayWnd",
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(Win.WS_EX_TOOLWINDOW | WS_EX_TOPMOST | Win.WS_EX_NOACTIVATE, "Shell_TrayWnd", "", WS_POPUP, 0, 0, 0, 0, 0, 0, inst, 0);
        AnnounceTaskbar();
    }

    /// <summary>Tells every running program the taskbar was (re)created, so they register their icons again.</summary>
    static void AnnounceTaskbar() => Win.PostMessage(0xFFFF, Win.RegisterWindowMessage("TaskbarCreated"), 0, 0);

    /// <summary>Programs look the tray up by class name; make sure the first match is still ours.</summary>
    public void EnsureOnTop()
    {
        if (_hwnd == 0) return;
        if (Win.FindWindow("Shell_TrayWnd", null) != _hwnd)
            Win.SetWindowPos(_hwnd, -1, 0, 0, 0, 0, 0x2 | 0x1 | Win.SWP_NOACTIVATE);   // NOMOVE | NOSIZE
    }

    // ---------- incoming messages ----------

    nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_COPYDATA)
            {
                // (Windows only lets WM_COPYDATA be sent, never posted, and copies the data for us: lParam is valid.)
                var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
                long kind = (long)cds.dwData;
                if (kind == CopyTray) return HandleTray(cds.lpData, cds.cbData) ? 1 : 0;
                if (!Relays(kind)) { NoteOnce($"dropped WM_COPYDATA kind {kind}"); return 0; }
                // Other programs' app-bar requests (and some of Explorer's own) go on to Explorer. Theirs carry a
                // shared-memory handle made out to our process, which Explorer collects from us, so the answers travel
                // back correctly through this hop. RestoreWin10's own bars do not come this way (see AppBar.cs).
                return Forward(msg, wParam, lParam);
            }
            if (msg >= 0x400 || msg == WM_COMMAND)
            {
                NoteOnce(msg == WM_COMMAND ? $"relayed WM_COMMAND {(long)wParam & 0xFFFF}" : $"relayed message 0x{msg:x}");
                return Forward(msg, wParam, lParam);
            }
        }
        catch { }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    const long CopyAppBar = 0, CopyTray = 1, CopyIconRect = 3;   // WM_COPYDATA kinds (dwData) Explorer's tray answers

    /// <summary>WM_COPYDATA kinds passed on to Explorer: app-bar requests and Shell_NotifyIconGetRect.</summary>
    internal static bool Relays(long kind) => kind is CopyAppBar or CopyIconRect;

    /// <summary>A callback message an icon may ask for: the application range, not a system message.</summary>
    internal static bool IsAppMessage(uint m) => m is >= 0x400 and <= 0xFFFF;

    readonly HashSet<string> _noted = new();
    void NoteOnce(string what) { if (_noted.Add(what)) Log(what); }

    nint Forward(uint msg, nint wParam, nint lParam)
    {
        if (_native == 0 || !IsWindow(_native)) return 0;
        SendMessageTimeout(_native, msg, wParam, lParam, SMTO_ABORTIFHUNG, 2000, out var result);
        return result;
    }

    static int I32(nint p, int off) => Marshal.ReadInt32(p, off);

    internal bool HandleTray(nint data, int size)
    {
        // Layout (32-bit handles regardless of sender): [0] signature  [4] NIM_* command  [8..] NOTIFYICONDATA
        if (size < 8 + 24) return false;
        int cmd = I32(data, 4);
        nint nid = data + 8;
        int avail = size - 8;
        nint hwnd = (nint)I32(nid, 4);
        uint uid = (uint)I32(nid, 8), flags = (uint)I32(nid, 12);
        string key = $"{(long)hwnd:x}:{uid}";
        var icon = _all.Find(t => t.Key == key);

        // The icon's window must exist and must not be this host itself; an icon whose window now belongs to another
        // process (the handle was reused after its program ended) is the old program's leftover and goes.
        uint pid = 0;
        if (cmd != NIM_DELETE)
        {
            if (hwnd == 0 || hwnd == _hwnd || !IsWindow(hwnd)) { NoteOnce($"refused an icon for window 0x{(long)hwnd:x}: no such window"); return false; }
            Win.GetWindowThreadProcessId(hwnd, out pid);
            if (icon != null && icon.Pid != pid) { _all.Remove(icon); icon = null; Rebuild(); }
        }

        switch (cmd)
        {
            case NIM_ADD:
            case NIM_MODIFY:
                bool adoptedByModify = false;
                if (icon == null)
                {
                    if (cmd == NIM_MODIFY && (flags & NIF_ICON) == 0) return false;
                    adoptedByModify = cmd == NIM_MODIFY;
                    icon = new TrayIcon { Hwnd = hwnd, Pid = pid, Uid = uid, Key = key };
                    icon.Owner = Win.ExePath(pid) ?? "";
                    _all.Add(icon);
                }
                // Programs that registered with Explorer before we appeared often re-register with only a "modify",
                // flags unset - but their persistent struct still carries the callback message and the version.
                uint cb = (uint)I32(nid, 16);
                if ((flags & NIF_MESSAGE) != 0)
                {
                    icon.Callback = IsAppMessage(cb) ? cb : 0;   // a system message (WM_CLOSE...) is never posted on its behalf
                    if (!IsAppMessage(cb)) NoteOnce($"refused callback message 0x{cb:x} from {Path.GetFileName(icon.Owner)}");
                }
                else if (icon.Callback == 0 && IsAppMessage(cb)) icon.Callback = cb;
                if (icon.Version == 0 && (flags & 0x10 /* NIF_INFO: the field would be a timeout */) == 0 && avail >= 804 && I32(nid, 800) is 3 or 4)
                    icon.Version = I32(nid, 800);
                if ((flags & NIF_ICON) != 0)
                {
                    var h = (nint)I32(nid, 20);
                    if (h != 0)
                        try
                        {
                            var bs = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            bs.Freeze();
                            icon.Icon = bs;
                        }
                        catch { }
                }
                if ((flags & NIF_TIP) != 0 && avail >= 24 + 256) icon.Tooltip = Marshal.PtrToStringUni(nid + 24, 128)?.Split('\0')[0] ?? "";
                if ((flags & NIF_STATE) != 0 && avail >= 288)
                {
                    int state = I32(nid, 280), mask = I32(nid, 284);
                    if ((mask & 1) != 0) icon.Hidden = (state & 1) != 0;   // NIS_HIDDEN
                }
                Log($"{(cmd == NIM_ADD ? "ADD" : "MODIFY")} owner={Path.GetFileName(icon.Owner)} uid={uid} flags=0x{flags:x} ver={icon.Version} cb=0x{icon.Callback:x} u800={(avail >= 804 ? I32(nid, 800) : -1)}");
                Rebuild();
                // A "modify" for an icon we did not know, with no callback to report clicks to: the icon is shown, but
                // the answer is failure, as Explorer gives for an unknown icon. Programs re-registering after a
                // taskbar restart (Windows Security among them) try a modify first and send the full "add", with
                // their callback, only when it fails; answering success left their icons unclickable.
                if (adoptedByModify && icon.Callback == 0) { NoteOnce($"asked {Path.GetFileName(icon.Owner)} to add its icon in full (modify without a callback)"); return false; }
                return true;

            case NIM_DELETE:
                Log($"DELETE owner={(icon == null ? "?" : Path.GetFileName(icon.Owner))} uid={uid}");
                if (icon != null) { _all.Remove(icon); Rebuild(); }
                return true;

            case NIM_SETVERSION:
                if (icon == null && avail >= 804 && I32(nid, 20) != 0)
                {
                    // The program believes its icon exists (it did, in Explorer's tray); its struct has enough to show it.
                    icon = new TrayIcon { Hwnd = hwnd, Pid = pid, Uid = uid, Key = key };
                    icon.Owner = Win.ExePath(pid) ?? "";
                    uint cb2 = (uint)I32(nid, 16);
                    if (IsAppMessage(cb2)) icon.Callback = cb2;
                    try
                    {
                        var bs = Imaging.CreateBitmapSourceFromHIcon((nint)I32(nid, 20), Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        bs.Freeze();
                        icon.Icon = bs;
                    }
                    catch { }
                    icon.Tooltip = avail >= 280 ? Marshal.PtrToStringUni(nid + 24, 128)?.Split('\0')[0] ?? "" : "";
                    _all.Add(icon);
                    Rebuild();
                }
                if (icon != null && avail >= 804) icon.Version = I32(nid, 800);
                Log($"SETVERSION owner={(icon == null ? "?" : Path.GetFileName(icon.Owner))} uid={uid} size={size} ver={(icon == null ? -1 : icon.Version)}");
                return true;

            default:
                return true;   // NIM_SETFOCUS and anything newer: acknowledge
        }
    }

    // ---------- presentation ----------

    /// <summary>The icon registered for a window and id (for the self-test).</summary>
    internal TrayIcon? Find(nint hwnd, uint uid) => _all.Find(t => t.Hwnd == hwnd && t.Uid == uid);

    public bool IsPinned(TrayIcon t) => _state.TrayPinned.Contains(t.Owner, StringComparer.OrdinalIgnoreCase);

    public void SetPinned(TrayIcon t, bool pin)
    {
        _state.TrayPinned.RemoveAll(p => p.Equals(t.Owner, StringComparison.OrdinalIgnoreCase));
        if (pin && t.Owner.Length > 0) _state.TrayPinned.Add(t.Owner);
        _state.Save();
        Rebuild();
    }

    void Rebuild()
    {
        static void Sync(ObservableCollection<TrayIcon> target, List<TrayIcon> wanted)
        {
            for (int i = target.Count - 1; i >= 0; i--) if (!wanted.Contains(target[i])) target.RemoveAt(i);
            for (int i = 0; i < wanted.Count; i++)
            {
                int at = target.IndexOf(wanted[i]);
                if (at < 0) target.Insert(Math.Min(i, target.Count), wanted[i]);
                else if (at != i) target.Move(at, i);
            }
        }
        // Explorer's own system icons (volume, network, safely-remove...) only work inside Explorer's tray; the bar
        // has its own sound and network buttons instead.
        var visible = _all.Where(t => !t.Hidden && t.Icon != null && !Path.GetFileName(t.Owner).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                          .OrderBy(t => Path.GetFileName(t.Owner), StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Uid).ToList();
        Sync(Inline, visible.Where(IsPinned).ToList());
        Sync(Overflow, visible.Where(t => !IsPinned(t)).ToList());
    }

    /// <summary>Drops icons whose owning window no longer exists (the program crashed without removing them).</summary>
    public void Prune()
    {
        if (_all.RemoveAll(t => !IsWindow(t.Hwnd)) > 0) Rebuild();
    }

    // ---------- clicks forwarded to the owning program ----------

    public enum Button { Left, Right, LeftDouble }

    /// <summary>The icon is Windows Security's own (its tray program in System32, by the process's image path).</summary>
    internal static bool IsWindowsSecurity(TrayIcon t) =>
        t.Owner.Equals(Path.Combine(Environment.SystemDirectory, "SecurityHealthSystray.exe"), StringComparison.OrdinalIgnoreCase);

    public void Click(TrayIcon t, Button button)
    {
        if (t.Callback == 0)
        {
            // Windows Security's icon without its callback still opens Windows Security, through the address Windows
            // registers for it (never on behalf of any other program).
            if (IsWindowsSecurity(t)) { Log($"CLICK {button} owner={Path.GetFileName(t.Owner)}: no callback, opened Windows Security"); Launcher.Settings(Launcher.WindowsSecurity); }
            return;
        }
        // Only to the program that registered the icon: its window may have closed and the handle gone to another program.
        Win.GetWindowThreadProcessId(t.Hwnd, out uint pid);
        if (!IsWindow(t.Hwnd) || pid != t.Pid) { _all.Remove(t); Rebuild(); return; }
        ClaimForeground();
        AllowSetForegroundWindow(pid);   // so the program may show its menu/window in front
        Log($"CLICK {button} owner={Path.GetFileName(t.Owner)} uid={t.Uid} ver={t.Version} cb=0x{t.Callback:x}");

        // Exactly what Explorer sends, by the protocol version the program chose (NIM_SETVERSION): version 0 gets the
        // mouse messages only; version 3 also NIN_SELECT / WM_CONTEXTMENU; version 4 all of them in the newer layout
        // (position in wParam). Anything extra, such as a newer-layout message to a version-0 program, can be misread
        // by it as another click (v1 guessed so for some icons: programs opened, menus appeared in odd places).
        bool v4 = t.Version >= 4, v3 = t.Version >= 3;
        Post(t, 0x200 /* WM_MOUSEMOVE */, v4);
        switch (button)
        {
            case Button.Left:
                Post(t, 0x201, v4); Post(t, 0x202, v4);
                if (v3) Post(t, 0x400 /* NIN_SELECT */, v4);
                break;
            case Button.LeftDouble:
                Post(t, 0x203, v4); Post(t, 0x202, v4);
                break;
            case Button.Right:
                Post(t, 0x204, v4); Post(t, 0x205, v4);
                if (v3) Post(t, 0x7B /* WM_CONTEXTMENU */, v4);
                break;
        }
    }

    /// <summary>Posts one notification: old layout (wParam = id, lParam = event) or version-4 layout (wParam = x,y; lParam = event | id &lt;&lt; 16).</summary>
    static void Post(TrayIcon t, uint evt, bool v4form)
    {
        GetCursorPos(out var p);
        nint wParam, lParam;
        if (v4form) { wParam = (nint)((p.x & 0xFFFF) | (p.y << 16)); lParam = (nint)(uint)((evt & 0xFFFF) | (t.Uid << 16)); }
        else { wParam = (nint)t.Uid; lParam = (nint)evt; }
        Win.PostMessage(t.Hwnd, t.Callback, wParam, lParam);
    }

    public void Dispose()
    {
        if (s_fg != null) { s_fg.Dispose(); s_fg = null; }
        if (_hwnd == 0) return;
        DestroyWindow(_hwnd); _hwnd = 0;
        UnregisterClass("Shell_TrayWnd", GetModuleHandle(null));
        AnnounceTaskbar();   // programs fall back to Explorer's tray
    }
}
