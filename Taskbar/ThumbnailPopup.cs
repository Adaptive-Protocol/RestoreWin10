using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace RestoreWin10;

/// <summary>
/// The hover preview above a taskbar button: one card per window with its title, a close button and a
/// live DWM thumbnail (drawn by Windows itself, so it stays up to date without any work on our side).
/// </summary>
public sealed class ThumbnailPopup : Window
{
    const double CardW = 220, ThumbW = 204, ThumbH = 115;

    readonly List<nint> _thumbs = new();
    readonly List<(nint src, Border slot)> _slots = new();
    // Windows whose X was clicked in this preview. Their cards go at once, as in Windows 10, instead of when the
    // program has closed and the next refresh notices (up to half a second later). Forgotten when the preview closes,
    // so a window that stays (a program asking to save) is in the preview again next time.
    readonly HashSet<nint> _closing = new();
    public IReadOnlyList<nint> Current { get; private set; } = Array.Empty<nint>();

    /// <summary>The windows of <paramref name="item"/> this preview shows: all but those whose X was just clicked.</summary>
    public List<nint> Showing(TaskItem item) => item.Windows.Where(w => !_closing.Contains(w)).ToList();

    /// <summary>Leaves <paramref name="hwnd"/> out of this preview from now on: its close has just been asked for.</summary>
    public void LeaveOut(nint hwnd) => _closing.Add(hwnd);

    public event Action<nint>? WindowChosen;
    public event Action<nint>? CloseRequested;
    public event Action? PointerEntered;
    public event Action? PointerLeft;

    public ThumbnailPopup()
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        ShowActivated = false; SizeToContent = SizeToContent.WidthAndHeight; Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual; UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Win.SetWindowLong(h, Win.GWL_EXSTYLE, Win.GetWindowLong(h, Win.GWL_EXSTYLE) | Win.WS_EX_TOOLWINDOW | Win.WS_EX_NOACTIVATE);
            HwndSource.FromHwnd(h)?.AddHook((nint _, int msg, nint _, nint _, ref bool handled) =>
            {
                if (msg == 0x0021) { handled = true; return 3; }   // never take focus when clicked
                return 0;
            });
            Personalization.Follow(this, Personalization.Kind.Flyout);
        };
        MouseEnter += (_, _) => PointerEntered?.Invoke();
        MouseLeave += (_, _) => PointerLeft?.Invoke();
        new WindowInteropHelper(this).EnsureHandle();
    }

    /// <summary>Shows previews of <paramref name="item"/>'s windows next to the button at <paramref name="anchorPx"/> (screen pixels).</summary>
    public void Present(TaskItem item, Rect anchorPx, bool above)
    {
        ReleaseThumbnails();
        Current = Showing(item);
        if (Current.Count == 0) { Dismiss(); return; }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
        foreach (var h in Current) row.Children.Add(BuildCard(item, h));
        Content = row;

        // Position: centred on the button, kept within the work area of the button's own screen (any monitor).
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = row.DesiredSize;
        var work = Screens.FromPoint((int)(anchorPx.X + anchorPx.Width / 2), (int)(anchorPx.Y + anchorPx.Height / 2)).Work;
        double left = anchorPx.X / scale + anchorPx.Width / scale / 2 - size.Width / 2;
        Left = Math.Max(work.Left / scale, Math.Min(left, work.Right / scale - size.Width));
        Top = above ? anchorPx.Y / scale - size.Height - 4 : (anchorPx.Y + anchorPx.Height) / scale + 4;

        if (!IsVisible) Show();
        UpdateLayout();
        RegisterThumbnails();
    }

    Border BuildCard(TaskItem item, nint hwnd)
    {
        var title = new TextBlock { Text = Win.Text(hwnd), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var close = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10,
            Width = 28, Height = 28, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 9, 0, 0), Background = Brushes.Transparent,
            Visibility = Visibility.Hidden, ToolTip = "Close window",
        };
        close.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        close.MouseEnter += (_, _) => { close.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)); close.Foreground = Brushes.White; };
        close.MouseLeave += (_, _) => { close.Background = Brushes.Transparent; close.SetResourceReference(TextBlock.ForegroundProperty, "Fg"); };
        close.MouseLeftButtonUp += (_, e) => { CloseRequested?.Invoke(hwnd); e.Handled = true; };

        var header = new Grid { Height = 28 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new Image { Source = item.Icon, Width = 16, Height = 16, Margin = new Thickness(8, 0, 8, 0) });
        Grid.SetColumn(title, 1); header.Children.Add(title);
        Grid.SetColumn(close, 2); header.Children.Add(close);

        var slot = new Border { Width = ThumbW, Height = ThumbH, Margin = new Thickness(8, 2, 8, 8) };
        slot.SetResourceReference(Border.BackgroundProperty, "ThumbSlot");
        _slots.Add((hwnd, slot));

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(slot);

        var card = new Border { Width = CardW, Background = Brushes.Transparent, Child = stack, Cursor = Cursors.Arrow };
        card.MouseEnter += (_, _) => { card.SetResourceReference(Border.BackgroundProperty, "Hover"); close.Visibility = Visibility.Visible; };
        card.MouseLeave += (_, _) => { card.Background = Brushes.Transparent; close.Visibility = Visibility.Hidden; };
        card.MouseLeftButtonUp += (_, _) => WindowChosen?.Invoke(hwnd);
        return card;
    }

    void RegisterThumbnails()
    {
        var dest = new WindowInteropHelper(this).Handle;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (var (src, slot) in _slots)
        {
            if (Win.DwmRegisterThumbnail(dest, src, out var thumb) != 0) continue;
            _thumbs.Add(thumb);

            // Fit the window's picture inside the slot, keeping its aspect ratio.
            Win.DwmQueryThumbnailSourceSize(thumb, out var sz);
            double w = ThumbW, h = ThumbH;
            if (sz.cx > 0 && sz.cy > 0)
            {
                double k = Math.Min(ThumbW / sz.cx, ThumbH / sz.cy);
                if (k > 1) k = 1;
                w = sz.cx * k; h = sz.cy * k;
            }
            var origin = slot.TransformToAncestor(this).Transform(new Point());
            int x = (int)Math.Round((origin.X + (ThumbW - w) / 2) * scale), y = (int)Math.Round((origin.Y + (ThumbH - h) / 2) * scale);
            var props = new Win.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Win.DWM_TNP_RECTDESTINATION | Win.DWM_TNP_VISIBLE | Win.DWM_TNP_OPACITY,
                rcDestination = new Win.RECT { Left = x, Top = y, Right = x + (int)Math.Round(w * scale), Bottom = y + (int)Math.Round(h * scale) },
                opacity = 255, fVisible = 1,
            };
            Win.DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    void ReleaseThumbnails()
    {
        foreach (var t in _thumbs) Win.DwmUnregisterThumbnail(t);
        _thumbs.Clear(); _slots.Clear();
    }

    public void Dismiss()
    {
        ReleaseThumbnails();
        _closing.Clear();
        Current = Array.Empty<nint>();
        if (IsVisible) Hide();
    }
}
