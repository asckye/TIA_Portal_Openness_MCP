using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.ControlChannel;

internal interface IWorkbenchControlSurface
{
    WorkbenchSnapshot Snapshot { get; }
    WorkbenchControlResponse Apply(WorkbenchControlRequest request);
}

// Frozen JSON owns its data. Readers receive private DTOs and never touch WPF or native objects.
internal sealed class WorkbenchSnapshot
{
    private readonly byte[] _state, _selection;
    internal WorkbenchSnapshot(WorkbenchControlData state, WorkbenchControlData selection)
    {
        _state = JsonSerializer.SerializeToUtf8Bytes(state, WorkbenchControlProtocol.Json);
        _selection = JsonSerializer.SerializeToUtf8Bytes(selection, WorkbenchControlProtocol.Json);
    }
    internal WorkbenchControlData Read(WorkbenchControlArguments arguments)
    {
        var data = JsonSerializer.Deserialize<WorkbenchControlData>(
            arguments is WorkbenchReadSelectionArguments ? _selection : _state, WorkbenchControlProtocol.Json)!;
        if (arguments is WorkbenchReadSelectionArguments paging)
        {
            var blocks = data.CheckedBlocks ?? [];
            data.CheckedBlocks = blocks.Skip(paging.Offset).Take(paging.Limit).ToArray();
            data.Paging = new WorkbenchPaging { Offset = paging.Offset, Limit = paging.Limit, Total = blocks.Length };
        }
        return data;
    }
}

internal sealed class WorkbenchControlSurface(Func<WorkbenchControlRequest, WorkbenchControlResponse> apply,
    WorkbenchSnapshot initial) : IWorkbenchControlSurface
{
    private WorkbenchSnapshot _snapshot = initial;
    public WorkbenchSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal void Publish(WorkbenchSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
    public WorkbenchControlResponse Apply(WorkbenchControlRequest request) => apply(request);
}

internal static class ControlResponse
{
    internal static WorkbenchControlInfo Info => new() { Version = ViewModels.MainViewModel.AppVersion.TrimStart('v'), Contracts = [1] };
    internal static WorkbenchControlResponse Done(WorkbenchControlRequest request, WorkbenchControlData data)
        => new() { RequestId = request.RequestId, Status = WorkbenchControlStatus.Done, Data = data, Workbench = Info };
    internal static WorkbenchControlResponse Refuse(WorkbenchControlRequest request, string condition,
        WorkbenchControlError code = WorkbenchControlError.PreconditionFailed, string target = "", string[]? candidates = null)
        => new() { RequestId = request.RequestId, Status = WorkbenchControlStatus.Refused, Workbench = Info,
            Refusal = new() { Code = code, Condition = condition, Target = target, Candidates = candidates ?? [],
                Stage = code == WorkbenchControlError.Timeout ? "workbench-ui" : null,
                Capability = code == WorkbenchControlError.UnsupportedCapability ? "workbench-control.v1" : null } };
    internal static WorkbenchControlResponse Failed(WorkbenchControlRequest request, string diagnosticId)
    {
        var response = Refuse(request, "workbench-control-failed", WorkbenchControlError.InternalError);
        response.Status = WorkbenchControlStatus.Failed;
        response.Refusal!.DiagnosticId = diagnosticId;
        return response;
    }
    internal static string OriginKey(WorkbenchControlOrigin origin)
        => origin.HostProcessId + ":" + origin.ReleaseKey + ":" + origin.McpSession;
}
