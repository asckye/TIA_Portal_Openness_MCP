using System.Reflection;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;
using Xunit;

public sealed class DeviceCreationBoundaryTests
{
    private static RequestContext<CallToolRequestParams> Request(string json) => new(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
        Params = new() { Name = "CreateHardwareDevice", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
    private static FoundationV4Tool Tool(CandidateWorkerFixture worker, bool safe, string release = "19",
        Func<ApprovalSettings>? approvalSettings = null) => new(
        new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "AddDeviceWithFallback"), worker), release,
        _ => safe ? BehaviorPolicy.SafeV4 : BehaviorPolicy.Current, approvalSettings);
    private static void AssertApprovalDisabled(Envelope envelope)
        => Assert.Single(envelope.Meta.Warnings, warning => warning.Code == WarningCode.ApprovalDisabled);
    private sealed class DisabledApprovalState : IDisposable
    {
        private readonly string settingsPath;
        internal DisabledApprovalState()
        {
            var root = Path.Combine(Path.GetTempPath(), "tia-foundation-device-approval-" + Guid.NewGuid().ToString("N"));
            var config = Path.Combine(root, "config"); Directory.CreateDirectory(config);
            settingsPath = Path.Combine(config, "approval.settings");
            File.WriteAllText(settingsPath, "enabled=false\ntimeoutSeconds=120\n", new UTF8Encoding(false));
            Assert.False(ApprovalSettings.Load(settingsPath).Enabled);
        }
        internal Func<ApprovalSettings> Load => () => ApprovalSettings.Load(settingsPath);
        public void Dispose()
        {
            try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(settingsPath)!)!, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
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
        var worker = new CandidateWorkerFixture(); var tool = Tool(worker, safe); var props = tool.ProtocolTool.InputSchema.GetProperty("properties");
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
        var worker = new CandidateWorkerFixture(); var result = Body(await Tool(worker, true).InvokeAsync(Request(json)));
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code); Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CandidateKeepsCorrelationAndPoisonedWorkerOutcome(bool corruptReadback)
    {
        using var approval = new DisabledApprovalState();
        var worker = new CandidateWorkerFixture(); var tool = Tool(worker, true, approvalSettings: approval.Load);
        var args = new JsonObject { ["typeIdentifier"] = CandidateWorkerFixture.Identifier, ["deviceName"] = "PLC_2", ["family"] = "S7-1500" };
        var preview = Body(await tool.InvokeAsync(Request(args.ToJsonString()))); Assert.True(preview.Ok); Assert.Equal(0, worker.Device.Creates);
        args["mode"] = "apply"; args["confirm"] = true; args["expectedPlanHash"] = preview.Data!.Value.GetProperty("plan").GetProperty("hash").GetString(); args["expectedProjectFile"] = @"C:\Test.ap19";
        worker.Device.Fault = corruptReadback ? "" : "during-after"; worker.CorruptReadback = corruptReadback;
        var result = Body(await tool.InvokeAsync(Request(args.ToJsonString())));
        AssertApprovalDisabled(result);
        Assert.Equal("CreateHardwareDeviceCandidate", worker.Operation); Assert.Equal(1, worker.Device.Creates);
        Assert.Equal(Outcome.Unknown, result.Meta.Outcome); Assert.True(result.Meta.RequiresSessionReset); Assert.False(string.IsNullOrWhiteSpace(result.Meta.RequestId));
        Assert.Equal(corruptReadback ? "unavailable" : "checked", result.Data!.Value.GetProperty("residueCheck").GetProperty("status").GetString());
        Assert.True(worker.Outcome.Poisoned); Assert.Throws<InvalidOperationException>(() => worker.Outcome.RequireUsable());
        int calls = worker.Calls; var replay = Body(await tool.InvokeAsync(Request(args.ToJsonString())));
        AssertApprovalDisabled(replay);
        Assert.Equal(ErrorCode.SessionResetRequired, replay.Error!.Code); Assert.Equal(calls, worker.Calls);

    }

    [Theory]
    [InlineData("", true, false)] [InlineData("before", false, false)]
    [InlineData("during-before", false, true)] [InlineData("during-after", false, true)]
    [InlineData("after", false, true)] [InlineData("identity-after", false, true)] [InlineData("wrong-parent", false, true)]
    public async Task RemoteBoundaryRetainsTheNativeFaultMatrix(string fault, bool ok, bool unknown)
    {
        using var approval = new DisabledApprovalState();
        var worker = new CandidateWorkerFixture(); var tool = Tool(worker, true, approvalSettings: approval.Load);
        var args = new JsonObject { ["typeIdentifier"] = CandidateWorkerFixture.Identifier, ["deviceName"] = "PLC_2", ["family"] = "S7-1500" };
        var preview = Body(await tool.InvokeAsync(Request(args.ToJsonString()))); Assert.True(preview.Ok);
        args["mode"] = "apply"; args["confirm"] = true;
        args["expectedPlanHash"] = preview.Data!.Value.GetProperty("plan").GetProperty("hash").GetString(); args["expectedProjectFile"] = @"C:\Test.ap19";
        worker.Device.Fault = fault;
        var result = Body(await tool.InvokeAsync(Request(args.ToJsonString())));
        AssertApprovalDisabled(result);
        Assert.Equal(ok, result.Ok); Assert.Equal(unknown, result.Meta.RequiresSessionReset);
        Assert.Equal(fault == "before" ? 0 : 1, worker.Device.Creates);
        Assert.Equal(unknown, worker.Outcome.Poisoned);
        if (unknown) Assert.Equal(Outcome.Unknown, result.Meta.Outcome);
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
