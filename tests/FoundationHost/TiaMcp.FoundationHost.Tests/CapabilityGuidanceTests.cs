extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Xunit;

public sealed class CapabilityGuidanceTests
{
    [Theory, InlineData(true, ""), InlineData(false, "Global")]
    public async Task V16_block_master_copy_refusal_precedes_worker_dispatch(bool preview, string library)
    {
        const string name = "CreateLibraryMasterCopy";
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, "16");
        var tool = PortedToolContract.Create(worker, "16").Single(item => item.ProtocolTool.Name == name);
        var args = new JsonObject { ["sourceKind"] = "block", ["sourcePath"] = "Group/Db", ["dryRun"] = preview, ["libraryName"] = library };
        var result = await tool.InvokeAsync(new(System.Reflection.DispatchProxy.Create<ModelContextProtocol.Server.IMcpServer,
            ApprovalPrecheckTests.ServerProxy>()) { Params = new() { Name = name, Arguments =
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) } });
        var body = result.StructuredContent!;
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Equal(0, worker.Calls);
        string? reason = ReleaseCapabilityQuery.Problem(name, "16", args);
        Assert.Equal(reason, (string?)body["error"]?["message"]);
        Assert.Contains("V16 crash protection", reason);
        Assert.Contains("Supported releases: V14 SP1, V15.1, V17, V18, V19, V20, V21", reason);
        Assert.DoesNotContain("Minimum release", reason);
        var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        var example = ToolUsageCatalog.Describe(name, "16", "plc-foundation", "", schema)["example"]!;
        Assert.Equal("type", (string?)example["request"]?["params"]?["arguments"]?["sourceKind"]);
        Assert.Contains("refuses PLC block", (string?)example["releaseNote"]);
    }

    [Fact]
    public void Drive_parameter_read_view_reports_the_same_limit_at_the_service_boundary()
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("19", worker,
            Array.Empty<ModelContextProtocol.Server.McpServerTool>());
        using var session = pipeline.EnterSession();
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.StartdriveService((_, _) => throw new InvalidOperationException("Unexpected call."),
            () => throw new InvalidOperationException("Unexpected write identity."));
        var result = new toolpipeline::TiaMcpServer.ModelContextProtocol.StartdriveTools(service)
            .ReadDriveParameters(new[] { "Drive" }, new[] { "Axis" }, driveObjectIndex: 0, source: "read");
        var body = result.StructuredContent!;
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Equal(ReleaseCapabilityQuery.Problem("GetDriveParameters", "19", new JsonObject { ["source"] = "read" }),
            (string?)body["error"]?["message"]);
        Assert.Contains("V20", (string?)body["error"]?["message"]);
    }

    [Fact]
    public void An_omitted_action_reports_the_default_from_the_registered_schema()
    {
        const string name = "ManageSyslogServers";
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, "19");
        var tool = PortedToolContract.Create(worker, "19").OfType<FoundationTool>().Single(item => item.ProtocolTool.Name == name);
        var result = tool.RefusePortedCapability(new Dictionary<string, JsonElement>());
        var body = result!.StructuredContent!;
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
        Assert.Contains(name + ".read", (string?)body["error"]?["message"]);
        Assert.Contains("V20", (string?)body["error"]?["message"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
    }

    [Theory]
    [InlineData("ManageDeviceServiceObjects", "webApplications", "read", "DefaultWebPagesFeature requires V21")]
    [InlineData("ManagePlcUserGroup", "blocks", "rename", "group Name has no public setter")]
    public void Registered_call_and_usage_agree_on_the_action_and_family(string name, string family, string action, string reason)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, "20");
        var tool = PortedToolContract.Create(worker, "20").OfType<FoundationTool>().Single(item => item.ProtocolTool.Name == name);
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(new JsonObject {
            ["action"] = action, ["family"] = family, ["dryRun"] = true }.ToJsonString())!;
        var body = tool.RefusePortedCapability(args)!.StructuredContent!;
        var message = (string?)body["error"]?["message"];
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Contains(reason, message);
        Assert.Contains(name + "." + action, message);
        Assert.Contains("V20", message);
        var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        var usage = ToolUsageCatalog.Describe(name, "20", "plc-foundation", "", schema, operation: action);
        var variants = usage["operations"]!.AsArray().Single(row => (string?)row!["operation"] == action)!["variants"]!.AsArray();
        var unavailable = variants.Single(row => (string?)row!["context"]?["family"] == family);
        Assert.False((bool?)unavailable!["availableInRelease"]);
        Assert.Equal(message, (string?)unavailable["reason"]);
        if (name == "ManageDeviceServiceObjects")
            Assert.Throws<ArgumentException>(() => ToolUsageCatalog.Describe(name, "20", "plc-foundation", "", schema, operation: "list"));
    }
}
