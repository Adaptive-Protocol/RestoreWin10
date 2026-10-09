using System.Runtime.InteropServices;

namespace RestoreWin10;

/// <summary>
/// The taskbar menu's "Cascade windows", "Show windows stacked" and "Show windows side by side", with Windows 10's
/// matching "Undo ..." entry afterwards. Windows are arranged within each screen's own work area, on the screen they
/// are on, so nothing is thrown across monitors or under a taskbar.
/// </summary>
static class WindowArrange
{
    public enum Kind { Cascade, Stacked, SideBySide }

    const uint MDITILE_VERTICAL = 0, MDITILE_HORIZONTAL = 1, MDITILE_ZORDER = 4;

    static List<(nint Hwnd, Win.WINDOWPLACEMENT Placement)>? s_undo;
    static Kind s_undoKind;

    /// <summary>The undo entry for the last arrangement, if there is one to undo.</summary>
    public static string? UndoLabel => s_undo == null ? null : s_undoKind switch
    {
        Kind.Cascade => "Undo Cascade all windows",
        Kind.Stacked => "Undo Show all windows stacked",
        _ => "Undo Show all windows side by side",
    };

    public static void Arrange(Kind kind)
    {
        var wins = TaskTracker.OpenWindows().Distinct()
            .Where(h => Win.IsWindow(h) && Win.IsWindowVisible(h) && !Win.IsIconic(h) && Win.ClassName(h) != "Ghost").ToList();
        if (wins.Count == 0) return;

        s_undo = wins.Select(h => (h, Placement(h))).ToList();
        s_undoKind = kind;
        foreach (var h in wins) if (Win.IsZoomed(h)) Win.ShowWindow(h, Win.SW_RESTORE);   // maximised windows join in, as in Windows 10

        foreach (var onScreen in wins.GroupBy(Screens.HandleOf))
        {
            var area = Screens.FromWindow(onScreen.First()).Work;
            var kids = onScreen.ToArray();
            if (kind == Kind.Cascade) Win.CascadeWindows(0, MDITILE_ZORDER, ref area, (uint)kids.Length, kids);
            else Win.TileWindows(0, kind == Kind.Stacked ? MDITILE_HORIZONTAL : MDITILE_VERTICAL, ref area, (uint)kids.Length, kids);
        }
    }

    /// <summary>Puts every window back where (and as maximised/normal as) it was before the arrangement.</summary>
    public static void Undo()
    {
        if (s_undo == null) return;
        foreach (var (h, p) in s_undo)
        {
            if (!Win.IsWindow(h)) continue;
            var wp = p;
            Win.SetWindowPlacement(h, ref wp);
        }
        s_undo = null;
    }

    static Win.WINDOWPLACEMENT Placement(nint h)
    {
        var p = new Win.WINDOWPLACEMENT { length = Marshal.SizeOf<Win.WINDOWPLACEMENT>() };
        Win.GetWindowPlacement(h, ref p);
        return p;
    }
}
