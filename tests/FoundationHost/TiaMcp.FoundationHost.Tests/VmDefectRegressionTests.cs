extern alias enginehost;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.PlcWorker;
using TiaMcp.WorkerChannel;
using TiaMcp.Logic.V4.Inputs;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class VmDefectRegressionTests
{
    [Theory]
    [InlineData(@"C:\Projects\Example.ap21")]
    [InlineData("C:/Projects/Example.ap21")]
    [InlineData("/Projects/Example.ap21")]
    [InlineData(@"\\server\share\Example.ap21")]
    public void Expected_project_paths_are_rejected_before_any_worker_call(string project)
    {
        using var scope = EngineHostParity.EnterScope();
        var result = Host.McpServer.CallTool("GetUnifiedRuntimeSettings", new ToolArguments(JsonSerializer.SerializeToElement(
            new { softwarePath = "HMI_RT_1", expectedProject = project })));
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("expectedProject", (string?)result.StructuredContent?["error"]?["details"]?["parameter"]);
        Assert.Contains("NAME", (string)result.StructuredContent!["error"]!["message"]!);
        Assert.False((bool)result.StructuredContent["meta"]!["requiresSessionReset"]!);
    }

    [Fact]
    public void Confirmed_tag_creation_with_unsupported_binding_is_partial_with_reset()
    {
        using var scope = EngineHostParity.EnterScope();
        var response = new Host.ResponseMessage { Message = "Binding unsupported", Meta = new JsonObject {
            ["success"] = false, ["operationSuccess"] = false, ["mayHaveChanged"] = true,
            ["tagCreateConfirmed"] = true, ["v4FailureCode"] = "UNSUPPORTED_CAPABILITY" } };
        var result = Host.UnifiedHmiContract.Map("EnsureUnifiedHmiTag", response, true, true);
        Assert.Equal("PARTIAL_FAILURE", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("partial", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.True((bool)result.StructuredContent!["meta"]!["requiresSessionReset"]!);
        Assert.Contains("no rollback", (string)result.StructuredContent["data"]!["partialWrite"]!);
    }

    public static string CreatePrimitive(string identifier, string name) => identifier + ":" + name;
    public static bool PreviewPrimitive(bool dryRun = true) => dryRun;

    [Fact]
    public void Validated_transport_preview_does_not_become_an_unknown_native_argument()
    {
        var method = typeof(VmDefectRegressionTests).GetMethod(nameof(CreatePrimitive))!;
        var input = WorkerJson.ParseArguments("{\"identifier\":\"OrderNumber:6ES7 512-1DK01-0AB0/V2.9\",\"name\":\"MCP_TEST_PLC\",\"dryRun\":false}");
        var values = WorkerJson.FacadeArguments(method.GetParameters(), input);
        Assert.Equal("OrderNumber:6ES7 512-1DK01-0AB0/V2.9:MCP_TEST_PLC", method.Invoke(null, values));
        input["unknown"] = JsonSerializer.SerializeToElement(true);
        Assert.Throws<ArgumentException>(() => WorkerJson.FacadeArguments(method.GetParameters(), input));
        var preview = typeof(VmDefectRegressionTests).GetMethod(nameof(PreviewPrimitive))!;
        Assert.False((bool)preview.Invoke(null, WorkerJson.FacadeArguments(preview.GetParameters(), WorkerJson.ParseArguments("{\"dryRun\":false}")))!);
    }

    [Fact]
    public void Owner_lane_shutdown_detaches_before_discarding_retained_handles()
    {
        var state = new WorkerSessionOutcomeState(); state.MarkUncertain();
        bool detached = false; int invalidations = 0, disposals = 0;
        var recovery = new WorkerSessionRecovery(state, () => detached, () => { }, () => { }, _ => { },
            () => invalidations++, () => disposals++, detachOnShutdown: () => detached = true);
        recovery.DisposeSession();
        Assert.True(detached); Assert.Equal(0, invalidations); Assert.Equal(1, disposals);
        Assert.True(state.RequiresReset);
    }

    [Fact]
    public void Unified_panel_major_guard_is_shared_and_runs_before_any_creation()
    {
        Assert.Throws<AdapterPreconditionException>(() => DeviceCreationRules.RequireUnifiedPanelVersion(
            "OrderNumber:6AV2 128-3QB06-0AX1/20.0.0.0", "", 21));
        DeviceCreationRules.RequireUnifiedPanelVersion("OrderNumber:6AV2 128-3QB06-0AX1/21.0.0.0", "", 21);
        var request = new PlcDeviceAddRequest { Release = "21", Project = @"C:\Fixture\Fixture.ap21", ProcessId = 42,
            RootIdentity = "root", PreferredMlfb = "6AV2 128-3QB06-0AX1", PreferredVersion = "20.0.0.0", Name = "Panel", Family = "HMI" };
        int creates = 0;
        Assert.Throws<AdapterPreconditionException>(() => PlcDeviceAddPolicy.Run(request,
            () => new[] { new PlcHardwareCatalogCandidate { ArticleNumber = request.PreferredMlfb, Version = request.PreferredVersion,
                TypeIdentifier = "OrderNumber:" + request.PreferredMlfb + "/" + request.PreferredVersion } },
            () => Array.Empty<PlcDeviceAddItem>(), () => { }, (_, _) => { creates++; return null!; }));
        Assert.Equal(0, creates);
    }

    [Fact]
    public void Creation_verifies_the_existing_ungrouped_system_parent()
    {
        var request = new PlcDeviceAddRequest { Release = "21", Project = @"C:\Fixture\Fixture.ap21", ProcessId = 42,
            RootIdentity = "root", PreferredMlfb = "6ES7 512-1DK01-0AB0", PreferredVersion = "V2.9", Name = "PLC", Family = "ET 200SP" };
        var items = new List<PlcDeviceAddItem> { new() { Name = "$ungrouped", Identity = "system", ParentIdentity = "root",
            ParentVerified = true, IsGroup = true, IsUngroupedSystemGroup = true } };
        int creates = 0;
        PlcDeviceAddResult Run() => PlcDeviceAddPolicy.Run(request,
            () => new[] { new PlcHardwareCatalogCandidate { ArticleNumber = request.PreferredMlfb, Version = request.PreferredVersion,
                TypeIdentifier = "OrderNumber:" + request.PreferredMlfb + "/" + request.PreferredVersion } },
            () => items, () => { }, (_, name) => {
                creates++; var created = new PlcDeviceAddItem { Name = name, Identity = "new", ParentIdentity = "system", ParentVerified = true };
                items.Add(created); return created;
            });
        request.ExpectedHash = Run().PlanHash; request.ExpectedProject = request.Project; request.Confirm = true; request.DryRun = false;
        Assert.True(Run().Executed); Assert.Equal(1, creates);
    }

    [Theory]
    [InlineData("CreatePlcBlockGroup")]
    [InlineData("MovePlcBlockToGroup")]
    [InlineData("RepairAndReimportPlcBlock")]
    [InlineData("ImportPlcTagTablesFromDirectory")]
    [InlineData("CreatePlcTypeGroup")]
    [InlineData("DeletePlcBlock")]
    [InlineData("DescribePlcBlockLogic")]
    [InlineData("ListPlcForceTables")]
    public void Real_worker_reply_shape_is_valid_without_step_metadata(string tool)
    {
        var reply = new PlcSoftwareReply { Found = true, Created = new[] { "MCP_TEST_Grp", "Sub" },
            Data = new() { ["groupPath"] = "MCP_TEST_Grp/Sub" } };
        var json = JsonNode.Parse(WorkerJson.Serialize(reply));
        var operation = PortedFamilies.ForTool(tool).OperationPrefix + ".ExecutePlcOrganisation";
        HardwareFamilyWire.Validate(operation, json);
        var state = new WorkerOutcomeState();
        state.AcceptResult(operation, new JsonObject(), json);
        Assert.False(state.Poisoned);
        json!["Unexpected"] = true;
        Assert.Throws<IOException>(() => HardwareFamilyWire.Validate(operation, json));
        json.AsObject().Remove("Unexpected"); json["Created"] = new JsonArray(42);
        Assert.Throws<IOException>(() => HardwareFamilyWire.Validate(operation, json));
    }

    [Theory]
    [InlineData("GetDccObject")]
    [InlineData("GetDriveParameters")]
    [InlineData("CompareLibraries")]
    [InlineData("CompareProjects")]
    [InlineData("GetSivarcRuleTree")]
    [InlineData("ListSivarcRules")]
    [InlineData("GetSafetyBlockSignatures")]
    [InlineData("ListSafetyActivationTests")]
    public void Failed_read_reset_is_truthful_and_diagnostics_survive(string tool)
    {
        var error = new WorkerOperationException("Native option unavailable", -32603, "read-failed",
            "{\"exceptionType\":\"EngineeringTargetInvocationException\"}");
        error.Data["foundationRequestSent"] = true;
        error.Data["foundationSessionPoisoned"] = true;
        var result = FoundationV4Result.Failure("21", tool, "vm-read", true, false, null, error);
        Assert.Equal("read-failed", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.True((bool)result.StructuredContent!["meta"]!["requiresSessionReset"]!);
        Assert.Contains("DisconnectPortal", (string)result.StructuredContent["data"]!["recovery"]!);
        Assert.Equal("Native option unavailable", (string?)result.StructuredContent["error"]?["details"]?["nativeMessage"]);
        error.Data["foundationSessionPoisoned"] = false;
        var ordinary = FoundationV4Result.Failure("21", tool, "vm-read", true, false, null, error);
        Assert.False((bool)ordinary.StructuredContent!["meta"]!["requiresSessionReset"]!);
        var state = new WorkerOutcomeState(); state.Failed(true, error); Assert.False(state.Poisoned);
        var reconciled = FoundationV4Result.ReconcileWorker(ordinary, true,
            new JsonObject { ["exceptionType"] = "EngineeringTargetInvocationException", ["nativeMessage"] = "Native option unavailable" });
        Assert.True((bool)reconciled.StructuredContent!["meta"]!["requiresSessionReset"]!);
        Assert.Equal("read-failed", (string?)reconciled.StructuredContent["meta"]?["outcome"]);
        Assert.True(JsonNode.DeepEquals(reconciled.StructuredContent, JsonNode.Parse(
            ((global::ModelContextProtocol.Protocol.TextContentBlock)Assert.Single(reconciled.Content)).Text)));
    }

    [Fact]
    public void Timed_out_read_requires_reset_and_exact_restart_guidance()
    {
        var error = new TimeoutException("Worker timed out; native outcome is unknown.");
        error.Data["foundationRequestSent"] = true;
        error.Data["foundationSessionPoisoned"] = true;
        error.Data["workerOutcomeUnknown"] = true;
        var result = FoundationV4Result.Failure("21", "SearchInstalledGsdDevices", "vm-timeout", true, false, null, error);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.True((bool)result.StructuredContent!["meta"]!["requiresSessionReset"]!);
        Assert.Contains("TIA must be restarted", (string)result.StructuredContent["data"]!["recovery"]!);
        Assert.Equal(TimeSpan.FromSeconds(120), WorkerTimeoutPolicy.Defaults["default"]);
        var state = new WorkerOutcomeState(); state.Failed(true, error); Assert.True(state.Poisoned);
    }

    [Fact]
    public void Reflection_wrappers_retain_the_actual_native_cause_without_changing_wire_fields()
    {
        var native = new InvalidOperationException("Native capability unavailable");
        var wrapped = new System.Reflection.TargetInvocationException(native);
        var evidence = JsonNode.Parse(WorkerJson.Evidence(wrapped))!;
        Assert.Equal(nameof(InvalidOperationException), (string?)evidence["exceptionType"]);
        Assert.Equal(native, WorkerFailurePolicy.DiagnosticCause(wrapped));
    }

    [Fact]
    public void Authored_admission_is_preserved_instead_of_exposing_an_internal_file_failure()
    {
        var refusal = new InvalidOperationException(TiaOpenness.Shared.SessionBehavior.LeaseReserved,
            new IOException("Internal private lease file failure"));
        Assert.Same(refusal, WorkerFailurePolicy.DiagnosticCause(refusal));
        Assert.Equal(TiaOpenness.Shared.SessionBehavior.LeaseReserved, WorkerFailurePolicy.DiagnosticCause(refusal).Message);
        Assert.Equal(nameof(InvalidOperationException), (string?)JsonNode.Parse(WorkerJson.Evidence(refusal))?["exceptionType"]);
    }

    [Theory]
    [InlineData("ET 200SP", "6ES7 512-1DK01-0AB0", "V2.9")]
    [InlineData("S7-1500", "6ES7 516-3AN02-0AB0", "V3.1")]
    [InlineData("HMI", "6AV2 123-3GB32-0AW0", "V21.0")]
    public void Exact_catalog_rows_are_not_restricted_to_documentation_cpus(string family, string article, string version)
    {
        var request = new PlcDeviceAddRequest { Release = "21", Project = @"C:\Fixture\Fixture.ap21", ProcessId = 42,
            RootIdentity = "root", PreferredMlfb = article, PreferredVersion = version, Name = "MCP_TEST_Device", Family = family };
        var items = new List<PlcDeviceAddItem>(); int creates = 0;
        var identifier = "OrderNumber:" + article + "/" + version;
        PlcDeviceAddResult Run() => PlcDeviceAddPolicy.Run(request,
            () => new[] { new PlcHardwareCatalogCandidate { ArticleNumber = article, Version = version, TypeIdentifier = identifier } },
            () => items, () => { }, (type, name) => {
                Assert.Equal(identifier, type); creates++;
                var item = new PlcDeviceAddItem { Name = name, Identity = "new", ParentIdentity = "root", ParentVerified = true };
                items.Add(item); return item;
            });
        var preview = Run(); Assert.Equal(0, creates);
        request.ExpectedHash = preview.PlanHash; request.ExpectedProject = request.Project; request.Confirm = true; request.DryRun = false;
        var result = Run(); Assert.Equal(1, creates); Assert.True(result.Executed); Assert.False(result.RequiresSessionReset);
        var wireRequest = JsonSerializer.SerializeToNode(new { preferredMlfb = article, preferredVersion = version,
            deviceName = request.Name, family, dryRun = false, confirm = true, expectedPlanHash = preview.PlanHash,
            expectedProjectFile = request.Project })!.AsObject();
        DeviceAddContract.Validate(JsonSerializer.SerializeToNode(result), wireRequest);
        Assert.Throws<AdapterPreconditionException>(() => Run()); Assert.Equal(1, creates);
    }
}
