using System;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class LogView : UserControl
{
    public LogView() => InitializeComponent();
    private MainViewModel _model => (MainViewModel)DataContext;
    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange > 0) ((ScrollViewer)sender).ScrollToEnd();
    }
    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_model.Activity.Log);
        }
        catch (System.Runtime.InteropServices.COMException) /* swallow(ui): another process can hold the clipboard; an unsuccessful log copy does not interrupt the workbench */
        {
            // The clipboard is a shared OS resource and another process can hold it open.
            // Failing to copy a log is not worth an error dialog.
        }
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => _model.Activity.ClearLog();

}
