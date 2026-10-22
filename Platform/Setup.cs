using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace RestoreWin10;

/// <summary>
/// Installing and uninstalling RestoreWin10 for the whole PC (decision K2), and giving the Windows taskbar back.
/// Runs from Program.Main. The elevated steps never load WPF and show only plain Windows message boxes; the install window
/// (a downloaded RestoreWin10.exe opened without arguments) is WPF, and runs only as the user.
/// <list type="bullet">
/// <item>No arguments, not the installed copy: the install window (install, update, reinstall; an older version is refused),
/// with one remembered choice, start at sign-in.</item>
/// <item><c>--install</c>: as the user; asks once for administrator rights (UAC), then the elevated step below installs
/// RestoreWin10 and starts it.</item>
/// <item><c>--uninstall</c>: as the user; confirms, asks for administrator rights, then offers to restart (decision D7).</item>
/// <item><c>--restore-taskbar</c>: as the user; closes RestoreWin10 and gives the Windows 11 taskbar back.</item>
/// <item><c>--quiet</c>: no message boxes.</item>
/// </list>
/// The elevated steps (<c>--install-elevated</c>, <c>--uninstall-elevated</c>) keep to the threat model (F-02, F-03):
/// <list type="bullet">
/// <item>They write only to %ProgramFiles%\RestoreWin10, HKLM and Task Scheduler, never to the user's profile (not even
/// crash.log), and refuse a program folder that is a junction or link.</item>
/// <item>They use no temporary files and no command interpreter. The sign-in task is registered through Task Scheduler's
/// own interface from text in memory; files are written and deleted through the file system.</item>
/// <item>The program installed is exactly the file Windows started for the elevated step, read while it is locked.</item>
/// </list>
/// </summary>
static class Setup
{
    const string Name = Identity.Product;   // "RestoreWin10"
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Name;

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Name);
    public static string InstalledExe => Path.Combine(InstallDir, Name + ".exe");
    /// <summary>Where v1 installed itself, per user.</summary>
    static string V1Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    // Exit codes between the two halves.
    internal const int Ok = 0, Failed = 1, Cancelled = 2, OkRestartPending = 3, NewerInstalled = 4;

    static string Version =>
        typeof(Setup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "2.0.0";

    /// <summary>This process is the installed copy in Program Files (not a downloaded installer).</summary>
    public static bool IsInstalledCopy => string.Equals(Environment.ProcessPath, InstalledExe, StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        bool quiet = args.Contains("--quiet");
        if (Identity.Dev)
        {
            Message("This is a development build of RestoreWin10. Only release builds can be installed, uninstalled or used for recovery.", MB_ICONINFORMATION, quiet);
            return Failed;
        }
        // The elevated step takes only fixed switches: nothing in its command line is a path, a name or free text.
        if (args.Contains("--install-elevated"))
            return Elevated(quiet, () => InstallElevated(signIn: !args.Contains("--no-sign-in"), start: !args.Contains("--no-start")));
        if (args.Contains("--uninstall-elevated")) return Elevated(quiet, UninstallElevated);
        if (args.Contains("--install")) return Install(quiet);
        if (args.Contains("--uninstall")) return Uninstall(quiet);
        if (args.Contains("--restore-taskbar")) return TaskbarRecovery.Run(quiet);
        return Failed;
    }

    // ---------- versions: install, update, reinstall, or refuse an older version ----------

    public enum Plan { Install, Update, Reinstall, NewerInstalled }

    /// <summary>"2.0.8" or "2.0.8+commit" as major.minor.build (what decides which version is newer); null if unreadable.</summary>
    internal static System.Version? ParseVersion(string? s)
    {
        if (s == null || !System.Version.TryParse(s.Split('+')[0].Trim(), out var v)) return null;
        return new System.Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    internal static System.Version OwnVersion => ParseVersion(Version) ?? new System.Version(0, 0, 0);

    /// <summary>The version installed in Program Files, or null when none is (or it cannot be read).</summary>
    internal static System.Version? InstalledVersion()
    {
        try { return File.Exists(InstalledExe) ? ParseVersion(FileVersionInfo.GetVersionInfo(InstalledExe).ProductVersion) : null; }
        catch { return null; }
    }

    internal static Plan PlanFor(System.Version own, System.Version? installed) =>
        installed == null ? Plan.Install : own > installed ? Plan.Update : own == installed ? Plan.Reinstall : Plan.NewerInstalled;

    // ---------- the one remembered choice ----------

    const string SignInValue = "StartAtSignIn";

    /// <summary>Whether RestoreWin10 starts at sign-in, as chosen at the last install (on when never chosen). Only the
    /// default of a check box: nothing else depends on it.</summary>
    internal static bool RememberedSignIn()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(UninstallKey);
            return k?.GetValue(SignInValue) is not int v || v != 0;
        }
        catch { return true; }
    }

    // ---------- as the user ----------

    /// <summary>Double-clicking a downloaded RestoreWin10.exe: the install window (release builds only).</summary>
    public static int Wizard()
    {
        var w = new InstallWindow(OwnVersion, InstalledVersion(), RememberedSignIn());
        w.ShowDialog();
        return w.ExitCode;
    }

    static int Install(bool quiet)
    {
        if (PlanFor(OwnVersion, InstalledVersion()) == Plan.NewerInstalled)
        {
            Message($"A newer {Name} ({InstalledVersion()!.ToString(3)}) is already installed, so this installer ({OwnVersion.ToString(3)}) " +
                    "changed nothing. To go back to this version, uninstall RestoreWin10 from Settings > Apps first.", MB_ICONINFORMATION, quiet);
            return NewerInstalled;
        }
        int code = RunInstall(RememberedSignIn(), startAfter: true, quiet, out _);
        if (code == Ok)
            Message($"{Name} {Version} is installed in {InstallDir} and is starting now. It starts by itself when you sign in.\n\n" +
                    "To remove it, use Settings > Apps > Installed apps.", MB_ICONINFORMATION, quiet);
        return code;
    }

    /// <summary>
    /// Installs or updates, shared by the install window and <c>--install</c>. A running RestoreWin10 is closed through
    /// Windows' Restart Manager from this, the user's, side, and started again by it afterwards (or put back if the install
    /// did not go through), so it always runs as the user and never with administrator rights. The elevated step's own close
    /// request stays as the fallback. <paramref name="restarted"/>: Restart Manager started RestoreWin10 again.
    /// </summary>
    internal static int RunInstall(bool signIn, bool startAfter, bool quiet, out bool restarted)
    {
        restarted = false;
        using var closer = ShellCloser.CloseRunning();
        var step = new List<string> { "--install-elevated", "--no-start" };
        if (!signIn) step.Add("--no-sign-in");
        if (quiet) step.Add("--quiet");
        int code = IsElevated ? Elevated(quiet, () => InstallElevated(signIn, start: false)) : Elevate(step, quiet);
        if (closer != null) restarted = closer.Restart();   // also when the install did not go through: put back what ran
        if (code == Ok && !restarted && startAfter) StartShell();
        return code;
    }

    /// <summary>
    /// Starts the installed RestoreWin10 as the user: through its sign-in task (the way it starts at sign-in), or, if this
    /// account may not run the task directly, the program itself. Never with administrator rights.
    /// </summary>
    internal static void StartShell()
    {
        try { SignInTask.Run(); return; } catch { }
        if (IsElevated || !File.Exists(InstalledExe)) return;
        try
        {
            var psi = new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = Launcher.System32 };
            psi.ArgumentList.Add("--replace");
            Process.Start(psi)?.Dispose();
        }
        catch { }
    }

    /// <summary>
    /// Closes the installed RestoreWin10 the way installers do: Windows' Restart Manager asks it to close (never forced),
    /// and after the install starts it again with the arguments it registered ("--replace", see AppRestart). Only from a
    /// process without administrator rights, so the restarted shell is not elevated.
    /// </summary>
    sealed class ShellCloser : IDisposable
    {
        uint _session;
        bool _closed;

        /// <summary>Null when nothing is running, when this process is elevated, or when Restart Manager could not close
        /// it (the elevated step then asks it to close itself).</summary>
        public static ShellCloser? CloseRunning()
        {
            if (IsElevated || RunningShells().Count == 0) return null;
            var c = new ShellCloser();
            if (RmStartSession(out c._session, 0, new System.Text.StringBuilder(64)) != 0) return null;
            if (RmRegisterResources(c._session, 1, new[] { InstalledExe }, 0, null, 0, null) != 0 || RmShutdown(c._session, 0, 0) != 0)
            {
                c.Dispose();
                return null;
            }
            c._closed = true;
            return c;
        }

        public bool Restart() => _closed && _session != 0 && RmRestart(_session, 0, 0) == 0;

        public void Dispose()
        {
            if (_session != 0) RmEndSession(_session);
            _session = 0;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint handle, int flags, System.Text.StringBuilder key);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint handle, uint nFiles, string[] files, uint nApps, nint[]? apps, uint nServices, string[]? services);
        [DllImport("rstrtmgr.dll")] static extern int RmShutdown(uint handle, uint flags, nint callback);
        [DllImport("rstrtmgr.dll")] static extern int RmRestart(uint handle, int reserved, nint callback);
        [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint handle);
    }

    static int Uninstall(bool quiet)
    {
        if (!quiet && Ask($"Remove {Name}?\n\nThe Windows 11 Start menu and taskbar come back. Your tiles and settings stay in " +
                          $"%APPDATA%\\{Name}, in case you install it again.") != IDYES)
            return Cancelled;
        int code = IsElevated ? Elevated(quiet, UninstallElevated) : Elevate("--uninstall-elevated", quiet);
        if (code is not (Ok or OkRestartPending)) return code;

        // If RestoreWin10 ever crashed while replacing the taskbar, Explorer's own setting is put back. That reads and writes
        // state.json in the user's profile, so only a non-elevated uninstaller does it.
        if (!IsElevated) TaskbarRecovery.RestoreSavedState();
        if (code == OkRestartPending)
        {
            if (!quiet && Ask($"{Name} has been removed. Windows deletes its last program file when the PC restarts.\n\nRestart now?") == IDYES)
                Session.Restart(installUpdates: false);   // Windows' normal restart: programs with unsaved work can still object
        }
        else Message($"{Name} has been removed.", MB_ICONINFORMATION, quiet);
        return Ok;
    }

    /// <summary>Runs this exe again with administrator rights (one UAC prompt) and returns the elevated step's result.</summary>
    static int Elevate(string step, bool quiet)
    {
        var args = new List<string> { step };
        if (quiet) args.Add("--quiet");
        return Elevate(args, quiet);
    }

    static int Elevate(List<string> args, bool quiet)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Launcher.System32 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return Cancelled; }   // UAC prompt declined
    }

    /// <summary>
    /// Removes what v1 left in the user's profile: its Settings > Apps entry and its program file. Called by the installed
    /// v2 shell at startup, as the user: the elevated steps never touch the profile.
    /// </summary>
    public static void RemoveV1Leftovers()
    {
        if (Identity.Dev || !string.Equals(Environment.ProcessPath, InstalledExe, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Name))
                if (k?.GetValue("InstallLocation") is string loc && string.Equals(loc.TrimEnd('\\'), V1Dir, StringComparison.OrdinalIgnoreCase))
                {
                    k.Dispose();
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Name, false);
                    CrashLog.Note("removed RestoreWin10 1.x's Settings > Apps entry");
                }
            var v1Exe = Path.Combine(V1Dir, Name + ".exe");
            if (Directory.Exists(V1Dir) && !IsReparsePoint(V1Dir) && File.Exists(v1Exe))
            {
                File.Delete(v1Exe);
                CrashLog.Note("removed RestoreWin10 1.x's program file");
            }
            if (Directory.Exists(V1Dir) && !IsReparsePoint(V1Dir) && !Directory.EnumerateFileSystemEntries(V1Dir).Any())
                Directory.Delete(V1Dir);
        }
        catch (Exception ex) { CrashLog.Write("removing RestoreWin10 1.x", ex); }
    }

    // ---------- elevated ----------

    static int Elevated(bool quiet, Func<int> step)
    {
        CrashLog.Enabled = false;   // the user's profile is off limits to elevated code
        if (!IsElevated) { Message("This step needs administrator rights.", MB_ICONERROR, quiet); return Failed; }
        try { return step(); }
        catch (Exception ex)
        {
            Message($"{Name} could not finish: {ex.Message}", MB_ICONERROR, quiet);
            return Failed;
        }
    }

    static int InstallElevated(bool signIn, bool start)
    {
        // Checked here as well as in the install window: an older version never replaces a newer one, however it was started.
        if (PlanFor(OwnVersion, InstalledVersion()) == Plan.NewerInstalled) return NewerInstalled;
        byte[] program = ReadOwnImage();
        StopShells();
        PrepareFolder();
        WriteProgram(program);
        WriteAppsEntry(program.Length, signIn);
        var user = WindowsIdentity.GetCurrent().User!;   // elevated, still this account (UAC elevates the same user)
        SignInTask.Register(InstalledExe, user, signIn);
        if (start) SignInTask.Run();                     // starts the shell as the user, not elevated
        return Ok;
    }

    static int UninstallElevated()
    {
        StopShells();
        SignInTask.Delete();
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        return RemoveProgramFiles() ? OkRestartPending : Ok;
    }

    /// <summary>
    /// The bytes of the exe Windows started for this process. The file is opened so it cannot be changed, renamed or deleted
    /// while it is read, and Windows confirms (NtAreMappedFilesTheSame) that the file at that path is still the one mapped as
    /// this process's image: a file swapped in after the UAC prompt is refused.
    /// </summary>
    internal static byte[] ReadOwnImage()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("the installer's own location is unknown");
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!IsRunningImage(fs))
            throw new InvalidOperationException("the installer file was changed after Windows started it, so nothing was installed");
        var bytes = new byte[fs.Length];
        fs.Position = 0;
        fs.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>True when the open file is the one Windows mapped as this process's image.</summary>
    internal static bool IsRunningImage(FileStream fs)
    {
        using var map = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        nint mapped = view.SafeMemoryMappedViewHandle.DangerousGetHandle() + (nint)view.PointerOffset;
        return NtAreMappedFilesTheSame(GetModuleHandleW(null), mapped) == 0;
    }

    /// <summary>Asks every running RestoreWin10 (v1 or v2) to close, and waits until they have.</summary>
    static void StopShells()
    {
        NamedObjects.SetExisting(Name + ".Exit");   // the running shell closes and gives the Windows taskbar back
        var deadline = DateTime.Now.AddSeconds(15);
        while (DateTime.Now < deadline && RunningShells().Count > 0) Thread.Sleep(250);
        if (RunningShells().Count > 0)
            throw new InvalidOperationException($"{Name} is still running and did not close when asked. Close it from its tray icon " +
                                                $"(Exit {Name}), then try again");
    }

    /// <summary>RestoreWin10 processes started from an installed location (v1's or v2's), other than this one.</summary>
    static List<int> RunningShells()
    {
        var installed = new[] { InstalledExe, Path.Combine(V1Dir, Name + ".exe") };
        var list = new List<int>();
        foreach (var p in Process.GetProcessesByName(Name))
            using (p)
                if (p.Id != Environment.ProcessId && Win.ExePath((uint)p.Id) is { } exe && installed.Contains(exe, StringComparer.OrdinalIgnoreCase))
                    list.Add(p.Id);
        return list;
    }

    /// <summary>Creates %ProgramFiles%\RestoreWin10 if needed (it inherits Program Files' permissions: only administrators and
    /// SYSTEM can change it) and refuses to use it if it, or Program Files itself, is a junction or link.</summary>
    static void PrepareFolder()
    {
        var parent = Path.GetDirectoryName(InstallDir)!;
        if (IsReparsePoint(parent)) throw new InvalidOperationException($"{parent} is a link or junction");
        if (!Directory.Exists(InstallDir)) Directory.CreateDirectory(InstallDir);
        if (IsReparsePoint(InstallDir)) throw new InvalidOperationException($"{InstallDir} is a link or junction");
    }

    /// <summary>Writes the program as a new file and moves it into place. A copy still in use is renamed aside and deleted at the next restart.</summary>
    static void WriteProgram(byte[] program)
    {
        var target = InstalledExe;
        var fresh = target + ".new";
        if (File.Exists(fresh)) File.Delete(fresh);
        using (var fs = new FileStream(fresh, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.Write(program);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(target))
        {
            try { File.Delete(target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var aside = target + ".old-" + Guid.NewGuid().ToString("N")[..8];
                File.Move(target, aside);
                DeleteAtRestart(aside);
            }
        }
        File.Move(fresh, target);
    }

    /// <summary>The entry Settings > Apps shows for the PC, with the icon, version, size and Uninstall button.</summary>
    static void WriteAppsEntry(long size, bool signIn)
    {
        using var k = Registry.LocalMachine.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", Name);
        k.SetValue("DisplayVersion", Version);
        k.SetValue("Publisher", Name);
        k.SetValue("Comments", "Windows 10 Start menu and taskbar for Windows 11");
        k.SetValue("DisplayIcon", $"{InstalledExe},0");
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        k.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
        k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        k.SetValue("EstimatedSize", (int)((size + 1023) / 1024), RegistryValueKind.DWord);
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        k.SetValue(SignInValue, signIn ? 1 : 0, RegistryValueKind.DWord);   // remembered for the next install or update
    }

    /// <summary>
    /// Deletes RestoreWin10's own files from its program folder, by name (nothing else in it is touched), then the folder if
    /// it is left empty. Files still in use (this uninstaller is usually one of them) are deleted at the next restart.
    /// Returns true when something waits for the restart.
    /// </summary>
    static bool RemoveProgramFiles()
    {
        if (!Directory.Exists(InstallDir)) return false;
        if (IsReparsePoint(InstallDir)) throw new InvalidOperationException($"{InstallDir} is a link or junction");
        bool pending = false;
        foreach (var file in Directory.EnumerateFiles(InstallDir, Name + ".exe*").ToList())   // RestoreWin10.exe, .new, .old-xxxxxxxx
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use (this uninstaller runs from it). It is renamed to a name of its own first, so the deletion waiting
                // for the restart can never hit a RestoreWin10.exe installed again before that restart.
                var aside = Path.GetFileName(file).Contains(".old-") ? file : file + ".old-" + Guid.NewGuid().ToString("N")[..8];
                if (aside != file) File.Move(file, aside);
                DeleteAtRestart(aside);
                pending = true;
            }
        }
        if (!Directory.EnumerateFileSystemEntries(InstallDir).Any()) Directory.Delete(InstallDir);
        else if (pending && Directory.EnumerateFiles(InstallDir).All(f => Path.GetFileName(f).StartsWith(Name + ".exe", StringComparison.OrdinalIgnoreCase))
                 && !Directory.EnumerateDirectories(InstallDir).Any())
            DeleteAtRestart(InstallDir);   // after its files, which were registered first
        return pending;
    }

    static void DeleteAtRestart(string path)
    {
        if (!MoveFileExW(path, null, MOVEFILE_DELAY_UNTIL_REBOOT)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    static bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    // ---------- message boxes (no WPF here) ----------

    const uint MB_YESNO = 0x4, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONINFORMATION = 0x40, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
    const int IDYES = 6;

    internal static void Message(string text, uint icon, bool quiet)
    {
        if (!quiet) MessageBoxW(0, text, Name, icon | MB_SETFOREGROUND | MB_TOPMOST);
    }

    static int Ask(string text) => MessageBoxW(0, text, Name, MB_YESNO | MB_ICONQUESTION | MB_SETFOREGROUND | MB_TOPMOST);

    // ---------- Win32 ----------

    const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(nint owner, string text, string caption, uint type);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool MoveFileExW(string from, string? to, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);
    [DllImport("ntdll.dll")] static extern int NtAreMappedFilesTheSame(nint mappedAsImage, nint mappedAsFile);
}

/// <summary>
/// The sign-in task, through Task Scheduler's own COM interface (no schtasks.exe, no temporary file). It starts RestoreWin10
/// for this account when it signs in, 8 seconds later, at normal priority, not elevated, and never times it out.
/// <para>
/// It also starts RestoreWin10 again 5 seconds after it crashes, the way Windows restarts Explorer: Windows records a
/// crash as "Application Error" event 1000 naming the program, and that event is the task's second trigger. A program
/// ended on purpose (Task Manager's End task) is not restarted, as with Explorer. (The task's "restart on failure"
/// setting does not cover this: the Phase 8 crash test showed it only applies when the task cannot start at all.)
/// After three crashes within ten minutes the shell gives the Windows taskbar back and stays off (App.OnStartup).
/// </para>
/// </summary>
static class SignInTask
{
    const string TaskName = Identity.Product;
    static readonly Guid TaskSchedulerClass = new("0f87369f-a4e5-4cfc-bd3e-73e6154572dd");   // by class id, not by a name the registry maps
    const int TASK_CREATE_OR_UPDATE = 6, TASK_LOGON_INTERACTIVE_TOKEN = 3;

    internal static dynamic RootFolder()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromCLSID(TaskSchedulerClass, throwOnError: true)!)!;
        service.Connect();
        return service.GetFolder("\\");
    }

    /// <param name="signIn">Start at sign-in (the install window's choice). Off: only the sign-in trigger is disabled; the
    /// restart after a crash and starting the task by hand still work.</param>
    public static void Register(string exe, SecurityIdentifier user, bool signIn = true)
    {
        dynamic root = RootFolder();
        DeleteIfPresent(root, TaskName);   // v1's per-user task, or an earlier v2 one
        // Administrators and SYSTEM may change it; this account may read and run it, but not change what it starts.
        string security = $"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;{user.Value})";
        root.RegisterTask(TaskName, Xml(exe, user, signIn), TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, security);
    }

    public static void Run() => RootFolder().GetTask(TaskName).Run(null);

    public static void Delete() => DeleteIfPresent(RootFolder(), TaskName);

    /// <summary>
    /// Deletes a task if there is one. Task Scheduler answers "no such task" with 0x80070002, which .NET raises as
    /// FileNotFoundException (not COMException), so the answer is recognised by its code; any other error still stops setup.
    /// </summary>
    internal static void DeleteIfPresent(dynamic root, string name)
    {
        try { root.DeleteTask(name, 0); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { }
    }

    /// <summary>Windows' record of RestoreWin10 crashing (Application Error, event 1000, naming the program).</summary>
    internal const string CrashQuery =
        "<QueryList><Query Id=\"0\" Path=\"Application\"><Select Path=\"Application\">" +
        "*[System[Provider[@Name='Application Error'] and EventID=1000]] and *[EventData[Data='RestoreWin10.exe']]" +
        "</Select></Query></QueryList>";

    internal static string Xml(string exe, SecurityIdentifier user, bool signIn = true)
    {
        string sid = SecurityElement.Escape(user.Value)!;
        string atSignIn = signIn ? "true" : "false";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts {TaskName} (Windows 10 Start menu and taskbar) when you sign in.</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>{atSignIn}</Enabled><UserId>{sid}</UserId><Delay>PT8S</Delay></LogonTrigger>
                <EventTrigger><Enabled>true</Enabled><Subscription>{SecurityElement.Escape(CrashQuery)}</Subscription><Delay>PT5S</Delay></EventTrigger>
              </Triggers>
              <Principals><Principal id="Author"><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
                <RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
              </Settings>
              <Actions Context="Author"><Exec>
                <Command>{SecurityElement.Escape(exe)}</Command><Arguments>--replace</Arguments>
                <WorkingDirectory>{SecurityElement.Escape(Environment.SystemDirectory)}</WorkingDirectory>
              </Exec></Actions>
            </Task>
            """;
    }
}

/// <summary>
/// Giving the Windows taskbar back (threat model F-12). While replacing it, RestoreWin10 turns on auto-hide for Explorer's
/// taskbar and hides it; the setting Explorer had before is kept in state.json until RestoreWin10 restores it. If
/// RestoreWin10 was ended abruptly, <c>--restore-taskbar</c> (or the next start, or uninstalling) puts it right. This
/// replaces v1's RestoreTaskbar.ps1.
/// </summary>
static class TaskbarRecovery
{
    const int ABM_GETSTATE = 4, ABM_SETSTATE = 10, ABS_AUTOHIDE = 1;

    public static int Run(bool quiet)
    {
        // Ask RestoreWin10 to close (it gives the taskbar back itself); one that does not answer within 5 seconds is ended.
        NamedObjects.SetExisting(Identity.Product + ".Exit");
        var deadline = DateTime.Now.AddSeconds(5);
        while (DateTime.Now < deadline && InstalledShells().Any()) Thread.Sleep(250);
        foreach (var p in InstalledShells()) using (p) { try { p.Kill(); p.WaitForExit(3000); } catch { } }

        RestoreSavedState(force: true);
        foreach (var h in ExplorerBars()) Win.ShowWindowAsync(h, Win.SW_SHOW);
        Win.PostMessage(0xFFFF, Win.RegisterWindowMessage("TaskbarCreated"), 0, 0);   // tray icons go back to Explorer
        Setup.Message("The Windows taskbar is back.", 0x40, quiet);
        return 0;
    }

    /// <summary>
    /// At startup without replacing the taskbar (preview mode): if the last run ended abruptly while replacing it, Explorer's
    /// taskbar is shown again and its own auto-hide setting put back.
    /// </summary>
    public static void RecoverAfterCrash()
    {
        if (State.Shared.ExplorerTaskbarState is null) return;
        RestoreSavedState();
        foreach (var h in ExplorerBars()) Win.ShowWindowAsync(h, Win.SW_SHOW);
        CrashLog.Note("the last run ended while replacing the Windows taskbar; it has been given back");
    }

    /// <summary>Puts back Explorer's taskbar setting if RestoreWin10 left it changed (or, with <paramref name="force"/>,
    /// turns auto-hide off when nothing was recorded). As the user only.</summary>
    public static void RestoreSavedState(bool force = false)
    {
        var st = State.Shared;
        if (st.ExplorerTaskbarState is not int before && !force) return;
        var tray = ExplorerBars().FirstOrDefault();
        var abd = new Win.APPBARDATA { cbSize = Marshal.SizeOf<Win.APPBARDATA>(), hWnd = tray };
        int state = st.ExplorerTaskbarState ?? ((int)Win.SHAppBarMessage(ABM_GETSTATE, ref abd) & ~ABS_AUTOHIDE);
        abd.lParam = state;
        Win.SHAppBarMessage(ABM_SETSTATE, ref abd);
        st.ExplorerTaskbarState = null;
        st.Save();
    }

    /// <summary>Explorer's own taskbar windows (main and secondary), not RestoreWin10's.</summary>
    static List<nint> ExplorerBars()
    {
        var bars = new List<nint>();
        Win.EnumWindows((h, _) =>
        {
            if (Win.ClassName(h) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                Win.GetWindowThreadProcessId(h, out uint pid);
                if (Win.ExePath(pid) is { } exe && Path.GetFileName(exe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                    if (Win.ClassName(h) == "Shell_TrayWnd") bars.Insert(0, h); else bars.Add(h);
            }
            return true;
        }, 0);
        return bars;
    }

    static List<Process> InstalledShells() =>
        Process.GetProcessesByName(Identity.Product)
               .Where(p => p.Id != Environment.ProcessId && Win.ExePath((uint)p.Id) is { } exe &&
                           (exe.Equals(Setup.InstalledExe, StringComparison.OrdinalIgnoreCase) ||
                            exe.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Identity.Product, Identity.Product + ".exe"), StringComparison.OrdinalIgnoreCase)))
               .ToList();
}
