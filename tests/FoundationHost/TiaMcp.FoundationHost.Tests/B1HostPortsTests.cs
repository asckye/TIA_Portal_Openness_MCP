extern alias enginehost;
using System.Text.Json;
using System.Text.Json.Nodes;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using Xunit;

public sealed class B1HostPortsTests
{
    [Fact]
    public void HostDeclarationsHaveNoSiemensAssemblyDependency()
    {
        Assert.DoesNotContain(typeof(Host.McpServer).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name!.StartsWith("Siemens.", StringComparison.Ordinal));
    }
    public static IEnumerable<object[]> Tools => PortedFamilies.All.Where(f => f.Name is "F01" or "F02" or "F03")
        .SelectMany(f => f.Tools).Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(Tools))]
    public void HostOwnsAdmissionAndRefusesUnknownArguments(string name)
    {
        using var scope = EngineHostParity.EnterScope();
        Assert.Equal("host", Host.McpServer.CatalogView.All[name].Execution);
        var result = Host.McpServer.CallTool(name, new ToolArguments(JsonSerializer.SerializeToElement(new { unknownB1Argument = true })));
        Assert.True(result.IsError);
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(((global::ModelContextProtocol.Protocol.TextContentBlock)Assert.Single(result.Content)).Text), result.StructuredContent));
        foreach (var release in new[] { "14sp1", "15.1", "16", "17", "18", "19" })
            Assert.False(PortedFamilies.Available(release, name));
    }

    [Fact]
    public async Task DoctorReportsNoTiaInBothLanguagesWithoutRepairOrNativeAccess()
    {
        using var scope = EngineHostParity.EnterScope();
        var operations = new List<string>();
        var prior = Host.HostToolServices.Override;
        try
        {
            Host.HostToolServices.Override = (operation, _) => {
                operations.Add(operation);
                return operation switch {
                    "environment" => new JsonArray(new JsonObject { ["Id"] = "tia-install", ["Ok"] = false,
                        ["NameEn"] = "TIA Portal installation", ["DetailEn"] = "no TIA Portal V21 installation found",
                        ["FixEn"] = "Install TIA Portal", ["FixZh"] = "请安装 TIA Portal。" }),
                    "group" => JsonValue.Create(false),
                    "session.GetState" => new JsonObject { ["IsConnected"] = false, ["Project"] = "-" },
                    _ => throw new InvalidOperationException("Unexpected diagnostic operation: " + operation)
                };
            };
            var result = await Host.McpServer.GetEnvironmentDiagnosticsV4(fix: false);
            var body = result.StructuredContent!.AsObject();
            Assert.False((bool)body["data"]!["ready"]!);
            Assert.Contains("no TIA Portal V21", body.ToJsonString());
            Assert.Contains("请安装", (string)body["data"]!["recommendedFixZh"]!);
            Assert.DoesNotContain("group.fix", operations);
            Assert.DoesNotContain("ExportBlockDocument", operations);
        }
        finally { Host.HostToolServices.Override = prior; }
    }

    [Theory]
    [InlineData("\"[]\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[42]")]
    public void HostDirectBridgeAndBatchRejectInvalidArraysBeforeFileAccess(string value)
    {
        using var scope = EngineHostParity.EnterScope();
        var args = JsonNode.Parse("{\"templatePath\":\"C:/fixture.xml\",\"outputDirectory\":\"C:/fixture\",\"rows\":" + value + "}")!.AsObject();
        ToolArguments Input(JsonObject data) => new(JsonSerializer.SerializeToElement(data));
        var direct = Host.McpServer.CallTool("InstantiatePlcTemplates", Input(args));
        var bridge = Host.McpServer.CallTool("CallTool", Input(new JsonObject { ["name"] = "InstantiatePlcTemplates", ["arguments"] = args.DeepClone() }));
        var batch = Host.McpServer.CallTool("PreviewToolBatch", Input(new JsonObject {
            ["operations"] = new JsonArray(new JsonObject { ["name"] = "InstantiatePlcTemplates", ["arguments"] = args.DeepClone() }), ["expectedProject"] = "Fixture"
        }));
        foreach (var result in new[] { direct, bridge, batch })
        {
            Assert.True(result.IsError);
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        }
    }

    [Fact]
    public void SharedArrayArgumentsBindThroughTheBridgeAndStillRejectWrongShapes()
    {
        using var scope = EngineHostParity.EnterScope();
        var tool = global::ModelContextProtocol.Server.McpServerTool.Create(
            (string[] files) => string.Join(",", files), new() { Name = "SharedArrayFixture" });
        var catalog = new enginehost::TiaMcp.FoundationHost.SharedToolCatalog(Host.McpServer.CatalogView,
            new[] { tool }, new HashSet<string>());
        var invoker = new enginehost::TiaMcp.FoundationHost.WorkerToolInvoker(catalog,
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Worker, new Host.ImportStagingHostLifetime(), new[] { tool });
        Assert.Null(invoker.Bind("SharedArrayFixture", new ToolArguments(JsonSerializer.SerializeToElement(
            new { files = new[] { "one.scl", "two.scl" } })), out var call));
        Assert.NotNull(call);
        Assert.NotNull(invoker.Bind("SharedArrayFixture", new ToolArguments(JsonSerializer.SerializeToElement(
            new { files = "one.scl" })), out _));
        Assert.NotNull(invoker.Bind("SharedArrayFixture", new ToolArguments(JsonSerializer.SerializeToElement(
            new { files = new[] { 1 } })), out _));
    }

    [Fact]
    public async Task UpdateCheckUsesTheWorkerLayoutBeforeRepositoryAndHttpChecks()
    {
        using var scope = EngineHostParity.EnterScope();
        string root = Path.Combine(Path.GetTempPath(), "update layout 中文 " + Guid.NewGuid().ToString("N"));
        const string selection = "TiaOpenness.Shared.BundleLayout.v4";
        object? previous = AppDomain.CurrentDomain.GetData(selection);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "manifest"));
            File.WriteAllText(Path.Combine(root, "manifest", "package-manifest.json"), "{}");
            AppDomain.CurrentDomain.SetData(selection, root);
            var context = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Current;
            foreach (string release in new[] { "20", "21" })
            {
                foreach (string anchor in new[] { "src/Engine/bin/Release/net48", "sdk-fixture", "runtime/v" + release + "/worker" })
                {
                    context.WorkerBaseDirectory = Path.Combine(root, anchor.Replace('/', Path.DirectorySeparatorChar));
                    var result = (await Host.McpServer.CheckProductUpdateV4("x")).StructuredContent!;
                    Assert.StartsWith("repository must be 'owner/name'", (string)result["data"]!["summary"]!);
                    var evidence = result["data"]!["evidence"]!;
                    Assert.Null(evidence["installRoot"]);
                    Assert.Null(evidence["updaterScript"]);
                    Assert.False((bool)evidence["success"]!);
                    Assert.Null(evidence["releaseApiUrl"]);
                }
                context.WorkerBaseDirectory = Path.Combine(root, "runtime", "v" + release);
                var missing = (await Host.McpServer.CheckProductUpdateV4("x")).StructuredContent!;
                Assert.Equal("RESOURCE_UNAVAILABLE", (string?)missing["error"]?["code"]);
                Assert.Contains("delivery.json", missing.ToJsonString());
                File.WriteAllText(Path.Combine(root, "manifest", "delivery.json"), "{}");
                missing = (await Host.McpServer.CheckProductUpdateV4("x")).StructuredContent!;
                Assert.Equal("RESOURCE_UNAVAILABLE", (string?)missing["error"]?["code"]);
                Assert.Contains("TiaMcp.Updater.exe", missing.ToJsonString());
                Directory.CreateDirectory(Path.Combine(root, "runtime", "tools"));
                string updater = Path.Combine(root, "runtime", "tools", "TiaMcp.Updater.exe");
                File.WriteAllText(updater, "fixture");
                var installed = (await Host.McpServer.CheckProductUpdateV4("x")).StructuredContent!;
                Assert.StartsWith("repository must be 'owner/name'", (string)installed["data"]!["summary"]!);
                Assert.Equal(root, (string?)installed["data"]!["evidence"]!["installRoot"]);
                Assert.Equal(updater, (string?)installed["data"]!["evidence"]!["updaterScript"]);
                Assert.Null(installed["data"]!["evidence"]!["releaseApiUrl"]);
                File.Delete(updater);
                File.Delete(Path.Combine(root, "manifest", "delivery.json"));
            }
        }
        finally
        {
            AppDomain.CurrentDomain.SetData(selection, previous);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NativeJournalReaderRetainsRotatedPairsAndCountsTrailingRecords()
    {
        using var scope = EngineHostParity.EnterScope();
        string scratch = Path.Combine(Path.GetTempPath(), "tia-host-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", scratch);
            string prior = Path.Combine(scratch, "calls-fixture.jsonl.previous");
            File.WriteAllText(prior, "{\"phase\":\"BEFORE\",\"binding\":{\"text\":\"中文 😀 <>&'`+\",\"null\":null,\"number\":-2146233079}}\n");
            File.SetLastWriteTimeUtc(prior, DateTime.UtcNow.AddMinutes(-1));
            File.WriteAllText(Path.Combine(scratch, "calls-fixture.jsonl"), "{\"phase\":\"RETURNED\",\"binding\":{\"text\":\"\\u4E2D\\u6587 \\uD83D\\uDE00 <>&'`+\",\"null\":null,\"number\":-2146233079}}\n{partial");
            File.WriteAllText(Path.Combine(scratch, "calls-fixture.jsonl.unrelated"), "{\"phase\":\"unrelated\"}\n");
            var reader = new Host.EngineeringDiagnosticsTools();
            var meta = reader.ReadNativeInvocationLog(100).Meta!;
            Assert.True(JsonNode.DeepEquals(meta["records"]![0]!["binding"], meta["records"]![1]!["binding"]));
            Assert.Equal(2, (int)meta["filesRead"]!);
            Assert.Equal(1, (int)meta["malformedLines"]!);
            Assert.DoesNotContain("unrelated", meta.ToJsonString());
            Assert.Single(reader.ReadNativeInvocationLog(1).Meta!["records"]!.AsArray());
            Assert.Equal("RETURNED", (string?)reader.ReadNativeInvocationLog(1).Meta!["records"]![0]!["phase"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", previous);
            Directory.Delete(scratch, true);
        }
    }
}
