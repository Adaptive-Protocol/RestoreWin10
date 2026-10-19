using System.Runtime.InteropServices;

namespace RestoreWin10;

/// <summary>
/// Being closed by an installer, and coming back afterwards.
/// <para>
/// Installers and updaters (Windows Installer, Office Click-to-Run, Windows Update) close programs that hold files they
/// must replace through Windows' Restart Manager. RestoreWin10 can hold such files (icons and shell extensions of
/// installed programs), and was once ended that way overnight by an Office update: it did not come back until the
/// next sign-in, because a copy ended on purpose is not restarted by the sign-in task (only a crash is).
/// </para>
/// <para>
/// So the installed shell registers its restart with Windows: Restart Manager starts it again, with "--replace", once
/// the installer is done. Only for that case: a crash is left to the sign-in task (with its three-crashes limit) and a
/// restart of the PC to the sign-in itself, so nothing is started twice. The command line is fixed here; nothing in it
/// comes from outside.
/// </para>
/// </summary>
static class AppRestart
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int RegisterApplicationRestart(string? commandLine, int flags);
    [DllImport("kernel32.dll")] static extern int UnregisterApplicationRestart();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetApplicationRestartSettings(nint process, char[]? commandLine, ref int size, out int flags);
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();

    const int RESTART_NO_CRASH = 1, RESTART_NO_HANG = 2, RESTART_NO_REBOOT = 8;

    /// <summary>The arguments Windows starts RestoreWin10 with after an installer closed it.</summary>
    internal const string CommandLine = "--replace";
    /// <summary>Only after an installer or update ("patch"): not after a crash, a hang or a restart of the PC.</summary>
    internal const int Flags = RESTART_NO_CRASH | RESTART_NO_HANG | RESTART_NO_REBOOT;

    /// <summary>Registers the restart (the installed shell, replacing the taskbar). Takes effect after 60 s of running.</summary>
    public static bool Register()
    {
        int hr = RegisterApplicationRestart(CommandLine, Flags);
        if (hr != 0) CrashLog.Note($"could not register restart after updates (0x{hr:x8})");
        return hr == 0;
    }

    public static void Unregister() => UnregisterApplicationRestart();

    /// <summary>What Windows has on record for this process (for the self-test), or null.</summary>
    internal static (string CommandLine, int Flags)? Registered()
    {
        int size = 0;
        if (GetApplicationRestartSettings(GetCurrentProcess(), null, ref size, out _) != 0 && size == 0) return null;
        var buf = new char[Math.Max(size, 1)];
        if (GetApplicationRestartSettings(GetCurrentProcess(), buf, ref size, out int flags) != 0) return null;
        return (new string(buf).TrimEnd('\0'), flags);
    }

    // ---------- being asked to close ----------

    const int ENDSESSION_CLOSEAPP = 0x1;

    /// <summary>
    /// WM_ENDSESSION from Restart Manager (an installer closing this program, not the user signing out): true when
    /// the session is really ending for us and it is an application close.
    /// </summary>
    public static bool IsInstallerClose(nint wParam, nint lParam) => wParam != 0 && (lParam.ToInt64() & ENDSESSION_CLOSEAPP) != 0;
}
