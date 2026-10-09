using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace RestoreWin10;

/// <summary>The "^" flyout: notification-area icons that are not pinned to the taskbar.</summary>
public partial class TrayFlyout : Window
{
    readonly TrayHost _host;
    DateTime _hiddenAt = DateTime.MinValue;

    public TrayFlyout(TrayHost host)
    {
        InitializeComponent();
        _host = host;
        List.ItemsSource = host.Overflow;
        host.Overflow.CollectionChanged += (_, _) => FitGrid();
        FitGrid();

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Win.SetWindowLong(h, Win.GWL_EXSTYLE, Win.GetWindowLong(h, Win.GWL_EXSTYLE) | Win.WS_EX_TOOLWINDOW | Win.WS_EX_NOACTIVATE);
            Personalization.Follow(this, Personalization.Kind.Flyout);
        };
        new WindowInteropHelper(this).EnsureHandle();
    }

    const double Cell = 36;

    /// <summary>Windows 10's square grid: as many columns as rows (16 icons: 4 x 4; 13: 4 columns, 4 rows).</summary>
    void FitGrid()
    {
        int n = _host.Overflow.Count;
        Empty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.Width = n == 0 ? double.NaN : Math.Ceiling(Math.Sqrt(n)) * Cell;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct POINT { public int X, Y; }

    bool CursorInside()
    {
        GetCursorPos(out var p);
        var s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return p.X >= Left * s && p.X <= (Left + ActualWidth) * s && p.Y >= Top * s && p.Y <= (Top + ActualHeight) * s;
    }

    public void Toggle(Rect anchorPx, bool above)
    {
        if (IsVisible) { Dismiss(); return; }
        if ((DateTime.Now - _hiddenAt).TotalMilliseconds < 250) return;   // the same click just closed us
        var s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var content = (FrameworkElement)Content;   // (a window reports no size until it has been shown)
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = content.DesiredSize;
        // Centred over the "^" button, as in Windows 10, but never past the edges of its screen.
        var screen = Screens.FromPoint((int)((anchorPx.Left + anchorPx.Right) / 2), (int)anchorPx.Top).Bounds;
        double widthPx = size.Width * s;
        double leftPx = Math.Clamp((anchorPx.Left + anchorPx.Right - widthPx) / 2, screen.Left, Math.Max(screen.Left, screen.Right - widthPx));
        Left = leftPx / s;
        Top = above ? anchorPx.Top / s - size.Height - 4 : anchorPx.Bottom / s + 4;
        Show();
        // A non-activating window never gets a "lost focus" event: a press anywhere outside closes it instead.
        PopupGuard.Watch(this, PopupGuard.FlyoutOf(new WindowInteropHelper(this).Handle), Dismiss);
    }

    public void Dismiss()
    {
        PopupGuard.Unwatch(this);
        if (!IsVisible) return;
        _hiddenAt = DateTime.Now;
        Hide();
        MemoryTrim.Schedule();
    }

    // ---------- mouse on an icon ----------

    static TrayIcon IconOf(object sender) => (TrayIcon)((FrameworkElement)sender).DataContext;
    static bool Ctrl => (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    void OnIconDown(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !Ctrl) { Dismiss(); _host.Click(IconOf(s), TrayHost.Button.LeftDouble); }
    }

    void OnIconUp(object s, MouseButtonEventArgs e)
    {
        var t = IconOf(s);
        if (Ctrl) { _host.SetPinned(t, true); return; }          // Ctrl+click: pin this icon to the taskbar
        if (e.ClickCount >= 2) return;
        Dismiss();
        _host.Click(t, TrayHost.Button.Left);
    }

    void OnIconRight(object s, MouseButtonEventArgs e)
    {
        var t = IconOf(s);
        if (Ctrl) { _host.SetPinned(t, true); return; }
        // The flyout stays up under the program's menu, as in Windows 10 (a press anywhere outside it still closes it).
        _host.Click(t, TrayHost.Button.Right);
    }
}
