extern alias enginehost;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;

public sealed class CompatibilityPreviewTests
{
    [Fact]
    public void MovePreviewExposesItsNativeHashAndBoundProjectToThePlanner()
    {
        const string project = "C:/Projects/original.ap21";
        string hash = new string('b', 64);
        var service = new enginehost::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((_, arguments) => {
            Assert.True((bool?)arguments["request"]?["DryRun"]);
            return new JsonObject { ["Message"] = "Move preview", ["Meta"] = new JsonObject {
                ["planHash"] = hash, ["dryRun"] = true, ["operationSuccess"] = true, ["mayHaveChanged"] = false } };
        }, () => true, () => project);
        var result = new enginehost::TiaMcpServer.ModelContextProtocol.PlcOrganisationTools(service)
            .MoveBlockToGroupV4("PLC", "Motor", "Target");
        Assert.True((bool?)result.StructuredContent?["ok"]);
        var projection = TiaMcp.Logic.Generation.StepReviewRules.Project(result.StructuredContent!["data"]!.AsObject());
        Assert.Equal(hash, (string?)projection["planHash"]);
        Assert.Equal(project, (string?)projection["projectFile"]);
    }

    [Fact]
    public void OldReleaseDevicePreviewBindsArgumentsAndProjectBeforeServiceAccess()
    {
        using var worker = new PlanWorker();
        using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        var tools = new enginehost::TiaMcpServer.ModelContextProtocol.HardwareDevicesTools(null!);
        var preview = tools.CreateDeviceV4("article", "version", "Device");
        Assert.True((bool?)preview.StructuredContent?["ok"]);
        string hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
        Assert.Matches("^[0-9a-f]{64}$", hash);
        var stale = tools.CreateDeviceV4("different", "version", "Device", dryRun: false, confirm: true,
            expectedPlanHash: hash, expectedProjectFile: "C:/Projects/original.ap21");
        Assert.Equal("PLAN_STALE", (string?)stale.StructuredContent?["error"]?["code"]);
        var wrong = tools.CreateDeviceV4("article", "version", "Device", dryRun: false, confirm: true,
            expectedPlanHash: hash, expectedProjectFile: "C:/Projects/other.ap21");
        Assert.Equal("IDENTITY_MISMATCH", (string?)wrong.StructuredContent?["error"]?["code"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.ap21")]
    [InlineData("C:")]
    [InlineData("C:relative.ap21")]
    public void GenericPreviewRejectsNonAbsoluteProjectBeforeDispatch(string project)
    {
        using var worker = new PlanWorker();
        using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        int calls = 0;
        CallToolResult Execute() { calls++; return Host.V4Result("CreateDevice", new JsonObject(), completed: true); }
        var args = new JsonObject { ["name"] = "Device" };
        var preview = Host.RunWritePreview("CreateDevice", true, args, Execute);
        var hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
        var result = Host.RunWritePreview("CreateDevice", false, args, Execute, hash, project);
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void GenericPreviewIsCanonicalAndRequiresReviewedHash()
    {
        using var worker = new PlanWorker();
        using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        int calls = 0;
        CallToolResult Execute() { calls++; return Host.V4Result("CreateDevice", new JsonObject(), completed: true); }
        var args = new JsonObject { ["name"] = "Device", ["version"] = "V1" };
        var reordered = new JsonObject { ["version"] = "V1", ["name"] = "Device" };
        var preview = Host.RunWritePreview("CreateDevice", true, args, Execute);
        string hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
        Assert.Equal(hash, (string?)Host.RunWritePreview("CreateDevice", true, reordered, Execute).StructuredContent?["data"]?["planHash"]);
        Assert.True(Host.RunWritePreview("CreateDevice", false, args, Execute, "", "C:/Projects/original.ap21").IsError);
        Assert.Equal(0, calls);
        Assert.True((bool?)Host.RunWritePreview("CreateDevice", false, args, Execute, hash, "c:/projects/ORIGINAL.ap21")
            .StructuredContent?["ok"]);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ForwardedApprovalBindsMappedArgumentsAndRemainsSingleUse()
    {
        var source = new JsonObject { ["dryRun"] = false, ["confirm"] = true, ["expectedPlanHash"] = new string('a', 64),
            ["expectedProjectFile"] = "C:/Project.ap21", ["orderNumber"] = "article" };
        var preview = source.DeepClone().AsObject(); preview["dryRun"] = true;
        var item = new JsonObject { ["tool"] = "CreateDevice", ["arguments"] = preview, ["canonicalPlanHash"] = new string('a', 64) };
        var pending = PendingApproval.Create("foundation", "21", "Batch", "{}", "{}", 120);
        pending.BatchPlan = new JsonObject { ["expectedProjectFile"] = "C:/Project.ap21", ["items"] = new JsonArray(item) };
        using var grant = GenerationStepApproval.EnterBatchItem(new ApprovalOutcome(pending, false, null), 0, "CreateDevice", source);
        var mapped = source.DeepClone().AsObject(); mapped.Remove("orderNumber"); mapped["preferredMlfb"] = "article";
        using var forward = GenerationStepApproval.EnterForwardedTool("CreateDevice", source, "CreateHardwareDevice", mapped);
        var canonical = PendingApproval.Create("foundation", "21", "CreateHardwareDevice", mapped.ToJsonString(), "{}", 120);
        Assert.Null(GenerationStepApproval.Match(canonical)!.Reason);
        Assert.Equal("denied", GenerationStepApproval.Match(canonical)!.Reason);
    }

    private sealed class PlanWorker : enginehost::TiaMcp.FoundationHost.IEngineWorker
    {
        internal JsonObject Identity = new() { ["identity"] = new JsonObject { ["processId"] = 123, ["projectPath"] = "C:/Projects/original.ap21" } };
        public bool Faulted => false;
        public JsonNode? Binding => Identity;
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new();
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new NotSupportedException();
        public Task<IDisposable> Acquire(CancellationToken token) => throw new NotSupportedException();
        public Task<enginehost::TiaMcp.FoundationHost
            .EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token) => throw new NotSupportedException();
        public void Dispose() { }
    }

}
