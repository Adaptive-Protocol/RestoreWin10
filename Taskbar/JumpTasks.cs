using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace RestoreWin10;

/// <summary>
/// A program's own jump-list tasks (Firefox: "Open new window", "Open new private window"), read from the file Windows
/// keeps for it: %APPDATA%\Microsoft\Windows\Recent\CustomDestinations\{AppID}.customDestinations-ms.
/// <para>
/// Layout (checked against real files): version 2, the number of categories, a reserved word; then each category:
/// its type (0 named, 1 known, 2 tasks), for named and task categories their entries, and the footer 0xBABFFBAB. An entry
/// is a 16-byte class id followed by that object's own data. Only shortcuts (CLSID_ShellLink) are read, by Windows' own
/// shortcut loader, which also tells where each one ends (the size inside a shortcut is not reliable).
/// </para>
/// <para>
/// Any program running as the user can write these files, so they are treated as untrusted (threat model F-09): files
/// over 1 MB and over 64 entries per category are refused, a task is offered only when it starts a program on this PC,
/// and it is started through <see cref="Launcher.Task"/> with the program's own command line, nothing else.
/// </para>
/// </summary>
public static class JumpTasks
{
    /// <summary>A task (its title, the program and command line it runs, its icon) or, with no program, a separator.</summary>
    public sealed record Entry(string Category, string Title, string Exe, string Arguments, string? IconFile, int IconIndex)
    {
        public bool IsSeparator => Exe.Length == 0;
    }

    const long MaxFile = 1024 * 1024;
    const int MaxEntries = 64, MaxCategories = 16;
    const uint Footer = 0xBABFFBAB;
    static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent\CustomDestinations");

    static readonly PROPERTYKEY Title = new() { fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), pid = 2 };
    static readonly PROPERTYKEY IsSeparator = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 6 };

    /// <summary>
    /// The tasks of the first of <paramref name="appIds"/> that has any, for the program <paramref name="programExe"/>.
    /// A task is offered only when it starts a program from that program's own folder, or from the folder above it (where
    /// launchers such as Squirrel's Update.exe live): a task file planted under another program's name cannot make its
    /// button run something unrelated, such as a command interpreter labelled "New window" (v2 threat model, V2-02).
    /// </summary>
    public static List<Entry> For(IEnumerable<string> appIds, string? programExe)
    {
        if (programExe == null || !Launcher.IsOnThisPc(programExe)) return new();
        foreach (var id in appIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var file = Path.Combine(Dir, id + ".customDestinations-ms");
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > MaxFile) continue;
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[fs.Length];
                fs.ReadExactly(bytes);
                var list = Read(bytes).Where(e => e.IsSeparator ||
                    (Launcher.IsOnThisPc(e.Exe) && e.Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && SameHome(e.Exe, programExe))).ToList();
                if (list.Any(e => !e.IsSeparator)) return Tidy(list);
            }
            catch { }
        }
        return new();
    }

    /// <summary>The task's program lives in <paramref name="programExe"/>'s folder, or in the folder above it.</summary>
    internal static bool SameHome(string taskExe, string programExe)
    {
        var task = Path.GetDirectoryName(Path.GetFullPath(taskExe));
        var home = Path.GetDirectoryName(Path.GetFullPath(programExe));
        if (task == null || home == null) return false;
        return string.Equals(task, home, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(task, Path.GetDirectoryName(home), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>No separator first, last or twice in a row.</summary>
    static List<Entry> Tidy(List<Entry> list)
    {
        var tidy = new List<Entry>();
        foreach (var e in list)
            if (!e.IsSeparator || (tidy.Count > 0 && !tidy[^1].IsSeparator && tidy[^1].Category == e.Category)) tidy.Add(e);
        while (tidy.Count > 0 && tidy[^1].IsSeparator) tidy.RemoveAt(tidy.Count - 1);
        return tidy;
    }

    /// <summary>The entries of a customDestinations-ms file (everything up to the first thing that does not fit the layout).</summary>
    internal static List<Entry> Read(byte[] b)
    {
        var result = new List<Entry>();
        if (b.Length < 12 || BitConverter.ToInt32(b, 0) != 2) return result;
        int categories = BitConverter.ToInt32(b, 4);
        int pos = 12;
        for (int c = 0; c < categories && c < MaxCategories; c++)
        {
            if (pos + 4 > b.Length) break;
            int type = BitConverter.ToInt32(b, pos); pos += 4;
            string category; int count;
            if (type == 1)
            {
                pos += 4;   // a known category (Frequent / Recent): its items are the automatic list's, read by JumpLists
                if (!FooterAt(b, pos)) break;
                pos += 4;
                continue;
            }
            if (type == 0) { if (!NamedHeader(b, ref pos, out category, out count)) break; }
            else if (type == 2)
            {
                if (pos + 4 > b.Length) break;
                count = BitConverter.ToInt32(b, pos); pos += 4;
                category = "Tasks";
            }
            else break;
            if (count is < 0 or > MaxEntries) break;
            bool ok = true;
            for (int i = 0; i < count && ok; i++)
            {
                if (!ShellLinkAt(b, pos)) { ok = false; break; }
                pos += 16;
                var e = LoadLink(b, ref pos, category);
                if (pos < 0) ok = false;
                else if (e != null) result.Add(e);
            }
            if (!ok || !FooterAt(b, pos)) break;
            pos += 4;
        }
        return result;
    }

    static bool FooterAt(byte[] b, int pos) => pos >= 0 && pos + 4 <= b.Length && BitConverter.ToUInt32(b, pos) == Footer;
    static bool ShellLinkAt(byte[] b, int pos) => pos >= 0 && pos + 16 <= b.Length && new Guid(b.AsSpan(pos, 16)) == ShellLinkClsid;

    /// <summary>A named category's header. Its name length is read as 2 bytes, else as 4, whichever is followed by a
    /// sensible entry count and then an entry (or the footer).</summary>
    static bool NamedHeader(byte[] b, ref int pos, out string name, out int count)
    {
        foreach (int lengthSize in new[] { 2, 4 })
        {
            int p = pos;
            if (p + lengthSize > b.Length) continue;
            int len = lengthSize == 2 ? BitConverter.ToUInt16(b, p) : BitConverter.ToInt32(b, p);
            p += lengthSize;
            if (len is < 0 or > 256 || p + len * 2 + 4 > b.Length) continue;
            var n = Encoding.Unicode.GetString(b, p, len * 2);
            p += len * 2;
            int cnt = BitConverter.ToInt32(b, p);
            p += 4;
            if (cnt is < 0 or > MaxEntries || !(cnt == 0 ? FooterAt(b, p) : ShellLinkAt(b, p))) continue;
            name = Indirect(n) ?? n; count = cnt; pos = p;
            return true;
        }
        name = ""; count = 0;
        return false;
    }

    // ---------- one shortcut, through Windows' own loader ----------

    [ComImport, Guid("00000109-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistStream
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load(IStream stream);
        void Save(IStream stream, [MarshalAs(UnmanagedType.Bool)] bool clearDirty);
        void GetSizeMax(out long size);
    }

    [DllImport("shlwapi.dll")] static extern IStream SHCreateMemStream(byte[]? init, uint size);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int SHLoadIndirectString(string source, StringBuilder output, uint cch, nint reserved);

    /// <summary>Reads the shortcut at <paramref name="pos"/> and moves past it (to -1 when it cannot be read).</summary>
    static Entry? LoadLink(byte[] b, ref int pos, string category)
    {
        object? link = null; IStream? stream = null;
        nint at = Marshal.AllocHGlobal(8);
        try
        {
            stream = SHCreateMemStream(b[pos..], (uint)(b.Length - pos));
            link = new ShellLinkObject();
            ((IPersistStream)link).Load(stream);
            stream.Seek(0, 1 /* STREAM_SEEK_CUR */, at);
            long used = Marshal.ReadInt64(at);
            if (used <= 0 || used > b.Length - pos) { pos = -1; return null; }
            pos += (int)used;

            var props = (IPropertyStore)link;
            if (PropStore.GetBool(props, IsSeparator)) return new Entry(category, "", "", "", null, 0);
            var sl = (IShellLinkW)link;
            var sb = new StringBuilder(4096);
            sl.GetPath(sb, sb.Capacity, 0, 0);
            string exe = sb.ToString();
            sb.Clear(); sl.GetArguments(sb, sb.Capacity);
            string args = sb.ToString();
            sb.Clear(); sl.GetIconLocation(sb, sb.Capacity, out int iconIndex);
            string? iconFile = sb.Length > 0 ? Environment.ExpandEnvironmentVariables(sb.ToString()) : null;
            string title = PropStore.GetString(props, Title) is { Length: > 0 } t ? Indirect(t) ?? t : "";
            if (title.Length == 0) { sb.Clear(); sl.GetDescription(sb, sb.Capacity); title = sb.ToString(); }
            if (exe.Length == 0 || title.Length == 0 || title.StartsWith('@')) return null;   // nothing to start, or no readable name
            return new Entry(category, title, exe, args, iconFile, iconIndex);
        }
        catch { pos = -1; return null; }
        finally
        {
            Marshal.FreeHGlobal(at);
            if (link != null) Marshal.ReleaseComObject(link);
            if (stream != null) Marshal.ReleaseComObject(stream);
        }
    }

#if DEV_BUILD
    /// <summary>
    /// For the self-test: a customDestinations-ms file as Windows writes it, with a named category ("Pinned sites", name
    /// length in 2 bytes) holding one task, then a task category holding a task, a separator and a task.
    /// </summary>
    internal static byte[] Sample(string exe)
    {
        var o = new MemoryStream();
        void I32(int v) => o.Write(BitConverter.GetBytes(v));
        void Link(string title, string args, bool separator)
        {
            o.Write(ShellLinkClsid.ToByteArray());
            object link = new ShellLinkObject();
            var sl = (IShellLinkW)link;
            if (!separator) { sl.SetPath(exe); sl.SetArguments(args); }
            var props = (IPropertyStore)link;
            var key = separator ? IsSeparator : Title;
            var pv = separator ? new PROPVARIANT { vt = 11, p = 0xFFFF } : new PROPVARIANT { vt = 31, p = Marshal.StringToCoTaskMemUni(title) };
            props.SetValue(ref key, ref pv);
            props.Commit();
            if (!separator) Marshal.FreeCoTaskMem(pv.p);
            var stream = SHCreateMemStream(null, 0);
            ((IPersistStream)link).Save(stream, true);
            stream.Stat(out var st, 1);
            stream.Seek(0, 0, 0);
            var data = new byte[st.cbSize];
            stream.Read(data, data.Length, 0);
            o.Write(data);
            Marshal.ReleaseComObject(stream); Marshal.ReleaseComObject(link);
        }
        I32(2); I32(2); I32(0);
        I32(0);                                                     // named category
        var name = "Pinned sites";
        o.Write(BitConverter.GetBytes((ushort)name.Length)); o.Write(Encoding.Unicode.GetBytes(name));
        I32(1); Link("Open example", "--site example", false);
        I32(unchecked((int)Footer));
        I32(2); I32(3);                                             // tasks
        Link("New window", "-new-window", false); Link("", "", true); Link("New private window", "-private-window", false);
        I32(unchecked((int)Footer));
        return o.ToArray();
    }
#endif

    /// <summary>
    /// A title kept as a resource reference ("@{package?ms-resource://...}", "@C:\...\app.dll,-101"): its text, when the
    /// resource is in a package or in a file on this PC; otherwise null.
    /// </summary>
    static string? Indirect(string s)
    {
        if (!s.StartsWith('@')) return null;
        if (!s.StartsWith("@{"))
        {
            var file = Environment.ExpandEnvironmentVariables(s[1..].Split(',')[0]);
            if (!Launcher.IsOnThisPc(file)) return null;
        }
        var sb = new StringBuilder(512);
        return SHLoadIndirectString(s, sb, (uint)sb.Capacity, 0) == 0 && sb.Length > 0 ? sb.ToString() : null;
    }
}
