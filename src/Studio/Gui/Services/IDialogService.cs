namespace TiaOpenness.Gui.Services;

public enum DialogButtons { OKCancel, YesNoCancel }
public enum DialogIcon { Question, Warning }
public enum DialogResult { OK, Yes, No, Cancel, Detach }
public enum ConfirmationAction { SaveProject, EndSharedSession, OverwriteProject, ImportBlocks }
public sealed record ConfirmationLabels(ConfirmationAction Action)
{
    public bool Detach => Action == ConfirmationAction.EndSharedSession;
}

/// <summary>The workbench's confirmation and file/folder choices, independent of WPF dialogs.</summary>
public interface IDialogService
{
    public DialogResult ShowMessage(string text, string caption, DialogButtons buttons, DialogIcon icon);
    public DialogResult ShowMessage(string text, string caption, DialogButtons buttons, DialogIcon icon, ConfirmationLabels labels)
        => ShowMessage(text, caption, buttons, icon);
    public string[]? OpenFiles(string title, string filter, bool multiselect = false);
    public string? OpenFolder(string title);
}
