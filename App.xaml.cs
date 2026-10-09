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
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { CrashLog.Write("fatal", ex.ExceptionObject as Exception); _taskbar?.EmergencyRestore(); };

#if DEV_BUILD
        if (e.Args.Contains("--selftest")) { Shutdown(SelfTest.Run()); return; }   // checks only: nothing is started or shown
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

        // The installed v2 removes what v1 left in this account's profile (its program file and Apps entry).
        Setup.RemoveV1Leftovers();

        // Windows' colours (light/dark, accent, transparency) before any window paints. Windows' own settings are only
        // read here; they change only when the user changes them (in Settings or from the taskbar's menu).
        Personalization.ApplyResources();

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
        // default   : preview mode - dock at the top and leave the existing taskbar alone.
        // --no-taskbar : Start menu only.
        // A development copy always runs in preview mode, next to the installed RestoreWin10.
        _replace = e.Args.Contains("--replace") && !Identity.Dev;
        if (!_replace) TaskbarRecovery.RecoverAfterCrash();   // a last run that ended while replacing the taskbar is put right
        StartMenu.BarAtTop = !_replace && !e.Args.Contains("--no-taskbar");
        if (!e.Args.Contains("--no-taskbar"))
        {
            _taskbar = new Taskbar(_menu, replace: _replace);
            _taskbar.Show();
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
        base.OnExit(e);
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
        lines.Count(l => l.Length > 27 && l.AsSpan(19).StartsWith(" [fatal]") &&
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
