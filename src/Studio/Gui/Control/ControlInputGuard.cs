using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TiaOpenness.Gui.ControlChannel;

internal sealed class ControlInputGuard(Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private DateTimeOffset _lastHuman = DateTimeOffset.MinValue, _lastSwitch = DateTimeOffset.MinValue;
    internal bool HumanActive => _now() - _lastHuman < TimeSpan.FromMilliseconds(1500);
    internal bool ClickGuardActive => _now() - _lastSwitch < TimeSpan.FromMilliseconds(500);
    internal void HumanInput() => _lastHuman = _now();
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
}
