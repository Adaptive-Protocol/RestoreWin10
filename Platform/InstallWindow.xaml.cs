using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RestoreWin10;

/// <summary>
/// The install window: one page that says what will happen (install, update from the installed version, reinstall, or
/// that a newer version is already installed), the remembered "start at sign-in" choice, then progress and a finish page
/// with "Start RestoreWin10 now". No folder choice: RestoreWin10 belongs in Program Files, where only administrators can
/// change it (threat model F-03). No licence to accept (MIT asks for none).
/// </summary>
public partial class InstallWindow : Window
{
    readonly Setup.Plan _plan;
    bool _busy, _done;

    /// <summary>The process's exit code: Setup's codes (0 installed, 2 cancelled, 4 a newer version is installed...).</summary>
    public int ExitCode { get; private set; } = Setup.Cancelled;

    public InstallWindow(System.Version own, System.Version? installed, bool signIn)
    {
        InitializeComponent();
        _plan = Setup.PlanFor(own, installed);
        SignInBox.IsChecked = signIn;
        Shield.Source = ShieldIcon();
        string v(System.Version x) => x.ToString(3);
        const string removal = "To remove RestoreWin10 later, use Settings › Apps › Installed apps.";
        switch (_plan)
        {
            case Setup.Plan.Install:
                Heading.Text = "Install RestoreWin10";
                SubHeading.Text = $"Version {v(own)}";
                ReadyText.Text = "RestoreWin10 brings back the Windows 10 Start menu and taskbar. It is installed for this PC in " +
                                 "Program Files, with one administrator prompt, and runs without administrator rights.";
                PrimaryText.Text = "Install";
                ReadyNote.Text = removal;
                break;
            case Setup.Plan.Update:
                Heading.Text = "Update RestoreWin10";
                SubHeading.Text = $"{v(installed!)} → {v(own)}";
                ReadyText.Text = "Your tiles, pins and settings are kept. If RestoreWin10 is running, it closes for a moment and " +
                                 "starts again by itself.";
                PrimaryText.Text = "Update";
                ReadyNote.Text = removal;
                break;
            case Setup.Plan.Reinstall:
                Heading.Text = "Reinstall RestoreWin10";
                SubHeading.Text = $"Version {v(own)} is already installed";
                ReadyText.Text = "Installs the same version again, for example to repair it. Your tiles, pins and settings are kept.";
                PrimaryText.Text = "Reinstall";
                ReadyNote.Text = removal;
                break;
            default:   // a newer version is installed: nothing to do
                Heading.Text = "A newer version is installed";
                SubHeading.Text = $"Installed: {v(installed!)}    This installer: {v(own)}";
                ReadyText.Text = "This installer is older than the RestoreWin10 already on this PC, so it does not replace it. " +
                                 "Nothing was changed. To go back to this version, uninstall RestoreWin10 from Settings › Apps first.";
                SignInBox.Visibility = Visibility.Collapsed;
                PrimaryButton.Visibility = Visibility.Collapsed;
                SecondaryButton.Content = "Close";
                ExitCode = Setup.NewerInstalled;
                break;
        }
    }

    async void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (_done)
        {
            if (StartNowBox.Visibility == Visibility.Visible && StartNowBox.IsChecked == true) Setup.StartShell();
            Close();
            return;
        }
        if (_busy || _plan == Setup.Plan.NewerInstalled) return;
        bool signIn = SignInBox.IsChecked == true;
        ShowBusy();
        var (code, restarted) = await Task.Run(() =>
        {
            int c = Setup.RunInstall(signIn, startAfter: false, quiet: false, out bool r);
            return (c, r);
        });
        _busy = false;
        if (code == Setup.Ok) ShowDone(restarted);
        else ShowReady(code == Setup.Cancelled
            ? "The administrator prompt was declined, so nothing was installed. Choose " + PrimaryText.Text + " to try again."
            : code == Setup.NewerInstalled
                ? "A newer version was installed in the meantime, so nothing was changed."
                : "Installing did not finish (the reason was shown in the message before this). RestoreWin10 is as it was before.");
    }

    void OnSecondary(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy) e.Cancel = true;   // never left half way
        base.OnClosing(e);
    }

    void ShowBusy()
    {
        _busy = true;
        ReadyPage.Visibility = Visibility.Collapsed;
        BusyPage.Visibility = Visibility.Visible;
        BusyText.Text = (_plan == Setup.Plan.Update ? "Updating RestoreWin10…" : "Installing RestoreWin10…") +
                        " If Windows asks for administrator permission, choose Yes.";
        PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = false;
    }

    void ShowReady(string note)
    {
        BusyPage.Visibility = Visibility.Collapsed;
        ReadyPage.Visibility = Visibility.Visible;
        ReadyNote.Text = note;
        PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = true;
    }

    /// <param name="restarted">Restart Manager already started RestoreWin10 again (it was running before an update).</param>
    void ShowDone(bool restarted)
    {
        _done = true;
        ExitCode = Setup.Ok;
        BusyPage.Visibility = ReadyPage.Visibility = Visibility.Collapsed;
        DonePage.Visibility = Visibility.Visible;
        Heading.Text = _plan == Setup.Plan.Update ? "RestoreWin10 is updated" : "RestoreWin10 is installed";
        DoneText.Text = restarted
            ? "RestoreWin10 is running again with your tiles, pins and settings."
            : "RestoreWin10 is ready." + (SignInBox.IsChecked == true ? " It starts by itself when you sign in." : "");
        StartNowBox.Visibility = restarted ? Visibility.Collapsed : Visibility.Visible;
        PrimaryText.Text = "Finish";
        Shield.Visibility = Visibility.Collapsed;
        SecondaryButton.Visibility = Visibility.Collapsed;
        PrimaryButton.IsEnabled = true;
    }

    // ---------- the UAC shield on the button that asks for administrator rights ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHSTOCKICONINFO { public uint cbSize; public nint hIcon; public int iSysIconIndex; public int iIcon; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHGetStockIconInfo(uint siid, uint flags, ref SHSTOCKICONINFO info);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint h);

    static ImageSource? ShieldIcon()
    {
        var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
        if (SHGetStockIconInfo(77 /* SIID_SHIELD */, 0x101 /* SHGSI_ICON | SHGSI_SMALLICON */, ref info) != 0 || info.hIcon == 0) return null;
        try
        {
            var bs = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze();
            return bs;
        }
        finally { DestroyIcon(info.hIcon); }
    }

#if DEV_BUILD
    /// <summary>Development builds only: draws the window into a PNG without showing it; <paramref name="done"/> shows the
    /// finish page (for checking the layout; nothing is installed).</summary>
    internal void Snapshot(string file, bool done = false, bool restarted = false)
    {
        if (done) ShowDone(restarted);
        var content = (FrameworkElement)Content;
        content.Measure(new Size(Width, double.PositiveInfinity));
        content.Arrange(new Rect(new Size(Width, content.DesiredSize.Height)));
        content.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Width, (int)Math.Ceiling(content.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(content);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(file);
        png.Save(fs);
    }
#endif
}
