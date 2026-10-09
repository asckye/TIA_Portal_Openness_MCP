using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TiaOpenness.Gui.ControlChannel;

internal sealed class ControlInputGuard(Func<DateTimeOffset>? now = null, Func<bool>? humanActive = null,
    Func<Point?>? mousePosition = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<Point?> _mousePosition = mousePosition ?? ReadMousePosition;
    private Point? _lastMousePosition;
    private DateTimeOffset _lastHuman = DateTimeOffset.MinValue, _lastSwitch = DateTimeOffset.MinValue;
    internal bool HumanActive => humanActive?.Invoke() ?? _now() - _lastHuman < TimeSpan.FromMilliseconds(1500);
    internal bool ClickGuardActive => _now() - _lastSwitch < TimeSpan.FromMilliseconds(500);
    internal void HumanInput() => _lastHuman = _now();
    internal void MouseMove()
    {
        if (_mousePosition() is not { } position) return;
        // The first sample is a baseline. Layout/page changes under a stationary cursor are not human input.
        if (_lastMousePosition is { } previous && previous != position) HumanInput();
        _lastMousePosition = position;
    }
    internal void UiChanged() => _lastSwitch = _now();
    internal bool ShouldBlock(DependencyObject? source)
    {
        if (!ClickGuardActive) return false;
        // Protect every action button. Navigation and editable fields remain available.
        while (source != null)
        {
            if (source is ButtonBase and not ToggleButton) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
    private static Point? ReadMousePosition() => GetCursorPos(out var position) ? new Point(position.X, position.Y) : null;
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { internal int X, Y; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out CursorPoint point);
}
