extern alias toolpipeline;
extern alias foundationhost;

using System.Text.Json.Nodes;
using TiaMcp.Logic.Generation;
using ClassicHmi = TiaMcp.Adapters.Native.Hmi.ClassicHmiLogic;
using HardwarePath = TiaMcp.Adapters.Hardware.HardwarePath;
using LibraryPolicy = TiaMcp.Adapters.Libraries.LibraryPolicy;
using HostTools = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Motion = TiaMcp.Adapters.Native.Plc.PlcMotionPolicy;
using MotionInputs = toolpipeline::TiaMcpServer.Siemens.MotionProDiagClassicHmiLogic;
using StartdriveLogic = toolpipeline::TiaMcpServer.Siemens.StartdriveLogic;
using TiaMcpServer.Siemens;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.EngineHost.Tests;

public sealed class ServedOperationPolicyTests
{
    [Theory]
    [InlineData("ManagePortInterconnection", """{"action":"read","devicePath":[""],"itemPath":[]}""")]
    [InlineData("ManageDeviceUserGroup", """{"action":"create","groupPath":"../Line"}""")]
    [InlineData("ManageGlobalLibrary", """{"action":"open","openMode":"ReadOnly","upgrade":true}""")]
    [InlineData("ManageCommunicationConnection", """{"action":"create","connectionType":"Unknown","devicePath":["PLC"],"itemPath":[]}""")]
    [InlineData("ManageMotionAxis", """{"action":"updateMapping","aspect":"toMapping","name":"Axis","properties":{"Alias":"Other"}}""")]
    public void Example_policy_rejects_invalid_operation_inputs(string tool, string arguments)
    {
        var input = new Inputs(JsonNode.Parse(arguments)!.AsObject(), new JsonObject(), "21");
        Assert.NotNull(Record.Exception(() => Policies[tool](input)));
    }

    [Fact]
    public void Simulator_registration_guides_cpu_selection_by_enum_name()
    {
        var (_, usage) = new TiaMcp.Engine.Tests.Shared.ServedToolOperations("21").For("ManagePlcSimAdvancedInstance").First();
        string description = (string)usage["inputSchema"]!["properties"]!["cpuType"]!["description"]!;
        Assert.Contains("ECPUType", description);
        Assert.DoesNotContain("SearchHardwareCatalog", description);
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Served_reference_queries_accept_an_export_directory_with_an_ob_and_an_fb(string release)
    {
        string root = Directory.CreateTempSubdirectory("plc-reference-examples-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Main.xml"),
                "<Document><SW.Blocks.OB ID=\"0\"><AttributeList><Name>Main</Name><Number>1</Number></AttributeList></SW.Blocks.OB></Document>");
            File.WriteAllText(Path.Combine(root, "FB_Pump.xml"),
                "<Document><SW.Blocks.FB ID=\"0\"><AttributeList><Name>FB_Pump</Name><Number>1</Number></AttributeList></SW.Blocks.FB></Document>");
            var requests = new TiaMcp.Engine.Tests.Shared.ServedToolOperations(release).For("AnalyzePlcReferences").ToArray();
            Assert.NotEmpty(requests);
            foreach (var (_, usage) in requests)
            {
                var args = usage["example"]!["request"]!["params"]!["arguments"]!.AsObject();
                var input = new Inputs(args, usage["inputSchema"]!["properties"]!.AsObject(), release);
                Assert.True(Path.IsPathFullyQualified(input.Text("sourceDirectory")));
                TiaMcpServer.ModelContextProtocol.PlcOfflineReferences.Analyze(root, input.Text("action"), input.Text("target"),
                    input.Number("maxDepth"), input.Number("offset"), input.Number("limit"));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Served_package_operations_validate_against_an_isolated_store(string release)
    {
        string root = Directory.CreateTempSubdirectory("package-examples-").FullName;
        try
        {
            var catalog = new TiaMcp.Engine.Tests.Shared.ServedToolOperations(release);
            string repository = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory), "templates", "standards");
            var store = new StandardPackageStore(repository, Path.Combine(root, "fresh"));
            var seeded = new StandardPackageStore(repository, Path.Combine(root, "seeded"));
            var userOnly = new StandardPackageStore(Path.Combine(root, "empty"), seeded.UserRoot);
            var fork = seeded.Manage("fork", "tiamcp.basic", "1.0.0", "", "", "example.basic", "1.0.0", true, "");
            seeded.Manage("fork", "tiamcp.basic", "1.0.0", "", "", "example.basic", "1.0.0", false, (string)fork["planHash"]!);
            using (var file = File.Create(Path.Combine(root, "example.zip"))) store.Load("tiamcp.basic", "1.0.0").WriteCanonicalZip(file);
            foreach (string name in new[] { "ManageStandardPackage", "ValidateStandardPackage" })
            foreach (var (_, usage) in catalog.For(name))
            {
                var args = TiaMcp.Logic.V4.CanonicalToolParameters.LegacyArguments(name, usage["example"]!["request"]!["params"]!["arguments"]!.AsObject());
                string Text(string field) => (string?)args[field] ?? "";
                string BindPath(string field)
                {
                    string path = Text(field);
                    if (path == "") return path;
                    Assert.True(Path.IsPathFullyQualified(path));
                    return Path.Combine(root, Path.GetFileName(path));
                }
                string action = Text("action");
                if (name == "ValidateStandardPackage")
                {
                    var tools = new toolpipeline::TiaMcpServer.ModelContextProtocol.GenerationTools(release, () => store);
                    var result = tools.ValidateStandardPackage(Text("packageId"), Text("version"), BindPath("sourcePath"));
                    Assert.True((bool?)result.StructuredContent!["data"]?["valid"], result.StructuredContent.ToJsonString());
                }
                else
                {
                    Assert.True((bool?)args["dryRun"]);
                    var preview = (action is "import" or "remove" ? userOnly : store).Manage(action, Text("packageId"), Text("version"),
                        BindPath("sourcePath"), BindPath("outputPath"), Text("newId"), Text("newVersion"), true, Text("expectedPlanHash"));
                    Assert.False((bool?)preview["executed"]);
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Served_requests_satisfy_their_offline_operation_policy(string release)
    {
        var catalog = new TiaMcp.Engine.Tests.Shared.ServedToolOperations(release);
        var errors = new List<string>();
        int checkedRequests = 0;
        foreach (var (name, validate) in Policies)
        foreach (var (operation, usage) in catalog.For(name))
        {
            try
            {
                var example = usage["example"]!;
                var arguments = example["request"]!["params"]!["arguments"]!.AsObject();
                var input = new Inputs(arguments, usage["inputSchema"]!["properties"]!.AsObject(), release);
                var guarded = ReleaseCapabilityQuery.WithSelectorDefaults(arguments, input.Properties);
                var reason = ReleaseCapabilityQuery.Problem(name, release, guarded)
                    ?? ToolVersionPolicy.CallProblem(release, name, key => guarded[key]?.ToString());
                Assert.Equal(reason, (string?)example["releaseProblem"] ?? "");
                var recipe = ToolUsageCatalog.ServedCall(name, release)?["operations"]?[operation];
                if (recipe?["requiredContext"] is JsonObject requiredContext)
                {
                    validate(input);
                    foreach (var (field, required) in requiredContext)
                    {
                        Assert.Equal((string?)required, input.Text(field));
                        foreach (var other in input.Properties[field]!["enum"]!.AsArray().Where(value => !JsonNode.DeepEquals(value, required)))
                        {
                            var alternative = arguments.DeepClone().AsObject();
                            alternative[field] = other?.DeepClone();
                            var error = Record.Exception(() => validate(input with { Arguments = alternative }));
                            Assert.True(error is ArgumentException or NotSupportedException, $"{name}.{operation} admits {field}={other}");
                        }
                    }
                }
                if ((string?)example["kind"] == "unavailable")
                {
                    Assert.NotEmpty(reason);
                    continue;
                }
                if (name == "ManageOnlineDriveFunctions" && input.Text("action") != "read" && input.Flag("dryRun"))
                {
                    // RunWritePreview returns before the online policy. Only the reviewed execution form supplies consent.
                    Assert.False(input.Flag("confirmOnline"));
                    Assert.Contains("confirmOnline=true", Assert.Throws<ArgumentException>(() => validate(input)).Message);
                    var apply = arguments.DeepClone().AsObject();
                    foreach (var field in example["execution"]!["arguments"]!.AsObject()) apply[field.Key] = field.Value?.DeepClone();
                    input = input with { Arguments = apply };
                    Assert.False(input.Flag("dryRun"));
                }
                validate(input);
                checkedRequests++;
                if (operation == "default")
                    foreach (var source in usage["parameterSources"]!.AsObject())
                        foreach (var alternate in source.Value!["argumentExamples"]?.AsArray() ?? [])
                            validate(input with { Arguments = alternate!.AsObject() });
            }
            catch (Exception error) { errors.Add($"{release}/{name}/{operation}: {error.Message}"); }
        }
        Assert.True(checkedRequests > 0);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    // Bind the public V4 inputs and their declared defaults to the same pure validation methods as the engineering tools.
    private sealed record Inputs(JsonObject Arguments, JsonObject Properties, string Release)
    {
        private JsonNode? Value(string name) => Arguments[name] ?? Properties[name]?["default"];
        public string Text(string name) => (string?)Value(name) ?? "";
        public bool Flag(string name) => (bool?)Value(name) ?? false;
        public int Number(string name) => (int?)Value(name) ?? 0;
        public long Long(string name) => (long?)Value(name) ?? 0;
        public JsonObject Map(string name) => Value(name)?.AsObject() ?? new JsonObject();
        public string[] Names(string name) => Value(name)?.AsArray().Select(node => (string)node!).ToArray() ?? [];
        public string Json(string name) => Value(name)?.ToJsonString() ?? (string?)Properties[name]?["type"] switch
        {
            "object" => "{}", "array" => "[]", _ => ""
        };
    }

    private static readonly Dictionary<string, Action<Inputs>> Policies = new()
    {
        ["SetPlcWebOperatingMode"] = i =>
        {
            TiaMcpServer.Runtime.RuntimeChannelsLogic.NormalizeS7WebHost(i.Text("host"));
            TiaMcpServer.Runtime.RuntimeChannelsLogic.NormalizeOperatingModeRequest(i.Text("mode"));
            Assert.NotEmpty(i.Text("username"));
            Assert.True(i.Flag("dryRun"));
            Assert.False(i.Flag("confirmModeChange"));
        },
        ["ValidateStandardPackage"] = i =>
        {
            bool source = i.Text("sourcePath").Length > 0;
            Assert.Equal(!source, i.Text("packageId").Length > 0);
            Assert.Equal(!source, i.Text("version").Length > 0);
        },
        ["PrefillWorkbenchForm"] = i =>
        {
            var fields = System.Text.Json.JsonSerializer.Deserialize<WorkbenchPrefillArguments>(i.Arguments.ToJsonString(), WorkbenchControlProtocol.Json);
            WorkbenchControlProtocol.Validate(new WorkbenchControlRequest {
                Operation = WorkbenchControlOperation.PrefillForm, Arguments = fields!, RequestId = Guid.NewGuid().ToString("N"),
                Origin = new WorkbenchControlOrigin { ReleaseKey = i.Release, HostProcessId = Environment.ProcessId, McpSession = "0123456789abcdef" },
                DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5)
            });
        },
        ["ManagePlcTestLibrary"] = i =>
        {
            if (i.Text("action") is "read" or "delete") TiaMcp.Logic.Testing.PlcTestCaseDocument.ValidateId(i.Text("id"));
            if (i.Text("action") == "save") TiaMcp.Logic.Testing.PlcTestCaseDocument.Parse(i.Json("testCase"));
        },
        ["BuildUnifiedHmiButtonActionScript"] = i =>
        {
            var recipe = toolpipeline::TiaMcpServer.ModelContextProtocol.HmiDesignBuilder.BuildUnifiedHmiButtonActionScript(
                i.Text("actionKind"), i.Text("eventType"), i.Text("targetTag"), i.Text("targetScreen"), i.Text("targetPopup")).Meta!;
            Assert.True((bool?)recipe["ok"], recipe.ToJsonString());
            Assert.Equal(i.Text("actionKind") is "confirm-write" or "open-popup", (bool?)recipe["applyBlocked"]);
        },
        ["BuildPlcLadFcBlock"] = i => foundationhost::TiaMcp.FoundationHost.OfflineLadderBuilders
            .BuildForRelease("BuildPlcLadFcBlock", i.Text("outputReleaseKey"), i.Json("ladFcBlock")),
        ["ManageMachineDescription"] = i =>
        {
            if (i.Text("action") is "export" or "validate") TiaMcp.Logic.Generation.GenerationDocuments.MachineHash(i.Json("machine"));
            else Assert.Null(i.Arguments["machine"]);
        },
        ["ManagePlcUserGroup"] = i => toolpipeline::TiaMcpServer.Siemens.Services.PlcOrganisationPortService
            .ValidateGroup(i.Text("groupPath"), i.Text("family") == "types", i.Text("family") == "blocks"),
        ["ManageDeviceUserGroup"] = i =>
        {
            string action = HardwareNetworkLogic.RequireOneOf(i.Text("action"), HardwareNetworkLogic.DeviceGroupActions, "action");
            HardwarePath.Parts(i.Text("groupPath"), rootAllowed: action == "read");
            if (action == "rename") HardwarePath.RequireNewName(i.Text("newName"));
        },
        ["ManagePortInterconnection"] = i =>
        {
            string action = i.Text("action");
            HostTools.HardwareToolContract.Path(i.Names("devicePath"), "devicePath", false, false);
            HostTools.HardwareToolContract.Path(i.Names("itemPath"), "itemPath", true, false);
            HostTools.HardwareToolContract.Path(i.Names("partnerDevicePath"), "partnerDevicePath", true, true);
            HostTools.HardwareToolContract.Path(i.Names("partnerItemPath"), "partnerItemPath", true, true);
            HostTools.HardwareNetworkTools.ValidatePortAction(action, i.Names("partnerDevicePath"));
            if (action != "read") Assert.NotEmpty(i.Names("partnerDevicePath"));
        },
        ["ManageGlobalLibrary"] = i =>
        {
            LibraryPolicy.ValidateGlobalAction(i.Text("action"));
            LibraryPolicy.ValidateUpgrade(i.Text("action"), i.Flag("upgrade"), i.Text("openMode") == "ReadWrite");
            if (i.Text("action") == "archive")
            {
                LibraryPolicy.RequireOneOf(i.Text("archiveMode"), LibraryPolicy.ArchivationModes, "archiveMode");
                LibraryPolicy.ValidateArchiveName(i.Text("archiveName"));
            }
        },
        ["ManagePlcCertificate"] = i =>
        {
            string action = i.Text("action");
            var names = SecurityDeepLogic.ParseSubjectAlternativeNames(i.Json("subjectAlternativeNames"));
            if (action != "create") Assert.Empty(names);
            if (action is "template" or "create") SecurityDeepLogic.RequireOneOf(i.Text("usage"), SecurityDeepLogic.CertificateUsages, "usage");
            SecurityDeepLogic.ValidateTemplateProperties(i.Map("properties"));
            if (action is "read" or "delete" or "export") Assert.NotEmpty(i.Text("certificateId"));
            if (action is "assign" or "unassign")
                Assert.Throws<NotSupportedException>(() => TiaMcp.Adapters.Contracts.SecurityAttributePolicy
                    .RequireApproved(i.Text("assignment"), TiaMcp.Adapters.Contracts.SecurityAttributePolicy.CertificateNames));
        },
        ["ManageCommunicationConnection"] = i => HostTools.HardwareServicesPortTools.ValidateConnectionAction(
            i.Text("action"), i.Text("connectionType"), i.Flag("dryRun"), i.Flag("confirmDelete")),
        ["ExchangeSystemDiagnosticsSettings"] = i => HardwareServicesLogic.RequireOneOf(i.Text("action"), ["export", "import"], "action"),
        ["ManageWatchForceTableWebAccess"] = i =>
        {
            HardwareServicesLogic.RequireOneOf(i.Text("action"), ["read", "assign", "unassign"], "action");
            HardwareServicesLogic.RequireOneOf(i.Text("tableKind"), ["watch", "force"], "tableKind");
        },
        ["ManageMotionAxis"] = i =>
        {
            string action = i.Text("action"), aspect = i.Text("aspect");
            string category = Motion.MotionCategory(action, aspect);
            if (category == "mapping")
            {
                Motion.RequireName(i.Text("name"), "alias");
                Motion.ValidateMappingProperties(aspect, i.Map("properties").Select(pair => pair.Key));
            }
            if (action == "deleteMapping") Assert.True(i.Flag("confirmDelete"));
            if (action == "updateMapping") Assert.NotEmpty(i.Map("properties"));
            if (action == "connect") Motion.RequireConnectionMode(aspect, MotionInputs.ParseConnectionTarget(i.Json("target")).Mode);
            if (action == "connectIdent") Assert.Equal("deviceItem", MotionInputs.ParseConnectionTarget(i.Json("target")).Mode);
        },
        ["ManageClassicHmiScript"] = i =>
        {
            string action = ClassicHmi.RequireAction(i.Text("action"), ClassicHmi.ScriptActions);
            if (action != "import") ClassicHmi.SplitObjectPath(i.Text("scriptPath"));
            MotionInputs.ParseAttributes(i.Json("attributes"), action == "setAttributes");
        },
        ["ManageClassicHmiCycle"] = i =>
        {
            string action = ClassicHmi.RequireAction(i.Text("action"), ClassicHmi.CycleActions);
            ClassicHmi.RequirePagination(i.Number("offset"), i.Number("limit"));
            if (action != "import" && (action != "read" || i.Text("cycleName") != "")) ClassicHmi.RequireName(i.Text("cycleName"), "cycleName");
            MotionInputs.ParseAttributes(i.Json("attributes"), action == "setAttributes");
        },
        ["ManageClassicHmiTextGraphicList"] = i =>
        {
            string action = ClassicHmi.RequireAction(i.Text("action"), ClassicHmi.ListActions);
            ClassicHmi.RequirePagination(i.Number("offset"), i.Number("limit"));
            Assert.Contains(i.Text("listKind"), ClassicHmi.ListKinds.Keys);
            if (action != "import" && (action != "read" || i.Text("listName") != "")) ClassicHmi.RequireName(i.Text("listName"), "listName");
            if (action is "readEntries" or "createEntry" or "deleteEntry") Assert.NotEmpty(i.Text("compositionName"));
            if (action == "deleteEntry") ClassicHmi.RequireName(i.Text("entryName"), "entryName");
            MotionInputs.ParseAttributes(i.Json("attributes"), action is "createEntry" or "setAttributes");
        },
        ["ManagePlcSupervision"] = i =>
        {
            string action = MotionInputs.RequireAction(i.Text("action"), MotionInputs.SupervisionActions);
            MotionInputs.RequirePagination(i.Number("offset"), i.Number("limit"));
            MotionInputs.ParseAttributes(i.Json("attributes"), action is "setAttributes" or "createEntry");
            if (action is "readComposition" or "createEntry" or "deleteEntry") Assert.NotEmpty(i.Text("compositionName"));
            if (action == "deleteEntry")
            {
                MotionInputs.RequireName(i.Text("entryName"), "entryName");
                Assert.True(i.Flag("confirmDelete"));
            }
        },
        ["CompilePlcSoftware"] = i => TiaMcp.Adapters.Contracts.PlcCompileScope.Validate(i.Text("scope"), i.Text("scopePath"), i.Text("unitName")),
        ["CompilePlcDiagnostics"] = i => TiaMcp.Adapters.Contracts.PlcCompileScope.Validate(i.Text("scope"), i.Text("scopePath"), i.Text("unitName")),
        ["ManageUnifiedHmiGroup"] = i => TiaMcp.Adapters.Contracts.UnifiedHmiInputValidation.Request(new TiaMcp.Adapters.Contracts.UnifiedHmiRequest
        {
            Operation = "ManageUnifiedHmiGroup", Action = i.Text("action"), Family = i.Text("family"), GroupPath = i.Text("groupPath"),
            NewName = i.Text("newName"), DryRun = i.Flag("dryRun")
        }),
        ["CompareProjects"] = i => ProjectSecurityLogic.ValidateCompareRequest(
            i.Text("kind"), i.Text("softwarePath"), i.Json("devicePath"), i.Text("targetProjectName"), i.Text("targetSoftwarePath"),
            i.Json("targetDevicePath")),
        ["ExchangeCfcCharts"] = i => CfcLogic.ValidateExchangeRequest(
            i.Text("action"), i.Text("filePath"), i.Text("modelVersion"), i.Long("filter"), i.Json("chartNames"), i.Flag("deleteAtTarget"),
            i.Flag("dryRun")),
        ["ExchangePlcAlarmTextLists"] = i => AlarmTextListRules.ValidateXlsxRequest(
            i.Text("action"), i.Text("filePath"), i.Text("unitName"), i.Text("unitKind"), i.Json("textListNames"), i.Json("cultures"),
            i.Text("importOption"), i.Flag("confirmImport"), i.Flag("dryRun")),
        ["ExchangeTestSuiteCase"] = i => TestSuiteLogic.ValidateExchangeRequest(
            i.Text("category"), i.Text("action"), i.Text("name"), i.Text("filePath"), i.Text("importOptions"), i.Text("loadOptions"),
            i.Text("kind"), i.Flag("dryRun")),
        ["GetDriveParameters"] = i => StartdriveLogic.ValidateParametersRequest(
            i.Text("source"), i.Json("names"), i.Json("numbers"), i.Number("offset"), i.Number("limit")),
        ["GetPlcObjectFingerprints"] = i => SoftwareUnitDeepLogic.ValidateFingerprintRequest(
            i.Text("objectKind"), i.Text("objectPath"), i.Text("unitName"), i.Text("unitKind")),
        ["ManageClassicHmiFolder"] = i => ClassicHmiFoldersLogic.ValidateFolderRequest(
            i.Text("folderKind"), i.Text("folderPath"), i.Text("action"), i.Text("newName"), i.Flag("confirmDelete"), i.Flag("dryRun")),
        ["ManageClassicHmiGraphic"] = i => ClassicHmiFoldersLogic.ValidateGraphicRequest(
            i.Text("action"), i.Text("name"), i.Text("filePath"), i.Text("importOptions"), i.Flag("confirmDelete"), i.Flag("dryRun")),
        ["ManageClassicHmiScreenObject"] = i => ClassicHmiFoldersLogic.ValidateObjectRequest(
            i.Text("objectKind"), i.Text("objectPath"), i.Text("action"), i.Text("filePath"), i.Text("importOptions"), i.Flag("confirmDelete"),
            i.Flag("dryRun")),
        ["ManageDcbLibraries"] = i => DccLogic.ValidateLibraryRequest(
            i.Text("action"), i.Text("filePath"), i.Flag("dryRun")),
        ["ManageDccBlock"] = i => DccLogic.ValidateBlockRequest(
            i.Text("chartPath"), i.Text("blockName"), i.Text("action"), i.Text("blockType"), i.Text("libraryName"), i.Json("properties"),
            i.Flag("confirmDelete"), i.Flag("dryRun"), i.Number("sequenceIndex")),
        ["ManageDccChart"] = i => DccLogic.ValidateChartRequest(
            i.Text("chartName"), i.Text("action"), i.Text("filePath"), i.Text("importOptions"), i.Json("properties"), i.Flag("confirmDelete"),
            i.Flag("dryRun"), i.Number("sequenceIndex")),
        ["ManageDccChartInterface"] = i => DccLogic.ValidateInterfaceRequest(
            i.Text("chartPath"), i.Text("interfaceName"), i.Text("action"), i.Text("sourceBlock"), i.Text("sourcePin"), i.Json("properties"),
            i.Flag("confirmDelete"), i.Flag("dryRun")),
        ["ManageDccChartPartition"] = i => DccLogic.ValidatePartitionRequest(
            i.Text("chartPath"), i.Text("partitionName"), i.Text("action"), i.Json("properties"), i.Flag("confirmDelete"), i.Flag("dryRun")),
        ["ManageDccPin"] = i => DccLogic.ValidatePinRequest(
            i.Text("chartPath"), i.Text("blockName"), i.Text("pinName"), i.Text("action"), i.Json("properties"), i.Json("partner"),
            i.Flag("setAsSignal"), i.Number("parameterNumber"), i.Number("arrayIndex"), i.Flag("dryRun")),
        ["ManageDeviceServiceObjects"] = i => DeviceServiceObjectRules.ValidateServiceObjectRequest(
            i.Text("family"), i.Text("action"), i.Text("name"), i.Map("properties"), i.Text("filePath"), i.Flag("confirmChange"),
            i.Flag("dryRun")),
        ["ManageDeviceUsers"] = i => HardwareNetworkLogic.ValidateUserRequest(
            i.Text("family"), i.Text("action"), i.Text("userName"), i.Text("password"), i.Names("permissions"), i.Text("newName")),
        ["ManageDriveFunctions"] = i => StartdriveLogic.ValidateFunctionRequest(
            i.Text("action"), i.Json("value"), i.Flag("dryRun")),
        ["ManageDriveHardwareModule"] = i => StartdriveLogic.ValidateHardwareModuleRequest(
            i.Text("action"), i.Text("typeIdentifier"), i.Number("positionNumber"), i.Flag("dryRun")),
        ["ManageDriveSafetyAcceptanceTest"] = i => StartdriveLogic.ValidateSafetyTestRequest(
            i.Text("action"), i.Text("identifier"), i.Text("filePath"), i.Text("fileOperation"), i.Flag("dryRun")),
        ["ManageDriveSecurity"] = i => StartdriveLogic.ValidateSecurityRequest(
            i.Text("action"), i.Text("password"), i.Flag("dryRun")),
        ["ManageDriveTelegrams"] = i => StartdriveLogic.ValidateTelegramRequest(
            i.Text("action"), i.Text("telegramType"), i.Number("telegramNumber"), i.Number("inputSize"), i.Number("outputSize"),
            i.Text("direction"), i.Number("size"), i.Flag("keepOriginalAddress"), i.Text("softwarePath"), i.Text("objectPath"),
            i.Text("interfaceKind"), i.Number("sensorIndex"), i.Text("connectOption"), i.Flag("dryRun")),
        ["ManageHardwareUtilities"] = i => HardwareUtilityRules.ValidateHardwareUtilityRequest(
            i.Text("action"), i.Text("typeIdentifier"), i.Json("devicePath"), i.Text("filePath"), i.Text("password"), i.Flag("dryRun")),
        ["ManageIoSystem"] = i => HardwareNetworkLogic.ValidateIoSystemRequest(
            i.Text("action"), i.Text("name"), i.Text("subnetName"), i.Text("ioSystemName")),
        ["ManageLibraryType"] = i => LibraryDeepLogic.ValidateTypeRequest(
            i.Text("action"), i.Map("properties"), i.Text("targetLibraryName"), i.Text("libraryName"),
            LibraryDeepLogic.ParseScopes(i.Json("scopeSoftwarePaths"))),
        ["ManageMultiuserSession"] = i => ProjectSecurityLogic.ValidateMultiuser(
            i.Text("action"), i.Text("serverName"), i.Text("projectName"), i.Text("protocol"), i.Text("host"), i.Number("port"),
            i.Text("commitComment"), i.Flag("confirmChange"), i.Flag("dryRun")),
        ["ManageNetworkDomain"] = i => HardwareNetworkLogic.ValidateDomainRequest(
            i.Text("kind"), i.Text("action"), i.Text("name")),
        ["ManageOnlineDriveFunctions"] = i => StartdriveLogic.ValidateOnlineFunctionRequest(
            i.Text("action"), i.Text("resetMode"), i.Text("activationState"), i.Flag("confirmOnline")),
        ["ManagePasswordPolicy"] = i => SecurityDeepLogic.ValidatePolicyRequest(
            i.Text("action"), i.Text("target"), i.Map("properties"), i.Flag("confirmChange"), i.Flag("dryRun")),
        ["ManagePlcAlarmTextList"] = i => PlcBlockServicesLogic.ValidateTextListRequest(
            i.Text("action"), i.Text("name"), i.Text("libraryName"), i.Text("masterCopyPath"), i.Flag("confirmDelete"), i.Flag("dryRun")),
        ["ManagePlcBlockProtection"] = i => PlcBlockServicesLogic.ValidateProtectionRequest(
            i.Text("action"), i.Text("password"), i.Flag("confirmProtectionChange"), i.Flag("dryRun")),
        ["ManagePlcBlockWriteProtection"] = i => SoftwareUnitDeepLogic.ValidateWriteProtectionRequest(
            i.Text("action"), i.Text("password"), i.Text("newPassword"), i.Flag("confirmProtectionChange"), i.Flag("dryRun")),
        ["ManagePlcDataBlockSnapshot"] = i => PlcBlockServicesLogic.ValidateSnapshotRequest(
            i.Text("action"), i.Text("filePath"), i.Flag("confirmValueChange"), i.Flag("dryRun")),
        ["ManagePlcDocuments"] = i => SoftwareUnitDeepLogic.ValidateDocumentRequest(
            i.Text("action"), i.Text("objectKind"), i.Text("name"), i.Text("unitName"), i.Text("unitKind"), i.Text("directoryPath"),
            i.Text("importOption"), i.Text("libraryName"), i.Text("masterCopyPath"), i.Text("copyMode"), i.Text("typePath"), i.Text("version"),
            i.Text("updatePathsMode"), i.Flag("dryRun")),
        ["ManagePlcExternalSources"] = i => ExternalSourceRules.ValidateExternalSourceRequest(
            i.Text("action"), i.Text("name"), i.Text("unitName"), i.Text("unitKind"), i.Text("filePath"), i.Text("libraryName"),
            i.Text("masterCopyPath"), i.Text("copyMode"), i.Text("generateOption"), i.Text("targetKind"), i.Text("targetGroupPath"),
            i.Text("newName"), i.Flag("confirmDelete"), i.Flag("dryRun")),
        ["ManagePlcProtection"] = i => PlcProtectionLogic.Validate(
            i.Text("action"), i.Text("accessLevel"), i.Text("password"), i.Text("newPassword")),
        ["ManagePlcSafety"] = i =>
        {
            string action = i.Text("action");
            SafetyLogic.ValidateRequest(action, i.Text("runtimeGroup"), i.Text("password"), i.Flag("confirmSafetyChange"), i.Flag("dryRun"));
            if (action == "updateRuntimeGroup") SafetyLogic.SplitRuntimeGroupChanges(i.Json("properties"));
            if (action == "updateSettings") SafetyLogic.SplitSettingsChanges(i.Json("properties"));
        },
        ["ManageSafetyGlobalSettings"] = i =>
        {
            if (i.Text("action") == "update") SafetyLogic.ParseGlobalSettingChanges(i.Json("properties"));
            else Assert.Equal("read", i.Text("action"));
        },
        ["ManagePlcSoftwareUnit"] = i => SoftwareUnitDeepLogic.ValidateUnitRequest(
            i.Text("action"), i.Text("unitKind"), i.Text("name"), i.Text("relatedUnit"), i.Text("relationType"), i.Json("properties"),
            i.Json("comments"), i.Text("libraryName"), i.Text("masterCopyPath"), i.Text("copyMode"), i.Flag("dryRun")),
        ["ManagePlcTableEntries"] = i => TiaMcp.Adapters.Native.Plc.PlcWatchPolicy.ValidateTableEntryRequest(
            i.Text("tableKind"), i.Text("tablePath"), i.Text("action"), i.Number("entryIndex"), i.Flag("confirmDelete"), i.Flag("dryRun"),
            i.Number("offset"), i.Number("limit")),
        ["ManagePlcTagDefinition"] = i => PlcTagEditingLogic.Validate(
            i.Text("action"), i.Text("kind"), i.Text("name"), i.Text("dataType"), i.Text("addressOrValue"), i.Json("properties"),
            i.Flag("dryRun")),
        ["ManageProjectCompilationSettings"] = i => SoftwareUnitDeepLogic.ValidateCompilationSettingsRequest(
            i.Text("action"), i.Json("properties"), i.Flag("dryRun")),
        ["ManageProjectUserManagement"] = i => ProjectSecurityLogic.ValidateUserManagement(
            i.Text("action"), i.Text("name"), i.Text("password"), i.Text("roleName"), i.Text("rightName"), i.Text("group"), i.Json("devicePath")),
        ["ManageSafetyActivationTest"] = i => SafetyValidationLogic.ValidateActivationTestRequest(
            i.Text("action"), i.Text("name"), i.Text("evaluationDeviceName"), i.Text("sourceName"), i.Text("masterCopyPath"), i.Text("newValue"),
            i.Text("filePath"), i.Text("exportOptions"), i.Text("documentInfoOptions"), i.Text("importOptions"), i.Flag("confirmDelete"),
            i.Flag("dryRun")),
        ["ManageSafetyActivationTestGroup"] = i => SafetyValidationLogic.ValidateGroupRequest(
            i.Text("action"), i.Text("name"), i.Text("masterCopyPath"), i.Text("newName"), i.Flag("confirmDelete"), i.Flag("dryRun")),
        ["ManageSafetyFunction"] = i => SafetyValidationLogic.ValidateSafetyFunctionRequest(
            i.Text("action"), i.Text("name"), i.Text("sourceName"), i.Json("properties"), i.Text("filePath"), i.Text("exportOptions"),
            i.Text("documentInfoOptions"), i.Text("importOptions"), i.Flag("confirmDelete"), i.Flag("dryRun")),
        ["ManageSafetyFunctionCondition"] = i => SafetyValidationLogic.ValidateConditionRequest(
            i.Text("action"), i.Number("index"), i.Text("deviceName"), i.Text("signalUsage"), i.Text("signalName"), i.Json("properties"),
            i.Flag("dryRun")),
        ["ManageSivarcBlockDefinition"] = i => SivarcLogic.ValidateDefinitionRequest(
            i.Text("blockPath"), i.Text("kind"), i.Text("name"), i.Text("action"), i.Json("properties"), i.Json("texts"), i.Flag("confirmDelete"),
            i.Flag("dryRun")),
        ["ManageSivarcRuleContainer"] = i => SivarcLogic.ValidateContainerRequest(
            i.Text("category"), i.Text("kind"), i.Text("path"), i.Text("action"), i.Text("typePath"), i.Text("typeVersion"),
            i.Flag("confirmDelete"), i.Flag("dryRun")),
        ["ManageSivarcScreenLayout"] = i => SivarcLogic.ValidateLayoutRequest(
            i.Text("screenName"), i.Text("action"), i.Text("filePath"), i.Flag("dryRun"), i.Flag("allowRemoveScreenItems")),
        ["ManageSivarcTableRule"] = i => SivarcLogic.ValidateRuleRequest(
            i.Text("category"), i.Text("tablePath"), i.Text("rulePath"), i.Text("kind"), i.Text("action"), i.Json("properties"),
            i.Json("references"), i.Json("deviceSelection"), i.Text("masterCopyPath"), i.Text("createOption"), i.Flag("confirmDelete"),
            i.Flag("dryRun")),
        ["ManageStartdriveParameter"] = i => StartdriveLogic.ValidateParameterWriteRequest(
            i.Text("parameter"), i.Text("action")),
        ["ManageSyslogServers"] = i => SecurityDeepLogic.ValidateSyslogRequest(
            i.Text("scope"), i.Text("action"), i.Text("name"), i.Map("properties"), i.Json("devicePath"), i.Text("serverAddress"),
            i.Number("serverPort"), i.Flag("confirmDelete"), i.Flag("dryRun")),
        ["ManageTeamcenterConnection"] = i => TeamcenterLogic.ValidateConnectionRequest(
            i.Text("action"), i.Text("userName"), i.Text("password"), i.Text("group"), i.Text("role"), i.Text("hostUrl"), i.Text("instance"),
            i.Text("loginUrl"), i.Text("applicationId"), i.Flag("dryRun")),
        ["ManageTeamcenterDataset"] = i => TeamcenterLogic.ValidateDatasetRequest(
            i.Text("action"), i.Text("itemId"), i.Text("revisionId"), i.Text("datasetType"), i.Text("datasetName"), i.Text("itemType"),
            i.Text("tiaObjectName"), i.Text("itemName"), i.Text("localCacheOption"), i.Flag("dryRun")),
        ["ManageTeamcenterWorkflow"] = i => TeamcenterLogic.ValidateWorkflowRequest(
            i.Text("action"), i.Text("target"), i.Text("libraryName"), i.Text("itemType"), i.Text("itemId"), i.Text("revisionId"),
            i.Text("localCacheOption"), i.Json("itemDetails"), i.Json("revisionDetails"), i.Json("customAttributes"), i.Flag("confirmSave"),
            i.Flag("dryRun")),
        ["ManageTechnologyExtensions"] = i => StartdriveLogic.ValidateTechnologyExtensionRequest(
            i.Text("action"), i.Text("identifier"), i.Text("filePath"), i.Flag("confirmUninstallInUse"), i.Flag("dryRun")),
        ["ManageTestSuiteCase"] = i => TestSuiteLogic.ValidateManageRequest(
            i.Text("category"), i.Text("name"), i.Text("action"), i.Text("kind"), i.Text("newName"), i.Text("softwarePath"),
            i.Text("instanceName"), i.Text("executionMode"), i.Text("opcUaServerAddress"), i.Text("serverInterfaceType"),
            i.Text("interfaceFolderPath"), i.Text("updateOptions"), i.Json("scope"), i.Text("targetName"), i.Text("masterCopyPath"),
            i.Flag("dryRun")),
        ["ManageTransferArea"] = i => HardwareNetworkLogic.ValidateTransferAreaRequest(
            i.Text("kind"), i.Text("action"), i.Text("name"), i.Text("type"), i.Number("positionNumber"), i.Number("extendedPositionNumber"),
            i.Number("length"), i.Number("ruleIndex")),
        ["ManageUmcUsers"] = i => SecurityDeepLogic.ValidateUmcRequest(
            i.Text("kind"), i.Text("action"), i.Text("name"), i.Text("newName"), i.Text("roleName"), i.Text("serverUserName"),
            i.Text("serverPassword"), i.Flag("confirmChange"), i.Flag("dryRun")),
        ["SynchronizeLibrary"] = i => LibraryDeepLogic.ValidateSyncRequest(
            i.Text("action"), i.Text("libraryName"), i.Text("targetLibraryName"), LibraryDeepLogic.ParseScopes(i.Json("scopeSoftwarePaths")),
            LibraryDeepLogic.ParseSelection(i.Json("selection")), i.Text("forceUpdateMode"), i.Text("deleteUnusedVersionsMode"),
            i.Text("structureConflictResolutionMode"), i.Text("cleanUpMode")),
    };
}
