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
        bool foundation = args.Contains("--native-session");
        string release = args[Array.IndexOf(args, foundation ? "--native-session" : "--tia-major-version") + 1];
        string exe = Environment.ProcessPath!;
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        var identity = new ChannelIdentity(release, Hash(exe), Hash(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + release + ".dll")),
            Environment.ProcessId, foundation ? args[Array.IndexOf(args, "--native-session") + 3] : Environment.GetEnvironmentVariable("TIA_MCP_ENGINE_NONCE")!);
        int? attached = null;
        PortalProcessLease? lease = null;
        long epoch = 0; bool uncertain = false;
        string? leaseRoot = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT");
        JsonObject Status() => new() { ["releaseKey"] = release, ["readiness"] = new JsonObject { ["ready"] = true },
            ["behaviorCapabilities"] = BehaviorCapabilities.Table(typeof(BehaviorCapabilities).Assembly, release),
            ["binding"] = attached == null ? null : new JsonObject { ["projectPath"] = "C:/fixture.ap" + release },
            ["session"] = new JsonObject { ["isConnected"] = attached != null } };
        ChannelResponse Dispatch(ChannelRequest request)
        {
            if (request.Method == "engine.status") return ChannelResponse.Success(Status().ToJsonString());
            var input = JsonNode.Parse(request.ArgumentsJson)!.AsObject();
            if (request.Method == "engine.observe" && (string?)input["operation"] == "assemblies")
                return ChannelResponse.Success("[]");
            if (request.Method == "adapter.Attach")
            {
                int pid = (int)input["processId"]!;
                if (pid is 901 or 902 or 903 or 904)
                    return ChannelResponse.Error(new ChannelFailure(pid == 901 ? LeaseMessage : pid == 904 ? SessionBehavior.LeaseReserved : pid == 903 ? "Authored refusal at C:\\private\\lease.json" : "Internal secret exception text", -32603,
                        ChannelOutcome.RejectedBeforeNative, new JsonObject { ["exceptionType"] = pid == 903 ? "AdapterPreconditionException" : "InvalidOperationException" }.ToJsonString()));
                if (!foundation || leaseRoot != null) lease = PortalProcessLease.Acquire(leaseRoot ?? DataLocations.Current.LeasesDirectory, pid, 1);
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
            if (request.Method == "adapter.FixtureUnknown") return ChannelResponse.Error(new ChannelFailure("Fixture answered native unknown.", -32603, ChannelOutcome.Unknown));
            if (request.Method == "adapter.FixtureHang") { if (leaseRoot != null) File.WriteAllText(Path.Combine(leaseRoot, "busy"), ""); Thread.Sleep(10000); return ChannelResponse.Success("{}"); }
            if (request.Method == "adapter.ReadPortalProcessProjects")
                return ChannelResponse.Success(new JsonObject { ["ReleaseKey"] = release, ["Processes"] = new JsonArray(),
                    ["IdentityStrategy"] = "os-pid-and-start-time-observation-only" }.ToJsonString());
            if (request.Method.StartsWith("adapter."))
                return ChannelResponse.Success(new JsonObject { ["ReleaseKey"] = release, ["IsAttached"] = attached != null, ["ProcessId"] = attached,
                    ["ProjectFile"] = null, ["OwnsProject"] = false, ["IsLocalSession"] = false,
                    ["IdentityStatus"] = "unverified-cached-pid", ["RuntimeConnectionStatus"] = "not-probed" }.ToJsonString());
            string name = (string)input["name"]!, id = (string)input["requestId"]!;
            if ((bool?)input["arguments"]?["hang"] == true) Thread.Sleep(10000);
            bool unknown = (bool?)input["arguments"]?["fault"] == true;
            var body = Envelope.Create(new JsonObject { ["fixturePid"] = Environment.ProcessId }, unknown
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
            if (request.Method != "engine.status") lease?.BeginRequest();
            var response = Dispatch(request);
            uncertain |= response.Failure?.Outcome == ChannelOutcome.Unknown
                || (bool?)JsonNode.Parse(response.ResultJson)?["result"]?["structuredContent"]?["meta"]?["requiresSessionReset"] == true;
            if (request.Method != "engine.status") lease?.CompleteRequest(uncertain);
            return response;
        }, foundation ? ChannelProfile.Foundation : ChannelProfile.Engine);
        try { server.Run(); }
        finally { if (!uncertain) lease?.ReleaseCleanly(); lease?.Dispose(); }
        return 0;
    }
}
