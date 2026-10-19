extern alias foundationhost;
extern alias enginehost;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Foundation = foundationhost::TiaMcp.FoundationHost;
using Engine = enginehost::TiaMcp.FoundationHost;

// Both backends use their production clients and supervisor services. The adapter
// operation used by session fixtures is private to the synthetic worker executable.
internal sealed class WorkerBackendFixture : Foundation.IFoundationSessionWorker, Foundation.IFoundationWorkerResult,
    Engine.IEngineWorker, Engine.IEngineWorkerProgress
{
    private readonly Foundation.EngineWorkerClient? engine;
    private readonly Foundation.WorkerClient? plc;
    private readonly Foundation.FoundationHostPorts.Services services;
    private readonly bool faultFixture;
    internal WorkerBackendFixture(bool plcWorker, Foundation.HostOptions options, string hash, string? spills = null, TimeSpan? statusTimeout = null)
    {
        if (plcWorker)
            plc = new Foundation.WorkerClient(options.ReleaseKey, options.WorkerExe == "unused" ? options.EngineWorkerExe! : options.WorkerExe,
                options.ApiDirectory, options.NativeEnabled, options.EngineTimeoutSeconds, options.WorkerTimeoutConfig)
                { Bundled = options.BundledWorker, SharedSession = true };
        else engine = new Foundation.EngineWorkerClient(options, hash, spills) { StatusTimeoutForTest = statusTimeout };
        faultFixture = plc != null && Path.GetFileName(options.WorkerExe) == "TiaMcp.PlcWorker.FaultFixture.exe";
        services = Foundation.FoundationHostPorts.CreateServices(Inner, options.ReleaseKey,
            () => new JsonObject { ["ready"] = true }, options.ApiDirectory);
    }
    private Foundation.IFoundationSessionWorker Inner => (Foundation.IFoundationSessionWorker?)engine ?? plc!;
    internal object Client => (object?)engine ?? plc!;
    internal static string Hash(string path) => Foundation.EngineWorkerClient.Hash(path);
    public bool Faulted => services.Faulted;
    public bool Poisoned => Inner.Poisoned;
    public JsonNode? Binding => services.Binding;
    public object SessionKey => services.SessionKey;
    public string ApprovalIdentity => Inner.ApprovalIdentity;
    public bool Bundled => Inner.Bundled;
    public bool SharedSession => Inner.SharedSession;
    public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? StagingOwner => Inner.StagingOwner;
    internal Func<bool>? SessionLocked
    {
        set { if (engine != null) engine.SessionLocked = value; else plc!.SessionLocked = value; }
    }
    // The served host wires the PlcWorker's staging owner to its pipeline; the engine
    // client reads the ambient pipeline session instead.
    internal Func<TiaMcp.Logic.ModelContextProtocol.ImportStagingSession>? StagingProvider
    {
        set { if (plc != null) plc.StagingProvider = value; }
    }
    internal TimeSpan? StatusTimeoutForTest => engine?.StatusTimeoutForTest;
    public IDisposable? EnterRequest(RequestContext<CallToolRequestParams> request) => Inner.EnterRequest(request);
    public Task<IDisposable?> AcquireLane(CancellationToken token) => Inner.AcquireLane(token);
    public void ActivateLane(IDisposable? lane) => Inner.ActivateLane(lane);
    public void MarkUncertain() => Inner.MarkUncertain();
    public async Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
    {
        if (faultFixture && operation is "ReadBlocks" or "optional-products.ExecuteOptionalProduct" or "Attach")
        {
            string tool = operation == "ReadBlocks" ? "ListPlcBlocks" : operation == "Attach" ? "AttachOpenProject" : "ListTestSuiteCases";
            using var request = Foundation.FoundationWorkerRequest.Use(Foundation.FoundationWorkerRequest.CorrelationId, tool);
            await plc!.Call("FaultFixtureArm", new() { ["method"] = "adapter." + operation }, token);
            return await Inner.Call(operation, args, token);
        }
        return await Inner.Call(operation, args, token);
    }
    public Task<JsonNode?> Observe(string operation, JsonObject args, CancellationToken token) => engine != null
        ? engine.Observe(operation, args, token) : plc!.Observe(operation, args, token);
    public JsonObject Snapshot() => services.Snapshot();
    public Task<JsonObject> Status(CancellationToken token) => services.StartForStatus(token);
    public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => services.Restart(confirmed, token);
    public Task<IDisposable> Acquire(CancellationToken token) => services.Acquire(token);
    public Task<Engine.EngineReply> Invoke(string id, string name, JsonObject args, bool preview, CancellationToken token)
        => services.Invoke(id, name, args, preview, token);
    public IDisposable UseProgress(Action<string>? report) => services.UseProgress(report);
    public CallToolResult ParkResult(CallToolResult result, string name) => ((Foundation.IFoundationWorkerResult)Inner).ParkResult(result, name);
    public void Dispose() => Inner.Dispose();
}
