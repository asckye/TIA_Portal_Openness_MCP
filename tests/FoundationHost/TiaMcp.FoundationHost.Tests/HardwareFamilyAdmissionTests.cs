extern alias toolpipeline;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Xunit;

public sealed class HardwareFamilyAdmissionTests
{
    [Theory]
    [InlineData("18", false, 0)]
    [InlineData("21", false, 0)]
    [InlineData("17", true, 0)]
    [InlineData("18", true, 1)]
    public void Installed_gsd_catalog_lookup_requires_a_portal_and_catalog_api(string release, bool attached, int expectedReads)
    {
        var portal = attached ? new object() : null;
        var catalog = new object();
        int reads = 0;
        var result = TiaMcp.Adapters.PlcHardwareCatalogPolicy.ReadInstalledCatalog(release, portal, source => {
            reads++;
            Assert.Same(portal, source);
            return catalog;
        });
        Assert.Equal(expectedReads, reads);
        Assert.Same(expectedReads == 0 ? null : catalog, result);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(22, 50)]
    [InlineData(50, 50)]
    [InlineData(72, 50)]
    [InlineData(72, 65)]
    public void Gsd_search_reports_the_matching_total_and_requested_page(int total, int limit)
    {
        using var release = toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareContract.UseRelease("21");
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareDevicesService((operation, args) => {
            Assert.Equal("hardware-devices.HardwareLegacySearchInstalledGsdDevices", operation);
            Assert.Equal(limit, (int?)args["limit"]);
            var matches = Enumerable.Range(0, total).Select(i => new DeviceCreationRules.GsdMatch {
                TypeIdentifier = "GSD:FIXTURE.XML/DAP/" + i, DapId = i.ToString() }).ToList();
            var rows = JsonNode.Parse(TiaMcp.PlcWorker.WorkerJson.Serialize(DeviceCreationRules.GsdPage(matches, "FIXTURE", limit)))!.AsArray();
            WorkerProtocol.ValidateExchangeResult(operation, args, rows);
            return rows;
        }, () => true, () => "fixture.ap21");
        var tools = new toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareDevicesTools(service);
        var result = limit == 50 ? tools.SearchInstalledGsdDevicesV4("FIXTURE") : tools.SearchInstalledGsdDevicesV4("FIXTURE", limit);
        var data = result.StructuredContent!["data"]!;
        Assert.Equal(Math.Min(total, limit), data["items"]!.AsArray().Count);
        Assert.Equal(total, (int?)data["evidence"]?["total"]);
        Assert.Equal(total > limit, (bool?)data["evidence"]?["truncated"]);
    }

    [Fact]
    public void Gsd_search_worker_row_is_a_discoverable_hardware_contract()
    {
        const string operation = "hardware-devices.HardwareLegacySearchInstalledGsdDevices";
        string returnType = "System.Collections.Generic.List<" + typeof(DeviceCreationRules.GsdMatch).FullName + ">";
        var (args, reply) = Assert.Single(TiaMcp.FoundationHost.Tests.FamilyReplyShapes.For(operation, returnType));
        WorkerProtocol.ValidateExchangeResult(operation, args, reply);
        Assert.NotNull(reply![0]!["TotalMatches"]);
    }

    [Theory]
    [InlineData(50, 72)]
    [InlineData(65, 72)]
    [InlineData(3, 2)]
    public void Gsd_create_relays_its_page_limit_and_reports_truncation(int limit, int total)
    {
        using var selected = toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareContract.UseRelease("21");
        using var worker = new CreationPreviewWorker();
        using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        const string exact = "GSD:FIXTURE.XML/DAP/DAP1";
        int calls = 0;
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareDevicesService((operation, args) => {
            calls++;
            Assert.Equal("hardware-devices.HardwareCreateGsdDevice", operation);
            Assert.Equal(limit, (int?)args["limit"]);
            Assert.Equal(exact, (string?)args["keyword"]);
            var rows = new JsonArray();
            for (int i = 1; i <= Math.Min(total, limit); i++)
                rows.Add(new JsonObject { ["TypeIdentifier"] = "GSD:FIXTURE.XML/DAP/DAP" + i, ["TotalMatches"] = total });
            var reply = new JsonObject { ["Device"] = new JsonObject { ["Name"] = "ENC_1" }, ["Candidate"] = rows[0]!.DeepClone(),
                ["Candidates"] = rows, ["Attempts"] = new JsonArray("created"), ["Error"] = null };
            WorkerProtocol.ValidateExchangeResult(operation, args, reply);
            return reply;
        }, () => true, () => "C:/Fixture/Fixture.ap21");
        var tools = new toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareDevicesTools(service);
        var preview = limit == 50 ? tools.CreateGsdDeviceV4(exact, "ENC_1") : tools.CreateGsdDeviceV4(exact, "ENC_1", limit: limit);
        string hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
        string project = (string)preview.StructuredContent["data"]!["targetIdentity"]!["identity"]!["projectPath"]!;
        var result = limit == 50 ? tools.CreateGsdDeviceV4(exact, "ENC_1", dryRun: false, expectedPlanHash: hash, expectedProjectFile: project)
            : tools.CreateGsdDeviceV4(exact, "ENC_1", dryRun: false, expectedPlanHash: hash, expectedProjectFile: project, limit: limit);
        var body = result.StructuredContent!;
        Assert.True((bool)body["ok"]!, body.ToJsonString());
        Assert.Equal(1, calls);
        Assert.Equal(Math.Min(total, limit), body["data"]!["candidates"]!.AsArray().Count);
        Assert.Equal(total, (int?)body["data"]!["evidence"]?["total"]);
        Assert.Equal(total > limit, (bool?)body["data"]!["evidence"]?["truncated"]);
    }

    [Fact]
    public void Published_gsd_description_explains_return_and_traversal_limits()
    {
        var method = typeof(toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareDevicesTools).GetMethod("SearchInstalledGsdDevicesV4")!;
        string text = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description;
        Assert.Contains("The limit bounds returned candidates. Truncated results include the total match count.", text, StringComparison.Ordinal);
        Assert.Contains("synchronous native Find run to completion.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Published_gsd_create_accepts_the_page_size()
    {
        using var worker = new OnlinePortAdmissionTests.OnlineWorker();
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21", worker: worker);
        using var session = fixture.EnterSession();
        var result = await fixture.Call("CreateGsdDevice", new { keyword = "GSD:FIXTURE.XML/DAP/1", deviceName = "ENC_1", limit = 65 });
        var body = result.StructuredContent!;
        Assert.True((bool)body["ok"]!, body.ToJsonString());
        Assert.NotNull(body["data"]?["planHash"]);
        Assert.Equal(65, (int?)body["data"]?["plannedChanges"]?["limit"]);
        Assert.Empty(worker.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_gsd_limits_are_refused_before_search_or_creation(int limit)
    {
        using var worker = new CreationPreviewWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareDevicesService((_, _) =>
            throw new InvalidOperationException("Unexpected worker call"), () => true, () => "fixture.ap21");
        var tools = new toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareDevicesTools(service);
        foreach (var result in new[] { tools.SearchInstalledGsdDevicesV4("FIXTURE", limit), tools.CreateGsdDeviceV4("FIXTURE", "ENC_1", limit: limit) })
        {
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent!["error"]?["code"]);
            Assert.Equal("/limit", (string?)result.StructuredContent["error"]?["details"]?["parameter"]);
        }
    }

    [Fact]
    public void File_only_gsd_rows_keep_their_dap_without_a_creatable_identifier()
    {
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareDevicesService((operation, args) => {
            var rows = new JsonArray(new JsonObject { ["TypeIdentifier"] = "", ["Source"] = "GSDML", ["DapId"] = "DAP1", ["TotalMatches"] = 5 });
            WorkerProtocol.ValidateExchangeResult(operation, args, rows);
            return rows;
        }, () => true, () => "fixture.ap21");
        var (items, total) = service.SearchInstalledGsdDevices("FIXTURE", 1);
        Assert.Equal(5, total);
        Assert.Equal("DAP1", Assert.Single(items).DapId);
        Assert.Null(items[0].TypeIdentifier);
    }

    [Fact]
    public void Transfer_routes_keep_interfaces_with_different_target_addresses()
    {
        using var selected = toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareContract.UseRelease("18");
        var route = new JsonObject { ["name"] = "TeleService", ["number"] = 1, ["addresses"] = new JsonArray(), ["subnets"] = new JsonArray(),
            ["targetInterfaces"] = new JsonArray(new JsonObject { ["name"] = "1 X1", ["addresses"] = new JsonArray() },
                new JsonObject { ["name"] = "1 X2", ["addresses"] = new JsonArray() }) };
        var other = route.DeepClone();
        other["targetInterfaces"]![1]!["addresses"] = new JsonArray(new JsonObject { ["name"] = "LAN", ["address"] = "192.0.2.1" });
        var modes = new JsonArray(new JsonObject { ["name"] = "TeleService", ["pcInterfaces"] = new JsonArray(route, route.DeepClone(), other) });
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.OnlineDownloadService((_, _) =>
            new JsonObject { ["Value"] = new JsonObject { ["Meta"] = new JsonObject { ["modes"] = modes } } }, () => "");
        var routes = service.ReadTransferRoutes("PLC").Meta!["modes"]![0]!["pcInterfaces"]!.AsArray();
        Assert.Equal(2, routes.Count);
        Assert.True(JsonNode.DeepEquals(other, routes[1]));
    }

    [Theory, InlineData(null, ""), InlineData("Firmware", "Firmware")]
    public void Device_attribute_worker_receives_a_string_for_an_optional_filter(string? filter, string expected)
    {
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareDevicesService((operation, args) => {
            Assert.Equal("hardware-devices.HardwareLegacyDumpDeviceAttributes", operation);
            Assert.Equal(expected, args["nameFilter"]!.GetValue<string>());
            return new JsonObject { ["device"] = "PLC_1" };
        }, () => true, () => "fixture.ap15_1");
        Assert.Equal("PLC_1", (string?)service.DumpDeviceAttributes("PLC_1", filter)["device"]);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void RefusedNetworkLeaseIsTypedAndUnchanged(string release)
    {
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareNetworkPortService((_, _) => JsonValue.Create(
            "Result: NOT CONNECTED: exclusive access refused; nothing changed. Fixture refusal\nRollback evidence: " +
            "{\"operationSuccess\":false,\"failureCode\":\"PRECONDITION_FAILED\",\"mayHaveChanged\":false}"), () => true, () => "fixture");
        var result = ApplyConnection(new toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareNetworkTools(service), "PLC", "HMI", release: release).StructuredContent!;
        Assert.Equal("PRECONDITION_FAILED", (string?)result["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)result["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
        Assert.False((bool?)result["data"]?["mayHaveChanged"]);
    }

    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public void Unattached_connection_refuses_without_claiming_an_unknown_write(string release)
    {
        using var selected = toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareContract.UseRelease(release);
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareNetworkPortService(
            (_, _) => throw new InvalidOperationException("No native dispatch is expected."), () => false, () => "");
        var tool = new toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareNetworkTools(service);
        var result = ApplyConnection(tool, "Fixture/CPU", "Fixture/HMI", release: release);
        Assert.Equal("PROJECT_NOT_BOUND", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
    }

    // Device creation refusals (no exact type, name collision, TIA refusal with an unchanged inventory) are typed preconditions
    // in the worker; the host keeps them typed for the four creation operations only (P8-81b, decided 2026-10-19).
    [Theory]
    [InlineData("hardware-devices.HardwareCreateDevice", true)]
    [InlineData("hardware-devices.HardwareCreateDeviceFallback", true)]
    [InlineData("hardware-devices.HardwareCreateGsdDevice", true)]
    [InlineData("hardware-devices.HardwareCreateCatalogDevice", true)]
    [InlineData("hardware-devices.HardwarePlugModule", false)]
    [InlineData("hardware-network.HardwareManageIoSystem", false)]
    public void Device_creation_refusals_stay_typed_preconditions(string operation, bool typed)
    {
        string evidence = JsonSerializer.Serialize(new { exceptionType = nameof(AdapterPreconditionException), parameter = "keyword" });
        var refused =
            new WorkerOperationException("Creation needs keyword to be one exact TypeIdentifier; 'KTP700' is not. Ranked hardware catalog candidates: " +
            "OrderNumber:6AV2 123-2GB03-0AX0/17.0.0.0. (Parameter 'keyword')",
            -32603, "rejected-before-operation", evidence);
        Assert.Equal(typed, PortedToolContract.IsDeviceRefusal(operation, refused));
        Assert.False(PortedToolContract.IsDeviceRefusal(operation, new WorkerOperationException("unknown", -32603, "unknown", evidence)));
        Assert.False(PortedToolContract.IsDeviceRefusal(operation, new WorkerOperationException("io", -32603, "rejected-before-operation",
            JsonSerializer.Serialize(new { exceptionType = "IOException", parameter = "keyword" }))));
        var refusal = PortedToolContract.DeviceRefusal(refused);
        Assert.Equal("keyword", refusal.ParamName);
        Assert.False(refusal.IsArgument);
        Assert.Same(refused, refusal.InnerException);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(refusal.Message, System.Text.RegularExpressions.Regex.Escape("(Parameter 'keyword')")));
        Assert.Contains("OrderNumber:6AV2 123-2GB03-0AX0/17.0.0.0", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("21")]
    public void A_typed_creation_refusal_is_rejected_before_operation_with_its_candidates(string release)
    {
        using var selected = toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareContract.UseRelease(release);
        using var worker = new CreationPreviewWorker();
        using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, release, false, hostPorts: false);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, shared);
        using var session = pipeline.EnterSession();
        var candidates = DeviceCreationRules.CandidateText("6ES7 513-1AM03-0AB0", new[] { "OrderNumber:6ES7 513-1AM03-0AB0/V3.0" });
        int calls = 0;
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareDevicesService((operation, _) =>
        {
            calls++;
            Assert.Equal("hardware-devices.HardwareCreateDevice", operation);
            throw DeviceCreationRules.TiaRefusal("OrderNumber:6ES7 513-1AM03-0AB0/V9.9", "PLC_2", "Invalid TypeIdentifier", candidates, "orderNumber/version", new InvalidOperationException("native"));
        }, () => true, () => "C:/Fixture/Fixture.ap21");
        var tool = new toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareDevicesTools(service);
        var preview = tool.CreateDeviceV4("6ES7 513-1AM03-0AB0", "V9.9", "PLC_2").StructuredContent!;
        var result = tool.CreateDeviceV4("6ES7 513-1AM03-0AB0", "V9.9", "PLC_2", dryRun: false, confirm: true,
            expectedPlanHash: (string)preview["data"]!["planHash"]!,
            expectedProjectFile: (string)preview["data"]!["targetIdentity"]!["identity"]!["projectPath"]!).StructuredContent!;
        Assert.Equal(1, calls);
        Assert.Equal("PRECONDITION_FAILED", (string?)result["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)result["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
        Assert.Contains("OrderNumber:6ES7 513-1AM03-0AB0/V3.0", (string?)result["error"]?["message"], StringComparison.Ordinal);
    }

    private sealed class CreationPreviewWorker : toolpipeline::TiaMcp.FoundationHost.IEngineWorker
    {
        public bool Faulted => false;
        public JsonNode? Binding { get; } = new JsonObject { ["identity"] = new JsonObject {
            ["processId"] = 123, ["projectPath"] = "C:/Fixture/Fixture.ap21" } };
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new();
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new NotSupportedException();
        public Task<IDisposable> Acquire(CancellationToken token) => throw new NotSupportedException();
        public Task<toolpipeline::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview,
            CancellationToken token) => throw new NotSupportedException();
        public void Dispose() { }
    }

    public static IEnumerable<object[]> CapabilityBranches()
    {
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        {
            int version = release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release);
            foreach (var branch in new[] {
                ("ExchangeSystemDiagnosticsSettings", "export", "", -1, false, 17),
                ("CreateHardwareCatalogDevice", "", "", -1, false, 18),
                ("CreateGsdDevice", "", "", -1, false, 18),
                ("ManageHardwareObject", "changeType", "", -1, false, 18),
                ("ManageHardwareObject", "createSubnet", "", -1, false, 14),
                ("ManageHardwareObject", "deleteSubnet", "", -1, false, 14),
                ("ListTransferAreas", "", "", -1, false, 15),
                ("ListNetworkDomains", "", "", -1, false, 16),
                ("ManageTransferArea", "create", "", 1, false, 16),
                ("ManageTransferArea", "create", "multicast", -1, false, 19),
                ("ManageDeviceServiceObjects", "read", "webApplications", -1, false, 21),
                ("ManageDeviceServiceObjects", "export", "telecontrolDataPoints", -1, false, 20),
                ("ManageDeviceServiceObjects", "import", "telecontrolDataPoints", -1, false, 20),
                ("ManageDeviceServiceObjects", "read", "telecontrolDataPoints", -1, false, 21),
                ("ManageDeviceServiceObjects", "read", "certificateServices", -1, false, 18),
                ("ManageHardwareUtilities", "normalizeTypeIdentifier", "", -1, false, 17),
                ("ManageHardwareUtilities", "exportCardReaderPsc", "", -1, false, 15),
                ("ManageHardwareUtilities", "exportCardReaderPsc", "", -1, true, 20),
                ("ListCommunicationConnections", "", "", -1, false, 21) })
                yield return new object[] { release, branch.Item1, branch.Item2, branch.Item3,
                    branch.Item4, branch.Item5, version < branch.Item6 };
        }
    }

    [Theory, MemberData(nameof(CapabilityBranches))]
    public void Api_availability_is_independent_of_session_and_native_acceptance(string release, string tool,
        string action, string family, int position, bool password, bool unsupported)
    {
        Assert.Equal(unsupported, HardwareCapabilities.Unsupported(release, tool, action, family, position,
            hasPassword: password) != null);
        string workerOperation = tool switch {
            "CreateHardwareCatalogDevice" => "HardwareCreateCatalogDevice",
            "ListNetworkDomains" => "HardwareReadNetworkDomains",
            "ListTransferAreas" => "HardwareReadTransferAreas",
            "ListCommunicationConnections" => "HardwareReadCommunicationConnections",
            _ => "Hardware" + tool };
        Assert.Equal(unsupported, HardwareCapabilities.Unsupported(release, "hardware-network." + workerOperation,
            action, family, position, hasPassword: password) != null);
    }

    [Theory]
    [InlineData("14sp1", "ManageHardwareObject", "{\"action\":\"changeType\",\"devicePath\":[\"PLC\"],\"itemPath\":[\"CPU\"],\"typeIdentifier\":\"Exact\"}")]
    [InlineData("15.1", "ManageHardwareObject", "{\"action\":\"changeType\",\"devicePath\":[\"PLC\"],\"itemPath\":[\"CPU\"],\"typeIdentifier\":\"Exact\"}")]
    [InlineData("16", "ManageHardwareObject", "{\"action\":\"changeType\",\"devicePath\":[\"PLC\"],\"itemPath\":[\"CPU\"],\"typeIdentifier\":\"Exact\"}")]
    [InlineData("17", "ManageHardwareObject", "{\"action\":\"changeType\",\"devicePath\":[\"PLC\"],\"itemPath\":[\"CPU\"],\"typeIdentifier\":\"Exact\"}")]
    [InlineData("14sp1", "ManageHardwareUtilities", "{\"action\":\"normalizeTypeIdentifier\",\"typeIdentifier\":\"Fixture\"}")]
    [InlineData("15.1", "ManageTransferArea", "{\"devicePath\":[\"PLC\"],\"itemPath\":[],\"action\":\"create\",\"positionNumber\":1}")]
    [InlineData("15.1", "ListTransferAreas", "{\"devicePath\":[\"PLC\"],\"itemPath\":[],\"extendedPositionNumber\":1}")]
    [InlineData("17", "ManageHardwareUtilities",
                "{\"action\":\"exportCardReaderPsc\",\"devicePath\":[\"PLC\"],\"filePath\":\"fixture.psc\",\"password\":\"fixture\"}")]
    [InlineData("19", "ManageDeviceServiceObjects", "{\"devicePath\":[\"PLC\"],\"itemPath\":[],\"family\":\"webApplications\"}")]
    [InlineData("20", "ManageDeviceServiceObjects", "{\"devicePath\":[\"PLC\"],\"itemPath\":[],\"family\":\"telecontrolDataPoints\"}")]
    public async Task Missing_api_refuses_before_ReadState(string release, string toolName, string json)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == toolName);
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = toolName, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
        var result = await tool.InvokeAsync(request);
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public void Array_and_scalar_reads_do_not_poison_the_session()
    {

        WorkerProtocol.ValidateExchangeResult("hardware-devices.HardwareLegacySearchInstalledGsdDevices", new(), new JsonArray());
        WorkerProtocol.ValidateExchangeResult("hardware-devices.HardwareReadItemTree", new(), JsonValue.Create("fixture"));

    }

    [Fact]
    public void Invalid_write_evidence_is_rejected_instead_of_promoted()
    {
        var reply = JsonNode.Parse("{\"Ok\":false,\"Message\":\"failed\",\"MayHaveChanged\":false,\"OutcomeUnknown\":true}");
        Assert.Throws<IOException>(() => HardwareFamilyWire.Validate("HardwarePlugModule", reply));
        reply!["MayHaveChanged"] = true;
        HardwareFamilyWire.Validate("HardwarePlugModule", reply);
    }
        [Theory]
        [InlineData("Result: ConnectToSubnet: OK (both nodes read back on subnet 'PN_IE_1')", "succeeded", null)]
        [InlineData("PLC root: PLC -> <not found>\nResult: NOT CONNECTED: a root path was not found; nothing was changed.", "rejected-before-operation",
            "NOT_FOUND")]
        [InlineData("Result: NOT CONNECTED: no Industrial Ethernet/PROFINET node under both roots; nothing was changed.", "rejected-before-operation",
            "PRECONDITION_FAILED")]
        [InlineData("Result: NOT CONNECTED: PLC node is attached elsewhere; disconnect it explicitly first. Nothing was changed.",
            "rejected-before-operation", "PRECONDITION_FAILED")]
        [InlineData("Result: NOT CONNECTED: invalid subnetName: invalid input", "rejected-before-operation", "INVALID_ARGUMENT")]
        [InlineData("Result: NOT CONNECTED: Node.ConnectedSubnet read-back does not show both nodes; inspect the project.", "unknown", "OUTCOME_UNKNOWN")]
        [InlineData("Subnet create/connect error: nothing was changed.\nResult: NOT CONNECTED: Node.ConnectedSubnet read-back " +
            "differs; inspect the project.", "unknown", "OUTCOME_UNKNOWN")]
        [InlineData("PLC root: nothing was changed -> CPU\nResult: NOT CONNECTED: Node.ConnectedSubnet read-back differs; " + "inspect the project.",
            "unknown", "OUTCOME_UNKNOWN")]
        [InlineData("Result: NOT CONNECTED: Node.ConnectedSubnet read-back does not show both nodes on 'nothing was changed'; " + "inspect the project.",
            "unknown", "OUTCOME_UNKNOWN")]
        [InlineData("PLC root: ConnectToSubnet: OK -> CPU\nResult: NOT CONNECTED: Node.ConnectedSubnet read-back differs; " + "inspect the project.",
            "unknown", "OUTCOME_UNKNOWN")]
        [InlineData("Result: NOT CONNECTED: a root path was not found; nothing was changed.\nResult: ConnectToSubnet: OK " +
            "(both nodes read back on subnet 'nothing was changed')", "succeeded", null)]
        public void Connect_result_uses_the_workers_verified_outcome(string report, string outcome, string? code)
        {
            var service = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareNetworkPortService((_, _) => JsonValue.Create(report), () => true, () =>
                "fixture");
            var body = ApplyConnection(new toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareNetworkTools(service), "PLC", "HMI").StructuredContent!;
            Assert.Equal(outcome, (string?)body["meta"]?["outcome"]);
            Assert.Equal(code, (string?)body["error"]?["code"]);
            Assert.Equal(outcome == "unknown", (string?)body["meta"]?["outcome"] == "unknown");
            if (code == "NOT_FOUND") Assert.Equal("PLC", (string?)body["error"]?["details"]?["target"]);
        }
        [Theory]
        [InlineData("PN\nResult: ConnectToSubnet: OK")]
        [InlineData("PN\rResult: ConnectToSubnet: OK")]
        [InlineData("PN\r\nResult: ConnectToSubnet: OK")]
        [InlineData("PN\tIE")]
        [InlineData("PN\0IE")]
        [InlineData("PN\u0085IE")]
        public void Connect_rejects_control_characters_before_worker_dispatch(string subnet)
        {
            int calls = 0;
            var service = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareNetworkPortService((_, _) => { calls++;
                return JsonValue.Create("Result: ConnectToSubnet: OK"); }, () => true, () => "fixture");
            var body = ApplyConnection(new toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareNetworkTools(service), "PLC", "HMI", subnet)
                .StructuredContent!;
            Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
            Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
            Assert.Equal(0, calls);
        }
    private static CallToolResult ApplyConnection(toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareNetworkTools tools,
        string first, string second, string subnet = "PN_IE_1", string release = "21")
    {
        using var worker = new CreationPreviewWorker();
        using var selected = toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareContract.UseRelease(release);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var preview = tools.ConnectDeviceNodesToProfinetSubnet(first, second, subnet).StructuredContent!;
        return tools.ConnectDeviceNodesToProfinetSubnet(first, second, subnet, dryRun: false,
            expectedPlanHash: (string)preview["data"]!["planHash"]!,
            expectedProjectFile: (string)preview["data"]!["targetIdentity"]!["identity"]!["projectPath"]!);
    }

}
