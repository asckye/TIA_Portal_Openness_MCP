#if TIA_FOUNDATION_TEST_HOST
extern alias enginehost;
using EngineHostPipeline = enginehost::TiaMcp.FoundationHost.EngineHostPipeline;
using IEngineWorker = enginehost::TiaMcp.FoundationHost.IEngineWorker;
using EngineReply = enginehost::TiaMcp.FoundationHost.EngineReply;
using IHostToolServices = enginehost::TiaMcpServer.ModelContextProtocol.IHostToolServices;
using McpServer = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using TiaMcpServer.ModelContextProtocol;
#else
using TiaMcpServer.ModelContextProtocol;
#endif
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace TiaMcp.FoundationHost;

// The old-release bridge uses the registered Foundation implementations as-is.
// Native objects and their thread remain in the existing PLC worker.
internal static class FoundationHostPorts
{
    internal static ModelContextProtocol.Protocol.CallToolResult? BeforeDispatch() => McpServer.FoundationBeforeDispatch?.Invoke();
    internal static IReadOnlyList<McpServerTool> Create(IFoundationWorker worker, string release,
        IReadOnlyList<McpServerTool> shared, Func<JsonObject> readiness, string? apiDirectory)
    {
        var services = new Services(worker, release, readiness, apiDirectory);
        var pipeline = new EngineHostPipeline(release, services, shared);
        services.Roster = () => pipeline.AllTools;
        services.ResetExports = pipeline.ResetExports;
        if (worker is WorkerClient client) client.HostPipeline = pipeline;
        return pipeline.AllTools;
    }

    private sealed class Services(IFoundationWorker worker, string release, Func<JsonObject> readiness, string? apiDirectory)
        : IEngineWorker, IHostToolServices
    {
        internal Func<IReadOnlyList<McpServerTool>> Roster = () => Array.Empty<McpServerTool>();
        internal Action ResetExports = () => { };
        private readonly object sessionKey = new();
        public bool Faulted => worker is IFoundationSessionWorker { Poisoned: true };
        public object SessionKey => worker is WorkerClient client ? client.HostSessionKey : sessionKey;
        public JsonNode? Binding => Snapshot()["binding"]?.DeepClone();
        public JsonObject Snapshot()
        {
            var result = worker is WorkerClient client ? client.HostSnapshot() : new JsonObject();
            var ready = readiness();
            ready["groupOk"] = ready["environment"]?["opennessGroupOk"]?.DeepClone();
            result["readiness"] = ready;
            return result;
        }
        public async Task<JsonObject> Status(CancellationToken token)
        {
            if (worker is WorkerClient client) await client.RefreshHostState(token);
            else if (worker is IFoundationSessionWorker session)
                return ProjectStatus(JsonNode.Parse(session.ApprovalIdentity)!.AsObject(), Snapshot());
            return Snapshot();
        }
        private JsonObject ProjectStatus(JsonObject identity, JsonObject result)
        {
            string? file = (string?)identity["ProjectFile"] ?? (string?)identity["ProjectPath"];
            string? project = (string?)identity["Project"] ?? (file == null ? null : Path.GetFileNameWithoutExtension(file));
            result["session"] = new JsonObject { ["isConnected"] = identity["ProcessId"] != null, ["project"] = project };
            result["binding"] = new JsonObject { ["identity"] = new JsonObject {
                ["tiaMajorVersion"] = TiaMcp.Versioning.TiaVersionCatalog.Get(release).MajorVersion, ["processId"] = identity["ProcessId"]?.DeepClone(),
                ["processStartUtc"] = identity["ProcessStartUtc"]?.DeepClone(), ["projectPath"] = file, ["projectName"] = project,
                ["generation"] = identity["BindingEpoch"]?.DeepClone() } };
            return result;
        }
        public async Task<IDisposable> Acquire(CancellationToken token)
        {
            var lane = worker is IFoundationSessionWorker session ? await session.AcquireLane(token) : null;
            (worker as IFoundationSessionWorker)?.ActivateLane(lane);
            return lane ?? new EmptyLane();
        }
        public async Task<JsonObject> Restart(bool confirmed, CancellationToken token)
        {
            var result = worker is WorkerClient client ? await client.RestartHostWorker(confirmed, token)
                : ResponseMeta.Unstamped(!confirmed, ("dryRun", JsonValue.Create(!confirmed)), ("worker", Snapshot()));
            if ((bool?)result["restarted"] == true) ResetExports();
            return result;
        }
        public Task<EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
            => throw new InvalidOperationException("This release has no engine dispatch route.");
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
            => worker.Call(operation, arguments, token);
        public async Task<JsonNode?> Observe(string operation, JsonObject arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var ready = readiness();
            switch (operation)
            {
                case "environment":
                    return new JsonArray(new JsonObject { ["Id"] = "tia-install", ["Ok"] = ready["environment"]?["tiaInstallPath"] != null,
                        ["NameEn"] = "TIA Portal installation", ["NameZh"] = "TIA Portal installation", ["DetailEn"] = ready["cause"]?.DeepClone() ?? JsonValue.Create("Matching release API files found."),
                        ["DetailZh"] = ready["cause"]?.DeepClone(), ["FixEn"] = ready["recommendedFix"]?.DeepClone(), ["FixZh"] = ready["recommendedFixZh"]?.DeepClone() });
                case "group": return ready["environment"]?["opennessGroupOk"]?.DeepClone() ?? JsonValue.Create(false);
                case "group.fix": throw new NotSupportedException("Use Windows administration to configure the Siemens TIA Openness group on this release.");
                case "assemblies":
                    var assemblies = new JsonArray();
                    if (Directory.Exists(apiDirectory))
                        foreach (var path in Directory.GetFiles(apiDirectory!, "Siemens.Engineering*.dll"))
                        {
                            var identity = System.Reflection.AssemblyName.GetAssemblyName(path);
                            assemblies.Add(new JsonObject { ["name"] = identity.Name, ["version"] = identity.Version?.ToString(), ["location"] = path });
                        }
                    return assemblies;
                case "tool-names": return new JsonArray(Roster().Select(t => (JsonNode?)JsonValue.Create(t.ProtocolTool.Name)).ToArray());
                case "reflection": return new JsonObject { ["guard"] = true, ["DescribeService"] = false, ["InvokeService"] = false, ["InvokeObject"] = false };
                case "reflection-deny": return JsonValue.Create((string?)arguments["method"] == "GetAttribute" ? null : "Reflection writes are not registered on this release.");
                case "session.GetState":
                    var state = (await Status(token))["session"];
                    return new JsonObject { ["IsConnected"] = state?["isConnected"]?.DeepClone() ?? JsonValue.Create(false), ["Project"] = state?["project"]?.DeepClone() };
                case "session.ListPortalProcessProjects":
                    var query = await worker.Call("ReadPortalProcessProjects", arguments, token);
                    return new JsonArray((query?["Processes"]?.AsArray() ?? new JsonArray())
                        .Select(p => (JsonNode?)JsonValue.Create(p!.ToJsonString())).ToArray());
                case "session.GetProjectTree": return await worker.Call("ReadProjectTree", arguments, token);
                case "session.ConnectPortal":
                    var connected = McpServer.ToolInvoker.Invoke("ConnectPortal", McpServer.EmptyArguments(), false).Result;
                    return JsonValue.Create((bool?)connected.StructuredContent?["ok"] == true);
                default: throw new NotSupportedException("This observation is unavailable on release " + release + ": " + operation);
            }
        }
        public void Dispose() { }
        private sealed class EmptyLane : IDisposable { public void Dispose() { } }
    }
}
