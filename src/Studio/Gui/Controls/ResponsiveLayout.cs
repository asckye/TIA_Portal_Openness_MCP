using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace TiaOpenness.Gui.Controls;

/// <summary>Moves a page's context column below its content with scrolling at narrow widths.</summary>
public static class ResponsiveLayout
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ResponsiveLayout), new PropertyMetadata(false, Changed));
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(ResponsiveLayout));

    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Grid grid || e.NewValue is not true) return;
        var state = new State(grid);
        grid.SetValue(StateProperty, state);
        grid.SizeChanged += (_, _) => state.Queue();
        grid.Loaded += (_, _) => state.Queue();
    }

    private sealed class State(Grid grid)
    {
        private List<UIElement>? _children;
        private readonly Dictionary<FrameworkElement, double> _minimums = [];
        private readonly Dictionary<FrameworkElement, double> _heights = [];
        private ScrollViewer? _scroll;
        private ToggleButton? _toggle;
        private FrameworkElement? _side;
        private Visibility _sideVisibility;
        private GridLength _sideWidth, _gapWidth;
        private bool _collapsed, _changing;
        private bool _queued;
        internal void Queue()
        {
            if (_queued) return;
            _queued = true;
            grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _queued = false;
                bool narrow = grid.ActualWidth > 0 && grid.ActualWidth < 1008;
                EnsureContextToggle();
                if (narrow && _collapsed) CollapseContext(false);
                if (_toggle != null) _toggle.Visibility = !narrow && grid.ActualWidth < 1232
                    && (_collapsed || _side?.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
                if (narrow == (_scroll != null)) return;
                if (narrow)
                {
                    _children = grid.Children.Cast<UIElement>().Where(child => child != _toggle).ToList();
                    grid.Children.Clear();
                    var stack = new StackPanel();
                    foreach (var child in _children.OrderBy(Grid.GetColumn))
                    {
                        if (child is FrameworkElement view)
                        {
                            _minimums[view] = view.MinHeight;
                            _heights[view] = view.Height;
                            view.SetCurrentValue(FrameworkElement.MinHeightProperty,
                                Math.Max(view.MinHeight, Grid.GetColumn(view) == 2 ? 320 : 520));
                            // Lists keep a finite viewport so reflow does not defeat virtualization.
                            view.SetCurrentValue(FrameworkElement.HeightProperty,
                                Math.Max(view.MinHeight, grid.ActualHeight));
                        }
                        stack.Children.Add(child);
                    }
                    _scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                    Grid.SetColumnSpan(_scroll, grid.ColumnDefinitions.Count);
                    grid.Children.Add(_scroll);
                }
                else
                {
                    ((StackPanel)_scroll!.Content).Children.Clear();
                    grid.Children.Clear();
                    foreach (var child in _children!) grid.Children.Add(child);
                    if (_toggle != null) grid.Children.Add(_toggle);
                    foreach (var (view, minimum) in _minimums) view.SetCurrentValue(FrameworkElement.MinHeightProperty, minimum);
                    foreach (var (view, height) in _heights) view.SetCurrentValue(FrameworkElement.HeightProperty, height);
                    _minimums.Clear();
                    _heights.Clear();
                    _scroll = null;
                }
            }));
        }

        private void EnsureContextToggle()
        {
            if (_toggle != null || grid.ColumnDefinitions.Count < 3) return;
            _side = grid.Children.OfType<FrameworkElement>().FirstOrDefault(child => Grid.GetColumn(child) == 2);
            if (_side == null) return;
            _toggle = new ToggleButton { Content = "◂", Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Right };
            _toggle.SetResourceReference(FrameworkElement.StyleProperty, "Primer.ContextToggle");
            _toggle.SetBinding(AutomationProperties.NameProperty, Localization.TrExtension.CreateBinding("Layout.HideContext"));
            _toggle.SetBinding(FrameworkElement.ToolTipProperty, Localization.TrExtension.CreateBinding("Layout.HideContext"));
            _toggle.Click += (_, _) => CollapseContext(!_collapsed);
            _side.IsVisibleChanged += (_, _) =>
            {
                if (_changing) return;
                // Page changes reset the optional fold; the page still owns sidebar visibility.
                if (_collapsed)
                {
                    _collapsed = false;
                    grid.ColumnDefinitions[2].Width = _sideWidth; grid.ColumnDefinitions[1].Width = _gapWidth;
                }
                Queue();
            };
            Grid.SetColumn(_toggle, 2); grid.Children.Add(_toggle);
        }

        private void CollapseContext(bool collapse)
        {
            if (_side == null || _toggle == null || _collapsed == collapse) return;
            _changing = true;
            try
            {
                if (collapse)
                {
                    _sideVisibility = _side.Visibility;
                    _sideWidth = grid.ColumnDefinitions[2].Width; _gapWidth = grid.ColumnDefinitions[1].Width;
                }
                _side.Visibility = collapse ? Visibility.Collapsed : _sideVisibility;
                grid.ColumnDefinitions[2].Width = collapse ? new GridLength(0) : _sideWidth;
                grid.ColumnDefinitions[1].Width = collapse ? new GridLength(0) : _gapWidth;
                _collapsed = collapse;
                _toggle.IsChecked = collapse;
                _toggle.Content = collapse ? "▸" : "◂";
                Grid.SetColumn(_toggle, collapse ? 0 : 2);
                _toggle.SetBinding(AutomationProperties.NameProperty, collapse
                    ? Localization.TrExtension.CreateBinding("Layout.ShowContext")
                    : Localization.TrExtension.CreateBinding("Layout.HideContext"));
                _toggle.SetBinding(FrameworkElement.ToolTipProperty, collapse
                    ? Localization.TrExtension.CreateBinding("Layout.ShowContext")
                    : Localization.TrExtension.CreateBinding("Layout.HideContext"));
            }
            finally { _changing = false; }
        }
    }
}
