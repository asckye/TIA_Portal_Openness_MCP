extern alias toolpipeline;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.Generation;
using TiaOpenness.Shared;
using Xunit;

public sealed class GenerationToolTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "generation-tools", Guid.NewGuid().ToString("N"));
    private readonly StandardPackageStore store;
    public GenerationToolTests()
    {
        Directory.CreateDirectory(root);
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !Directory.Exists(Path.Combine(repository.FullName, "templates", "standards"))) repository = repository.Parent;
        store = new StandardPackageStore(Path.Combine(repository!.FullName, "templates", "standards"), Path.Combine(root, "data", "standards"));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private FoundationV4Tool Tool(string name, string release, Action<PendingApproval>? observe = null, string? refusal = null)
        => new(new GenerationTool(name, release, () => store), release, () => new ApprovalSettings(true, 1),
            (pending, _, _) => { observe?.Invoke(pending); return Task.FromResult(new ApprovalOutcome(pending, false, refusal)); });
    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject arguments)
        => new(DispatchProxy.Create<IMcpServer, ServerProxy>()) { Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };
    [Theory]
    [InlineData("unknown", "/machine/devices/0", "Unknown property.")]
    [InlineData("station", "/machine/devices/0/station", "Select one of the declared PLC stations.")]
    [InlineData("topology", "/machine/topology/0/children/0/kind", "Required property is missing.")]
    [InlineData("locale", "/machine/machine/name", "Unknown property.")]
    public async Task Planning_reports_declared_machine_paths_and_redacts_free_keys(string invalid, string pointer, string message)
    {
        const string name = "PlanProjectGeneration";
        var args = Identity();
        var machine = JsonNode.Parse(store.Load("tiamcp.basic", "1.0.0").ReadFile("examples/machine.json"))!.AsObject();
        args["machine"] = machine;
        if (invalid == "unknown")
        {
            machine["devices"]![0]!["privateField"] = "privateValue";
            machine["devices"]![0]!["nested/key~"] = new JsonObject { ["innerKey"] = "innerValue" };
        }
        else if (invalid == "topology") machine["topology"]![0]!["children"]![0]!.AsObject().Remove("kind");
        else if (invalid == "locale") machine["machine"]!["name"]!["privateField"] = "privateValue";
        else machine["devices"]![0]!["station"] = "MissingStation";
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var context = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
            new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(worker) { Catalog = new PortedPortalAdmissionTests.Catalog("20") });
        var session = new toolpipeline::TiaMcpServer.ModelContextProtocol.GenerationExecutionSession(() => root,
            (_, _, _) => throw new InvalidOperationException("Validation must finish before project reads."));
        var tool = new FoundationV4Tool(new GenerationTool(name, "20", () => store, session), "20");
        using var diagnostics = new GenerationFailureDiagnostics();
        var response = await tool.InvokeAsync(Request(name, args));
        var result = response.StructuredContent!;
        Assert.True((string?)result["error"]?["code"] == "INVALID_ARGUMENT", diagnostics.Failure(name, response));
        Assert.Equal(pointer, (string?)result["error"]?["details"]?["parameter"]);
        Assert.Equal(message, (string?)result["error"]?["message"]);
        Assert.True(JsonNode.DeepEquals(result, JsonNode.Parse(((TextContentBlock)response.Content.Single()).Text)));
        Assert.DoesNotContain("privateField", result.ToJsonString());
        Assert.DoesNotContain("privateValue", result.ToJsonString());
        Assert.DoesNotContain("nested", result.ToJsonString());
        Assert.DoesNotContain("innerKey", result.ToJsonString());
        Assert.DoesNotContain("innerValue", result.ToJsonString());
        var issues = result["data"]!["validationErrors"]!.AsArray();
        Assert.Equal(invalid == "unknown" ? 2 : 1, issues.Count);
        Assert.All(issues, issue => {
            Assert.Equal(pointer, (string?)issue!["path"]);
            Assert.Equal(message, (string?)issue["message"]);
        });
    }

    [Fact]
    public async Task Planning_usage_preserves_the_generic_machine_argument_and_example()
    {
        const string name = "PlanProjectGeneration";
        var plan = Tool(name, "20");
        var usage = new FoundationV4Tool(new ToolUsageTool("20", () => new[] { plan }), "20");
        using var diagnostics = new GenerationFailureDiagnostics();
        var response = await usage.InvokeAsync(Request("GetToolUsage", new JsonObject { ["toolName"] = name }));
        Assert.True((bool?)response.StructuredContent?["ok"], diagnostics.Failure(name, response));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(plan.ProtocolTool.InputSchema.GetRawText()), response.StructuredContent!["data"]?["inputSchema"]));
        var schema = plan.ProtocolTool.InputSchema;
        Assert.False(schema.TryGetProperty("$defs", out _));
        var machine = schema.GetProperty("properties").GetProperty("machine");
        Assert.Equal("object", machine.GetProperty("type").GetString());
        Assert.False(machine.TryGetProperty("properties", out _));
        Assert.NotNull(response.StructuredContent!["data"]?["example"]?["request"]?["params"]?["arguments"]?["machine"]);
    }

    private static JsonObject Identity() => new() { ["packageId"] = "tiamcp.basic", ["version"] = "1.0.0" };
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(Releases))]
    public async Task EveryToolAdmitsOfflineCallsAndRefusesInvalidFieldsBeforeApproval(string release)
    {
        using var diagnostics = new GenerationFailureDiagnostics();
        var machine = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(store.Load("tiamcp.basic", "1.0.0").ReadFile("examples/machine.json")))!;
        machine["target"]!["release"] = release;
        foreach (var name in new[] { "ListStandardPackages", "ValidateStandardPackage", "DescribeStandardPackage", "ManageStandardPackage", "ManageMachineDescription" })
        {
            int waits = 0;
            var tool = Tool(name, release, _ => waits++);
            var args = name == "ListStandardPackages" ? new JsonObject() : Identity();
            if (name == "ManageStandardPackage") { args["action"] = "fork"; args["newId"] = "example.fork"; args["newVersion"] = "1.1.0"; }
            if (name == "ManageMachineDescription") { args["action"] = "validate"; args["machine"] = machine.DeepClone(); args["dryRun"] = false; }
            var result = await tool.InvokeAsync(Request(name, args));
            Assert.True((bool?)result.StructuredContent?["ok"], diagnostics.Failure(name, result));
            Assert.Equal(release, (string?)result.StructuredContent?["meta"]?["releaseKey"]);
            if (name == "ManageMachineDescription")
            {
                var usage = new FoundationV4Tool(new ToolUsageTool(release, () => new[] { tool }), release);
                var described = await usage.InvokeAsync(Request("GetToolUsage", new JsonObject { ["toolName"] = name }));
                var example = described.StructuredContent!["data"]!["example"]!["request"]!["params"]!["arguments"]!.AsObject();
                var admitted = await tool.InvokeAsync(Request(name, (JsonObject)example.DeepClone()));
                Assert.True((bool?)admitted.StructuredContent?["ok"], diagnostics.Failure(name + " usage example", admitted));
            }
            args["unknownField"] = true;
            result = await tool.InvokeAsync(Request(name, args));
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal(0, waits);
        }
        Assert.False(Directory.Exists(store.UserRoot));
    }
    [Theory]
    [InlineData("granted")]
    [InlineData("denied")]
    [InlineData("timeout")]
    public async Task PackageWriteUsesExistingApprovalAndReviewedPlan(string decision)
    {
        int waits = 0;
        var tool = Tool("ManageStandardPackage", "19", p => { waits++; Assert.Equal("ManageStandardPackage", p.Tool); }, decision == "granted" ? null : decision);
        var args = Identity(); args["action"] = "fork"; args["newId"] = "example.fork"; args["newVersion"] = "1.1.0";
        var preview = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args));
        Assert.True((bool?)preview.StructuredContent?["ok"]);
        args["dryRun"] = false; args["expectedPlanHash"] = preview.StructuredContent!["data"]!["planHash"]!.DeepClone();
        var result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args));
        Assert.Equal(1, waits);
        Assert.Equal(decision == "granted", (bool?)result.StructuredContent?["ok"]);
        Assert.Equal(decision == "granted", Directory.Exists(store.UserRoot));
    }
    [Fact]
    public async Task StalePlanAndRepositoryRemoveRefuseBeforeApproval()
    {
        int waits = 0; var tool = Tool("ManageStandardPackage", "21", _ => waits++);
        var args = Identity(); args["action"] = "fork"; args["newId"] = "example.fork"; args["newVersion"] = "1.0.0";
        args["dryRun"] = false; args["expectedPlanHash"] = new string('a', 64);
        var result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args));
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        args.Remove("newId"); args.Remove("newVersion"); args["action"] = "remove";
        result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args));
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(0, waits); Assert.False(Directory.Exists(store.UserRoot));
    }
    [Fact]
    public async Task MachineFileWritePreviewsAndRequiresApproval()
    {
        int waits = 0; var tool = Tool("ManageMachineDescription", "14sp1", _ => waits++, "denied");
        var args = Identity(); args["action"] = "exportTemplate"; args["format"] = "csv"; args["outputPath"] = Path.Combine(root, "csv");
        var preview = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args));
        Assert.True((bool?)preview.StructuredContent?["ok"]);
        args["dryRun"] = false; args["expectedPlanHash"] = preview.StructuredContent!["data"]!["planHash"]!.DeepClone();
        var result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args));
        Assert.True((string?)result.StructuredContent?["error"]?["code"] == "CONFIRMATION_REQUIRED", result.StructuredContent?.ToJsonString());
        Assert.Equal(1, waits); Assert.False(Directory.Exists(Path.Combine(root, "csv")));
    }
}
