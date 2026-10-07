using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TiaOpenness.Gui.Controls;

public sealed class LogTailView : ListBox
{
    internal const double RowHeight = 24;
    private bool _tailQueued;
    private ScrollViewer? _scroll;

    public static readonly DependencyProperty LogTextProperty = DependencyProperty.Register(
        nameof(LogText), typeof(string), typeof(LogTailView), new PropertyMetadata("", OnLogTextChanged));
    public string LogText { get => (string)GetValue(LogTextProperty); set => SetValue(LogTextProperty, value); }

    public LogTailView()
    {
        Loaded += (_, _) => FollowTail();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _scroll = GetTemplateChild("PART_ScrollViewer") as ScrollViewer;
        FollowTail();
    }

    private static void OnLogTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (LogTailView)sender;
        view.ItemsSource = ((string?)e.NewValue ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(12).Select(line =>
            {
                line = line.TrimEnd('\r');
                bool timed = line.Length >= 9 && line[2] == ':' && line[5] == ':';
                return new TailRow(timed ? line[..8] : "", timed ? line[8..].TrimStart() : line);
            }).ToArray();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        // Keep the viewport on row boundaries, including short result-card tails.
        if (!double.IsInfinity(constraint.Height))
            constraint.Height = Math.Floor(constraint.Height / RowHeight) * RowHeight;
        return base.MeasureOverride(constraint);
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        FollowTail();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        FollowTail();
    }

    private void FollowTail()
    {
        if (_tailQueued) return;
        _tailQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _tailQueued = false;
            if (Items.Count > 0) _scroll?.ScrollToEnd();
        }));
    }

    private sealed record TailRow(string Time, string Message);
}
