using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace RestoreWin10;

/// <summary>The Windows 10 volume flyout: device name (with two or more playback devices, a button that opens the list
/// of devices to switch between), mute button, slider, level.</summary>
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

    // Where the flyout is anchored: above the taskbar its bottom edge stays put when the device list opens or closes.
    bool _above;
    double _anchorEdge;

    public void Toggle(Rect anchorPx, bool above)
    {
        if (IsVisible) { Dismiss(); return; }
        if ((DateTime.Now - _hiddenAt).TotalMilliseconds < 250) return;   // the same click just closed us
        ShowList(false);
        UpdateHeader();
        Refresh();
        var s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _above = above;
        _anchorEdge = above ? anchorPx.Top / s - 4 : anchorPx.Bottom / s + 4;
        Left = Math.Max(0, anchorPx.Right / s - Width);
        Place();
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

    /// <summary>Puts the flyout against its anchor for its current content: above the taskbar it grows upwards.</summary>
    void Place()
    {
        var content = (FrameworkElement)Content;
        content.Measure(new Size(Width, double.PositiveInfinity));
        double h = content.DesiredSize.Height;
        Top = _above ? _anchorEdge - h : _anchorEdge;
    }

    /// <summary>A device in the list (bound by the template): its name, and a marker and bold text for the current one.</summary>
    public sealed record DeviceRow(string Id, string Name, bool Current)
    {
        public Visibility Marker => Current ? Visibility.Visible : Visibility.Hidden;
        public FontWeight Weight => Current ? FontWeights.SemiBold : FontWeights.Normal;
    }

    /// <summary>The list of playback devices open (true) or the volume slider (false), as Windows 10 switches them.</summary>
    void ShowList(bool open)
    {
        DeviceList.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        LevelRow.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        DeviceChevron.Text = open ? "" : "";   // chevron down while open, up while closed
        if (IsVisible) Place();
    }

    void OnDeviceHeader(object sender, RoutedEventArgs e)
    {
        if (DeviceList.Visibility == Visibility.Visible) { ShowList(false); return; }
        var (devices, current) = Audio.Devices();
        if (devices.Count < 2) { ShowList(false); return; }
        DeviceList.ItemsSource = devices.Select(d => new DeviceRow(d.Id, d.Name, d.Id == current)).ToList();
        ShowList(true);
    }

    void OnDeviceChosen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id) return;
        // Not possible without Windows' interface: Settings > Sound lets the user choose instead.
        if (!Audio.SetDefault(id)) { Dismiss(); Launcher.Settings("ms-settings:sound"); return; }
        ShowList(false);
        UpdateHeader();
        Refresh();
        Changed?.Invoke();
        // Audible confirmation on the device just chosen: Windows' "Default Beep", as Windows 10's volume flyout plays.
        // Windows takes a moment to move playback to the new default, so the sound waits for it.
        var ding = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        ding.Tick += (_, _) => { ding.Stop(); Ding(); };
        ding.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool MessageBeep(uint type);

    /// <summary>The header opens the list only when there is something to choose from (Windows 10 shows the chevron
    /// then). Checked when the flyout opens and after a switch, not on every half-second refresh.</summary>
    void UpdateHeader()
    {
        bool several = Audio.Devices().Devices.Count >= 2;
        DeviceButton.IsEnabled = several;
        DeviceChevron.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
        DeviceButton.ToolTip = several ? "Choose the playback device" : null;
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

    // ---------- the slider: press anywhere, then drag ----------

    bool _sliding;

    /// <summary>
    /// The level under the mouse, straight from its position on the slider's track. (Not Track.ValueFromPoint: that
    /// adds the distance from the thumb's last laid-out position to the current value, so a value set a moment
    /// earlier, before layout caught up, made the next reading overshoot and clicks landed in the wrong place.)
    /// </summary>
    double LevelAt(System.Windows.Input.MouseEventArgs e)
    {
        if (Level.Template.FindName("PART_Track", Level) is System.Windows.Controls.Primitives.Track track && track.ActualWidth > 0)
            return LevelFromX(e.GetPosition(track).X, track.ActualWidth, track.Thumb?.ActualWidth ?? 0, Level.Minimum, Level.Maximum);
        return Level.Value;
    }

    /// <summary>
    /// The whole-number level for a point <paramref name="x"/> along a track <paramref name="trackWidth"/> wide: the
    /// thumb's centre can travel from half a thumb in from one end to half a thumb in from the other.
    /// </summary>
    internal static double LevelFromX(double x, double trackWidth, double thumbWidth, double min, double max)
    {
        double span = trackWidth - thumbWidth;
        if (span <= 0) return min;
        return Math.Round(Math.Clamp(min + (x - thumbWidth / 2) / span * (max - min), min, max));
    }

    void OnLevelDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!Level.IsEnabled) return;
        e.Handled = true;              // the thumb and the track buttons do not run their own (jump-only) handling
        _sliding = true;               // set before capture: taking capture re-sends a mouse move (R12)
        Level.Value = LevelAt(e);
        Level.CaptureMouse();
    }

    void OnLevelMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_sliding && Level.IsMouseCaptured) Level.Value = LevelAt(e);
    }

    void OnLevelUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_sliding) return;
        e.Handled = true;
        _sliding = false;              // before releasing: releasing capture re-sends a mouse move (R12)
        Level.ReleaseMouseCapture();
        Ding();                        // as Windows 10 does on letting go of the slider: heard at the new level
    }

    /// <summary>Windows' "Default Beep" (MessageBeep MB_OK), the sound Windows 10's volume flyout plays.</summary>
    static void Ding() => MessageBeep(0);

    void OnLevelLost(object sender, System.Windows.Input.MouseEventArgs e) => _sliding = false;

    void OnMute(object sender, RoutedEventArgs e)
    {
        var s = Audio.Get();
        if (s == null) return;
        Audio.SetMute(!s.Mute);
        Refresh();
        Changed?.Invoke();
    }

    void OnDeactivated(object? sender, EventArgs e) => Dismiss();

#if DEV_BUILD
    /// <summary>Development builds only: draws the flyout, closed or with the device list open, into a PNG without
    /// showing it (for checking the layout; nothing is changed).</summary>
    internal void Snapshot(string file, bool listOpen)
    {
        UpdateHeader();
        Refresh();
        if (listOpen)
        {
            var (devices, current) = Audio.Devices();
            DeviceList.ItemsSource = devices.Select(d => new DeviceRow(d.Id, d.Name, d.Id == current)).ToList();
        }
        ShowList(listOpen);
        var content = (FrameworkElement)Content;
        content.Measure(new Size(Width, double.PositiveInfinity));
        content.Arrange(new Rect(new Size(Width, content.DesiredSize.Height)));
        content.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Width, (int)Math.Ceiling(content.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(content);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(file);
        png.Save(fs);
    }
#endif
}
