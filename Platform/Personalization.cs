using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RestoreWin10;

/// <summary>
/// Windows' own personalization settings, followed live, so Settings works for RestoreWin10 the way it did for the
/// Windows 10 shell:
///   Colors  - light/dark Windows mode, transparency effects, accent colour, "Show accent color on Start and taskbar";
///   Taskbar - Search (hidden / icon / box) and the Task view button;
///   Start   - "Show most used apps".
/// Everything visual is published as application-wide brushes (DynamicResource keys below), so a change repaints every
/// open window at once; window backgrounds (acrylic or solid) are re-applied through <see cref="Surface"/>.
/// </summary>
static class Personalization
{
    public sealed record Palette(bool Light, bool Transparency, bool AccentOnShell, Color Accent, Color AccentLight3, Color ShellAccent);

    public enum Kind { Taskbar, Start, Flyout }

    /// <summary>
    /// The taskbar's styles, after TranslucentTB's (reimplemented here: RestoreWin10's bar is its own window, so nothing
    /// is changed inside Explorer). Normal follows Windows; Clear lets the wallpaper show through untouched; Acrylic is
    /// always blurred; Opaque is always solid.
    /// </summary>
    public enum BarStyle { Normal, Clear, Acrylic, Opaque }

    public static Palette Current { get; private set; } = ReadPalette();

    /// <summary>The palette changed: windows re-apply their backgrounds (brushes update by themselves).</summary>
    public static event Action? Changed;
    /// <summary>A taskbar or Start option changed in Settings (or from the taskbar's own menu).</summary>
    public static event Action? OptionsChanged;

    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    const string SearchKey = @"Software\Microsoft\Windows\CurrentVersion\Search";
    const string Dwm = @"Software\Microsoft\Windows\DWM";

    static object? Get(string key, string name)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(key); return k?.GetValue(name); }
        catch { return null; }
    }
    static int Int(string key, string name, int fallback) => Get(key, name) is int v ? v : fallback;

    static Color FromAbgr(int v) => Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));

    static Palette ReadPalette()
    {
        bool light = Int(Personalize, "SystemUsesLightTheme", 1) != 0;
        bool transparency = Int(Personalize, "EnableTransparency", 1) != 0;
        // Windows only offers accent-coloured Start and taskbar in dark mode.
        bool accentOnShell = !light && Int(Personalize, "ColorPrevalence", 0) != 0;

        // AccentPalette: eight RGBA entries, light3 light2 light1 ACCENT dark1 dark2 dark3 (unused).
        Color accent = Color.FromRgb(0x00, 0x78, 0xD7), light3 = Color.FromRgb(0xA6, 0xD8, 0xFF), dark2 = Color.FromRgb(0x00, 0x4E, 0x8C);
        if (Get(AccentKey, "AccentPalette") is byte[] p && p.Length >= 32)
        {
            Color At(int i) => Color.FromRgb(p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
            light3 = At(0); accent = At(3); dark2 = At(5);
        }
        else if (Get(Dwm, "AccentColor") is int dwm)
        {
            accent = FromAbgr(dwm);
            light3 = Mix(accent, Colors.White, 0.6); dark2 = Mix(accent, Colors.Black, 0.35);
        }
        // The colour Windows paints Start, the taskbar and the action centre with when "show accent color" is on.
        var shell = Get(AccentKey, "StartColorMenu") is int sc ? FromAbgr(sc) : dark2;
        return new Palette(light, transparency, accentOnShell, accent, light3, shell);
    }

    // (v1 kept tiles Windows 10 blue while a particular accent was set. v2's tiles are theme-aware instead, decision D2.)

    static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    // ---------- taskbar and Start options (Settings > Personalization > Taskbar / Start) ----------

    /// <summary>0 hidden, 1 search icon, 2 search box. (Windows 11's "icon and label" shows as the box.)</summary>
    public static int SearchMode => Int(SearchKey, "SearchboxTaskbarMode", 1) switch { 0 => 0, 1 => 1, _ => 2 };
    public static bool ShowTaskView => Int(Advanced, "ShowTaskViewButton", 1) != 0;
    /// <summary>Start: "Show most used apps".</summary>
    public static bool ShowMostUsed => Int(Advanced, "Start_TrackProgs", 1) != 0;
    /// <summary>
    /// Start: "Show recently added apps" (Settings keeps it as ShowRecentList next to ShowFrequentList, the most-used
    /// switch), unless a group policy hides recently added apps.
    /// </summary>
    public static bool ShowRecentlyAdded =>
        Int(StartKey, "ShowRecentList", 1) != 0 &&
        !(Registry.GetValue(@"HKEY_CURRENT_USER\" + ExplorerPolicy, "HideRecentlyAddedApps", 0) is 1) &&
        !(Registry.GetValue(@"HKEY_LOCAL_MACHINE\" + ExplorerPolicy, "HideRecentlyAddedApps", 0) is 1);
    const string StartKey = @"Software\Microsoft\Windows\CurrentVersion\Start";
    const string ExplorerPolicy = @"Software\Policies\Microsoft\Windows\Explorer";

    public static void SetSearchMode(int mode) => Set(SearchKey, "SearchboxTaskbarMode", mode);
    public static void SetShowTaskView(bool on) => Set(Advanced, "ShowTaskViewButton", on ? 1 : 0);

    /// <summary>Writes one of Windows' own taskbar settings (what Settings writes), so both stay in agreement.</summary>
    static void Set(string key, string name, int value)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(key);
            k.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch (Exception ex) { CrashLog.Write("writing a taskbar setting", ex); }
        Refresh();
    }

    // (v1 copied its own search / Task View choices into Windows' settings once at startup. v2 never writes Windows'
    // settings on its own: only the taskbar menu's Search and Task View entries do, when the user picks them.)

    // ---------- following changes ----------

    static DispatcherTimer? s_poll, s_debounce;
    static string s_options = "";

    /// <summary>Starts following Windows: <paramref name="listener"/> is a top-level window of ours (it receives the
    /// broadcasts Settings sends); a slow poll catches options that are not broadcast.</summary>
    public static void Start(Window listener)
    {
        s_options = OptionsKey();
        var src = HwndSource.FromHwnd(new WindowInteropHelper(listener).EnsureHandle());
        src?.AddHook((nint h, int msg, nint w, nint l, ref bool handled) =>
        {
            // WM_SETTINGCHANGE (colours arrive as "ImmersiveColorSet"), WM_THEMECHANGED, WM_DWMCOLORIZATIONCOLORCHANGED
            if (msg is 0x001A or 0x031A or 0x0320) Soon();
            return 0;
        });
        s_poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        s_poll.Tick += (_, _) => Refresh();
        s_poll.Start();
    }

    static void Soon()
    {
        if (s_debounce == null)
        {
            s_debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            s_debounce.Tick += (_, _) => { s_debounce.Stop(); Refresh(); };
        }
        s_debounce.Stop(); s_debounce.Start();
    }

    static string OptionsKey() => $"{SearchMode}|{ShowTaskView}|{ShowMostUsed}|{ShowRecentlyAdded}|{StartPlaces.Signature()}";

    /// <summary>Re-reads everything; tells the windows only about what actually changed.</summary>
    public static void Refresh()
    {
        var p = ReadPalette();
        if (p != Current)
        {
            Current = p;
            ApplyResources();
            Changed?.Invoke();
        }
        var o = OptionsKey();
        if (o != s_options) { s_options = o; OptionsChanged?.Invoke(); }
    }

    // ---------- brushes ----------

    static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static SolidColorBrush B(uint argb) => B(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    static SolidColorBrush B(int argb) => B(unchecked((uint)argb));   // 0x33FFFFFF and friends are ints to C#
    static Color A(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>Called once at startup (before any window exists) and after every palette change.</summary>
    public static void ApplyResources()
    {
        var r = Application.Current?.Resources;
        if (r == null) return;
        var p = Current;
        bool l = p.Light;
        r["Fg"] = B(l ? 0xFF000000 : 0xFFFFFFFF);
        r["FgDim"] = B(l ? 0x99000000 : 0xB3FFFFFF);
        r["FgFaint"] = B(l ? 0x73000000 : 0x80FFFFFF);
        r["Hover"] = B(l ? 0x1A000000 : 0x33FFFFFF);
        r["Press"] = B(l ? 0x26000000 : 0x22FFFFFF);
        r["Selected"] = B(l ? 0x33000000 : 0x4DFFFFFF);
        r["Stroke"] = B(l ? 0x80000000 : 0x80FFFFFF);
        r["RailBg"] = B(l ? 0x0F000000 : 0x40000000);
        // The line under a running program, as on Windows 10's taskbar: the accent's lightest shade on a dark bar (measured
        // from a Windows 10 screenshot), the accent itself on a light one. The active program's line is full width.
        r["Running"] = B(l ? p.Accent : p.AccentLight3);
        r["Accent"] = B(p.Accent);
        // Tiles as in Windows 10 20H2 and later (decision D2): a translucent grey plate on dark Start, a light one on light
        // Start, the accent colour only while "Show accent color on Start" is on.
        r["TileBrush"] = p.AccentOnShell ? B(A(p.Accent, p.Transparency ? (byte)0xE6 : (byte)0xFF))
                       : l ? B(p.Transparency ? 0x8CFFFFFF : 0xFFF7F7F7)
                           : B(p.Transparency ? 0x26FFFFFF : 0xFF353535);
        r["TileFg"] = B(l && !p.AccentOnShell ? 0xFF000000 : 0xFFFFFFFF);
        // Start's left strip when it opens over the app list, and the Power / account panels inside it.
        r["RailOpenBg"] = p.AccentOnShell ? B(A(Mix(p.ShellAccent, Colors.Black, 0.15), p.Transparency ? (byte)0xF2 : (byte)0xFF))
                        : B(l ? (p.Transparency ? 0xF2EBEBEB : 0xFFEBEBEB) : (p.Transparency ? 0xF2232323 : 0xFF232323));
        // Solid: a panel is drawn over the strip's own entries (Power's covers the folders above it, as in Windows 10).
        r["RailPanelBg"] = p.AccentOnShell ? B(Mix(p.ShellAccent, Colors.Black, 0.3)) : B(l ? 0xFFDCDCDC : 0xFF1A1A1A);
        r["RailPanelLine"] = B(l ? 0x26000000 : 0x26FFFFFF);
        r["TileHoverBorder"] = B(l && !p.AccentOnShell ? 0x66000000 : 0x80FFFFFF);
        r["FolderBandBg"] = B(l && !p.AccentOnShell ? 0x14000000 : 0x1AFFFFFF);   // an open folder's band, a shade off the menu
        r["ThumbSlot"] = B(l ? 0xFFD9D9D9 : 0xFF2B2B2B);
        r["FlyoutBg"] = B(l ? (p.Transparency ? 0xE6F2F2F2 : 0xFFF2F2F2) : (p.Transparency ? 0xE61F1F1F : 0xFF1F1F1F));
        r["SearchPaneBg"] = B(l ? (p.Transparency ? 0xCCF2F2F2 : 0xFFF2F2F2) : (p.Transparency ? 0xCC1B1B1B : 0xFF1B1B1B));
        r["MenuBg"] = B(l ? 0xFFF2F2F2 : 0xFF1F1F1F);
        r["MenuBorder"] = B(l ? 0xFFCCCCCC : 0xFF454545);
        r["MenuHover"] = B(l ? 0xFFDADADA : 0x33FFFFFF);
        r["MenuFg"] = B(l ? 0xFF000000 : 0xFFFFFFFF);
        r["MenuFgDisabled"] = B(l ? 0xFF6D6D6D : 0xFF777777);
    }

    /// <summary>
    /// The window's background for the current palette: acrylic with the right tint when transparency effects are on,
    /// a solid colour when they are off; Start and the taskbar take the accent colour when Windows says so.
    /// A taskbar <paramref name="style"/> other than Normal overrides the transparency setting (the user chose it).
    /// </summary>
    public static void Surface(Window w, Kind kind, BarStyle style = BarStyle.Normal)
    {
        var p = Current;
        if (style == BarStyle.Clear && Acrylic.Clear(w, 0))
        {
            w.Background = Brushes.Transparent;
            return;
        }
        Color c; byte alpha;
        if (kind != Kind.Flyout && p.AccentOnShell) { c = p.ShellAccent; alpha = kind == Kind.Start ? (byte)0xD9 : (byte)0xE6; }
        else if (p.Light)
        {
            c = kind switch { Kind.Taskbar => Color.FromRgb(0xEE, 0xEE, 0xEE), Kind.Start => Color.FromRgb(0xE6, 0xE6, 0xE6), _ => Color.FromRgb(0xF2, 0xF2, 0xF2) };
            alpha = kind == Kind.Start ? (byte)0xCC : (byte)0xD9;
        }
        else
        {
            c = kind == Kind.Flyout ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Color.FromRgb(0x14, 0x14, 0x14);
            alpha = kind == Kind.Start ? (byte)0xCC : (byte)0xE6;
        }

        if (style == BarStyle.Acrylic) alpha = 0x80;   // lighter than Normal's tint, so more of the wallpaper shows
        bool blur = style == BarStyle.Acrylic || (style != BarStyle.Opaque && p.Transparency);
        if (blur && Acrylic.Apply(w, (alpha << 24) | (c.B << 16) | (c.G << 8) | c.R))
            w.Background = Brushes.Transparent;
        else
        {
            Acrylic.Disable(w);
            w.Background = B(c);
        }
    }

    /// <summary>Applies <see cref="Surface"/> now and again whenever the palette changes, until the window closes.</summary>
    public static void Follow(Window w, Kind kind, Func<BarStyle>? style = null)
    {
        void Again() => w.Dispatcher.BeginInvoke(() => Surface(w, kind, style?.Invoke() ?? BarStyle.Normal));
        Surface(w, kind, style?.Invoke() ?? BarStyle.Normal);
        Changed += Again;
        w.Closed += (_, _) => Changed -= Again;
    }
}
