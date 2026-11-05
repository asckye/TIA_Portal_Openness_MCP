extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.Generation;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using Context = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration;

[Collection(BundleSelectionCollection.Name)]
public sealed class CanonicalParameterRound3Tests
{
    public static IEnumerable<object[]> Releases() => CanonicalParameterRound2Tests.Releases();

    [Theory, MemberData(nameof(Releases))]
    public async Task Aml_preview_accepts_the_pending_default_but_rejects_a_missing_explicit_directory(string release)
    {
        using var fixture = new Fixture(release);
        using var session = fixture.Pipeline.EnterSession();
        Context.Current.OutputRoot = () => fixture.Root;
        var args = new JsonObject { ["devicePath"] = "PLC_1" };
        var preview = Host.PreviewToolCall("ExportDeviceAml", new ToolArguments(JsonSerializer.SerializeToElement(args)));
        Assert.True((bool?)preview.StructuredContent!["ok"], preview.StructuredContent.ToJsonString());
        Assert.True((bool?)preview.StructuredContent["data"]!["ok"], preview.StructuredContent.ToJsonString());
        string planned = Context.Current.ExportDirectory!;
        Assert.False(Directory.Exists(planned));
        var explicitArgs = args.DeepClone().AsObject();
        explicitArgs["exportDirectory"] = Path.Combine(fixture.Root, "missing");
        var refused = Host.PreviewToolCall("ExportDeviceAml", new ToolArguments(JsonSerializer.SerializeToElement(explicitArgs)));
        Assert.False((bool?)refused.StructuredContent!["data"]!["ok"]);
        Assert.Contains("requires an existing directory", (string?)refused.StructuredContent["data"]!["summary"]);
        var inner = new Capture("ExportDeviceAml", "exportPath", true, "devicePath");
        var tool = new enginehost::TiaMcp.FoundationHost.CanonicalParameterTool(inner, release);
        Assert.True((bool?)(await tool.InvokeAsync(Request(tool, args))).StructuredContent!["ok"]);
        Assert.Equal(planned, inner.Last!.Arguments!["exportPath"].GetString());
        Assert.True(Directory.Exists(planned));
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Csv_import_uses_a_json_output_file_for_both_names(string release)
    {
        using var fixture = new Fixture(release);
        var inner = new Capture("ManageMachineDescription", "outputPath", false, "action", "format", "inputPath");
        var tool = new enginehost::TiaMcp.FoundationHost.CanonicalParameterTool(inner, release);
        var args = new JsonObject { ["action"] = "import", ["format"] = "csv", ["inputPath"] = fixture.Root };
        string output = Path.Combine(fixture.Root, "machine.json");
        foreach (string name in new[] { "outputPath", "exportFile" })
        {
            var variant = args.DeepClone().AsObject(); variant[name] = output;
            var result = await tool.InvokeAsync(Request(tool, variant));
            Assert.True((bool?)result.StructuredContent!["ok"], result.StructuredContent.ToJsonString());
            Assert.Equal(output, inner.Last!.Arguments!["outputPath"].GetString());
        }
        args["exportDirectory"] = fixture.Root;
        var refused = await tool.InvokeAsync(Request(tool, args));
        Assert.Equal("INVALID_ARGUMENT", (string?)refused.StructuredContent!["error"]!["code"]);
        Assert.Equal(2, inner.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Csv_import_preview_and_execution_write_the_same_json_target(string release)
    {
        using var fixture = new Fixture(release);
        string repository = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var store = new StandardPackageStore(Path.Combine(repository, "templates", "standards"), Path.Combine(fixture.Root, "packages"));
        var package = store.Load("tiamcp.basic", "1.0.0");
        var machine = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(package.ReadFile("examples/machine.json")))!;
        machine["target"]!["release"] = release;
        string csv = Path.Combine(fixture.Root, "csv"); Directory.CreateDirectory(csv);
        foreach (var file in MachineCsv.Export(package, machine.ToJsonString())) File.WriteAllBytes(Path.Combine(csv, file.Key), file.Value);
        var tool = new enginehost::TiaMcp.FoundationHost.CanonicalParameterTool(
            new GenerationTool("ManageMachineDescription", release, () => store), release);
        string target = Path.Combine(fixture.Root, "machine.json");
        var args = new JsonObject { ["packageId"] = "tiamcp.basic", ["version"] = "1.0.0", ["action"] = "import",
            ["format"] = "csv", ["inputPath"] = csv, ["exportFile"] = target };
        var preview = await tool.InvokeAsync(Request(tool, args));
        Assert.True((bool?)preview.StructuredContent!["ok"], preview.StructuredContent.ToJsonString());
        Assert.Equal(target, (string?)preview.StructuredContent["data"]!["outputPath"]);
        Assert.Equal("machine.json", Assert.Single(preview.StructuredContent["data"]!["files"]!.AsObject()).Key);
        Assert.False(File.Exists(target));
        args["dryRun"] = false;
        args["expectedPlanHash"] = preview.StructuredContent["data"]!["planHash"]!.DeepClone();
        var written = await tool.InvokeAsync(Request(tool, args));
        Assert.True((bool?)written.StructuredContent!["ok"], written.StructuredContent.ToJsonString());
        Assert.True(File.Exists(target));
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(target)));
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Both_request_adapters_preserve_progress_metadata_and_restore_the_request(string release)
    {
        using var fixture = new Fixture(release);
        using var session = fixture.Pipeline.EnterSession();
        foreach (bool outer in new[] { false, true })
        {
            var inner = new Capture("SaveExportContent", "outputPath", true);
            var tool = new enginehost::TiaMcp.FoundationHost.CanonicalParameterTool(inner, release);
            var request = Request(tool, new JsonObject { [outer ? "outputPath" : "exportFile"] = Path.Combine(fixture.Root, "out.txt") });
            request.Params = JsonSerializer.Deserialize<CallToolRequestParams>(
                JsonSerializer.Serialize(request.Params).TrimEnd('}') + ",\"_meta\":{\"progressToken\":42,\"trace\":\"fixture\"}}");
            var original = request.Params!;
            Assert.NotNull(original.ProgressToken);
            using var cancellation = new CancellationTokenSource();
            if (outer) await enginehost::TiaMcp.FoundationHost.CanonicalParameterTool.InvokeWithDefaults(inner, request, cancellation.Token);
            else await tool.InvokeAsync(request, cancellation.Token);
            Assert.Same(original, request.Params);
            Assert.Equal(cancellation.Token, inner.Cancellation);
            foreach (var property in typeof(CallToolRequestParams).GetProperties().Where(p => p.Name is not "Name" and not "Arguments"))
                Assert.Equal(JsonSerializer.Serialize(property.GetValue(original)), JsonSerializer.Serialize(property.GetValue(inner.Last)));
            Assert.True(inner.HeartbeatAvailable);
        }
    }

    private static RequestContext<CallToolRequestParams> Request(McpServerTool tool, JsonObject input) =>
        new(DispatchProxy.Create<IMcpServer, ServerProxy>()) { Params = new() { Name = tool.ProtocolTool.Name,
            Arguments = input.Deserialize<Dictionary<string, JsonElement>>() } };

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "des4-r3-" + Guid.NewGuid().ToString("N"));
        private readonly HardwareAddressingAdmissionTests.UnboundWorker worker = new();
        internal readonly enginehost::TiaMcp.FoundationHost.EngineHostPipeline Pipeline;
        private readonly string release = Context.ReleaseKey;
        private readonly string profile = Host.ResolvedProfile();
        internal Fixture(string key)
        {
            Directory.CreateDirectory(Root); Context.ReleaseKey = key; Host.SetProfileOverride("full");
            Pipeline = new(key, worker, LegacyHostToolRegistry.Create(worker, key, false, hostPorts: false));
        }
        public void Dispose()
        {
            Pipeline.Dispose(); worker.Dispose(); Context.ReleaseKey = release; Host.SetProfileOverride(profile);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(Root));
            Directory.Delete(Root, true);
        }
    }

    private sealed class Capture : McpServerTool
    {
        internal CallToolRequestParams? Last;
        internal CancellationToken Cancellation;
        internal bool HeartbeatAvailable;
        internal int Calls;
        public override Tool ProtocolTool { get; }
        internal Capture(string name, string output, bool required, params string[] other)
        {
            var properties = new JsonObject { [output] = new JsonObject { ["type"] = "string" } };
            foreach (string field in other) properties[field] = new JsonObject { ["type"] = "string" };
            ProtocolTool = new Tool { Name = name, InputSchema = JsonSerializer.SerializeToElement(new JsonObject {
                ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false,
                ["required"] = new JsonArray((required ? new[] { output }.Concat(other) : other).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()) }) };
        }
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            Last = request.Params; Cancellation = cancellationToken; Calls++;
            using var heartbeat = enginehost::TiaMcpServer.ModelContextProtocol.ProgressHeartbeat.Begin(request);
            HeartbeatAvailable = enginehost::TiaMcpServer.ModelContextProtocol.ProgressHeartbeat.CaptureSender() != null;
            var body = JsonNode.Parse(V4Json.Serialize(Envelope.Create(new { written = true }, null,
                new Meta(DateTimeOffset.UtcNow, "21", ProtocolTool.Name, "test", Outcome.Succeeded, Execution.Completed,
                    false, BehaviorPolicy.NotApplicable, Completeness.Complete, null, Array.Empty<Warning>()))))!;
            return ValueTask.FromResult(new CallToolResult { StructuredContent = body,
                Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } });
        }
    }
}
