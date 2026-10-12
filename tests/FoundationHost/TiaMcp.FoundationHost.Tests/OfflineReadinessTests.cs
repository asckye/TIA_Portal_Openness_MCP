extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.ModelContextProtocol;
using TiaOpenness.Shared;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Pipeline = enginehost::TiaMcp.FoundationHost.EngineHostPipeline;
using Xunit;

[Collection(BundleSelectionCollection.Name)]
public sealed class OfflineReadinessTests
{
    public static IEnumerable<object[]> Cases =>
        new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
            .SelectMany(release => ToolUsageCatalog.ProfileEntries(release).OfType<JsonObject>()
                .Select(row => (string)row["currentName"]!).Distinct()
                .Where(name => TiaMcpServer.ModelContextProtocol.ToolMetadata.Find(name)?.Operation == "OFFLINE")
                .Select(name => new object[] { release, name }));

    // DecodePlcSimaticMl runs the plug-in ecosystem Python. Without that environment it must refuse with its own prerequisite
    // (IO_FAILED ecosystem-python) before any work - never with the TIA readiness refusal; with it, it must succeed.
    private static void AssertRanWithoutReadiness(string name, JsonNode? content)
    {
        Assert.NotEqual("RESOURCE_UNAVAILABLE", (string?)content?["error"]?["code"]);
        if (name == "DecodePlcSimaticMl" && !EcosystemPythonAvailable())
        {
            Assert.True((bool?)content?["ok"] == false, name + ": " + content);
            Assert.Equal("IO_FAILED", (string?)content?["error"]?["code"]);
            Assert.Equal("ecosystem-python", (string?)content?["error"]?["details"]?["operation"]);
            Assert.Equal("not-started", (string?)content?["meta"]?["execution"]);
            return;
        }
        Assert.True((bool?)content?["ok"] == true, name + ": " + content);
        Assert.Null(content?["error"]);
    }

    // Same resolution as DataLocations.EcosystemPython: the configured executable, else the per-user environment.
    private static bool EcosystemPythonAvailable()
    {
        string? configured = Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON");
        if (configured != null) return Path.IsPathRooted(configured) && File.Exists(configured);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return local.Length > 0 && File.Exists(Path.Combine(local, "TiaMcp", "ecosystem-python", "Scripts", "python.exe"));
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Every_registered_offline_tool_runs_without_SDK_or_readiness(string release, string name)
    {
        Assert.True(TiaMcpServer.ModelContextProtocol.ToolTaxonomy.IsSafeWithoutTia(name), name);
        Assert.DoesNotContain(typeof(Host.McpServer).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Siemens."));
        using var fixture = new Files();
        using var worker = new ForbiddenWorker();
        using var releaseScope = new ReleaseScope(release);
        var shared = LegacyHostToolRegistry.Create(worker, release, false, readinessProvider: Unready, hostPorts: false);
        using var pipeline = new Pipeline(release, worker, shared);
        var target = pipeline.AllTools.Single(tool => tool.ProtocolTool.Name == name);
        var usage = ToolUsageCatalog.Describe(name, release, release is "20" or "21" ? "full-engine" : "plc-foundation",
            target.ProtocolTool.Description!, JsonNode.Parse(target.ProtocolTool.InputSchema.GetRawText())!.AsObject());
        var arguments = usage["example"]!["request"]!["params"]!["arguments"]!.DeepClone().AsObject();
        fixture.Bind(name, arguments);
        var loadedBefore = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Siemens.")).ToArray();
        var result = await target.InvokeAsync(Request(name, arguments));
        AssertRanWithoutReadiness(name, result.StructuredContent);
        fixture.ResetOutput(arguments);
        var bridged = await pipeline.AllTools.Single(t => t.ProtocolTool.Name == "CallTool").InvokeAsync(
            Request("CallTool", new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() }));
        AssertRanWithoutReadiness(name, bridged.StructuredContent);
        Assert.Equal(0, worker.Calls);
        Assert.Equal(loadedBefore, AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Siemens.")).ToArray());
    }

    [Theory]
    [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")] [InlineData("17")]
    [InlineData("18")] [InlineData("19")] [InlineData("20")] [InlineData("21")]
    public async Task Project_tools_keep_the_environment_refusal(string release)
    {
        using var worker = new ForbiddenWorker();
        using var releaseScope = new ReleaseScope(release);
        var shared = LegacyHostToolRegistry.Create(worker, release, false, readinessProvider: Unready, hostPorts: false);
        using var pipeline = new Pipeline(release, worker, shared);
        var result = await pipeline.AllTools.Single(t => t.ProtocolTool.Name == "ListDevices").InvokeAsync(Request("ListDevices", new()));
        Assert.Equal("RESOURCE_UNAVAILABLE", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("tia-openness-environment", (string?)result.StructuredContent?["error"]?["details"]?["resource"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("PlanPlcExternalSourceImport")] [InlineData("RenderPlcBlockDocument")]
    public void Tools_with_project_read_paths_are_not_offline(string name)
    {
        Assert.Equal("READ", TiaMcpServer.ModelContextProtocol.ToolMetadata.Find(name)!.Operation);
        Assert.False(TiaMcpServer.ModelContextProtocol.ToolTaxonomy.IsSafeWithoutTia(name));
    }

    private static JsonObject Unready() => new() { ["ready"] = false, ["cause"] = "fixture: no SDK or TIA",
        ["recommendedFix"] = "fixture", ["recommendedFixZh"] = "fixture", ["environment"] = new JsonObject() };
    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject arguments)
        => new(DispatchProxy.Create<IMcpServer, ServerProxy>()) { Params = new() { Name = name,
            Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };

    private sealed class ReleaseScope : IDisposable
    {
        private readonly string previous = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        internal ReleaseScope(string release) => enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
        public void Dispose() => enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previous;
    }

    private sealed class ForbiddenWorker : IFoundationSessionWorker, enginehost::TiaMcp.FoundationHost.IEngineWorker
    {
        internal int Calls;
        public bool Bundled => true;
        public bool SharedSession => true;
        public bool Poisoned => false;
        public bool Faulted => false;
        public string ApprovalIdentity => "{}";
        public ImportStagingSession? StagingOwner => null;
        public JsonNode? Binding => null;
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new() { ["readiness"] = Unready() };
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Offline tool dispatched " + operation); }
        public Task<enginehost::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Offline tool dispatched " + name); }
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException();
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new Lane());
        public Task<IDisposable?> AcquireLane(CancellationToken token) => Task.FromResult<IDisposable?>(new Lane());
        public void ActivateLane(IDisposable? lane) { }
        public IDisposable? EnterRequest(RequestContext<CallToolRequestParams> request) => null;
        public void MarkUncertain() => throw new InvalidOperationException();
        public void Dispose() { }
        private sealed class Lane : IDisposable { public void Dispose() { } }
    }

    private sealed class Files : IDisposable
    {
        private const string BundleSelection = "TiaOpenness.Shared.BundleLayout.v4";
        private readonly object? previousBundle = AppDomain.CurrentDomain.GetData(BundleSelection);
        // BundleLayout.Initialize also sets this process-wide variable; restore it with the selection.
        private readonly string? previousRootVariable = Environment.GetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT");
        private readonly string root = Path.Combine(Path.GetTempPath(), "offline-readiness-" + Guid.NewGuid().ToString("N"));
        internal Files()
        {
            BundleLayout.Initialize(AppContext.BaseDirectory, TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "System")); Directory.CreateDirectory(Path.Combine(root, "XRef"));
            foreach (string file in new[] { "System/PEData.plf", "System/PEData.idx", "XRef/XRef.db" })
                File.WriteAllText(Path.Combine(root, file), "synthetic file-system fixture");
            File.WriteAllText(Path.Combine(root, "unified_screen.json"), """
                {"Screen":{"Name":"Overview","Width":800,"Height":480},"Items":[
                  {"Type":"Text","Name":"StatusText","Left":20,"Top":20,"Width":160,"Height":40,"Text":"Ready"}]}
                """);
            File.WriteAllText(Path.Combine(root, "report.json"), "{}");
            var block = TiaMcpServer.ModelContextProtocol.PlcBuilderToolJson.ComposeFcBlock("{\"blockName\":\"FC_Ready\",\"blockNumber\":10,\"inputs\":[],\"outputs\":[{\"name\":\"Ready\",\"datatype\":\"Bool\"}],\"structuredText\":{\"operations\":[{\"op\":\"assign\",\"target\":\"#Ready\",\"literalValue\":\"TRUE\"}]}}");
            Directory.CreateDirectory(Path.Combine(root, "exports"));
            File.WriteAllText(Path.Combine(root, "exports/FC_Ready.xml"), (string)block["xml"]!);
            File.WriteAllText(Path.Combine(root, "tags.xml"), """
                <Document><SW.Tags.PlcTagTable ID="0"><AttributeList><Name>Tags</Name></AttributeList><ObjectList>
                <SW.Tags.PlcTag ID="1"><AttributeList><Name>Ready</Name><DataTypeName>Bool</DataTypeName>
                <LogicalAddress>%M0.0</LogicalAddress></AttributeList></SW.Tags.PlcTag></ObjectList></SW.Tags.PlcTagTable></Document>
                """);
            File.WriteAllText(Path.Combine(root, "fragment.xml"), """
                <Document><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="Input"/></Sections></Interface></Document>
                """);
            Directory.CreateDirectory(Path.Combine(root, "schemas"));
            File.WriteAllText(Path.Combine(root, "schemas/SW.InterfaceSections_v5.xsd"), """
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" elementFormDefault="qualified">
                <xs:element name="Sections"><xs:complexType><xs:sequence><xs:element name="Section">
                <xs:complexType><xs:attribute name="Name" use="required"/></xs:complexType></xs:element>
                </xs:sequence></xs:complexType></xs:element></xs:schema>
                """);
        }
        internal void Bind(string name, JsonObject args)
        {
            foreach (string key in new[] { "libraryPath", "templateDirectory", "directory", "referenceProjectPath", "referenceGlobalLibraryPath" })
                if (args.ContainsKey(key)) args[key] = root;
            if (args.ContainsKey("templateFile")) args["templateFile"] = Path.Combine(root, "unified_screen.json");
            if (args.ContainsKey("offlineReleaseSuiteJsonPath")) args["offlineReleaseSuiteJsonPath"] = Path.Combine(root, "report.json");
            if (args.ContainsKey("outputPath")) args["outputPath"] = Path.Combine(root, name == "BuildDeviceAmlDocument" ? "output.aml" : "output.scl");
            if (name == "AnalyzePlcReferences")
            { args["directory"] = Path.Combine(root, "exports"); args["action"] = "summary"; args.Remove("target"); }
            if (name == "BuildPlcSymbolManifestFromPath")
            { args["inputRoot"] = root; args["files"] = new JsonArray("tags.xml"); }
            if (name == "ValidatePlcDocumentSchemas")
            { args["filePath"] = Path.Combine(root, "fragment.xml"); args["schemaDirectory"] = Path.Combine(root, "schemas"); }
            if (name == "DecodePlcSimaticMl")
            {
                var built = TiaMcpServer.ModelContextProtocol.PlcBuilderToolJson.ComposeFcBlock("""
                    {"blockName":"FC_Ready","blockNumber":10,"inputs":[],"outputs":[{"name":"Ready","datatype":"Bool"}],
                     "structuredText":{"operations":[{"op":"assign","target":"#Ready","literalValue":"TRUE"}]}}
                    """);
                string path = Path.Combine(root, "decode.xml"); File.WriteAllText(path, (string)built["xml"]!); args["filePath"] = path;
            }
            if (name == "ValidateStandardPackage")
            { args.Clear(); args["packageId"] = "tiamcp.basic"; args["version"] = "1.0.0"; }
        }
        internal void ResetOutput(JsonObject args)
        {
            if (args["outputPath"] is JsonValue value && value.TryGetValue<string>(out var path) && File.Exists(path))
                File.Delete(path);
        }
        public void Dispose()
        {
            AppDomain.CurrentDomain.SetData(BundleSelection, previousBundle);
            Environment.SetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT", previousRootVariable);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
