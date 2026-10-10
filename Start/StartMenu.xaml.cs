using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RestoreWin10;

public partial class StartMenu : Window
{
    readonly State _state = State.Shared;
    readonly ObservableCollection<TileGroup> _groups = new();
    List<AppEntry> _all = new();
    ObservableCollection<Row> _rows = new();                                              // what the list shows
    readonly HashSet<string> _openFolders = new(StringComparer.CurrentCultureIgnoreCase);  // folders opened in the list
    AppWatcher? _watcher;            // installs and uninstalls while running
    List<AppEntry>? _pendingApps;    // a new app list that waits until the menu is closed (rows never move under the user)
    bool _refreshing, _againShortcuts, _againStore;
    DateTime _hiddenAt = DateTime.MinValue;
    bool _popupOpen;       // a context menu is up: don't treat the focus change as "click away"
    bool _railExpanded;

    // drag state
    Tile? _pressed, _dragTile;
    FrameworkElement? _pressedEl;
    Point _downPos, _grab;
    bool _drag;
    TileGroup? _dragGroup, _zoneGroup;
    bool _zoneEnd;
    FrameworkElement? _ghost;
    DateTime _lastShuffle;
    AppEntry? _listApp;     // a list row the button went down on (becomes a drag if the mouse travels)
    bool _appDrag;          // the dragged tile is a new pin coming from the list, not an existing tile
    bool _overTiles;        // the ghost's centre is somewhere in the tile area
    bool _overList;         // ... or over the app list: letting go there unpins the tile
    DateTime _dragEndedAt;
    readonly List<(Tile Tile, UIElement Ghost)> _landing = new();   // dropped tiles whose ghost is still gliding home
    Point _lastPt;
    DispatcherTimer? _shuffleRetry;   // re-checks the ghost's position after a cooldown swallowed a move

    public StartMenu()
    {
        InitializeComponent();
        Groups.ItemsSource = _groups;
        BuildRail();
        Root.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnRootUp), handledEventsToo: true);

        // Windows' colours (light/dark, accent tiles, transparency, accent-coloured Start), followed live.
        SourceInitialized += (_, _) => Personalization.Follow(this, Personalization.Kind.Start);
        IsVisibleChanged += (_, _) => OpenChanged?.Invoke();
        // Settings > Personalization > Start > "Show most used apps".
        Personalization.OptionsChanged += () =>
        {
            if (!_drag) ShowList(Search.Text.Trim());
            if (_panel == RailPanelKind.None && !_railExpanded) BuildRail();   // Start > Folders changed (not while the strip is in use)
        };
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
    }

    // ---------- rail ----------

    Button? _userButton, _powerButton;

    /// <summary>
    /// The left strip: the hamburger with "START" at the top; at the bottom the account, the folders Windows' own setting
    /// switches on (Settings > Personalization > Start > Folders, decision D3), and Power. Rebuilt when that setting changes.
    /// </summary>
    void BuildRail()
    {
        RailTop.Children.Clear();
        RailBottom.Children.Clear();
        RailTop.Children.Add(RailButton("\ue700", "START", ToggleRail, bold: true));
        // The signed-in user, as in Windows 10: picture (or the person glyph) and display name. A left-click opens the
        // account panel; a right-click does nothing (Windows 10 did the same on both, which the user asked to drop).
        _userButton = RailButton("\ue77b", Session.DisplayName, ShowAccountPanel, picture: Session.MyPicture);
        RailBottom.Children.Add(_userButton);
        foreach (var place in StartPlaces.Read(OpenFolder, () => OpenSettings(Launcher.SettingsHome),
                                               () => { Hide(); Launcher.Windows(Launcher.Explorer); },
                                               () => { Hide(); Launcher.Windows(Launcher.Explorer, false, "shell:NetworkPlacesFolder"); }))
        {
            var p = place;
            RailBottom.Children.Add(RailButton(p.Glyph, p.Label, p.Open));
        }
        _powerButton = RailButton("\ue7e8", "Power", ShowPowerPanel);
        RailBottom.Children.Add(_powerButton);
    }

    /// <summary>A strip entry: its glyph (or account picture) in the 48-wide column, and its label beside it, which shows
    /// once the strip is open (the closed strip simply clips it).</summary>
    Button RailButton(string glyph, string label, Action? onClick, ImageSource? picture = null, bool bold = false)
    {
        var lbl = new TextBlock { Text = label, FontSize = bold ? 15 : 14, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 24, 0),
                                  Width = 180, TextTrimming = TextTrimming.CharacterEllipsis };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (picture != null)
        {
            // An account picture: a small circle, like Windows 10's.
            sp.Children.Add(new Border
            {
                Width = 48, Height = 48,
                Child = new Image { Source = picture, Width = 24, Height = 24, Stretch = Stretch.UniformToFill,
                                    Clip = new EllipseGeometry(new Point(12, 12), 12, 12) },
            });
        }
        else
        {
            var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
                                       Width = 48, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            sp.Children.Add(icon);
        }
        sp.Children.Add(lbl);
        var b = new Button { Content = sp, Height = 48, Padding = new Thickness(0), Style = (Style)FindResource("Flat"), ToolTip = label };
        if (onClick != null) b.Click += (_, _) => onClick();
        return b;
    }

    // ---------- the strip opening over the list ----------

    const double RailClosedWidth = 48, RailOpenWidth = 256;
    bool _railPinned;                 // opened by the hamburger or a panel (otherwise it follows the pointer)
    DispatcherTimer? _railHover;

    void ToggleRail()
    {
        bool open = !_railExpanded;
        _railPinned = open;
        SetRail(open);
    }

    /// <summary>Resting the pointer on the strip opens it after a moment, as in Windows 10.</summary>
    void OnRailEnter(object s, MouseEventArgs e)
    {
        if (_railExpanded || _drag || _movingGroup != null || e.LeftButton == MouseButtonState.Pressed) return;
        if (_railHover == null)
        {
            _railHover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _railHover.Tick += (_, _) => { _railHover.Stop(); if (Rail.IsMouseOver && !_drag && IsVisible) SetRail(true); };
        }
        _railHover.Start();
    }

    void OnRailLeave(object s, MouseEventArgs e)
    {
        _railHover?.Stop();
        if (_railExpanded && !_railPinned && _panel == RailPanelKind.None) SetRail(false);
    }

    void SetRail(bool open, bool animate = true)
    {
        _railExpanded = open;
        if (!open) { _railPinned = false; CloseRailPanel(); }
        if (open) Rail.SetResourceReference(Border.BackgroundProperty, "RailOpenBg");
        else Rail.Background = Brushes.Transparent;
        Rail.Effect = open ? new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Direction = 0, Opacity = 0.45, Color = Colors.Black } : null;
        ListColumn.Effect = open ? new BlurEffect { Radius = 18 } : null;   // the list behind the open strip, as Windows 10's acrylic
        double to = open ? RailOpenWidth : RailClosedWidth;
        if (animate)
            Rail.BeginAnimation(WidthProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(open ? 160 : 110))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else { Rail.BeginAnimation(WidthProperty, null); Rail.Width = to; }
    }

    // ---------- the Power and account panels (inside the strip, opened by a left-click) ----------

    enum RailPanelKind { None, Power, Account }
    RailPanelKind _panel;

    /// <summary>Windows 10's power choices (Sleep, Hibernate, Shut down, Restart, "Update and ..." while updates wait), with
    /// Lock on top (decision D4).</summary>
    void ShowPowerPanel()
    {
        if (_panel == RailPanelKind.Power) { CloseRailPanel(); return; }
        var items = new List<(string? Glyph, string Text, ImageSource? Picture, Action Act)>();
        if (Session.ShowLock) items.Add(("\ue72e", "Lock", null, () => { Hide(); Session.Lock(); }));
        foreach (var c in Session.PowerChoices()) { var choice = c; items.Add((choice.Glyph, choice.Text, null, () => { Hide(); choice.Act(); })); }
        OpenRailPanel(RailPanelKind.Power, items, _powerButton!);
    }

    /// <summary>Windows 10's account choices: account settings, Lock (decision D4), Sign out, then the other accounts (or Switch user).</summary>
    void ShowAccountPanel()
    {
        if (_panel == RailPanelKind.Account) { CloseRailPanel(); return; }
        // Text only, as Windows 10's account menu was (other accounts show their picture).
        var items = new List<(string? Glyph, string Text, ImageSource? Picture, Action Act)>
        {
            (null, "Change account settings", null, () => OpenSettings("ms-settings:yourinfo")),
        };
        if (Session.ShowLock) items.Add((null, "Lock", null, () => { Hide(); Session.Lock(); }));
        items.Add((null, "Sign out", null, () => { Hide(); Session.SignOut(); }));
        var others = Session.OtherUsers();
        if (others.Count == 0) items.Add((null, "Switch user", null, () => { Hide(); Session.SwitchUser(); }));
        foreach (var (sid, name) in others)   // picking one goes to the sign-in screen, as in Windows 10
            items.Add(("\ue77b", name, Session.PictureOf(sid), () => { Hide(); Session.SwitchUser(); }));
        OpenRailPanel(RailPanelKind.Account, items, _userButton!);
    }

    /// <summary>Opens a panel in the strip, just above the button it belongs to, and keeps the strip open while it is up.</summary>
    void OpenRailPanel(RailPanelKind kind, List<(string? Glyph, string Text, ImageSource? Picture, Action Act)> items, Button anchor)
    {
        RailPanelItems.Children.Clear();
        foreach (var (glyph, text, picture, act) in items)
        {
            var row = RailButton(glyph ?? "", text, act, picture);
            row.Height = 40;
            RailPanelItems.Children.Add(row);
        }
        _panel = kind;
        _railPinned = true;
        SetRail(true);
        Rail.UpdateLayout();
        double anchorTop = anchor.TranslatePoint(new Point(0, 0), Rail).Y;
        RailPanel.Margin = new Thickness(0, 0, 0, Math.Max(0, Rail.ActualHeight - anchorTop));
        RailPanel.Visibility = Visibility.Visible;
    }

    void CloseRailPanel()
    {
        RailPanel.Visibility = Visibility.Collapsed;
        RailPanelItems.Children.Clear();
        _panel = RailPanelKind.None;
    }

    /// <summary>A press anywhere else closes an open panel, and a press outside the open strip closes the strip.</summary>
    void OnRootPreviewDown(object s, MouseButtonEventArgs e)
    {
        if (_panel != RailPanelKind.None && !RailPanel.IsMouseOver &&
            !(_panel == RailPanelKind.Power ? _powerButton : _userButton)!.IsMouseOver)
            CloseRailPanel();
        if (_railExpanded && !Rail.IsMouseOver) SetRail(false);
    }

    // ---------- data ----------

    public async Task PreloadAsync()
    {
        // Desktop apps first (fast), so the menu is usable at once; Store apps are merged in when their
        // slower enumeration finishes.
        _all = await Task.Run(AppEntry.ScanShortcuts);
        LoadTiles(final: false);
        ShowList(Search.Text.Trim());

        var shortcuts = _all;
        var targets = Task.Run(() => AppEntry.ResolveTargets(shortcuts));   // lets "regedit"-style searches find apps
        var store = await Task.Run(AppEntry.ScanStoreApps);
        _all = AppEntry.Merge(_all, store);
        await targets;

        if (!_state.Seeded)
        {
            // First run: pin a few familiar apps so the tile area isn't empty.
            string[] seed = { "Microsoft Edge", "Notepad", "Paint", "Calculator", "Snipping Tool", "Task Manager", "Command Prompt", "Windows PowerShell" };
            foreach (var n in seed)
                if (_all.FirstOrDefault(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } a)
                    _state.Tiles.Add(new State.TileRef(a.Path, _state.Tiles.Count < 2 ? "W" : "M"));
            _state.Seeded = true; _state.Save();
        }

        if (AppList.NoteFirstSeen(_state.FirstSeen, _all, DateTime.Now, CreatedAt)) _state.Save();
        LoadTiles(final: true);
        ShowList(Search.Text.Trim());
        MemoryTrim.Schedule(3000);   // startup is over: drop what the scans needed

        // From now on the list follows programs being installed and removed (no restart needed).
        _watcher = new AppWatcher(AppEntry.ProgramRoots);
        _watcher.Changed += (shortcuts, store) => Dispatcher.BeginInvoke(() => RefreshApps(shortcuts, store));
    }

    static DateTime? CreatedAt(string path)
    {
        try { return File.GetCreationTime(path); } catch { return null; }
    }

    /// <summary>
    /// Rescans what changed (shortcuts, Store apps or both) in the background. Each shortcut that was already known keeps
    /// the program it points to, so searching by program name ("regedit") keeps working.
    /// </summary>
    async void RefreshApps(bool shortcuts, bool store)
    {
        if (_refreshing) { _againShortcuts |= shortcuts; _againStore |= store; return; }
        _refreshing = true;
        try
        {
            var old = _all;
            var desktop = old.Where(a => !a.IsStore).ToList();
            if (shortcuts)
            {
                desktop = await Task.Run(AppEntry.ScanShortcuts);
                var known = old.Where(a => a.Target != null).GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.First().Target, StringComparer.OrdinalIgnoreCase);
                foreach (var a in desktop) if (known.TryGetValue(a.Path, out var t)) a.Target = t;
                var fresh = desktop;
                await Task.Run(() => AppEntry.ResolveTargets(fresh));   // only the new ones still need it
            }
            var storeApps = store ? await Task.Run(AppEntry.ScanStoreApps) : old.Where(a => a.IsStore).ToList();
            ApplyApps(AppEntry.Merge(desktop, storeApps));
        }
        catch (Exception ex) { CrashLog.Write("refreshing the app list", ex); }
        finally { _refreshing = false; }
        if (_againShortcuts || _againStore)
        {
            var (s, t) = (_againShortcuts, _againStore);
            _againShortcuts = _againStore = false;
            RefreshApps(s, t);
        }
    }

    /// <summary>Puts a new app list in place, or keeps it until the menu is closed so nothing moves under the user.</summary>
    void ApplyApps(List<AppEntry> apps)
    {
        if (IsVisible || _drag) { _pendingApps = apps; return; }
        _all = apps;
        if (AppList.NoteFirstSeen(_state.FirstSeen, _all, DateTime.Now, CreatedAt)) _state.Save();
        LoadTiles(final: true);   // tiles of programs that were removed go
        ShowList("");
    }

    /// <summary>
    /// Builds the tile groups from the saved state. Before the Store scan has finished (<paramref name="final"/>
    /// false) Store tiles whose app is not known yet are left out rather than shown with a placeholder name.
    /// </summary>
    void LoadTiles(bool final)
    {
        if (_drag) return;   // never rebuild the tiles under the user's cursor

        if (_state.Groups.Count == 0 && _state.Tiles.Count > 0)   // migrate the old single-list format
        {
            _state.Groups.Add(new State.GroupRef("Pinned", _state.Tiles.ToList()));
            _state.Tiles.Clear();
        }

        Tile? AppTile(State.TileRef t)
        {
            bool store = t.Path.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase);
            if (!store && !File.Exists(t.Path)) return null;
            var app = _all.FirstOrDefault(a => a.Path.Equals(t.Path, StringComparison.OrdinalIgnoreCase));
            if (app == null)
            {
                if (store && !final) return null;
                app = store ? new AppEntry { Name = AppEntry.StoreDisplayName(t.Path), Path = t.Path } : AppEntry.ForFile(t.Path);
            }
            return new Tile { App = app, Size = t.Size };
        }

        _groups.Clear();
        foreach (var gr in _state.Groups)
        {
            var g = new TileGroup { Name = gr.Name };
            foreach (var t in gr.Tiles)
            {
                if (t.Items == null) { if (AppTile(t) is { } tile) g.Tiles.Add(tile); continue; }
                // A folder: its apps that are still there. With one left it shows as that tile.
                var inside = t.Items.Select(AppTile).Where(x => x != null).Select(x => x!).ToList();
                if (inside.Count == 1) { g.Tiles.Add(inside[0]); continue; }
                if (inside.Count == 0) continue;
                var folder = new TileFolder { Name = t.Name ?? "" };
                foreach (var x in inside) folder.Tiles.Add(x);
                g.Tiles.Add(new Tile { App = Tile.FolderPlaceholder, Folder = folder, Size = t.Size });
            }
            if (g.Tiles.Count > 0) _groups.Add(g);
        }
        EmptyTiles.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SaveTiles()
    {
        static State.TileRef Ref(Tile t) => t.IsFolder
            ? new State.TileRef("", t.Size, t.Folder!.Tiles.Select(c => new State.TileRef(c.App.Path, c.Size)).ToList(), t.Folder.Name)
            : new State.TileRef(t.App.Path, t.Size);
        _state.Groups = _groups.Where(g => g.Tiles.Count > 0)
            .Select(g => new State.GroupRef(g.Name, g.Tiles.Select(Ref).ToList())).ToList();
        _state.Tiles.Clear();
        EmptyTiles.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _state.Save();
    }

    /// <summary>
    /// The full list (Recently added, Most used, A–Z with folders) or, with search text, the matching apps: never
    /// "Recently added" or folders there, as in Windows 10.
    /// </summary>
    void ShowList(string q)
    {
        HideJumpGrid();
        var rows = q.Length == 0
            ? AppList.Build(_all, _state.Usage, _state.FirstSeen, DateTime.Now,
                            Personalization.ShowRecentlyAdded, Personalization.ShowMostUsed, _openFolders)
            : FindApps(q).Select(a => new Row { App = a }).ToList();
        _rows = new ObservableCollection<Row>(rows);
        Apps.ItemsSource = _rows;
        if (q.Length > 0) Apps.SelectedItem = _rows.FirstOrDefault();
    }

    /// <summary>Opens or closes a folder in place: its apps appear (or go) right below it, nothing else moves.</summary>
    void ToggleFolder(Row folderRow)
    {
        var f = folderRow.Folder!;
        int at = _rows.IndexOf(folderRow);
        if (at < 0) return;
        if (f.IsOpen)
        {
            while (at + 1 < _rows.Count && _rows[at + 1].Parent == f) _rows.RemoveAt(at + 1);
            _openFolders.Remove(f.Name);
        }
        else
        {
            foreach (var child in AppList.ChildRows(f).Reverse()) _rows.Insert(at + 1, child);
            _openFolders.Add(f.Name);
        }
        f.IsOpen = !f.IsOpen;
    }

    // ---------- the A–Z jump grid (click a letter header) ----------

    void ShowJumpGrid()
    {
        JumpKeys.ItemsSource = AppList.JumpKeys(_rows);
        Apps.Visibility = Visibility.Hidden;
        JumpGrid.Visibility = Visibility.Visible;
    }

    void HideJumpGrid()
    {
        if (JumpGrid.Visibility != Visibility.Visible) return;
        JumpGrid.Visibility = Visibility.Collapsed;
        Apps.Visibility = Visibility.Visible;
    }

    void OnJumpKey(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is not string key) return;
        HideJumpGrid();
        var header = _rows.FirstOrDefault(r => r.IsHeader && r.Key == key);
        if (header == null) return;
        int index = _rows.IndexOf(header);
        // The list scrolls by whole rows: put the header at the very top, as Windows 10 does.
        Dispatcher.BeginInvoke(() =>
        {
            Apps.UpdateLayout();
            FindScroller(Apps)?.ScrollToVerticalOffset(index);
        }, DispatcherPriority.Loaded);
    }

    void OnJumpBackground(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == JumpGrid) HideJumpGrid();   // a click between the keys closes the grid
    }

    static ScrollViewer? FindScroller(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScroller(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }

    // ---------- show / hide ----------

    /// <summary>RestoreWin10's bar is docked at the top of the screen (preview mode): the menu opens from there.</summary>
    internal static bool BarAtTop;

    const double DefaultShare = 0.62;   // of the work area's height, roughly where Windows 10 starts out
    const double MinMenuHeight = 400;

    Screens.Info? _screen;   // where the menu is (or was last) open
    bool _top;

    /// <summary>The screen Start is open on (null while it is closed). That screen's taskbar shows its normal look meanwhile.</summary>
    public string? OpenOn => IsVisible ? _screen?.Device : null;
    /// <summary>Start opened, closed, or moved to another screen.</summary>
    public event Action? OpenChanged;

    /// <summary>Win key, tray icon, second launch: closes the menu, or opens it on the screen the mouse is on.</summary>
    public void Toggle()
    {
        if (IsVisible) { Hide(); return; }
        Toggle(Screens.FromCursor(), BarAtTop);
    }

    /// <summary>
    /// Opens the menu on <paramref name="screen"/>, against its taskbar edge. Open on that screen already: closes it.
    /// Open on another screen: moves there (Start was clicked on a different monitor's taskbar).
    /// </summary>
    internal void Toggle(Screens.Info screen, bool top)
    {
        if (IsVisible)
        {
            if (_screen?.Device == screen.Device) Hide(); else { Place(screen, top); OpenChanged?.Invoke(); }
            return;
        }
        // A taskbar click deactivates (hides) us just before the toggle; don't instantly reopen.
        if ((DateTime.Now - _hiddenAt).TotalMilliseconds < 150) return;
        Search.Text = "";
        Place(screen, top);
        Show();
        Place(screen, top);   // again now it is visible, in case arriving on that screen changed our DPI
        // Opened by the Windows key, another program still owns the keyboard and Windows may refuse to hand it over;
        // then take it the way the taskbar's buttons do, so typing goes into Start's search as in Windows 10.
        if (!Activate()) Win.Activate(Hwnd);
        Search.Focus();
        Apps.ScrollIntoView(Apps.Items.Count > 0 ? Apps.Items[0] : null!);
    }

    nint Hwnd => new System.Windows.Interop.WindowInteropHelper(this).Handle;

    /// <summary>
    /// Sizes and positions the menu in physical pixels from the monitor's own work area: its height a share of that
    /// work area (so it fits any resolution or scaling), its corner on the work area's bottom-left (top-left when the
    /// bar is at the top), which is right above (below) the taskbar of that screen.
    /// </summary>
    void Place(Screens.Info screen, bool top)
    {
        _screen = screen; _top = top;
        var wa = Taskbar.UsableArea(screen);
        // Onto the right screen first, so a different DPI there is in effect before the size is worked out.
        Win.SetWindowPos(Hwnd, 0, wa.Left, wa.Top, 0, 0, Win.SWP_NOSIZE | Win.SWP_NOZORDER | Win.SWP_NOACTIVATE);
        double workH = (wa.Bottom - wa.Top) / screen.Scale;
        double share = _state.StartHeight > 0 ? _state.StartHeight : DefaultShare;
        Height = Math.Round(Math.Clamp(workH * share, Math.Min(MinMenuHeight, workH), workH));
        UpdateLayout();
        Anchor();
    }

    /// <summary>Keeps the menu's taskbar-side edge on the work area's edge (after a resize, the other edge moves).</summary>
    void Anchor()
    {
        if (_screen == null) return;
        var wa = Taskbar.UsableArea(_screen);
        Win.GetWindowRect(Hwnd, out var rc);
        int y = _top ? wa.Top : wa.Bottom - (rc.Bottom - rc.Top);
        Win.SetWindowPos(Hwnd, 0, wa.Left, y, 0, 0, Win.SWP_NOSIZE | Win.SWP_NOZORDER | Win.SWP_NOACTIVATE);
        ResizeGrip.VerticalAlignment = _top ? VerticalAlignment.Bottom : VerticalAlignment.Top;
    }

    // ----- resizing by the free edge, as in Windows 10 (the height is remembered as a share of the screen) -----

    bool _resizing;
    int _resizeFromY;
    double _resizeFromH;

    void OnGripDown(object s, MouseButtonEventArgs e)
    {
        if (_screen == null) return;
        Win.GetCursorPos(out var p);
        _resizing = true; _resizeFromY = p.Y; _resizeFromH = ActualHeight;
        ResizeGrip.CaptureMouse();
        e.Handled = true;
    }

    void OnGripMove(object s, MouseEventArgs e)
    {
        if (!_resizing || _screen == null) return;
        Win.GetCursorPos(out var p);
        double dy = (p.Y - _resizeFromY) / _screen.Scale;
        double workH = (Taskbar.UsableArea(_screen).Bottom - Taskbar.UsableArea(_screen).Top) / _screen.Scale;
        double h = _top ? _resizeFromH + dy : _resizeFromH - dy;
        Height = Math.Round(Math.Clamp(h, Math.Min(MinMenuHeight, workH), workH));
        UpdateLayout();
        Anchor();
        e.Handled = true;
    }

    void OnGripUp(object s, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        ResizeGrip.ReleaseMouseCapture();   // EndResize runs from LostMouseCapture
        e.Handled = true;
    }

    void OnGripLost(object s, MouseEventArgs e)
    {
        if (!_resizing || _screen == null) return;
        _resizing = false;
        double workH = (Taskbar.UsableArea(_screen).Bottom - Taskbar.UsableArea(_screen).Top) / _screen.Scale;
        _state.StartHeight = Math.Round(ActualHeight / workH, 3);
        _state.Save();
    }

    /// <summary>Put the caret back in the search field with its text selected, so typing replaces the old query.</summary>
    public void FocusSearch() { Activate(); Search.Focus(); Search.SelectAll(); }

    public void Dismiss() => Hide();

    /// <summary>Apps matching <paramref name="q"/>, best matches first (used by the taskbar search pane).</summary>
    public IReadOnlyList<AppEntry> Find(string q, int max) => FindApps(q).Take(max).ToList();

    /// <summary>
    /// Apps whose name or executable name matches <paramref name="q"/>, best matches first; then, if the text is
    /// something the Run box could start and no listed app already is that program, that program too.
    /// </summary>
    IEnumerable<AppEntry> FindApps(string q)
    {
        var hits = _all.Where(a => a.Matches(q))
                       .OrderBy(a => a.Rank(q)).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (AppEntry.FromCommand(q) is { } cmd &&
            !hits.Any(h => h.Name.Equals(cmd.Name, StringComparison.OrdinalIgnoreCase) ||
                           (h.Target != null && h.Target.Equals(cmd.Path, StringComparison.OrdinalIgnoreCase))))
            hits.Add(cmd);
        return hits;
    }

    /// <summary>Launches an app exactly as a click in the menu would (usage count, menu closes).</summary>
    public void Launch(AppEntry a) => LaunchApp(a);

    new void Hide()
    {
        if (_resizing) ResizeGrip.ReleaseMouseCapture();
        if (_movingGroup != null) EndGroupMove();
        if (_drag || _pressed != null) CancelDrag();
        _railHover?.Stop();
        SetRail(false, animate: false);   // the strip starts closed next time
        foreach (var g in _groups) CloseFolder(g);   // and folders closed
        FinishLandings();
        _hiddenAt = DateTime.Now;
        base.Hide();
        HideJumpGrid();
        if (Search.Text.Length > 0) Search.Text = "";   // (rebuilds the list)
        if (_pendingApps != null)                       // programs installed or removed while the menu was open
        {
            var apps = _pendingApps;
            _pendingApps = null;
            _openFolders.Clear();
            ApplyApps(apps);
        }
        else if (_openFolders.Count > 0) { _openFolders.Clear(); ShowList(""); }   // folders start closed next time
        MemoryTrim.Schedule();
    }

    void OnDeactivated(object? s, EventArgs e) { if (IsVisible && !_popupOpen) Hide(); }

#if DEV_BUILD
    /// <summary>Memory probe only: opens the menu off-screen without taking focus, lets it render, closes it.</summary>
    internal async Task ProbeShowAsync()
    {
        Place(Screens.Primary, top: false);
        Win.SetWindowPos(Hwnd, 0, -32000, -32000, 0, 0, 0x1 | 0x4 | 0x10);   // NOSIZE | NOZORDER | NOACTIVATE
        ShowActivated = false;
        Show();
        await Task.Delay(1200);
        Hide();
        await Task.Delay(300);
    }

    /// <summary>Memory probe only: a search typed into the off-screen menu, then cleared.</summary>
    internal async Task ProbeSearchAsync(string text)
    {
        Place(Screens.Primary, top: false);
        Win.SetWindowPos(Hwnd, 0, -32000, -32000, 0, 0, 0x1 | 0x4 | 0x10);
        ShowActivated = false;
        Show();
        Search.Text = text;
        await Task.Delay(1200);
        Hide();
        await Task.Delay(300);
    }
#endif

    /// <summary>A hidden window runs no animations: finish any drop still gliding home (tile shown, layout saved).</summary>
    void FinishLandings()
    {
        foreach (var (t, g) in _landing.ToList()) FinishDrag(t, g);
        if (!_drag) DragCanvas.Children.Clear();
    }

    void OpenPopup(ContextMenu m)
    {
        _popupOpen = true;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        // Belong to this window. (A menu with no placement target gets a window of its own, which the outside-click
        // guard took for "outside": every item click closed the menu instead - Unpin and Resize did nothing.)
        if (m.PlacementTarget == null)
        {
            m.PlacementTarget = Root;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        }
        PopupGuard.Watch(m, PopupGuard.MenuOf(hwnd, m), () => m.IsOpen = false);
        m.Closed += (_, _) => { PopupGuard.Unwatch(m); _popupOpen = false; if (IsVisible) Activate(); };
        m.IsOpen = true;
    }

    MenuItem MenuItemFor(string text, Action a, string? glyph = null, ImageSource? picture = null)
    {
        var mi = new MenuItem { Header = text };
        if (picture != null)
            mi.Icon = new Image { Source = picture, Width = 16, Height = 16, Clip = new EllipseGeometry(new Point(8, 8), 8, 8) };
        else if (glyph != null)
        {
            var g = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14 };
            g.SetResourceReference(TextBlock.ForegroundProperty, "MenuFg");
            mi.Icon = g;
        }
        mi.Click += (_, _) => a();
        return mi;
    }

    void OnKey(object s, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox tb && tb != Search && e.Key == Key.Enter)   // finish renaming a group
        {
            Keyboard.ClearFocus(); Search.Focus(); e.Handled = true; return;
        }
        switch (e.Key)
        {
            case Key.Escape when _panel != RailPanelKind.None: CloseRailPanel(); e.Handled = true; break;
            case Key.Escape when _railExpanded: SetRail(false); e.Handled = true; break;
            case Key.Escape when JumpGrid.Visibility == Visibility.Visible: HideJumpGrid(); e.Handled = true; break;
            case Key.Escape: Hide(); e.Handled = true; break;
            case Key.Enter: LaunchSelected(); e.Handled = true; break;
            case Key.Down when Search.IsFocused:
                Apps.Focus();
                if (Apps.SelectedIndex < 0) Apps.SelectedItem = _rows.FirstOrDefault(r => r.IsApp);
                e.Handled = true; break;
        }
    }

    void OnSearchChanged(object s, TextChangedEventArgs e)
    {
        var q = Search.Text.Trim();
        Hint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowList(q);
    }

    // ---------- launching ----------

    // Launches go through Launcher (full paths, checked targets); these close Start first.
    void OpenFolder(string path) { Hide(); Launcher.Folder(path); }
    void OpenSettings(string page) { Hide(); Launcher.Settings(page); }

    void LaunchApp(AppEntry a)
    {
        _state.Usage[a.Path] = _state.Usage.GetValueOrDefault(a.Path) + 1;
        AppList.NoteLaunched(_state.FirstSeen, a.Path, DateTime.Now);   // started: it leaves "Recently added"
        _state.Save();
        Hide();
        a.Launch();
    }

    void LaunchSelected()
    {
        if (Apps.SelectedItem is Row { App: { } a }) { LaunchApp(a); return; }
        if (Apps.SelectedItem is Row { IsFolder: true } folder) { ToggleFolder(folder); return; }
        // Enter starts the best match: an app, or a command the Run box would recognise ("regedit"). Text that matches
        // nothing is never handed to Windows to run (decision D5, threat model F-10).
        var q = Search.Text.Trim();
        if (q.Length > 0 && FindApps(q).FirstOrDefault() is { } hit) LaunchApp(hit);
    }

    // ---------- app list interaction ----------

    Row? RowAt(object src) =>
        ItemsControl.ContainerFromElement(Apps, (DependencyObject)src) is ListBoxItem it ? it.DataContext as Row : null;

    void OnAppClick(object s, MouseButtonEventArgs e)
    {
        // The release that ends a tile drag over the list is the drop, never a launch.
        if (_drag || (DateTime.Now - _dragEndedAt).TotalMilliseconds < 300) return;
        switch (RowAt(e.OriginalSource))
        {
            case { App: { } a }: LaunchApp(a); break;                       // Windows 10 launches on single click
            case { IsFolder: true } folder: ToggleFolder(folder); e.Handled = true; break;
            case { IsInteractive: true, IsHeader: true }: ShowJumpGrid(); e.Handled = true; break;   // a letter: the A–Z grid
        }
    }

    void OnAppRightClick(object s, MouseButtonEventArgs e)
    {
        if (RowAt(e.OriginalSource) is not { App: { } a }) return;
        var m = new ContextMenu();
        m.Items.Add(IsPinned(a.Path)
            ? MenuItemFor("Unpin from Start", () => RemoveTile(AppTiles().First(t => t.App.Path.Equals(a.Path, StringComparison.OrdinalIgnoreCase))))
            : MenuItemFor("Pin to Start", () => AddTile(a)));
        if (TaskbarPinEntry(a) is { } tp) m.Items.Add(tp);
        m.Items.Add(new Separator());
        if (!a.IsStore)
        {
            m.Items.Add(MenuItemFor("Run as administrator", () => RunAsAdmin(a)));
            m.Items.Add(MenuItemFor("Open file location", () => OpenLocation(a)));
            m.Items.Add(MenuItemFor("Properties", () => ShowProperties(a)));
            m.Items.Add(new Separator());
        }
        m.Items.Add(MenuItemFor("Uninstall", () => OpenSettings("ms-settings:appsfeatures")));
        OpenPopup(m);
    }

    void RunAsAdmin(AppEntry a)
    {
        Hide();
        Launcher.Item(a.Path, verb: "runas");   // the UAC prompt names the program and its publisher
    }

    void ShowProperties(AppEntry a)
    {
        Hide();
        Launcher.Item(a.Path, verb: "properties");
    }

    void OpenLocation(AppEntry a)
    {
        Hide();
        Launcher.ShowInFolder(a.Path);
    }

    // ---------- tiles: groups, drag and drop ----------

    Tile TileOf(object sender) => (Tile)((FrameworkElement)sender).DataContext;
    TileGroup? GroupOf(Tile t) => _groups.FirstOrDefault(g => g.Tiles.Contains(t));
    /// <summary>Every app tile, the ones inside folders included (folder tiles themselves are not apps).</summary>
    IEnumerable<Tile> AppTiles() => _groups.SelectMany(g => g.Tiles).SelectMany(t => t.IsFolder ? t.Folder!.Tiles : (IEnumerable<Tile>)new[] { t });
    /// <summary>The folder tile a tile is inside, or null.</summary>
    Tile? FolderTileOf(Tile child) => _groups.SelectMany(g => g.Tiles).FirstOrDefault(t => t.IsFolder && t.Folder!.Tiles.Contains(child));
    bool IsPinned(string path) => AppTiles().Any(t => t.App.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    void AddTile(AppEntry a)
    {
        if (_groups.Count == 0) _groups.Add(new TileGroup());
        _groups[0].Tiles.Add(new Tile { App = a, Size = "M" });
        SaveTiles();
    }

    void RemoveTile(Tile t)
    {
        if (GroupOf(t) is { } g)
        {
            if (g.OpenFolder == t) CloseFolder(g);
            g.Tiles.Remove(t);   // a folder tile goes with everything in it
            if (g.Tiles.Count == 0) _groups.Remove(g);
        }
        else if (FolderTileOf(t) is { } f) TakeOut(t, f);
        SaveTiles();
    }

    // ---------- folder tiles (Windows 10: drop a tile onto another and they share one tile) ----------

    /// <summary>Opens a folder tile (its tiles show in a band below its row), or closes it; only one is open at a time.</summary>
    void ToggleTileFolder(Tile folderTile)
    {
        var g = GroupOf(folderTile);
        if (g == null) return;
        bool open = g.OpenFolder != folderTile;
        foreach (var other in _groups) CloseFolder(other);
        if (open) { g.OpenFolder = folderTile; folderTile.IsOpen = true; }
    }

    static void CloseFolder(TileGroup g)
    {
        if (g.OpenFolder == null) return;
        g.OpenFolder.IsOpen = false;
        g.OpenFolder = null;
    }

    /// <summary>Takes a tile out of its folder. A folder left with one tile becomes that tile again; an empty one goes.</summary>
    void TakeOut(Tile child, Tile folderTile)
    {
        var tiles = folderTile.Folder!.Tiles;
        tiles.Remove(child);
        if (tiles.Count > 1 || GroupOf(folderTile) is not { } g) return;
        CloseFolder(g);
        int i = g.Tiles.IndexOf(folderTile);
        if (tiles.Count == 1) { var last = tiles[0]; tiles.Clear(); g.Tiles[i] = last; }
        else g.Tiles.RemoveAt(i);
    }

    /// <summary>Drops <paramref name="tile"/> onto <paramref name="target"/>: into it if it is a folder, else both become a new folder in the target's place.</summary>
    void JoinFolder(Tile target, Tile tile)
    {
        if (target.IsFolder) { target.Folder!.Tiles.Add(tile); return; }
        if (GroupOf(target) is not { } g) return;
        var folder = new TileFolder();
        folder.Tiles.Add(target);
        folder.Tiles.Add(tile);
        g.Tiles[g.Tiles.IndexOf(target)] = new Tile { App = Tile.FolderPlaceholder, Folder = folder, Size = target.Size };
    }

    /// <summary>The collection a tile sits in: its group's, or its folder's.</summary>
    IList<Tile>? HomeOf(Tile t) => (IList<Tile>?)GroupOf(t)?.Tiles ?? FolderTileOf(t)?.Folder!.Tiles;

    /// <summary>"Pin to taskbar" / "Unpin from taskbar" for apps that start a real executable, and for Store / packaged
    /// apps (pinned by their app id, which survives their updates).</summary>
    MenuItem? TaskbarPinEntry(AppEntry a)
    {
        var exe = a.IsStore ? (Launcher.IsLaunchable(a.Path) ? a.Path : null) : a.TargetExe();
        if (exe == null) return null;
        bool pinned = _state.TaskbarPins.Contains(exe, StringComparer.OrdinalIgnoreCase);
        return MenuItemFor(pinned ? "Unpin from taskbar" : "Pin to taskbar", () =>
        {
            if (pinned) _state.UnpinFromTaskbar(exe); else _state.PinToTaskbar(exe, a.IsShortcut ? a.Path : null);
            _state.Save();   // the taskbar notices on its next refresh
        });
    }

    void OnGroupNameLostFocus(object s, RoutedEventArgs e) => SaveTiles();

    // ---------- moving a whole group by its handle ----------

    TileGroup? _movingGroup;

    void OnGroupHandleDown(object s, MouseButtonEventArgs e)
    {
        if (_drag || _pressed != null || (s as FrameworkElement)?.DataContext is not TileGroup g) return;
        e.Handled = true;              // not a click on the name box
        _movingGroup = g;
        g.IsMoving = true;
        Mouse.Capture(Root, CaptureMode.Element);   // last: taking capture re-sends a move, which must find the move set up
    }

    /// <summary>
    /// The group swaps places with a neighbour once the pointer passes that neighbour's middle. After a swap the pointer is
    /// on the other side of the neighbour's new middle, so groups of different heights cannot bounce back and forth.
    /// </summary>
    void MoveGroup(Point inGroups)
    {
        var g = _movingGroup!;
        int i = _groups.IndexOf(g);
        (double Top, double Mid) Span(int index)
        {
            if (Groups.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement c) return (double.NaN, double.NaN);
            double top = c.TranslatePoint(new Point(0, 0), Groups).Y;
            return (top, top + c.ActualHeight / 2);
        }
        if (i + 1 < _groups.Count && inGroups.Y > Span(i + 1).Mid) _groups.Move(i, i + 1);
        else if (i > 0 && inGroups.Y < Span(i - 1).Mid) _groups.Move(i, i - 1);
        UpdateAutoScroll(Mouse.GetPosition(Root));   // near the top or bottom edge the tile area scrolls
    }

    void EndGroupMove()
    {
        var g = _movingGroup;
        _movingGroup = null;           // first: releasing capture re-sends a move, which must find no move in progress
        _autoScroll?.Stop();
        if (g != null) g.IsMoving = false;
        if (Mouse.Captured == Root) Mouse.Capture(null);
        if (g != null) SaveTiles();
    }

    void OnTileDown(object s, MouseButtonEventArgs e)
    {
        _pressedEl = (FrameworkElement)s;
        _pressed = TileOf(s);
        _downPos = e.GetPosition(Root);
        _grab = e.GetPosition(_pressedEl);
        Mouse.Capture(Root, CaptureMode.SubTree);   // keep receiving moves even outside the tile
    }

    /// <summary>A press on a list row: remembered so that moving the mouse can turn it into a drag.</summary>
    void OnAppDown(object s, MouseButtonEventArgs e)
    {
        _listApp = RowAt(e.OriginalSource)?.App;
        _downPos = e.GetPosition(Root);
    }

    bool Moved(Point pt) =>
        Math.Abs(pt.X - _downPos.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(pt.Y - _downPos.Y) >= SystemParameters.MinimumVerticalDragDistance;

    void OnRootMove(object s, MouseEventArgs e)
    {
        var pt = e.GetPosition(Root);
        if (_movingGroup != null)
        {
            if (e.LeftButton != MouseButtonState.Pressed) EndGroupMove(); else MoveGroup(e.GetPosition(Groups));
            return;
        }
        if (!_drag)
        {
            if (_pressed != null)
            {
                if (e.LeftButton != MouseButtonState.Pressed) { Release(); return; }
                if (!Moved(pt)) return;
                BeginDrag();
            }
            else if (_listApp != null)
            {
                if (e.LeftButton != MouseButtonState.Pressed) { _listApp = null; return; }
                if (!Moved(pt)) return;
                BeginAppDrag();
            }
            else return;
        }
        UpdateDrag(pt);
    }

    void OnRootUp(object s, MouseButtonEventArgs e)
    {
        if (_movingGroup != null) { EndGroupMove(); return; }
        _listApp = null;
        if (_drag) { EndDrag(); return; }
        if (_pressed == null) return;

        var tile = _pressed; var el = _pressedEl; var pt = e.GetPosition(Root);
        Release();
        // A plain click launches (a folder tile opens or closes), as long as the button came up over the tile it went down on.
        if (el != null && new Rect(el.TranslatePoint(new Point(), Root), el.RenderSize).Contains(pt))
        {
            if (tile.IsFolder) ToggleTileFolder(tile); else LaunchApp(tile.App);
        }
    }

    void Release()
    {
        _pressed = null; _pressedEl = null;   // first: letting go of the mouse re-sends a move, which must find nothing pressed
        if (Mouse.Captured == Root) Mouse.Capture(null);
    }

    void BeginDrag()
    {
        _dragFolderTile = FolderTileOf(_pressed!);   // a tile dragged out of an open folder leaves it once it reaches the group
        StartDrag(_pressed!, GroupOf(_pressed!));
    }

    /// <summary>A list row is being dragged: an existing tile for that app moves; otherwise a new medium tile is born.</summary>
    void BeginAppDrag()
    {
        var app = _listApp!; _listApp = null;
        var existing = AppTiles().FirstOrDefault(t => t.App.Path.Equals(app.Path, StringComparison.OrdinalIgnoreCase));
        Tile tile; TileGroup? from = null;
        if (existing != null) { tile = existing; from = GroupOf(existing); _dragFolderTile = FolderTileOf(existing); }
        else { tile = new Tile { App = app, Size = "M" }; _appDrag = true; }
        _grab = new Point(tile.Width / 2, tile.Height / 2);   // the ghost hangs centred under the cursor
        StartDrag(tile, from);
    }

    void StartDrag(Tile tile, TileGroup? from)
    {
        if (tile.IsFolder && from != null && from.OpenFolder == tile) CloseFolder(from);   // a folder travels closed
        _dragTile = tile;
        _dragGroup = from;
        _ghost = BuildGhost(tile);
        Canvas.SetLeft(_ghost, _downPos.X - _grab.X);
        Canvas.SetTop(_ghost, _downPos.Y - _grab.Y);
        DragCanvas.Children.Add(_ghost);
        tile.IsDragging = true;
        _lastShuffle = DateTime.MinValue;
        foreach (var g in _groups) g.ShowZone = true;
        EndZone.BeginAnimation(HeightProperty, new DoubleAnimation(46, TimeSpan.FromMilliseconds(180)));
        EndZoneRect.Opacity = 1;

        // Last, once everything above is in place: taking capture can re-dispatch a mouse move synchronously,
        // and that move must find a complete drag to work with.
        _drag = true;
        Mouse.Capture(Root, CaptureMode.Element);   // nothing else gets mouse-over while something is being dragged
    }

    /// <summary>A full-strength copy of the tile that travels with the cursor; the tile's own slot is an empty gap.</summary>
    Border BuildGhost(Tile tile)
    {
        var body = new Grid();
        if (tile.IsFolder)
        {
            // A folder: its small icons, as on the tile.
            var icons = new WrapPanel { Width = tile.PreviewWidth, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                                        Margin = tile.Size == "S" ? new Thickness(0) : new Thickness(5, 5, 0, 0) };
            foreach (var icon in tile.Preview)
                icons.Children.Add(new Image { Source = icon, Width = tile.PreviewIcon, Height = tile.PreviewIcon, Margin = new Thickness(3) });
            body.Children.Add(icons);
        }
        else body.Children.Add(new Image
        {
            Source = tile.Icon, Width = tile.IconSize, Height = tile.IconSize,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = tile.ShowLabel ? new Thickness(0, 0, 0, 14) : new Thickness(0),
        });
        if (tile.ShowLabel)
            body.Children.Add(new TextBlock
            {
                Text = tile.Name, Foreground = (Brush)FindResource("TileFg"), FontSize = 12, Margin = new Thickness(8, 0, 6, 6),
                VerticalAlignment = VerticalAlignment.Bottom, TextTrimming = TextTrimming.CharacterEllipsis,
            });
        return new Border
        {
            Width = tile.Width, Height = tile.Height, Background = (Brush)FindResource("TileBrush"),
            BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(2), Child = body, IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.5, Color = Colors.Black },
        };
    }

    /// <summary>The size most tiles in <paramref name="g"/> use (medium when empty or tied), for a tile joining it.</summary>
    static string DominantSize(TileGroup g) =>
        g.Tiles.GroupBy(t => t.Size).OrderByDescending(x => x.Count()).ThenBy(x => x.Key == "M" ? 0 : 1)
               .Select(x => x.Key).FirstOrDefault() ?? "M";

    /// <summary>Swaps the dragged (new) tile for one of another size, keeping the ghost centred under the cursor.</summary>
    Tile Resized(Tile tile, string size)
    {
        if (tile.Size == size) return tile;
        var t = tile.WithSize(size);
        t.IsDragging = true;
        _dragTile = t;
        if (_ghost != null) DragCanvas.Children.Remove(_ghost);
        _ghost = BuildGhost(t);
        DragCanvas.Children.Add(_ghost);
        _grab = new Point(t.Width / 2, t.Height / 2);
        Canvas.SetLeft(_ghost, _lastPt.X - _grab.X);
        Canvas.SetTop(_ghost, _lastPt.Y - _grab.Y);
        return t;
    }

    /// <summary>Where the ghost's centre is, in the Start menu's coordinates, for a cursor at <paramref name="pt"/>.</summary>
    Point GhostCentre(Point pt) => new(pt.X - _grab.X + _dragTile!.Width / 2, pt.Y - _grab.Y + _dragTile.Height / 2);

    void UpdateDrag(Point pt)
    {
        if (_ghost == null || _dragTile == null) return;
        // The whole window must stay captured for the drag: if anything took the mouse (the list under the ghost
        // started highlighting rows and caught the release), take it back - but only while the button is still held.
        // (Releasing capture at the drop re-sends a move; re-taking it then left the window holding the mouse, and
        // the Start menu and taskbar stopped responding to clicks.) Capturing re-sends this move.
        if (Mouse.Captured != Root && Mouse.LeftButton == MouseButtonState.Pressed)
        {
            Mouse.Capture(Root, CaptureMode.Element);
            if (Mouse.Captured == Root) return;
        }
        _lastPt = pt;
        Canvas.SetLeft(_ghost, pt.X - _grab.X);
        Canvas.SetTop(_ghost, pt.Y - _grab.Y);

        UpdateAutoScroll(pt);

        // Everything below goes by where the ghost is, not where the cursor happens to hold it.
        var centre = GhostCentre(pt);
        var p = Root.TranslatePoint(centre, TilesArea);
        _overTiles = p.X >= 0 && p.Y >= 0 && p.X <= TilesArea.ActualWidth && p.Y <= TilesArea.ActualHeight;
        var lp = Root.TranslatePoint(centre, Apps);
        _overList = !_overTiles && !_appDrag && lp.X >= 0 && lp.Y >= 0 && lp.X <= Apps.ActualWidth && lp.Y <= Apps.ActualHeight;
        _ghost.Opacity = _overList ? 0.55 : 1;   // a pinned tile held over the list fades: letting go unpins it
        if (!_overTiles)
        {
            SetZone(null, false);
            ClearFolderTarget();
            // A new pin carried back out of the tile area leaves the group it had joined.
            if (_appDrag && _dragGroup != null && _dragTile != null) { _dragGroup.Tiles.Remove(_dragTile); _dragGroup = null; }
            return;
        }

        TileGroup? group = null, zone = null;
        bool zoneEnd = false, overBand = false;
        for (var d = VisualTreeHelper.HitTest(TilesArea, p)?.VisualHit; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement fe)
                switch (fe.Tag as string)
                {
                    case "zone": zone ??= fe.DataContext as TileGroup; break;
                    case "zoneEnd": zoneEnd = true; break;
                    case "group": group ??= fe.DataContext as TileGroup; break;
                    case "band": overBand = true; break;
                }

        if (zone != null || zoneEnd) { ClearFolderTarget(); SetZone(zone, zoneEnd); return; }
        if (overBand) { ClearFolderTarget(); SetZone(null, false); return; }   // over an open folder's tiles: nothing moves
        // Anywhere in the empty part of the tile area below the last group starts a new group there, as in
        // Windows 10, not just the dotted slot itself.
        if (group == null && p.Y > Groups.TranslatePoint(new Point(0, Groups.ActualHeight), TilesArea).Y) { ClearFolderTarget(); SetZone(null, true); return; }
        SetZone(null, false);
        if (group == null) { ClearFolderTarget(); return; }
        // A tile carried out of a folder leaves it as soon as it reaches the group's tiles; the layout settles first.
        if (_dragFolderTile != null && _dragTile != null && FolderTileOf(_dragTile) == _dragFolderTile)
        {
            TakeOut(_dragTile, _dragFolderTile);
            _dragFolderTile = null;
            _lastShuffle = DateTime.Now;
            RetryShuffleLater();
            return;
        }
        Shuffle(group, centre);
    }

    // ----- dropping a tile onto another makes a folder -----

    Tile? _dragFolderTile;      // the folder the dragged tile came out of (until it leaves it)
    Tile? _folderTarget;        // the tile the dragged one is resting on
    DispatcherTimer? _folderDwell;

    /// <summary>The dragged tile rests on the middle of <paramref name="target"/>: after a moment the target lights up, and
    /// letting go then puts both in one folder (or the dragged tile into the target folder).</summary>
    void HoverFolderTarget(Tile target)
    {
        if (_folderTarget == target) return;
        ClearFolderTarget();
        _folderTarget = target;
        if (_folderDwell == null)
        {
            _folderDwell = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _folderDwell.Tick += (_, _) => { _folderDwell.Stop(); if (_drag && _folderTarget != null) _folderTarget.IsFolderTarget = true; };
        }
        _folderDwell.Stop();
        _folderDwell.Start();
    }

    void ClearFolderTarget()
    {
        _folderDwell?.Stop();
        if (_folderTarget != null) _folderTarget.IsFolderTarget = false;
        _folderTarget = null;
    }

    /// <summary>Takes the dragged tile out of wherever it is: its group, or the folder it was dragged out of.</summary>
    void Detach(Tile tile)
    {
        if (GroupOf(tile) is { } g) g.Tiles.Remove(tile);
        else if (FolderTileOf(tile) is { } f) TakeOut(tile, f);
    }

    // ----- scrolling the tiles while dragging: with many groups the place to drop may be out of view -----

    DispatcherTimer? _autoScroll;
    double _scrollStep;

    /// <summary>Near the top or bottom edge of the tile area the tiles scroll (faster the closer the cursor is).</summary>
    void UpdateAutoScroll(Point cursor)
    {
        const double edge = 56;
        var c = Root.TranslatePoint(cursor, TilesArea);
        double h = TilesArea.ActualHeight;
        bool inColumn = c.X >= 0 && c.X <= TilesArea.ActualWidth;
        double step = !inColumn ? 0 : c.Y < edge ? -(edge - Math.Max(c.Y, 0)) : c.Y > h - edge ? Math.Min(c.Y, h) - (h - edge) : 0;
        _scrollStep = step * 0.35;   // up to ~20 px every 30 ms
        if (_scrollStep == 0) { _autoScroll?.Stop(); return; }
        if (_autoScroll == null)
        {
            _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _autoScroll.Tick += (_, _) =>
            {
                if (!_drag && _movingGroup == null) { _autoScroll!.Stop(); return; }
                double before = TilesArea.VerticalOffset;
                TilesArea.ScrollToVerticalOffset(before + _scrollStep);
                TilesArea.UpdateLayout();
                if (TilesArea.VerticalOffset == before) { _autoScroll!.Stop(); return; }   // at the end already
                if (_drag) UpdateDrag(_lastPt); else MoveGroup(Mouse.GetPosition(Groups));
            };
        }
        if (!_autoScroll.IsEnabled) _autoScroll.Start();
    }

    /// <summary>The wheel scrolls the tiles during a drag too (the mouse is captured, so it would otherwise do nothing).</summary>
    void OnRootWheel(object s, MouseWheelEventArgs e)
    {
        if (!_drag) return;
        TilesArea.ScrollToVerticalOffset(TilesArea.VerticalOffset - e.Delta * 0.6);
        TilesArea.UpdateLayout();
        UpdateDrag(_lastPt);
        e.Handled = true;
    }

    void SetZone(TileGroup? g, bool end)
    {
        foreach (var grp in _groups) grp.ZoneActive = grp == g;
        EndZoneRect.Fill = end ? (Brush)FindResource("Hover") : Brushes.Transparent;
        _zoneGroup = g; _zoneEnd = end;
    }

    /// <summary>
    /// Moves the dragged tile to the slot under the ghost's centre within <paramref name="group"/>; the others slide
    /// aside. A swap needs the centre well inside another tile's slot (a dead band around every edge), and swaps are
    /// spaced out so each slide can finish: a slow drag never flickers back and forth.
    /// </summary>
    void Shuffle(TileGroup group, Point centre)
    {
        var tile = _dragTile!;
        var panel = group.Panel;
        if (panel == null || panel.Count != group.Tiles.Count) return;   // layout hasn't caught up yet

        const double band = 10;
        var q = Root.TranslatePoint(centre, panel);
        int from = group.Tiles.IndexOf(tile);   // -1 when the tile is coming from another group
        int nearest = 0; bool exact = false, after = false;
        if (group.Tiles.Count > 0)
        {
            double best = double.MaxValue;
            for (int i = 0; i < panel.Count; i++)
            {
                var r = panel.RectOf(i);
                if (i == from ? r.Contains(q) : Within(r, q, band)) { nearest = i; exact = true; break; }
                double dx = Math.Max(Math.Max(r.Left - q.X, 0), q.X - r.Right), dy = Math.Max(Math.Max(r.Top - q.Y, 0), q.Y - r.Bottom);
                if (dx * dx + dy * dy < best) { best = dx * dx + dy * dy; nearest = i; }
            }
        }

        // Resting on the middle of another tile (well away from its edges) does not move anything: letting go there
        // makes a folder, as in Windows 10. Near a tile's edges the tiles make way as before. Folders do not nest.
        if (exact && nearest != from && !tile.IsFolder)
        {
            var r = panel.RectOf(nearest);
            if (Within(r, q, Math.Min(r.Width, r.Height) * 0.3)) { HoverFolderTarget(group.Tiles[nearest]); return; }
        }
        ClearFolderTarget();

        if ((DateTime.Now - _lastShuffle).TotalMilliseconds < 140) { RetryShuffleLater(); return; }
        if (group.Tiles.Count > 0)
        {
            if (!exact)
            {
                var r = panel.RectOf(nearest);
                if (Within(r, q, -band)) return;   // on a slot's edge or in the gap beside it: hold still
                after = q.Y > r.Bottom || (q.Y >= r.Top && q.X > r.Right);
            }
            if (nearest == from) return;   // hovering its own slot
        }

        // Over a tile: go before it while the ghost's centre is left of the tile's midpoint, after it once past.
        // That stays stable whatever the two sizes are, because after a swap the centre is over its own slot.
        if (exact) { var r = panel.RectOf(nearest); after = q.X > r.Left + r.Width / 2; }
        int p0 = nearest + (after ? 1 : 0);
        if (from >= 0)
        {
            int final = from < p0 ? p0 - 1 : p0;
            if (final == from) return;
            group.Tiles.Move(from, final);
        }
        else
        {
            _dragGroup?.Tiles.Remove(tile);
            if (_appDrag) tile = Resized(tile, DominantSize(group));   // a new pin takes the size its group uses
            group.Tiles.Insert(Math.Min(p0, group.Tiles.Count), tile);
            _dragGroup = group;
        }
        _lastShuffle = DateTime.Now;
    }

    /// <summary>The mouse may be resting when the cooldown ends, so look at where the ghost is once more then.</summary>
    void RetryShuffleLater()
    {
        _shuffleRetry ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _shuffleRetry.Tick -= OnShuffleRetry; _shuffleRetry.Tick += OnShuffleRetry;
        _shuffleRetry.Stop(); _shuffleRetry.Start();
    }

    void OnShuffleRetry(object? s, EventArgs e)
    {
        _shuffleRetry!.Stop();
        if (_drag) UpdateDrag(_lastPt);
    }

    /// <summary>True when <paramref name="p"/> lies inside <paramref name="r"/> shrunk by <paramref name="inset"/> on every side.</summary>
    static bool Within(Rect r, Point p, double inset) =>
        p.X >= r.Left + inset && p.X <= r.Right - inset && p.Y >= r.Top + inset && p.Y <= r.Bottom - inset;

    void EndDrag()
    {
        var tile = _dragTile!; var ghost = _ghost!;
        void FadeGhost()
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (_, _) => DragCanvas.Children.Remove(ghost);
            ghost.BeginAnimation(OpacityProperty, fade);
        }
        if (_folderTarget is { IsFolderTarget: true } target && target != _dragFolderTile && target != tile)
        {
            // Let go on a lit tile: both go into one folder (or the tile into that folder).
            ClearFolderTarget();
            Detach(tile);
            tile.IsDragging = false;
            if (GroupOf(target) != null) JoinFolder(target, tile);
            EndDragVisuals();
            SaveTiles();
            FadeGhost();
            return;
        }
        ClearFolderTarget();
        if (_overList)
        {
            // Dropped on the app list: unpin. The ghost fades out where it was let go.
            Detach(tile);
            EndDragVisuals();
            SaveTiles();   // now, not after the fade: the menu may close before an animation ends
            FadeGhost();
            return;
        }
        if (_zoneGroup != null || _zoneEnd)
        {
            Detach(tile);
            var ng = new TileGroup();
            ng.Tiles.Add(tile);
            _groups.Insert(_zoneEnd ? _groups.Count : _groups.IndexOf(_zoneGroup!), ng);
        }
        else if (_appDrag && _overTiles && GroupOf(tile) == null)
        {
            // Dropped on the tile area but on no group (there may be none yet): start one with it.
            var ng = new TileGroup();
            ng.Tiles.Add(tile);
            _groups.Add(ng);
        }
        EndDragVisuals();

        // Glide the ghost into the tile's final slot, then reveal the real tile.
        _landing.Add((tile, ghost));
        Dispatcher.BeginInvoke(() =>
        {
            UpdateLayout();
            var slot = _groups.Select(g => g.Panel).Where(pn => pn != null)
                              .SelectMany(pn => pn!.Children.Cast<FrameworkElement>()).FirstOrDefault(c => c.DataContext == tile);
            if (slot == null) { FinishDrag(tile, ghost); return; }
            var dest = slot.TranslatePoint(new Point(), Root);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(170);
            var ay = new DoubleAnimation(Canvas.GetTop(ghost), dest.Y, dur) { EasingFunction = ease };
            ay.Completed += (_, _) => FinishDrag(tile, ghost);
            ghost.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(Canvas.GetLeft(ghost), dest.X, dur) { EasingFunction = ease });
            ghost.BeginAnimation(Canvas.TopProperty, ay);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Common teardown: capture, drop slots, empty groups.</summary>
    void EndDragVisuals()
    {
        // The drag is over BEFORE the mouse is let go: releasing capture re-sends a mouse move, which must not find
        // a drag still in progress.
        _drag = false;
        _shuffleRetry?.Stop();
        _autoScroll?.Stop();
        ClearFolderTarget();
        Release();
        _dragTile = null; _dragGroup = null; _ghost = null; _appDrag = false; _overTiles = false; _overList = false; _listApp = null;
        _dragFolderTile = null;
        _dragEndedAt = DateTime.Now;
        SetZone(null, false);
        foreach (var g in _groups) g.ShowZone = false;
        EndZone.BeginAnimation(HeightProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        EndZoneRect.Opacity = 0;
        foreach (var g in _groups.Where(g => g.Tiles.Count == 0).ToList()) _groups.Remove(g);
    }

    void FinishDrag(Tile tile, UIElement ghost)
    {
        if (_landing.RemoveAll(l => l.Tile == tile && l.Ghost == ghost) == 0 && !DragCanvas.Children.Contains(ghost)) return;   // done already
        DragCanvas.Children.Remove(ghost);
        tile.IsDragging = false;
        SaveTiles();
    }

    void CancelDrag()
    {
        var tile = _dragTile; var ghost = _ghost;
        if (_appDrag && tile != null) GroupOf(tile)?.Tiles.Remove(tile);   // an unfinished new pin is simply dropped
        EndDragVisuals();
        if (ghost != null) DragCanvas.Children.Remove(ghost);
        if (tile != null) tile.IsDragging = false;
        SaveTiles();
    }

    void OnTileRightClick(object s, MouseButtonEventArgs e)
    {
        var t = TileOf(s);
        var m = new ContextMenu();
        m.Items.Add(MenuItemFor(t.IsFolder ? "Unpin folder from Start" : "Unpin from Start", () => RemoveTile(t)));
        if (!t.IsFolder && TaskbarPinEntry(t.App) is { } tp) m.Items.Add(tp);
        var resize = new MenuItem { Header = "Resize" };
        foreach (var (label, code) in new[] { ("Small", "S"), ("Medium", "M"), ("Wide", "W"), ("Large", "L") })
        {
            var mi = MenuItemFor(label, () =>
            {
                // In its group or inside a folder; a folder tile keeps its contents (and stays open if it was).
                if (HomeOf(t) is not { } home) return;
                int i = home.IndexOf(t);
                if (i < 0) return;
                var resized = t.WithSize(code);
                var g = GroupOf(t);
                bool wasOpen = g?.OpenFolder == t;
                if (wasOpen) CloseFolder(g!);
                home[i] = resized;
                if (wasOpen) ToggleTileFolder(resized);
                SaveTiles();
            });
            mi.IsChecked = t.Size == code;
            resize.Items.Add(mi);
        }
        m.Items.Add(resize);
        if (t.IsFolder) { OpenPopup(m); e.Handled = true; return; }   // nothing below applies to a folder
        m.Items.Add(new Separator());
        if (!t.App.IsStore)
        {
            m.Items.Add(MenuItemFor("Run as administrator", () => RunAsAdmin(t.App)));
            m.Items.Add(MenuItemFor("Open file location", () => OpenLocation(t.App)));
            m.Items.Add(MenuItemFor("Properties", () => ShowProperties(t.App)));
            m.Items.Add(new Separator());
        }
        m.Items.Add(MenuItemFor("Uninstall", () => OpenSettings("ms-settings:appsfeatures")));
        OpenPopup(m);
        e.Handled = true;
    }
}

