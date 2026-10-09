using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RestoreWin10;

/// <summary>One entry of Start's left strip that Settings > Personalization > Start > Folders switches on or off.</summary>
public sealed record StartPlace(Guid Id, string Label, string Glyph, Action Open);

/// <summary>
/// The folders shown on Start's left strip follow Windows' own setting (decision D3), which Windows keeps as a list of
/// 16-byte ids in HKCU\...\CurrentVersion\Start\VisiblePlaces. The ids below are the ones in Windows' own table
/// (StartTileData.dll, read on 8 October 2026). They are shown in Windows 10's order, Settings last, above Power.
/// </summary>
public static class StartPlaces
{
    const string StartKey = @"Software\Microsoft\Windows\CurrentVersion\Start";

    static readonly (Guid Id, string Label, string Glyph)[] Known =
    {
        (new("148a24bc-d60c-4289-a080-6ed9bba24882"), "File Explorer", ""),
        (new("2d34d5ce-fa5a-4543-82f2-22e6eaf7773c"), "Documents", ""),
        (new("e367b32f-89de-4355-bfce-61f37b18a937"), "Downloads", ""),
        (new("b00b0620-7f51-4c32-aa1e-34cc547f7315"), "Music", ""),
        (new("383f07a0-e80a-4c80-b05a-86db845dbc4d"), "Pictures", ""),
        (new("42b3a5c5-7d86-42f4-80a4-93faca7a88b5"), "Videos", ""),
        (new("fe758144-080d-42ae-8bda-34ed97b66394"), "Network", ""),
        (new("74bdb04a-f94a-4f68-8bd6-4398071da8bc"), "Personal folder", ""),
        (new("52730886-51aa-4243-9f7b-2776584659d4"), "Settings", ""),
    };

    /// <summary>The raw setting, so a change can be noticed.</summary>
    public static string Signature()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(StartKey);
            return k?.GetValue("VisiblePlaces") is byte[] b ? Convert.ToHexString(b) : "";
        }
        catch { return ""; }
    }

    /// <summary>The ids switched on in Windows' setting (unknown ids are ignored).</summary>
    public static HashSet<Guid> Enabled(byte[]? raw)
    {
        var ids = new HashSet<Guid>();
        if (raw == null) return ids;
        for (int i = 0; i + 16 <= raw.Length; i += 16) ids.Add(new Guid(raw.AsSpan(i, 16)));
        return ids;
    }

    /// <summary>The strip's folder entries for Windows' current setting, in Windows 10's order.</summary>
    public static List<StartPlace> Read(Action<string> openFolder, Action openSettings, Action openExplorer, Action openNetwork)
    {
        byte[]? raw = null;
        try { using var k = Registry.CurrentUser.OpenSubKey(StartKey); raw = k?.GetValue("VisiblePlaces") as byte[]; } catch { }
        var on = Enabled(raw);
        var list = new List<StartPlace>();
        foreach (var (id, label, glyph) in Known)
        {
            if (!on.Contains(id)) continue;
            Action open = label switch
            {
                "File Explorer" => openExplorer,
                "Settings" => openSettings,
                "Network" => openNetwork,
                "Documents" => () => openFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                "Music" => () => openFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
                "Pictures" => () => openFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                "Videos" => () => openFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
                "Personal folder" => () => openFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                _ => () => openFolder(KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B")) ?? ""),   // Downloads
            };
            list.Add(new StartPlace(id, label, glyph, open));
        }
        return list;
    }

    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);

    /// <summary>A known folder's path (Downloads has no Environment.SpecialFolder).</summary>
    static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(ref id, 0, 0, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
    }
}
