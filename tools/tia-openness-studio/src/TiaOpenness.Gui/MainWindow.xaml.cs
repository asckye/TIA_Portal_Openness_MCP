using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _model;
        Closed += (_, _) => _model.Dispose();
        Loaded += async (_, _) => await _model.ApplyStartupAsync(System.Environment.GetCommandLineArgs());
    }

    // ---- browse buttons ----------------------------------------------------

    /// <summary>The tree carries folders as well as devices; only a device changes the selection.</summary>
    private void OnDeviceSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ViewModels.DeviceNode { Device: not null } node) _model.SelectedDevice = node.Device;
    }

    private void OnBrowseProject(object sender, RoutedEventArgs e) => _model.BrowseProject();

    private void OnBrowseOutput(object sender, RoutedEventArgs e) => _model.BrowseOutput();

    private void OnBrowseWorkspace(object sender, RoutedEventArgs e) => _model.BrowseWorkspaceFolder();

    private void OnSelectAll(object sender, RoutedEventArgs e) => _model.SelectAll(true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => _model.SelectAll(false);

    // ---- log ---------------------------------------------------------------

    /// <summary>Keeps the log pinned to the newest line as it grows.</summary>
    private void OnLogChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box) box.ScrollToEnd();
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_model.Log);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is a shared OS resource and another process can hold it open.
            // Failing to copy a log is not worth an error dialog.
        }
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => _model.ClearLog();

}
