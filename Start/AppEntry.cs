using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace RestoreWin10;

public sealed class AppEntry
{
    public string Name { get; init; } = "";
    /// <summary>What launches it: a .lnk, an executable, or a shell:AppsFolder id for Store apps.</summary>
    public string Path { get; init; } = "";
    /// <summary>For shortcuts, the executable they start (filled in after the scan); for executables, the path itself.</summary>
    public string? Target { get; internal set; }

    /// <summary>The Start Menu folder it is listed in (its first-level folder, as Windows 10 shows them), or null at the top level.</summary>
    public string? Folder { get; init; }

    /// <summary>Store / system app launched through its AppUserModelID rather than a shortcut file.</summary>
    public bool IsStore => Path.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase);
    public bool IsShortcut => Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
    /// <summary>The A–Z group it is listed under (see <see cref="AppList.KeyOf"/>).</summary>
    public string Letter => AppList.KeyOf(Name);

    /// <summary>The executable's bare name ("regedit"), so typing it finds the app whatever its shortcut is called.</summary>
    public string Alias => Target == null ? "" : System.IO.Path.GetFileNameWithoutExtension(Target);

    public bool Matches(string q) =>
        Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
        (Alias.Length > 0 && Alias.StartsWith(q, StringComparison.OrdinalIgnoreCase));

    /// <summary>0 = the name starts with the query, 1 = the executable name does, 2 = the name merely contains it.</summary>
    public int Rank(string q) =>
        Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 0 :
        Alias.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    // ---------- icons ----------

    static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new();
    public BitmapSource? Icon => Cache.GetOrAdd(Path, LoadIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, nint[] icons, uint[]? ids, uint n, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint h);

    static BitmapSource? FromHandle(nint h)
    {
        var bs = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        bs.Freeze();
        return bs;
    }

    /// <summary>A file's own icon (executables, .msc consoles, documents).</summary>
    static BitmapSource? FileIcon(string file, int index = 0)
    {
        var h = new nint[1];
        if (PrivateExtractIcons(file, index, 48, 48, h, null, 1, 0) > 0 && h[0] != 0)
        {
            try { return FromHandle(h[0]); } finally { DestroyIcon(h[0]); }
        }
        return IconHelper.AssociatedIcon(file);
    }

    /// <summary>Icon of the shortcut's target, without the shortcut-arrow overlay.</summary>
    static BitmapSource? LoadIcon(string path)
    {
        if (path.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase)) return StoreIcon(path);
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) { try { return FileIcon(path); } catch { return null; } }
        try
        {
            string? file = null; int idx = 0;
            if (Shortcut.Read(path) is { } sc)
            {
                if (sc.IconFile.Length > 0) { file = sc.IconFile; idx = sc.IconIndex; }
                else if (sc.Target.Length > 0) file = sc.Target;
            }
            if (file != null && File.Exists(file) && FileIcon(file, idx) is { } icon) return icon;
            return IconHelper.AssociatedIcon(path);
        }
        catch { return null; }
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(Sz size, int flags, out nint hbitmap); }
    [StructLayout(LayoutKind.Sequential)] struct Sz { public int cx, cy; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, nint bc, ref Guid riid, out IShellItemImageFactory factory);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint h);

    /// <summary>The app's own tile icon, as the shell would draw it.</summary>
    static BitmapSource? StoreIcon(string parsingName)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(parsingName, 0, ref iid, out var f) != 0) return null;
            if (f.GetImage(new Sz { cx = 48, cy = 48 }, 0x4 /* ICONONLY */, out var hbm) != 0 || hbm == 0) return null;
            try
            {
                var bs = Imaging.CreateBitmapSourceFromHBitmap(hbm, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                return bs;
            }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
    }

    // ---------- launching ----------

    public void Launch() => Launcher.Item(Path);

    /// <summary>The executable this entry starts, or null (Store apps, folders, control panel items...).</summary>
    public string? TargetExe()
    {
        if (IsStore) return null;
        if (IsShortcut && Target == null) ResolveTargets(new[] { this });
        var t = IsShortcut ? Target : Path;
        return t != null && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(t) ? t : null;
    }

    // ---------- scanning ----------

    /// <summary>The all-users and per-user Start Menu "Programs" folders, the two places desktop apps list themselves.</summary>
    public static string[] ProgramRoots => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
    };

    /// <summary>
    /// Desktop apps: every shortcut in the two Start Menu trees, as Windows 10 lists them. A shortcut in a subfolder belongs
    /// to its first-level folder (deeper levels are folded in), and a folder that exists in both trees is listed once. The
    /// Startup folder and uninstall shortcuts are left out; the same name in the same place is listed once. Fast.
    /// </summary>
    public static List<AppEntry> ScanShortcuts()
    {
        var startup = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        };
        var folderNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // folder path -> name shown
        var map = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ProgramRoots.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
            {
                var n = System.IO.Path.GetFileNameWithoutExtension(f);
                if (n.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                if (startup.Any(s => s.Length > 0 && f.StartsWith(s + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                // The first-level folder below the root, if any.
                var rel = f[(root.Length + 1)..];
                int slash = rel.IndexOf('\\');
                string? folder = null;
                if (slash > 0)
                {
                    var folderPath = System.IO.Path.Combine(root, rel[..slash]);
                    if (!folderNames.TryGetValue(folderPath, out folder))
                        folderNames[folderPath] = folder = DisplayName(folderPath) ?? rel[..slash];
                }
                map.TryAdd((folder ?? "") + "\0" + n, new AppEntry { Name = n, Path = f, Folder = folder });
            }
        return map.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public nint hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern nint SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    /// <summary>The name Explorer shows for a folder (a desktop.ini can give it a localized name), or null.</summary>
    static string? DisplayName(string path)
    {
        var info = new SHFILEINFO();
        return SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x200 /* SHGFI_DISPLAYNAME */) != 0 &&
               !string.IsNullOrWhiteSpace(info.szDisplayName) ? info.szDisplayName : null;
    }

    /// <summary>Fills in <see cref="Target"/> for every shortcut in one pass (COM, so on its own STA thread).</summary>
    public static void ResolveTargets(IEnumerable<AppEntry> apps)
    {
        var todo = apps.Where(a => a.IsShortcut && a.Target == null).ToList();
        if (todo.Count == 0) return;
        var t = new Thread(() =>
        {
            foreach (var a in todo)
                if (Shortcut.Read(a.Path) is { Target.Length: > 0 } sc) a.Target = sc.Target;
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
    }

    /// <summary>Store apps via the shell's Apps folder (keeps only AUMID-style entries; desktop apps come from shortcuts).</summary>
    public static List<AppEntry> ScanStoreApps()
    {
        var list = new List<AppEntry>();
        var t = new Thread(() =>
        {
            foreach (var (id, name) in Shortcut.AppsFolder())
                if (id.Contains('!') && !id.Contains('\\') && name.Length > 0)
                    list.Add(new AppEntry { Name = name, Path = "shell:AppsFolder\\" + id });
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        return list;
    }

    /// <summary>Everything: shortcuts plus Store apps (the Store enumeration can take several seconds).</summary>
    public static List<AppEntry> Scan() => Merge(ScanShortcuts(), ScanStoreApps());

    /// <summary>Both lists, the first winning when an app of the same name is listed in the same place (folder) in both.</summary>
    public static List<AppEntry> Merge(List<AppEntry> first, List<AppEntry> second)
    {
        var map = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in first) map.TryAdd((a.Folder ?? "") + "\0" + a.Name, a);
        foreach (var a in second) map.TryAdd((a.Folder ?? "") + "\0" + a.Name, a);
        return map.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>A readable stand-in name for a Store app we could not resolve, e.g. "WindowsNotepad".</summary>
    public static string StoreDisplayName(string parsingName)
    {
        var id = System.IO.Path.GetFileName(parsingName);   // text after the last separator
        int bang = id.IndexOf('!'); if (bang > 0) id = id[..bang];
        int us = id.LastIndexOf('_'); if (us > 0) id = id[..us];
        int dot = id.LastIndexOf('.'); if (dot >= 0) id = id[(dot + 1)..];
        return id.Length > 0 ? id : parsingName;
    }

    /// <summary>An entry for a plain file: named by its version-info description when it has one ("Registry Editor").</summary>
    public static AppEntry ForFile(string path)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        try
        {
            var d = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(d)) name = d.Trim();
            else if (name.Length > 0 && char.IsLower(name[0])) name = char.ToUpper(name[0]) + name[1..];
        }
        catch { }
        return new AppEntry { Name = name, Path = path, Target = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null };
    }

    // ---------- "Run box" fallback ----------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint SearchPath(string? path, string fileName, string? extension, uint bufferLength, System.Text.StringBuilder buffer, nint filePart);

    static readonly Dictionary<string, AppEntry?> CommandCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What the Run box would start for <paramref name="typed"/> ("regedit", "msconfig", "services.msc"): looked up
    /// on the PATH and under the registry's App Paths. Null when the text is not a runnable name.
    /// </summary>
    public static AppEntry? FromCommand(string typed)
    {
        typed = typed.Trim();
        if (typed.Length < 2 || typed.Any(char.IsWhiteSpace) || typed.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return null;
        lock (CommandCache)
        {
            if (CommandCache.TryGetValue(typed, out var known)) return known;
            AppEntry? entry = null;
            try
            {
                string? full = null;
                var sb = new System.Text.StringBuilder(1024);
                bool hasExt = System.IO.Path.HasExtension(typed);
                if (SearchPath(LocalSearchPath(), typed, hasExt ? null : ".exe", (uint)sb.Capacity, sb, 0) > 0) full = sb.ToString();
                full ??= AppPathsLookup(typed);
                // Only something RestoreWin10 would start anyway, on this PC: a lookup never reaches a network share.
                if (full != null && Launcher.IsOnThisPc(full) && Launcher.IsLaunchable(full) && File.Exists(full)) entry = ForFile(full);
            }
            catch { }
            CommandCache[typed] = entry;
            return entry;
        }
    }

    /// <summary>
    /// The folders a typed command is looked for in: the PATH's folders that are on this PC, with Windows' own first. (Not
    /// the current or program folder, and never a network folder: as the user types, each keystroke is looked up, and a
    /// network folder on the PATH would be contacted every time. Threat model F-10.)
    /// </summary>
    static string LocalSearchPath()
    {
        var dirs = new List<string> { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) };
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var dir = Environment.ExpandEnvironmentVariables(d.Trim().Trim('"'));
            if (Launcher.IsOnThisPc(dir) && !dirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) dirs.Add(dir);
        }
        return string.Join(";", dirs);
    }

    static string? AppPathsLookup(string typed)
    {
        var key = typed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? typed : typed + ".exe";
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + key);
            if (k?.GetValue("") is string v && v.Length > 0) return Environment.ExpandEnvironmentVariables(v.Trim().Trim('"'));
        }
        return null;
    }
}
