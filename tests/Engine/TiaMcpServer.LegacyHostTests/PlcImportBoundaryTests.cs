using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;
using TiaMcp.Logic.V4;
using Xunit;

public sealed class PlcImportBoundaryTests
{
    private sealed class Worker : IFoundationWorker
    {
        internal int Calls;
        internal string? Operation;
        internal JsonObject? Arguments;
        internal bool Unknown, Malformed;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Calls++; Operation = operation; Arguments = arguments;
            var error = Unknown ? new Error("Unknown import.", new OutcomeUnknownDetails("native-import", new Dictionary<string, JsonElement>()))
                : new Error("No project bound.", new ProjectNotBoundDetails());
            var data = Unknown ? new JsonObject { ["residueCheck"] = new JsonObject { ["status"] = "unavailable", ["reason"] = "injected-failure" } } : null;
            var result = PlcImportSession.Result("19", (string)arguments["tool"]!, (string)arguments["requestId"]!, data, error,
                Unknown ? Outcome.Unknown : Outcome.RejectedBeforeOperation, Unknown ? Execution.Unknown : Execution.NotStarted);
            var body = JsonNode.Parse(V4Json.Serialize(result)); if (Malformed) body!["meta"]!["tool"] = "WrongTool";
            return Task.FromResult(body);
        }
        public void Dispose() { }
    }
    private static RequestContext<CallToolRequestParams> Request(JsonObject args) => new(DispatchProxy.Create<IMcpServer, DeviceCreationBoundaryTests.ServerProxy>())
    { Params = new() { Name = "ImportPlcBlock", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) } };
    private static FoundationV4Tool Tool(Worker worker, string name, string release, bool safe) => new(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == name), worker), release,
        family => family == "P6-IMPORT" && safe ? BehaviorPolicy.SafeV4 : BehaviorPolicy.Current);
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
        var worker = new Worker(); var tool = Tool(worker, name, release, safe); var schema = tool.ProtocolTool.InputSchema;
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
        var worker = new Worker(); var args = new JsonObject { ["softwarePath"] = "devices/PLC/CPU", [entry == "ImportPlcTagTable" ? "folderPath" : "groupPath"] = "Group", ["importPath"] = @"C:\input.xml" };
        var result = Body(await Tool(worker, entry, "19", true).InvokeAsync(Request(args)));
        Assert.Equal(ErrorCode.ProjectNotBound, result.Error!.Code); Assert.Equal("ImportPlcCandidate", worker.Operation); Assert.Equal(1, worker.Calls);
        Assert.Equal("Group", (string?)worker.Arguments![group]); Assert.Equal(@"C:\input.xml", (string?)worker.Arguments["inputPath"]);
        Assert.Equal(1, (int?)worker.Arguments["maxItems"]); Assert.Equal((string?)worker.Arguments["requestId"], result.Meta.RequestId);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnknownAndMalformedNativeReplyPreserveUncertaintyAndPoisonOutcome(bool malformed)
    {
        var worker = new Worker { Unknown = true, Malformed = malformed };
        var args = new JsonObject { ["softwarePath"] = "PLC", ["groupPath"] = "", ["importPath"] = @"C:\input.xml", ["mode"] = "apply", ["confirm"] = true,
            ["expectedPlanHash"] = new string('a', 64), ["expectedProjectFile"] = @"C:\Test.ap19" };
        var result = Body(await Tool(worker, "ImportBlock", "19", true).InvokeAsync(Request(args)));
        Assert.Equal(Outcome.Unknown, result.Meta.Outcome); Assert.True(result.Meta.RequiresSessionReset); Assert.Equal(1, worker.Calls);
        Assert.NotNull(result.Error!.Details); Assert.Equal((string?)worker.Arguments!["requestId"], result.Meta.RequestId);
        var outcome = new WorkerOutcomeState(); outcome.AcceptResult(worker.Operation!, worker.Arguments, JsonNode.Parse(V4Json.Serialize(result)));
        Assert.True(outcome.Poisoned); Assert.Throws<InvalidOperationException>(() => outcome.RequireUsable());
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
