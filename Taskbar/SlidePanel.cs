using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RestoreWin10;

/// <summary>A StackPanel whose children glide to their new slot when the order (or size) changes.</summary>
public sealed class SlidePanel : StackPanel
{
    static readonly Duration Slide = new(TimeSpan.FromMilliseconds(180));
    Dictionary<UIElement, Point> _last = new();

    /// <summary>Layout rectangle of the child at <paramref name="index"/>, in this panel's coordinates.</summary>
    public Rect SlotOf(int index) => LayoutInformation.GetLayoutSlot((FrameworkElement)InternalChildren[index]);

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = base.ArrangeOverride(arrangeSize);
        var now = new Dictionary<UIElement, Point>();
        foreach (UIElement child in InternalChildren)
        {
            var p = LayoutInformation.GetLayoutSlot((FrameworkElement)child).TopLeft;
            now[child] = p;
            if (child is FrameworkElement { DataContext: IDragItem { IsDragging: true } })
            {
                if (child.RenderTransform is TranslateTransform held) { held.BeginAnimation(TranslateTransform.XProperty, null); held.X = 0; }
                continue;
            }
            if (IsLoaded && _last.TryGetValue(child, out var old) && (old - p).Length > 0.5)
            {
                if (child.RenderTransform is not TranslateTransform tt) child.RenderTransform = tt = new TranslateTransform();
                double fx = tt.X + old.X - p.X, fy = tt.Y + old.Y - p.Y;   // where it visually is now (a slide may be mid-way)
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fx, 0, Slide) { EasingFunction = ease });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fy, 0, Slide) { EasingFunction = ease });
            }
        }
        _last = now;
        return size;
    }
}
