using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Controls;

/// <summary>A visible, keyboard-accessible annotation next to an AI-filled field.</summary>
public sealed class PrefillMarker : StackPanel
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(FrameworkElement), typeof(PrefillMarker));
    public FrameworkElement? Target { get => (FrameworkElement?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    // Tunnel requests so the shell can guard mouse, keyboard and automation activation before a field changes.
    public static readonly RoutedEvent ConfirmRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(ConfirmRequested), RoutingStrategy.Tunnel, typeof(RoutedEventHandler), typeof(PrefillMarker));
    public static readonly RoutedEvent ClearRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(ClearRequested), RoutingStrategy.Tunnel, typeof(RoutedEventHandler), typeof(PrefillMarker));

    public event RoutedEventHandler ConfirmRequested
    {
        add => AddHandler(ConfirmRequestedEvent, value);
        remove => RemoveHandler(ConfirmRequestedEvent, value);
    }

    public event RoutedEventHandler ClearRequested
    {
        add => AddHandler(ClearRequestedEvent, value);
        remove => RemoveHandler(ClearRequestedEvent, value);
    }

    public PrefillMarker()
    {
        Visibility = Visibility.Collapsed;
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(StyleProperty, "Primer.Muted");
        var status = new Run();
        status.SetBinding(Run.TextProperty, TrExtension.CreateBinding("Control.Prefilled"));
        var caption = new Run();
        caption.SetBinding(Run.TextProperty, TrExtension.CreateBinding("Control.Confirm"));
        var confirm = new Hyperlink(caption);
        confirm.SetBinding(AutomationProperties.NameProperty, TrExtension.CreateBinding("Control.Confirm"));
        confirm.Click += (_, _) => RaiseEvent(new RoutedEventArgs(ConfirmRequestedEvent, this));
        label.Inlines.Add(status);
        label.Inlines.Add("  ");
        label.Inlines.Add(confirm);
        var clear = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        clear.SetResourceReference(StyleProperty, "Primer.Quiet");
        clear.SetBinding(ContentControl.ContentProperty, TrExtension.CreateBinding("Control.Clear"));
        clear.SetBinding(AutomationProperties.NameProperty, TrExtension.CreateBinding("Control.Clear"));
        clear.Click += (_, _) => RaiseEvent(new RoutedEventArgs(ClearRequestedEvent, this));
        Children.Add(label);
        Children.Add(clear);
        AutomationProperties.SetLiveSetting(label, AutomationLiveSetting.Polite);
    }

    internal void SetPending(bool pending)
    {
        Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        if (Target != null)
        {
            if (pending) Target.SetBinding(AutomationProperties.HelpTextProperty, TrExtension.CreateBinding("Control.Prefilled"));
            else Target.ClearValue(AutomationProperties.HelpTextProperty);
            Target.ToolTip = pending ? Loc.Current["Control.Prefilled"] : null;
        }
        if (pending) AccessibleStatus.Announce((TextBlock)Children[0]);
    }
}
