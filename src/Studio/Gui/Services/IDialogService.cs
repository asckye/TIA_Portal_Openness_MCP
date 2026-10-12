namespace TiaOpenness.Gui.Services;

public enum DialogButtons { OKCancel, YesNoCancel }
public enum DialogIcon { Question, Warning }
public enum DialogResult { OK, Yes, No, Cancel }

/// <summary>The workbench's confirmation and file/folder choices, independent of WPF dialogs.</summary>
public interface IDialogService
{
    public DialogResult ShowMessage(string text, string caption, DialogButtons buttons, DialogIcon icon);
    public string[]? OpenFiles(string title, string filter, bool multiselect = false);
    public string? OpenFolder(string title);
}
