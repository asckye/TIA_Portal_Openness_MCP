using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Xunit;

public sealed class TagTableBatchExportTests
{
    [Theory, MemberData(nameof(B3HostPortsTests.Releases), MemberType = typeof(B3HostPortsTests))]
    public async Task Usage_serves_the_registered_preview_and_execution_examples(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        var result = await fixture.Call("GetToolUsage", new { toolName = "ExportPlcTagTables" });
        Assert.True((bool?)result["ok"], result.ToJsonString());
        var usage = result["data"]!;
        Assert.Equal("ExportPlcTagTables", (string?)usage["toolName"]);
        Assert.Equal(release, (string?)usage["releaseKey"]);
        var example = usage["example"]!;
        var preview = example["request"]!["params"]!["arguments"]!;
        Assert.Equal("<exact softwarePath from GetProjectTree>", (string?)preview["softwarePath"]);
        Assert.NotNull(preview["exportDirectory"]);
        Assert.Null(preview["exportPath"]);
        Assert.True((bool?)preview["recursive"]);
        Assert.True((bool?)preview["dryRun"]);
        var execution = example["execution"]!["arguments"]!;
        Assert.False((bool?)execution["dryRun"]);
        Assert.True((bool?)execution["confirm"]);
        Assert.NotNull(execution["expectedInventoryHash"]);
        Assert.NotNull(execution["expectedProjectFile"]);
        Assert.Contains("NOT RUN", (string)example["validation"]!);
        Assert.Equal(release is not ("14sp1" or "15.1"), usage["inputSchema"]!["properties"]!.AsObject().ContainsKey("unitName"));
    }

    [Theory, MemberData(nameof(B3HostPortsTests.Releases), MemberType = typeof(B3HostPortsTests))]
    public void Preview_maps_encoded_paths_and_execution_requires_the_complete_inventory(string release)
    {
        string directory = Path.GetFullPath(Path.Combine("bin-build", "tag-table-export-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        int exports = 0, offline = 0;
        string repository = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        byte[] contents = File.ReadAllBytes(Path.Combine(repository, "templates/mcp-full-e2e-verify/import/MCP_E2E_Tags.xml"));
        var inventory = new[] { "Root", "G/Sub/T 1", "G%2FLine/变量表" }.Select(path => new PlcBatchExportSource {
            Path = path, Consistent = true, Export = file => { exports++; File.WriteAllBytes(file.FullName, contents); }
        }).ToArray();
        string unit = release is "14sp1" or "15.1" ? "" : "Unit_1";
        PlcBatchExportResult Run(bool dry, string hash = "", int max = 128) => PlcBatchExportPolicy.Run("tagTables",
            "C:/Fixture.ap" + release, "devices/PLC/CPU", "", true, directory, max, dry, hash, inventory, () => offline++,
            (file, export) => { export(file); return null; }, unit);
        try
        {
            var preview = Run(true);
            Assert.False(preview.Executed);
            Assert.True(preview.InventoryComplete);
            Assert.Equal(unit, Assert.IsType<PlcScopedBatchExportResult>(preview).UnitName);
            Assert.Equal(inventory.Select(item => item.Path).Order(StringComparer.Ordinal), preview.Items.Select(item => item.ObjectPath));
            Assert.All(preview.Items, item => {
                Assert.Equal("planned", item.Status);
                Assert.Equal("tagTables-" + PlcBatchExportPolicy.Hash(item.ObjectPath) + ".xml", Path.GetFileName(item.OutputFile));
            });
            Assert.Equal(64, preview.InventoryHash.Length);
            Assert.Empty(Directory.GetFileSystemEntries(directory));
            Assert.Equal(0, exports);
            Assert.Equal(0, offline);
            Assert.Equal("expectedInventoryHash", Assert.Throws<AdapterPreconditionException>(() => Run(false)).ParamName);
            Assert.Equal("expectedInventoryHash", Assert.Throws<AdapterPreconditionException>(() => Run(false, new string('f', 64))).ParamName);
            Assert.Equal("groupPath", Assert.Throws<AdapterPreconditionException>(() => Run(true, max: 2)).ParamName);
            Assert.Equal(0, exports);
            var request = new JsonObject { ["softwarePath"] = "devices/PLC/CPU", ["groupPath"] = "", ["recursive"] = true,
                ["maxItems"] = 128, ["dryRun"] = true };
            WorkerProtocol.ValidateExchangeResult("ExportTagTables", request, JsonSerializer.SerializeToNode(preview));
            request["groupPath"] = "G";
            Assert.Throws<IOException>(() => WorkerProtocol.ValidateExchangeResult("ExportTagTables", request,
                JsonSerializer.SerializeToNode(preview)));
            var applied = Run(false, preview.InventoryHash);
            Assert.Equal(3, exports);
            Assert.Equal(3, offline);
            Assert.All(applied.Items, item => Assert.Equal("exported", item.Status));
            Assert.Equal(3, Directory.GetFiles(directory).Length);
            Assert.All(applied.Items, item => Assert.Equal(contents, File.ReadAllBytes(item.OutputFile)));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Existing_batch_policy_and_dispatch_guards_include_tag_tables()
    {
        BatchExportTests.Run((passed, message) => Assert.True(passed, message), message => Assert.Fail(message));
        var server = DispatchProxy.Create<IMcpServer, ServerProxy>();
        BatchExportDispatchTests.Run(server, (passed, message) => Assert.True(passed, message)).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Group_scope_and_recursion_reach_the_batch_worker()
    {
        string directory = Path.GetFullPath(Path.Combine("bin-build", "tag-table-dispatch-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        using var worker = new InventoryWorker();
        var tool = FoundationTools.Create(worker).Single(tool => tool.ProtocolTool.Name == "ExportPlcTagTables");
        var server = DispatchProxy.Create<IMcpServer, ServerProxy>();
        async Task<JsonNode> Preview(string group, bool recursive)
        {
            var request = new RequestContext<CallToolRequestParams>(server) { Params = new() {
                Name = tool.ProtocolTool.Name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new {
                    softwarePath = "devices/PLC/CPU", groupPath = group, recursive, exportPath = directory
                }))
            } };
            var response = await tool.InvokeAsync(request);
            Assert.Equal("ExportTagTables", worker.Operation);
            Assert.Equal(group, (string?)worker.Arguments!["groupPath"]);
            Assert.Equal(recursive, (bool?)worker.Arguments["recursive"]);
            return JsonNode.Parse(response.Content.OfType<TextContentBlock>().Single().Text)!;
        }
        static string[] Paths(JsonNode result) => result["Items"]!.AsArray().Select(item => (string)item!["ObjectPath"]!).ToArray();
        try
        {
            Assert.Equal(new[] { "Root" }, Paths(await Preview("", false)));
            Assert.Equal(new[] { "G%2FLine/变量表", "G/Sub/T 1", "G/T 2", "Root" }, Paths(await Preview("", true)));
            Assert.Equal(new[] { "G/T 2" }, Paths(await Preview("G", false)));
            Assert.Equal(new[] { "G/Sub/T 1", "G/T 2" }, Paths(await Preview("G", true)));
            Assert.Equal(new[] { "G%2FLine/变量表" }, Paths(await Preview("G%2FLine", true)));
            Assert.Empty(Directory.GetFileSystemEntries(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class InventoryWorker : IFoundationWorker
    {
        internal string? Operation;
        internal JsonObject? Arguments;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Operation = operation; Arguments = arguments.DeepClone().AsObject();
            string group = (string)arguments["groupPath"]!;
            bool recursive = (bool)arguments["recursive"]!;
            var paths = new[] { "Root", "G/Sub/T 1", "G/T 2", "G%2FLine/变量表" };
            var sources = paths.Where(path => recursive
                ? group == "" || path.StartsWith(group + "/", StringComparison.Ordinal)
                : path.Substring(0, Math.Max(0, path.LastIndexOf('/'))) == group).Select(path => new PlcBatchExportSource {
                    Path = path, Consistent = true, Export = _ => throw new InvalidOperationException("Preview cannot export.")
                });
            var result = PlcBatchExportPolicy.Run("tagTables", "C:/Fixture.ap21", (string)arguments["softwarePath"]!, group, recursive,
                (string)arguments["exportPath"]!, (int)arguments["maxItems"]!, (bool)arguments["dryRun"]!,
                (string)arguments["expectedInventoryHash"]!, sources, () => throw new InvalidOperationException("Preview cannot publish."));
            WorkerProtocol.ValidateExchangeResult(operation, arguments, JsonSerializer.SerializeToNode(result));
            return Task.FromResult(JsonSerializer.SerializeToNode(result));
        }
        public void Dispose() { }
    }
}
