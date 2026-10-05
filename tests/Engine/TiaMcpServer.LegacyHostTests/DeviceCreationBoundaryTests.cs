using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;
using TiaMcp.Logic.V4;
using Xunit;

public sealed class DeviceCreationBoundaryTests
{
    private sealed class Worker : IFoundationWorker
    {
        public int Calls;
        public string? Operation;
        public JsonObject? Arguments;
        public bool Unknown;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken cancellationToken)
        {
            Calls++; Operation = operation; Arguments = arguments;
            var data = new JsonObject { ["createIssued"] = Unknown, ["residueCheck"] = new JsonObject { ["status"] = "checked", ["added"] = new JsonArray("PLC_2") } };
            var error = Unknown ? new Error("Unknown write.", new OutcomeUnknownDetails("create", new Dictionary<string, JsonElement>()))
                : new Error("Missing catalog entry.", new NotFoundDetails((string?)arguments["typeIdentifier"]));
            var envelope = DeviceCreationSession.Result("19", "CreateHardwareDevice", (string)arguments["requestId"]!, data, error,
                Unknown ? Outcome.Unknown : Outcome.RejectedBeforeOperation, Unknown ? Execution.Unknown : Execution.NotStarted);
            return Task.FromResult(JsonNode.Parse(V4Json.Serialize(envelope)));
        }
        public void Dispose() { }
    }
    private static RequestContext<CallToolRequestParams> Request(string json) => new(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
        Params = new() { Name = "CreateHardwareDevice", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
    private static FoundationV4Tool Tool(Worker worker, bool safe, string release = "19") => new(
        new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "AddDeviceWithFallback"), worker), release,
        _ => safe ? BehaviorPolicy.SafeV4 : BehaviorPolicy.Current);
    private static Envelope Body(CallToolResult result)
    {
        var text = ((TextContentBlock)result.Content.Single()).Text;
        Assert.True(JsonNode.DeepEquals(result.StructuredContent, JsonNode.Parse(text)));
        var envelope = V4Json.Deserialize<Envelope>(text); Assert.Equal(!envelope.Ok, result.IsError); return envelope;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SelectedSchemaAndAdmissionAgree(bool safe)
    {
        var worker = new Worker(); var tool = Tool(worker, safe); var props = tool.ProtocolTool.InputSchema.GetProperty("properties");
        Assert.Equal(safe, props.TryGetProperty("typeIdentifier", out _));
        Assert.Equal(!safe, props.TryGetProperty("preferredMlfb", out _));
        Assert.Equal(!safe, props.TryGetProperty("dryRun", out _));
        var rejected = Body(await tool.InvokeAsync(Request(safe
            ? "{\"preferredMlfb\":\"X\",\"deviceName\":\"X\"}"
            : "{\"typeIdentifier\":\"X\",\"deviceName\":\"X\",\"family\":\"S7-1500\"}")));
        Assert.Equal(ErrorCode.InvalidArgument, rejected.Error!.Code); Assert.Equal(0, worker.Calls);
        Assert.Equal(safe ? BehaviorPolicy.SafeV4 : BehaviorPolicy.Current, rejected.Meta.BehaviorPolicy);
    }

    [Theory]
    [InlineData("{\"typeIdentifier\":\"X\",\"deviceName\":\"X\",\"family\":\"HMI\"}")]
    [InlineData("{\"typeIdentifier\":\"X\",\"deviceName\":\"X\",\"family\":\"S7-1500\",\"mode\":\"execute\"}")]
    [InlineData("{\"typeIdentifier\":null,\"deviceName\":\"X\",\"family\":\"S7-1500\"}")]
    public async Task CandidateRejectsUnsupportedArgumentsBeforeWorker(string json)
    {
        var worker = new Worker(); var result = Body(await Tool(worker, true).InvokeAsync(Request(json)));
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code); Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public async Task CandidateKeepsCorrelationAndPoisonedWorkerOutcome()
    {
        var worker = new Worker { Unknown = true };
        var result = Body(await Tool(worker, true).InvokeAsync(Request("{\"typeIdentifier\":\"X\",\"deviceName\":\"PLC_2\",\"family\":\"S7-1500\",\"mode\":\"apply\",\"confirm\":true,\"expectedPlanHash\":\"" + new string('a', 64) + "\",\"expectedProjectFile\":\"C:\\\\Test.ap19\"}")));
        Assert.Equal("CreateHardwareDeviceCandidate", worker.Operation); Assert.Equal(1, worker.Calls);
        Assert.Equal(Outcome.Unknown, result.Meta.Outcome); Assert.True(result.Meta.RequiresSessionReset);
        Assert.Equal((string?)worker.Arguments!["requestId"], result.Meta.RequestId);
        Assert.Equal("checked", result.Data!.Value.GetProperty("residueCheck").GetProperty("status").GetString());
        var outcome = new WorkerOutcomeState(); outcome.AcceptResult(worker.Operation!, worker.Arguments, JsonNode.Parse(V4Json.Serialize(result)));
        Assert.True(outcome.Poisoned); Assert.Throws<InvalidOperationException>(() => outcome.RequireUsable());
    }

    [Fact]
    public void RuntimeEnvironmentCannotEnableTheReleasedPolicy()
    {
        var previous = Environment.GetEnvironmentVariable("TiaMcpTestPolicy");
        try
        {
            Environment.SetEnvironmentVariable("TiaMcpTestPolicy", BehaviorCapabilities.DeviceCandidate);
            Assert.Equal(BehaviorPolicy.Current, BehaviorCapabilities.Released("19", "P6-DEVICE"));
            if (!typeof(BehaviorCapabilities).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == BehaviorCapabilities.TestProperty))
                Assert.Equal(BehaviorPolicy.Current, BehaviorCapabilities.Select(typeof(FoundationV4Tool).Assembly, "19", "P6-DEVICE"));
        }
        finally { Environment.SetEnvironmentVariable("TiaMcpTestPolicy", previous); }
    }

    public class ServerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.ReturnType == typeof(IServiceProvider) ? null : targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }
}
