using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace TiaOpenness.Gui.Controls;

public sealed class DecorativeImage : Image
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
