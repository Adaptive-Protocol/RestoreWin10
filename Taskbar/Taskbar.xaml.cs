using System.Diagnostics;
using System.Net.NetworkInformation;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RestoreWin10;

/// <summary>
/// Windows 10 style taskbar, run as a docked "app bar" so maximised windows stop at its edge.
/// Preview mode docks it at the TOP and leaves the existing taskbar alone;
/// replace mode (--replace) docks at the bottom and hides the native taskbar while running.
/// </summary>
public partial class Taskbar : Window
{
    const int BarHeightDip = 40;
#if DEV_BUILD
    /// <summary>Memory probe only: bars are created but never docked, and sit off-screen.</summary>
    internal static bool Offscreen;
#endif
    /// <summary>A program button's width (Windows 10's, measured from a screenshot); the drag and scroll arithmetic uses it.</summary>
    const double Pitch = 40;

    readonly StartMenu _menu;
    readonly TaskTracker _tracker;
    readonly bool _replace;
    readonly uint _callbackMsg;
    readonly DispatcherTimer _refresh, _clock;
    ThumbnailPopup? _preview;

    /// <summary>The hover-preview window, created the first time a button is hovered.</summary>
    ThumbnailPopup Preview
    {
        get
        {
            if (_preview != null) return _preview;
            var pv = new ThumbnailPopup();
            pv.PointerEntered += () => _leaveTimer.Stop();
            pv.PointerLeft += () => _leaveTimer.Start();
            pv.WindowChosen += h => { pv.Dismiss(); DismissStart(); Win.ActivateOther(h); };
            pv.CloseRequested += h => { Win.CloseWindow(h); pv.LeaveOut(h); ShowPreview(); };   // its card goes at once
            return _preview = pv;
        }
    }
    SearchFlyout? _search;
    VolumeFlyout? _volume;
    DateTime _netChecked = DateTime.MinValue;
    bool _netChecking;
    bool _slotOpen;
    string _flyoutText = "";
    TrayHost? _tray;
    TrayFlyout? _trayFlyout;
    readonly DispatcherTimer _hoverTimer, _leaveTimer;
    // button drag state
    TaskItem? _pressed;
    FrameworkElement? _pressedEl;
    Point _grab, _downPos;
    bool _dragging;
    uint _shellHookMsg;
    int _nativeCheck;
    FrameworkElement? _ghost;
    DateTime _lastSwap;
    double _lastListX;                 // where the cursor last was, in the button strip's coordinates
    DispatcherTimer? _swapRetry;       // re-checks the slot after a cooldown swallowed a move

    TaskItem? _hoverItem;
    FrameworkElement? _hoverEl;
    nint _hwnd;
    bool _registered;
    int _savedState;

    // ----- one bar per screen -----
    readonly bool _main;     // the main bar (primary screen): search, Task View, notification area, sound and network
    string _device;          // the screen this bar is on (\\.\DISPLAYn); a secondary bar keeps its screen for life
    Win.RECT _dockedRect;    // the edge last claimed, so a neighbour's position change doesn't trigger a needless re-claim

    /// <summary>The screen this bar belongs to.</summary>
    public string Device => _device;
    /// <summary>The screens may have changed (raised by the main bar; the app then adds or removes secondary bars).</summary>
    public event Action? DisplaysChanged;
    /// <summary>The main bar, for things only it has (the search box).</summary>
    static Taskbar? s_main;

    /// <summary>
    /// <paramref name="device"/> null: the main taskbar, on the primary screen. Otherwise a secondary taskbar on that
    /// screen, as Windows 10 has: Start button, the program buttons Windows' multiple-display setting gives it, the clock.
    /// </summary>
    public Taskbar(StartMenu menu, bool replace, string? device = null)
    {
        InitializeComponent();
        _menu = menu; _replace = replace;
        _main = device == null;
        _device = device ?? Screens.Primary.Device;
        if (_main) s_main = this;
        _tracker = new TaskTracker(State.Shared, _main);
        TaskList.ItemsSource = _tracker.Items;
        if (_main)
        {
            SeedPins();
            if (!State.Shared.WindowsPinsImported) ImportWindowsPins(interactive: false);   // carry the Windows taskbar's pins over, once
            RekeyPackagedPins();
        }
        ApplyBarSettings();
        Personalization.OptionsChanged += ApplyBarSettings;   // Settings > Personalization > Taskbar, or our own menu
        _menu.OpenChanged += UpdateStyle;                     // the normal look while Start is open on this screen
        StyleChosen += UpdateStyle;

        _callbackMsg = Win.RegisterWindowMessage("RestoreWin10AppBar");
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;

        _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _refresh.Tick += (_, _) =>
        {
            if (_replace && _main) HideNativeBars();
            if (++_ticks % 10 == 0) CheckReservation();   // every ~3 s
            _tray?.EnsureOnTop();
            _tray?.Prune();
            _tracker.Refresh();
            SyncPreview();
            UpdateStyle();   // Task View opening or closing
        };

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); ShowPreview(); };
        _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _leaveTimer.Tick += (_, _) => { _leaveTimer.Stop(); _hoverItem = null; _preview?.Dismiss(); };
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
        UpdateClock();
        _refresh.Start(); _clock.Start();
        _tracker.Refresh();
    }

    // ---------- docking ----------

    void OnSourceInitialized(object? s, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // Tool window (no taskbar button of its own) that never takes focus when clicked.
        Win.SetWindowLong(_hwnd, Win.GWL_EXSTYLE, Win.GetWindowLong(_hwnd, Win.GWL_EXSTYLE) | Win.WS_EX_TOOLWINDOW | Win.WS_EX_NOACTIVATE);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        // Shell-hook notifications: this is how a taskbar learns that a window wants attention.
        _shellHookMsg = Win.RegisterWindowMessage("SHELLHOOK");
        Win.RegisterShellHookWindow(_hwnd);
        Personalization.Follow(this, Personalization.Kind.Taskbar, Shown);   // Windows' colours, live, in the bar's style

#if DEV_BUILD
        if (Offscreen)   // memory probe: never docked, nothing reserved, out of sight
        {
            var scr = Screens.ByDevice(_device) ?? Screens.Primary;
            Win.SetWindowPos(_hwnd, 0, -32000, -32000, scr.Bounds.Right - scr.Bounds.Left, (int)Math.Round(BarHeightDip * scr.Scale), 0x4 | 0x10);
            return;
        }
#endif
        if (_replace && _main) HideNativeBars(save: true);   // Explorer's bars on every screen

        if (_replace && _main)
        {
            // Become the notification-area host: programs find the tray by its window class and send us their icons.
            _tray = new TrayHost(State.Shared);
            _tray.Start();
            InlineTray.ItemsSource = _tray.Inline;
            ChevronBtn.Visibility = Visibility.Visible;
        }

        var abd = NewAppBarData();
        AppBar.Send(Win.ABM_NEW, ref abd);
        _registered = true;
        Dock(force: true);
    }

    Win.APPBARDATA NewAppBarData() => new()
    {
        cbSize = Marshal.SizeOf<Win.APPBARDATA>(), hWnd = _hwnd, uCallbackMessage = _callbackMsg,
        uEdge = _replace ? Win.ABE_BOTTOM : Win.ABE_TOP,
    };

    /// <summary>
    /// Claims the bottom (preview mode: top) edge of this bar's screen, so other windows keep clear, and sizes the bar
    /// to it. Everything is in physical pixels of that monitor, at that monitor's own scaling.
    /// <paramref name="force"/> false (another app bar moved): the edge is only re-claimed if our spot actually changed,
    /// so two bars answering each other's notifications cannot ping-pong.
    /// </summary>
    void Dock(bool force)
    {
        if (!_registered) return;
        var scr = _main ? Screens.Primary : Screens.ByDevice(_device);
        if (scr == null) return;   // the screen is gone; the app closes this bar
        _device = scr.Device;
        _tracker.Monitor = scr.Handle;
        int h = (int)Math.Round(BarHeightDip * scr.Scale);
        var m = scr.Bounds;
        bool top = !_replace;

        var abd = NewAppBarData();
        abd.rc = new Win.RECT { Left = m.Left, Right = m.Right, Top = top ? m.Top : m.Bottom - h, Bottom = top ? m.Top + h : m.Bottom };
        var asked = abd.rc;
        AppBar.Send(Win.ABM_QUERYPOS, ref abd);
        var queried = abd.rc;
        if (top) abd.rc.Bottom = abd.rc.Top + h; else abd.rc.Top = abd.rc.Bottom - h;
        if (force || !abd.rc.Equals(_dockedRect))
        {
            AppBar.Send(Win.ABM_SETPOS, ref abd);
            _dockedRect = abd.rc;
            _dockedAt = Environment.TickCount64;
            s_docked[_device] = abd.rc;
            DockLog($"{_device} claimed {R(abd.rc)} (asked {R(asked)}, offered {R(queried)})");
        }
        Win.SetWindowPos(_hwnd, -1 /* TOPMOST */, abd.rc.Left, abd.rc.Top, abd.rc.Right - abd.rc.Left, h, Win.SWP_NOACTIVATE | Win.SWP_SHOWWINDOW);
    }

    static string R(Win.RECT r) => $"{r.Left},{r.Top}-{r.Right},{r.Bottom}";

    /// <summary>%APPDATA%\RestoreWin10\dock.log: where each bar claimed its edge and what Windows made of it (kept small).</summary>
    static void DockLog(string line)
    {
        try
        {
            var path = Identity.DataFile("dock.log");
            if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { }
    }

    int _ticks;
    long _dockedAt;
    readonly Queue<long> _workFixes = new();   // when the work area last had to be set directly (to back off if Explorer fights it)

    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref Win.RECT rect, uint winIni);

    /// <summary>
    /// Every few seconds: is this bar's strip still kept clear of maximised windows? It should be (see AppBar.cs for
    /// why it once was not); this is a safety net. First the bar registers afresh, which makes Explorer recompute the
    /// screen's work area; if that does not hold, the work area is set directly (only the bar's own edge moves, so
    /// space other app bars reserve is kept). If Explorer keeps undoing it, the bar stops trying for a few minutes
    /// rather than make maximised windows jump back and forth.
    /// </summary>
    void CheckReservation()
    {
        if (!_registered || _dockedRect.Right <= _dockedRect.Left) return;
        if (Environment.TickCount64 - _dockedAt < 1500) return;   // Explorer applies a new position a moment later
        var scr = Screens.ByDevice(_device);
        if (scr == null) return;
        var work = scr.Work;
        bool ok = _replace ? work.Bottom <= _dockedRect.Top : work.Top >= _dockedRect.Bottom;
        if (ok) return;
        long now = Environment.TickCount64;
        while (_workFixes.Count > 0 && now - _workFixes.Peek() > 5 * 60_000) _workFixes.Dequeue();
        if (_workFixes.Count >= 3) return;
        _workFixes.Enqueue(now);
        if (_workFixes.Count == 1)
        {
            DockLog($"{_device} work area {R(scr.Work)} ignores the bar at {R(_dockedRect)}: registering again");
            var abd = NewAppBarData();
            AppBar.Send(Win.ABM_REMOVE, ref abd);
            AppBar.Send(Win.ABM_NEW, ref abd);
            Dock(force: true);
        }
        else
        {
            if (_replace) work.Bottom = _dockedRect.Top; else work.Top = _dockedRect.Bottom;
            bool done = SystemParametersInfo(0x002F /* SPI_SETWORKAREA */, 0, ref work, 0x2 /* SPIF_SENDCHANGE */);
            DockLog($"{_device} work area {R(scr.Work)} still ignores the bar: set to {R(work)} ({(done ? "ok" : "failed")})");
        }
    }

    nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == _callbackMsg && (int)wParam == Win.ABN_POSCHANGED) Dock(force: false);
        else if (msg == 0x007E /* WM_DISPLAYCHANGE */ || msg == 0x02E0 /* WM_DPICHANGED */)
        {
            Dispatcher.BeginInvoke(() => Dock(force: true));
            if (msg == 0x007E && _main) DisplaysChanged?.Invoke();
        }
        // Settings > Taskbar > "Show my taskbar on all displays" and friends announce themselves as "TraySettings". (Work-area
        // changes, which every bar's docking causes, also arrive as WM_SETTINGCHANGE: reacting to those would loop.)
        else if (msg == 0x001A /* WM_SETTINGCHANGE */ && _main && lParam != 0 && Marshal.PtrToStringUni(lParam) == "TraySettings")
            DisplaysChanged?.Invoke();
        else if (msg == 0x0021 /* WM_MOUSEACTIVATE */) { handled = true; return 3 /* MA_NOACTIVATE */; }
        else if (_shellHookMsg != 0 && msg == (int)_shellHookMsg && (int)wParam == Win.HSHELL_FLASH) _tracker.Flash(lParam);
        else if (msg == 0x0016 /* WM_ENDSESSION */ && _main && AppRestart.IsInstallerClose(wParam, lParam))
        {
            // A backup only. When an installer closes RestoreWin10 through Restart Manager, WPF itself normally shuts
            // the application down in order (OnExit gives the Windows taskbar back); in the Restart Manager test of
            // 19 Oct that path closed it and this handler was not reached, so nothing was written to crash.log. If the
            // message does reach this bar first, the Windows taskbar and the tray icons are given back here, before the
            // process can be ended, and the application closes normally. Windows starts RestoreWin10 again when the
            // installer is done (AppRestart). A sign-out takes WPF's usual path.
            CrashLog.Note("an installer asked RestoreWin10 to close; the Windows taskbar was given back, and Windows will start RestoreWin10 again afterwards");
            EmergencyRestore();
            Application.Current.Shutdown();
        }
        return 0;
    }

    void OnClosed(object? s, EventArgs e)
    {
        _refresh.Stop(); _clock.Stop(); _hoverTimer.Stop(); _leaveTimer.Stop();
        _preview?.Dismiss(); _preview?.Close();
        if (_registered)
        {
            var abd = NewAppBarData();
            AppBar.Send(Win.ABM_REMOVE, ref abd);
        }
        if (_shellHookMsg != 0) Win.DeregisterShellHookWindow(_hwnd);
        Personalization.OptionsChanged -= ApplyBarSettings;
        _menu.OpenChanged -= UpdateStyle;
        StyleChosen -= UpdateStyle;
        if (s_main == this) s_main = null;
        if (_replace && _main) RestoreNativeBars();
        _trayFlyout?.Close(); _search?.Close(); _volume?.Close();
        _tray?.Dispose();   // after the native bar is back, so programs re-register with Explorer
    }

    // ---------- style (TranslucentTB's ideas, on RestoreWin10's own window) ----------

    /// <summary>Every bar: the style was changed from a bar's menu.</summary>
    static event Action? StyleChosen;

    static Personalization.BarStyle ChosenStyle => State.Shared.TaskbarStyle switch
    {
        "normal" => Personalization.BarStyle.Normal,
        "acrylic" => Personalization.BarStyle.Acrylic,
        "opaque" => Personalization.BarStyle.Opaque,
        _ => Personalization.BarStyle.Clear,   // the default: the wallpaper shows through, as TranslucentTB's default does on Windows 10
    };

    Personalization.BarStyle? _shown;

    /// <summary>
    /// The chosen style, except Windows' normal look while Start, Search or Task View is open on this bar's screen
    /// (TranslucentTB's Windows 10 default), so the bar matches what is open above it.
    /// </summary>
    Personalization.BarStyle Wanted() =>
        string.Equals(_menu.OpenOn, _device, StringComparison.OrdinalIgnoreCase) || (_search?.IsVisible == true) || TaskViewOpen()
            ? Personalization.BarStyle.Normal : ChosenStyle;

    Personalization.BarStyle Shown() => (_shown = Wanted()).Value;

    void UpdateStyle()
    {
        if (_hwnd != 0 && _shown != Wanted()) Personalization.Surface(this, Personalization.Kind.Taskbar, Shown());
    }

    /// <summary>
    /// Windows' Task View is open on this bar's screen: Explorer's full-screen XAML host window is in front. (Alt+Tab uses
    /// the same window class; were it full screen, the bar would only show its normal look a moment longer.)
    /// </summary>
    bool TaskViewOpen()
    {
        var fg = Win.GetForegroundWindow();
        if (fg == 0 || Win.ClassName(fg) != "XamlExplorerHostIslandWindow" || !Win.GetWindowRect(fg, out var r)) return false;
        var b = Screens.ByDevice(_device)?.Bounds;
        return b is { } m && r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    void ChooseStyle(string style)
    {
        State.Shared.TaskbarStyle = style;
        State.Shared.Save();
        StyleChosen?.Invoke();
    }

    // ---------- native taskbar (replace mode only) ----------

    // Explorer's own taskbar windows. Captured once, BEFORE the tray host creates a window of the same class name
    // (after that, looking the class up would find ours first).
    readonly List<nint> _nativeBars = new();

    void HideNativeBars(bool save = false)
    {
        if (save)
        {
            var abd = NewAppBarData();
            // Explorer's own setting is kept in state.json until it is put back (threat model F-12). If it is already there,
            // the last run ended abruptly and what Explorer reports now is RestoreWin10's change, not the user's setting.
            var st = State.Shared;
            if (st.ExplorerTaskbarState is int before) _savedState = before;
            else
            {
                _savedState = (int)AppBar.Send(Win.ABM_GETSTATE, ref abd);
                st.ExplorerTaskbarState = _savedState;
                st.Save();
            }
            abd.lParam = _savedState | Win.ABS_AUTOHIDE;
            AppBar.Send(Win.ABM_SETSTATE, ref abd);

            _nativeBars.Clear();
            Win.EnumWindows((h, _) =>   // the main bar and one secondary bar per extra screen
            {
                if (Win.ClassName(h) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") _nativeBars.Add(h);
                return true;
            }, 0);
        }
        if (!save && ++_nativeCheck % 10 == 0) AdoptNewNativeBars();   // every ~3 s: has Explorer been restarted?
        // Explorer's windows: asked without waiting (every 300 ms), so a busy or frozen Explorer cannot freeze this bar.
        foreach (var h in _nativeBars) if (Win.IsWindowVisible(h)) Win.ShowWindowAsync(h, Win.SW_HIDE);
    }

    /// <summary>
    /// Explorer may crash and be restarted (or be restarted by the user) while we are replacing its taskbar.
    /// Its new taskbar windows must be hidden like the old ones, and it will have forgotten the auto-hide state.
    /// </summary>
    void AdoptNewNativeBars()
    {
        _nativeBars.RemoveAll(h => !Win.IsWindow(h));
        bool found = false;
        Win.EnumWindows((h, _) =>
        {
            if (h == _hwnd || (_tray != null && h == _tray.Handle) || _nativeBars.Contains(h)) return true;
            var cls = Win.ClassName(h);
            if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") { _nativeBars.Add(h); found = true; }
            return true;
        }, 0);
        if (!found) return;
        var abd = NewAppBarData();
        abd.lParam = _savedState | Win.ABS_AUTOHIDE;
        AppBar.Send(Win.ABM_SETSTATE, ref abd);
        _tray?.EnsureOnTop();
    }

    /// <summary>Re-claims the screen edge (the app calls this after the screens change).</summary>
    public void Redock() => Dock(force: true);

    // Where each bar is docked, by screen, so the Start menu can sit against the bar itself.
    static readonly Dictionary<string, Win.RECT> s_docked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="screen"/>'s work area, never overlapping a RestoreWin10 bar on it, whatever Windows reports (its
    /// work area can lag behind a bar that has just docked).
    /// </summary>
    internal static Win.RECT UsableArea(Screens.Info screen)
    {
        var a = screen.Work;
        if (s_docked.TryGetValue(screen.Device, out var bar) && bar.Right > bar.Left)
        {
            if (bar.Top <= screen.Bounds.Top) a.Top = Math.Max(a.Top, bar.Bottom);   // bar along the top
            else a.Bottom = Math.Min(a.Bottom, bar.Top);                            // bar along the bottom
        }
        return a;
    }

    /// <summary>The process is dying unexpectedly: give the Windows taskbar back, from whatever thread this is.</summary>
    public void EmergencyRestore()
    {
        if (!_replace || !_main) return;
        try { RestoreNativeBars(); } catch { }
        Win.PostMessage(0xFFFF, Win.RegisterWindowMessage("TaskbarCreated"), 0, 0);   // tray icons go back to Explorer
    }

    void RestoreNativeBars()
    {
        var abd = NewAppBarData();
        abd.lParam = _savedState;
        AppBar.Send(Win.ABM_SETSTATE, ref abd);
        foreach (var h in _nativeBars) Win.ShowWindowAsync(h, Win.SW_SHOW);
        State.Shared.ExplorerTaskbarState = null;   // put back: nothing left to recover
        State.Shared.Save();
    }

    // ---------- pins & clock ----------

    static void SeedPins()
    {
        var st = State.Shared;
        if (st.TaskbarSeeded) return;
        // First run: start from whatever the Windows taskbar has; failing that, File Explorer and Edge.
        var pins = WindowsPins.Read();
        if (pins.Count > 0)
        {
            foreach (var p in pins) st.PinToTaskbar(p.Exe, p.Shortcut);
            st.WindowsPinsImported = true;
        }
        else
            foreach (var p in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            })
                if (File.Exists(p)) st.TaskbarPins.Add(p);
        st.TaskbarSeeded = true;
        st.Save();
    }

    /// <summary>
    /// Pins of a packaged app's exe (earlier versions pinned a running Store app by its exe, whose folder is named after
    /// the version and goes away at the app's next update) become pins of its app id. Running apps are handled by the
    /// tracker as soon as they are seen; this covers the ones that are not running, looked up in the Apps folder in the
    /// background (that enumeration can take seconds). A package with several apps is left alone rather than guessed.
    /// </summary>
    void RekeyPackagedPins()
    {
        var old = State.Shared.TaskbarPins.Select(p => (Exe: p, Family: TaskTracker.PackageFamilyOf(p)))
                                          .Where(x => x.Family != null).ToList();
        if (old.Count == 0) return;
        Task.Run(() =>
        {
            var ids = AppEntry.ScanStoreApps().Select(a => a.Path).ToList();
            Dispatcher.BeginInvoke(() =>
            {
                bool changed = false;
                foreach (var (exe, family) in old)
                {
                    var matches = ids.Where(k => k.StartsWith(Launcher.StorePrefix + family + "!", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 1) changed |= TaskTracker.RekeyPin(State.Shared, exe, matches[0]);
                }
                if (changed) { State.Shared.Save(); _tracker.Refresh(); }
            });
        });
    }

    /// <summary>Replaces the pins with what is pinned to the Windows taskbar, in the same left-to-right order.</summary>
    public int ImportWindowsPins(bool interactive)
    {
        var st = State.Shared;
        var pins = WindowsPins.Read();
        if (pins.Count > 0)
        {
            st.TaskbarPins.Clear();
            st.TaskbarPinLaunch.Clear();
            foreach (var p in pins) st.PinToTaskbar(p.Exe, p.Shortcut);
            _tracker.ResetOrder();   // the imported order leads; running programs follow
        }
        st.WindowsPinsImported = true;
        st.Save();
        _tracker.Refresh();
        if (interactive)
            MessageBox.Show(pins.Count > 0
                    ? $"Imported {pins.Count} pinned programs from the Windows taskbar, in their order.\n\nPins of Store apps cannot be carried over."
                    : "Nothing is pinned to the Windows taskbar.",
                "RestoreWin10", MessageBoxButton.OK, MessageBoxImage.Information);
        return pins.Count;
    }

    // ---------- overflow of the button strip ----------

    void OnTaskScrollChanged(object s, ScrollChangedEventArgs e)
    {
        bool overflow = TaskScroll.ScrollableWidth > 0;
        ScrollLeft.Visibility = overflow && TaskScroll.HorizontalOffset > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScrollRight.Visibility = overflow && TaskScroll.HorizontalOffset < TaskScroll.ScrollableWidth - 0.5 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnScrollLeft(object s, RoutedEventArgs e) => TaskScroll.ScrollToHorizontalOffset(TaskScroll.HorizontalOffset - Pitch * 4);
    void OnScrollRight(object s, RoutedEventArgs e) => TaskScroll.ScrollToHorizontalOffset(TaskScroll.HorizontalOffset + Pitch * 4);

    void OnTaskWheel(object s, MouseWheelEventArgs e)
    {
        if (TaskScroll.ScrollableWidth <= 0) return;
        TaskScroll.ScrollToHorizontalOffset(TaskScroll.HorizontalOffset - Math.Sign(e.Delta) * Pitch * 2);
        e.Handled = true;
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        TimeText.Text = now.ToString("t");
        DateText.Text = now.ToString("d");
        UpdateSystemGlyphs();
    }

    /// <summary>The sound glyph follows the volume (as in Windows 10); the network glyph the connection type.</summary>
    void UpdateSystemGlyphs()
    {
        SoundGlyph.Text = Audio.Glyph(Audio.Get());
        if (_main) UpdateBattery();
        if (_netChecking || (DateTime.Now - _netChecked).TotalSeconds < 5) return;
        _netChecking = true;
        Task.Run(NetworkGlyph).ContinueWith(t =>
        {
            _netChecked = DateTime.Now; _netChecking = false;
            if (t.IsCompletedSuccessfully) NetGlyph.Text = t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The battery icon: shown, with charge and charging state, only while Windows reports a system battery.</summary>
    void UpdateBattery()
    {
        var b = Battery.Read();
        BatteryBtn.Visibility = b == null ? Visibility.Collapsed : Visibility.Visible;
        if (b == null) return;
        if (BatteryGlyph.Text != b.Glyph) BatteryGlyph.Text = b.Glyph;
        if (BatteryBtn.ToolTip as string != b.Tooltip) BatteryBtn.ToolTip = b.Tooltip;
    }

    static string NetworkGlyph()
    {
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)).ToList();
            if (up.Any(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx)) return "\uE839";
            if (up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)) return "\uE701";
            return up.Count > 0 ? "\uE839" : "\uF384";
        }
        catch { return "\uE839"; }
    }

    // ---------- buttons ----------

    void DismissStart() { if (_menu.IsVisible) _menu.Dismiss(); }

    /// <summary>Opens Start on this bar's screen, right above this bar.</summary>
    void OnStart(object s, RoutedEventArgs e) => _menu.Toggle(Screens.FromWindow(_hwnd), top: !_replace);

    /// <summary>
    /// The flyouts cannot see clicks on this window (it never takes focus), so tell them. A press anywhere on the bar
    /// also closes Start, as in Windows 10 - except on a Start button, which toggles it (or brings it to its screen).
    /// </summary>
    void OnRootDown(object s, MouseButtonEventArgs e)
    {
        _search?.Dismiss(); _trayFlyout?.Dismiss(); _volume?.Dismiss();
        if (!IsInside(e.OriginalSource as DependencyObject, StartBtn)) DismissStart();
    }

    static bool IsInside(DependencyObject? d, DependencyObject target)
    {
        while (d != null)
        {
            if (d == target) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>What the main bar shows, from Windows' own taskbar settings (Settings > Personalization > Taskbar, which
    /// the taskbar menu also writes). Other screens' bars show what Windows 10's do: no search, Task View, notification
    /// area, network or sound.</summary>
    void ApplyBarSettings()
    {
        if (!_main)
        {
            SearchSlot.Visibility = TaskViewBtn.Visibility = NetBtn.Visibility = SoundBtn.Visibility = BatteryBtn.Visibility = Visibility.Collapsed;
            return;
        }
        int searchMode = Personalization.SearchMode;
        TaskViewBtn.Visibility = Personalization.ShowTaskView ? Visibility.Visible : Visibility.Collapsed;
        SearchSlot.Visibility = searchMode == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (searchMode == 2) SetSlot(true);                                                   // always a box
        else if (_flyoutText.Trim().Length == 0 && _search?.IsVisible != true) SetSlot(false);   // the glyph
        UpdateSlotText();
    }

    // ---------- search slot ----------

    const double SlotCollapsed = 48, SlotExpanded = 300;

    void OnSlotEnter(object s, MouseEventArgs e) { if (!_slotOpen) SearchSlot.SetResourceReference(BackgroundProperty, "Hover"); }
    void OnSlotLeave(object s, MouseEventArgs e) { if (!_slotOpen) SearchSlot.Background = Brushes.Transparent; }

    void OnSearchSlot(object s, MouseButtonEventArgs e)
    {
        if (_slotOpen) OpenSearch();
        else SetSlot(true, OpenSearch);   // let the slot finish widening, then put the caret in it
    }

    /// <summary>Widens the slot into the search box or shrinks it back to the glyph.</summary>
    void SetSlot(bool open, Action? then = null)
    {
        if (_slotOpen == open) { then?.Invoke(); return; }
        _slotOpen = open;
        var anim = new DoubleAnimation(open ? SlotExpanded : SlotCollapsed, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (then != null) anim.Completed += (_, _) => then();
        SearchSlot.BeginAnimation(WidthProperty, anim);
        SearchSlot.Background = open ? new SolidColorBrush(Color.FromArgb(0xE6, 0xF2, 0xF2, 0xF2)) : Brushes.Transparent;
        SearchIcon.Visibility = open ? Visibility.Collapsed : Visibility.Visible;   // Windows 10's bold icon on the bar
        SearchGlyph.Visibility = open ? Visibility.Visible : Visibility.Collapsed;  // the thin dark one in the light box
        SearchText.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetIsEnabled(SearchSlot, !open);
    }

    void UpdateSlotText()
    {
        bool empty = _flyoutText.Length == 0;
        SearchText.Text = empty ? "Type here to search" : _flyoutText;
        SearchText.Foreground = new SolidColorBrush(empty ? Color.FromRgb(0x44, 0x44, 0x44) : Colors.Black);
    }

    void OpenSearch()
    {
        _search ??= CreateSearch();
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var tl = SearchSlot.PointToScreen(new Point());
        _search.Open(new Rect(tl.X, tl.Y, SlotExpanded * scale, SearchSlot.ActualHeight * scale), above: _replace, _flyoutText);
    }

    SearchFlyout CreateSearch()
    {
        var f = new SearchFlyout(_menu);
        f.IsVisibleChanged += (_, _) => UpdateStyle();
        f.QueryChanged += q => { _flyoutText = q; UpdateSlotText(); };
        f.Dismissed += () =>
        {
            // Nothing typed: back to the glyph (in search-box mode the box stays). Text left in it: the slot stays open and shows it.
            if (_flyoutText.Trim().Length == 0)
            {
                _flyoutText = ""; UpdateSlotText();
                if (Personalization.SearchMode != 2) SetSlot(false);
            }
        };
        return f;
    }
    void OnTaskView(object s, RoutedEventArgs e) { DismissStart(); Win.WinCombo(0x09 /* Tab */); }
    void OnNotifications(object s, RoutedEventArgs e) { DismissStart(); Win.WinCombo(0x4E /* N */); }
    void OnClock(object s, RoutedEventArgs e) { DismissStart(); Win.WinCombo(0x4E /* N */); }
    void OnShowDesktop(object s, MouseButtonEventArgs e) => ToggleDesktop();

    // "Show the desktop" was used and the desktop is still in front: the menus then offer "Show open windows".
    static bool s_desktopShown;
    static bool DesktopShowing => s_desktopShown && Win.ClassName(Win.GetForegroundWindow()) is "Progman" or "WorkerW";

    /// <summary>Win+D: shows the desktop, or brings the windows back if that is what it did last.</summary>
    void ToggleDesktop()
    {
        DismissStart();
        s_desktopShown = !DesktopShowing;
        Win.WinCombo(0x44 /* D */);
    }
    void OnChevron(object s, RoutedEventArgs e)
    {
        if (_tray == null) return;
        _trayFlyout ??= new TrayFlyout(_tray);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var tl = ChevronBtn.PointToScreen(new Point());
        _trayFlyout.Toggle(new Rect(tl.X, tl.Y, ChevronBtn.ActualWidth * scale, ChevronBtn.ActualHeight * scale), above: true);
    }

    // ----- icons pinned to the taskbar -----
    static TrayIcon TrayOf(object s) => (TrayIcon)((FrameworkElement)s).DataContext;
    static bool Ctrl => (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    void OnTrayDown(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !Ctrl) _tray?.Click(TrayOf(s), TrayHost.Button.LeftDouble);
    }

    void OnTrayUp(object s, MouseButtonEventArgs e)
    {
        if (_tray == null || e.ClickCount >= 2) return;
        if (Ctrl) _tray.SetPinned(TrayOf(s), false);            // Ctrl+click: send it back to the "^" flyout
        else _tray.Click(TrayOf(s), TrayHost.Button.Left);
    }

    void OnTrayRight(object s, MouseButtonEventArgs e)
    {
        e.Handled = true;   // the program shows its own menu; the taskbar's menu must not follow
        if (_tray == null) return;
        if (Ctrl) _tray.SetPinned(TrayOf(s), false);
        else _tray.Click(TrayOf(s), TrayHost.Button.Right);
    }

    void OnNetwork(object s, RoutedEventArgs e) => Settings("ms-availablenetworks:");
    void OnBattery(object s, RoutedEventArgs e) => Settings("ms-settings:batterysaver");

    void OnBatteryMenu(object s, MouseButtonEventArgs e)
    {
        var m = NewMenu(BatteryBtn);
        m.Items.Add(Entry("Power Options", () => Windows(Launcher.Sys("control.exe"), false, "powercfg.cpl")));
        m.Items.Add(Entry("Open Battery settings", () => Settings("ms-settings:batterysaver")));
        Show(m);
        e.Handled = true;
    }

    void OnVolume(object s, RoutedEventArgs e)
    {
        DismissStart();
        _volume ??= new VolumeFlyout();
        _volume.Changed -= UpdateSystemGlyphs; _volume.Changed += UpdateSystemGlyphs;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var tl = SoundBtn.PointToScreen(new Point());
        _volume.Toggle(new Rect(tl.X, tl.Y, SoundBtn.ActualWidth * scale, SoundBtn.ActualHeight * scale), above: _replace);
    }

    /// <summary>Opens a menu and keeps it honest: a press anywhere outside it (this bar included) closes it.</summary>
    void Show(ContextMenu m)
    {
        PopupGuard.Watch(m, PopupGuard.MenuOf(_hwnd, m), () => m.IsOpen = false);
        m.Closed += (_, _) => { PopupGuard.Unwatch(m); MemoryTrim.Schedule(); };   // a jump list's icons and menu go
        // The taskbar never takes the keyboard (it would take it from the program in front), so its menus did not get
        // it either and Esc went to that program. As in Windows 10, an open menu takes the keyboard (Esc, arrows, Enter);
        // the user has just clicked the taskbar, so Windows allows it, and gives it back to the program on closing.
        m.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(m) is HwndSource src) Win.Activate(src.Handle);
            m.Focus();
        };
        m.IsOpen = true;
    }

    void OnSoundMenu(object s, MouseButtonEventArgs e)
    {
        var m = NewMenu(SoundBtn);
        m.Items.Add(Entry("Open Volume mixer", () => Windows(Launcher.Sys("SndVol.exe"))));
        m.Items.Add(Entry("Playback devices", () => Windows(Launcher.Sys("control.exe"), false, "mmsys.cpl,,0")));
        m.Items.Add(Entry("Recording devices", () => Windows(Launcher.Sys("control.exe"), false, "mmsys.cpl,,1")));
        m.Items.Add(Entry("Sounds", () => Windows(Launcher.Sys("control.exe"), false, "mmsys.cpl,,2")));
        m.Items.Add(new Separator());
        m.Items.Add(Entry("Troubleshoot sound problems", () => Settings("ms-settings:troubleshoot")));
        m.Items.Add(Entry("Open Sound settings", () => Settings("ms-settings:sound")));
        Show(m);
        e.Handled = true;
    }

    void OnNetworkMenu(object s, MouseButtonEventArgs e)
    {
        var m = NewMenu(NetBtn);
        m.Items.Add(Entry("Troubleshoot problems", () => Settings("ms-settings:troubleshoot")));
        m.Items.Add(Entry("Open Network & Internet settings", () => Settings("ms-settings:network")));
        Show(m);
        e.Handled = true;
    }

    // ---------- the taskbar's own menus ----------

    /// <summary>Right-click on an empty part of the bar (or Search, Task View, the notifications button...).</summary>
    void OnBarRight(object s, MouseButtonEventArgs e)
    {
        if (e.Handled || _dragging) return;
        DismissStart();
        ShowAtCursor(BarMenu(clock: false));
        e.Handled = true;
    }

    void OnClockRight(object s, MouseButtonEventArgs e)
    {
        DismissStart();
        ShowAtCursor(BarMenu(clock: true));
        e.Handled = true;
    }

    void ShowAtCursor(ContextMenu m)
    {
        m.PlacementTarget = Root;   // owned by this bar, so a press outside it (and only outside it) closes it
        m.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        Show(m);
    }

    /// <summary>Windows 10's taskbar menu. The clock's variant has the date/time entries where Search and Task View are.</summary>
    ContextMenu BarMenu(bool clock)
    {
        var m = new ContextMenu();
        if (clock)
        {
            m.Items.Add(Entry("Adjust date/time", () => Settings("ms-settings:dateandtime")));
            m.Items.Add(Entry("Customize notification icons", () => Settings("ms-settings:taskbar")));
        }
        else
        {
            var search = new MenuItem { Header = "Search" };
            foreach (var (label, mode) in new[] { ("Hidden", 0), ("Show search icon", 1), ("Show search box", 2) })
            {
                var mi = Entry(label, () => Personalization.SetSearchMode(mode));   // what Settings writes, so both agree
                mi.IsChecked = Personalization.SearchMode == mode;
                search.Items.Add(mi);
            }
            m.Items.Add(search);
            bool taskView = Personalization.ShowTaskView;
            var tv = Entry("Show Task View button", () => Personalization.SetShowTaskView(!taskView));
            tv.IsChecked = taskView;
            m.Items.Add(tv);
        }
        m.Items.Add(new Separator());
        m.Items.Add(Entry("Cascade windows", () => WindowArrange.Arrange(WindowArrange.Kind.Cascade)));
        m.Items.Add(Entry("Show windows stacked", () => WindowArrange.Arrange(WindowArrange.Kind.Stacked)));
        m.Items.Add(Entry("Show windows side by side", () => WindowArrange.Arrange(WindowArrange.Kind.SideBySide)));
        if (WindowArrange.UndoLabel is { } undo) m.Items.Add(Entry(undo, WindowArrange.Undo));
        m.Items.Add(Entry(DesktopShowing ? "Show open windows" : "Show the desktop", ToggleDesktop));
        m.Items.Add(new Separator());
        m.Items.Add(Entry("Task Manager", () => Windows(Launcher.Sys("Taskmgr.exe"))));
        m.Items.Add(new Separator());
        // RestoreWin10's bars are always docked, so this is shown the way a locked taskbar is, and cannot be unticked.
        var lockItem = new MenuItem { Header = TaskTracker.MultipleBars ? "Lock all taskbars" : "Lock the taskbar", IsChecked = true, IsEnabled = false,
                                      ToolTip = "RestoreWin10's taskbars always stay docked at the bottom of their screens." };
        ToolTipService.SetShowOnDisabled(lockItem, true);
        m.Items.Add(lockItem);
        // The taskbar's style (the normal look returns by itself while Start, Search or Task View is open).
        var look = new MenuItem { Header = "Taskbar appearance" };
        foreach (var (label, style) in new[] { ("Clear", "clear"), ("Acrylic", "acrylic"), ("Opaque", "opaque"), ("Normal (follow Windows)", "normal") })
        {
            var mi = Entry(label, () => ChooseStyle(style));
            mi.IsChecked = (State.Shared.TaskbarStyle ?? "clear") == style;
            look.Items.Add(mi);
        }
        m.Items.Add(look);
        m.Items.Add(new Separator());
        m.Items.Add(Entry("Taskbar settings", () => Settings("ms-settings:taskbar"), glyph: ""));
        return m;
    }

    /// <summary>Right-click on Start: Windows 10's quick-link menu (the one Win+X opens).</summary>
    void OnStartRight(object s, MouseButtonEventArgs e)
    {
        DismissStart();
        var m = NewMenu(StartBtn);
        void Add(string text, Action a) => m.Items.Add(Entry(text, a));
        Add("Apps and _Features", () => Settings("ms-settings:appsfeatures"));
        Add("Power _Options", () => Settings("ms-settings:powersleep"));
        Add("Event _Viewer", () => Console("eventvwr.msc"));
        Add("S_ystem", () => Settings("ms-settings:about"));
        Add("Device _Manager", () => Console("devmgmt.msc"));
        Add("Net_work Connections", () => Settings("ms-settings:network-status"));
        Add("Dis_k Management", () => Console("diskmgmt.msc"));
        Add("Computer Mana_gement", () => Console("compmgmt.msc"));
        Add("W_indows PowerShell", () => Windows(Launcher.PowerShell));
        Add("Windows PowerShell (_Admin)", () => Windows(Launcher.PowerShell, elevated: true));
        m.Items.Add(new Separator());
        Add("_Task Manager", () => Windows(Launcher.Sys("Taskmgr.exe")));
        Add("Setti_ngs", () => Settings(Launcher.SettingsHome));   // always the main page
        Add("File _Explorer", () => Windows(Launcher.Explorer));
        Add("_Search", OpenSearchAnywhere);
        Add("_Run", () => { DismissStart(); Launcher.RunDialog(); });
        m.Items.Add(new Separator());
        var power = new MenuItem { Header = "Sh_ut down or sign out" };
        // The same choices as Start's power button (Sleep, Hibernate, Shut down, Restart, "Update and ..." when due).
        power.Items.Add(Entry("Sign _out", () => { DismissStart(); Session.SignOut(); }));
        foreach (var choice in Session.PowerChoices())
            power.Items.Add(Entry(choice.AccessText, () => { DismissStart(); choice.Act(); }));
        m.Items.Add(power);
        Add("_Desktop", ToggleDesktop);
        Show(m);
        e.Handled = true;
    }

    /// <summary>"Search" from the quick-link menu: the main bar's search box, or Start's own when the bar has none.</summary>
    void OpenSearchAnywhere()
    {
        if (s_main is { } bar && bar.SearchSlot.Visibility == Visibility.Visible)
        {
            if (bar._slotOpen) bar.OpenSearch(); else bar.SetSlot(true, bar.OpenSearch);
        }
        else _menu.Toggle(Screens.FromWindow(_hwnd), top: !_replace);
    }

    // Every launch goes through Launcher (full paths, argument lists, checked targets); these close Start first.
    void Settings(string page) { DismissStart(); Launcher.Settings(page); }
    void Windows(string fullPath, bool elevated = false, params string[] args) { DismissStart(); Launcher.Windows(fullPath, elevated, args); }
    void Console(string msc) { DismissStart(); Launcher.Console(msc); }

    // ---------- program buttons ----------

    static TaskItem ItemOf(object sender) => (TaskItem)((FrameworkElement)sender).DataContext;

    // ---------- drag to reorder ----------

    void OnTaskDown(object s, MouseButtonEventArgs e)
    {
        _pressedEl = (FrameworkElement)s;
        _pressed = ItemOf(s);
        _downPos = e.GetPosition(Root);
        _grab = e.GetPosition(_pressedEl);
        Mouse.Capture(Root, CaptureMode.SubTree);   // keep getting moves if the cursor strays off the button
    }

    void OnRootMove(object s, MouseEventArgs e)
    {
        if (_pressed == null) return;
        var pt = e.GetPosition(Root);
        if (!_dragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { ReleaseDrag(); return; }
            if (Math.Abs(pt.X - _downPos.X) < SystemParameters.MinimumHorizontalDragDistance) return;
            BeginDrag();
        }

        Canvas.SetLeft(_ghost, pt.X - _grab.X);
        Canvas.SetTop(_ghost, 0);
        _lastListX = e.GetPosition(TaskList).X;
        ReconsiderSlot();
    }

    /// <summary>
    /// Swaps only once the ghost's centre is well into another slot (dead band), and gives the slide animation
    /// time to play before the next swap, so a slow drag never flickers back and forth. A check that falls in
    /// the cooldown is repeated when it ends, because the mouse may be resting by then.
    /// </summary>
    void ReconsiderSlot()
    {
        if (!_dragging || _pressed == null) return;
        int cur = _tracker.Items.IndexOf(_pressed);
        if (cur < 0) return;
        if ((DateTime.Now - _lastSwap).TotalMilliseconds < 120)
        {
            _swapRetry ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
            _swapRetry.Tick -= OnSwapRetry; _swapRetry.Tick += OnSwapRetry;
            _swapRetry.Stop(); _swapRetry.Start();
            return;
        }
        double centre = _lastListX - _grab.X + Pitch / 2;
        double offset = centre - (cur * Pitch + Pitch / 2);
        if (Math.Abs(offset) < Pitch * 0.6) return;
        int slot = Math.Clamp(cur + (int)Math.Round(offset / Pitch), 0, _tracker.Items.Count - 1);
        if (slot == cur) return;
        _tracker.Items.Move(cur, slot);
        _tracker.CommitOrder(save: false);   // the file is written once, when the button is dropped
        _lastSwap = DateTime.Now;
    }

    void OnSwapRetry(object? s, EventArgs e) { _swapRetry!.Stop(); ReconsiderSlot(); }

    void OnRootUp(object s, MouseButtonEventArgs e)
    {
        if (_pressed == null) return;
        if (!_dragging) { ReleaseDrag(); return; }

        // Glide the ghost into the slot, then reveal the real button.
        var item = _pressed; var ghost = _ghost!;
        _tracker.CommitOrder(save: true);
        int idx = _tracker.Items.IndexOf(item);
        double destX = TaskList.TranslatePoint(new Point(), Root).X + Math.Max(idx, 0) * Pitch;
        ReleaseDrag();
        var anim = new DoubleAnimation(Canvas.GetLeft(ghost), destX, TimeSpan.FromMilliseconds(130)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        anim.Completed += (_, _) => { DragLayer.Children.Remove(ghost); item.IsDragging = false; };
        ghost.BeginAnimation(Canvas.LeftProperty, anim);
    }

    void BeginDrag()
    {
        _dragging = true;
        _refresh.Stop();                           // no window polling while the slide animations run
        _hoverTimer.Stop(); _preview?.Dismiss();
        TaskList.IsHitTestVisible = false;         // no hover highlights flashing under the ghost
        var el = _pressedEl!;
        var item = _pressed!;

        // Only the icon travels with the cursor, exactly as Windows 10 does; the button's own slot becomes a gap.
        var ghost = new Grid { Width = el.ActualWidth, Height = el.ActualHeight, IsHitTestVisible = false };
        ghost.Children.Add(new Image { Source = item.Icon, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        _ghost = ghost;
        DragLayer.Children.Add(ghost);
        item.IsDragging = true;
        _lastSwap = DateTime.MinValue;
    }

    /// <summary>Ends the press/drag state (the ghost, if any, is cleaned up by the caller).</summary>
    void ReleaseDrag()
    {
        // State first, then the mouse: letting go of capture re-sends a move, which must find no press or drag.
        _swapRetry?.Stop();
        _pressed = null; _pressedEl = null; _dragging = false; _ghost = null;
        if (Mouse.Captured == Root) Mouse.Capture(null);
        TaskList.IsHitTestVisible = true;
        if (!_refresh.IsEnabled) _refresh.Start();
    }

    // ---------- hover previews ----------

    void OnTaskEnter(object s, MouseEventArgs e)
    {
        if (_dragging) return;
        _leaveTimer.Stop();
        _hoverItem = ItemOf(s); _hoverEl = (FrameworkElement)s;
        if (!_hoverItem.IsRunning) { _hoverTimer.Stop(); _preview?.Dismiss(); return; }
        if (_preview?.IsVisible == true) ShowPreview();          // already open: hop to the new button at once
        else { _hoverTimer.Stop(); _hoverTimer.Start(); }
    }

    void OnTaskLeave(object s, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        _leaveTimer.Start();
    }

    void ShowPreview()
    {
        if (_hoverItem is not { IsRunning: true } item || _hoverEl == null) return;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var tl = _hoverEl.PointToScreen(new Point());
        Preview.Present(item, new Rect(tl.X, tl.Y, _hoverEl.ActualWidth * scale, _hoverEl.ActualHeight * scale), above: _replace);
    }

    /// <summary>Keeps an open preview in step with windows opening and closing underneath it.</summary>
    void SyncPreview()
    {
        if (_preview?.IsVisible != true || _hoverItem == null) return;
        if (!_hoverItem.IsRunning) { _preview?.Dismiss(); return; }
        if (!_preview!.Showing(_hoverItem).SequenceEqual(_preview.Current)) ShowPreview();
    }

    void OnTaskClick(object s, MouseButtonEventArgs e)
    {
        if (_dragging) return;   // that was the end of a drag, not a click
        var item = ItemOf(s);
        _hoverTimer.Stop(); _preview?.Dismiss();
        DismissStart();
        if (!item.IsRunning) { LaunchNew(item); return; }
        if (item.Windows.Count == 1)
        {
            var h = item.Windows[0];
            if (item.IsActive) Win.MinimizeOther(h); else Win.ActivateOther(h);   // never waits on the program
        }
        else { _hoverItem = item; _hoverEl = (FrameworkElement)s; ShowPreview(); }
    }

    void OnTaskMiddle(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        LaunchNew(ItemOf(s));
        e.Handled = true;
    }

    static void LaunchNew(TaskItem item)
    {
        if (item.IsStore) { Launcher.Item(item.Key); return; }   // a Store app starts through its app id, never its exe
        if (item.ExePath == null) return;
        // A pin made from a shortcut launches through it, so arguments and advertised targets are honoured.
        var target = State.Shared.TaskbarPinLaunch.TryGetValue(item.ExePath, out var lnk) && File.Exists(lnk) ? lnk : item.ExePath;
        Launcher.Item(target);
    }

    ContextMenu NewMenu(FrameworkElement anchor) => new()
    {
        PlacementTarget = anchor,
        Placement = _replace ? System.Windows.Controls.Primitives.PlacementMode.Top : System.Windows.Controls.Primitives.PlacementMode.Bottom,
    };

    /// <summary>A file or program name shown as-is (in menu text "_" marks a keyboard accelerator: "my_file" would lose it).</summary>
    static string Literal(string s) => s.Replace("_", "__");

    static MenuItem Entry(string text, Action a, bool bold = false, ImageSource? icon = null, string? tip = null, string? glyph = null)
    {
        var mi = new MenuItem { Header = text, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, ToolTip = tip };
        if (icon != null) mi.Icon = new Image { Source = icon, Width = 16, Height = 16 };
        else if (glyph != null)
        {
            var g = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14 };
            g.SetResourceReference(TextBlock.ForegroundProperty, "MenuFg");
            mi.Icon = g;
        }
        mi.Click += (_, _) => a();
        return mi;
    }

    void OnTaskRight(object s, MouseButtonEventArgs e)
    {
        var item = ItemOf(s);
        _hoverTimer.Stop(); _preview?.Dismiss();
        var m = NewMenu((FrameworkElement)s);

        // The Windows 10 jump list: the program's pinned items, its recent ones (frequent folders for File Explorer),
        // then the program.
        var recent = JumpLists.Recent(AppIdsOf(item), 15);
        if (recent.Count > 0)
        {
            bool explorer = item.ExePath?.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase) == true;
            bool? section = null;
            foreach (var r in recent)
            {
                if (section != r.Pinned)
                {
                    if (section != null) m.Items.Add(new Separator());
                    m.Items.Add(new MenuItem { Header = r.Pinned ? "Pinned" : explorer ? "Frequent" : "Recent", IsEnabled = false });
                    section = r.Pinned;
                }
                var path = r.Path;
                var name = r.Name ?? System.IO.Path.GetFileName(path.TrimEnd('\\'));
                // A network item gets its type's icon: opening the menu must not contact another computer (F-09).
                var icon = Launcher.IsOnThisPc(path) ? IconHelper.AssociatedIcon(path) : IconHelper.TypeIcon(path);
                m.Items.Add(Entry(Literal(name.Length > 0 ? name : path), () => OpenRecent(item, path), icon: icon, tip: path));
            }
            m.Items.Add(new Separator());
        }

        // The program's own tasks ("Open new window"...), under their categories, as in Windows 10.
        var tasks = JumpTasks.For(AppIdsOf(item), ProgramOf(item));
        if (tasks.Count > 0)
        {
            string? category = null;
            foreach (var t in tasks)
            {
                if (t.Category != category)
                {
                    if (category != null || recent.Count > 0) m.Items.Add(new Separator());
                    m.Items.Add(new MenuItem { Header = Literal(t.Category), IsEnabled = false });
                    category = t.Category;
                }
                if (t.IsSeparator) { m.Items.Add(new Separator()); continue; }
                var icon = t.IconFile is { } f && Launcher.IsOnThisPc(f) ? IconHelper.FromFile(f, t.IconIndex) : IconHelper.FromFile(t.Exe);
                // The tooltip says exactly what runs, so a task's name can never hide what it starts.
                m.Items.Add(Entry(Literal(t.Title), () => { DismissStart(); Launcher.Task(t.Exe, t.Arguments); }, icon: icon,
                                  tip: (t.Exe + " " + t.Arguments).Trim()));
            }
            m.Items.Add(new Separator());
        }

        m.Items.Add(Entry(Literal(item.Name), () => LaunchNew(item), bold: true, icon: item.Icon));
        if (item.PinKey != null)
            m.Items.Add(item.IsPinned
                ? Entry("Unpin from taskbar", () => SetPinned(item, false))
                : Entry("Pin to taskbar", () => SetPinned(item, true)));
        if (item.IsRunning)
        {
            m.Items.Add(new Separator());
            m.Items.Add(Entry(item.HasMany ? "Close all windows" : "Close window", () =>
            {
                foreach (var h in item.Windows) Win.CloseWindow(h);
            }));
        }
        Show(m);
        e.Handled = true;
    }

    /// <summary>The AppIDs Windows may have filed this program's jump list under: its AppUserModelID (from a window
    /// or its pinned shortcut) if it has one, and the hash of its path.</summary>
    static IEnumerable<string> AppIdsOf(TaskItem item)
    {
        foreach (var h in item.Windows.Take(1))
            if (PropStore.WindowAumid(h) is { Length: > 0 } a) yield return JumpLists.AppId(a);
        if (item.IsStore) yield return JumpLists.AppId(item.Key[Launcher.StorePrefix.Length..]);   // also when not running
        if (item.ExePath == null) yield break;
        if (State.Shared.TaskbarPinLaunch.TryGetValue(item.ExePath, out var lnk) && PropStore.ShortcutAumid(lnk) is { Length: > 0 } b) yield return JumpLists.AppId(b);
        yield return JumpLists.AppId(item.ExePath);
    }

    /// <summary>Opens a recent item with its program (a folder: in File Explorer).</summary>
    static void OpenRecent(TaskItem item, string path)
    {
        if (item.IsStore) { Launcher.Item(item.Key, argument: path); return; }
        if (item.ExePath == null) return;
        var target = State.Shared.TaskbarPinLaunch.TryGetValue(item.ExePath, out var lnk) && File.Exists(lnk) ? lnk : item.ExePath;
        Launcher.Item(target, argument: path);   // one argument, quoted by the system; a path with a quote in it is refused
    }

    /// <summary>The program behind a button: its exe, or for a Store app pinned before it ran, the running window's exe.</summary>
    static string? ProgramOf(TaskItem item)
    {
        if (item.ExePath != null) return item.ExePath;
        if (item.Windows.FirstOrDefault() is var h && h != 0) { Win.GetWindowThreadProcessId(h, out uint pid); return Win.ExePath(pid); }
        return null;
    }

    void SetPinned(TaskItem item, bool pin)
    {
        if (item.PinKey is not { } key) return;
        if (pin) State.Shared.PinToTaskbar(key, null); else State.Shared.UnpinFromTaskbar(key);
        State.Shared.Save();
        _tracker.Refresh();
    }
}
