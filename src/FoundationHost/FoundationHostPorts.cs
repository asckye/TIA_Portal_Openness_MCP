#if TIA_FOUNDATION_TEST_HOST
extern alias enginehost;
using EngineHostPipeline = enginehost::TiaMcp.FoundationHost.EngineHostPipeline;
using IEngineWorker = enginehost::TiaMcp.FoundationHost.IEngineWorker;
using IEngineWorkerProgress = enginehost::TiaMcp.FoundationHost.IEngineWorkerProgress;
using IEngineWorkerReadiness = enginehost::TiaMcp.FoundationHost.IEngineWorkerReadiness;
using EngineReply = enginehost::TiaMcp.FoundationHost.EngineReply;
using IWorkerLane = enginehost::TiaMcpServer.Dispatch.ToolDispatchLanes.IWorkerLane;
using IHostToolServices = enginehost::TiaMcpServer.ModelContextProtocol.IHostToolServices;
using ReleaseCatalogData = enginehost::TiaMcp.FoundationHost.ReleaseCatalogData;
using McpServer = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using TiaMcpServer.ModelContextProtocol;
#else
using IWorkerLane = TiaMcpServer.Dispatch.ToolDispatchLanes.IWorkerLane;
using TiaMcpServer.ModelContextProtocol;
#endif
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace TiaMcp.FoundationHost;

// All releases use the registered Foundation implementations through these host services.
// Native objects and their thread remain in the existing PLC worker.
internal static class FoundationHostPorts
{
    internal static ModelContextProtocol.Protocol.CallToolResult? GenerationFence(string name, string arguments, bool dispatched = false)
        => McpServer.GenerationFenceRefusal(name, arguments, dispatched, write: true);
    internal static IDisposable GenerationRequest(RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> request, CancellationToken token)
        => McpServer.UseProgressRequest(request, token);
    internal static Task<ModelContextProtocol.Protocol.CallToolResult?> BeforeDispatchAsync()
        => McpServer.FoundationBeforeDispatch?.Invoke() ?? Task.FromResult<ModelContextProtocol.Protocol.CallToolResult?>(null);
    internal static IReadOnlyList<McpServerTool> Create(IFoundationWorker worker, string release,
        IReadOnlyList<McpServerTool> shared, Func<JsonObject> readiness, string? apiDirectory)
    {
        var services = CreateServices(worker, release, readiness, apiDirectory);
        var pipeline = new EngineHostPipeline(release, services, shared);
        services.Roster = () => pipeline.AllTools;
        services.ResetExports = pipeline.ResetExports;
        if (worker is WorkerClient client) client.HostPipeline = pipeline;
        return pipeline.AllTools;
    }

    internal static Services CreateServices(IFoundationWorker worker, string release, Func<JsonObject> readiness, string? apiDirectory)
    {
        if (worker is WorkerClient plc) plc.SupervisorReadiness = readiness;
        return new(worker, release, readiness, apiDirectory);
    }

    internal sealed class Services(IFoundationWorker worker, string release, Func<JsonObject> readiness, string? apiDirectory)
        : IEngineWorker, IEngineWorkerProgress, IEngineWorkerReadiness, IHostToolServices
    {
        public Task PrepareReadiness(CancellationToken token)
            => worker is IFoundationWorkerReadiness readiness ? readiness.PrepareReadiness(token) : Task.CompletedTask;
        public IDisposable UseProgress(Action<string>? report)
            => worker is IEngineWorkerProgress progress ? progress.UseProgress(report) : new EmptyLane();
        internal Func<IReadOnlyList<McpServerTool>> Roster = () => Array.Empty<McpServerTool>();
        internal Action ResetExports = () => { };
        private readonly object sessionKey = new();
        public bool Faulted => worker is IEngineWorker engine ? engine.Faulted : worker is WorkerClient client ? client.WorkerFaulted : worker is IFoundationSessionWorker { Poisoned: true };
        public object SessionKey => worker is IEngineWorker engine ? engine.SessionKey : worker is WorkerClient client ? client.HostSessionKey : sessionKey;
        public JsonNode? Binding => Snapshot()["binding"]?.DeepClone();
        internal JsonObject OperatorSnapshot()
        {
            var result = (worker as IFoundationWorkerSupervisor)?.OperatorSnapshot() ?? new JsonObject();
            result["sessionLocked"] = Snapshot()["sessionLocked"]?.DeepClone();
            return result;
        }
        public JsonObject Snapshot()
        {
            if (worker is IEngineWorker engine)
            {
                var snapshot = engine.Snapshot();
                if ((string?)snapshot["state"] == "NotStarted")
                {
                    var initial = readiness();
                    snapshot["readiness"] = new JsonObject { ["ready"] = initial["ready"]?.DeepClone(), ["cause"] = initial["cause"]?.DeepClone(),
                        ["groupOk"] = initial["environment"]?["opennessGroupOk"]?.DeepClone(),
                        ["recommendedFix"] = initial["recommendedFix"]?.DeepClone(), ["recommendedFixZh"] = initial["recommendedFixZh"]?.DeepClone() };
                }
                return snapshot;
            }
            var result = worker is WorkerClient client ? client.HostSnapshot() : new JsonObject();
            var ready = readiness();
            ready["groupOk"] = ready["environment"]?["opennessGroupOk"]?.DeepClone();
            result["readiness"] = ready;
            return worker is WorkerClient plc ? plc.SupervisorSnapshot(ready) : result;
        }
        public Task<JsonObject> Status(CancellationToken token) => Status(false, token);
        public Task<JsonObject> StartForStatus(CancellationToken token) => Status(true, token);
        private async Task<JsonObject> Status(bool startForStatus, CancellationToken token)
        {
            if (worker is IEngineWorker engine) return startForStatus ? await engine.StartForStatus(token) : await engine.Status(token);
            if (worker is WorkerClient client) await client.RefreshHostState(startForStatus, token);
            else if (worker is IFoundationSessionWorker session)
                return ProjectStatus(JsonNode.Parse(session.ApprovalIdentity)!.AsObject(), Snapshot());
            var result = Snapshot();
            if (ReleaseCatalogData.Releases[release].ExtendedWorkerStatus && worker is WorkerClient plc)
            {
                var cached = plc.SharedStatus?["session"] ?? plc.HostSnapshot()["session"];
                result["session"] = new JsonObject { ["isConnected"] = cached?["isConnected"]?.DeepClone(),
                    ["project"] = cached?["project"]?.DeepClone() ?? JsonValue.Create("-"), ["session"] = cached?["session"]?.DeepClone() ?? JsonValue.Create("-") };
            }
            return result;
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
            if (worker is not IFoundationSessionWorker session) return new EmptyLane();
            var lane = await session.AcquireLane(token);
            return lane == null ? new EmptyLane() : new WorkerLane(session, lane);
        }
        public async Task<JsonObject> Restart(bool confirmed, CancellationToken token)
        {
            var result = worker is IEngineWorker engine ? await engine.Restart(confirmed, token)
                : worker is WorkerClient client ? await client.RestartHostWorker(confirmed, token)
                : ResponseMeta.Unstamped(!confirmed, ("dryRun", JsonValue.Create(!confirmed)), ("worker", Snapshot()));
            if ((bool?)result["restarted"] == true) ResetExports();
            return result;
        }
        public Task<EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
            => worker is IEngineWorker engine ? engine.Invoke(id, name, arguments, preview, token)
                : throw new InvalidOperationException("This release has no engine dispatch route.");
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
            => worker.Call(operation, arguments, token);
        public async Task<JsonNode?> Observe(string operation, JsonObject arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var ready = readiness();
            var profile = ReleaseCatalogData.Releases[release];
            bool useLocalReadiness = profile.NativeObservations && (bool?)ready["ready"] == false
                && ready["environment"]?["tiaInstallPath"] == null;
            if (!profile.ObservationSet.Contains(operation)) throw new NotSupportedException("This observation is unavailable on release " + release + ": " + operation);
            if (operation == "tool-names") return new JsonArray((profile.ObservationToolNames.Count == 0
                ? Roster().Select(t => t.ProtocolTool.Name) : profile.ObservationToolNames).Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
            async Task<JsonNode?> ReadFacts()
            {
                if (worker is IHostToolServices facts) return await facts.Observe(operation, arguments, token);
                if (operation == "group.fix") return await worker.Call("project-lifecycle.EnsureOpennessUserGroup", new JsonObject(), token);
                if (operation == "session.ListPortalProcessProjects")
                {
                    var projects = await worker.Call("ReadPortalProcessProjects", arguments, token);
                    return new JsonArray((projects?["Processes"]?.AsArray() ?? new JsonArray()).Select(p => (JsonNode?)JsonValue.Create(p!.ToJsonString())).ToArray());
                }
                try { return worker is WorkerClient plc ? await plc.Observe(operation, arguments, token)
                    : await worker.Call("HostObserve", new JsonObject { ["operation"] = operation, ["arguments"] = arguments.DeepClone() }, token); }
                catch (WorkerOperationException error) when (error.Outcome == "read-failed")
                { throw new TiaMcp.WorkerChannel.ChannelFailureException(error.Message, error.Code, TiaMcp.WorkerChannel.ChannelOutcome.ReadFailed, error.EvidenceJson); }
            }
            switch (operation)
            {
                case "environment":
                    if (profile.NativeObservations && !useLocalReadiness) return await ReadFacts();
                    return new JsonArray(new JsonObject { ["Id"] = "tia-install", ["Ok"] = ready["environment"]?["tiaInstallPath"] != null,
                        ["NameEn"] = "TIA Portal installation", ["NameZh"] = "TIA Portal installation", ["DetailEn"] = ready["cause"]?.DeepClone() ?? JsonValue.Create("Matching release API files found."),
                        ["DetailZh"] = ready["cause"]?.DeepClone(), ["FixEn"] = ready["recommendedFix"]?.DeepClone(), ["FixZh"] = ready["recommendedFixZh"]?.DeepClone() });
                case "group": return profile.NativeObservations && !useLocalReadiness ? await ReadFacts() : ready["environment"]?["opennessGroupOk"]?.DeepClone() ?? JsonValue.Create(false);
                case "group.fix":
                    if (profile.NativeObservations) return await ReadFacts();
                    throw new NotSupportedException("Use Windows administration to configure the Siemens TIA Openness group on this release.");
                case "assemblies":
                    if (profile.NativeObservations && !useLocalReadiness) return await ReadFacts();
                    var assemblies = new JsonArray();
                    if (Directory.Exists(apiDirectory))
                        foreach (var path in Directory.GetFiles(apiDirectory!, "Siemens.Engineering*.dll"))
                        {
                            var identity = System.Reflection.AssemblyName.GetAssemblyName(path);
                            assemblies.Add(new JsonObject { ["name"] = identity.Name, ["version"] = identity.Version?.ToString(), ["location"] = path });
                        }
                    return assemblies;
                case "tool-names": return new JsonArray(Roster().Select(t => (JsonNode?)JsonValue.Create(t.ProtocolTool.Name)).ToArray());
                case "reflection": return profile.NativeObservations ? await ReadFacts() : new JsonObject { ["guard"] = true, ["DescribeService"] = false, ["InvokeService"] = false, ["InvokeObject"] = false };
                case "reflection-deny": return profile.NativeObservations ? await ReadFacts() : JsonValue.Create((string?)arguments["method"] == "GetAttribute" ? null : "Reflection writes are not registered on this release.");
                case "session.GetState":
                    if (profile.NativeObservations && !useLocalReadiness) return await ReadFacts();
                    var state = useLocalReadiness ? Snapshot()["session"] : (await Status(token))["session"];
                    return new JsonObject { ["IsConnected"] = state?["isConnected"]?.DeepClone() ?? JsonValue.Create(false), ["Project"] = state?["project"]?.DeepClone() };
                case "session.ListPortalProcessProjects":
                    if (profile.NativeObservations) return await ReadFacts();
                    var query = await worker.Call("ReadPortalProcessProjects", arguments, token);
                    return new JsonArray((query?["Processes"]?.AsArray() ?? new JsonArray())
                        .Select(p => (JsonNode?)JsonValue.Create(p!.ToJsonString())).ToArray());
                case "session.GetProjectTree": return profile.NativeObservations ? await ReadFacts() : await worker.Call("ReadProjectTree", arguments, token);
                case "session.ValidateAutomationContext": return await ReadFacts();
                case "session.ConnectPortal":
                    if (profile.NativeObservations) return await ReadFacts();
                    var connected = (await McpServer.ToolInvoker.InvokeAsync("ConnectPortal", McpServer.EmptyArguments(), false).ConfigureAwait(false)).Result;
                    return JsonValue.Create((bool?)connected.StructuredContent?["ok"] == true);
                default: throw new NotSupportedException("This observation is unavailable on release " + release + ": " + operation);
            }
        }
        public void Dispose() { }
        private sealed class WorkerLane(IFoundationSessionWorker owner, IDisposable lane) : IDisposable, IWorkerLane
        {
            public void Activate() => owner.ActivateLane(lane);
            public void Dispose() => lane.Dispose();
        }
        private sealed class EmptyLane : IDisposable { public void Dispose() { } }
    }
}
