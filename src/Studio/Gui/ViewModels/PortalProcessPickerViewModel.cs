using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.ViewModels;

public sealed class PortalProcessRow(PortalProcessInfo process) : ObservableObject
{
    public int ProcessId => process.ProcessId;
    public string ProjectPath => string.IsNullOrEmpty(process.ProjectPath) ? Loc.Current["ProcessPicker.NoProject"] : process.ProjectPath;
    public string Thread => TiaMcp.Versioning.TiaVersionCatalog.FromApiVersion(process.Release).OwnerApartment.ToString();
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
    private readonly CancellationTokenSource _lifetime = new();
    private PortalProcessRow? _selectedProcess;
    private IReadOnlyList<PortalProcessRow> _processes = [];
    public IReadOnlyList<PortalProcessRow> Processes
    {
        get => _processes;
        private set
        {
            if (!Set(ref _processes, value)) return;
            Raise(nameof(CountText));
            Raise(nameof(ChoiceHint));
        }
    }
    public string ChoiceHint => Processes.Count > 1 ? Loc.Current["ProcessPicker.Hint"] : CountText;
    public string CountText => Loc.Current.T("Overlay.ProcessCount", Processes.Count);
    public string AttachText => SelectedProcess == null ? Loc.Current["ProcessPicker.Connect"] : Loc.Current.T("Overlay.AttachPid", SelectedProcess.ProcessId);
    public string? ScanError { get; private set; }
    public AsyncCommand Rescan { get; }
    public PortalProcessRow? SelectedProcess
    {
        get => _selectedProcess;
        set { if (Set(ref _selectedProcess, value)) { Raise(nameof(CanConnect)); Raise(nameof(AttachText)); Connect.RaiseCanExecuteChanged(); } }
    }
    public bool CanConnect => SelectedProcess != null && Processes.Contains(SelectedProcess);
    public int? ResultProcessId { get; private set; }
    public AsyncCommand Connect { get; }
    public AsyncCommand Cancel { get; }
    public event EventHandler? CloseRequested;

    public PortalProcessPickerViewModel(IReadOnlyList<PortalProcessInfo> processes,
        Func<CancellationToken, Task<IReadOnlyList<PortalProcessInfo>>>? rescan = null)
    {
        Processes = processes.Select(process => new PortalProcessRow(process)).ToArray();
        Rescan = new AsyncCommand(async () =>
        {
            SelectedProcess = null;
            Processes = [];
            ScanError = null;
            Raise(nameof(ScanError));
            var token = _lifetime.Token;
            try
            {
                var snapshot = await rescan!(token);
                if (!token.IsCancellationRequested) Processes = snapshot.Select(process => new PortalProcessRow(process)).ToArray();
            }
            catch (Exception error)
            {
                if (token.IsCancellationRequested) return;
                ScanError = error.Message;
                Raise(nameof(ScanError));
            }
        }, () => rescan != null);
        Connect =
            new AsyncCommand(() => { ResultProcessId = SelectedProcess!.ProcessId; CloseRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; },
                () => CanConnect);
        Cancel = new AsyncCommand(() => { ResultProcessId = null; CloseRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; });
        Loc.Current.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        foreach (var process in Processes) process.RefreshLanguage();
        Raise(nameof(AttachText));
        Raise(nameof(CountText));
        Raise(nameof(ChoiceHint));
    }
    public void Dispose()
    {
        if (_lifetime.IsCancellationRequested) return;
        Loc.Current.LanguageChanged -= OnLanguageChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
