using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace RestoreWin10;

/// <summary>A row in the app list: either a letter/section header or an app.</summary>
/// <summary>One row of the Start menu's app list: a header, an app, or a folder.</summary>
public sealed class Row
{
    /// <summary>A header's text: a letter, "&amp;", "#", the globe glyph, "Recently added" or "Most used".</summary>
    public string? Header { get; init; }
    /// <summary>For headers the jump grid can reach: the group's key (A–Z, &amp;, #, globe) or "recent".</summary>
    public string? Key { get; init; }
    /// <summary>The header is a Segoe MDL2 glyph (the globe), not text.</summary>
    public bool GlyphHeader { get; init; }
    public AppEntry? App { get; init; }
    public AppFolder? Folder { get; init; }
    /// <summary>For an app shown inside an open folder: that folder.</summary>
    public AppFolder? Parent { get; init; }

    public bool IsApp => App != null;
    public bool IsHeader => Header != null;
    public bool IsFolder => Folder != null;
    public bool IsIndented => Parent != null;
    /// <summary>Letter headers open the jump grid; apps and folders act on a click; "Most used" does nothing.</summary>
    public bool IsInteractive => IsApp || IsFolder || (IsHeader && Key != null && Key != AppList.Recent);
}

/// <summary>An item a panel must leave as an empty, un-animated gap while the user drags it.</summary>
public interface IDragItem { bool IsDragging { get; } }

public abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A pinned tile. Size: S 1x1, M 2x2, W 4x2, L 4x4 cells (48px cells, 2px gaps).</summary>
public sealed class Tile : Notifier, IDragItem
{
    bool _dragging, _open, _folderHover;
    TileFolder? _folder;

    /// <summary>The app it starts (for a folder tile: a placeholder that is never started).</summary>
    public AppEntry App { get; init; } = null!;
    public string Size { get; init; } = "M";

    /// <summary>A folder tile's contents (Windows 10: drop a tile onto another and they share one tile).</summary>
    public TileFolder? Folder
    {
        get => _folder;
        init
        {
            _folder = value;
            if (value != null) value.Tiles.CollectionChanged += (_, _) => Raise(nameof(Preview));
        }
    }
    public bool IsFolder => _folder != null;
    /// <summary>A folder tile: the first icons of its tiles, shown small (four on a small tile, nine otherwise).</summary>
    public List<BitmapSource?> Preview => _folder?.Tiles.Take(Size == "S" ? 4 : 9).Select(t => t.Icon).ToList() ?? new();
    public double PreviewIcon => Size == "S" ? 16 : 22;   // each with 3 px around it
    public double PreviewWidth => Size switch { "S" => 44, "M" => 84, _ => 168 };   // two, three or six icons a row
    /// <summary>A folder tile whose contents are showing below it.</summary>
    public bool IsOpen { get => _open; set => Set(ref _open, value); }
    /// <summary>A dragged tile is resting on this one: letting go makes (or adds to) a folder.</summary>
    public bool IsFolderTarget { get => _folderHover; set => Set(ref _folderHover, value); }

    public string Name => _folder?.Name ?? App.Name;
    public BitmapSource? Icon => IsFolder ? null : App.Icon;

    public int Cols => Size switch { "S" => 1, "M" => 2, _ => 4 };
    public int Rows => Size switch { "S" => 1, "L" => 4, _ => 2 };
    public double Width => Cols * 50 - 2;
    public double Height => Rows * 50 - 2;
    public bool ShowLabel => Size != "S";
    public double IconSize => Size switch { "S" => 24, "L" => 48, _ => 32 };

    public bool IsDragging { get => _dragging; set => Set(ref _dragging, value); }

    public Tile WithSize(string size) => new() { App = App, Folder = Folder, Size = size };

    /// <summary>What a folder tile stands on in place of an app.</summary>
    public static readonly AppEntry FolderPlaceholder = new() { Name = "", Path = "" };
}

/// <summary>The tiles inside a folder tile, and the folder's name (empty until the user names it).</summary>
public sealed class TileFolder : Notifier
{
    string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    public ObservableCollection<Tile> Tiles { get; } = new();
}

/// <summary>A named cluster of tiles laid out on a 6-column cell grid.</summary>
public sealed class TileGroup : Notifier
{
    string _name = "";
    bool _showZone, _zoneActive, _moving;
    Tile? _openFolder;
    double _bandTop;

    public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>The folder tile in this group whose contents are showing, in a band below its row (or null).</summary>
    public Tile? OpenFolder { get => _openFolder; set => Set(ref _openFolder, value); }
    /// <summary>Where that band starts, set by the group's tile panel, which also moves the tiles below it down.</summary>
    public double BandTop
    {
        get => _bandTop;
        set { if (Math.Abs(_bandTop - value) > 0.5) { _bandTop = value; Raise(); Raise(nameof(BandMargin)); } }
    }
    public System.Windows.Thickness BandMargin => new(0, _bandTop, 0, 0);
    /// <summary>Being moved by its handle (shown dimmed while it travels).</summary>
    public bool IsMoving { get => _moving; set => Set(ref _moving, value); }
    public ObservableCollection<Tile> Tiles { get; } = new();

    /// <summary>The group's layout panel (set by the panel itself once loaded).</summary>
    public TilePanel? Panel { get; set; }

    /// <summary>While dragging: the "drop here to start a new group" slot above this group is expanded.</summary>
    public bool ShowZone { get => _showZone; set => Set(ref _showZone, value); }
    public bool ZoneActive { get => _zoneActive; set => Set(ref _zoneActive, value); }
}

