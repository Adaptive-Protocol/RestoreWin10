using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RestoreWin10;

/// <summary>
/// Windows 10 style tile layout: 6 columns of 48px cells separated by 2px, each tile placed at the
/// first free spot in reading order. When a tile's slot changes it slides to the new slot
/// (FLIP: arrange at the destination, then animate from the old offset back to zero).
/// </summary>
public sealed class TilePanel : Panel
{
    const int Columns = 6, Pitch = 50, MinRows = 2;
    static readonly Duration Slide = new(TimeSpan.FromMilliseconds(230));

    readonly List<Rect> _rects = new();
    Dictionary<UIElement, Point> _last = new();

    public TilePanel()
    {
        Loaded += (_, _) =>
        {
            if (IsBand || DataContext is not TileGroup g) return;
            g.Panel = this;
            // A folder opening or closing in this group, or tiles joining or leaving the open folder, changes the room
            // the band below the folder's row needs.
            g.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(TileGroup.OpenFolder)) return;
                if (_watched != null) _watched.CollectionChanged -= OnFolderChanged;
                _watched = g.OpenFolder?.Folder?.Tiles;
                if (_watched != null) _watched.CollectionChanged += OnFolderChanged;
                InvalidateMeasure();
            };
        };
    }

    System.Collections.ObjectModel.ObservableCollection<Tile>? _watched;
    void OnFolderChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => InvalidateMeasure();

    /// <summary>The panel inside an open folder's band: it lays out the folder's tiles and makes no band of its own.</summary>
    public bool IsBand { get; set; }

    /// <summary>The band's own parts: padding above, the "Name folder" box, the gap below it, padding below.</summary>
    public const double BandChrome = 6 + 30 + 4 + 8;

    /// <summary>How tall an open folder's band is for these tiles (the band lays them out with this same panel).</summary>
    public static double BandHeight(IEnumerable<Tile> tiles) => BandChrome + Layout(tiles.Select(t => (t.Cols, t.Rows)).ToList()).Height;

    public int Count => _rects.Count;
    public Rect RectOf(int i) => _rects[i];

    /// <summary>The tile a child shows. Asked of the items control itself: a freshly made child (a tile just resized
    /// replaces its old self) may be measured before its DataContext is set, and was then laid out at the default
    /// medium size and drawn clipped.</summary>
    Tile? TileOf(UIElement child) =>
        child is FrameworkElement { DataContext: Tile t } ? t
        : ItemsControl.GetItemsOwner(this)?.ItemContainerGenerator.ItemFromContainer(child) as Tile;

    (int Cols, int Rows) Span(UIElement child) => TileOf(child) is { } t ? (t.Cols, t.Rows) : (2, 2);

    protected override void OnVisualChildrenChanged(DependencyObject added, DependencyObject removed)
    {
        base.OnVisualChildrenChanged(added, removed);
        if (added is FrameworkElement fe) fe.DataContextChanged += (_, _) => InvalidateMeasure();
    }

    /// <summary>Each tile at the first free spot in reading order; returns the slots and the height they need.</summary>
    static (List<Rect> Rects, double Height) Layout(IReadOnlyList<(int Cols, int Rows)> spans)
    {
        var rects = new List<Rect>();
        var used = new List<bool[]>();
        bool[] Row(int r) { while (used.Count <= r) used.Add(new bool[Columns]); return used[r]; }

        foreach (var (w, h) in spans)
        {
            for (int r = 0; ; r++)
            {
                int found = -1;
                for (int c = 0; c + w <= Columns && found < 0; c++)
                {
                    bool free = true;
                    for (int dy = 0; dy < h && free; dy++)
                        for (int dx = 0; dx < w; dx++)
                            if (Row(r + dy)[c + dx]) { free = false; break; }
                    if (free) found = c;
                }
                if (found < 0) continue;
                for (int dy = 0; dy < h; dy++)
                    for (int dx = 0; dx < w; dx++) Row(r + dy)[found + dx] = true;
                rects.Add(new Rect(found * Pitch, r * Pitch, w * Pitch - 2, h * Pitch - 2));
                break;
            }
        }
        double bottom = rects.Count == 0 ? 0 : rects.Max(r => r.Bottom);
        return (rects, Math.Max(bottom, MinRows * Pitch - 2));
    }

    double _bandBottom;   // where the open folder's band ends (0: no band)

    void Compute()
    {
        var spans = InternalChildren.Cast<UIElement>().Select(Span).ToList();
        _rects.Clear();
        _rects.AddRange(Layout(spans).Rects);
        _bandBottom = 0;
        if (IsBand || DataContext is not TileGroup g) return;

        int open = -1;
        for (int i = 0; i < InternalChildren.Count && g.OpenFolder != null; i++)
            if (TileOf(InternalChildren[i]) == g.OpenFolder) { open = i; break; }
        if (open < 0) return;

        // The band goes below the folder's row: below every tile that starts before the band would, so none is cut.
        double rowEnd = _rects[open].Bottom;
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var r in _rects)
                if (r.Top < rowEnd && r.Bottom > rowEnd) { rowEnd = r.Bottom; grew = true; }
        }
        double top = rowEnd + 2, height = BandHeight(g.OpenFolder!.Folder!.Tiles);
        for (int i = 0; i < _rects.Count; i++)   // everything below moves down to make room
            if (_rects[i].Top >= rowEnd) _rects[i] = new Rect(_rects[i].X, _rects[i].Y + height + 4, _rects[i].Width, _rects[i].Height);
        g.BandTop = top;
        _bandBottom = top + height;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Compute();
        for (int i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Measure(_rects[i].Size);
        double bottom = Math.Max(_rects.Count == 0 ? 0 : _rects.Max(r => r.Bottom), _bandBottom);
        return new Size(Columns * Pitch - 2, Math.Max(bottom, MinRows * Pitch - 2));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_rects.Count != InternalChildren.Count) Compute();
        var now = new Dictionary<UIElement, Point>();
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var r = _rects[i];
            child.Arrange(r);
            now[child] = r.TopLeft;

            if (child is FrameworkElement { DataContext: IDragItem { IsDragging: true } })
            {
                // The dragged tile is the gap: it is never slid about underneath the ghost.
                if (child.RenderTransform is TranslateTransform held)
                {
                    held.BeginAnimation(TranslateTransform.XProperty, null); held.BeginAnimation(TranslateTransform.YProperty, null);
                    held.X = 0; held.Y = 0;
                }
                continue;
            }
            if (IsLoaded && _last.TryGetValue(child, out var old) && (old - r.TopLeft).Length > 0.5)
            {
                if (child.RenderTransform is not TranslateTransform tt) child.RenderTransform = tt = new TranslateTransform();
                // Where the tile visually is right now (an earlier slide may still be running).
                double fx = tt.X + old.X - r.X, fy = tt.Y + old.Y - r.Y;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fx, 0, Slide) { EasingFunction = ease });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fy, 0, Slide) { EasingFunction = ease });
            }
        }
        _last = now;
        return finalSize;
    }
}
