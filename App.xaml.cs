using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace RestoreWin10;

public partial class App : Application
{
    SafeWaitHandle? _single;
    NamedObjects.Signal? _toggleSignal, _exitSignal;
    StartMenu? _menu;
    KeyHook? _hook;
    Taskbar? _taskbar;
    AppTrayIcon? _tray;

    // (Program.Main has already applied Hardening and handled installing, uninstalling and taskbar recovery.)

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // A fault in one handler must not take the whole shell down, and a fatal one must not leave the
        // Windows taskbar hidden. Both are written to %APPDATA%\RestoreWin10\crash.log.
        DispatcherUnhandledException += (_, ex) => { CrashLog.Write("UI thread", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { CrashLog.Write("fatal", ex.ExceptionObject as Exception); RunRecord.End(); _taskbar?.EmergencyRestore(); };
        SessionEnding += (_, _) => RunRecord.End();   // signing out or shutting down is a normal end

#if DEV_BUILD
        if (e.Args.Contains("--selftest")) { Shutdown(SelfTest.Run()); return; }   // checks only: nothing is started or shown
        if (e.Args.Contains("--leakprobe")) { Shutdown(LeakProbe.Run()); return; }   // the jump list's steps, counting leaked handles
        if (e.Args.FirstOrDefault(a => a.StartsWith("--closeprobe")) is { } cp)     // the taskbar's Close in every window state
        { Shutdown(CloseProbe.Run(cp.Contains('=') ? cp[(cp.IndexOf('=') + 1)..] : "")); return; }
        Battery.FakeFrom(e.Args);   // a desktop PC has no battery: the icon can be tried with --fake-battery=57[,ac|,saver]
        if (e.Args.Contains("--software")) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        // Crash test (Phase 8): a fatal error 3 seconds after start, so Windows records the crash the sign-in task listens for.
        if (e.Args.Contains("--crash"))
            new Thread(() => { Thread.Sleep(3000); throw new InvalidOperationException("development crash test"); }) { IsBackground = true }.Start();
#endif
        if (e.Args.Contains("--exit"))   // ask the running copy to shut down cleanly (restores the native taskbar)
        {
            NamedObjects.SetExisting(Identity.Name + ".Exit");
            Shutdown();
            return;
        }
        _single = NamedObjects.Lock(Identity.Name + ".Single", out bool first, out bool trusted);
        if (!trusted)
        {
            CrashLog.Note("RestoreWin10 did not start: its single-instance lock exists but was not made by this account's RestoreWin10.");
            Shutdown(1);
            return;
        }
        if (!first)
        {
            // Already running. A plain double-click of the exe opens the Start menu; a launch with mode
            // switches (a startup task relaunching us, say) must simply do nothing.
            if (e.Args.Length == 0) NamedObjects.SetExisting(Identity.Name + ".Toggle");
            Shutdown();
            return;
        }
        _toggleSignal = NamedObjects.CreateSignal(Identity.Name + ".Toggle");
        _exitSignal = NamedObjects.CreateSignal(Identity.Name + ".Exit");
        if (_toggleSignal == null || _exitSignal == null)
        {
            CrashLog.Note("RestoreWin10 did not start: its Toggle/Exit signals exist but were not made by this account's RestoreWin10.");
            Shutdown(1);
            return;
        }

        // The sign-in task starts RestoreWin10 again after a crash. Three crashes within ten minutes: give the Windows taskbar
        // back and stay off until the next sign-in (or a start by hand), rather than crash over and over.
        if (e.Args.Contains("--replace") && !Identity.Dev && CrashLog.RecentFatal(TimeSpan.FromMinutes(10)) >= 3)
        {
            CrashLog.Note("RestoreWin10 crashed three times within ten minutes; the Windows taskbar is back and it was not started again.");
            TaskbarRecovery.RecoverAfterCrash();
            Shutdown(1);
            return;
        }
        // Says in crash.log when the last run was ended without a trace (from outside), and keeps this run's record.
        RunRecord.Begin();

        // The installed v2 removes what v1 left in this account's profile (its program file and Apps entry).
        Setup.RemoveV1Leftovers();

        // Windows' colours (light/dark, accent, transparency) before any window paints. Windows' own settings are only
        // read here; they change only when the user changes them (in Settings or from the taskbar's menu).
        Personalization.ApplyResources();
#if DEV_BUILD
        // Layout check: the volume flyout drawn into PNGs (closed, and with the device list open), never shown.
        if (e.Args.FirstOrDefault(a => a.StartsWith("--render-volume=")) is { } rv)
        {
            var dir = rv["--render-volume=".Length..];
            var vf = new VolumeFlyout();
            vf.Snapshot(System.IO.Path.Combine(dir, "volume_closed.png"), listOpen: false);
            vf.Snapshot(System.IO.Path.Combine(dir, "volume_list.png"), listOpen: true);
            Shutdown();
            return;
        }
        // Layout check: the install window's pages drawn into PNGs, never shown; nothing is installed.
        if (e.Args.FirstOrDefault(a => a.StartsWith("--render-installer=")) is { } ri)
        {
            var dir = ri["--render-installer=".Length..];
            var v208 = new System.Version(2, 0, 8); var v209 = new System.Version(2, 0, 9);
            new InstallWindow(v209, null, true).Snapshot(System.IO.Path.Combine(dir, "install_new.png"));
            new InstallWindow(v209, v208, false).Snapshot(System.IO.Path.Combine(dir, "install_update.png"));
            new InstallWindow(v208, v209, true).Snapshot(System.IO.Path.Combine(dir, "install_newer.png"));
            new InstallWindow(v209, null, true).Snapshot(System.IO.Path.Combine(dir, "install_done.png"), done: true);
            new InstallWindow(v209, v208, true).Snapshot(System.IO.Path.Combine(dir, "install_done_update.png"), done: true, restarted: true);
            Shutdown();
            return;
        }
#endif

        _menu = new StartMenu();
        Personalization.Start(_menu);   // the (hidden) Start window hears Settings' broadcasts
        _hook = new KeyHook();
        // Raised on the hook's own thread, which must never wait for Start to open (see KeyHook): queue it and return.
        _hook.WinKeyTapped += () => Dispatcher.BeginInvoke(() =>
        {
            try { _menu.Toggle(); }
            catch (Exception ex) { CrashLog.Write("Windows key", ex); }
        });
        if (!Identity.Dev) _hook.Install();   // a development copy leaves the Windows key to the installed RestoreWin10

        var signals = new WaitHandle[] { _toggleSignal, _exitSignal };
        var t = new Thread(() =>
        {
            while (true)
            {
                if (WaitHandle.WaitAny(signals) == 1) { Dispatcher.Invoke(Shutdown); return; }
                SafeInvoke(_menu.Toggle);
            }
        }) { IsBackground = true };
        t.Start();

        var menu = new ContextMenu();
        static MenuItem Item(string text, Action a) { var mi = new MenuItem { Header = text }; mi.Click += (_, _) => a(); return mi; }
        menu.Items.Add(Item("Open Start menu", () => _menu.Toggle()));
        // (v1's "Start with Windows" toggle is gone: the sign-in task belongs to the installation, which only an
        // administrator can change. Installing turns it on; uninstalling removes it.)
        menu.Items.Add(Item("Import Windows taskbar pins", () => _taskbar?.ImportWindowsPins(interactive: true)));
        menu.Items.Add(new Separator());
        // Which RestoreWin10 and which .NET it runs on (the runtime is patched by Windows Update, decision D1).
        var version = typeof(App).Assembly.GetName().Version;
        menu.Items.Add(new MenuItem { Header = $"RestoreWin10 {version?.ToString(3)} on .NET {Environment.Version}", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Identity.Dev ? "Exit RestoreWin10 (development build)" : "Exit RestoreWin10", () => Shutdown()));
        _tray = new AppTrayIcon(Identity.Dev ? "RestoreWin10 development build (running)" : "RestoreWin10 (running)", menu, () => _menu.Toggle());

        var preload = _menu.PreloadAsync();
#if DEV_BUILD
        // Memory measurement (Phase 7): Start off-screen only, no taskbar; the probe ends the process.
        if (e.Args.Contains("--memprobe")) { MemProbe.Run(_menu, preload, e.Args.Contains("--software") ? "software" : "default"); return; }
#endif

        // --replace : dock at the bottom and hide the native taskbar while running.
        // --preview (or any other start of a copy that is not the installed one): dock at the top and leave the existing
        //             taskbar alone. (A downloaded copy opened without arguments shows the install window instead: Program.cs.)
        // --no-taskbar : Start menu only.
        // A development copy always runs in preview mode, next to the installed RestoreWin10.
        // The installed copy opened plainly (double-clicked in Program Files) starts as the sign-in task starts it.
        _replace = (e.Args.Contains("--replace") || (e.Args.Length == 0 && Setup.IsInstalledCopy)) && !Identity.Dev;
        if (!_replace) TaskbarRecovery.RecoverAfterCrash();   // a last run that ended while replacing the taskbar is put right
        StartMenu.BarAtTop = !_replace && !e.Args.Contains("--no-taskbar");
        if (!e.Args.Contains("--no-taskbar"))
        {
            _taskbar = new Taskbar(_menu, replace: _replace);
            _taskbar.Show();
            // Closed by an installer (an Office or Windows update replacing a file it holds): Windows starts it again
            // afterwards. Only the installed shell that replaces the taskbar registers (see AppRestart).
            if (_replace) AppRestart.Register();
            MemoryTrim.StartIdle(() => _menu.IsVisible || System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed);
            // The other screens get bars of their own (Windows' "show my taskbar on all displays" setting decides),
            // and they follow screens being plugged in, unplugged or rearranged, and Remote Desktop sessions.
            _screensSettle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _screensSettle.Tick += (_, _) => { _screensSettle.Stop(); SafeInvoke(SyncBars); };
            _taskbar.DisplaysChanged += () => { _screensSettle.Stop(); _screensSettle.Start(); };
            SyncBars();
        }
    }

    bool _replace;
    readonly List<Taskbar> _secondary = new();
    DispatcherTimer? _screensSettle;

    /// <summary>One bar per non-primary screen; bars whose screen went away (or became primary) are closed.</summary>
    void SyncBars()
    {
        if (_taskbar == null || _menu == null) return;
        var wanted = Screens.TaskbarOnAllDisplays
            ? Screens.All().Where(m => !m.Primary).Select(m => m.Device).ToList()
            : new List<string>();
        foreach (var bar in _secondary.ToList())
            if (!wanted.Contains(bar.Device, StringComparer.OrdinalIgnoreCase)) { _secondary.Remove(bar); bar.Close(); }
        foreach (var device in wanted)
            if (!_secondary.Any(b => b.Device.Equals(device, StringComparison.OrdinalIgnoreCase)))
            {
                var bar = new Taskbar(_menu, _replace, device);
                _secondary.Add(bar);
                bar.Show();
            }
        TaskTracker.MultipleBars = _secondary.Count > 0;
        _taskbar.Redock();
        foreach (var bar in _secondary) bar.Redock();
    }

    /// <summary>Runs an action on the UI thread for a worker thread; an exception there is logged, not fatal.</summary>
    void SafeInvoke(Action a)
    {
        try { Dispatcher.Invoke(a); }
        catch (Exception ex) { CrashLog.Write("worker", ex); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _screensSettle?.Stop();
        foreach (var bar in _secondary) bar.Close();   // each releases its screen edge
        _taskbar?.Close();   // releases the screen edge and restores the native taskbar if it was hidden
        _tray?.Dispose();
        _hook?.Dispose();   // restores the normal Windows key behaviour
        _single?.Dispose();
        RunRecord.End();
        base.OnExit(e);
    }
}

/// <summary>
/// One line about the running copy in %APPDATA%\RestoreWin10\running.txt: its process, when it started and when it was
/// last seen running (refreshed every minute). Exiting normally, Windows signing out or shutting down, and a crash
/// (already in crash.log) remove it. Still there at the next start, it means the last run ended some other way, most
/// likely from outside (Task Manager's End task, taskkill /F), which leaves no other trace: crash.log then says when.
/// </summary>
static class RunRecord
{
    const string FileName = "running.txt", Format = "yyyy-MM-dd'T'HH:mm:ss";
    static readonly object Gate = new();
    static Timer? _beat;
    static string _started = "";
    static bool _ended;
    static int _beats;

    public static void Begin()
    {
        try
        {
            // Written by this account's own programs, so read as untrusted text: small, and only our exact format counts.
            var file = Identity.DataFile(FileName);
            if (System.IO.File.Exists(file) && new System.IO.FileInfo(file).Length <= 512 &&
                Describe(System.IO.File.ReadAllText(file), WindowsStarted) is { } note)
                CrashLog.Note(note);
        }
        catch { }
        _started = DateTime.Now.ToString(Format, System.Globalization.CultureInfo.InvariantCulture);
        Beat();
        _beat = new Timer(_ => Beat(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public static void End()
    {
        lock (Gate)
        {
            if (_ended) return;
            _ended = true;
            _beat?.Dispose();
            try { System.IO.File.Delete(Identity.DataFile(FileName)); } catch { }
        }
    }

    static void Beat()
    {
        lock (Gate)
        {
            if (_ended) return;
            try
            {
                System.IO.Directory.CreateDirectory(Identity.DataDir);
                var file = Identity.DataFile(FileName);
                var now = DateTime.Now.ToString(Format, System.Globalization.CultureInfo.InvariantCulture);
                System.IO.File.WriteAllText(file + ".tmp", $"pid={Environment.ProcessId} started={_started} seen={now}");
                System.IO.File.Move(file + ".tmp", file, overwrite: true);   // never half-written
            }
            catch { }
            // From the first refresh on (a minute in): by then the windows and their icons have loaded.
            if (_beats++ > 0) ModuleWatch.Check(firstMinute: _beats == 2);
        }
    }

    static DateTime WindowsStarted => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public enum Revival { No, Yes, Capped }

    /// <summary>
    /// For the revive task (<c>--revive</c>, every 2 minutes): true when the last run was ended from outside since Windows
    /// started. Its record is then still there but has not been refreshed for over 90 seconds (a running copy refreshes it
    /// every minute). Exit from the tray, an installer closing it, signing out and a logged crash all remove the record, so
    /// none of those is revived (a crash is restarted by the sign-in task itself). After three ends from outside within
    /// 30 minutes it stays off and says so, rather than being ended and started over and over.
    /// </summary>
    public static bool ReviveWanted()
    {
        try
        {
            var file = Identity.DataFile(FileName);
            if (!System.IO.File.Exists(file) || new System.IO.FileInfo(file).Length > 512) return false;
            var record = System.IO.File.ReadAllText(file);
            var log = Identity.DataFile("crash.log");
            int recent = System.IO.File.Exists(log) ? CrashLog.CountForcedEnds(System.IO.File.ReadLines(log), DateTime.Now, CapWindow) : 0;
            var verdict = Decide(record, WindowsStarted, DateTime.Now, recent);
            if (verdict == Revival.Capped)
            {
                if (Describe(record, WindowsStarted) is { } note) CrashLog.Note(note);
                CrashLog.Note($"RestoreWin10 was ended from outside {ReviveCap} times within {CapWindow.TotalMinutes:0} minutes, so it was not " +
                              "started again. Start it from Win+R or sign in again.");
                System.IO.File.Delete(file);   // said once; the next checks find nothing to do
            }
            return verdict == Revival.Yes;
        }
        catch { return false; }
    }

    const int ReviveCap = 3;
    static readonly TimeSpan Stale = TimeSpan.FromSeconds(90), CapWindow = TimeSpan.FromMinutes(30);

    internal static Revival Decide(string record, DateTime windowsStarted, DateTime now, int recentForcedEnds)
    {
        if (!TryParse(record, out _, out _, out var seen) || seen < windowsStarted || now - seen < Stale) return Revival.No;
        return recentForcedEnds >= ReviveCap ? Revival.Capped : Revival.Yes;
    }

    static bool TryParse(string record, out uint pid, out DateTime started, out DateTime seen)
    {
        pid = 0; started = seen = default;
        var parts = record.Trim().Split(' ');
        return parts.Length == 3 && parts[0].StartsWith("pid=") && uint.TryParse(parts[0][4..], out pid) &&
               parts[1].StartsWith("started=") && Parse(parts[1][8..], out started) &&
               parts[2].StartsWith("seen=") && Parse(parts[2][5..], out seen);

        static bool Parse(string s, out DateTime at) =>
            DateTime.TryParseExact(s, Format, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out at);
    }

    /// <summary>The crash.log note for a record the last run left behind, or null when it is not in our format.</summary>
    internal static string? Describe(string record, DateTime windowsStarted)
    {
        if (!TryParse(record, out var pid, out var started, out var seen)) return null;
        var why = seen < windowsStarted
            ? "Windows has started again since then, so the PC was most likely restarted, shut down or lost power while it ran."
            : "Windows has not restarted since then, so it was ended from outside (Task Manager's End task, taskkill /F or another program).";
        return $"the last run (process {pid}, started {started:yyyy-MM-dd HH:mm:ss}) did not exit normally. It was last seen running at " +
               $"{seen:yyyy-MM-dd HH:mm:ss} and ended within about a minute after that. {why}";
    }
}

/// <summary>
/// Unexpected exceptions and refused actions go to %APPDATA%\RestoreWin10\crash.log: at most 50 entries per session, so a
/// storm cannot fill the disk, and once the file passes 256 KB it becomes crash.old.log (replacing the previous one).
/// </summary>
static class CrashLog
{
    const long MaxBytes = 256 * 1024;
    static int _count;
    static bool _checkedSize;

    /// <summary>False in the elevated setup steps: elevated code never writes into the user's profile.</summary>
    public static bool Enabled { get; set; } = true;

    public static void Write(string where, Exception? ex)
    {
        if (ex != null) Append($"[{where}] {ex}");
    }

    /// <summary>Something worth knowing that is not an exception (a launch refused, a lock not trusted).</summary>
    public static void Note(string message) => Append($"[note] {message}");

    /// <summary>Fatal crashes logged within <paramref name="window"/> before now.</summary>
    public static int RecentFatal(TimeSpan window)
    {
        try
        {
            var file = Identity.DataFile("crash.log");
            return System.IO.File.Exists(file) ? CountFatal(System.IO.File.ReadLines(file), DateTime.Now, window) : 0;
        }
        catch { return 0; }
    }

    /// <summary>Lines of crash.log that start a fatal entry ("yyyy-MM-dd HH:mm:ss [fatal]") within the window.</summary>
    internal static int CountFatal(IEnumerable<string> lines, DateTime now, TimeSpan window) =>
        Count(lines, rest => rest.StartsWith(" [fatal]"), now, window);

    /// <summary>Notes within the window saying a run was ended from outside (RunRecord).</summary>
    internal static int CountForcedEnds(IEnumerable<string> lines, DateTime now, TimeSpan window) =>
        Count(lines, rest => rest.StartsWith(" [note] the last run") && rest.Contains("from outside"), now, window);

    static int Count(IEnumerable<string> lines, Func<string, bool> kind, DateTime now, TimeSpan window) =>
        lines.Count(l => l.Length > 27 && kind(l[19..]) &&
                         DateTime.TryParseExact(l[..19], "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                                                System.Globalization.DateTimeStyles.None, out var at) &&
                         at <= now && now - at <= window);

    static void Append(string text)
    {
        if (!Enabled || Interlocked.Increment(ref _count) > 50) return;
        try
        {
            System.IO.Directory.CreateDirectory(Identity.DataDir);
            var file = Identity.DataFile("crash.log");
            if (!_checkedSize)
            {
                _checkedSize = true;
                var info = new System.IO.FileInfo(file);
                if (info.Exists && info.Length > MaxBytes) System.IO.File.Move(file, Identity.DataFile("crash.old.log"), overwrite: true);
            }
            System.IO.File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}
