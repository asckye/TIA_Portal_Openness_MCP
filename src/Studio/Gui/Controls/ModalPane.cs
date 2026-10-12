using System;
using System.Collections.Generic;
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
    private readonly List<(UIElement Element, bool Enabled)> _background = [];
    private bool _closed;

    internal ModalPane(FrameworkElement pane, FrameworkElement initial, Action dismiss)
    {
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
                    if (sibling != branch && sibling.Visibility == Visibility.Visible)
                    {
                        _background.Add((sibling, sibling.IsEnabled));
                        sibling.SetCurrentValue(UIElement.IsEnabledProperty, false);
                    }
            branch = parent;
        }
        pane.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!_closed && pane.IsVisible) initial.Focus();
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
        foreach (var (element, enabled) in _background) element.SetCurrentValue(UIElement.IsEnabledProperty, enabled);
        if (_previous is UIElement { IsVisible: true, IsEnabled: true } previous) previous.Focus();
    }
}
