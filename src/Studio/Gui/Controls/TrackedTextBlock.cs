using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TiaOpenness.Gui.Controls;

/// <summary>Applies the handoff's em tracking without changing the accessible text.</summary>
public sealed class TrackedTextBlock : TextBlock
{
    public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(
        nameof(Tracking), typeof(double), typeof(TrackedTextBlock), new PropertyMetadata(0d, RefreshTracking));

    static TrackedTextBlock()
    {
        TextProperty.OverrideMetadata(typeof(TrackedTextBlock), new FrameworkPropertyMetadata(RefreshTracking));
        FontSizeProperty.OverrideMetadata(typeof(TrackedTextBlock), new FrameworkPropertyMetadata(RefreshTracking));
    }

    public double Tracking { get => (double)GetValue(TrackingProperty); set => SetValue(TrackingProperty, value); }

    private static void RefreshTracking(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var text = (TrackedTextBlock)sender;
        var effects = new TextEffectCollection();
        for (int i = 1; i < (text.Text?.Length ?? 0); i++)
            effects.Add(new TextEffect { PositionStart = i, PositionCount = 1, Transform = new TranslateTransform(i * text.FontSize * text.Tracking, 0) });
        text.TextEffects = effects;
    }
}
