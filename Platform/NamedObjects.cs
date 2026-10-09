using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RestoreWin10;

/// <summary>
/// The single-instance lock and the Toggle / Exit signals (threat model F-07).
/// <list type="bullet">
/// <item>They are created with a security descriptor that lets only this Windows account use them.</item>
/// <item>An existing one is trusted only if this account (or an elevated copy of it) made it, at medium integrity or
/// above, so another account or a sandboxed program cannot hold the lock or send the signals.</item>
/// </list>
/// </summary>
static class NamedObjects
{
    /// <summary>An auto-reset event that can be waited on with the other wait handles.</summary>
    public sealed class Signal : WaitHandle
    {
        internal Signal(SafeWaitHandle h) => SafeWaitHandle = h;
        public void Set() => SetEvent(SafeWaitHandle);
    }

    /// <summary>
    /// Takes the single-instance lock. <paramref name="first"/>: no other copy holds it. <paramref name="trusted"/>: false
    /// when the existing lock was made by someone else, or could not be opened; the caller should then not start.
    /// </summary>
    public static SafeWaitHandle? Lock(string name, out bool first, out bool trusted)
    {
        var h = Create(name, mutex: true, out bool existed);
        first = h != null && !existed;
        trusted = h != null && (!existed || Trusted(h));
        return h;
    }

    /// <summary>Creates (or, when it is trustworthy, joins) a signal. Null when it exists but cannot be trusted.</summary>
    public static Signal? CreateSignal(string name)
    {
        var h = Create(name, mutex: false, out bool existed);
        if (h == null) return null;
        if (existed && !Trusted(h)) { h.Dispose(); return null; }
        return new Signal(h);
    }

    /// <summary>Sets the signal another copy created. False if there is none, or it cannot be trusted.</summary>
    public static bool SetExisting(string name)
    {
        using var h = OpenEventW(EVENT_MODIFY_STATE | READ_CONTROL, false, name);
        if (h.IsInvalid || !Trusted(h)) return false;
        return SetEvent(h);
    }

    // ---------- creating ----------

    static SafeWaitHandle? Create(string name, bool mutex, out bool existed)
    {
        existed = false;
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        // Protected DACL: full access for this account, nobody else (no inherited entries).
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GA;;;{sid})", 1, out nint sd, out _))
            throw new Win32Exception();
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
            var h = mutex ? CreateMutexExW(ref sa, name, CREATE_MUTEX_INITIAL_OWNER, MUTEX_ALL_ACCESS)
                          : CreateEventExW(ref sa, name, 0 /* auto-reset, not signalled */, EVENT_ALL_ACCESS);
            int error = Marshal.GetLastWin32Error();
            if (h.IsInvalid) { CrashLog.Note($"could not create or open {name} (error {error})"); h.Dispose(); return null; }
            existed = error == ERROR_ALREADY_EXISTS;
            return h;
        }
        finally { LocalFree(sd); }
    }

    /// <summary>Made by this account (or by it elevated, which makes Administrators the owner), at medium integrity or above.</summary>
    static bool Trusted(SafeHandle h)
    {
        if (GetSecurityInfo(h, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION,
                            out nint owner, out _, out _, out nint sacl, out nint sd) != 0) return false;
        try
        {
            var ownerSid = new SecurityIdentifier(owner);
            if (ownerSid != WindowsIdentity.GetCurrent().User && ownerSid.Value != "S-1-5-32-544") return false;
            // The mandatory label, if any: SYSTEM_MANDATORY_LABEL_ACE = header (4 bytes), mask (4), then the label's SID.
            if (sacl != 0 && GetAce(sacl, 0, out nint ace) && Marshal.ReadByte(ace) == SYSTEM_MANDATORY_LABEL_ACE_TYPE)
            {
                nint labelSid = ace + 8;
                int count = Marshal.ReadByte(GetSidSubAuthorityCount(labelSid));
                int level = Marshal.ReadInt32(GetSidSubAuthority(labelSid, (uint)(count - 1)));
                if (level < 0x2000) return false;   // below medium integrity (a sandboxed program)
            }
            return true;
        }
        catch { return false; }
        finally { LocalFree(sd); }
    }

    // ---------- Win32 ----------

    const uint CREATE_MUTEX_INITIAL_OWNER = 1;
    const uint MUTEX_ALL_ACCESS = 0x1F0001, EVENT_ALL_ACCESS = 0x1F0003, EVENT_MODIFY_STATE = 0x2, READ_CONTROL = 0x20000;
    const int ERROR_ALREADY_EXISTS = 183, SE_KERNEL_OBJECT = 6;
    const uint OWNER_SECURITY_INFORMATION = 0x1, LABEL_SECURITY_INFORMATION = 0x10;
    const byte SYSTEM_MANDATORY_LABEL_ACE_TYPE = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int nLength; public nint lpSecurityDescriptor; public int bInheritHandle; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeWaitHandle CreateMutexExW(ref SECURITY_ATTRIBUTES sa, string name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeWaitHandle CreateEventExW(ref SECURITY_ATTRIBUTES sa, string name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeWaitHandle OpenEventW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetEvent(SafeWaitHandle h);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint h);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint sd, out uint size);
    [DllImport("advapi32.dll")]
    static extern uint GetSecurityInfo(SafeHandle h, int type, uint info, out nint owner, out nint group, out nint dacl, out nint sacl, out nint sd);
    [DllImport("advapi32.dll")] static extern bool GetAce(nint acl, int index, out nint ace);
    [DllImport("advapi32.dll")] static extern nint GetSidSubAuthority(nint sid, uint index);
    [DllImport("advapi32.dll")] static extern nint GetSidSubAuthorityCount(nint sid);
}
