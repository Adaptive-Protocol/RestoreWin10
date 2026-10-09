using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace RestoreWin10;

/// <summary>Windows' shortcut object (CLSID_ShellLink), used directly.</summary>
[ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLinkObject { }

[ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellLinkW
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, nint findData, uint flags);
    void GetIDList(out nint pidl);
    void SetIDList(nint pidl);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
    void GetHotkey(out short hotkey);
    void SetHotkey(short hotkey);
    void GetShowCmd(out int showCmd);
    void SetShowCmd(int showCmd);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
    void Resolve(nint hwnd, uint flags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}

/// <summary>
/// Reads .lnk files and lists the Apps folder through Windows' own interfaces. (v1 used the scripting objects
/// WScript.Shell and Shell.Application through C# "dynamic", which loads the C# runtime binder and keeps compiled call
/// sites for the life of the process; Phase 7, memory.)
/// </summary>
static class Shortcut
{
    /// <summary>A shortcut's target (environment variables expanded), its arguments, and its icon location (empty when it has none).</summary>
    public sealed record Info(string Target, string Arguments, string IconFile, int IconIndex);

    public static Info? Read(string lnk)
    {
        object? link = null;
        try
        {
            link = new ShellLinkObject();
            ((IPersistFile)link).Load(lnk, 0 /* STGM_READ */);
            var sl = (IShellLinkW)link;
            var sb = new StringBuilder(1024);
            sl.GetPath(sb, sb.Capacity, 0, 0);
            string target = Environment.ExpandEnvironmentVariables(sb.ToString());
            sb.Clear();
            sl.GetArguments(sb, sb.Capacity);
            string args = sb.ToString();
            sb.Clear();
            sl.GetIconLocation(sb, sb.Capacity, out int index);
            return new Info(target, args, Environment.ExpandEnvironmentVariables(sb.ToString()), index);
        }
        catch { return null; }
        finally { if (link != null) Marshal.ReleaseComObject(link); }
    }

    // ---------- the Apps folder ----------

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(nint bc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, out IShellItem item, out uint fetched);
        void Skip(uint count);
        void Reset();
        void Clone(out IEnumShellItems copy);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, nint bc, ref Guid riid, out IShellItem item);

    const uint SigdnNormalDisplay = 0, SigdnParentRelativeParsing = 0x80018001;

    /// <summary>The Apps folder's entries: (id relative to the folder, display name). Its own thread is the caller's job.</summary>
    public static List<(string Id, string Name)> AppsFolder()
    {
        var list = new List<(string, string)>();
        var iid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName("shell:AppsFolder", 0, ref iid, out var folder) != 0) return list;
        object? items = null;
        try
        {
            var bhid = new Guid("94f60519-2850-4924-aa5a-d15e84868039");   // BHID_EnumItems
            var eiid = typeof(IEnumShellItems).GUID;
            folder.BindToHandler(0, ref bhid, ref eiid, out items);
            var e = (IEnumShellItems)items;
            while (e.Next(1, out var item, out uint got) == 0 && got == 1)
            {
                try
                {
                    string? id = Name(item, SigdnParentRelativeParsing), name = Name(item, SigdnNormalDisplay);
                    if (id != null && name != null) list.Add((id, name));
                }
                finally { Marshal.ReleaseComObject(item); }
            }
        }
        catch { }
        finally
        {
            if (items != null) Marshal.ReleaseComObject(items);
            Marshal.ReleaseComObject(folder);
        }
        return list;
    }

    static string? Name(IShellItem item, uint sigdn)
    {
        try
        {
            item.GetDisplayName(sigdn, out var p);
            try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
        }
        catch { return null; }
    }
}
