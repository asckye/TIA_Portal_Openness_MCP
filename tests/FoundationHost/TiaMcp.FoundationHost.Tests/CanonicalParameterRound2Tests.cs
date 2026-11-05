extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaOpenness.Shared;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using Context = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration;

public sealed class CanonicalParameterRound2Tests
{
    public static IEnumerable<object[]> Releases() => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    [Theory, MemberData(nameof(Releases))]
    public void Full_and_lite_catalogs_have_no_root_combinators(string release)
    {
        using var fixture = new Fixture(release);
        foreach (string profile in new[] { "full", "lite" })
        {
            using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("", Path.Combine(fixture.Root, "worker.exe"),
                release, fixture.Worker, profile, fixture.Shared);
            Assert.NotEmpty(pipeline.Tools);
            foreach (var tool in pipeline.Tools)
                foreach (string keyword in new[] { "oneOf", "allOf", "anyOf" })
                    Assert.False(tool.ProtocolTool.InputSchema.TryGetProperty(keyword, out _), release + "/" + profile + "/" + tool.ProtocolTool.Name);
        }
    }

    [Theory, MemberData(nameof(Releases))]
    public void Preflight_accepts_old_new_and_default_export_targets(string release)
    {
        using var fixture = new Fixture(release);
        using var session = fixture.Pipeline.EnterSession();
        foreach (var args in new[] { new JsonObject { ["exportId"] = "fixture", ["exportFile"] = "C:/Exports/content.txt" },
            new JsonObject { ["exportId"] = "fixture", ["outputPath"] = "C:/Exports/content.txt" },
            new JsonObject { ["exportId"] = "fixture" } })
        {
            var report = Host.PreflightToolCall("SaveExportContent", args.ToJsonString());
            Assert.True((bool?)report.Meta!["ok"], report.Message);
            Assert.Empty(report.Meta["missing"]!.AsArray());
            var preview = Host.PreviewToolCall("SaveExportContent", new ToolArguments(JsonSerializer.SerializeToElement(args)));
            Assert.True((bool?)preview.StructuredContent!["ok"], preview.StructuredContent.ToJsonString());
        }
        var descriptor = Context.Current.Catalog!.Find("SaveExportContent")!;
        Assert.All(descriptor.Parameters.Where(parameter => parameter.Name is "outputPath" or "exportFile"),
            parameter => { Assert.False(parameter.Required); Assert.Contains("mutually exclusive", parameter.Description); });
        string discovery = string.Join("\n", Host.FindTools("SaveExportContent").Items!);
        Assert.Contains("outputPath?: string", discovery);
        Assert.Contains("exportFile?: string", discovery);
        Assert.Contains("mutually exclusive", discovery);
        Assert.False(Directory.Exists(Context.Current.ExportDirectory));
        Assert.Contains("exportPath", Context.Current.Catalog.Find("ExportPlcTagTable")!.ImplementationRequired);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Default_uses_the_resolved_output_root_on_first_use(string release)
    {
        using var fixture = new Fixture(release);
        foreach (string layout in new[] { "read-only", "configured", "development" })
        {
            string bundle = Path.Combine(fixture.Root, layout);
            string bin = Path.Combine(bundle, "runtime", "v21");
            Directory.CreateDirectory(bin);
            Directory.CreateDirectory(Path.Combine(bundle, "manifest"));
            File.WriteAllText(Path.Combine(bundle, "manifest", "package-manifest.json"), "{}");
            if (layout == "read-only") File.WriteAllText(Path.Combine(bundle, "data"), "blocked");
            if (layout == "development") File.WriteAllText(Path.Combine(bundle, "TiaPortalOpenness.slnx"), "<Solution />");
            string? configured = layout == "configured" ? Path.Combine(fixture.Root, "configured-data") : null;
            var locations = DataLocations.Resolve(bin, configured!, Path.Combine(fixture.Root, "local"), fixture.Root);
            using var session = fixture.Pipeline.EnterSession();
            Context.Current.ExportDirectory = null;
            Context.Current.OutputRoot = () => locations.OutputRoot;
            var inner = new Capture();
            var tool = new enginehost::TiaMcp.FoundationHost.CanonicalParameterTool(inner, release);
            var result = await tool.InvokeAsync(Request(tool, new JsonObject()));
            Assert.True((bool?)result.StructuredContent!["ok"], result.StructuredContent.ToJsonString());
            string output = (string)inner.Last!["outputPath"]!;
            Assert.Equal(locations.OutputRoot, Path.GetDirectoryName(Path.GetDirectoryName(output)));
            Assert.True(CallerInputFiles.IsInsideOutputRoot(output, locations.OutputRoot));
            Assert.True(Directory.Exists(Path.GetDirectoryName(output)));
            Assert.True(Guid.TryParseExact(Path.GetFileName(Path.GetDirectoryName(output)), "N", out _));
        }
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Missing_or_unwritable_default_refuses_only_the_export_call(string release)
    {
        using var fixture = new Fixture(release);
        using var session = fixture.Pipeline.EnterSession();
        foreach (bool missing in new[] { true, false })
        {
            string blocked = Path.Combine(fixture.Root, "blocked-root");
            File.WriteAllText(blocked, "file");
            Context.Current.ExportDirectory = null;
            Context.Current.OutputRoot = () => missing ? null : blocked;
            var inner = new Capture();
            var tool = new enginehost::TiaMcp.FoundationHost.CanonicalParameterTool(inner, release);
            var refused = await tool.InvokeAsync(Request(tool, new JsonObject()));
            Assert.False((bool?)refused.StructuredContent!["ok"]);
            Assert.Equal(missing ? "INVALID_ARGUMENT" : "IO_FAILED", (string?)refused.StructuredContent["error"]!["code"]);
            Assert.Contains(missing ? "explicitly" : "Could not create", (string?)refused.StructuredContent["error"]!["message"]);
            Assert.Equal("not-started", (string?)refused.StructuredContent["meta"]!["execution"]);
            Assert.Equal(0, inner.Calls);
            var explicitTarget = await tool.InvokeAsync(Request(tool, new JsonObject { ["exportFile"] = Path.Combine(fixture.Root, "out.txt") }));
            Assert.True((bool?)explicitTarget.StructuredContent!["ok"]);
            Assert.Equal(1, inner.Calls);
        }
    }

    [Fact]
    public void Machine_csv_examples_pass_runtime_alias_validation()
    {
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var calls = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference/tool-examples/calls.json")))!["profiles"]!.AsObject();
        foreach (var profile in calls)
        {
            var row = profile.Value!["ManageMachineDescription"]!;
            foreach (var action in new[] { "export", "exportTemplate" })
            {
                var args = row["arguments"]!.DeepClone().AsObject();
                foreach (var pair in row["operations"]![action]!["arguments"]!.AsObject()) args[pair.Key] = pair.Value?.DeepClone();
                Assert.Null(CanonicalToolParameters.ValidateAliases("ManageMachineDescription", args));
            }
        }
    }

    [Theory, MemberData(nameof(Releases))]
    public void Every_served_example_passes_runtime_alias_validation(string release)
    {
        using var fixture = new Fixture(release);
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var profiles = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference/tool-examples/calls.json")))!["profiles"]!.AsObject();
        foreach (var profile in profiles)
            foreach (var row in profile.Value!.AsObject())
            {
                if (!fixture.Shared.Any(tool => tool.ProtocolTool.Name == row.Key)
                    && !fixture.Pipeline.AllTools.Any(tool => tool.ProtocolTool.Name == row.Key)) continue;
                var args = row.Value!["arguments"]!.AsObject();
                Assert.Null(CanonicalToolParameters.ValidateAliases(row.Key, args));
                foreach (var operation in row.Value["operations"]!.AsObject())
                {
                    var variant = args.DeepClone().AsObject();
                    foreach (var argument in operation.Value!["arguments"]!.AsObject()) variant[argument.Key] = argument.Value?.DeepClone();
                    Assert.Null(CanonicalToolParameters.ValidateAliases(row.Key, variant));
                }
            }
    }

    [Theory]
    [InlineData("inspectionRules", "set", "{}")]
    [InlineData("blockSelection", "set", "{\"softwarePath\":\"PLC\"}")]
    [InlineData("exportBlocks", "set", "{\"outputDirectory\":\"C:/Exports\"}")]
    [InlineData("blockFilter", "set", "{\"filter\":\"x\",\"namePattern\":\"y\"}")]
    [InlineData("importBlocks", "clear", "{\"paths\":[\"C:/in.xml\"]}")]
    public async Task Prefill_cross_field_requirements_are_enforced_before_connection(string form, string mode, string fields)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var client = new WorkbenchControlClient(() => throw new InvalidOperationException("Must not connect."));
        var tool = WorkbenchControlTools.Create(worker, "21", client).Single(tool => tool.ProtocolTool.Name == "PrefillWorkbenchForm");
        var result = await tool.InvokeAsync(Request(tool, new JsonObject { ["form"] = form, ["mode"] = mode,
            ["fields"] = JsonNode.Parse(fields) }));
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent!["error"]!["code"]);
        Assert.Equal("/fields", (string?)result.StructuredContent["error"]!["details"]!["parameter"]);
    }

    private static RequestContext<CallToolRequestParams> Request(McpServerTool tool, JsonObject input) =>
        new(DispatchProxy.Create<IMcpServer, ServerProxy>()) { Params = new() { Name = tool.ProtocolTool.Name,
            Arguments = input.Deserialize<Dictionary<string, JsonElement>>() } };

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "des4-r2-" + Guid.NewGuid().ToString("N"));
        internal readonly HardwareAddressingAdmissionTests.UnboundWorker Worker = new();
        internal readonly IReadOnlyList<McpServerTool> Shared;
        internal readonly enginehost::TiaMcp.FoundationHost.EngineHostPipeline Pipeline;
        private readonly string release = Context.ReleaseKey;
        private readonly string profile = Host.ResolvedProfile();
        internal Fixture(string key)
        {
            Directory.CreateDirectory(Root);
            Context.ReleaseKey = key; Host.SetProfileOverride("full");
            Shared = LegacyHostToolRegistry.Create(Worker, key, false, hostPorts: false);
            Pipeline = new(key, Worker, Shared);
        }
        public void Dispose()
        {
            Pipeline.Dispose(); Worker.Dispose(); Context.ReleaseKey = release; Host.SetProfileOverride(profile);
            Directory.Delete(Root, true);
        }
    }

    private sealed class Capture : McpServerTool
    {
        internal int Calls;
        internal JsonObject? Last;
        public override Tool ProtocolTool { get; } = new() { Name = "SaveExportContent", InputSchema = JsonSerializer.SerializeToElement(
            new { type = "object", additionalProperties = false, required = new[] { "outputPath" },
                properties = new { outputPath = new { type = "string" } } }) };
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            Calls++; Last = JsonSerializer.SerializeToNode(request.Params!.Arguments)!.AsObject();
            var meta = new Meta(DateTimeOffset.UtcNow, "21", "SaveExportContent", "test", Outcome.Succeeded, Execution.Completed,
                false, BehaviorPolicy.NotApplicable, Completeness.Complete, null, Array.Empty<Warning>());
            var body = JsonNode.Parse(V4Json.Serialize(Envelope.Create(new { written = true }, null, meta)))!;
            return ValueTask.FromResult(new CallToolResult { IsError = false, StructuredContent = body,
                Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } });
        }
    }
}
