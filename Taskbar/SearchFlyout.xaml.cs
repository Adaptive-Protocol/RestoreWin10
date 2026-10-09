using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace RestoreWin10;

/// <summary>
/// The taskbar's search pane: a text box that sits exactly over the taskbar's search slot, with matching apps
/// listed above (or below, when the bar is at the top). It is a window of its own because the taskbar never
/// takes keyboard focus. Click-away or Esc dismisses it; the taskbar decides whether its slot stays open.
/// </summary>
public partial class SearchFlyout : Window
{
    readonly StartMenu _menu;
    bool _above;
    double _slotTopDip, _slotBottomDip;

    /// <summary>Current text; the taskbar shows it in the slot while the pane is closed.</summary>
    public event Action<string>? QueryChanged;
    /// <summary>The pane was hidden (click-away, Esc, or a launch).</summary>
    public event Action? Dismissed;

    public SearchFlyout(StartMenu menu)
    {
        InitializeComponent();
        _menu = menu;
        SourceInitialized += (_, _) =>
        {
            Personalization.Follow(this, Personalization.Kind.Flyout);
        };
        SizeChanged += (_, _) => { if (_above && IsVisible) Top = _slotBottomDip - ActualHeight; };

        new WindowInteropHelper(this).EnsureHandle();
    }

    /// <summary>Opens over the slot at <paramref name="slotPx"/> (screen pixels) with <paramref name="text"/> selected.</summary>
    public void Open(Rect slotPx, bool above, string text)
    {
        _above = above;
        DockPanel.SetDock(Box, above ? Dock.Bottom : Dock.Top);
        var s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Width = slotPx.Width / s;
        Box.Height = slotPx.Height / s;
        _slotTopDip = slotPx.Y / s; _slotBottomDip = slotPx.Bottom / s;
        Left = slotPx.X / s;
        Input.Text = text;
        Refresh();
        Show();
        UpdateLayout();
        Top = above ? _slotBottomDip - ActualHeight : _slotTopDip;
        Activate();
        Input.Focus();
        Input.SelectAll();
        // Presses on windows that never take focus (the taskbar) cause no Deactivated event: the guard closes us then.
        PopupGuard.Watch(this, PopupGuard.FlyoutOf(new WindowInteropHelper(this).Handle), Dismiss);
    }

    public void Dismiss()
    {
        PopupGuard.Unwatch(this);
        if (!IsVisible) return;
        Hide();
        Dismissed?.Invoke();
        MemoryTrim.Schedule();
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

    void Refresh()
    {
        var q = Input.Text.Trim();
        Hint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        IReadOnlyList<AppEntry> items = q.Length == 0 ? Array.Empty<AppEntry>() : _menu.Find(q, 8);
        Results.ItemsSource = items;
        Results.SelectedIndex = items.Count > 0 ? 0 : -1;
        Header.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = items.Count == 0 && q.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Pane.Visibility = items.Count > 0 || q.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnTextChanged(object s, TextChangedEventArgs e)
    {
        Refresh();
        QueryChanged?.Invoke(Input.Text);
    }

    void OnKey(object s, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Dismiss(); e.Handled = true; break;
            case Key.Enter: Launch(Results.SelectedItem as AppEntry); e.Handled = true; break;
            case Key.Down: Move(+1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
        }
    }

    void Move(int delta)
    {
        int n = Results.Items.Count;
        if (n == 0) return;
        Results.SelectedIndex = ((Results.SelectedIndex + delta) % n + n) % n;
    }

    void OnResultClick(object s, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Results, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: AppEntry a }) Launch(a);
    }

    /// <summary>
    /// Launches <paramref name="a"/>, or with nothing selected the top result, then clears and closes. Text that matched
    /// nothing is never handed to Windows to run (decision D5, threat model F-10): the results already include anything
    /// the Run box would recognise ("regedit", "services.msc").
    /// </summary>
    void Launch(AppEntry? a)
    {
        a ??= Results.Items.Count > 0 ? Results.Items[0] as AppEntry : null;
        if (a == null) return;    // nothing recognised: the box stays open with the text
        Input.Text = "";          // the slot collapses once we are dismissed with nothing in it
        Dismiss();
        _menu.Launch(a);
    }

    void OnDeactivated(object? s, EventArgs e) => Dismiss();
}
