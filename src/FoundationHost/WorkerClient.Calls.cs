#if TIA_FOUNDATION_TEST_HOST
extern alias toolpipeline;
using McpServer = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;
using ImportStagingTools = toolpipeline::TiaMcpServer.ModelContextProtocol.ImportStagingTools;
using WorkerSupervisorReplies = toolpipeline::TiaMcp.FoundationHost.WorkerSupervisorReplies;
using WorkerSupervisorState = toolpipeline::TiaMcp.FoundationHost.WorkerSupervisorState;
using ReleaseCatalogData = toolpipeline::TiaMcp.FoundationHost.ReleaseCatalogData;
using UnsettledCalls = toolpipeline::TiaMcp.FoundationHost.UnsettledCalls;
using LateCalls = toolpipeline::TiaMcp.FoundationHost.LateCalls;
using UnknownSettlement = toolpipeline::TiaMcp.FoundationHost.UnknownSettlement;
using ResponseGuardTool = toolpipeline::TiaMcpServer.ModelContextProtocol.ResponseGuardTool;
#else
using McpServer = TiaMcpServer.ModelContextProtocol.McpServer;
using ImportStagingTools = TiaMcpServer.ModelContextProtocol.ImportStagingTools;
using ResponseGuardTool = TiaMcpServer.ModelContextProtocol.ResponseGuardTool;
#endif
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.WorkerChannel;
using TiaMcp.PlcWorker;
using TiaMcpServer.Siemens;

namespace TiaMcp.FoundationHost;
internal sealed partial class WorkerClient
{
    private string? secondaryProjectFile;
    private int? secondaryProcessId;
    private string? secondaryProcessStartUtc;
    internal Func<int, (bool Gone, string? StartUtc)> SecondaryProcessObservation { get; set; } = ReadSecondaryProcess;
    private static (bool Gone, string? StartUtc) ReadSecondaryProcess(int pid)
    {
        try
        {
            using var holder = Process.GetProcessById(pid);
            return holder.HasExited ? (true, null) : (false, holder.StartTime.ToUniversalTime()
                .ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (ArgumentException) { /* swallow(env-probe): no process exists with the recorded PID. */ return (true, null); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { /* swallow(env-probe): access failure cannot establish that the holding process is gone. */ return (false, null); }
    }
    private void ClearSecondaryRecord()
    {
        secondaryProjectFile = null; secondaryProcessId = null; secondaryProcessStartUtc = null;
    }
    private bool ForgetSecondaryIfHolderGone()
    {
        lock (stateSync)
        {
            if (secondaryProjectFile == null || secondaryProcessId is not int pid) return false;
            var observed = SecondaryProcessObservation(pid);
            if (!observed.Gone && (observed.StartUtc == null || secondaryProcessStartUtc == null
                || observed.StartUtc == secondaryProcessStartUtc)) return false;
            ClearSecondaryRecord();
            return true;
        }
    }
    private void RequireSecondaryHolder()
    {
        var observed = secondaryProcessId is int pid ? SecondaryProcessObservation(pid) : default;
        if (attachedProcessId != secondaryProcessId || observed.StartUtc == null || observed.StartUtc != secondaryProcessStartUtc
            || hostProcessStartUtc != secondaryProcessStartUtc)
            throw SecondaryHolderRefusal("ConnectPortal to that holding process before CloseProject secondaryProjectFile.");
    }
    private TiaMcp.Adapters.Contracts.AdapterPreconditionException SecondaryHolderRefusal(string next)
        => new("The secondary project is held by TIA PID " + secondaryProcessId + " (started " + secondaryProcessStartUtc + "). " + next,
            "secondaryProjectFile", false);
    internal static bool IsSecondaryProjectOperation(string operation, JsonObject arguments)
        => operation == "OpenProject" && (string?)arguments["openMode"] == "secondary"
            || operation == "CloseProject" && (string?)arguments["secondaryProjectFile"] is { Length: > 0 };

    internal Task<JsonNode?> Observe(string operation, JsonObject arguments, CancellationToken token)
        => CallCore("HostObserve", new JsonObject { ["operation"] = operation, ["arguments"] = arguments.DeepClone() }, true, false, token);
    public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        => CallCore(operation, arguments, false, false, token);
    private string[] UnsettledRequestIds()
    {
        var calls = Unsettled.Snapshot();
        if (calls.Count > ChannelLimits.UnsettledRequestCount && Interlocked.Exchange(ref unsettledLimitReported, 1) == 0)
        {
            Console.Error.WriteLine("Worker transport limited unsettled request IDs to " + ChannelLimits.UnsettledRequestCount
                + ". The session retains all request evidence.");
        }
        return calls.Take(ChannelLimits.UnsettledRequestCount).Select(call => call.RequestId).ToArray();
    }
    internal async Task<JsonNode?> ReadForSettlement(string operation, JsonObject arguments, CancellationToken token)
    {
        using var scope = FoundationWorkerRequest.Use(Guid.NewGuid().ToString("N"), operation);
        return await CallCore(operation, arguments, false, true, token).ConfigureAwait(false);
    }
    private void SettlementRunning(JsonObject running)
        => throw new TimeoutException("The optional settlement read exceeded its wait budget.");
    private void RetainSettlementDrain(Task<string> reply)
    {
        lock (stateSync) settlementDrain = reply;
        RetainRecoveryDrain(reply);
    }

    internal static BindingChange BindingChangeFor(string operation, JsonObject arguments)
    {
        if (IsSecondaryProjectOperation(operation, arguments)) return BindingChange.None;
        var dryRun = arguments["dryRun"]?.GetValue<bool>() ?? true;
        var request = arguments["request"] as JsonObject;
        bool rebinds = operation switch
        {
            "project-lifecycle.ArchiveSaveProjectCopy" => !dryRun,
            "project-lifecycle.ConnectIsolatedPortal" => true,
            "project-lifecycle.RetrieveProjectArchive" => (bool?)request?["DryRun"] == false,
            "project-lifecycle.ExecuteMultiuserSession" => (string?)request?["Action"] == "commit"
                && (bool?)request?["DryRun"] == false,
            "project-lifecycle.ExecuteProjectScaffoldPrimitive" => (string?)request?["Kind"] is "connect" or "createProject",
            _ => false
        };
        if (rebinds) return BindingChange.MayAdvance;
        return operation is "Attach" or "BindProject" || (!dryRun && operation is "OpenProject" or "CreateProject" or "CloseProject")
            ? BindingChange.Advance : operation == "Disconnect" ? BindingChange.MayAdvance : BindingChange.None;
    }
}
