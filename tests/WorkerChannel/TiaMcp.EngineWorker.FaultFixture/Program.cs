using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.WorkerChannel;

#if !TIA_MCP_ENGINE_WORKER_FAULT_FIXTURE
return 2;
#else
string release = args[Array.IndexOf(args, "--tia-major-version") + 1];
string exe = Environment.ProcessPath!;
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
var identity = new ChannelIdentity(release, Hash(exe), Hash(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + release + ".dll")),
    Environment.ProcessId, Environment.GetEnvironmentVariable("TIA_MCP_ENGINE_NONCE")!);
bool bound = false;
long epoch = 0;
JsonObject Status() => new() { ["releaseKey"] = release, ["diagnosticsDirectory"] = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY"), ["readiness"] = new JsonObject { ["ready"] = true },
    ["behaviorCapabilities"] = BehaviorCapabilities.Table(typeof(BehaviorCapabilities).Assembly, release),
    ["binding"] = bound ? new JsonObject { ["projectPath"] = "C:/fixture.ap" + release } : null,
    ["session"] = new JsonObject { ["isConnected"] = bound }, ["nativeFault"] = null };
var server = new ChannelServer(Console.OpenStandardInput(), Console.OpenStandardOutput(), identity, () => new(epoch, bound), request => {
    if (request.Method == "engine.status") return ChannelResponse.Success(Status().ToJsonString());
    string? mode = WorkerFaultInjection.Mode(Assembly.GetExecutingAssembly());
    WorkerFaultInjection.BeforeDispatch(mode, request);
    var args = JsonNode.Parse(request.ArgumentsJson)!.AsObject();
    if (request.Method == "adapter.Attach") { bound = true; epoch++; return ChannelResponse.Success("{\"Stage\":\"attached\",\"OwnsPortal\":false,\"AttemptedPids\":[123],\"Strategy\":\"explicit\",\"LaunchMode\":\"attach\"}"); }
    if (request.Method == "adapter.Bind") { bound = true; epoch++; return ChannelResponse.Success("{\"ProjectFile\":\"C:/fixture.ap" + release + "\"}"); }
    if (mode == "slow" || mode == "progress")
    {
        for (int i = 0; i < 3; i++) {
            Thread.Sleep(50);
            if (mode == "progress" && (bool?)args["progress"] == true)
                request.ReportProgress(i * 50, new JsonObject { ["progress"] = i, ["total"] = 2, ["message"] = "fixture progress" }.ToJsonString());
        }
    }
    if (mode == "tia-lost" && request.Method.StartsWith("adapter."))
        return ChannelResponse.Error(new ChannelFailure("Fixture TIA process lost.", -32603, ChannelOutcome.Unknown,
            "{\"exceptionType\":\"PortalProcessLost\"}"));
    string padding = mode == "oversized" ? new string('x', ChannelLimits.ResponseBytes) : "fixture";
    if (request.Method.StartsWith("adapter.")) {
        string raw = request.Method == "adapter.ReadBlocks" ? new JsonArray(new JsonObject { ["Name"] = padding }).ToJsonString()
            : new JsonObject { ["ReleaseKey"] = release, ["IsAttached"] = bound, ["ProcessId"] = bound ? 123 : null,
                ["ProjectFile"] = bound ? "C:/fixture.ap" + release : null, ["OwnsProject"] = false, ["IsLocalSession"] = false,
                ["IdentityStatus"] = "unverified-cached-pid", ["RuntimeConnectionStatus"] = "not-probed" }.ToJsonString();
        return ChannelResponse.WithSpill(raw, () => WorkerReplySpill.Write(Environment.GetEnvironmentVariable("TIA_MCP_WORKER_SPILLS_DIRECTORY")!, raw));
    }
    var status = Status();
    string name = (string)args["name"]!, id = (string)args["requestId"]!;
    var meta = new Meta(DateTimeOffset.UtcNow, release, name, id,
        mode == "tia-lost" ? Outcome.ReadFailed : Outcome.Succeeded, Execution.ReadOnly,
        false, BehaviorPolicy.Current, mode == "tia-lost" ? Completeness.None : Completeness.Complete,
        null, new[] { new Warning(WarningCode.UnverifiedBehavior, "Fixture native behavior.", new Dictionary<string, JsonElement>()) });
    var body = Envelope.Create(new JsonObject { ["fixture"] = padding }, mode == "tia-lost"
        ? new Error("Fixture TIA process lost.", new NativeOperationFailedDetails(null, "Fixture TIA process lost.", new Dictionary<string, JsonElement>())) : null, meta);
    var node = JsonNode.Parse(V4Json.Serialize(body))!;
    status["result"] = new JsonObject { ["isError"] = mode == "tia-lost", ["structuredContent"] = node,
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = node.ToJsonString() }) };
    status["nativeCallIssued"] = true;
    if (mode == "tia-lost") status["nativeFault"] = "Fixture TIA process lost.";
    string wire = status.ToJsonString();
    return ChannelResponse.WithSpill(wire, () => WorkerReplySpill.Write(Environment.GetEnvironmentVariable("TIA_MCP_WORKER_SPILLS_DIRECTORY")!, wire));
}, ChannelProfile.Engine);
server.Run();
return 0;
#endif
