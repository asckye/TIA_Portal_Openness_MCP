#if TIA_FOUNDATION_TEST_HOST
extern alias toolpipeline;
using EngineHostPipeline = toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline;
using IEngineWorker = toolpipeline::TiaMcp.FoundationHost.IEngineWorker;
using IEngineWorkerProgress = toolpipeline::TiaMcp.FoundationHost.IEngineWorkerProgress;
using EngineReply = toolpipeline::TiaMcp.FoundationHost.EngineReply;
using IWorkerLane = toolpipeline::TiaMcpServer.Dispatch.ToolDispatchLanes.IWorkerLane;
using IHostToolServices = toolpipeline::TiaMcpServer.ModelContextProtocol.IHostToolServices;
using ReleaseCatalogData = toolpipeline::TiaMcp.FoundationHost.ReleaseCatalogData;
using UnsettledCalls = toolpipeline::TiaMcp.FoundationHost.UnsettledCalls;
using LateCalls = toolpipeline::TiaMcp.FoundationHost.LateCalls;
using McpServer = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;
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
        : IEngineWorker, IEngineWorkerProgress, IHostToolServices
    {
        public async Task<JsonObject?> ReadSimulatorInstances(CancellationToken token)
        {
            if (worker is IEngineWorker engine) return await engine.ReadSimulatorInstances(token).ConfigureAwait(false);
            return await worker.Call("runtime-channels.ExecutePlcSimAdvanced", new JsonObject {
                ["request"] = new JsonObject { ["operation"] = "ReadPlcSimAdvancedInstances",
                    ["arguments"] = new JsonObject { ["includeState"] = true } } }, token).ConfigureAwait(false) as JsonObject;
        }
        public IDisposable UseProgress(Action<string>? report)
            => worker is IEngineWorkerProgress progress ? progress.UseProgress(report) : new EmptyLane();
        internal Func<IReadOnlyList<McpServerTool>> Roster = () => Array.Empty<McpServerTool>();
        internal Action ResetExports = () => { };
        private readonly object sessionKey = new();
        public bool Faulted => worker is IEngineWorker engine ? engine.Faulted : worker is WorkerClient client ? client.WorkerFaulted
            : worker is IFoundationSessionWorker { Faulted: true };
        public UnsettledCalls Unsettled => worker is IEngineWorker engine ? engine.Unsettled : SharedSessionState.For(worker).Unsettled;
        public LateCalls LateResults => worker is IEngineWorker engine ? engine.LateResults : SharedSessionState.For(worker).LateResults;
        public object SessionKey => worker is IEngineWorker engine ? engine.SessionKey : worker is WorkerClient client ? client.HostSessionKey : sessionKey;
        public bool RequiresOpennessInstallation { get; } = FoundationPassiveDiagnostics.RequiresOpennessInstallation(worker);
        public JsonNode? Binding => Snapshot()["binding"]?.DeepClone();
        internal JsonObject OperatorSnapshot()
            => SharedSessionState.For(worker).OperatorSnapshot(release);
        public JsonObject EnvironmentReadiness()
        {
            var environment = readiness();
            return new JsonObject { ["ready"] = environment["ready"]?.DeepClone(), ["cause"] = environment["cause"]?.DeepClone(),
                ["groupOk"] = environment["environment"]?["opennessGroupOk"]?.DeepClone(),
                ["recommendedFix"] = environment["recommendedFix"]?.DeepClone(), ["recommendedFixZh"] = environment["recommendedFixZh"]?.DeepClone() };
        }
        public JsonObject Snapshot()
        {
            if (worker is IEngineWorker engine)
            {
                var snapshot = engine.Snapshot();
                if ((string?)snapshot["state"] == "NotStarted")
                    snapshot["readiness"] = EnvironmentReadiness();
                return SessionSnapshot(snapshot);
            }
            var ready = readiness();
            ready["groupOk"] = ready["environment"]?["opennessGroupOk"]?.DeepClone();
            var result = worker is WorkerClient plc ? plc.SupervisorSnapshot(ready) : new JsonObject { ["readiness"] = ready };
            return SessionSnapshot(result);
        }
        private JsonObject SessionSnapshot(JsonObject result)
        {
            var state = SharedSessionState.For(worker).Snapshot(release);
            result["binding"] = state["binding"]?.DeepClone();
            string? file = (string?)state["binding"]?["identity"]?["projectPath"];
            result["session"] = new JsonObject { ["isConnected"] = state["connected"]?.DeepClone(),
                ["project"] = (string?)state["binding"]?["identity"]?["projectName"] ?? (file == null ? "-" : Path.GetFileNameWithoutExtension(file)),
                ["session"] = (worker as WorkerClient)?.SharedStatus?["session"]?["session"]?.DeepClone() ?? JsonValue.Create("-") };
            result["sessionState"] = state;
            return result;
        }
        public Task<JsonObject> Status(CancellationToken token) => Status(false, token);
        public Task<JsonObject> StartForStatus(CancellationToken token) => Status(true, token);
        private async Task<JsonObject> Status(bool startForStatus, CancellationToken token)
        {
            if (worker is IEngineWorker engine) return SessionSnapshot(startForStatus ? await engine.StartForStatus(token) : await engine.Status(token));
            if (worker is WorkerClient client) await client.RefreshHostState(startForStatus, token);
            return Snapshot();
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
            if (result["worker"] is JsonObject snapshot) result["worker"] = SessionSnapshot(snapshot);
            return result;
        }
        private IEngineWorker Engine => worker as IEngineWorker ?? throw new InvalidOperationException("This release has no engine dispatch route.");
        public Task<EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
            => Engine.Invoke(id, name, arguments, preview, token);
        public Task<EngineReply> DispatchWrite(string id, string name, JsonObject arguments, Action sending, CancellationToken token)
            => Engine.DispatchWrite(id, name, arguments, sending, token);
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
            => worker.Call(operation, arguments, token);
        public async Task<JsonNode?> Observe(string operation, JsonObject arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var ready = readiness();
            var profile = ReleaseCatalogData.Releases[release];
            bool useLocalReadiness = profile.NativeObservations && (bool?)ready["ready"] == false
                && ready["environment"]?["tiaInstallPath"] == null;
            if (!profile.ObservationSet.Contains(operation))
                throw new NotSupportedException("This observation is unavailable on release " + release + ": " + operation);
            if (operation == "tool-names")
            {
                return new JsonArray(Roster().Select(t => (JsonNode?)JsonValue.Create(t.ProtocolTool.Name)).ToArray());
            }
            async Task<JsonNode?> ReadFacts()
            {
                if (worker is IHostToolServices facts) return await facts.Observe(operation, arguments, token);
                if (operation == "group.fix") return await worker.Call("project-lifecycle.EnsureOpennessUserGroup", new JsonObject(), token);
                if (operation == "session.ListPortalProcessProjects")
                {
                    var projects = await worker.Call("ReadPortalProcessProjects", arguments, token);
                    return new JsonArray((projects?["Processes"]?.AsArray() ?? new JsonArray()).Select(p => (JsonNode?)JsonValue.Create(p!.ToJsonString()))
                            .ToArray());
                }
                try { return worker is WorkerClient plc ? await plc.Observe(operation, arguments, token)
                    : await worker.Call("HostObserve", new JsonObject { ["operation"] = operation, ["arguments"] = arguments.DeepClone() }, token); }
                catch (WorkerOperationException error) when (error.Outcome == "read-failed")
                {
                    throw new TiaMcp.WorkerChannel
                        .ChannelFailureException(error.Message, error.Code, TiaMcp.WorkerChannel.ChannelOutcome.ReadFailed, error.EvidenceJson);
                }
            }
            switch (operation)
            {
                case "environment":
                    if (profile.NativeObservations && !useLocalReadiness) return await ReadFacts();
                    return new JsonArray(new JsonObject { ["Id"] = "tia-install", ["Ok"] = ready["environment"]?["tiaInstallPath"] != null,
                        ["NameEn"] = "TIA Portal installation", ["NameZh"] = "TIA Portal installation", ["DetailEn"] = ready["cause"]?.DeepClone() ?? JsonValue
                            .Create("Matching release API files found."),
                        ["DetailZh"] = ready["cause"]?.DeepClone(), ["FixEn"] = ready["recommendedFix"]?.DeepClone(), ["FixZh"] = ready["recommendedFixZh"]?
                            .DeepClone() });
                case "group": return profile.NativeObservations && !useLocalReadiness ? await ReadFacts() : ready["environment"]?["opennessGroupOk"]?
                    .DeepClone() ?? JsonValue.Create(false);
                case "group.fix":
                    if (profile.NativeObservations) return await ReadFacts();
                    throw new NotSupportedException("Use Windows administration to configure the Siemens TIA Openness group on this release.");
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
                case "reflection": return profile.NativeObservations ? await ReadFacts() : new JsonObject {
                    ["guard"] = true, ["DescribeService"] = false, ["InvokeService"] = false, ["InvokeObject"] = false };
                case "reflection-deny": return profile.NativeObservations ? await ReadFacts() : JsonValue
                    .Create((string?)arguments["method"] == "GetAttribute" ? null : "Reflection writes are not registered on this release.");
                case "session.GetState":
                    if (profile.NativeObservations && !useLocalReadiness) return await ReadFacts();
                    var state = useLocalReadiness ? Snapshot()["session"] : (await Status(token))["session"];
                    return new JsonObject {
                        ["IsConnected"] = state?["isConnected"]?.DeepClone() ?? JsonValue.Create(false), ["Project"] = state?["project"]?.DeepClone() };
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
