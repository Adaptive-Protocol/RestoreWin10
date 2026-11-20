using System.IO;
using System.Runtime.InteropServices;

namespace RestoreWin10;

/// <summary>
/// Notes in crash.log each file from outside Windows and .NET that is mapped into RestoreWin10 (a DLL, or a file mapped as
/// data), the first time it is seen: a minute after start for what loaded with its windows, then within a minute of
/// anything new (it runs with RunRecord's heartbeat). Office updates ended RestoreWin10 three times, and Firefox with it
/// once, while other programs stayed; this shows what it was holding and since when. It only reads this process's own address space.
/// </summary>
static class ModuleWatch
{
    static readonly HashSet<string> Seen = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, string>? _drives;

    public static void Check(bool firstMinute = false)
    {
        try
        {
            var found = new List<string>();
            foreach (var (path, image) in Mapped())
                if (!Ignored(path) && Seen.Add(path)) found.Add(image ? path : path + " (as data)");
            if (found.Count == 0) return;
            CrashLog.Note((firstMinute ? "files from outside Windows and .NET mapped into RestoreWin10 in its first minute: "
                                       : "newly mapped into RestoreWin10: ") + string.Join("; ", found));
        }
        catch { }
    }

    /// <summary>
    /// Windows itself, the .NET runtime, RestoreWin10's own folder and Windows' shell caches (the icon and thumbnail
    /// caches every program that shows file icons maps) are expected; everything else is noted. A mapped file that was
    /// deleted or replaced since moves to NTFS's hidden "\$Extend\$Deleted\" under a number: it was noted (or ignored)
    /// under its own path when it appeared, and the number names nothing, so it is left out.
    /// </summary>
    internal static bool Ignored(string path)
    {
        static bool Under(string path, string dir) =>
            dir.Length > 0 && path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        return path.Contains(@"\$Extend\$Deleted\", StringComparison.OrdinalIgnoreCase) ||
               Under(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ||
               Under(path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")) ||
               Under(path, AppContext.BaseDirectory) ||
               Under(path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Explorer")) ||
               Under(path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Caches")) ||
               Under(path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Caches"));
    }

    /// <summary>Every file mapped into this process (images and data), with drive-letter paths.</summary>
    internal static List<(string path, bool image)> Mapped()
    {
        var result = new List<(string, bool)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new char[1024];
        nint self = GetCurrentProcess(), addr = 0;
        while (VirtualQuery(addr, out var m, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != 0)
        {
            if (m.State == MEM_COMMIT && (m.Type == MEM_IMAGE || m.Type == MEM_MAPPED) && m.BaseAddress == m.AllocationBase)
            {
                int n = GetMappedFileNameW(self, m.BaseAddress, buffer, buffer.Length);
                if (n > 0 && ToDos(new string(buffer, 0, n)) is var p && names.Add(p)) result.Add((p, m.Type == MEM_IMAGE));
            }
            long next = (long)m.BaseAddress + (long)m.RegionSize;
            if (next <= (long)addr) break;
            addr = (nint)next;
        }
        return result;
    }

    /// <summary>"\Device\HarddiskVolume6\x" → "C:\x" (unchanged when no drive letter matches).</summary>
    static string ToDos(string device)
    {
        _drives ??= Environment.GetLogicalDrives().Select(d => d.TrimEnd('\\'))
            .Select(d => (d, target: QueryDosDevice(d, out var t) ? t : null))
            .Where(x => x.target != null).GroupBy(x => x.target!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().d, StringComparer.OrdinalIgnoreCase);
        foreach (var (target, letter) in _drives)
            if (device.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase)) return letter + device[target.Length..];
        return device;
    }

    static bool QueryDosDevice(string drive, out string target)
    {
        var buf = new char[512];
        int n = QueryDosDeviceW(drive, buf, buf.Length);
        target = n > 0 ? new string(buf, 0, Array.IndexOf(buf, '\0') is var z && z >= 0 ? z : n) : "";
        return target.Length > 0;
    }

    const uint MEM_COMMIT = 0x1000, MEM_IMAGE = 0x1000000, MEM_MAPPED = 0x40000;

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll")] static extern nint VirtualQuery(nint address, out MEMORY_BASIC_INFORMATION info, int length);
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)] static extern int GetMappedFileNameW(nint process, nint address, [Out] char[] name, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int QueryDosDeviceW(string device, [Out] char[] target, int size);
}
