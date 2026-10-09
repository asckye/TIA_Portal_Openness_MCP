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
        => new(new GenerationTool(name, release, () => store), release, null, () => new ApprovalSettings(true, 1),
            (pending, _, _) => { observe?.Invoke(pending); return Task.FromResult(new ApprovalOutcome(pending, false, refusal)); });
    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject arguments)
        => new(DispatchProxy.Create<IMcpServer, ServerProxy>()) { Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };
    private static JsonObject Identity() => new() { ["packageId"] = "tiamcp.basic", ["version"] = "1.0.0" };
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(Releases))]
    public async Task EveryToolAdmitsOfflineCallsAndRefusesInvalidFieldsBeforeApproval(string release)
    {
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
            Assert.True((bool?)result.StructuredContent?["ok"], result.Content.OfType<TextContentBlock>().First().Text);
            Assert.Equal(release, (string?)result.StructuredContent?["meta"]?["releaseKey"]);
            if (name == "ManageMachineDescription")
            {
                var usage = new FoundationV4Tool(new ToolUsageTool(release, () => new[] { tool }), release);
                var described = await usage.InvokeAsync(Request("GetToolUsage", new JsonObject { ["toolName"] = name }));
                var example = described.StructuredContent!["data"]!["example"]!["request"]!["params"]!["arguments"]!.AsObject();
                var admitted = await tool.InvokeAsync(Request(name, (JsonObject)example.DeepClone()));
                Assert.True((bool?)admitted.StructuredContent?["ok"], admitted.Content.OfType<TextContentBlock>().First().Text);
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
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(1, waits); Assert.False(Directory.Exists(Path.Combine(root, "csv")));
    }
}
