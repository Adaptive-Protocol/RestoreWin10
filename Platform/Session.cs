using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace RestoreWin10;

/// <summary>
/// The account and power actions of the Windows 10 Start menu, done the way it does them and offering the same choices:
/// Sleep where the PC can sleep, Hibernate where it can hibernate, "Update and shut down / restart" only while Windows has
/// updates waiting, Shut down using fast startup when that is turned on. Shutting down and restarting go through Windows'
/// normal shutdown, so programs with unsaved work can still ask to stay open.
/// </summary>
static class Session
{
    /// <summary>One menu choice: plain text (Start), text with an access key (Win+X menu), an icon glyph, the action.</summary>
    public sealed record Choice(string Text, string AccessText, string Glyph, Action Act);

    [DllImport("powrprof.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool IsPwrSuspendAllowed();
    [DllImport("powrprof.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool IsPwrHibernateAllowed();
    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force,
                                       [MarshalAs(UnmanagedType.U1)] bool disableWakeEvents);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint InitiateShutdown(string? machine, string? message, uint grace, uint flags, uint reason);
    [DllImport("user32.dll")] static extern bool ExitWindowsEx(uint flags, uint reason);
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("wtsapi32.dll")] static extern bool WTSDisconnectSession(nint server, int sessionId, bool wait);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [StructLayout(LayoutKind.Sequential, Pack = 4)] struct TOKEN_PRIVILEGES { public uint Count; public long Luid; public uint Attributes; }
    [DllImport("advapi32.dll")] static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TOKEN_PRIVILEGES state, uint length, nint previous, nint returned);
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("secur32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserNameEx(int format, StringBuilder name, ref uint size);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern int NetUserGetInfo(string? server, string user, int level, out nint buffer);
    [DllImport("netapi32.dll")] static extern int NetApiBufferFree(nint buffer);

    const uint SHUTDOWN_RESTART = 0x4, SHUTDOWN_POWEROFF = 0x8, SHUTDOWN_INSTALL_UPDATES = 0x40, SHUTDOWN_HYBRID = 0x200;
    const uint REASON_PLANNED = 0x80000000;   // "other", planned - what a user-chosen shutdown reports

    // ---------- what to offer ----------

    static int Flyout(string name, int fallback) =>
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FlyoutMenuSettings", name, null) is int v ? v : fallback;

    /// <summary>The PC can sleep, and Power Options has not removed Sleep from the power menu.</summary>
    public static bool CanSleep => IsPwrSuspendAllowed() && Flyout("ShowSleepOption", 1) != 0;
    /// <summary>The PC can hibernate (offered whenever it can; Windows 10 needed it ticked in Power Options as well).</summary>
    public static bool CanHibernate => IsPwrHibernateAllowed();
    public static bool ShowLock => Flyout("ShowLockOption", 1) != 0;

    /// <summary>Windows has installed updates that finish on the next shutdown or restart.</summary>
    static bool UpdatesWaiting
    {
        get
        {
            if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\WindowsUpdate\Orchestrator", "ShutdownFlyoutOptions", null) is int f && f != 0) return true;
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            return k != null;
        }
    }

    /// <summary>Control Panel's "Turn on fast startup".</summary>
    static bool FastStartup =>
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", null) is int h && h != 0
        && IsPwrHibernateAllowed();

    /// <summary>The power menu, in Windows 10's order.</summary>
    public static List<Choice> PowerChoices()
    {
        var list = new List<Choice>();
        if (CanSleep) list.Add(new("Sleep", "_Sleep", "", Sleep));
        if (CanHibernate) list.Add(new("Hibernate", "_Hibernate", "", Hibernate));
        bool updates = UpdatesWaiting;
        if (updates) list.Add(new("Update and shut down", "Update and shut d_own", "", () => ShutDown(installUpdates: true)));
        list.Add(new("Shut down", "Sh_ut down", "", () => ShutDown(installUpdates: false)));
        if (updates) list.Add(new("Update and restart", "Update and r_estart", "", () => Restart(installUpdates: true)));
        list.Add(new("Restart", "_Restart", "", () => Restart(installUpdates: false)));
        return list;
    }

    // ---------- doing it ----------

    public static void Sleep() => SetSuspendState(false, false, false);
    public static void Hibernate() => SetSuspendState(true, false, false);

    public static void ShutDown(bool installUpdates) =>
        Shutdown(SHUTDOWN_POWEROFF | (installUpdates ? SHUTDOWN_INSTALL_UPDATES : 0) | (FastStartup ? SHUTDOWN_HYBRID : 0),
                 FastStartup ? new[] { "/s", "/hybrid", "/t", "0" } : new[] { "/s", "/t", "0" });

    public static void Restart(bool installUpdates) =>
        Shutdown(SHUTDOWN_RESTART | (installUpdates ? SHUTDOWN_INSTALL_UPDATES : 0), new[] { "/r", "/t", "0" });

    public static void SignOut() => ExitWindowsEx(0 /* EWX_LOGOFF */, REASON_PLANNED);
    public static void Lock() => LockWorkStation();

    /// <summary>To the sign-in screen with this session kept running in the background (fast user switching).</summary>
    public static void SwitchUser() => WTSDisconnectSession(0 /* this server */, -1 /* this session */, false);

    static void Shutdown(uint flags, string[] fallbackArgs)
    {
        EnableShutdownPrivilege();
        if (InitiateShutdown(null, null, 0, flags, REASON_PLANNED) == 0) return;
        try { Launcher.Helper(Launcher.Sys("shutdown.exe"), fallbackArgs); }
        catch (Exception ex) { CrashLog.Write("shutdown", ex); }
    }

    /// <summary>Shutting down needs the shutdown privilege switched on in our own token (every user holds it; it starts off).</summary>
    static void EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 /* ADJUST_PRIVILEGES */ | 0x8 /* QUERY */, out var token)) return;
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out long luid)) return;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
            AdjustTokenPrivileges(token, false, ref tp, 0, 0, 0);
        }
        finally { Win.CloseHandle(token); }
    }

    // ---------- who is signed in ----------

    static string? MySid => WindowsIdentity.GetCurrent().User?.Value;

    /// <summary>The signed-in user's display name (the account's full name), as the Windows 10 Start menu shows it.</summary>
    public static string DisplayName
    {
        get
        {
            var sb = new StringBuilder(256); uint n = (uint)sb.Capacity;
            if (GetUserNameEx(3 /* NameDisplay */, sb, ref n) && sb.Length > 0) return sb.ToString();
            if (NetUserGetInfo(null, Environment.UserName, 10, out var buf) == 0 && buf != 0)
            {
                try { if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, 3 * IntPtr.Size)) is { Length: > 0 } full) return full; }   // usri10_full_name
                finally { NetApiBufferFree(buf); }
            }
            return Environment.UserName;
        }
    }

    /// <summary>The account picture Windows keeps for <paramref name="sid"/> (null when the account has none).</summary>
    public static ImageSource? PictureOf(string? sid)
    {
        if (sid == null) return null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid);
            foreach (var size in new[] { "Image96", "Image64", "Image48", "Image40", "Image32" })
                if (k?.GetValue(size) is string path && File.Exists(path))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit(); bmp.UriSource = new Uri(path); bmp.DecodePixelWidth = 64; bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
        }
        catch { }
        return null;
    }

    public static ImageSource? MyPicture => PictureOf(MySid);

    /// <summary>The other accounts that have signed in to this PC (what Windows 10 lists under the user's own entries).</summary>
    public static List<(string Sid, string Name)> OtherUsers()
    {
        var list = new List<(string, string)>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (k == null) return list;
            var me = MySid;
            foreach (var sid in k.GetSubKeyNames())
            {
                if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || sid == me) continue;
                try
                {
                    var account = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
                    list.Add((sid, account[(account.LastIndexOf('\\') + 1)..]));
                }
                catch { }   // an account that no longer exists
            }
        }
        catch { }
        return list.OrderBy(u => u.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
