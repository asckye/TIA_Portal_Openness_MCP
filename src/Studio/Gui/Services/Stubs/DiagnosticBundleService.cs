using System.ComponentModel;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Services.Stubs;

public interface IDiagnosticBundleService : INotifyPropertyChanged
{
    DiagnosticProgress Progress { get; }
    string Export();
    LocalizedText OpenFolder();
}

// Stub until P6-47: the view reports the unavailable service without producing a bundle.
public sealed class DiagnosticBundleServiceStub : ObservableObject, IDiagnosticBundleService
{
    public DiagnosticProgress Progress { get; } = new(DiagnosticState.Idle, LocalizedText.Empty);
    public string Export() => "Shell.DiagnosticsNotConnected";
    public LocalizedText OpenFolder() => LocalizedText.Key("Shell.DiagnosticsNotConnected");
}
