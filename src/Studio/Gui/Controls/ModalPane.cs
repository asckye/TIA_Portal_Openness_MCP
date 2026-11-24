using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TiaOpenness.Gui.Controls;

/// <summary>Keyboard and background isolation for the workbench's in-window modal panes.</summary>
internal sealed class ModalPane : IDisposable
{
    private readonly FrameworkElement _pane;
    private readonly Action _dismiss;
    private readonly IInputElement? _previous;
    private readonly Action? _restoreFocus;
    private readonly List<UIElement> _background = [];
    private bool _closed;

    private static readonly DependencyProperty BlockCountProperty = DependencyProperty.RegisterAttached(
        "BlockCount", typeof(int), typeof(ModalPane), new PropertyMetadata(0, (element, _) => element.CoerceValue(UIElement.IsEnabledProperty)));

    static ModalPane()
    {
        // Siblings can be arbitrary WPF controls. This final coercion runs after their own IsEnabled coercion
        // and leaves bindings, current values and inherited state under WPF's control.
        var enabled = DependencyPropertyDescriptor.FromProperty(UIElement.IsEnabledProperty, typeof(UIElement));
        var previous = enabled.DesignerCoerceValueCallback;
        enabled.DesignerCoerceValueCallback = (element, value) =>
            (int)element.GetValue(BlockCountProperty) > 0 ? false : previous?.Invoke(element, value) ?? value;
    }

    internal ModalPane(FrameworkElement pane, FrameworkElement initial, Action dismiss)
        : this(pane, () => initial, dismiss) { }

    internal ModalPane(FrameworkElement pane, Func<FrameworkElement> initial, Action dismiss,
        UIElement? interactiveSibling = null, Action? restoreFocus = null)
    {
        _restoreFocus = restoreFocus;
        _pane = pane;
        _dismiss = dismiss;
        _previous = Keyboard.FocusedElement;
        AutomationProperties.SetIsDialog(pane, true);
        FocusManager.SetIsFocusScope(pane, true);
        KeyboardNavigation.SetTabNavigation(pane, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(pane, KeyboardNavigationMode.Cycle);
        pane.PreviewKeyDown += OnKeyDown;
        // Disable the siblings at each level, including the shell behind a page-local options pane.
        for (DependencyObject? branch = pane; branch != null;)
        {
            var parent = VisualTreeHelper.GetParent(branch);
            if (parent is Panel panel)
                foreach (UIElement sibling in panel.Children)
                    if (sibling != branch && sibling != interactiveSibling && sibling.Visibility == Visibility.Visible)
                    {
                        _background.Add(sibling);
                        sibling.SetValue(BlockCountProperty, (int)sibling.GetValue(BlockCountProperty) + 1);
                    }
            branch = parent;
        }
        pane.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!_closed && pane.IsVisible) initial().Focus();
        }));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        _dismiss();
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _pane.PreviewKeyDown -= OnKeyDown;
        foreach (var element in _background)
        {
            int remaining = (int)element.GetValue(BlockCountProperty) - 1;
            if (remaining == 0) element.ClearValue(BlockCountProperty);
            else element.SetValue(BlockCountProperty, remaining);
        }
        if (_restoreFocus != null) _restoreFocus();
        else if (_previous is UIElement { IsVisible: true, IsEnabled: true } previous) previous.Focus();
    }
}
