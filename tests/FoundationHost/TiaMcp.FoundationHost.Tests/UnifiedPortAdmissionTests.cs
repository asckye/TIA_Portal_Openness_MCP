extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.FoundationHost;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Xunit;

// The fixture mutates process-wide release and approval state; run it alone.
[Collection(BundleSelectionCollection.Name)]
public sealed class UnifiedPortAdmissionTests
{
    [Fact]
    public void Refusal_examples_use_Unified_roots_and_keep_optional_domains_separate()
    {
        var path = JsonNode.Parse(EngineeringInputValidation.ObjectPathExample)!.AsArray();
        Assert.Equal("Screens", (string?)path[0]!["property"]);
        Assert.Equal("Overview", (string?)path[0]!["name"]);
        Assert.Equal("ScreenItems", (string?)path[1]!["property"]);
        Assert.Equal("StartButton", (string?)path[1]!["name"]);
        enginehost::TiaMcpServer.Siemens.Services.UnifiedPortService.ValidateAddress(EngineeringInputValidation.ObjectPathExample);
        var unified = Assert.Throws<ArgumentException>(() => enginehost::TiaMcpServer.Siemens.Services.UnifiedPortService.ValidateAddress("{}"));
        Assert.Contains(EngineeringInputValidation.ObjectPathExample, unified.Message);
        Assert.DoesNotContain("ScreenFolder", unified.Message);
        Assert.DoesNotContain("TagFolder", unified.Message);
        enginehost::TiaMcpServer.Siemens.Services.UnifiedPortService.ValidateAddress(EngineeringInputValidation.OptionalObjectPathExample, optional: true);
        var optional = Assert.Throws<ArgumentException>(() => enginehost::TiaMcpServer.Siemens.Services.UnifiedPortService.ValidateAddress("{}", optional: true));
        Assert.Contains("[] (the typed DCC/SiVArc root)", optional.Message);
        Assert.DoesNotContain("Screens", optional.Message);
    }

    [Fact]
    public void Malformed_branch_JSON_uses_the_branch_grammar_in_its_refusal_example()
    {
        enginehost::TiaMcpServer.Siemens.Services.UnifiedPortService.ValidateAddress(EngineeringInputValidation.BranchPathExample, branch: true);
        var error = Assert.Throws<ArgumentException>(() => enginehost::TiaMcpServer.Siemens.Services.UnifiedPortService.ValidateAddress("{", branch: true));
        Assert.Contains(EngineeringInputValidation.BranchPathExample, error.Message);
        Assert.DoesNotContain(EngineeringInputValidation.ObjectPathExample, error.Message);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Backslash_HMI_paths_are_rejected_before_any_worker_call(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        foreach (var name in new[] { "EnsureUnifiedHmiTagTable", "EnsureUnifiedHmiTag", "EnsureUnifiedHmiScreen" })
        foreach (var path in new[] { "Group\\Sub\\Name", "/Group\\Sub/Name" })
        {
            var arguments = Arguments(name);
            arguments[name == "EnsureUnifiedHmiScreen" ? "screenName" : "tagTableName"] = path;
            var result = await tools.Single(t => t.ProtocolTool.Name == name).InvokeAsync(Request(name, arguments));
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
            Assert.Contains("Corrected slash form: /Group/Sub/Name", (string)result.StructuredContent!["error"]!["message"]!);
        }
        Assert.Equal(0, worker.Calls);
    }

    private static readonly Type[] AddressServices = { typeof(enginehost::TiaMcpServer.Siemens.Services.UnifiedUiModelService), typeof(enginehost::TiaMcpServer.Siemens.Services.UnifiedObjectServicesService),
        typeof(enginehost::TiaMcpServer.Siemens.Services.UnifiedEventsService), typeof(enginehost::TiaMcpServer.Siemens.Services.MigrationReadService), typeof(enginehost::TiaMcpServer.Siemens.Services.DccService), typeof(enginehost::TiaMcpServer.Siemens.Services.OptionalEngineeringService) };
    private static readonly string[] AddressParameters = { "objectPathJson", "branchJson", "collectionPathJson", "tagPathJson", "durationPathJson" };
    public static IEnumerable<object[]> ServiceAddressCases => from type in AddressServices
        from method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        from parameter in method.GetParameters().Where(p => AddressParameters.Contains(p.Name))
        from raw in new[] { "[\"Screens\",\"Main\"]", "{}", "[{\"property\":\"Screens\",\"unknown\":true}]", "[{\"property\":\"Parent\"}]" }
        select new object[] { type, method.Name, parameter.Name!, raw };

    [Theory, MemberData(nameof(ServiceAddressCases))]
    public void Every_service_rejects_malformed_raw_addresses_before_worker_or_identity_lookup(Type type, string methodName, string parameter, string raw)
    {
        int calls = 0;
        var service = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new object[] { (Func<string, JsonObject, JsonNode?>)((_, _) => { calls++; throw new InvalidOperationException(); }),
                (Func<string>)(() => { calls++; throw new InvalidOperationException(); }) }, null)!;
        var method = type.GetMethod(methodName)!;
        var arguments = method.GetParameters().Select(p => p.Name == parameter ? raw
            : AddressParameters.Contains(p.Name) ? (object)"[]"
            : p.Name == "propertiesJson" || p.Name == "scriptPropertiesJson" ? "{}"
            : p.Name == "action" ? "delete" : p.HasDefaultValue ? p.DefaultValue
            : p.ParameterType == typeof(string) ? "fixture" : Activator.CreateInstance(p.ParameterType)).ToArray();
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(service, arguments));
        var invalid = Assert.IsType<ArgumentException>(error.InnerException);
        Assert.Contains("Expected an array", invalid.Message);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"Width\":1,\"Width\":2}")]
    public void Properties_are_validated_before_dispatch_even_for_delete_preview(string properties)
    {
        int calls = 0;
        var service = new enginehost::TiaMcpServer.Siemens.Services.UnifiedUiModelService((_, _) => { calls++; throw new InvalidOperationException(); }, () => "fixture.ap21");
        Assert.Throws<ArgumentException>(() => service.ManageUnifiedObjectParts("HMI", "[]", "delete", propertiesJson: properties));
        Assert.Throws<ArgumentException>(() => service.ManageUnifiedDynamization("HMI", "[]", "Visible", "delete", propertiesJson: properties));
        Assert.Throws<ArgumentException>(() => service.ManageUnifiedScreenLayout("HMI", "[]", "delete", propertiesJson: properties));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("[]", false)]
    [InlineData("[{\"property\":\"Screens\",\"name\":\"Overview\"}]", false)]
    [InlineData("[{\"property\":\"ScreenItems\"},{\"index\":0},{\"attribute\":\"Display name\"}]", true)]
    [InlineData("[{\"name\":\"Overview\",\"key\":\"Name\"}]", true)]
    public void Address_preflight_preserves_the_existing_object_and_branch_grammars(string raw, bool branch)
        => enginehost::TiaMcpServer.Siemens.Services.UnifiedPortService.ValidateAddress(raw, branch);


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_ensure_tools_dispatch_an_absolute_table_path_to_the_nested_collection(bool ensureTag)
    {
        var nested = new TagFolder();
        var root = new TagFolder { TagTableGroups = new[] { new TagFolder { Name = "Hmi Tags", Groups = new[] { nested } } } };
        nested.Name = "Diagnostic";
        int writes = 0, calls = 0;
        JsonNode? Call(string operation, JsonObject arguments)
        {
            calls++;
            var request = arguments["request"]!.Deserialize<UnifiedHmiRequest>()!;
            Assert.Equal(ensureTag ? "EnsureUnifiedHmiTag" : "EnsureUnifiedHmiTagTable", request.Operation);
            var table = HmiExchangePolicy.EnsureTagTable(root, request.TagTableName, () => writes++);
            if (ensureTag) HmiExchangePolicy.EnsureTag(root, table, request.TagName, () => writes++);
            return new JsonObject { ["Value"] = new JsonObject { ["Message"] = "fixture" } };
        }
        var service = new enginehost::TiaMcpServer.Siemens.Services.UnifiedHmiService(Call, () => "fixture.ap21");
        for (int i = 0; i < 2; i++)
        {
            if (ensureTag) service.EnsureUnifiedHmiTag("HMI", "/Hmi%20Tags/Diagnostic/Diag%20Info", "Ready");
            else service.EnsureUnifiedHmiTagTable("HMI", "/Hmi%20Tags/Diagnostic/Diag%20Info");
        }
        Assert.Equal(2, calls);
        Assert.Equal(ensureTag ? 2 : 1, writes);
        Assert.Equal(new[] { "Diag Info" }, nested.TagTables.Calls);
        Assert.Empty(root.TagTables.Calls);
        Assert.Same(nested.TagTables.Single(), HmiExchangePolicy.TagTable(root, "/Hmi%20Tags/Diagnostic/Diag%20Info"));
    }

    [Theory]
    [InlineData("Group/Table")]
    [InlineData("/Group/Table/")]
    [InlineData("/Group//Table")]
    [InlineData("/")]
    public void Both_ensure_tools_refuse_invalid_paths_before_dispatch(string path)
    {
        int calls = 0;
        var service = new enginehost::TiaMcpServer.Siemens.Services.UnifiedHmiService((_, _) => { calls++; throw new InvalidOperationException(); },
            () => { calls++; throw new InvalidOperationException(); });
        Assert.Throws<ArgumentException>(() => service.EnsureUnifiedHmiTagTable("HMI", path));
        Assert.Throws<ArgumentException>(() => service.EnsureUnifiedHmiTag("HMI", path, "Ready"));
        Assert.Equal(0, calls);
    }

    public sealed class NamedTagTable { public string Name { get; set; } = ""; }
    public sealed class TagTables : List<NamedTagTable>
    {
        public List<string> Calls { get; } = new();
        public NamedTagTable Create(string name) { Calls.Add(name); var table = new NamedTagTable { Name = name }; Add(table); return table; }
    }
    public sealed class TagValue { public string Name { get; set; } = ""; public string TagTableName { get; set; } = ""; }
    public sealed class Tags : List<TagValue>
    {
        public TagValue Create(string name, string table) { var tag = new TagValue { Name = name, TagTableName = table }; Add(tag); return tag; }
    }
    public sealed class TagFolder
    {
        public string Name { get; set; } = "";
        public TagFolder[] TagTableGroups { get; set; } = Array.Empty<TagFolder>();
        public TagFolder[] Groups { get; set; } = Array.Empty<TagFolder>();
        public TagTables TagTables { get; } = new();
        public Tags Tags { get; } = new();
    }

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
    [InlineData("EnsureUnifiedHmiTagTable")]
    [InlineData("EnsureUnifiedHmiTag")]
    public void Table_parameter_descriptions_and_examples_explain_the_absolute_form(string name)
    {
        var parameter = Method(name).GetParameters().Single(p => p.Name == "tagTableName");
        var description = parameter.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description;
        Assert.Contains("/Group/Sub/Table", description);
        Assert.Contains("must already exist", description);
        Assert.Equal("/HmiTags/Diagnostic/DiagInfo", (string?)Arguments(name)["tagTableName"]);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Ensure_tools_reject_invalid_slash_names_before_readiness_or_worker_calls(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        foreach (var name in new[] { "EnsureUnifiedHmiTagTable", "EnsureUnifiedHmiTag", "EnsureUnifiedHmiScreen" })
        foreach (var path in new[] { "Group/Table", "/Group/Table/", "/Group//Table", "/" + string.Join("/", Enumerable.Repeat("G", 65)) + "/Table" })
        {
            var tool = tools.Single(t => t.ProtocolTool.Name == name);
            var arguments = Arguments(name);
            arguments[name == "EnsureUnifiedHmiScreen" ? "screenName" : "tagTableName"] = path;
            var result = await tool.InvokeAsync(Request(name, arguments));
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
            Assert.False((bool)result.StructuredContent!["meta"]!["requiresSessionReset"]!);
            Assert.Contains("/Group/Sub/Table", (string)result.StructuredContent!["error"]!["message"]!);
        }
        Assert.Equal(0, worker.Calls);
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

    public static IEnumerable<object[]> MalformedAddresses
    {
        get
        {
            foreach (var release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
            foreach (var method in Host.PortedToolDeclarations.Methods(release))
            {
                var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
                if (!PortedFamilies.Available(release, name)) continue;
                foreach (var parameter in method.GetParameters().Where(p => p.ParameterType.IsArray
                    && p.Name is "objectPath" or "branch" or "collectionPath" or "tagPath" or "durationPath"))
                foreach (var path in new[] { "[\"Screens\",\"Main\"]", "{}", "[{\"property\":\"Screens\",\"unknown\":true}]", "[{\"property\":\"Parent\"}]" })
                foreach (bool encoded in new[] { false, true })
                    yield return new object[] { release, name, parameter.Name!, path, encoded };
            }
        }
    }

    [Theory, MemberData(nameof(MalformedAddresses))]
    public async Task Every_registered_address_tool_refuses_malformed_paths_before_any_worker_call(
        string release, string name, string parameter, string path, bool encoded)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == name);
        var arguments = Arguments(name);
        arguments[parameter] = encoded ? JsonValue.Create(path) : JsonNode.Parse(path);
        var schema = tool.ProtocolTool.InputSchema.GetProperty("properties");
        if (schema.TryGetProperty("action", out _)) arguments["action"] = "delete";
        if (schema.TryGetProperty("dryRun", out _)) arguments["dryRun"] = true;
        if (schema.TryGetProperty("confirmDelete", out _)) arguments["confirmDelete"] = false;
        var result = await tool.InvokeAsync(Request(name, arguments));
        var body = result.StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal(parameter, (string?)body["error"]?["details"]?["parameter"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.False((bool)body["meta"]!["requiresSessionReset"]!);
        var message = (string)body["error"]!["message"]!;
        Assert.Contains("Expected an array", message);
        Assert.Contains("\"property\"", message);
        if (path.StartsWith("[", StringComparison.Ordinal)) Assert.Contains("step 0", message);
        if (path.StartsWith("[\"", StringComparison.Ordinal)) Assert.Contains("is a string", message);
        Assert.Equal(0, worker.Calls);
    }
    [Theory]
    [InlineData("20", "GetUnifiedObjectEvents")]
    [InlineData("21", "GetUnifiedPlantObject")]
    [InlineData("20", "GetUnifiedScreenBranch")]
    [InlineData("21", "ValidateUnifiedObject")]
    public void Read_address_tools_do_not_require_approval(string release, string name)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        Assert.False(Host.McpServer.ApprovalWrite(name, Arguments(name).ToJsonString()));
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("20", "GetDccObject")]
    [InlineData("21", "GetDccObject")]
    [InlineData("20", "ListSivarcRules")]
    [InlineData("21", "ListSivarcRules")]
    public async Task Optional_root_addresses_keep_their_array_type_during_capability_admission(string release, string name)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tool = PortedToolContract.Create(worker, release).OfType<FoundationTool>().Single(t => t.ProtocolTool.Name == name);
        var arguments = Arguments(name); arguments["objectPath"] = new JsonArray();
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString())!;
        Assert.Null(tool.RefusePortedCapability(values));
        var result = await new FoundationV4Tool(tool, release).InvokeAsync(Request(name, arguments));
        Assert.Equal("PRECONDITION_FAILED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(1, worker.Calls);
    }

    [Theory]
    [InlineData("19", "ManageUnifiedObjectParts", "objectPath")]
    [InlineData("20", "ManageUnifiedObjectParts", "objectPath")]
    [InlineData("21", "SetUnifiedPlantObject", "objectPath")]
    [InlineData("20", "GetUnifiedObjectEvents", "objectPath")]
    [InlineData("21", "GetUnifiedScreenBranch", "branch")]
    [InlineData("21", "ManageSivarcRule", "collectionPath")]
    public void Bridge_binding_refuses_malformed_addresses_before_approval_or_dispatch(string release, string name, string parameter)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var selected = new ReleaseState(release);
        using var lifetime = new Host.ImportStagingHostLifetime();
        var invoker = new enginehost::TiaMcp.FoundationHost.WorkerToolInvoker(new ApprovalCatalog(release), worker, lifetime);
        var arguments = Arguments(name);
        arguments[parameter] = JsonNode.Parse("[{\"property\":\"Parent\"}]");
        var error = invoker.Bind(name, new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.SerializeToElement(arguments)), out var call);
        Assert.NotNull(error);
        Assert.Equal(TiaMcp.Logic.V4.ErrorCode.InvalidArgument, error!.Code);
        Assert.Equal(parameter, Assert.IsType<TiaMcp.Logic.V4.InvalidArgumentDetails>(error.Details).Parameter);
        Assert.Contains("Expected an array", error.Message);
        Assert.Null(call);
        Assert.Equal(0, worker.Calls);
    }

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
