using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace RestoreWin10;

/// <summary>Pinned tile + usage persistence (%APPDATA%\RestoreWin10\state.json).</summary>
public sealed class State
{
    /// <summary>A tile: an app (Path), or a folder (Items: its tiles; Name: the folder's name; Path unused).</summary>
    public record TileRef(string Path, string Size, List<TileRef>? Items = null, string? Name = null);

    public record GroupRef(string Name, List<TileRef> Tiles);

    public List<GroupRef> Groups { get; set; } = new();
    public List<TileRef> Tiles { get; set; } = new();   // legacy single-list format, migrated into Groups on load
    public Dictionary<string, int> Usage { get; set; } = new();
    /// <summary>When each app was first seen, for "Recently added" (DateTime.MinValue: an old app, or one already started).</summary>
    public Dictionary<string, DateTime> FirstSeen { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Seeded { get; set; }

    /// <summary>Programs pinned to the taskbar, by executable path.</summary>
    public List<string> TaskbarPins { get; set; } = new();
    public bool TaskbarSeeded { get; set; }
    /// <summary>For pins made from shortcuts: exe -> the shortcut to launch it with (keeps arguments, advertised targets).</summary>
    public Dictionary<string, string> TaskbarPinLaunch { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The Windows taskbar's pins have been carried over once (it can be redone from the tray menu).</summary>
    public bool WindowsPinsImported { get; set; }

    /// <summary>Pins <paramref name="exe"/> at the end of the taskbar, launched through <paramref name="shortcut"/> when given.</summary>
    public void PinToTaskbar(string exe, string? shortcut)
    {
        TaskbarPins.RemoveAll(p => p.Equals(exe, StringComparison.OrdinalIgnoreCase));
        TaskbarPins.Add(exe);
        if (shortcut != null) TaskbarPinLaunch[exe] = shortcut; else TaskbarPinLaunch.Remove(exe);
    }

    public void UnpinFromTaskbar(string exe)
    {
        TaskbarPins.RemoveAll(p => p.Equals(exe, StringComparison.OrdinalIgnoreCase));
        TaskbarPinLaunch.Remove(exe);
    }

    /// <summary>Programs (exe paths) whose notification-area icons are shown on the taskbar instead of the "^" flyout.</summary>
    public List<string> TrayPinned { get; set; } = new();

    /// <summary>Start menu height as a share of the screen's work area (0 = the default). A share, not pixels, so the
    /// menu keeps its proportions on any screen, resolution or scaling.</summary>
    public double StartHeight { get; set; }
    /// <summary>Taskbar search: 0 hidden, 1 search icon (default), 2 search box. As in the taskbar menu's Search entry.</summary>
    public int SearchMode { get; set; } = 1;
    public bool ShowTaskView { get; set; } = true;
    /// <summary>The two choices above were copied into Windows' own settings, which are followed from then on.</summary>
    public bool TaskbarOptionsMigrated { get; set; }
    /// <summary>The Windows accent colour (RRGGBB) the user asked to see as Windows 10 blue instead. Tiles stay blue while
    /// the accent is still this colour; once it is changed in Settings, the accent is followed and this is cleared.</summary>
    public string? KeepBlueWhileAccent { get; set; }
    /// <summary>Explorer's taskbar setting (auto-hide, always on top) from before RestoreWin10 replaced the taskbar; set while
    /// replacing, cleared once it is put back. Still set at startup means the last run ended abruptly (threat model F-12).</summary>
    public int? ExplorerTaskbarState { get; set; }
    /// <summary>The taskbar's style from its menu: "clear", "acrylic", "opaque" or "normal" (follows Windows). Null: clear.</summary>
    public string? TaskbarStyle { get; set; }

    static readonly string Dir = Identity.DataDir;
    static readonly string File_ = Identity.DataFile("state.json");

    // Loaded on first use, not in a static initializer: those run in textual order, and an initializer
    // that ran before Dir/File_ existed would silently load an empty state and later overwrite the file.
    static State? _shared;

    /// <summary>The one live copy shared by the Start menu and the taskbar.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public static State Shared => _shared ??= Load();

    /// <summary>Far above any real state.json (tens of KB): a bigger file is not read at all, but kept as state.json.bad.</summary>
    const long MaxFileBytes = 4 * 1024 * 1024;

    public static State Load() => LoadFrom(File_);

    /// <summary>Reads a settings file (the real one, or a test copy), keeping a file it cannot use as *.bad.</summary>
    internal static State LoadFrom(string file)
    {
        if (!File.Exists(file)) return new State();
        try
        {
            if (new FileInfo(file).Length > MaxFileBytes) throw new InvalidDataException("state.json is too large to be RestoreWin10's");
            var s = Parse(File.ReadAllText(file), out var rejected);
            if (rejected.Count > 0)
            {
                // Keep the file as it was, and say what was left out, before the next save rewrites it.
                try { File.Copy(file, file + ".rejected", overwrite: true); } catch { }
                CrashLog.Note($"state.json: {rejected.Count} entries left out (kept in state.json.rejected): " +
                              string.Join(" | ", rejected.Take(20).Select(r => r.Length > 200 ? r[..200] + "…" : r)));
            }
            return s;
        }
        catch (Exception ex)
        {
            // Never let a read failure masquerade as "first run": keep the file as evidence and start blank.
            try { File.Copy(file, file + ".bad", overwrite: true); } catch { }
            CrashLog.Note($"{Path.GetFileName(file)} could not be used ({ex.Message}); it is kept as {Path.GetFileName(file)}.bad and RestoreWin10 started blank");
            return new State();
        }
    }

    /// <summary>state.json's text, with anything unsafe left out; <paramref name="rejected"/> lists what was left out.</summary>
    internal static State Parse(string json, out List<string> rejected)
    {
        var s = JsonSerializer.Deserialize<State>(json) ?? new State();
        rejected = s.Sanitize();
        return s;
    }

    /// <summary>
    /// state.json is effectively a list of things to start (threat model F-08), so every path in it must be what
    /// RestoreWin10 itself would have put there (see <see cref="Launcher.IsLaunchable"/>):
    /// <list type="bullet">
    /// <item>tiles and usage counts: Store app ids, or local shortcuts, programs, consoles and Control Panel items;</item>
    /// <item>taskbar and tray pins: local programs;</item>
    /// <item>pin shortcuts: local .lnk files;</item>
    /// <item>numbers are kept in range.</item>
    /// </list>
    /// Anything else is left out. Returns what was left out.
    /// </summary>
    List<string> Sanitize()
    {
        var rejected = new List<string>();
        bool Ok(string? value, Func<string?, bool> test) { if (test(value)) return true; rejected.Add(value ?? "(empty)"); return false; }
        static bool Program(string? p) => Launcher.IsLocalPath(p) && p!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        static bool Shortcut(string? p) => Launcher.IsLocalPath(p) && p!.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
        static string Size(string? s) => s is "S" or "M" or "W" or "L" ? s : "M";

        // A folder keeps the app tiles in it that pass (folders do not nest). With one left it is that tile again; with
        // none it goes.
        TileRef? Checked(TileRef? t)
        {
            if (t == null) return null;
            if (t.Items == null) return Ok(t.Path, Launcher.IsLaunchable) ? t with { Size = Size(t.Size), Name = null } : null;
            var items = t.Items.Where(c => c != null && (c.Items == null || Ok("(folder inside a folder)", _ => false)) && Ok(c.Path, Launcher.IsLaunchable))
                               .Select(c => c with { Size = Size(c.Size), Name = null }).ToList();
            var name = t.Name ?? "";
            return items.Count switch
            {
                0 => null,
                1 => items[0],
                _ => new TileRef("", Size(t.Size), items, name.Length > 100 ? name[..100] : name),
            };
        }
        Groups = (Groups ?? new()).Where(g => g != null).Select(g => new GroupRef(
                     (g.Name ?? "").Length > 100 ? g.Name![..100] : g.Name ?? "",
                     (g.Tiles ?? new()).Select(Checked).Where(t => t != null).Select(t => t!).ToList()))
                 .ToList();
        Tiles = (Tiles ?? new()).Where(t => t != null && Ok(t.Path, Launcher.IsLaunchable)).Select(t => t with { Size = Size(t.Size) }).ToList();
        Usage = (Usage ?? new()).Where(kv => Ok(kv.Key, Launcher.IsLaunchable) && kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        FirstSeen = new Dictionary<string, DateTime>(
            (FirstSeen ?? new()).Where(kv => Ok(kv.Key, Launcher.IsLaunchable)),
            StringComparer.OrdinalIgnoreCase);
        TaskbarPins = (TaskbarPins ?? new()).Where(p => Ok(p, Program)).ToList();
        TaskbarPinLaunch = new Dictionary<string, string>(
            (TaskbarPinLaunch ?? new()).Where(kv => Ok(kv.Key, Program) && Ok(kv.Value, Shortcut)),
            StringComparer.OrdinalIgnoreCase);   // paths compare case-insensitively
        TrayPinned = (TrayPinned ?? new()).Where(p => Ok(p, Program)).ToList();

        if (double.IsNaN(StartHeight) || StartHeight < 0.25 || StartHeight > 1) StartHeight = 0;   // 0 = the default height
        SearchMode = Math.Clamp(SearchMode, 0, 2);
        if (KeepBlueWhileAccent is { } k && !(k.Length == 6 && k.All(Uri.IsHexDigit))) KeepBlueWhileAccent = null;
        if (ExplorerTaskbarState is < 0 or > 3) ExplorerTaskbarState = null;   // ABS_AUTOHIDE | ABS_ALWAYSONTOP
        if (TaskbarStyle is not (null or "clear" or "acrylic" or "opaque" or "normal")) TaskbarStyle = null;
        return rejected;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(File_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { CrashLog.Write("saving state.json", ex); }   // never silently: a lost save loses pins and tiles
    }
}

public static class Theme
{
    /// <summary>Classic Windows 10 default accent.</summary>
    public static readonly Color Win10Blue = Color.FromRgb(0x00, 0x78, 0xD7);

    /// <summary>The user's Windows accent colour (DWM AccentColor, stored as ABGR).</summary>
    public static Color Accent()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int v)
                return Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
        }
        catch { }
        return Color.FromRgb(0x00, 0x78, 0xD7);
    }
}

public static class Acrylic
{
    [StructLayout(LayoutKind.Sequential)]
    struct AccentPolicy { public int State, Flags, GradientColor, AnimationId; }
    [StructLayout(LayoutKind.Sequential)]
    struct WinCompAttrData { public int Attribute; public nint Data; public int Size; }
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(nint hwnd, ref WinCompAttrData d);

    /// <summary>Blur-behind with a tint. tintAbgr = 0xAABBGGRR. Returns false if unsupported.</summary>
    public static bool Apply(Window w, int tintAbgr) => Set(w, 4 /* ACCENT_ENABLE_ACRYLICBLURBEHIND */, tintAbgr);

    /// <summary>No blur: what is behind shows through as it is, under a tint (alpha 0: untouched). Returns false if unsupported.</summary>
    public static bool Clear(Window w, int tintAbgr) => Set(w, 2 /* ACCENT_ENABLE_TRANSPARENTGRADIENT */, tintAbgr);

    /// <summary>No blur at all (transparency effects off): the window paints its own solid background.</summary>
    public static void Disable(Window w)
    {
        Set(w, 0 /* ACCENT_DISABLED */, 0);
        if (HwndSource.FromHwnd(new WindowInteropHelper(w).EnsureHandle())?.CompositionTarget is { } ct) ct.BackgroundColor = Colors.Black;
    }

    static bool Set(Window w, int state, int tintAbgr)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            var src = HwndSource.FromHwnd(hwnd);
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;

            var policy = new AccentPolicy { State = state, Flags = state == 0 ? 0 : 2, GradientColor = tintAbgr };
            int size = Marshal.SizeOf(policy);
            nint ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WinCompAttrData { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = ptr, Size = size };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { return false; }
    }
}

public static class IconHelper
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, nint[] icons, uint[]? ids, uint n, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint h);

    /// <summary>The first icon of an exe/dll/ico, 48px, frozen for use from any thread.</summary>
    public static System.Windows.Media.Imaging.BitmapSource? FromFile(string file) => FromFile(file, 0);

    /// <summary>The icon at <paramref name="index"/> (negative: by resource id) of an exe/dll/ico, 48px, frozen.</summary>
    public static System.Windows.Media.Imaging.BitmapSource? FromFile(string file, int index)
    {
        try
        {
            var h = new nint[1];
            if (PrivateExtractIcons(file, index, 48, 48, h, null, 1, 0) > 0 && h[0] != 0)
            {
                try { return Convert(h[0]); } finally { DestroyIcon(h[0]); }
            }
            return AssociatedIcon(file);
        }
        catch { return null; }
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

    /// <summary>The icon the shell itself would show for a file or shortcut (large size).</summary>
    /// <summary>
    /// The icon for <paramref name="path"/>'s type (a folder when it has no extension), worked out from its name alone:
    /// the file is never opened, so a network path is not contacted (threat model F-09).
    /// </summary>
    public static System.Windows.Media.Imaging.BitmapSource? TypeIcon(string path)
    {
        var info = new SHFILEINFO();
        uint attrs = System.IO.Path.HasExtension(path) ? 0x80u /* NORMAL */ : 0x10u /* DIRECTORY */;
        try
        {
            if (SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 | 0x10 /* SHGFI_ICON | SHGFI_USEFILEATTRIBUTES */) == 0 || info.hIcon == 0) return null;
            return Convert(info.hIcon);
        }
        catch { return null; }
        finally { if (info.hIcon != 0) DestroyIcon(info.hIcon); }
    }

    /// <summary>The name File Explorer shows for a local folder or file ("Documents", "Local Disk (C:)"), or null.</summary>
    public static string? DisplayName(string path)
    {
        var info = new SHFILEINFO();
        try { return SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x200 /* SHGFI_DISPLAYNAME */) != 0 && info.szDisplayName.Length > 0 ? info.szDisplayName : null; }
        catch { return null; }
    }

    public static System.Windows.Media.Imaging.BitmapSource? AssociatedIcon(string path)
    {
        var info = new SHFILEINFO();
        try
        {
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 /* SHGFI_ICON */) == 0 || info.hIcon == 0) return null;
            return Convert(info.hIcon);
        }
        catch { return null; }
        finally { if (info.hIcon != 0) DestroyIcon(info.hIcon); }
    }

    static System.Windows.Media.Imaging.BitmapSource Convert(nint hIcon)
    {
        var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty,
                     System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        bs.Freeze();
        return bs;
    }
}
