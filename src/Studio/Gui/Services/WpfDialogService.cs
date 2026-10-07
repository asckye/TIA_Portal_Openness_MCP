using System.Windows;

namespace TiaOpenness.Gui.Services;

public sealed class WpfDialogService : IDialogService
{
    public DialogResult ShowMessage(string text, string caption, DialogButtons buttons, DialogIcon icon)
        => TiaOpenness.Gui.Controls.WorkbenchMessageBox.Show(text, caption,
            buttons == DialogButtons.OKCancel ? MessageBoxButton.OKCancel : MessageBoxButton.YesNoCancel,
            icon == DialogIcon.Warning ? MessageBoxImage.Warning : MessageBoxImage.Question) switch
        {
            MessageBoxResult.OK => DialogResult.OK,
            MessageBoxResult.Yes => DialogResult.Yes,
            MessageBoxResult.No => DialogResult.No,
            _ => DialogResult.Cancel,
        };

    public string[]? OpenFiles(string title, string filter, bool multiselect = false)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, Multiselect = multiselect };
        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }

    public string? OpenFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
