using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TiaOpenness.Gui.Common;

/// <summary>Property-change plumbing shared by the view models.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>
/// An async command that disables itself while running. Every operation here talks to TIA
/// Portal, which is single-threaded and slow, so re-entrancy is a correctness issue rather
/// than a nicety.
/// </summary>
public class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null,
    Action<Exception>? reportError = null) : ICommand
{
    private bool _running;
    public bool IsRunning => _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        catch (Exception error)
        {
            (reportError ?? App.ReportUiException)(error);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Human execution of a form through the shared MCP panel.</summary>
public sealed class McpPanelCommand(Func<Task> execute, Func<bool>? canExecute = null) : AsyncCommand(execute, canExecute);

[AttributeUsage(AttributeTargets.Property)]
public sealed class PrefillTargetAttribute : Attribute
{
    public string Executes { get; set; } = "none";
}
