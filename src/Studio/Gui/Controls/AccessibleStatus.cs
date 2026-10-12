using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace TiaOpenness.Gui.Controls;

internal static class AccessibleStatus
{
    internal static event Action<FrameworkElement>? Announced;
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(AccessibleStatus), new PropertyMetadata("", Changed));
    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);
    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is FrameworkElement view && !Equals(e.OldValue, e.NewValue)) Announce(view);
    }

    internal static void Announce(FrameworkElement element)
    {
        if (AutomationProperties.GetLiveSetting(element) == AutomationLiveSetting.Off)
            AutomationProperties.SetLiveSetting(element, AutomationLiveSetting.Polite);
        var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        Announced?.Invoke(element);
    }
}
