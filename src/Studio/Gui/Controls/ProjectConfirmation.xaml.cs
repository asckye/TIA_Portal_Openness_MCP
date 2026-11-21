using System.IO;
using System.Linq;
using System.Windows.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Controls;

internal partial class ProjectConfirmation : UserControl
{
    internal ProjectConfirmation() => InitializeComponent();

    internal void ShowDetails(ConfirmationAction action, string caption, string message, MainViewModel? model)
    {
        var session = model?.Session;
        if (action == ConfirmationAction.SaveProject)
        {
            string? file = session?.OpenProjectFile;
            Heading.Text = string.IsNullOrWhiteSpace(file) ? caption : Loc.Current.T("Overlay.SaveTitle", Path.GetFileName(file));
            Body.Text = Loc.Current["Overlay.SaveText"];
            FirstLabel.Text = Loc.Current["Overlay.Project"];
            FirstValue.Text = string.IsNullOrWhiteSpace(file) ? "—" : file;
            SecondLabel.Text = Loc.Current["Overlay.Changes"];
            // The VCI comparison is not a list of unsaved TIA project edits.
            SecondValue.Text = "—";
        }
        else
        {
            Heading.Text = Loc.Current["Overlay.EndTitle"];
            Body.Text = message;
            FirstLabel.Text = Loc.Current["Overlay.AffectedClient"];
            var panel = model?.Panel;
            FirstValue.Text = panel is { Attached: true } ? string.Join(" · ", new[] { panel.ClientStatus, panel.SessionKey }
                .Where(s => !string.IsNullOrWhiteSpace(s))) : "—";
            SecondLabel.Text = Loc.Current["Overlay.Process"];
            SecondValue.Text = session is { ProcessId: > 0 } ? Loc.Current.T("Overlay.ProcessKept", session.ProcessId) : "—";
        }
    }
}
