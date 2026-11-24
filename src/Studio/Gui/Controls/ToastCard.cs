using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shapes;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Controls;

internal sealed class ToastCard
{
    internal Grid Host { get; }
    internal Border Surface { get; }
    internal Ellipse Dot { get; }
    internal TextBlock Title { get; }
    internal TextBlock Message { get; }
    internal Button Close { get; }
    internal Button Action { get; }
    internal Button SecondAction { get; }
    private readonly Path _icon;
    private readonly ScrollViewer _description;
    internal ToastNotice? Notice { get; private set; }
    internal bool ShowDetails { get; private set; }

    internal ToastCard(DataTemplate template)
    {
        Host = (Grid)template.LoadContent();
        T Part<T>(string name) where T : FrameworkElement => (T)Host.FindName(name);
        Surface = Part<Border>("ToastSurface");
        Dot = Part<Ellipse>("ToastDot");
        _icon = Part<Path>("ToastIcon");
        Title = Part<TextBlock>("ToastTitle");
        Message = Part<TextBlock>("ToastMessage");
        _description = Part<ScrollViewer>("DescriptionScroll");
        Close = Part<Button>("ToastClose");
        Action = Part<Button>("ToastAction");
        SecondAction = Part<Button>("ToastSecondAction");
        Surface.Visibility = Visibility.Collapsed;
    }

    internal void Display(ToastNotice? notice, double width)
    {
        if (!ReferenceEquals(Notice, notice)) _description.ScrollToTop();
        Notice = notice;
        Surface.Visibility = notice == null ? Visibility.Collapsed : Visibility.Visible;
        if (notice == null) return;
        _description.MaxHeight = notice.Sticky ? double.PositiveInfinity : 64;
        _description.VerticalScrollBarVisibility = notice.Sticky ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        Title.Text = notice.Title.Resolve();
        Title.Visibility = Title.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Message.Text = notice.Text.Resolve();
        Message.FontWeight = Title.Visibility == Visibility.Collapsed ? FontWeights.SemiBold : FontWeights.Normal;
        Surface.ToolTip = notice.FullText;
        AutomationProperties.SetName(Surface, notice.FullText);
        AutomationProperties.SetName(Message, notice.FullText);
        AutomationProperties.SetLiveSetting(Message, notice.Kind == ToastKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        var (color, icon) = notice.Kind switch
        {
            ToastKind.Error => ("Ant.colorError", "Icon.Error"),
            ToastKind.Warning => ("Primer.warn", "Icon.Warning"),
            ToastKind.Success => ("Primer.ok", "Icon.Check"),
            _ => ("Primer.accent", "Icon.Info")
        };
        _icon.SetResourceReference(Path.DataProperty, icon);
        Dot.SetResourceReference(Shape.FillProperty, color);
        Action.SetResourceReference(Control.ForegroundProperty, color);
        Surface.SetResourceReference(Border.BorderBrushProperty, notice.Kind is ToastKind.Error or ToastKind.Warning ? color : "Primer.inputBorder");

        Close.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var inset = Surface.Padding.Left + Surface.Padding.Right + Surface.BorderThickness.Left + Surface.BorderThickness.Right;
        var content = (FrameworkElement)Title.Parent;
        var textWidth = width - inset - ((FrameworkElement)Dot.Parent).Width - Close.DesiredSize.Width - content.Margin.Left - content.Margin.Right;
        Message.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bool needsDetails = notice.Kind == ToastKind.Error || Message.Text.Contains('\n') || Message.DesiredSize.Width > textWidth;
        bool detailsLabel = needsDetails && !notice.Sticky && notice.SecondAction == null;
        ShowDetails = detailsLabel && !notice.ActionShowsDetails;
        string actionLabel = notice.ActionLabel.Resolve();
        if (actionLabel.Length == 0) actionLabel = Loc.Current[notice.ActionKey];
        Action.Content = detailsLabel ? Loc.Current["Toast.Details"] : actionLabel;
        Action.Visibility = notice.Action != null || detailsLabel ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(Action, ((string)Action.Content).TrimEnd(' ', '→'));
        bool originalAction = ShowDetails && notice.Action != null;
        SecondAction.Visibility = notice.SecondAction != null || originalAction ? Visibility.Visible : Visibility.Collapsed;
        SecondAction.Content = originalAction ? actionLabel : notice.SecondActionKey.Resolve();
        string help = Action.Visibility == Visibility.Visible ? Loc.Current.T("Toast.ActionShortcut", Action.Content) : "";
        AutomationProperties.SetHelpText(Surface, help);
        AutomationProperties.SetHelpText(Message, help);
    }
}
