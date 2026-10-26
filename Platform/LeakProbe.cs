#if DEV_BUILD
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RestoreWin10;

/// <summary>
/// Development only (--leakprobe): runs the steps File Explorer's jump list takes, one at a time, and counts the registry
/// handles to Folder\shell\open this process holds after each, to find which step leaves the shell's folder verbs open.
/// Starts nothing and shows nothing; writes leakprobe.txt next to the self-test's file.
/// </summary>
static class LeakProbe
{
    public static int Run()
    {
        var sb = new StringBuilder();
        void Step(string what, Action a, int times = 3)
        {
            int before = FolderOpenHandles();
            for (int i = 0; i < times; i++) a();
            sb.AppendLine($"{FolderOpenHandles() - before,3}  {what} (x{times})");
        }
        var explorer = Launcher.Explorer;
        var ids = new[] { "f01b4d95cf55d32a", JumpLists.AppId(explorer) };
        var recent = JumpLists.Recent(ids, 15);
        sb.AppendLine($"File Explorer jump list: {recent.Count} entries; Folder\\shell\\open handles at start: {FolderOpenHandles()}");

        var held = new List<Microsoft.Win32.RegistryKey>();
        Step("control: open Folder\\shell\\open and keep it (must show 1)",
             () => held.Add(Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"Folder\shell\open")!), 1);
        foreach (var k in held) k.Dispose();
        var windows = new List<nint>();
        Win.EnumWindows((h, _) => { var c = new StringBuilder(64); Win.GetClassName(h, c, 64); if (c.ToString() == "CabinetWClass") windows.Add(h); return true; }, 0);
        sb.AppendLine($"open File Explorer windows: {windows.Count}");
        foreach (var h in windows.Take(1)) Step("PropStore.WindowAumid(File Explorer window)", () => PropStore.WindowAumid(h));
        var pinLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                  @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\File Explorer.lnk");
        if (File.Exists(pinLnk)) Step("PropStore.ShortcutAumid(File Explorer.lnk)", () => PropStore.ShortcutAumid(pinLnk));
        else sb.AppendLine("no File Explorer.lnk pin");
        var startLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "File Explorer.lnk");
        foreach (var l in new[] { pinLnk, startLnk }.Where(File.Exists))
        {
            Step($"Shortcut.Read({l})", () => Shortcut.Read(l));
            sb.AppendLine($"     target '{Shortcut.Read(l)?.Target}' -> started as {Launcher.ShellFolderProgram(l) ?? "(the shortcut, through the shell)"}");
        }
        Step("JumpLists.Recent", () => JumpLists.Recent(ids, 15));
        Step("JumpTasks.For", () => JumpTasks.For(ids, explorer));
        Step("IconHelper.FromFile(explorer.exe)", () => IconHelper.FromFile(explorer));
        int n = 0;
        foreach (var r in recent)
        {
            n++;
            string kind = Path.GetPathRoot(r.Path) == r.Path ? "drive root" : Directory.Exists(r.Path) ? "folder" : "file";
            Step($"entry {n} ({kind}): icon", () => { _ = Launcher.IsOnThisPc(r.Path) ? IconHelper.AssociatedIcon(r.Path) : IconHelper.TypeIcon(r.Path); }, 1);
            Step($"entry {n} ({kind}): display name", () => IconHelper.DisplayName(r.Path), 1);
        }
        sb.AppendLine($"Folder\\shell\\open handles at end: {FolderOpenHandles()}");
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RestoreWin10.Dev");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "leakprobe.txt"), sb.ToString());
        return 0;
    }

    // This process's own handle table: Key handles whose name ends in \Folder\shell\open. Only Key handles are asked
    // their name (a name query on some other kinds of handle can block).
    static int FolderOpenHandles()
    {
        int count = 0, size = 1 << 16;
        nint buf;
        for (;;)
        {
            buf = Marshal.AllocHGlobal(size);
            int s = NtQueryInformationProcess(-1, 51, buf, size, out int need);
            if (s == unchecked((int)0xC0000004)) { Marshal.FreeHGlobal(buf); size = Math.Max(size * 2, need); continue; }
            if (s != 0) { Marshal.FreeHGlobal(buf); return -1; }
            break;
        }
        try
        {
            long n = Marshal.ReadInt64(buf);
            var info = Marshal.AllocHGlobal(4096);
            try
            {
                for (long i = 0; i < n; i++)
                {
                    nint h = Marshal.ReadIntPtr(buf, 16 + (int)i * 40);
                    if (NtQueryObject(h, 2, info, 4096, out _) != 0 || Str(info) != "Key") continue;
                    if (NtQueryObject(h, 1, info, 4096, out _) == 0 && Str(info).EndsWith(@"\Folder\shell\open", StringComparison.OrdinalIgnoreCase)) count++;
                }
            }
            finally { Marshal.FreeHGlobal(info); }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return count;
    }

    static string Str(nint unicodeString)
    {
        int len = (ushort)Marshal.ReadInt16(unicodeString);
        nint p = Marshal.ReadIntPtr(unicodeString, 8);
        return len == 0 || p == 0 ? "" : Marshal.PtrToStringUni(p, len / 2);
    }

    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(nint process, int cls, nint buf, int size, out int need);
    [DllImport("ntdll.dll")] static extern int NtQueryObject(nint h, int cls, nint buf, int size, out int need);
}
#endif
