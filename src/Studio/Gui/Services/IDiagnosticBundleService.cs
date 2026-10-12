using System.ComponentModel;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Services.Stubs;

public interface IDiagnosticBundleService : INotifyPropertyChanged
{
    public DiagnosticProgress Progress { get; }
    public string Export();
    public LocalizedText OpenFolder();
}
