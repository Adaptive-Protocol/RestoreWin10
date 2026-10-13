using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace RestoreWin10;

/// <summary>
/// Every program RestoreWin10 starts goes through here (threat model F-01, F-08, F-10).
/// <list type="bullet">
/// <item>Windows' own programs are named by full path, so nothing planted in a writable folder, the PATH or App Paths can
/// stand in for them, and UAC prompts name Microsoft as the publisher.</item>
/// <item>Arguments are passed as a list, never glued into a command line, and the working directory is one the user
/// cannot write to.</item>
/// <item>What the user chose (apps, tiles, pins, recent files) is started only when it is a local file of a runnable kind
/// or a Store app id. Web addresses, network paths and anything else are refused and noted in crash.log.</item>
/// </list>
/// </summary>
static class Launcher
{
    public static readonly string System32 = Environment.SystemDirectory;
    static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>A file in System32, by full path.</summary>
    public static string Sys(string relative) => Path.Combine(System32, relative);
    public static string PowerShell => Sys(@"WindowsPowerShell\v1.0\powershell.exe");
    public static string Explorer => Path.Combine(WindowsDir, "explorer.exe");

    public const string StorePrefix = @"shell:AppsFolder\";

    // ---------- Windows' own programs and pages ----------

    /// <summary>One of Windows' own programs, by full path, through the shell (programs that need elevation get their UAC prompt).</summary>
    public static void Windows(string fullPath, bool elevated = false, params string[] args)
    {
        if (!Path.IsPathFullyQualified(fullPath)) { Refused("not a full path", fullPath); return; }
        var psi = new ProcessStartInfo(fullPath) { UseShellExecute = true, WorkingDirectory = System32 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (elevated) psi.Verb = "runas";
        Start(psi);
    }

    /// <summary>A management console (eventvwr.msc...) through mmc.exe, both by full path.</summary>
    public static void Console(string msc) => Windows(Sys("mmc.exe"), false, Sys(msc));

    /// <summary>Settings' main page. (A bare "ms-settings:" only brings an open Settings window forward, on whatever page it was left.)</summary>
    public const string SettingsHome = "ms-settings:home";

    /// <summary>Windows Security's own address (registered by Windows for the Windows Security app).</summary>
    public const string WindowsSecurity = "windowsdefender:";

    /// <summary>A Settings page. Only Windows' own settings addresses are accepted; a bare "ms-settings:" opens the main page.</summary>
    public static void Settings(string uri)
    {
        bool known = uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ||
                     uri.Equals("ms-availablenetworks:", StringComparison.OrdinalIgnoreCase) ||
                     uri.Equals(WindowsSecurity, StringComparison.OrdinalIgnoreCase);
        if (!known || uri.IndexOfAny(Unsafe) >= 0) { Refused("not a Settings address", uri); return; }
        if (uri.Equals("ms-settings:", StringComparison.OrdinalIgnoreCase)) uri = SettingsHome;
        Start(new ProcessStartInfo(uri) { UseShellExecute = true, WorkingDirectory = System32 });
    }

    /// <summary>The Run dialog.</summary>
    public static void RunDialog() =>
        Start(new ProcessStartInfo("shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}") { UseShellExecute = true, WorkingDirectory = System32 });

    /// <summary>A folder in File Explorer.</summary>
    public static void Folder(string path)
    {
        if (!IsLocalPath(path) || !Directory.Exists(path)) { Refused("not a local folder", path); return; }
        Windows(Explorer, false, path);
    }

    /// <summary>File Explorer with <paramref name="path"/> selected in its folder (no command line involved).</summary>
    public static void ShowInFolder(string path)
    {
        if (!IsLocalPath(path) || !(File.Exists(path) || Directory.Exists(path))) { Refused("not a local file", path); return; }
        nint pidl = ILCreateFromPathW(path);
        if (pidl == 0) return;
        try { SHOpenFolderAndSelectItems(pidl, 0, null, 0); }
        finally { ILFree(pidl); }
    }

    // ---------- what the user chose ----------

    /// <summary>
    /// An app, tile, pin or recent file. <paramref name="argument"/> (a document for the program) is passed as one
    /// argument, quoted by the system; <paramref name="verb"/> is a shell verb such as "runas" or "properties".
    /// Returns false (and notes why in crash.log) when the target is refused.
    /// </summary>
    public static bool Item(string target, string? argument = null, string? verb = null)
    {
        if (!IsLaunchable(target)) { Refused("not something RestoreWin10 starts", target); return false; }
        bool store = target.StartsWith(StorePrefix, StringComparison.OrdinalIgnoreCase);
        if (!store && !File.Exists(target)) { Refused("file not found", target); return false; }
        if (argument != null && (argument.IndexOfAny(Unsafe) >= 0 || !Path.IsPathFullyQualified(argument)))
        { Refused("not a full path", argument); return false; }

        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        // A program starts in its own folder, as when it is double-clicked; a shortcut brings its own "Start in".
        psi.WorkingDirectory = !store && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(target)! : System32;
        if (argument != null) psi.ArgumentList.Add(argument);
        if (verb != null) psi.Verb = verb;
        return Start(psi);
    }

    // Characters that never belong in a path or Settings address RestoreWin10 hands to the shell.
    static readonly char[] Unsafe = { '"', '\0', '\r', '\n', '<', '>', '|' };
    static readonly string[] Runnable = { ".lnk", ".exe", ".msc", ".cpl" };

    /// <summary>A drive path on this PC: fully qualified, not a network or device path, no alternate data stream, no odd characters.</summary>
    public static bool IsLocalPath(string? p) =>
        !string.IsNullOrWhiteSpace(p) && p.Length < 1024 && p.IndexOfAny(Unsafe) < 0 &&
        Path.IsPathFullyQualified(p) && !p.StartsWith(@"\\", StringComparison.Ordinal) && !p.StartsWith("//", StringComparison.Ordinal) &&
        p.IndexOf(':', 2) < 0;

    /// <summary>A document a program may be given to open (a recent item): a full path, local or on the network, with no odd characters.</summary>
    public static bool IsDocumentPath(string? p) =>
        !string.IsNullOrWhiteSpace(p) && p.Length < 1024 && p.IndexOfAny(Unsafe) < 0 && Path.IsPathFullyQualified(p) &&
        !p.StartsWith(@"\\?\", StringComparison.Ordinal) && !p.StartsWith(@"\\.\", StringComparison.Ordinal) && p.IndexOf(':', 2) < 0;

    /// <summary>
    /// A local path on a drive of this PC (not a network share, not a mapped network drive): looking at it never contacts
    /// another computer, so it cannot hand the user's Windows sign-in to a server someone else chose (threat model F-09).
    /// </summary>
    public static bool IsOnThisPc(string? p)
    {
        if (!IsLocalPath(p)) return false;
        try { return new DriveInfo(Path.GetPathRoot(p)!).DriveType is not (DriveType.Network or DriveType.NoRootDirectory); }
        catch { return false; }
    }

    /// <summary>What may be kept as a tile, pin or app and started: a Store app id, or a local shortcut, program, console or Control Panel item.</summary>
    public static bool IsLaunchable(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return false;
        if (t.StartsWith(StorePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = t[StorePrefix.Length..];   // e.g. Microsoft.WindowsCalculator_8wekyb3d8bbwe!App
            return id.Length is > 2 and < 256 && id.Contains('!') &&
                   id.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|', '\0' }) < 0;
        }
        return IsLocalPath(t) && Runnable.Contains(Path.GetExtension(t), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A program's own jump-list task: a program (.exe) on this PC, started with the command line that program wrote for
    /// the task ("-private-window"). The command line is passed as the program wrote it, so it must hold no control
    /// characters; anything else is refused and noted (threat model F-09).
    /// </summary>
    public static bool Task(string exe, string arguments)
    {
        if (!IsOnThisPc(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) { Refused("task is not a program on this PC", exe); return false; }
        if (arguments.Length > 2048 || arguments.Any(char.IsControl)) { Refused("task command line", exe); return false; }
        return Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = arguments, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }

    // ---------- hidden helper programs (installer, shutdown fallback) ----------

    /// <summary>Runs a Windows program by full path, hidden and without the shell; returns its exit code and output.</summary>
    public static (int code, string output) Helper(string fullPath, params string[] args)
    {
        var psi = new ProcessStartInfo(fullPath)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = System32,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.HasExited ? p.ExitCode : -1, output);
    }

    // ---------- plumbing ----------

    static bool Start(ProcessStartInfo psi)
    {
        try { Process.Start(psi)?.Dispose(); return true; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; }   // UAC prompt declined
        catch (Exception ex) { CrashLog.Write("starting " + psi.FileName, ex); return false; }
    }

    static void Refused(string why, string what) => CrashLog.Note($"refused to start ({why}): {what}");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern nint ILCreateFromPathW(string path);
    [DllImport("shell32.dll")] static extern int SHOpenFolderAndSelectItems(nint pidlFolder, uint count, nint[]? items, uint flags);
    [DllImport("shell32.dll")] static extern void ILFree(nint pidl);
}
