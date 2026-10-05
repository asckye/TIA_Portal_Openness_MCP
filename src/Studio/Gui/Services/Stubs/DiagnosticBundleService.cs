namespace TiaOpenness.Gui.Services.Stubs;

public interface IDiagnosticBundleService
{
    string Export();
}

// Stub until P6-44: the view reports the unavailable service without producing a bundle.
public sealed class DiagnosticBundleServiceStub : IDiagnosticBundleService
{
    public string Export() => "Shell.DiagnosticsNotConnected";
}
