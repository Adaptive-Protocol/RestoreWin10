using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RestoreWin10;

/// <summary>A Start Menu folder in the app list: listed once under its letter; clicking it shows or hides its apps.</summary>
public sealed class AppFolder : Notifier
{
    bool _open;
    public string Name { get; init; } = "";
    public List<AppEntry> Apps { get; init; } = new();
    public bool IsOpen { get => _open; set => Set(ref _open, value); }
    /// <summary>The plain yellow folder Windows 10 shows for every Start Menu folder.</summary>
    public static BitmapSource? Icon => s_icon ??= StockFolderIcon();

    static BitmapSource? s_icon;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHSTOCKICONINFO { public int cbSize; public nint hIcon; public int iSysImageIndex, iIcon; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath; }
    [DllImport("shell32.dll")] static extern int SHGetStockIconInfo(int id, uint flags, ref SHSTOCKICONINFO info);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint h);

    static BitmapSource? StockFolderIcon()
    {
        var info = new SHSTOCKICONINFO { cbSize = Marshal.SizeOf<SHSTOCKICONINFO>() };
        if (SHGetStockIconInfo(3 /* SIID_FOLDER */, 0x100 /* SHGSI_ICON */, ref info) != 0 || info.hIcon == 0) return null;
        try
        {
            var bs = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze();
            return bs;
        }
        finally { DestroyIcon(info.hIcon); }
    }
}

/// <summary>One key of the A–Z jump grid.</summary>
public sealed record JumpKey(string Key, string Text, bool Glyph, bool Enabled);

/// <summary>How the Start menu's app list is laid out, as in Windows 10. Pure functions, so the self-test can check them.</summary>
public static class AppList
{
    public const string Symbols = "&", Digits = "#", OtherScripts = "globe", Recent = "recent";
    public const string GlobeGlyph = "", ClockGlyph = "";

    /// <summary>
    /// The group a name is listed under: <c>&amp;</c> for symbols, <c>#</c> for digits, A–Z for Latin letters (accents
    /// ignored: "Écran" is under E), and the globe for other alphabets.
    /// </summary>
    public static string KeyOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return Symbols;
        char c = name[0];
        if (char.IsDigit(c)) return Digits;
        if (!char.IsLetter(c)) return Symbols;
        var plain = name[..1].Normalize(NormalizationForm.FormD)[0];   // the base letter without its accent
        plain = char.ToUpperInvariant(plain);
        return plain is >= 'A' and <= 'Z' ? plain.ToString() : OtherScripts;
    }

    /// <summary>Windows 10's order of the groups: &amp;, #, A–Z, then other alphabets.</summary>
    public static int Order(string key) => key switch
    {
        Symbols => 0,
        Digits => 1,
        OtherScripts => 100,
        _ => 2 + (key[0] - 'A'),
    };

    /// <summary>How long a new app stays in "Recently added", and how many are shown.</summary>
    public static readonly TimeSpan RecentFor = TimeSpan.FromDays(7);
    public const int RecentCount = 3;

    /// <summary>
    /// The full list (no search text): "Recently added" (at most three apps added in the last week, newest first), "Most
    /// used" (when Windows' setting allows), then each group. In a group, folders sort among the apps by name, and an
    /// open folder is followed by its apps.
    /// </summary>
    public static List<Row> Build(IReadOnlyList<AppEntry> apps, IReadOnlyDictionary<string, int> usage,
                                  IReadOnlyDictionary<string, DateTime> firstSeen, DateTime now,
                                  bool showRecent, bool showMostUsed, ISet<string> openFolders)
    {
        var rows = new List<Row>();
        var cmp = StringComparer.CurrentCultureIgnoreCase;

        if (showRecent)
        {
            var recent = apps.Where(a => firstSeen.TryGetValue(a.Path, out var t) && t > now - RecentFor)
                             .OrderByDescending(a => firstSeen[a.Path]).ThenBy(a => a.Name, cmp).Take(RecentCount).ToList();
            if (recent.Count > 0)
            {
                rows.Add(new Row { Header = "Recently added", Key = Recent });
                rows.AddRange(recent.Select(a => new Row { App = a }));
            }
        }
        if (showMostUsed)
        {
            var used = apps.Where(a => usage.GetValueOrDefault(a.Path) > 0).OrderByDescending(a => usage[a.Path]).Take(3).ToList();
            if (used.Count > 0)
            {
                rows.Add(new Row { Header = "Most used" });
                rows.AddRange(used.Select(a => new Row { App = a }));
            }
        }

        // Entries of the A–Z part: top-level apps, and one per folder.
        var entries = new List<(string Name, AppEntry? App, AppFolder? Folder)>();
        entries.AddRange(apps.Where(a => a.Folder == null).Select(a => (a.Name, (AppEntry?)a, (AppFolder?)null)));
        foreach (var g in apps.Where(a => a.Folder != null).GroupBy(a => a.Folder!, StringComparer.CurrentCultureIgnoreCase))
        {
            var folder = new AppFolder { Name = g.First().Folder!, Apps = g.OrderBy(a => a.Name, cmp).ToList(), IsOpen = openFolders.Contains(g.Key) };
            entries.Add((folder.Name, null, folder));
        }
        foreach (var group in entries.GroupBy(e => KeyOf(e.Name)).OrderBy(g => Order(g.Key)))
        {
            rows.Add(new Row { Header = group.Key == OtherScripts ? GlobeGlyph : group.Key, Key = group.Key, GlyphHeader = group.Key == OtherScripts });
            foreach (var e in group.OrderBy(e => e.Name, cmp))
            {
                if (e.App != null) { rows.Add(new Row { App = e.App }); continue; }
                rows.Add(new Row { Folder = e.Folder });
                if (e.Folder!.IsOpen) rows.AddRange(ChildRows(e.Folder));
            }
        }
        return rows;
    }

    /// <summary>The rows of a folder's apps, shown below it while it is open.</summary>
    public static IEnumerable<Row> ChildRows(AppFolder f) => f.Apps.Select(a => new Row { App = a, Parent = f });

    /// <summary>The jump grid: the clock ("Recently added"), &amp;, #, A–Z and the globe; keys with nothing under them are dimmed.</summary>
    public static List<JumpKey> JumpKeys(IEnumerable<Row> rows)
    {
        var present = rows.Where(r => r.Key != null).Select(r => r.Key!).ToHashSet();
        var keys = new List<JumpKey>
        {
            new(Recent, ClockGlyph, true, present.Contains(Recent)),
            new(Symbols, Symbols, false, present.Contains(Symbols)),
            new(Digits, Digits, false, present.Contains(Digits)),
        };
        for (char c = 'A'; c <= 'Z'; c++) keys.Add(new(c.ToString(), c.ToString(), false, present.Contains(c.ToString())));
        keys.Add(new(OtherScripts, GlobeGlyph, true, present.Contains(OtherScripts)));
        return keys;
    }

    /// <summary>
    /// Keeps note of when each app was first seen, for "Recently added". New apps are stamped with <paramref name="now"/>;
    /// apps that are gone are forgotten. The very first time (nothing noted yet), desktop apps take their shortcut's
    /// creation time, so something installed shortly before still counts, and Store apps count as old. Returns true when
    /// anything changed.
    /// </summary>
    public static bool NoteFirstSeen(Dictionary<string, DateTime> seen, IReadOnlyList<AppEntry> apps, DateTime now, Func<string, DateTime?> createdAt)
    {
        bool first = seen.Count == 0, changed = false;
        foreach (var a in apps)
            if (!seen.ContainsKey(a.Path))
            {
                seen[a.Path] = !first ? now : a.IsStore ? DateTime.MinValue : createdAt(a.Path) ?? DateTime.MinValue;
                changed = true;
            }
        var present = apps.Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in seen.Keys.Where(k => !present.Contains(k)).ToList()) { seen.Remove(gone); changed = true; }
        return changed;
    }

    /// <summary>An app the user has started leaves "Recently added" (as in Windows 10). Returns true when that changed anything.</summary>
    public static bool NoteLaunched(Dictionary<string, DateTime> seen, string path, DateTime now)
    {
        if (!seen.TryGetValue(path, out var t) || t <= now - RecentFor) return false;
        seen[path] = DateTime.MinValue;
        return true;
    }
}
