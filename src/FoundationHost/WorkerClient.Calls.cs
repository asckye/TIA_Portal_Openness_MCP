#if TIA_FOUNDATION_TEST_HOST
extern alias enginehost;
using McpServer = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using ImportStagingTools = enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingTools;
using WorkerSupervisorReplies = enginehost::TiaMcp.FoundationHost.WorkerSupervisorReplies;
using WorkerSupervisorState = enginehost::TiaMcp.FoundationHost.WorkerSupervisorState;
using ReleaseCatalogData = enginehost::TiaMcp.FoundationHost.ReleaseCatalogData;
using UnsettledCalls = enginehost::TiaMcp.FoundationHost.UnsettledCalls;
using LateCalls = enginehost::TiaMcp.FoundationHost.LateCalls;
using UnknownSettlement = enginehost::TiaMcp.FoundationHost.UnknownSettlement;
using ResponseGuardTool = enginehost::TiaMcpServer.ModelContextProtocol.ResponseGuardTool;
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
