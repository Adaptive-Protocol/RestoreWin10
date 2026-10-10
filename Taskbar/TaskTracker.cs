using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RestoreWin10;

/// <summary>One taskbar button: a pinned app and/or all open windows of one program.</summary>
public sealed class TaskItem : Notifier, IDragItem
{
    bool _pinned, _active, _dragging, _attention, _pulse;
    List<nint> _windows = new();

    /// <summary>The program asked for attention (FlashWindow, or Windows refused to bring it to the front, as with an
    /// elevation prompt behind another window). Cleared when one of its windows is activated.</summary>
    public bool NeedsAttention { get => _attention; set => Set(ref _attention, value); }
    /// <summary>Alternates for the first moments of attention, driving the pulse; then stays on.</summary>
    public bool Pulse { get => _pulse; set => Set(ref _pulse, value); }
    internal int FlashTicksLeft;

    public string Key { get; init; } = "";
    public string? ExePath { get; init; }
    public string Name { get; set; } = "";
    public BitmapSource? Icon { get; init; }

    public IReadOnlyList<nint> Windows => _windows;
    public bool IsRunning => _windows.Count > 0;
    public bool HasMany => _windows.Count > 1;
    public bool IsPinned { get => _pinned; set => Set(ref _pinned, value); }
    public bool IsActive { get => _active; set => Set(ref _active, value); }
    public bool IsDragging { get => _dragging; set => Set(ref _dragging, value); }

    /// <summary>A Store / packaged app: <see cref="Key"/> is its "shell:AppsFolder\…" id.</summary>
    public bool IsStore => Key.StartsWith(Launcher.StorePrefix, StringComparison.OrdinalIgnoreCase);
    /// <summary>What a pin of this button is saved as (an exe path or a Store app id), or null when it cannot be pinned
    /// (a window we could not tie to a program, or a UWP window whose app is unknown).</summary>
    public string? PinKey => IsStore || (ExePath != null && Key.Equals(ExePath, StringComparison.OrdinalIgnoreCase)) ? Key : null;

    public void SetWindows(List<nint> wins)
    {
        if (_windows.SequenceEqual(wins)) return;
        _windows = wins;
        Raise(nameof(Windows)); Raise(nameof(IsRunning)); Raise(nameof(HasMany));
    }
}

/// <summary>
/// Watches top-level windows and keeps one taskbar's button list in sync. Every taskbar (one per screen) has its own
/// tracker; they share one window snapshot per refresh and one left-to-right order, so the bars always agree.
/// </summary>
public sealed class TaskTracker
{
    public ObservableCollection<TaskItem> Items { get; } = new();

    readonly State _state;
    readonly bool _main;
    // Shared by all bars: left-to-right order of every button anywhere (pinned and running), caches.
    static readonly List<string> s_order = new();
    static readonly Dictionary<string, BitmapSource?> s_icons = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> s_names = new(StringComparer.OrdinalIgnoreCase);
    static readonly uint s_ownPid = (uint)Environment.ProcessId;
    static readonly Dictionary<uint, string?> s_exeByPid = new();
    static readonly Dictionary<uint, string?> s_appByPid = new();   // pid -> Store key of a packaged app's process, or null
    static readonly HashSet<uint> s_seenPids = new();
    static List<Win_>? s_snapshot;
    static long s_snapshotAt;
    nint _lastForeground;

    /// <summary>The monitor this tracker's bar sits on.</summary>
    public nint Monitor { get; set; }

    /// <summary>Taskbars exist on more than one screen (set by the app as bars come and go).</summary>
    public static bool MultipleBars { get; set; }

    public TaskTracker(State state, bool main = true) { _state = state; _main = main; }

    record Win_(nint Hwnd, string Key, string? Exe, string Title, nint Monitor);

    /// <summary>All taskbar-worthy windows; enumerated at most once per ~150 ms however many bars ask.</summary>
    static List<Win_> Snapshot()
    {
        long now = Environment.TickCount64;
        if (s_snapshot == null || now - s_snapshotAt > 150) { s_snapshot = Enumerate(); s_snapshotAt = now; }
        return s_snapshot;
    }

    /// <summary>The programs' windows that are open right now (for Cascade / Show windows stacked / side by side).</summary>
    public static List<nint> OpenWindows() { s_snapshot = null; return Snapshot().Select(w => w.Hwnd).ToList(); }

    static List<Win_> Enumerate()
    {
        var list = new List<Win_>();
        var _seenPids = s_seenPids; var _exeByPid = s_exeByPid; var _ownPid = s_ownPid;
        _seenPids.Clear();
        Win.EnumWindows((h, _) =>
        {
            if (!Win.IsWindowVisible(h)) return true;
            int ex = Win.GetWindowLong(h, Win.GWL_EXSTYLE);
            bool owned = Win.GetWindow(h, Win.GW_OWNER) != 0;
            if ((ex & Win.WS_EX_TOOLWINDOW) != 0 && (ex & Win.WS_EX_APPWINDOW) == 0) return true;
            if (owned && (ex & Win.WS_EX_APPWINDOW) == 0) return true;
            if (Win.DwmGetWindowAttribute(h, 14 /* CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;

            var title = Win.Text(h);
            if (title.Length == 0) return true;
            var cls = Win.ClassName(h);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;

            // A hung window Windows has ghosted: the ghost stands in for it on the same button (its title already
            // says "(Not Responding)"), and the original is skipped while its ghost exists. A hung window that
            // has not been ghosted yet gets the suffix from us.
            nint owner = h;
            if (cls == "Ghost")
            {
                owner = Win.HungFromGhost(h);
                if (owner == 0) return true;
            }
            else if (Win.IsHungAppWindow(h))
            {
                if (Win.GhostFromHung(h) != 0) return true;
                title += " (Not Responding)";
            }

            Win.GetWindowThreadProcessId(owner, out uint pid);
            if (pid == _ownPid) return true;
            _seenPids.Add(pid);
            if (!_exeByPid.TryGetValue(pid, out var exe)) _exeByPid[pid] = exe = Win.ExePath(pid);
            if (!s_appByPid.TryGetValue(pid, out var app)) s_appByPid[pid] = app = StoreKey(Win.PackagedAumid(pid));
            // UWP windows are all hosted by one frame process: each goes by the app it shows (its window's
            // AppUserModelID), or failing that is a button of its own. A packaged app goes by its app id, which stays
            // the same across its updates; its exe's folder does not.
            bool frame = exe?.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) == true;
            string key = exe == null ? $"pid:{pid}"
                : frame ? StoreKey(PropStore.WindowAumid(h)) ?? $"{exe}|{h}"
                : app ?? exe;
            list.Add(new Win_(h, key, exe, title, Screens.HandleOf(h)));
            return true;
        }, 0);
        foreach (var dead in _exeByPid.Keys.Where(p => !_seenPids.Contains(p)).ToList()) { _exeByPid.Remove(dead); s_appByPid.Remove(dead); }
        return list;
    }

    /// <summary>"shell:AppsFolder\&lt;AppUserModelID&gt;" for a Store app id that passes the launcher's checks, else null.</summary>
    internal static string? StoreKey(string? aumid) =>
        aumid is { Length: > 0 } && Launcher.IsLaunchable(Launcher.StorePrefix + aumid) ? Launcher.StorePrefix + aumid : null;

    /// <summary>
    /// The package family of an exe inside a packaged app's install folder, e.g.
    /// "C:\Program Files\WindowsApps\Claude_2.31226.1.0_x64__pzs8sxrjxfjjc\app\claude.exe" → "Claude_pzs8sxrjxfjjc";
    /// null for any other path. (The folder is Name_Version_Architecture_ResourceId_PublisherId.)
    /// </summary>
    internal static string? PackageFamilyOf(string path)
    {
        const string marker = @"\WindowsApps\";
        int at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var rest = path[(at + marker.Length)..];
        int slash = rest.IndexOf('\\');
        var folder = slash > 0 ? rest[..slash] : rest;
        var parts = folder.Split('_');
        if (parts.Length != 5 || parts[0].Length == 0 || parts[4].Length == 0) return null;
        return parts[0] + "_" + parts[4];
    }

    /// <summary>
    /// A pin made by a packaged app's exe (as earlier versions did) becomes a pin by its app id, keeping its place:
    /// <paramref name="exe"/> → <paramref name="key"/>. True when something changed.
    /// </summary>
    internal static bool RekeyPin(State state, string exe, string key)
    {
        var pins = state.TaskbarPins;
        int i = pins.FindIndex(p => p.Equals(exe, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        if (pins.Contains(key, StringComparer.OrdinalIgnoreCase)) pins.RemoveAt(i); else pins[i] = key;
        state.TaskbarPinLaunch.Remove(exe);
        int o = s_order.FindIndex(k => k.Equals(exe, StringComparison.OrdinalIgnoreCase));
        if (o >= 0) { if (s_order.Contains(key, StringComparer.OrdinalIgnoreCase)) s_order.RemoveAt(o); else s_order[o] = key; }
        return true;
    }

    static BitmapSource? IconFor(string? exe)
    {
        if (exe == null) return null;
        if (!s_icons.TryGetValue(exe, out var ic))
            s_icons[exe] = ic = exe.StartsWith(Launcher.StorePrefix, StringComparison.OrdinalIgnoreCase)
                ? new AppEntry { Path = exe }.Icon   // a Store app id: the Apps folder's own icon
                : IconHelper.FromFile(exe);
        return ic;
    }

    static string NameFor(string? exe, string fallback)
    {
        if (exe == null) return fallback;
        if (exe.StartsWith(Launcher.StorePrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!s_names.TryGetValue(exe, out var sn)) s_names[exe] = sn = Shortcut.AppName(exe) ?? AppEntry.StoreDisplayName(exe);
            return sn;
        }
        if (!s_names.TryGetValue(exe, out var n))
        {
            try { var d = FileVersionInfo.GetVersionInfo(exe).FileDescription; n = string.IsNullOrWhiteSpace(d) ? Path.GetFileNameWithoutExtension(exe) : d; }
            catch { n = Path.GetFileNameWithoutExtension(exe); }
            s_names[exe] = n;
        }
        return n;
    }

    public void Refresh()
    {
        var all = Snapshot();

        // Windows' "Show taskbar buttons on" setting decides what a bar shows when there are several:
        //   all taskbars (0): every bar shows every pin and window;
        //   main taskbar and taskbar where window is open (1): the main bar shows everything, the others their screen's windows;
        //   taskbar where window is open (2): each bar shows its own screen's windows, pins stay on the main bar.
        int mode = MultipleBars ? Screens.TaskbarButtonsMode : 0;
        bool onlyHere = mode == 2 || (mode == 1 && !_main);
        bool showPins = _main || mode == 0;
        var wins = onlyHere && Monitor != 0 ? all.Where(w => w.Monitor == Monitor).ToList() : all;
        var byKey = wins.GroupBy(w => w.Key).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Which window counts as "active"? Our own taskbar never does - keep the previous one.
        var fg = Win.GetForegroundWindow();
        if (fg != 0)
        {
            Win.GetWindowThreadProcessId(fg, out uint fpid);
            if (fpid != s_ownPid) _lastForeground = Win.GetAncestor(fg, Win.GA_ROOTOWNER) is var r && r != 0 ? r : fg;
        }

        // Buttons are whatever is pinned or running. Known ones keep their place; new ones go on the right.
        // (Pins are seeded into the order in their saved sequence the first time round.) The order covers every
        // button on every bar, so it is kept up to date against everything that exists, not just this bar's share.
        var pins = _state.TaskbarPins;
        // A packaged app pinned by its exe (pinned from its button by an earlier version) is pinned by its app id from
        // now on, so the pin survives the app's next update.
        if (_main && pins.Count > 0)
        {
            bool rekeyed = false;
            foreach (var w in all)
                if (w.Exe != null && w.Key.StartsWith(Launcher.StorePrefix, StringComparison.OrdinalIgnoreCase))
                    rekeyed |= RekeyPin(_state, w.Exe, w.Key);
            if (rekeyed) _state.Save();
        }
        var allKeys = all.Select(w => w.Key).ToList();
        var present = new HashSet<string>(pins.Concat(allKeys), StringComparer.OrdinalIgnoreCase);
        s_order.RemoveAll(k => !present.Contains(k));
        foreach (var k in pins.Concat(allKeys))
            if (!s_order.Contains(k, StringComparer.OrdinalIgnoreCase)) s_order.Add(k);
        var mine = new HashSet<string>(showPins ? pins.Concat(byKey.Keys) : byKey.Keys, StringComparer.OrdinalIgnoreCase);
        var desired = s_order.Where(mine.Contains).ToList();

        // Sync the collection in place (no flicker, existing buttons keep their identity).
        for (int i = Items.Count - 1; i >= 0; i--)
            if (!desired.Contains(Items[i].Key, StringComparer.OrdinalIgnoreCase)) Items.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            var key = desired[i];
            int at = Items.ToList().FindIndex(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                byKey.TryGetValue(key, out var w);
                bool store = key.StartsWith(Launcher.StorePrefix, StringComparison.OrdinalIgnoreCase);
                var exe = store ? w?.FirstOrDefault()?.Exe
                        : key.Contains('|') ? key[..key.IndexOf('|')] : key.StartsWith("pid:") ? null : key;
                Items.Insert(i, new TaskItem
                {
                    Key = key, ExePath = exe, Icon = IconFor(store ? key : exe),
                    Name = _state.TaskbarPinLaunch.TryGetValue(key, out var lnk) ? Path.GetFileNameWithoutExtension(lnk)
                                                                                  : NameFor(store ? key : exe, w?.FirstOrDefault()?.Title ?? key),
                });
            }
            else if (at != i) Items.Move(at, i);
        }

        foreach (var item in Items)
        {
            byKey.TryGetValue(item.Key, out var w);
            var handles = w?.Select(x => x.Hwnd).ToList() ?? new List<nint>();
            item.SetWindows(handles);
            item.IsPinned = _state.TaskbarPins.Contains(item.Key, StringComparer.OrdinalIgnoreCase);
            item.IsActive = handles.Contains(_lastForeground) && !Win.IsIconic(_lastForeground);

            if (item.IsActive) { item.NeedsAttention = false; item.Pulse = false; item.FlashTicksLeft = 0; }
            else if (item.FlashTicksLeft > 0)
            {
                item.Pulse = --item.FlashTicksLeft == 0 || !item.Pulse;   // blink, then settle on
            }
        }

        if (showPins) SavePinOrder();
    }

    /// <summary>A window asked for attention: light its button (the pulse is driven by <see cref="Refresh"/>).</summary>
    public void Flash(nint hwnd)
    {
        var root = Win.GetAncestor(hwnd, Win.GA_ROOTOWNER);
        if (root == 0) root = hwnd;
        var item = Items.FirstOrDefault(t => t.Windows.Contains(hwnd) || t.Windows.Contains(root));
        if (item == null || item.IsActive) return;
        item.NeedsAttention = true;
        item.Pulse = true;
        item.FlashTicksLeft = 6;
    }

    /// <summary>Pinned programs are saved in the order their buttons appear, whatever is running in between.</summary>
    void SavePinOrder()
    {
        var pins = _state.TaskbarPins;
        var ordered = s_order.Where(k => pins.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (ordered.Count != pins.Count) return;   // a pin not placed yet: the next refresh places it, then this saves
        if (!ordered.SequenceEqual(pins, StringComparer.OrdinalIgnoreCase))
        {
            pins.Clear(); pins.AddRange(ordered);
            _state.Save();
        }
    }

    /// <summary>Forgets the remembered order: pins lead in their saved order, running programs follow.</summary>
    public void ResetOrder() => s_order.Clear();

    /// <summary>
    /// Called after the user drags a button: this bar's buttons take the places in the shared order that its buttons
    /// held, in their new sequence; buttons that only other bars show stay where they were.
    /// </summary>
    public void CommitOrder(bool save = true)
    {
        var mine = Items.Select(t => t.Key).ToList();
        var set = new HashSet<string>(mine, StringComparer.OrdinalIgnoreCase);
        var slots = Enumerable.Range(0, s_order.Count).Where(i => set.Contains(s_order[i])).ToList();
        if (slots.Count == mine.Count)
            for (int j = 0; j < slots.Count; j++) s_order[slots[j]] = mine[j];
        else
        {
            int at = slots.Count > 0 ? slots[0] : 0;
            s_order.RemoveAll(set.Contains);
            s_order.InsertRange(Math.Min(at, s_order.Count), mine);
        }
        if (save) SavePinOrder();
    }

    public string TitleOf(nint hwnd)
    {
        var t = Win.Text(hwnd);
        if (Win.ClassName(hwnd) != "Ghost" && Win.IsHungAppWindow(hwnd)) t += " (Not Responding)";
        return t;
    }
}

