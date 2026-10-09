using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace RestoreWin10;

/// <summary>The Windows 10 volume flyout: device name, mute button, slider, level.</summary>
public partial class VolumeFlyout : Window
{
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    bool _updating;
    DateTime _hiddenAt = DateTime.MinValue;
    nint _hwnd;

    /// <summary>Raised whenever the level or mute state changes here, so the taskbar's glyph can follow at once.</summary>
    public event Action? Changed;

    public VolumeFlyout()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win.SetWindowLong(_hwnd, Win.GWL_EXSTYLE, Win.GetWindowLong(_hwnd, Win.GWL_EXSTYLE) | Win.WS_EX_TOOLWINDOW);
            Personalization.Follow(this, Personalization.Kind.Flyout);
        };
        _poll.Tick += (_, _) => Refresh();   // volume keys, other apps: keep the slider honest while open
        new WindowInteropHelper(this).EnsureHandle();
    }

    public void Toggle(Rect anchorPx, bool above)
    {
        if (IsVisible) { Dismiss(); return; }
        if ((DateTime.Now - _hiddenAt).TotalMilliseconds < 250) return;   // the same click just closed us
        Refresh();
        var s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var content = (FrameworkElement)Content;
        content.Measure(new Size(Width, double.PositiveInfinity));
        double h = content.DesiredSize.Height;
        Left = Math.Max(0, anchorPx.Right / s - Width);
        Top = above ? anchorPx.Top / s - h - 4 : anchorPx.Bottom / s + 4;
        Show();
        Activate();
        _poll.Start();
        PopupGuard.Watch(this, PopupGuard.FlyoutOf(_hwnd), Dismiss);
    }

    public void Dismiss()
    {
        PopupGuard.Unwatch(this);
        _poll.Stop();
        if (!IsVisible) return;
        _hiddenAt = DateTime.Now;
        Hide();
        MemoryTrim.Schedule();
    }

    void Refresh()
    {
        var s = Audio.Get();
        _updating = true;
        try
        {
            if (s == null)
            {
                DeviceName.Text = "No playback device";
                Level.IsEnabled = false; LevelText.Text = "";
                MuteGlyph.Text = "";
                return;
            }
            Level.IsEnabled = true;
            DeviceName.Text = s.Device.Length > 0 ? s.Device : "Speakers";
            int pct = (int)Math.Round(s.Level * 100);
            if (!Level.IsMouseCaptureWithin) Level.Value = pct;
            LevelText.Text = pct.ToString();
            MuteGlyph.Text = Audio.Glyph(s);
        }
        finally { _updating = false; }
    }

    void OnLevel(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        Audio.SetLevel((float)(e.NewValue / 100));
        if (e.NewValue > 0) Audio.SetMute(false);   // moving the slider un-mutes, as Windows does
        LevelText.Text = ((int)Math.Round(e.NewValue)).ToString();
        MuteGlyph.Text = Audio.Glyph(Audio.Get());
        Changed?.Invoke();
    }

    void OnMute(object sender, RoutedEventArgs e)
    {
        var s = Audio.Get();
        if (s == null) return;
        Audio.SetMute(!s.Mute);
        Refresh();
        Changed?.Invoke();
    }

    void OnDeactivated(object? sender, EventArgs e) => Dismiss();
}
