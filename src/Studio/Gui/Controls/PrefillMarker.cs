using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Controls;

/// <summary>A visible, keyboard-accessible annotation next to an AI-filled field.</summary>
public sealed class PrefillMarker : StackPanel
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(FrameworkElement), typeof(PrefillMarker));
    public FrameworkElement? Target { get => (FrameworkElement?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    public event EventHandler? ClearRequested;

    public PrefillMarker()
    {
        Visibility = Visibility.Collapsed;
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(StyleProperty, "Primer.Muted");
        label.SetBinding(TextBlock.TextProperty, TrExtension.CreateBinding("Control.Prefilled"));
        var clear = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        clear.SetResourceReference(StyleProperty, "Primer.Quiet");
        clear.SetBinding(ContentControl.ContentProperty, TrExtension.CreateBinding("Control.Clear"));
        clear.SetBinding(AutomationProperties.NameProperty, TrExtension.CreateBinding("Control.Clear"));
        clear.Click += (_, _) => ClearRequested?.Invoke(this, EventArgs.Empty);
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
