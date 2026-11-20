#if TIA_FOUNDATION_TEST_HOST
extern alias toolpipeline;
using UnsettledCalls = toolpipeline::TiaMcp.FoundationHost.UnsettledCalls;
using LateCalls = toolpipeline::TiaMcp.FoundationHost.LateCalls;
using EngineHostConfiguration = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration;
#endif
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

// One view of the worker's cached binding. Never query TIA or keep a second binding here.
internal sealed class SharedSessionState
{
    private static readonly ConditionalWeakTable<IFoundationWorker, SharedSessionState> States = new();
    private readonly IFoundationWorker worker;
    internal Func<string?> HostSessionKey = () => null;
    private JsonObject? runtimeState;
    private SharedSessionState(IFoundationWorker worker) => this.worker = worker;
    internal static SharedSessionState For(IFoundationWorker worker) => States.GetValue(worker, key => new(key));

    internal UnsettledCalls Unsettled => worker is IFoundationSessionWorker session ? session.Unsettled : UnsettledCalls.For(worker);
    internal LateCalls LateResults => worker is IFoundationSessionWorker session ? session.LateResults : LateCalls.For(worker);

    internal JsonObject? Identity
    {
        get
        {
            var binding = worker is WorkerClient legacy && (!legacy.SharedSession || legacy.SharedStatus == null) ? legacy.HostSnapshot()["binding"]
                : JsonNode.Parse((worker as IFoundationSessionWorker)?.ApprovalIdentity ?? "{}");
            var value = binding?["identity"] ?? binding?[nameof(Identity)] ?? binding;
            if (value is not JsonObject source || source.Count == 0) return null;
            JsonNode? Field(params string[] names) => names.Select(name => source[name]).FirstOrDefault(node => node != null)?.DeepClone();
            return new JsonObject {
                ["tiaMajorVersion"] = Field("tiaMajorVersion", "TiaMajorVersion"),
                ["processId"] = Field("processId", "ProcessId"),
                ["processStartUtc"] = Field("processStartUtc", "ProcessStartUtc"),
                ["projectPath"] = Field("projectPath", "ProjectPath", "projectFile", "ProjectFile"),
                ["projectName"] = Field("projectName", "ProjectName", "Project"),
                ["generation"] = Field("generation", "Generation", "BindingEpoch") };
        }
    }
    internal JsonObject Snapshot(string release)
        => Snapshot(release, (worker as IFoundationWorkerSupervisor)?.OperatorSnapshot() ?? new JsonObject());
    internal JsonObject RuntimeData(string release, JsonObject? observed = null)
    {
        if (observed != null) runtimeState = (JsonObject)observed.DeepClone();
        var cached = observed ?? (worker as WorkerClient)?.CachedRuntimeState ?? runtimeState;
        var transport = (worker as IFoundationWorkerSupervisor)?.OperatorSnapshot() ?? new JsonObject();
        var state = Snapshot(release, transport);
        var identity = state["binding"]?["identity"];
        var pid = observed != null ? observed["ProcessId"] : identity?["processId"] ?? transport["binding"]?["processId"];
        var file = observed != null ? observed["ProjectFile"] : identity?["projectPath"];
        if (observed == null && identity == null && worker is not WorkerClient)
        { pid = cached?["ProcessId"]; file = cached?["ProjectFile"]; }
        bool attached = (bool?)state["disconnected"] != true && pid != null;
        if (!attached) { pid = null; file = null; }
        bool sameProject = pid != null && file != null && JsonNode.DeepEquals(pid, cached?["ProcessId"])
            && JsonNode.DeepEquals(file, cached?["ProjectFile"]);
        string? identityStatus = JsonNode.DeepEquals(pid, cached?["ProcessId"]) ? (string?)cached?["IdentityStatus"] : null;
        return new JsonObject {
            ["releaseKey"] = release, ["isAttached"] = attached, ["processId"] = pid?.DeepClone(), ["projectFile"] = file?.DeepClone(),
            ["ownsProject"] = sameProject && (bool?)cached?["OwnsProject"] == true,
            ["isLocalSession"] = sameProject && (bool?)cached?["IsLocalSession"] == true,
            ["identityStatus"] = attached ? identityStatus ?? "cached" : "disconnected",
            ["runtimeConnectionStatus"] = "not-probed",
            ["canDetachOnUncertainShutdown"] = !attached || ((bool?)cached?["CanDetachOnUncertainShutdown"] ?? true),
            ["sessionState"] = state, ["outputRoot"] = DataLocations.Current.OutputRoot,
            ["presets"] = System.Text.Json.JsonSerializer.SerializeToNode(
                EngineHostConfiguration.CurrentOrNull?.Presets ?? TiaMcp.Logic.V4.ParameterPresets.Empty) };
    }
    private JsonObject Snapshot(string release, JsonObject transport)
    {
        var identity = Identity;
        Unsettled.ProjectChanged((string?)identity?["projectPath"]);
        if (identity != null)
        {
            identity["tiaMajorVersion"] ??= TiaMcp.Versioning.TiaVersionCatalog.Get(release).MajorVersion;
            if ((string?)identity["projectPath"] is string file) identity["projectName"] ??= Path.GetFileNameWithoutExtension(file);
        }
        bool ended = (bool?)transport["disconnected"] == true || worker is WorkerClient client && client.ExitedBinding() != null;
        bool connected = (bool?)transport["connected"] ?? (transport["binding"]?["processId"] != null
            || worker is WorkerClient { SharedSession: true } plc && (bool?)plc.SharedStatus?["session"]?["isConnected"] == true
            || worker is not IFoundationWorkerSupervisor && identity?["processId"] != null);
        var approval = new JsonObject { ["enabled"] = ApprovalSettings.Load(ApprovalSettings.SettingsPath).Enabled, ["readOnly"] = true };
        var result = new JsonObject { ["binding"] = ended || identity == null ? null : new JsonObject { ["identity"] = identity },
            ["connected"] = !ended && connected,
            ["workerGeneration"] = transport["generation"]?.DeepClone(),
            ["unsettled"] = Unsettled.ToJson(),
            ["settled"] = Unsettled.SettledJson(),
            ["running"] = transport["running"]?.DeepClone() ?? LateResults.Running,
            ["lateResults"] = LateResults.Snapshot(),
            ["workerFault"] = transport["workerFault"]?.DeepClone(),
            ["workerRecovery"] = transport["workerRecovery"]?.DeepClone(),
            ["disconnected"] = ended, ["switches"] = new JsonObject { ["approval"] = approval } };
        if (!ended && transport["secondaryProjectFile"] is { } secondary) result["secondaryProjectFile"] = secondary.DeepClone();
        return result;
    }
    internal JsonObject OperatorSnapshot(string release)
    {
        var transport = (worker as IFoundationWorkerSupervisor)?.OperatorSnapshot() ?? new JsonObject();
        var state = Snapshot(release, transport);
        var identity = state["binding"]?["identity"];
        return new JsonObject { ["pid"] = transport["pid"]?.DeepClone(), ["generation"] = state["workerGeneration"]?.DeepClone(),
            ["disconnected"] = state["disconnected"]?.DeepClone(), ["stopping"] = transport["stopping"]?.DeepClone(),
            ["binding"] = new JsonObject { ["processId"] = transport["binding"]?["processId"]?.DeepClone(),
                ["projectFile"] = identity?["projectPath"]?.DeepClone(), ["bindingEpoch"] = transport["binding"]?["bindingEpoch"]?.DeepClone() },
            ["sessionState"] = state };
    }
}
