using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.ViewModels;

public sealed class PortalProcessRow(PortalProcessInfo process) : ObservableObject
{
    public int ProcessId => process.ProcessId;
    public string ProjectPath => string.IsNullOrEmpty(process.ProjectPath) ? Loc.Current["ProcessPicker.NoProject"] : process.ProjectPath;
    public string Release => TiaMcp.Versioning.TiaVersionCatalog.FromApiVersion(process.Release).DisplayName;
    public string UiMode => process.UiMode switch
    {
        "WithUserInterface" => Loc.Current["ProcessPicker.WithUi"],
        "WithoutUserInterface" => Loc.Current["ProcessPicker.WithoutUi"],
        _ => Loc.Current["ProcessPicker.UnknownMode"],
    };
    public string AccessibleName => Loc.Current.T("ProcessPicker.Row", ProcessId, ProjectPath, Release, UiMode);
    internal void RefreshLanguage() { Raise(nameof(ProjectPath)); Raise(nameof(UiMode)); Raise(nameof(AccessibleName)); }
}

/// <summary>Only returns a human choice; opening, selecting and cancelling never connect.</summary>
public sealed class PortalProcessPickerViewModel : ObservableObject, IDisposable
{
    private PortalProcessRow? _selectedProcess;
    public IReadOnlyList<PortalProcessRow> Processes { get; }
    public PortalProcessRow? SelectedProcess
    {
        get => _selectedProcess;
        set { if (Set(ref _selectedProcess, value)) { Raise(nameof(CanConnect)); Connect.RaiseCanExecuteChanged(); } }
    }
    public bool CanConnect => SelectedProcess != null && Processes.Contains(SelectedProcess);
    public int? ResultProcessId { get; private set; }
    public AsyncCommand Connect { get; }
    public AsyncCommand Cancel { get; }
    public event EventHandler? CloseRequested;

    public PortalProcessPickerViewModel(IReadOnlyList<PortalProcessInfo> processes)
    {
        Processes = processes.Select(process => new PortalProcessRow(process)).ToArray();
        Connect =
            new AsyncCommand(() => { ResultProcessId = SelectedProcess!.ProcessId; CloseRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; },
                () => CanConnect);
        Cancel = new AsyncCommand(() => { ResultProcessId = null; CloseRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; });
        Loc.Current.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    { foreach (var process in Processes) process.RefreshLanguage(); }
    public void Dispose() => Loc.Current.LanguageChanged -= OnLanguageChanged;
}
