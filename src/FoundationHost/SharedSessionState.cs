using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

// One view of the worker's cached binding. Never query TIA or keep a second binding here.
internal sealed class SharedSessionState
{
    private static readonly ConditionalWeakTable<IFoundationWorker, SharedSessionState> States = new();
    private readonly IFoundationWorker worker;
    private readonly bool environmentCrossReferences = Environment.GetEnvironmentVariable("TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES") == "1";
    internal Func<bool> HumanCrossReferences = () => false;
    internal Func<string?> HostSessionKey = () => null;
    private SharedSessionState(IFoundationWorker worker) => this.worker = worker;
    internal static SharedSessionState For(IFoundationWorker worker) => States.GetValue(worker, key => new(key));

    internal bool RequiresReset(JsonNode? reply = null) => (worker as IFoundationSessionWorker)?.Poisoned == true
        || (bool?)reply?["requiresSessionReset"] == true || (bool?)reply?["RequiresSessionReset"] == true
        || (bool?)reply?["meta"]?["requiresSessionReset"] == true
        || (bool?)(worker as IFoundationWorkerSupervisor)?.OperatorSnapshot()["sessionLocked"] == true;

    internal JsonObject? Identity
    {
        get
        {
            var binding = worker is WorkerClient { SharedSession: false } legacy ? legacy.HostSnapshot()["binding"]
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
    internal JsonObject Switches(string release)
    {
        bool human = HumanCrossReferences();
        bool allowed = TiaMcp.Adapters.Contracts.NativeCrossReferenceConsent.DefaultAllowed(release) || environmentCrossReferences || human;
        return new JsonObject {
            ["nativeCrossReferences"] = new JsonObject { ["allowed"] = allowed, ["humanSessionAllowed"] = human,
                ["environmentAllowed"] = environmentCrossReferences, ["defaultAllowed"] = TiaMcp.Adapters.Contracts.NativeCrossReferenceConsent.DefaultAllowed(release) },
            ["approval"] = new JsonObject { ["enabled"] = ApprovalSettings.Load(ApprovalSettings.SettingsPath).Enabled, ["readOnly"] = true } };
    }
    internal JsonObject Snapshot(string release)
        => Snapshot(release, (worker as IFoundationWorkerSupervisor)?.OperatorSnapshot() ?? new JsonObject());
    private JsonObject Snapshot(string release, JsonObject transport)
    {
        var identity = Identity;
        if (identity != null)
        {
            identity["tiaMajorVersion"] ??= TiaMcp.Versioning.TiaVersionCatalog.Get(release).MajorVersion;
            if ((string?)identity["projectPath"] is string file) identity["projectName"] ??= Path.GetFileNameWithoutExtension(file);
        }
        bool ended = (bool?)transport["disconnected"] == true;
        bool locked = (bool?)transport["sessionLocked"] == true || (worker as IFoundationSessionWorker)?.Poisoned == true;
        bool connected = (bool?)transport["connected"] ?? (transport["binding"]?["processId"] != null
            || worker is WorkerClient { SharedSession: true } plc && (bool?)plc.SharedStatus?["session"]?["isConnected"] == true
            || worker is not IFoundationWorkerSupervisor && identity?["processId"] != null);
        var switches = Switches(release);
        if (ended) switches["nativeCrossReferences"]!["allowed"] = false;
        return new JsonObject { ["binding"] = identity == null ? null : new JsonObject { ["identity"] = identity },
            ["connected"] = !ended && connected,
            ["workerGeneration"] = transport["generation"]?.DeepClone(),
            ["disconnected"] = ended, ["requiresSessionReset"] = locked, ["switches"] = switches };
    }
    internal JsonObject OperatorSnapshot(string release)
    {
        var transport = (worker as IFoundationWorkerSupervisor)?.OperatorSnapshot() ?? new JsonObject();
        var state = Snapshot(release, transport);
        var identity = state["binding"]?["identity"];
        return new JsonObject { ["pid"] = transport["pid"]?.DeepClone(), ["generation"] = state["workerGeneration"]?.DeepClone(),
            ["disconnected"] = state["disconnected"]?.DeepClone(), ["stopping"] = transport["stopping"]?.DeepClone(),
            ["sessionLocked"] = state["requiresSessionReset"]?.DeepClone(),
            ["binding"] = new JsonObject { ["processId"] = transport["binding"]?["processId"]?.DeepClone(),
                ["projectFile"] = identity?["projectPath"]?.DeepClone(), ["bindingEpoch"] = transport["binding"]?["bindingEpoch"]?.DeepClone() },
            ["sessionState"] = state };
    }
}
