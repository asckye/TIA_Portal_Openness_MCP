using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using Xunit;

public sealed class FoundationV4ContractsTests
{
    private static RequestContext<CallToolRequestParams> Request(string name, string json = "{}") =>
        new(DispatchProxy.Create<IMcpServer, ServerProxy>()) { Params = new() { Name = name,
            Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
    private static JsonObject Body(CallToolResult result)
    {
        var text = ((TextContentBlock)result.Content.Single()).Text;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), result.StructuredContent));
        var envelope = V4Json.Deserialize<Envelope>(text);
        Assert.Equal(!envelope.Ok, result.IsError);
        return JsonNode.Parse(text)!.AsObject();
    }
    private static McpServerTool Tool(string name, FakeWorker? worker = null, string release = "19") =>
        LegacyHostToolRegistry.Create(worker ?? new FakeWorker(), release, false).Single(t => t.ProtocolTool.Name == name);

    [Theory]
    [InlineData("14sp1", 188)] [InlineData("15.1", 189)] [InlineData("16", 191)]
    [InlineData("17", 191)] [InlineData("18", 191)] [InlineData("19", 201)]
    public async Task EveryRegisteredEntryHasOneNameTypedSchemaAndV4Rejection(string release, int count)
    {
        var worker = new FakeWorker();
        var roster = LegacyHostToolRegistry.Create(worker, release, false);
        Assert.Equal(count, roster.Count);
        Assert.Equal(count, roster.Select(t => t.ProtocolTool.Name).Distinct().Count());
        Assert.DoesNotContain(roster, t => FoundationV4Tool.Names.ContainsKey(t.ProtocolTool.Name));
        Assert.Contains(roster, t => t.ProtocolTool.Name == "CallTool");
        foreach (var tool in roster)
        {
            if (TiaMcp.Adapters.Contracts.PortedFamilies.Contains(tool.ProtocolTool.Name)) Assert.Null(tool.ProtocolTool.OutputSchema);
            else Assert.NotNull(tool.ProtocolTool.OutputSchema);
            Assert.DoesNotContain(tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject(), p => p.Name.EndsWith("Json"));
            var result = Body(await tool.InvokeAsync(Request(tool.ProtocolTool.Name, "{\"__invalid\":true}")));
            Assert.Equal(4, (int)result["schemaVersion"]!);
            Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]!["code"]);
            Assert.Equal("not-started", (string?)result["meta"]!["execution"]);
            Assert.Equal(release, (string?)result["meta"]!["releaseKey"]);
            Assert.Equal(tool.ProtocolTool.Name, (string?)result["meta"]!["tool"]);
        }
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("{\"udt\":\"{}\",\"outputReleaseKey\":\"19\"}")]
    [InlineData("{\"udtJson\":\"{}\",\"outputReleaseKey\":\"19\"}")]
    [InlineData("{\"udt\":null,\"outputReleaseKey\":\"19\"}")]
    [InlineData("{\"udt\":{\"Name\":\"X\",\"members\":[]},\"outputReleaseKey\":\"19\"}")]
    [InlineData("{\"udt\":{\"name\":\"X\",\"name\":\"Y\",\"members\":[]},\"outputReleaseKey\":\"19\"}")]
    [InlineData("{\"udt\":{\"name\":\"X\",\"members\":[],\"unknown\":true},\"outputReleaseKey\":\"19\"}")]
    public async Task TypedConstructionRejectsLegacyAndAmbiguousInputs(string json)
    {
        var worker = new FakeWorker();
        var result = Body(await Tool("BuildPlcUdt", worker).InvokeAsync(Request("BuildPlcUdt", json)));
        Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]!["code"]);
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("A borrowed project/session is already open and bound; OpenProject cannot replace it.")]
    [InlineData("A project/session is already open and bound; close it explicitly before opening another.")]
    public void OpenProjectKeepsWorkerRefusalReason(string reason)
    {
        var result=Body(FoundationV4Result.Failure("18","OpenProject","fixture",true,true,null,
            new WorkerOperationException(reason,-32603,"rejected-before-operation","{\"exceptionType\":\"AdapterPreconditionException\",\"isArgument\":false}")));
        Assert.Equal("PRECONDITION_FAILED",(string?)result["error"]?["code"]);Assert.Equal(reason,(string?)result["error"]?["message"]);
        Assert.Equal("not-started",(string?)result["meta"]?["execution"]);Assert.False((bool?)result["meta"]?["requiresSessionReset"]);
    }
    [Fact]
    public void WorkerIOExceptionCarriesSanitizedBoundedMessage()
    {
        string text=@"Recovery export refused at C:\Users\Private\P.ap18; token=private-secret "+new string('x',6000);
        var result=Body(FoundationV4Result.Failure("18","ImportPlcBlocksFromDirectory","fixture",true,true,null,
            new WorkerOperationException(text,-32603,"unknown","{\"exceptionType\":\"IOException\"}")));
        string diagnostic=(string)result["error"]!["details"]!["evidence"]!["workerMessage"]!;
        Assert.Contains("Recovery export refused",diagnostic);Assert.Contains("<path>",diagnostic);Assert.True(diagnostic.Length<=4096);
        Assert.DoesNotContain("Private",diagnostic);Assert.DoesNotContain("private-secret",diagnostic);
        Assert.Equal("OUTCOME_UNKNOWN",(string?)result["error"]?["code"]);Assert.True((bool?)result["meta"]?["requiresSessionReset"]);
    }

    [Fact]
    public async Task CandidateKeepsOutputReleaseAndValidationEvidence()
    {
        var result = Body(await Tool("BuildPlcUdt").InvokeAsync(Request("BuildPlcUdt",
            "{\"udt\":{\"name\":\"UDT_Status\",\"members\":[{\"name\":\"Ready\",\"datatype\":\"Bool\"}]},\"outputReleaseKey\":\"14sp1\"}")));
        Assert.True((bool)result["ok"]!);
        Assert.Equal("14sp1", (string?)result["data"]!["outputReleaseKey"]);
        Assert.False((bool)result["data"]!["schemaValidated"]!);
        Assert.False((bool)result["data"]!["importValidated"]!);
        Assert.Contains(result["meta"]!["warnings"]!.AsArray(), w => (string?)w!["code"] == "CANDIDATE_ONLY");
    }

    [Fact]
    public async Task LimitsRemainEnforcedAndPlannerUsesClosedTypedRows()
    {
        var args = new JsonObject { ["udt"] = new JsonObject { ["name"] = new string('x', 4097), ["members"] = new JsonArray() }, ["outputReleaseKey"] = "19" };
        var result = Body(await Tool("BuildPlcUdt").InvokeAsync(Request("BuildPlcUdt", args.ToJsonString())));
        Assert.Equal("LIMIT_EXCEEDED", (string?)result["error"]!["code"]);
        foreach (var rows in new[] { "[{\"Id\":\"X\"}]", "[{\"id\":\"X\",\"priority\":1.5}]", "[{\"id\":\"X\",\"dependencies\":[\"X\"]}]" })
            Assert.False((bool)Body(await Tool("PlanArtifactImportOrder").InvokeAsync(Request("PlanArtifactImportOrder", "{\"artifacts\":" + rows + "}")))["ok"]!);
        result = Body(await Tool("PlanArtifactImportOrder").InvokeAsync(Request("PlanArtifactImportOrder", "{\"artifacts\":[{\"id\":\"B\",\"dependencies\":[\"A\"]},{\"id\":\"A\"}]}")));
        Assert.Equal("[\"A\",\"B\"]", result["data"]!["order"]!.ToJsonString());
    }

    [Theory]
    [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")] [InlineData("17")] [InlineData("18")] [InlineData("19")]
    public async Task MaintainedExamplesFitEveryAdvertisedSchema(string release)
    {
        var roster = LegacyHostToolRegistry.Create(new FakeWorker(), release, false);
        var guide = roster.Single(t => t.ProtocolTool.Name == "GetToolUsage");
        foreach (var tool in roster)
        {
            var usage = Body(await guide.InvokeAsync(Request("GetToolUsage", JsonSerializer.Serialize(new { toolName = tool.ProtocolTool.Name }))));
            Assert.True((bool)usage["ok"]!, tool.ProtocolTool.Name);
            var args = JsonSerializer.SerializeToElement(usage["data"]!["example"]!["request"]!["params"]!["arguments"]);
            var validation = new InputContract<JsonElement>(new InputSchema(tool.ProtocolTool.InputSchema), new InputBudget()).Read(args, "arguments");
            Assert.True(validation.IsValid, tool.ProtocolTool.Name + ": " + V4Json.Serialize(validation.Error));
            if (tool.ProtocolTool.Name == "BuildAndImportPlcArtifact")
                Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)Body(await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args.GetRawText())))["error"]?["code"]);
            else if (new[] { "BuildPlcUdt", "BuildPlcGlobalDb", "BuildPlcTagTable", "BuildStructuredText", "BuildFlgNetCall", "BuildPlcFcBlock", "BuildPlcFbBlock", "BuildPlcLadFcBlock" }.Contains(tool.ProtocolTool.Name))
                Assert.True((bool)Body(await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args.GetRawText())))["ok"]!, tool.ProtocolTool.Name);
        }
    }

    [Theory]
    [InlineData("outcome-unknown", false, true, "unknown", "unknown")]
    [InlineData("failed", false, false, "failed", "completed")]
    [InlineData("completed", true, false, "succeeded", "completed")]
    public void ExternalSourceEvidenceIsNeverPromoted(string status, bool executed, bool reset, string outcome, string execution)
    {
        var definition = FoundationTools.Definitions.Single(d => d.Name == "GenerateBlocksFromExternalSource");
        var raw = new JsonObject { ["Status"] = status, ["Executed"] = executed, ["RequiresSessionReset"] = reset,
            ["ResultBasis"] = "inventory-observation-only; native API returns void", ["ObservedChanges"] = new JsonArray("observed"),
            ["Evidence"] = new JsonObject { ["ExactNativeKey"] = "retained" } };
        var result = Body(FoundationV4Result.Worker("14sp1", definition, "test-request", new JsonObject { ["dryRun"] = false }, raw, raw));
        Assert.Equal(outcome, (string?)result["meta"]!["outcome"]);
        Assert.Equal(execution, (string?)result["meta"]!["execution"]);
        Assert.Equal(reset, (bool)result["meta"]!["requiresSessionReset"]!);
        Assert.Null(result["data"]!["nativeResult"]);
        Assert.Equal("retained", (string?)result["data"]!["evidence"]!["ExactNativeKey"]);
        Assert.Equal("observed", (string?)result["data"]!["observation"]!["observedChanges"]![0]);
        Assert.Equal("current", (string?)result["meta"]!["behaviorPolicy"]);
    }

    [Fact]
    public void BatchRetainsKnownSuccessUnknownFailureAndNotExecutedInOrder()
    {
        var definition = FoundationTools.Definitions.Single(d => d.Name == "ImportBlocksFromDirectory");
        var raw = JsonNode.Parse("{\"Executed\":true,\"RequiresSessionReset\":true,\"Items\":[{\"RelativePath\":\"a.xml\",\"Status\":\"imported\",\"Attempted\":true},{\"RelativePath\":\"b.xml\",\"Status\":\"failed\",\"Attempted\":true,\"Failure\":\"native-outcome-uncertain\"},{\"RelativePath\":\"c.xml\",\"Status\":\"not-attempted\"}]}")!;
        var result = Body(FoundationV4Result.Worker("19", definition, "batch", new JsonObject { ["dryRun"] = false }, raw, raw));
        Assert.Equal("unknown", (string?)result["meta"]!["outcome"]);
        Assert.Equal(new[] { "succeeded", "unknown", "rejected-before-operation" }, result["data"]!["items"]!.AsArray().Select(x => (string?)x!["result"]!["meta"]!["outcome"]));
        raw["Items"]![1]!["Attempted"] = false; raw["Items"]![1]!["Failure"] = "target-recheck-failed";
        result = Body(FoundationV4Result.Worker("19", definition, "batch", new JsonObject { ["dryRun"] = false }, raw, raw));
        Assert.Equal("partial", (string?)result["meta"]!["outcome"]);
        Assert.Equal("PARTIAL_FAILURE", (string?)result["error"]!["code"]);
        raw["Items"]!.AsArray().Add(JsonNode.Parse("{\"RelativePath\":\"d.xml\",\"Status\":\"not-attempted\"}"));
        result = Body(FoundationV4Result.Worker("19", definition, "batch", new JsonObject { ["dryRun"] = false }, raw, raw));
        Assert.Equal(1, (int)result["data"]!["items"]![3]!["result"]!["error"]!["details"]!["causeIndex"]!);
        raw["Items"]!.AsArray().RemoveAt(0);
        result = Body(FoundationV4Result.Worker("19", definition, "batch", new JsonObject { ["dryRun"] = false }, raw, raw));
        Assert.Equal("rejected-before-operation", (string?)result["meta"]!["outcome"]);
    }

    [Fact]
    public void MissingMutationResultAndNestedCompilerErrorsDoNotSucceed()
    {
        var result = Body(FoundationV4Result.Failure("19", "ImportPlcBlock", "x", true, true, null));
        Assert.Equal("unknown", (string?)result["meta"]!["outcome"]);
        var definition = FoundationTools.Definitions.Single(d => d.Name == "CompileSoftware");
        var raw = JsonNode.Parse("{\"Executed\":true,\"ErrorCount\":0,\"State\":\"Success\",\"Errors\":[\"nested error\"]}")!;
        result = Body(FoundationV4Result.Worker("19", definition, "x", new JsonObject { ["dryRun"] = false }, raw, raw));
        Assert.Equal("failed", (string?)result["meta"]!["outcome"]);
        Assert.Equal("nested error", (string?)result["data"]!["errors"]![0]);
    }

    [Fact]
    public void WorkerFailuresRetainEvidenceAndDistinguishUnsentFromUnknown()
    {
        var error = new WorkerOperationException("private worker detail", -32603, "unknown", "{\"ExactNativeKey\":{\"nested\":[1,null]}}");
        var result = Body(FoundationV4Result.Failure("19", "ImportPlcBlock", "x", true, true, null, error));
        Assert.Equal("unknown", (string?)result["meta"]!["outcome"]);
        Assert.Equal("[1,null]", result["data"]!["evidence"]!["ExactNativeKey"]!["nested"]!.ToJsonString());
        Assert.Null(result["error"]!["details"]!["evidence"]!["workerMessage"]);
        Assert.DoesNotContain("private worker detail", result.ToJsonString());
        var read = Body(FoundationV4Result.Failure("19", "ListPlcBlocks", "x", true, false, null,
            new WorkerOperationException("Project tree is incomplete.", -32603, "read-failed")));
        Assert.Null(read["error"]!["details"]!["nativeMessage"]);
        var rejected = Body(FoundationV4Result.Failure("19", "ExportPlcBlocks", "x", true, true, null,
            new WorkerOperationException("Export directory must already exist.", -32602, "rejected-before-operation", "{\"parameter\":\"exportPath\",\"isArgument\":true,\"exceptionType\":\"AdapterPreconditionException\"}")));
        Assert.Equal("rejected-before-operation", (string?)rejected["meta"]!["outcome"]);
        Assert.Equal("Export directory must already exist.", (string?)rejected["error"]!["message"]);
        Assert.Equal("exportPath", (string?)rejected["error"]!["details"]!["parameter"]);
        Assert.Equal("not-started", (string?)rejected["meta"]!["execution"]);
        string privatePath = @"C:\Users\Private\project.ap19";
        string longMessage = "Failed at " + privatePath + "; token=private-secret " + new string('x', 6000);
        var unknown = Body(FoundationV4Result.Failure("19", "ImportPlcBlock", "x", true, true, null,
            new WorkerOperationException(longMessage, -32603, "unknown")));
        Assert.Null(unknown["error"]!["details"]!["evidence"]!["workerMessage"]);
        string safeMessage = unknown.ToJsonString();
        Assert.DoesNotContain(privatePath, safeMessage);
        Assert.DoesNotContain("private-secret", safeMessage);
        error.Data["foundationRequestSent"] = false;
        result = Body(FoundationV4Result.Failure("19", "ImportPlcBlock", "x", true, true, null, error));
        Assert.Equal("not-started", (string?)result["meta"]!["execution"]);
        error.Data["foundationSessionPoisoned"] = true;
        result = Body(FoundationV4Result.Failure("19", "ImportPlcBlock", "x", true, true, null, error));
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)result["error"]!["code"]);
        Assert.True((bool)result["meta"]!["requiresSessionReset"]!);
        result = Body(FoundationV4Result.HostFailure("19", "GetToolUsage", "x"));
        Assert.Equal("INTERNAL_ERROR", (string?)result["error"]!["code"]);
        Assert.Equal("not-applicable", (string?)result["meta"]!["behaviorPolicy"]);
    }

    [Fact]
    public async Task UsagePagingAndFoundationSequenceUseV4Contracts()
    {
        var guide = Tool("GetToolUsage");
        var result = Body(await guide.InvokeAsync(Request("GetToolUsage", "{\"offset\":0,\"limit\":1}")));
        Assert.Equal(1, (int)result["meta"]!["paging"]!["nextOffset"]!);
        Assert.Null(result["data"]!["nextOffset"]);
        result = Body(await guide.InvokeAsync(Request("GetToolUsage", "{\"exampleKind\":\"sequence\"}")));
        Assert.Contains(result["data"]!["examples"]!.AsArray(), e => (string?)e!["id"] == "sequence/plc-scl-block-foundation" && (bool)e["available"]!);
        result = Body(await guide.InvokeAsync(Request("GetToolUsage", "{\"exampleId\":\"sequence/plc-scl-block-foundation\"}")));
        var steps = result["data"]!["examples"]![0]!["steps"]!.AsArray();
        Assert.Equal("GetSessionState", (string?)steps[0]!["tool"]);
        Assert.Contains(steps, s => (string?)s!["tool"] == "CompilePlcDiagnostics");
        Assert.DoesNotContain(steps, s => FoundationV4Tool.Names.ContainsKey((string)s!["tool"]!));
    }
}
