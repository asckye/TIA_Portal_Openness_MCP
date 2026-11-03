using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.WorkerChannel;
using TiaMcpServer.Siemens;
using TiaOpenness.Shared;

internal static class EngineSessionFixture
{
    internal const string LeaseMessage = "The prior MCP owner did not release this TIA instance cleanly. Native outcome is unknown. Inspect diagnostics and restart that TIA instance before reconnecting; do not erase the lease to bypass this guard.";
    internal static int Run(string[] args)
    {
        string release = args[Array.IndexOf(args, "--native-session") + 1];
        string exe = Environment.ProcessPath!;
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        var identity = new ChannelIdentity(release, Hash(exe), Hash(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + release + ".dll")),
            Environment.ProcessId, args[Array.IndexOf(args, "--native-session") + 3]);
        int? attached = null;
        PortalProcessLease? lease = null;
        long epoch = 0; bool uncertain = false, hasProject = true, lockOnNextFamily = false, locked = false;
        string? slowShutdownMarker = null;
        string? leaseRoot = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT");
        JsonObject Status() => new() { ["releaseKey"] = release, ["readiness"] = new JsonObject { ["ready"] = true },
            ["behaviorCapabilities"] = BehaviorCapabilities.Table(typeof(BehaviorCapabilities).Assembly, release),
            ["sessionLocked"] = locked,
            ["binding"] = attached == null || !hasProject ? null : new JsonObject {
                ["tiaMajorVersion"] = release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release),
                ["processId"] = attached, ["processStartUtc"] = "2026-10-05T00:00:00Z",
                ["projectPath"] = "C:/fixture.ap" + release, ["projectName"] = "fixture", ["generation"] = epoch },
            ["session"] = new JsonObject { ["isConnected"] = attached != null } };
        ChannelResponse Dispatch(ChannelRequest request)
        {
            if (request.Method == "adapter.HostObserve")
                return ChannelResponse.Success(((string?)JsonNode.Parse(request.ArgumentsJson)?["operation"] == "assemblies"
                    ? new JsonArray() : (JsonNode)Status()).ToJsonString());
            if (request.Method == "adapter.FixtureCorrelation") return ChannelResponse.Success(new JsonObject {
                ["id"] = request.CorrelationId, ["tool"] = request.ToolName }.ToJsonString());
            if (request.Method == "adapter.FixtureSpill")
            {
                string full = new JsonObject { ["text"] = new string('x', ChannelLimits.ResponseBytes) }.ToJsonString();
                return ChannelResponse.WithSpill(full, () => WorkerReplySpill.Write(
                    Environment.GetEnvironmentVariable("TIA_MCP_WORKER_SPILLS_DIRECTORY") ?? DataLocations.Current.WorkerSpillsDirectory, full));
            }
            var input = JsonNode.Parse(request.ArgumentsJson)!.AsObject();
            if (request.Method == "adapter.FixtureNoProject") { hasProject = false; return ChannelResponse.Success("{}"); }
            if (request.Method == "adapter.FixtureSlowShutdown") { slowShutdownMarker = (string)input["marker"]!; return ChannelResponse.Success("{}"); }
            if (request.Method == "adapter.FixtureLockOnNextFamily") { lockOnNextFamily = true; return ChannelResponse.Success("{}"); }
            if (locked && request.Method != "adapter.Disconnect")
                return ChannelResponse.Error(new ChannelFailureException(SessionBehavior.Recovery, -32603, ChannelOutcome.RejectedBeforeNative));
            if (lockOnNextFamily && request.Method == "adapter.plc-software.ExecutePlcOrganisation")
            {
                var outcome = new TiaMcp.PlcWorker.WorkerSessionOutcomeState();
                var recovery = new TiaMcp.PlcWorker.WorkerSessionRecovery(outcome, () => false, () => { }, () => { }, _ => { }, () => { }, () => { },
                    bindingFault: () => "Fixture binding refresh failed");
                var response = recovery.Synchronize(request, ChannelResponse.Success(TiaMcp.PlcWorker.WorkerJson.Serialize(new TiaMcp.Adapters.Contracts.PlcSoftwareReply { Found = true })));
                locked = outcome.RequiresReset;
                return response;
            }
            if (request.Method == "adapter.Attach")
            {
                int pid = (int)input["processId"]!;
                if (pid is 901 or 902 or 903 or 904)
                    return ChannelResponse.Error(new ChannelFailureException(pid == 901 ? LeaseMessage : pid == 904 ? SessionBehavior.LeaseReserved : pid == 903 ? "Authored refusal at C:\\private\\lease.json" : "Internal secret exception text", -32603,
                        ChannelOutcome.RejectedBeforeNative, new JsonObject { ["exceptionType"] = pid == 903 ? "AdapterPreconditionException" : "InvalidOperationException" }.ToJsonString()));
                if (leaseRoot != null) lease = PortalProcessLease.Acquire(leaseRoot ?? DataLocations.Current.LeasesDirectory, pid, 1);
                attached = pid; epoch++;
                return ChannelResponse.Success(new JsonObject { ["Stage"] = "attached", ["OwnsPortal"] = false,
                    ["AttemptedPids"] = new JsonArray(pid), ["Strategy"] = "explicit", ["LaunchMode"] = "attach" }.ToJsonString());
            }
            if (request.Method == "adapter.Disconnect")
            {
                var result = new JsonObject { ["Stage"] = "disconnected", ["SessionState"] = "terminal", ["Strategy"] = "non-owning-attachment-only",
                    ["WorkerAcknowledged"] = true, ["Detached"] = attached != null, ["ProcessId"] = attached,
                    ["SavedProject"] = false, ["ClosedProject"] = false, ["LaunchMode"] = "never" };
                if (attached != null) epoch++;
                attached = null;
                lease?.ReleaseCleanly(); lease = null;
                return ChannelResponse.Success(result.ToJsonString());
            }
            if (request.Method == "adapter.FixtureUnknown") return ChannelResponse.Error(new ChannelFailureException("Fixture answered native unknown.", -32603, ChannelOutcome.Unknown));
            if (request.Method == "adapter.FixtureHang") { if (leaseRoot != null) File.WriteAllText(Path.Combine(leaseRoot, "busy"), ""); Thread.Sleep(10000); return ChannelResponse.Success("{}"); }
            if (request.Method == "adapter.ReadPortalProcessProjects")
                return ChannelResponse.Success(new JsonObject { ["ReleaseKey"] = release, ["Processes"] = new JsonArray(),
                    ["IdentityStrategy"] = "os-pid-and-start-time-observation-only" }.ToJsonString());
            if (request.Method.StartsWith("adapter.") && request.Method != "adapter.FixtureInvoke")
                return ChannelResponse.Success(new JsonObject { ["ReleaseKey"] = release, ["IsAttached"] = attached != null, ["ProcessId"] = attached,
                    ["ProjectFile"] = null, ["OwnsProject"] = false, ["IsLocalSession"] = false,
                    ["IdentityStatus"] = "unverified-cached-pid", ["RuntimeConnectionStatus"] = "not-probed" }.ToJsonString());
            string name = (string)input["name"]!, id = (string)input["requestId"]!;
            if ((bool?)input["arguments"]?["hang"] == true) { if (leaseRoot != null) File.WriteAllText(Path.Combine(leaseRoot, "busy"), ""); Thread.Sleep(10000); }
            bool unknown = (bool?)input["arguments"]?["fault"] == true;
            var data = new JsonObject { ["fixturePid"] = Environment.ProcessId };
            if (unknown) data["nativeOutcomeUnknown"] = true;
            var body = Envelope.Create(data, unknown
                ? new Error("Fixture native outcome unknown.", new OutcomeUnknownDetails("fixture", new Dictionary<string, JsonElement>())) : null,
                new Meta(DateTimeOffset.UtcNow, release, name, id, unknown ? Outcome.Unknown : Outcome.Succeeded,
                    unknown ? Execution.Unknown : Execution.ReadOnly, unknown, BehaviorPolicy.Current,
                    unknown ? Completeness.Unknown : Completeness.Complete, null,
                    new[] { new Warning(WarningCode.UnverifiedBehavior, "Fixture native behavior.", new Dictionary<string, JsonElement>()) }));
            var node = JsonNode.Parse(V4Json.Serialize(body))!;
            var reply = Status();
            reply["nativeCallIssued"] = unknown;
            reply["result"] = new JsonObject { ["isError"] = unknown, ["structuredContent"] = node,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = node.ToJsonString() }) };
            return ChannelResponse.Success(reply.ToJsonString());
        }
        var server = new ChannelServer(Console.OpenStandardInput(), Console.OpenStandardOutput(), identity, () => new(epoch, attached != null), request => {
            lease?.BeginRequest();
            var response = Dispatch(request);
            uncertain |= response.Failure?.Outcome == ChannelOutcome.Unknown
                || (bool?)(JsonNode.Parse(response.ResultJson ?? "null") as JsonObject)?["result"]?["structuredContent"]?["meta"]?["requiresSessionReset"] == true;
            lease?.CompleteRequest(uncertain);
            return response;
        }, ChannelProfile.Foundation);
        try { server.Run(); }
        finally
        {
            if (slowShutdownMarker != null) { File.WriteAllText(slowShutdownMarker, "stopping"); Thread.Sleep(2000); }
            if (!uncertain) lease?.ReleaseCleanly(); lease?.Dispose();
        }
        return 0;
    }
}
