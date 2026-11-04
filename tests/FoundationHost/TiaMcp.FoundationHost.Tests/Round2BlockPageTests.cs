extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts.Paging;
using TiaMcp.FoundationHost;
using Pipeline = enginehost::TiaMcp.FoundationHost.EngineHostPipeline;
using Xunit;

[Collection(BundleSelectionCollection.Name)]
public sealed class Round2BlockPageTests
{
    [Fact]
    public async Task Generation_fixture_restores_the_host_context_before_paging_and_decoder_tests()
    {
        var previousRelease = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var context = new enginehost::TiaMcp.FoundationHost.EngineSessionContext(worker);
        using var scope = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(context);
        try
        {
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = "21";
            new StagedGenerationTests().FirstPreviewRefusalDoesNotConsumeAndCanRunAgain("17");
            await Default_native_pages_fit_and_keep_cursor_through_CallTool(false);
            await Default_native_pages_fit_and_keep_cursor_through_CallTool(true);
            await Missing_or_invalid_decoder_runtime_is_rejected_without_install_path(false);
            await Missing_or_invalid_decoder_runtime_is_rejected_without_install_path(true);
            Assert.Equal("21", enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey);
            Assert.Same(context, enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.CurrentOrNull);
        }
        finally { enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previousRelease; }
    }

    private sealed class Worker(JsonNode? malformed = null) : IFoundationWorker
    {
        internal int Limit;
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            Limit = (int)args["limit"]!;
            var paths = Enumerable.Range(0, 130).Select(i => "PLC/B" + i.ToString("D3")).ToArray();
            var page = PlcBlockPaging.Plan(paths, "fixture", Limit, (string)args["cursor"]!);
            return Task.FromResult(malformed?.DeepClone() ?? (JsonNode)new JsonObject {
                ["items"] = new JsonArray(paths.Skip(page.Offset).Take(Limit).Select(path => (JsonNode)new JsonObject {
                    ["path"] = path, ["name"] = path, ["description"] = new string('x', 2600) }).ToArray()),
                ["offset"] = page.Offset, ["total"] = page.Total, ["nextCursor"] = page.NextCursor });
        }
        public void Dispose() { }
    }

    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject args) =>
        new(DispatchProxy.Create<IMcpServer, ServerProxy>()) { Params = new() { Name = name,
            Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) } };

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Default_native_pages_fit_and_keep_cursor_through_CallTool(bool bridge)
    {
        using var worker = new Worker();
        JsonObject Ready() => new() { ["ready"] = true };
        var shared = LegacyHostToolRegistry.Create(worker, "21", false, readinessProvider: Ready, hostPorts: false);
        using var pipeline = new Pipeline("21", FoundationHostPorts.CreateServices(worker, "21", Ready, null), shared);
        var tools = pipeline.AllTools.ToDictionary(t => t.ProtocolTool.Name!);
        async Task<CallToolResult> Call(JsonObject args) => await tools[bridge ? "CallTool" : "ListPlcBlocks"].InvokeAsync(
            bridge ? Request("CallTool", new() { ["name"] = "ListPlcBlocks", ["arguments"] = args }) : Request("ListPlcBlocks", args));
        var result = await Call(new() { ["softwarePath"] = "PLC" });
        Assert.False(result.IsError);
        Assert.Equal(4, worker.Limit);
        Assert.True(((TextContentBlock)result.Content.Single()).Text.Length < 20000);
        Assert.Equal("cursor", (string?)result.StructuredContent!["meta"]!["paging"]!["mode"]);
        string cursor = (string)result.StructuredContent["meta"]!["paging"]!["nextCursor"]!;
        Assert.Null(result.StructuredContent["data"]!["continuation"]);
        var next = await Call(new() { ["softwarePath"] = "PLC", ["cursor"] = cursor });
        Assert.False(next.IsError, next.StructuredContent!.ToJsonString());
        Assert.Equal("PLC/B004", (string?)next.StructuredContent!["data"]!["items"]![0]!["path"]);
        var large = await Call(new() { ["softwarePath"] = "PLC", ["limit"] = 20 });
        Assert.False(large.IsError);
        Assert.Equal("cursor", (string?)large.StructuredContent!["meta"]!["paging"]!["mode"]);
        Assert.Equal(20, large.StructuredContent["data"]!["items"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"items\":null,\"offset\":0,\"total\":0,\"nextCursor\":null}")]
    [InlineData("{\"items\":[{\"path\":\"a\"},{\"path\":\"b\"}],\"offset\":0,\"total\":2,\"nextCursor\":null}")]
    [InlineData("{\"items\":[{\"path\":\"a\"}],\"offset\":0,\"total\":0,\"nextCursor\":null}")]
    [InlineData("{\"items\":[],\"offset\":0,\"total\":2,\"nextCursor\":\"next\"}")]
    [InlineData("{\"items\":[{}],\"offset\":0,\"total\":1,\"nextCursor\":null}")]
    [InlineData("{\"items\":[{\"path\":\"a\"}],\"offset\":0,\"total\":2,\"nextCursor\":null}")]
    public async Task Malformed_worker_pages_return_classified_read_failure(string payload)
    {
        using var worker = new Worker(JsonNode.Parse(payload));
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "GetBlocks"), worker), "21", null, () => new(false, 1));
        var result = await tool.InvokeAsync(Request("ListPlcBlocks", new() { ["softwarePath"] = "PLC", ["limit"] = 1 }));
        Assert.True(result.IsError);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)result.StructuredContent!["error"]!["code"]);
        Assert.Contains("InvalidDataException", result.StructuredContent.ToJsonString());
        Assert.DoesNotContain("NullReferenceException", result.StructuredContent.ToJsonString());
    }

    [Fact]
    public void Timeout_environment_writers_and_HTTP_fixtures_share_a_nonparallel_collection()
    {
        var types = new[] { typeof(PlcWorkerRobustnessTests), typeof(EngineHttpSessionTests), typeof(EngineHttpSessionTestsPlc) };
        Assert.All(types, type => Assert.Equal(WorkerDataDirectoryCollection.Name,
            type.CustomAttributes.SingleOrDefault(a => a.AttributeType == typeof(CollectionAttribute))?.ConstructorArguments.Single().Value));
        Assert.True(typeof(WorkerDataDirectoryCollection).GetCustomAttribute<CollectionDefinitionAttribute>()!.DisableParallelization);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Missing_or_invalid_decoder_runtime_is_rejected_without_install_path(bool invalidReceipt)
    {
        const string selection = "TiaOpenness.Shared.BundleLayout.v4";
        var previous = AppDomain.CurrentDomain.GetData(selection);
        string root = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "bin-build", "private-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "scripts/ecosystem"));
        File.WriteAllText(Path.Combine(root, "scripts/ecosystem/simaticml_decode_bridge.py"), "raise Exception('must not execute')");
        File.WriteAllText(Path.Combine(root, "input.xml"), "<Document/>");
        if (invalidReceipt)
        {
            Directory.CreateDirectory(Path.Combine(root, "runtime/decoder-python"));
            File.WriteAllText(Path.Combine(root, "runtime/decoder-python/python.exe"), "must not execute");
            File.WriteAllText(Path.Combine(root, "runtime/decoder-python/runtime.json"), "{}");
        }
        try
        {
            AppDomain.CurrentDomain.SetData(selection, root);
            using var worker = new Worker();
            JsonObject Ready() => new() { ["ready"] = true };
            var shared = LegacyHostToolRegistry.Create(worker, "21", false, readinessProvider: Ready, hostPorts: false);
            using var pipeline = new Pipeline("21", FoundationHostPorts.CreateServices(worker, "21", Ready, null), shared);
            var result = await pipeline.AllTools.Single(t => t.ProtocolTool.Name == "DecodePlcSimaticMl").InvokeAsync(
                Request("DecodePlcSimaticMl", new() { ["filePath"] = Path.Combine(root, "input.xml") }));
            Assert.True(result.IsError);
            Assert.Equal("IO_FAILED", (string?)result.StructuredContent!["error"]!["code"]);
            Assert.Equal("decoder-runtime", (string?)result.StructuredContent["error"]!["details"]!["operation"]);
            Assert.Equal("runtime/decoder-python", (string?)result.StructuredContent["error"]!["details"]!["path"]);
            Assert.DoesNotContain(root, result.StructuredContent.ToJsonString());
            Assert.DoesNotContain(root, ((TextContentBlock)result.Content.Single()).Text);
            Assert.Equal(0, worker.Limit);
        }
        finally { AppDomain.CurrentDomain.SetData(selection, previous); Directory.Delete(root, true); }
    }
}
