extern alias enginehost;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using Xunit;

public sealed class CanonicalParameterTests
{
    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Export_schema_separates_directory_and_file_and_retains_old_alias(string release)
    {
        using var selected = new ReleaseScope(release);
        using var worker = new FakeWorker();
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        var directory = tools.Single(t => t.ProtocolTool.Name == "ExportPlcBlocks").ProtocolTool.InputSchema.GetProperty("properties");
        Assert.True(directory.TryGetProperty("exportDirectory", out var target));
        Assert.Equal("string", target.GetProperty("type").GetString());
        Assert.False(target.TryGetProperty("default", out _));
        Assert.True(directory.TryGetProperty("exportPath", out var alias));
        Assert.Contains("5.0", alias.GetProperty("description").GetString());
        var file = tools.Single(t => t.ProtocolTool.Name == "ExportPlcTagTable").ProtocolTool.InputSchema.GetProperty("properties");
        Assert.True(file.TryGetProperty("exportFile", out _));
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Canonical_and_legacy_calls_reach_the_same_parameter_and_only_legacy_warns(string release)
    {
        var inner = new CapturingTool("ExportPlcTagTable", "exportPath", true, release);
        var tool = new enginehost::TiaMcp.FoundationHost.CanonicalParameterTool(inner, release);
        const string path = "C:/Exports/Tags.xml";
        var canonical = await tool.InvokeAsync(Request(tool, new JsonObject { ["exportFile"] = path }));
        Assert.Equal(path, (string?)inner.Last!["exportPath"]);
        Assert.Empty(canonical.StructuredContent!["meta"]!["warnings"]!.AsArray());
        var legacy = await tool.InvokeAsync(Request(tool, new JsonObject { ["exportPath"] = path }));
        Assert.Equal(path, (string?)inner.Last!["exportPath"]);
        var warning = Assert.Single(legacy.StructuredContent!["meta"]!["warnings"]!.AsArray())!;
        Assert.Equal("DEPRECATED_PARAMETER", (string?)warning["code"]);
        Assert.Equal("/exportPath", (string?)warning["details"]!["parameter"]);
        Assert.Equal(2, inner.Calls);
        Assert.Equal(release, (string?)legacy.StructuredContent!["meta"]!["releaseKey"]);
    }

    [Theory]
    [InlineData("{\"exportFile\":\"\"}")]
    [InlineData("{\"exportFile\":null}")]
    [InlineData("{\"exportFile\":\"relative.xml\"}")]
    [InlineData("{\"exportFile\":\"C:/Exports/Tags.xml\",\"exportPath\":\"C:/Exports/Other.xml\"}")]
    public async Task Invalid_canonical_forms_are_refused_without_dispatch(string json)
    {
        var inner = new CapturingTool("ExportPlcTagTable", "exportPath", true);
        var tool = new enginehost::TiaMcp.FoundationHost.CanonicalParameterTool(inner, "21");
        var result = await tool.InvokeAsync(Request(tool, JsonNode.Parse(json)!.AsObject()));
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent!["error"]!["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent!["meta"]!["execution"]);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Omitted_optional_output_stays_omitted_and_blank_legacy_keeps_its_meaning()
    {
        var inner = new CapturingTool("WritePlcSclSourceFile", "outputPath", false);
        var tool = new enginehost::TiaMcp.FoundationHost.CanonicalParameterTool(inner, "21");
        await tool.InvokeAsync(Request(tool, new JsonObject()));
        Assert.False(inner.Last!.ContainsKey("outputPath"));
        var legacy = await tool.InvokeAsync(Request(tool, new JsonObject { ["outputPath"] = "" }));
        Assert.Equal("", (string?)inner.Last!["outputPath"]);
        Assert.Single(legacy.StructuredContent!["meta"]!["warnings"]!.AsArray());
    }

    [Fact]
    public async Task Default_output_is_stable_and_does_not_create_files_during_preview()
    {
        string directory = Path.Combine(Path.GetTempPath(), "des4-default-" + Guid.NewGuid().ToString("N"));
        var inner = new CapturingTool("ExportPlcTagTable", "exportPath", true);
        var tool = new enginehost::TiaMcp.FoundationHost.CanonicalParameterTool(inner, "21", () => directory);
        await tool.InvokeAsync(Request(tool, new JsonObject()));
        string path = (string)inner.Last!["exportPath"]!;
        await tool.InvokeAsync(Request(tool, new JsonObject()));
        Assert.Equal(Path.Combine(directory, "ExportPlcTagTable.xml"), path);
        Assert.Equal(path, (string?)inner.Last!["exportPath"]);
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Device_array_preserves_group_order_and_legacy_string_warns(string release)
    {
        var inner = new CapturingTool("GetDeviceInfo", "devicePath", true);
        var tool = new enginehost::TiaMcp.FoundationHost.CanonicalParameterTool(inner, release);
        var canonical = await tool.InvokeAsync(Request(tool, new JsonObject { ["devicePath"] = new JsonArray("Group", "Station") }));
        Assert.Equal("Group/Station", (string?)inner.Last!["devicePath"]);
        Assert.Empty(canonical.StructuredContent!["meta"]!["warnings"]!.AsArray());
        var legacy = await tool.InvokeAsync(Request(tool, new JsonObject { ["devicePath"] = "Station" }));
        Assert.Equal("Station", (string?)inner.Last!["devicePath"]);
        Assert.Single(legacy.StructuredContent!["meta"]!["warnings"]!.AsArray());
    }

    [Fact]
    public void File_and_directory_are_not_silently_reinterpreted()
    {
        var error = CanonicalToolParameters.ValidateAliases("ExportDeviceAml", new JsonObject { ["exportFile"] = Path.GetTempPath() });
        Assert.Equal("/exportFile", Assert.IsType<InvalidArgumentDetails>(error!.Details).Parameter);
        Assert.NotNull(CanonicalToolParameters.ValidateAliases("WritePlcSclSourceFile", new JsonObject { ["exportFile"] = "C:/Exports/NoExtension" }));
        Assert.NotNull(CanonicalToolParameters.ValidateAliases("ManageMachineDescription",
            new JsonObject { ["exportFile"] = "C:/Exports/model.csv", ["format"] = "csv" }));
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Nested_binding_uses_the_same_canonical_schema_as_discovery(string release)
    {
        using var selected = new ReleaseScope(release);
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var shared = LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, shared);
        using var session = pipeline.EnterSession();
        var context = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Current;
        var args = new ToolArguments(JsonSerializer.SerializeToElement(new { exportFile = "C:/Exports/content.txt", exportId = "fixture" }));
        Assert.Null(context.Invoker!.Bind("SaveExportContent", args, out var call));
        Assert.NotNull(call);
        var device = new ToolArguments(JsonSerializer.SerializeToElement(new { devicePath = new[] { "Group", "Station" } }));
        Assert.Null(context.Invoker.Bind("GetDeviceInfo", device, out call));
        Assert.NotNull(call);
        Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public void Default_output_is_materialized_before_the_approval_card()
    {
        using var selected = new ReleaseScope("21");
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var shared = LegacyHostToolRegistry.Create(worker, "21", false, hostPorts: false);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        var context = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Current;
        context.ExportDirectory = Path.Combine(Path.GetTempPath(), "des4-approval-" + Guid.NewGuid().ToString("N"));
        var input = new ToolArguments(JsonSerializer.SerializeToElement(new { exportId = "fixture" }));
        var effective = enginehost::TiaMcp.FoundationHost.CanonicalParameterTool.ApplyDefaults("SaveExportContent", input);
        var approval = TiaOpenness.Shared.PendingApproval.Create("fixture", "21", "SaveExportContent", effective.Json.GetRawText(), null, 30);
        Assert.Equal(Path.Combine(context.ExportDirectory, "SaveExportContent.txt"), Assert.Single(approval.Operations).Target);
        Assert.False(Directory.Exists(context.ExportDirectory));
    }

    [Fact]
    public void Mapping_timing_is_reported_without_a_timing_gate()
    {
        var input = new JsonObject { ["exportFile"] = "C:/Exports/Tags.xml", ["dryRun"] = true };
        const int iterations = 10000;
        for (int i = 0; i < 1000; i++) CanonicalToolParameters.LegacyArguments("ExportPlcTagTable", input);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) input.DeepClone();
        double before = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        for (int i = 0; i < iterations; i++) CanonicalToolParameters.LegacyArguments("ExportPlcTagTable", input);
        double after = timer.Elapsed.TotalMilliseconds;
        Console.WriteLine($"DES-4 mapping, {iterations} copies: before={before:F3} ms; after={after:F3} ms; " +
            "managed copy/adaptation only, no TIA calls.");
        Assert.Equal("C:/Exports/Tags.xml", (string?)input["exportFile"]);
    }

    private static RequestContext<CallToolRequestParams> Request(McpServerTool tool, JsonObject arguments) =>
        new(DispatchProxy.Create<IMcpServer, ServerProxy>()) { Params = new() { Name = tool.ProtocolTool.Name,
            Arguments = arguments.Deserialize<Dictionary<string, JsonElement>>() } };

    private sealed class ReleaseScope : IDisposable
    {
        private readonly string previous = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        private readonly string profile = enginehost::TiaMcpServer.ModelContextProtocol.McpServer.ResolvedProfile();
        internal ReleaseScope(string release)
        {
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
            enginehost::TiaMcpServer.ModelContextProtocol.McpServer.SetProfileOverride("full");
        }
        public void Dispose()
        {
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previous;
            enginehost::TiaMcpServer.ModelContextProtocol.McpServer.SetProfileOverride(profile);
        }
    }

    private sealed class CapturingTool : McpServerTool
    {
        private readonly Tool tool;
        private readonly string release;
        internal JsonObject? Last;
        internal int Calls;
        internal CapturingTool(string name, string parameter, bool required, string release = "21")
        {
            this.release = release;
            tool = new Tool { Name = name, InputSchema = JsonSerializer.SerializeToElement(new JsonObject {
                ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JsonObject { [parameter] = new JsonObject { ["type"] = "string" } },
                ["required"] = required ? new JsonArray(parameter) : new JsonArray()
            }) };
        }
        public override Tool ProtocolTool => tool;
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Last = JsonSerializer.SerializeToNode(request.Params!.Arguments)!.AsObject();
            var meta = new Meta(DateTimeOffset.UtcNow, release, tool.Name, "test", Outcome.Succeeded, Execution.ReadOnly,
                false, BehaviorPolicy.NotApplicable, Completeness.Complete, null, Array.Empty<Warning>());
            var body = JsonNode.Parse(V4Json.Serialize(Envelope.Create(new { value = true }, null, meta)))!;
            return ValueTask.FromResult(new CallToolResult { IsError = false, StructuredContent = body,
                Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } });
        }
    }
}
