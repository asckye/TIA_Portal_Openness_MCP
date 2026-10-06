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

public sealed class PlcImportBoundaryTests
{
    private static RequestContext<CallToolRequestParams> Request(JsonObject args) => new(DispatchProxy.Create<IMcpServer, DeviceCreationBoundaryTests.ServerProxy>())
    { Params = new() { Name = "ImportPlcBlock", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) } };
    private static FoundationV4Tool Tool(CandidateWorkerFixture worker, string name, string release, bool safe) => new(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == name), worker), release,
        family => family == "P6-IMPORT" && safe ? BehaviorPolicy.SafeV4 : BehaviorPolicy.Current);
    private static void AssertApprovalDisabled(Envelope envelope)
        => Assert.Single(envelope.Meta.Warnings, warning => warning.Code == WarningCode.ApprovalDisabled);
    private sealed class DisabledApprovalState : IDisposable
    {
        private readonly string settingsPath;
        internal DisabledApprovalState()
        {
            var root = Path.Combine(Path.GetTempPath(), "tia-foundation-import-approval-" + Guid.NewGuid().ToString("N"));
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
        string text = ((TextContentBlock)result.Content.Single()).Text; Assert.True(JsonNode.DeepEquals(result.StructuredContent, JsonNode.Parse(text)));
        var envelope = V4Json.Deserialize<Envelope>(text); Assert.Equal(!envelope.Ok, result.IsError); return envelope;
    }
    public static IEnumerable<object[]> States()
    {
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19" })
            foreach (string name in new[] { "ImportBlock", "ImportType", "ImportPlcTagTable", "ImportBlocksFromDirectory", "ImportPlcProgramFromDirectory" })
                foreach (bool safe in new[] { false, true }) yield return new object[] { release, name, safe };
    }
    [Theory, MemberData(nameof(States))]
    public async Task EveryFoundationEntrySelectsOneSchemaAndRejectsOtherStateBeforeWorker(string release, string name, bool safe)
    {
        var worker = new CandidateWorkerFixture(); var tool = Tool(worker, name, release, safe); var schema = tool.ProtocolTool.InputSchema;
        Assert.Equal(safe, schema.GetProperty("properties").TryGetProperty("mode", out _));
        Assert.Equal(!safe, schema.GetProperty("properties").TryGetProperty("dryRun", out _));
        var args = new JsonObject(); foreach (var key in schema.GetProperty("required").EnumerateArray()) args[key.GetString()!] = key.GetString() == "importOrder" ? new JsonArray("a.xml") : JsonValue.Create("example");
        args[safe ? "dryRun" : "mode"] = safe ? JsonValue.Create(true) : JsonValue.Create("preview");
        var result = Body(await tool.InvokeAsync(Request(args))); Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
        Assert.Equal(safe ? BehaviorPolicy.SafeV4 : BehaviorPolicy.Current, result.Meta.BehaviorPolicy); Assert.Equal(0, worker.Calls);
    }
    [Theory]
    [InlineData("ImportBlock", "blockGroupPath")] [InlineData("ImportType", "typeGroupPath")] [InlineData("ImportPlcTagTable", "tagFolderPath")]
    public async Task SingleImportMapsOnlyToNewCandidateOperation(string entry, string group)
    {
        var worker = new CandidateWorkerFixture { MissingProject = true }; var args = new JsonObject { ["softwarePath"] = "devices/PLC/CPU", [entry == "ImportPlcTagTable" ? "folderPath" : "groupPath"] = "Group", ["importPath"] = @"C:\input.xml" };
        var result = Body(await Tool(worker, entry, "19", true).InvokeAsync(Request(args)));
        Assert.Equal(ErrorCode.ProjectNotBound, result.Error!.Code); Assert.Equal("ImportPlcCandidate", worker.Operation); Assert.Equal(1, worker.Calls);
        Assert.Equal("Group", (string?)worker.Arguments!["candidate"]!["Request"]![char.ToUpperInvariant(group[0]) + group.Substring(1)]); Assert.Equal(@"C:\input.xml", (string?)worker.Arguments["candidate"]!["Request"]!["InputPath"]);
        Assert.Equal(1, (int?)worker.Arguments["candidate"]!["Request"]!["MaxItems"]); Assert.False(string.IsNullOrWhiteSpace(result.Meta.RequestId));
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public async Task UnknownAndMalformedNativeReplyPreserveUncertaintyAndPoisonOutcome(bool malformed, bool corruptReadback)
    {
        using var approval = new DisabledApprovalState();
        var worker = new CandidateWorkerFixture();
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "ImportBlock"), worker), "19",
            family => family == "P6-IMPORT" ? BehaviorPolicy.SafeV4 : BehaviorPolicy.Current, approval.Load);
        var args = new JsonObject { ["softwarePath"] = "PLC", ["groupPath"] = "", ["importPath"] = worker.InputPath };
        var preview = Body(await tool.InvokeAsync(Request(args))); Assert.True(preview.Ok); Assert.Equal(0, worker.Import.Calls);
        args["mode"] = "apply"; args["confirm"] = true; args["expectedPlanHash"] = preview.Data!.Value.GetProperty("plan").GetProperty("hash").GetString(); args["expectedProjectFile"] = @"C:\Test.ap19";
        worker.Import.Fault = malformed || corruptReadback ? "" : "during-after";
        worker.Malformed = malformed; worker.CorruptReadback = corruptReadback;
        var result = Body(await tool.InvokeAsync(Request(args)));
        AssertApprovalDisabled(result);
        Assert.Equal(Outcome.Unknown, result.Meta.Outcome); Assert.True(result.Meta.RequiresSessionReset); Assert.Equal(1, worker.Import.Calls);
        Assert.NotNull(result.Error!.Details); Assert.False(string.IsNullOrWhiteSpace(result.Meta.RequestId));
        Assert.True(worker.Outcome.Poisoned); Assert.Throws<InvalidOperationException>(() => worker.Outcome.RequireUsable());
        int calls = worker.Calls; var replay = Body(await tool.InvokeAsync(Request(args))); Assert.Equal(ErrorCode.SessionResetRequired, replay.Error!.Code); Assert.Equal(calls, worker.Calls);
        AssertApprovalDisabled(replay);

    }
    [Theory]
    [InlineData("", true, false)] [InlineData("before", false, false)]
    [InlineData("during-before", false, true)] [InlineData("during-after", false, true)]
    [InlineData("after", false, true)] [InlineData("inventory-after", false, true)]
    [InlineData("identity-after", false, true)] [InlineData("wrong-parent", false, true)] [InlineData("content-mismatch", false, true)]
    public async Task RemoteBoundaryRetainsTheNativeFaultMatrix(string fault, bool ok, bool unknown)
    {
        using var approval = new DisabledApprovalState();
        var worker = new CandidateWorkerFixture();
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "ImportBlock"), worker), "19",
            family => family == "P6-IMPORT" ? BehaviorPolicy.SafeV4 : BehaviorPolicy.Current, approval.Load);
        var args = new JsonObject { ["softwarePath"] = "PLC", ["groupPath"] = "", ["importPath"] = worker.InputPath };
        var preview = Body(await tool.InvokeAsync(Request(args))); Assert.True(preview.Ok);
        args["mode"] = "apply"; args["confirm"] = true;
        args["expectedPlanHash"] = preview.Data!.Value.GetProperty("plan").GetProperty("hash").GetString(); args["expectedProjectFile"] = @"C:\Test.ap19";
        worker.Import.Fault = fault;
        var result = Body(await tool.InvokeAsync(Request(args)));
        AssertApprovalDisabled(result);
        Assert.Equal(ok, result.Ok); Assert.Equal(unknown, result.Meta.RequiresSessionReset);
        Assert.Equal(fault == "before" ? 0 : 1, worker.Import.Calls); Assert.Equal(unknown, worker.Outcome.Poisoned);
        if (unknown) Assert.Equal(Outcome.Unknown, result.Meta.Outcome);
    }

    [Fact]
    public void WorkerPreviewClassificationDoesNotChangeExistingOperations()
    {
        Assert.DoesNotContain("ImportPlcCandidate", TiaMcp.PlcWorker.WorkerOperations.Names);
        Assert.True(TiaMcp.PlcWorker.WorkerOperations.IsImportPreview("ImportPlcCandidate", null));
        Assert.True(TiaMcp.PlcWorker.WorkerOperations.IsImportPreview("ImportPlcCandidate", "preview"));
        Assert.False(TiaMcp.PlcWorker.WorkerOperations.IsImportPreview("ImportPlcCandidate", "apply"));
        Assert.False(TiaMcp.PlcWorker.WorkerOperations.IsImportPreview("ImportBlocks", "preview"));
    }
}
