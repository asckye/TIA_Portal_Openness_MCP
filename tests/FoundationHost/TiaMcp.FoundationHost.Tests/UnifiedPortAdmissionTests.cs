extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Xunit;

// The fixture mutates process-wide release and approval state; run it alone.
[Collection(BundleSelectionCollection.Name)]
public sealed class UnifiedPortAdmissionTests
{
    private static MethodInfo Method(string name) => Host.PortedToolDeclarations.Methods("21")
        .Single(method => method.GetCustomAttribute<McpServerToolAttribute>()!.Name == name);
    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject arguments)
        => new(DispatchProxy.Create<IMcpServer, ServerProxy>()) { Params = new() { Name = name,
            Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };
    private static JsonObject Arguments(string name)
    {
        var root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(Path.GetDirectoryName(typeof(UnifiedPortAdmissionTests).Assembly.Location)!);
        var source = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference/tool-examples/calls.json")))!;
        return source["profiles"]!["full-engine"]![name]!["arguments"]!.DeepClone().AsObject();
    }

    [Theory]
    [InlineData("19", "GetUnifiedAuditSettings", "alarmAuditClasses", "", "")]
    [InlineData("19", "ListUnifiedEngineeringObjects", "textLists", "", "")]
    [InlineData("19", "ManageUnifiedDynamization", "", "create", "Expression")]
    [InlineData("19", "ManageUnifiedDynamization", "", "update", "TagParameter")]
    [InlineData("20", "ExportUnifiedEngineeringList", "graphicLists", "", "")]
    [InlineData("21", "ManageUnifiedListEntries", "textLists", "create", "")]
    public async Task Missing_action_is_refused_by_the_served_host_before_any_worker_call(
        string release, string name, string category, string action, string kind)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == name);
        var arguments = Arguments(name);
        if (category.Length != 0) arguments["category"] = category;
        if (action.Length != 0) arguments["action"] = action;
        if (kind.Length != 0) arguments["dynamizationKind"] = kind;
        var result = await tool.InvokeAsync(Request(name, arguments));
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(0, worker.Calls);
    }

    public static IEnumerable<object[]> MappingCases => from family in PortedFamilies.All.Where(f => f.Name is "F27" or "F28")
        from name in family.Tools select new object[] { name };
    [Theory, MemberData(nameof(MappingCases))]
    public void Every_served_parameter_has_an_explicit_request_mapping(string name)
    {
        var method = Method(name);
        foreach (var parameter in method.GetParameters())
        {
            JsonElement input = parameter.ParameterType == typeof(bool) ? JsonSerializer.SerializeToElement(false)
                : parameter.ParameterType == typeof(int) || parameter.ParameterType == typeof(uint) ? JsonSerializer.SerializeToElement(7)
                : parameter.ParameterType == typeof(string) ? JsonSerializer.SerializeToElement("fixture")
                : JsonSerializer.SerializeToElement(parameter.ParameterType.IsArray ? (object)Array.Empty<object>() : new Dictionary<string, object>());
            var request = FoundationTool.BindUnifiedRequest(name, method, new Dictionary<string, JsonElement> { [parameter.Name!] = input });
            var paged = name is "GetUnifiedGraphicSelection" or "ListUnifiedGlobalScripts" or "GetUnifiedGlobalScript"
                or "ListUnifiedTagDefinitions" or "GetUnifiedScreenBranch" or "GetUnifiedLibraryType" or "ListUnifiedLibraryFolderEntries" or "GetUnifiedFaceplateInstance";
            var expected = parameter.Name == "previewToken" ? "ExpectedToken" : parameter.Name == "limit" && paged ? "PageSize"
                : char.ToUpperInvariant(parameter.Name![0]) + parameter.Name[1..] + (parameter.ParameterType != typeof(string)
                    && parameter.ParameterType != typeof(bool) && parameter.ParameterType != typeof(int) && parameter.ParameterType != typeof(uint) ? "Json" : "");
            var value = typeof(UnifiedRequest).GetProperty(expected)!.GetValue(request);
            Assert.Equal(input.ValueKind == JsonValueKind.String ? input.GetString() : parameter.ParameterType == typeof(int) ? (object)7
                : parameter.ParameterType == typeof(uint) ? 7u : parameter.ParameterType == typeof(bool) ? false : input.GetRawText(), value);
        }
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Duplicate_runtime_setting_keys_are_rejected_before_native_access(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == "SetUnifiedRuntimeSettings");
        var request = Request("SetUnifiedRuntimeSettings", Arguments("SetUnifiedRuntimeSettings"));
        using var document = JsonDocument.Parse("{\"StartScreen\":\"A\",\"StartScreen\":\"B\"}");
        var arguments = new Dictionary<string, JsonElement>(request.Params!.Arguments!);
        arguments["changes"] = document.RootElement.Clone();
        var duplicateRequest = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>())
            { Params = new() { Name = "SetUnifiedRuntimeSettings", Arguments = arguments } };
        var result = await tool.InvokeAsync(duplicateRequest);
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(0, worker.Calls);
    }
    public static IEnumerable<object[]> ApprovalCases
    {
        get
        {
            foreach (var release in new[] { "19", "20", "21" })
            foreach (var family in PortedFamilies.All.Where(f => f.Name is "F27" or "F28"))
            foreach (var name in family.Tools.Where(n => PortedFamilies.Available(release, n)))
            {
                bool classifiedWrite = Method(name).GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description.Contains("[WRITE]", StringComparison.Ordinal);
                if (!classifiedWrite && name is not ("ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules" or "ImportUnifiedOpcUaAlarms" or "ExportUnifiedEngineeringList")) continue;
                foreach (var preview in new[] { true, false })
                foreach (var action in name is "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules" ? new[] { "import", "export" } : new[] { "read" })
                    yield return new object[] { release, name, preview, action, !preview && (classifiedWrite || name == "ImportUnifiedOpcUaAlarms" || action == "import") && name != "ExportUnifiedEngineeringList" };
            }
        }
    }

    [Theory, MemberData(nameof(ApprovalCases))]
    public async Task Foundation_unified_approval_follows_preview_and_native_effects(
        string release, string name, bool preview, string action, bool gated)
    {
        using var worker = new ApprovalWorker(); int waits = 0;
        using var releaseState = new ReleaseState(release);
        using var lifetime = new Host.ImportStagingHostLifetime();
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        var catalog = new ApprovalCatalog(release);
        var invoker = new enginehost::TiaMcp.FoundationHost.WorkerToolInvoker(catalog, worker, lifetime, tools);
        using var context = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
            new enginehost::TiaMcp.FoundationHost.EngineSessionContext(worker) { Catalog = catalog, Invoker = invoker });
        var previousWait = Host.McpServer.ApprovalWaitOverrideForTests;
        var previousSettings = TiaOpenness.Shared.ApprovalSettings.Load(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
        new TiaOpenness.Shared.ApprovalSettings(true, 1).Save(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
        Task<TiaOpenness.Shared.ApprovalOutcome> Wait(TiaOpenness.Shared.PendingApproval pending,
            TiaOpenness.Shared.ApprovalSettings settings, CancellationToken token)
        {
            waits++;
            Assert.Equal(0, worker.Applies);
            return Task.FromResult(new TiaOpenness.Shared.ApprovalOutcome(pending, false, "denied"));
        }
        Host.McpServer.ApprovalWaitOverrideForTests = Wait;
        // Foundation releases use the production Foundation wrapper; full-engine
        // releases use its shared serialized approval boundary.
        var tool = release == "19"
            ? new FoundationV4Tool(PortedToolContract.Create(worker, release).Single(t => t.ProtocolTool.Name == name),
                release, null, approvalWait: Wait)
            : tools.Single(t => t.ProtocolTool.Name == name);
        var arguments = Arguments(name); arguments["dryRun"] = preview;
        if (name is "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules" or "ImportUnifiedOpcUaAlarms") arguments["action"] = action;
        if (release == "19" && name == "ManageUnifiedEngineeringObject") arguments["category"] = "alarmClasses";
        var directory = Path.Combine(AppContext.BaseDirectory, "unified-approval", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "input.xml"); File.WriteAllText(input, "<Document />");
        foreach (var field in new[] { "filePath", "xmlPath" })
            if (arguments.ContainsKey(field)) arguments[field] = input;
        if (arguments.ContainsKey("directory")) arguments["directory"] = directory;
        if (name == "ExchangeUnifiedScriptModules")
        {
            var script = Path.Combine(directory, arguments["fileName"]!.GetValue<string>());
            File.WriteAllText(script, "export function Fixture() { return 1; }");
            File.WriteAllText(Path.ChangeExtension(script, ".yml"), "Name: Status\n");
        }
        if (arguments.ContainsKey("destinationDirectory")) arguments["destinationDirectory"] = Path.Combine(directory, "new-output");
        try
        {
            var result = await tool.InvokeAsync(Request(name, arguments));
            Assert.Equal(gated ? 1 : 0, waits);
            if (gated)
            {
                Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
                Assert.Equal("rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
                Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
                Assert.Equal(0, worker.Applies);
            }
            else
            {
                Assert.NotEqual("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
                Assert.True(worker.Calls > 0);
            }
        }
        finally
        {
            Host.McpServer.ApprovalWaitOverrideForTests = previousWait;
            previousSettings.Save(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
            Assert.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(directory, true);
        }
    }

    private sealed class ReleaseState : IDisposable
    {
        private readonly string previousRelease;
        internal ReleaseState(string release)
        {
            previousRelease = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
        }
        public void Dispose() => enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previousRelease;
    }

    private sealed class ApprovalCatalog : Host.IToolCatalogView
    {
        public IReadOnlyDictionary<string, Host.ToolDescriptor> All { get; }
        public IReadOnlyDictionary<string, Host.ToolDescriptor> IncludingUnavailable => All;
        public IReadOnlyList<Host.ToolDescriptor> Lite => All.Values.ToArray();
        public JsonArray BehaviorCapabilities => new();
        public Host.ToolDescriptor? Find(string name, bool includeUnavailable = false) => All.GetValueOrDefault(name);
        internal ApprovalCatalog(string release)
        {
            var descriptors = new Dictionary<string, Host.ToolDescriptor>();
            foreach (var method in Host.PortedToolDeclarations.Methods(release))
            {
                var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
                if (!PortedToolContract.Families(release).Any(f => f.Tools.Contains(name))) continue;
                Host.DeclaredToolMetadata.Create(name, method, _ => throw new InvalidOperationException("Metadata only."), out var descriptor);
                descriptors.Add(name, new Host.ToolDescriptor(descriptor.Name, descriptor.RawDescription, descriptor.Tool, descriptor.Classification,
                    descriptor.Signature, descriptor.Parameters, descriptor.DryRun, descriptor.CandidateFamily, "foundation"));
            }
            All = descriptors;
        }
    }

    private sealed class ApprovalWorker : IFoundationWorker, enginehost::TiaMcp.FoundationHost.IEngineWorker
    {
        internal int Calls, Applies;
        public bool Faulted => false;
        public JsonNode? Binding => null;
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = true } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException("No restart expected.");
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new Lane());
        public Task<enginehost::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
            => throw new InvalidOperationException("No engine dispatch expected.");
        private sealed class Lane : IDisposable { public void Dispose() { } }
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Calls++;
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject { ["ProjectFile"] = "C:/fixture.ap21", ["IsAttached"] = true });
            if ((bool?)arguments["dryRun"] == false) Applies++;
            return Task.FromResult<JsonNode?>(new JsonObject { ["Value"] = new JsonObject { ["Message"] = "fixture", ["Meta"] = new JsonObject {
                ["success"] = true, ["operationSuccess"] = true, ["apiCallSuccess"] = true,
                ["dataComplete"] = true, ["verificationSuccess"] = true, ["mayHaveChanged"] = false } } });
        }
        public void Dispose() { }
    }
}
