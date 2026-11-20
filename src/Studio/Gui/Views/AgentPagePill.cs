using System.Windows.Controls;

namespace TiaOpenness.Gui.Views;

// A routed navigation request can be refused. Only the page the shell actually opened is checked.
public sealed class AgentPagePill : RadioButton
{
    protected override void OnToggle() { }
}
