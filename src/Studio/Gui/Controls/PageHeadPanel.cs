using System;
using System.Windows;
using System.Windows.Controls;

namespace TiaOpenness.Gui.Controls;

/// <summary>
/// Ant PageHead row (Flex justify space-between, wrap): the first child is the heading, the second its actions.
/// The actions stay at the right edge while the title fits beside them and wrap below the heading, left aligned,
/// when it does not, so a title never runs under the actions or loses its end.
/// </summary>
public sealed class PageHeadPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double),
        typeof(PageHeadPanel), new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The part of the heading (a descendant of the first child) that must fit beside the actions; the
    /// secondary lines below it may trim. Without it the whole heading must fit.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(UIElement),
        typeof(PageHeadPanel), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private bool _wrapped;

    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public UIElement? Title { get => (UIElement?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>True while the actions sit below the heading.</summary>
    public bool IsWrapped => _wrapped;

    private UIElement? Heading => InternalChildren.Count > 0 ? InternalChildren[0] : null;
    private UIElement? Actions => InternalChildren.Count > 1 && InternalChildren[1].Visibility != Visibility.Collapsed ? InternalChildren[1] : null;

    protected override Size MeasureOverride(Size available)
    {
        var heading = Heading;
        var actions = Actions;
        for (int i = 2; i < InternalChildren.Count; i++) InternalChildren[i].Measure(new Size(0, 0));
        if (heading == null) return new Size();
        if (actions == null)
        {
            _wrapped = false;
            heading.Measure(available);
            return heading.DesiredSize;
        }
        actions.Measure(new Size(double.PositiveInfinity, available.Height));
        Size side = actions.DesiredSize;
        heading.Measure(new Size(double.PositiveInfinity, available.Height));
        double natural = heading.DesiredSize.Width;
        double title = Title is { Visibility: Visibility.Visible } part ? part.DesiredSize.Width : natural;
        _wrapped = !double.IsInfinity(available.Width) && title + Spacing + side.Width > available.Width;
        if (!_wrapped)
        {
            double width = double.IsInfinity(available.Width) ? double.PositiveInfinity : Math.Max(0, available.Width - Spacing - side.Width);
            heading.Measure(new Size(width, available.Height));
            return new Size(heading.DesiredSize.Width + Spacing + side.Width, Math.Max(heading.DesiredSize.Height, side.Height));
        }
        heading.Measure(new Size(available.Width, double.PositiveInfinity));
        actions.Measure(new Size(available.Width, double.PositiveInfinity));
        return new Size(Math.Max(heading.DesiredSize.Width, actions.DesiredSize.Width),
            heading.DesiredSize.Height + Spacing + actions.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var heading = Heading;
        var actions = Actions;
        for (int i = 2; i < InternalChildren.Count; i++) InternalChildren[i].Arrange(new Rect());
        if (heading == null) return final;
        if (actions == null)
        {
            heading.Arrange(new Rect(final));
            return final;
        }
        double side = actions.DesiredSize.Width;
        if (!_wrapped)
        {
            heading.Arrange(new Rect(0, 0, Math.Max(0, final.Width - Spacing - side), final.Height));
            actions.Arrange(new Rect(Math.Max(0, final.Width - side), 0, Math.Min(side, final.Width), final.Height));
            return final;
        }
        double top = heading.DesiredSize.Height;
        heading.Arrange(new Rect(0, 0, final.Width, top));
        actions.Arrange(new Rect(0, top + Spacing, Math.Min(side, final.Width), actions.DesiredSize.Height));
        return final;
    }
}
