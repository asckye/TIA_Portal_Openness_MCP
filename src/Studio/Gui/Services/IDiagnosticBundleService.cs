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
