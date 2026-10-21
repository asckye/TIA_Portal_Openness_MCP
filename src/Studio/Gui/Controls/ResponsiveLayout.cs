using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;

namespace TiaOpenness.Gui.Controls;

/// <summary>Moves a page's context column below its content with scrolling at narrow widths.</summary>
public static class ResponsiveLayout
{
    /// <summary>
    /// Ant's xl breakpoint: a 1200 px window less the 2 px frame, the 216 px side menu and the 2 x 24 px content
    /// padding leaves 934 px for the page. Narrower pages stack the context column below the content, so the
    /// content column never gets less width than it has in the approved 1200 px window.
    /// </summary>
    public const double StackBelowWidth = 934;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ResponsiveLayout), new PropertyMetadata(false, Changed));
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    // Compact pages let their lists grow inside one page viewport, including the 1200 px boundary layout.
    // Spacious layouts retain finite list viewports and virtualization.
    public static readonly DependencyProperty ScrollWholePageProperty = DependencyProperty.RegisterAttached(
        "ScrollWholePage", typeof(bool), typeof(ResponsiveLayout),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetScrollWholePage(DependencyObject element) => (bool)element.GetValue(ScrollWholePageProperty);
    public static void SetScrollWholePage(DependencyObject element, bool value) => element.SetValue(ScrollWholePageProperty, value);
    private static readonly DependencyPropertyKey IsPageScrollingPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsPageScrolling", typeof(bool), typeof(ResponsiveLayout),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty IsPageScrollingProperty = IsPageScrollingPropertyKey.DependencyProperty;
    public static bool GetIsPageScrolling(DependencyObject element) => (bool)element.GetValue(IsPageScrollingProperty);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(ResponsiveLayout));

    /// <summary>True while the page shows its context column below the content.</summary>
    public static bool IsStacked(Grid grid) => grid.GetValue(StateProperty) is State { Stacked: true };

    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Grid grid || e.NewValue is not true) return;
        var state = new State(grid);
        grid.SetValue(StateProperty, state);
        // The layout pass that sizes the page also settles its arrangement, so the first frame (and a capture
        // taken right after UpdateLayout) already shows the stacked or the side-by-side page.
        grid.SizeChanged += (_, _) => state.Apply();
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
        private bool _stacked;
        private bool _pageScrolling;

        internal bool Stacked => _stacked;

        // Loaded and visibility changes arrive during tree walks, where the children cannot change yet.
        internal void Queue()
        {
            if (_queued) return;
            _queued = true;
            grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _queued = false;
                Apply();
            }));
        }

        internal void Apply()
        {
            // Half a pixel absorbs layout rounding, so a 1200 px window stays side by side at any DPI.
            bool narrow = grid.ActualWidth > 0 && grid.ActualWidth < StackBelowWidth - 0.5;
            EnsureContextToggle();
            if (narrow && _collapsed) CollapseContext(false);
            if (_toggle != null) _toggle.Visibility = !narrow && grid.ActualWidth < 1232
                && (_collapsed || _side?.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
            if (GetScrollWholePage(grid) && grid.ActualWidth <= StackBelowWidth + 0.5)
            {
                grid.SetValue(IsPageScrollingPropertyKey, true);
                ScrollPage(narrow);
                _pageScrolling = true;
                return;
            }
            if (_pageScrolling)
            {
                var content = (Panel)_scroll!.Content;
                foreach (var child in _children!) Move(child, content.Children, grid.Children);
                grid.Children.Remove(_scroll);
                _scroll = null;
                _stacked = _pageScrolling = false;
                grid.SetValue(IsPageScrollingPropertyKey, false);
            }
            if (narrow != (_scroll != null))
            {
                if (narrow) Stack();
                else Unstack();
            }
            // Stacked views keep one viewport of height, also when only the window height changes.
            if (narrow)
                foreach (var view in _heights.Keys)
                    view.SetCurrentValue(FrameworkElement.HeightProperty, Math.Max(view.MinHeight, grid.ActualHeight));
        }

        private void ScrollPage(bool narrow)
        {
            if (_scroll != null && narrow == _stacked) return;
            Panel content;
            if (narrow) content = new StackPanel();
            else
            {
                var columns = new Grid();
                foreach (var column in grid.ColumnDefinitions)
                {
                    var copy = new ColumnDefinition { MinWidth = column.MinWidth, MaxWidth = column.MaxWidth };
                    // The context fold still controls the outer grid's column widths.
                    BindingOperations.SetBinding(copy, ColumnDefinition.WidthProperty,
                        new Binding(nameof(ColumnDefinition.Width)) { Source = column });
                    columns.ColumnDefinitions.Add(copy);
                }
                foreach (var row in grid.RowDefinitions)
                    columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = row.MinHeight });
                content = columns;
            }

            if (_scroll == null)
            {
                _children = grid.Children.Cast<UIElement>().Where(child => child != _toggle).ToList();
                _scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                Grid.SetColumnSpan(_scroll, grid.ColumnDefinitions.Count);
                Grid.SetRowSpan(_scroll, Math.Max(1, grid.RowDefinitions.Count));
                grid.Children.Add(_scroll);
                MovePageChildren(grid, content);
            }
            else
            {
                var previous = (Panel)_scroll.Content;
                // Retain the inherited context while the previous panel leaves the connected tree.
                previous.DataContext = grid.DataContext;
                _scroll.Content = content;
                MovePageChildren(previous, content);
                previous.ClearValue(FrameworkElement.DataContextProperty);
            }
            _stacked = narrow;
        }

        private void MovePageChildren(Panel previous, Panel content)
        {
            foreach (var child in _children!.OrderBy(child => child is FrameworkElement head && IsPageHead(head) ? 0 : 1)
                .ThenBy(Grid.GetColumn).ThenBy(Grid.GetRow))
                Move(child, previous.Children, content.Children);
        }

        private void Stack()
        {
            _stacked = true;
            _children = grid.Children.Cast<UIElement>().Where(child => child != _toggle).ToList();
            var stack = new StackPanel();
            _scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetColumnSpan(_scroll, grid.ColumnDefinitions.Count);
            Grid.SetRowSpan(_scroll, Math.Max(1, grid.RowDefinitions.Count));
            // The scroller joins the page before the views move into it, so each view goes straight from one
            // connected parent to the next.
            grid.Children.Add(_scroll);
            if (_toggle != null) grid.Children.Remove(_toggle);
            foreach (var child in _children.OrderBy(child => child is FrameworkElement head && IsPageHead(head) ? 0 : 1)
                .ThenBy(Grid.GetColumn).ThenBy(Grid.GetRow))
            {
                // A page head in its own first row keeps its natural height above the stack.
                if (child is FrameworkElement view && !IsPageHead(view))
                {
                    _minimums[view] = view.MinHeight;
                    _heights[view] = view.Height;
                    view.SetCurrentValue(FrameworkElement.MinHeightProperty,
                        Math.Max(view.MinHeight, Grid.GetColumn(view) == 2 ? 320 : 520));
                    // Lists keep a finite viewport so reflow does not defeat virtualization.
                    view.SetCurrentValue(FrameworkElement.HeightProperty,
                        Math.Max(view.MinHeight, grid.ActualHeight));
                }
                Move(child, grid.Children, stack.Children);
            }
        }

        private void Unstack()
        {
            var stack = (StackPanel)_scroll!.Content;
            foreach (var child in _children!) Move(child, stack.Children, grid.Children);
            grid.Children.Remove(_scroll);
            if (_toggle != null) grid.Children.Add(_toggle);
            foreach (var (view, minimum) in _minimums) view.SetCurrentValue(FrameworkElement.MinHeightProperty, minimum);
            foreach (var (view, height) in _heights) view.SetCurrentValue(FrameworkElement.HeightProperty, height);
            _minimums.Clear();
            _heights.Clear();
            _scroll = null;
            _stacked = false;
        }

        // A view between two parents would lose its inherited data context for a moment: its lists would drop
        // and regenerate their rows, and the dropped rows report binding errors. Holding the context avoids that.
        private static void Move(UIElement child, UIElementCollection from, UIElementCollection to)
        {
            bool hold = child is FrameworkElement view && view.ReadLocalValue(FrameworkElement.DataContextProperty) == DependencyProperty.UnsetValue;
            if (hold) child.SetValue(FrameworkElement.DataContextProperty, ((FrameworkElement)child).DataContext);
            from.Remove(child);
            to.Add(child);
            if (hold) child.ClearValue(FrameworkElement.DataContextProperty);
        }

        private bool IsPageHead(FrameworkElement view)
            => grid.RowDefinitions.Count > 1 && Grid.GetRow(view) == 0 && Grid.GetRowSpan(view) == 1;

        private void EnsureContextToggle()
        {
            if (_toggle != null || grid.ColumnDefinitions.Count < 3) return;
            _side = grid.Children.OfType<FrameworkElement>().FirstOrDefault(child => Grid.GetColumn(child) == 2);
            if (_side == null) return;
            // The fold sits in the 24 px content gutter above the page, so it never covers a card header.
            _toggle = new ToggleButton { Content = Chevron("Icon.ChevronLeft"), Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -24, 0, 0) };
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
            Grid.SetColumn(_toggle, 2); Grid.SetRow(_toggle, 0); grid.Children.Add(_toggle);
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
                _toggle.Content = Chevron(collapse ? "Icon.ChevronRight" : "Icon.ChevronLeft");
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

        // The fold glyph is an Icons.xaml chevron drawn in the button's text colour.
        private static System.Windows.Shapes.Path Chevron(string icon)
        {
            var glyph = new System.Windows.Shapes.Path();
            glyph.SetResourceReference(FrameworkElement.StyleProperty, "Primer.ButtonIcon");
            glyph.SetResourceReference(System.Windows.Shapes.Path.DataProperty, icon);
            return glyph;
        }
    }
}
