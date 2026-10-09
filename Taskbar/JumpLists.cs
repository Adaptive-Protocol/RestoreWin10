using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace RestoreWin10;

/// <summary>
/// Reads the "Recent" lists Windows keeps for each program (the jump lists behind a Windows 10 taskbar button),
/// straight from the files Explorer maintains. Windows only fills them while "Show recently opened items in
/// Start, Jump Lists, and File Explorer" is on.
/// </summary>
public static class JumpLists
{
    /// <summary>A recent or pinned item. <paramref name="PinOrder"/>: its place among the pinned ones (-1: not pinned).
    /// <paramref name="Name"/>: a display name when it is not simply the file name (Windows' own folders).</summary>
    public record Entry(string Path, DateTime When, int PinOrder, string? Name = null)
    {
        public bool Pinned => PinOrder >= 0;
    }

    // A DestList is a few kilobytes (Windows keeps a few dozen entries); anything near these sizes is not one Windows
    // wrote, and is skipped rather than read into memory (threat model F-09).
    const long MaxFile = 16 * 1024 * 1024, MaxDestList = 4 * 1024 * 1024;

    static readonly string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent\AutomaticDestinations");

    /// <summary>
    /// The 16-hex-digit AppID Windows derives from a program's AppUserModelID, or from its full path when it has none:
    /// CRC-64 (reflected, polynomial 0x92C64265D32139A4) of the upper-cased UTF-16 text.
    /// </summary>
    public static string AppId(string text)
    {
        const ulong poly = 0x92C64265D32139A4UL;
        ulong crc = ulong.MaxValue;
        foreach (byte b in Encoding.Unicode.GetBytes(text.ToUpperInvariant()))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
        }
        return crc.ToString("x16");
    }

    /// <summary>Recent items for any of <paramref name="appIds"/>, newest first, de-duplicated, at most <paramref name="max"/>.</summary>
    public static List<Entry> Recent(IEnumerable<string> appIds, int max)
    {
        var all = new List<Entry>();
        foreach (var id in appIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var file = System.IO.Path.Combine(Dir, id + ".automaticDestinations-ms");
            var info = new FileInfo(file);
            if (info.Exists && info.Length <= MaxFile) all.AddRange(Read(file));
        }
        // Windows' own folders are filed as "knownfolder:{id}" (Documents, Downloads...): they become their real folder
        // and name. Only full paths are offered (they are what the program is then given to open); other shell names
        // ("::{...}"), web addresses and anything with characters a path cannot hold are left out.
        return all.Select(Resolve).Where(e => e != null && Launcher.IsDocumentPath(e.Path)).Select(e => e!)
                  .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(e => e.Pinned).ThenByDescending(e => e.When).First())
                  .OrderByDescending(e => e.Pinned).ThenBy(e => e.Pinned ? e.PinOrder : 0).ThenByDescending(e => e.When)
                  .Take(max).ToList();
    }

    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);

    static Entry? Resolve(Entry e)
    {
        const string prefix = "knownfolder:";
        if (!e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return e;
        if (!Guid.TryParse(e.Path[prefix.Length..], out var id)) return null;
        nint p = 0;
        try
        {
            if (SHGetKnownFolderPath(ref id, 0, 0, out p) != 0 || p == 0) return null;
            var path = Marshal.PtrToStringUni(p);
            return path == null ? null : e with { Path = path, Name = IconHelper.DisplayName(path) };
        }
        finally { if (p != 0) Marshal.FreeCoTaskMem(p); }
    }

    // ---------- the file: an OLE compound document whose "DestList" stream lists the entries ----------

    [ComImport, Guid("0000000B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IStorage
    {
        void CreateStream(string name, uint mode, uint r1, uint r2, out IStream stream);
        void OpenStream(string name, nint r1, uint mode, uint r2, out IStream stream);
    }
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    static extern int StgOpenStorageEx(string name, uint mode, uint fmt, uint attrs, nint opts, nint reserved, ref Guid iid, out IStorage storage);

    static List<Entry> Read(string file)
    {
        var result = new List<Entry>();
        IStorage? stg = null; IStream? stream = null; string? temp = null;
        try
        {
            var iid = typeof(IStorage).GUID;
            // Explorer may hold the file open; a copy is always readable.
            if (StgOpenStorageEx(file, 0x20 /* READ | SHARE_DENY_WRITE */, 0, 0, 0, 0, ref iid, out stg) != 0)
            {
                temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RestoreWin10_" + Guid.NewGuid().ToString("N") + ".ms");
                File.Copy(file, temp, true);
                if (StgOpenStorageEx(temp, 0x20, 0, 0, 0, 0, ref iid, out stg) != 0) return result;
            }
            stg.OpenStream("DestList", 0, 0x10 /* READ | SHARE_EXCLUSIVE */, 0, out stream);
            stream.Stat(out var st, 1);
            if (st.cbSize < 0 || st.cbSize > MaxDestList) return result;
            var buf = new byte[st.cbSize];
            var readPtr = Marshal.AllocHGlobal(4);
            try { stream.Read(buf, buf.Length, readPtr); } finally { Marshal.FreeHGlobal(readPtr); }
            Parse(buf, result);
        }
        catch { }
        finally
        {
            if (stream != null) Marshal.ReleaseComObject(stream);
            if (stg != null) Marshal.ReleaseComObject(stg);
            if (temp != null) { try { File.Delete(temp); } catch { } }
        }
        return result;
    }

    /// <summary>DestList layout: a 32-byte header, then entries whose string offset depends on the version (1, 3 or 4 and later).</summary>
    static void Parse(byte[] buf, List<Entry> result)
    {
        if (buf.Length < 32) return;
        int version = BitConverter.ToInt32(buf, 0);
        int pos = 32;
        while (pos + 114 <= buf.Length)
        {
            int lengthAt = version >= 3 ? pos + 128 : pos + 112;
            if (lengthAt + 2 > buf.Length) break;
            long when = BitConverter.ToInt64(buf, pos + 100);
            int pin = BitConverter.ToInt32(buf, pos + 108);
            int len = BitConverter.ToUInt16(buf, lengthAt);
            if (lengthAt + 2 + len * 2 > buf.Length) break;
            var path = Encoding.Unicode.GetString(buf, lengthAt + 2, len * 2);
            DateTime stamp;
            try { stamp = DateTime.FromFileTime(when); } catch { stamp = DateTime.MinValue; }
            if (path.Length > 0) result.Add(new Entry(path, stamp, pin >= 0 ? pin : -1));
            pos = lengthAt + 2 + len * 2 + (version >= 4 ? 4 : 0);
        }
    }
}
