extern alias foundationhost;
extern alias enginehost;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Foundation = foundationhost::TiaMcp.FoundationHost;
using Engine = enginehost::TiaMcp.FoundationHost;

// The fixture uses the production PLC client and supervisor services. The adapter
// operation used by session fixtures is private to the synthetic worker executable.
internal sealed class WorkerBackendFixture : Foundation.IFoundationSessionWorker, Foundation.IFoundationWorkerResult,
    Engine.IEngineWorker
{
    private readonly Foundation.WorkerClient plc;
    private readonly Foundation.FoundationHostPorts.Services services;
    private readonly bool faultFixture;
    internal WorkerBackendFixture(Foundation.HostOptions options, string? spills = null)
    {
        plc = new Foundation.WorkerClient(options.ReleaseKey, options.WorkerExe,
            options.ApiDirectory, options.NativeEnabled, options.EngineTimeoutSeconds, options.WorkerTimeoutConfig)
            { Bundled = options.BundledWorker, SharedSession = true, SpillsDirectoryForTest = spills };
        faultFixture = Path.GetFileName(options.WorkerExe) == "TiaMcp.PlcWorker.FaultFixture.exe";
        services = Foundation.FoundationHostPorts.CreateServices(Inner, options.ReleaseKey,
            () => new JsonObject { ["ready"] = true }, options.ApiDirectory);
    }
    private Foundation.IFoundationSessionWorker Inner => plc;
    internal object Client => plc;
    internal static string Hash(string path) => TiaMcpServer.Siemens.ArgumentRules.HashFile(path, FileShare.Read);
    public bool Faulted => services.Faulted;
    public Engine.UnsettledCalls Unsettled => plc.Unsettled;
    public Engine.LateCalls LateResults => plc.LateResults;
    public JsonObject? Running => plc.Running;
    public JsonNode? Binding => services.Binding;
    public object SessionKey => services.SessionKey;
    public string ApprovalIdentity => Inner.ApprovalIdentity;
    public bool Bundled => Inner.Bundled;
    public bool SharedSession => Inner.SharedSession;
    public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? StagingOwner => Inner.StagingOwner;
    // The served host wires the PlcWorker's staging owner to its pipeline.
    internal Func<TiaMcp.Logic.ModelContextProtocol.ImportStagingSession>? StagingProvider
    {
        set { plc.StagingProvider = value; }
    }
    public IDisposable? EnterRequest(RequestContext<CallToolRequestParams> request) => Inner.EnterRequest(request);
    public Task<IDisposable?> AcquireLane(CancellationToken token) => Inner.AcquireLane(token);
    public void ActivateLane(IDisposable? lane) => Inner.ActivateLane(lane);
    public async Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
    {
        if (faultFixture && operation is "ReadBlocks" or "plc-paging.ReadBlockPage" or "optional-products.ExecuteOptionalProduct" or "Attach")
        {
            string tool = global::TiaMcp.FoundationHost.FoundationWorkerRequest.ToolName
                ?? Foundation.FoundationWorkerRequest.ToolName
                ?? (operation is "ReadBlocks" or "plc-paging.ReadBlockPage" ? "ListPlcBlocks"
                    : operation == "Attach" ? "AttachOpenProject" : "ListTestSuiteCases");
            using var request = Foundation.FoundationWorkerRequest.Use(Foundation.FoundationWorkerRequest.CorrelationId, tool);
            await plc.Call("FaultFixtureArm", new() { ["method"] = "adapter." + operation }, token);
            return await Inner.Call(operation, args, token);
        }
        return await Inner.Call(operation, args, token);
    }
    public Task<JsonNode?> Observe(string operation, JsonObject args, CancellationToken token) => plc.Observe(operation, args, token);
    public JsonObject Snapshot() => services.Snapshot();
    public Task<JsonObject> Status(CancellationToken token) => services.StartForStatus(token);
    public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => services.Restart(confirmed, token);
    // Production budgets until disposed; see ProductionWorkerBudgets.
    internal IDisposable ProductionBudgets() => new ProductionWorkerBudgets(Client);
    public Task<IDisposable> Acquire(CancellationToken token) => services.Acquire(token);
    public Task<Engine.EngineReply> Invoke(string id, string name, JsonObject args, bool preview, CancellationToken token)
        => services.Invoke(id, name, args, preview, token);
    public CallToolResult ParkResult(CallToolResult result, string name) => ((Foundation.IFoundationWorkerResult)Inner).ParkResult(result, name);
    public void Dispose() => Inner.Dispose();
}
