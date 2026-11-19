extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using Xunit;
using Engine = toolpipeline::TiaMcp.FoundationHost;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using PortalFailure = TiaMcpServer.Siemens.PortalException;
using PortalCode = TiaMcpServer.Siemens.PortalErrorCode;

public sealed class NotFoundTargetTests
{
    [Theory]
    [InlineData("ioType/startAddress")]
    [InlineData("other, ioType, startAddress, ioType")]
    public void Combined_parameters_keep_only_distinct_declared_fields(string parameters)
    {
        using var worker = new MetadataWorker();
        using var contextWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var catalog = new Engine.FoundationCatalog("21", LegacyHostToolRegistry.Create(worker, "21", false, hostPorts: false));
        using var session = Engine.EngineHostConfiguration.Enter(new Engine.EngineSessionContext(contextWorker) { Catalog = catalog });
        var details = Host.McpServer.ArgumentDetails("SetDeviceItemIoAddress", parameters);
        Assert.Equal("/ioType", details.Parameter);
        Assert.Equal(new[] { "/ioType", "/startAddress" }, details.Parameters);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Non_string_parameter_evidence_is_absent(string json)
    {
        var evidence = new JsonObject { ["parameter"] = JsonNode.Parse(json) };
        Assert.Null(Host.McpServer.MissingTargetDetails("GetDeviceAddressing", evidence: evidence).Target);
        Assert.Equal("", Host.McpServer.ArgumentDetails("GetDeviceAddressing", null, evidence).Parameter);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Non_string_parameter_evidence_falls_back_to_a_declared_rejectedParameter(string json)
    {
        using var worker = new MetadataWorker();
        using var contextWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var catalog = new Engine.FoundationCatalog("21", LegacyHostToolRegistry.Create(worker, "21", false, hostPorts: false));
        using var session = Engine.EngineHostConfiguration.Enter(new Engine.EngineSessionContext(contextWorker) { Catalog = catalog });
        var evidence = new JsonObject { ["parameter"] = JsonNode.Parse(json), ["rejectedParameter"] = "devicePath" };
        Assert.Equal("/devicePath", Host.McpServer.ArgumentDetails("GetDeviceAddressing", null, evidence).Parameter);
        Assert.Equal("/devicePath", Host.McpServer.MissingTargetDetails("GetDeviceAddressing", evidence: evidence).Target);
    }

    [Theory]
    [InlineData("devicePath", "/devicePath")]
    [InlineData("privateCallerKey", null)]
    public void Already_exists_targets_only_declared_parameters(string parameter, string? expected)
    {
        using var worker = new MetadataWorker();
        using var contextWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var catalog = new Engine.FoundationCatalog("21", LegacyHostToolRegistry.Create(worker, "21", false, hostPorts: false));
        using var session = Engine.EngineHostConfiguration.Enter(new Engine.EngineSessionContext(contextWorker) { Catalog = catalog });
        using var selected = Host.HardwareContract.UseRelease("21");
        var result = Host.HardwareToolContract.MapResult("GetDeviceAddressing", new Host.ResponseMessage {
            Meta = new JsonObject { ["status"] = "AlreadyExists", ["parameter"] = parameter,
                ["operationSuccess"] = false, ["mayHaveChanged"] = false }
        }, readOnly: true, current: false);
        Assert.Equal("ALREADY_EXISTS", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(expected, (string?)result.StructuredContent?["error"]?["details"]?["target"]);
    }

    private static readonly string[] Releases = ["14sp1", "15.1", "16", "17", "18", "19", "20", "21"];
    private static readonly (string Family, string Tool, string Parameter)[] Families = [
        ("classic-motion", "GetMotionAxisConfiguration", "softwarePath"),
        ("drive", "GetDriveParameters", "devicePath"),
        ("engineering", "GetPlcOpcUaConfiguration", "softwarePath"),
        ("hmi-exchange", "ListHmiScreens", "softwarePath"),
        ("hmi-inspection", "GetHmiProgramInfo", "softwarePath"),
        ("library", "GetLibraryOverview", "libraryName"),
        ("optional", "ListTestSuiteCases", "name"),
        ("plc", "GetPlcBlockInfo", "blockPath"),
        ("runtime", "GetOnlineState", "softwarePath"),
        ("security", "CompareProjects", "softwarePath"),
        ("unified", "EnsureUnifiedHmiTagTable", "hmiSoftwarePath"),
        ("plc-exchange", "ExportPlcBlock", "blockPath"),
        ("offline", "PlanArtifactImportOrder", "artifacts") ];

    public static IEnumerable<object[]> EvidenceCases()
    {
        foreach (string release in Releases)
        foreach (var (family, tool, parameter) in Families)
        {
            if (family == "unified" && release is "14sp1" or "15.1" or "16" or "17" or "18") continue;
            if (family == "engineering" && release == "14sp1") continue;
            if (family == "optional" && release is "14sp1" or "15.1" or "16") continue;
            foreach (string scenario in new[] { "parameter", "rejectedParameter", "undeclared", "absent" })
                yield return new object[] { release, family, tool, parameter, scenario };
        }
    }

    [Theory, MemberData(nameof(EvidenceCases))]
    public void Not_found_evidence_targets_only_declared_parameters(string release, string family, string tool, string parameter, string scenario)
    {
        using var worker = new MetadataWorker();
        using var contextWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var catalog = new Engine.FoundationCatalog(release, LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false));
        using var session = Engine.EngineHostConfiguration.Enter(new Engine.EngineSessionContext(contextWorker) { Catalog = catalog });
        using var selected = Host.HardwareContract.UseRelease(release);
        Assert.NotNull(catalog.Find(tool));
        var evidence = new JsonObject { ["status"] = "NotFound", ["operationSuccess"] = false };
        if (scenario != "absent") evidence[scenario == "rejectedParameter" ? scenario : "parameter"] = scenario == "undeclared" ? "privateCallerKey" : parameter;
        if (family is "engineering" or "library" or "plc" or "runtime" or "unified") evidence["v4Rejection"] = "NOT_FOUND";
        if (family == "optional") evidence["rejectionCode"] = "NOT_FOUND";
        var response = new Host.ResponseMessage { Message = "Synthetic missing target.", Meta = evidence };
        var result = family switch {
            "classic-motion" => Host.ClassicMotionToolContract.Map(tool, response, true, false),
            "drive" => Host.DriveToolContract.Map(tool, response, false, false),
            "engineering" => Host.EngineeringToolContract.Map(tool, response, false),
            "hmi-exchange" => Host.HmiExchangeContract.Map(tool, response, false, false),
            "hmi-inspection" => Host.HmiInspectionContract.Map(tool, response, false, false),
            "library" => Host.LibraryToolContract.Map(tool, response, false, false),
            "optional" => Host.OptionalPackageContract.Map(tool, response, false),
            "plc" => Host.PlcToolContract.Map(tool, response, false, false),
            "runtime" => Host.RuntimeToolContract.Map(tool, response, true, false),
            "security" => Host.SecurityToolContract.MapResult(tool, response, true, false),
            "unified" => Host.UnifiedHmiContract.Map(tool, response, false, false),
            "plc-exchange" => Host.PlcExchangeContract.Map(tool, response, false, false),
            _ => Host.OfflineContracts.Map(tool, response)
        };
        AssertMissing(result, scenario is "parameter" or "rejectedParameter" ? "/" + parameter : null);
    }

    public static IEnumerable<object[]> ExceptionCases() =>
        from release in Releases
        from family in new[] { "runtime", "plc-move", "plc-exchange", "plc-exchange-file", "plc-exchange-directory" }
        from parameter in new string?[] { "declared", "privateCallerKey", null }
        select new object[] { release, family, parameter! };

    [Theory, MemberData(nameof(ExceptionCases))]
    public void Not_found_exceptions_retain_only_known_declared_parameters(string release, string family, string? parameter)
    {
        using var worker = new MetadataWorker();
        using var contextWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var catalog = new Engine.FoundationCatalog(release, LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false));
        using var session = Engine.EngineHostConfiguration.Enter(new Engine.EngineSessionContext(contextWorker) { Catalog = catalog });
        using var selected = Host.HardwareContract.UseRelease(release);
        string tool = family switch { "plc-move" => "MovePlcBlockToGroup", "runtime" => "GetOnlineState", _ => "ExportPlcBlock" };
        string declared = family switch { "runtime" => "softwarePath", "plc-move" => "blockName", _ => "blockPath" };
        parameter = parameter == "declared" ? declared : parameter;
        Exception error = family switch {
            "plc-exchange-file" => new FileNotFoundException("Synthetic missing input file."),
            "plc-exchange-directory" => new DirectoryNotFoundException("Synthetic missing input directory."),
            _ => new PortalFailure(PortalCode.NotFound, "Synthetic missing target.",
                inner: family == "runtime" && parameter != null ? new ArgumentException("Synthetic parameter evidence.", parameter) : null)
        };
        if (family == "plc-move") error.Data["nativeResultEvidence"] = new JsonObject { ["deleteStarted"] = false, ["parameter"] = parameter };
        var result = family switch {
            "runtime" => Host.RuntimeToolContract.Run(tool, true, false, () => throw error),
            "plc-move" => Host.PlcToolContract.Failure(tool, error, true, false),
            _ => Host.PlcExchangeContract.Run(tool, () => {
                Host.PlcExchangeContract.Observe("parameter", JsonValue.Create(parameter));
                throw error;
            }, false, false)
        };
        AssertMissing(result, parameter == declared ? "/" + declared : null);
    }

    public static IEnumerable<object[]> UnknownTargets() => from release in Releases
        from family in new[] { "engineering", "library", "hardware", "offline" }
        where family != "engineering" || release != "14sp1"
        select new object[] { release, family };

    [Theory, MemberData(nameof(UnknownTargets))]
    public void Missing_resources_without_parameter_evidence_keep_a_null_target(string release, string family)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var error = new PortalFailure(PortalCode.NotFound, "Synthetic missing target without parameter evidence.");
        var result = family switch {
            "engineering" => Host.EngineeringToolContract.Run("GetPlcOpcUaConfiguration", false, () => throw error),
            "library" => Host.LibraryToolContract.Run("GetLibraryOverview", false, false, () => throw error),
            "hardware" => Host.HardwareContract.Run("GetDeviceItemTree", () => throw error, false, false),
            _ => Host.OfflineContracts.Run("PlanArtifactImportOrder", () => throw new FileNotFoundException("Synthetic missing input."))
        };
        AssertMissing(result, null);
    }

    public static IEnumerable<object[]> AdapterNotFoundCases()
    {
        foreach (string release in Releases)
        foreach (string route in new[] { "exchange", "offline", "unified", "foundation", "hardware" })
        {
            if (route == "unified" && release is "14sp1" or "15.1" or "16" or "17" or "18") continue;
            foreach (string? parameter in new string?[] { "declared", "table", "privateCallerKey", null })
                yield return new object[] { release, route, parameter! };
        }
    }

    [Theory, MemberData(nameof(AdapterNotFoundCases))]
    public void Adapter_not_found_targets_only_the_tools_declared_parameter(string release, string route, string? parameter)
    {
        using var worker = new MetadataWorker();
        using var contextWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var catalog = new Engine.FoundationCatalog(release, LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false));
        using var session = Engine.EngineHostConfiguration.Enter(new Engine.EngineSessionContext(contextWorker) { Catalog = catalog });
        using var selected = Host.HardwareContract.UseRelease(release);
        string tool = route switch {
            "exchange" => "ExportPlcTagTable", "offline" => "PlanArtifactImportOrder", "unified" => "EnsureUnifiedHmiTagTable",
            "foundation" => "GetPlcBlockInfo", _ => "GetDeviceItemTree" };
        string declared = route switch {
            "exchange" => "tagTableName", "offline" => "artifacts", "unified" => "hmiSoftwarePath",
            "foundation" => "blockPath", _ => "deviceItemPath" };
        Assert.True(catalog.Find(tool)!.Tool.InputSchema.GetProperty("properties").TryGetProperty(declared, out _));
        parameter = parameter == "declared" ? declared : parameter;
        var error = AdapterPreconditionException.NotFound("Synthetic missing adapter target.", parameter);
        var result = route switch {
            "exchange" => Host.PlcExchangeContract.Run(tool, () => throw error, false, false),
            "offline" => Host.OfflineContracts.Run(tool, () => throw error),
            "unified" => Host.UnifiedHmiContract.Run(tool, false, false, () => throw error),
            "foundation" => FoundationV4Result.Failure(release, tool, "adapter-not-found", false, false, null, error),
            _ => Host.HardwareContract.Run(tool, () => throw error, false, false)
        };
        AssertMissing(result, parameter == declared ? "/" + declared : null);
    }

    [Fact]
    public void Logic_not_found_without_a_catalog_check_keeps_a_null_target()
    {
        var error = HostBehavior.FailureError(TiaOpenness.Shared.HostFailureKind.Precondition, "table",
            new Dictionary<string, JsonElement>(), condition: "not-found");
        Assert.Null(Assert.IsType<NotFoundDetails>(error.Details).Target);
    }

    private static void AssertMissing(CallToolResult result, string? pointer)
    {
        var body = result.StructuredContent!;
        Assert.Equal("NOT_FOUND", (string?)body["error"]!["code"]);
        Assert.Equal(pointer, (string?)body["error"]!["details"]!["target"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
    }

    private sealed class MetadataWorker : IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
            => throw new InvalidOperationException("Metadata and synthetic replies must not dispatch a worker operation.");
        public void Dispose() { }
    }
}
