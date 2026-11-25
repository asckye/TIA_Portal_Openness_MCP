extern alias toolpipeline;
using System.Diagnostics;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcp.WorkerChannel;
using TiaMcp.Engine.Tests.Shared;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Services = toolpipeline::TiaMcpServer.Siemens.Services;

public sealed class NativeRefusalEnvelopeTests
{
    private static async Task<JsonObject> Reply(string scenario)
    {
        string file = Path.Combine(AppContext.BaseDirectory, "verified-net48", "TiaMcp.VerifiedImport.Net48.Tests.exe");
        using var process = Process.Start(new ProcessStartInfo(file, "--worker-reply " + scenario) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TestDeadlines.Hang);
        Assert.True(process.ExitCode == 0, await errors);
        return JsonNode.Parse(await output)!.AsObject();
    }

    public static IEnumerable<object[]> VerifiedFailures => from row in VerifiedImportOutcomeTests.Releases()
        from scenario in new[] { "verified-compile/error", "verified-compile/recorded", "verified-compile/count", "verified-compile/messages-unavailable",
            "verified-compile/thrown", "verified-refusal/unchanged", "verified-refusal/date", "verified-refusal/unmarked", "verified-refusal/prior-change" }
        select new object[] { row[0], scenario };

    [Theory, MemberData(nameof(VerifiedFailures))]
    public async Task Verified_import_failure_reaches_the_client_with_its_mutation_evidence(string release, string scenario)
    {
        const string tool = "ImportPlcBlockVerified";
        bool compile = scenario.StartsWith("verified-compile/", StringComparison.Ordinal);
        bool priorChange = scenario == "verified-refusal/prior-change";
        bool unknown = priorChange || scenario is "verified-refusal/date" or "verified-refusal/unmarked" or "verified-compile/thrown";
        var wire = await Reply(priorChange ? "verified-refusal/unchanged" : scenario);
        if (priorChange) wire["MayHaveChanged"] = true;
        var channel = new WorkerReplyFixture();
        using var worker = new DirectoryImportRegressionTests.Worker((operation, input) => {
            Assert.Equal("plc-software.ExecutePlcOrganisation", operation);
            return channel.Accept(operation, input, wire);
        });
        var raw = PortedToolContract.Create(worker, release).Single(t => t.ProtocolTool.Name == tool);
        var entry = new FoundationV4Tool(raw, release, () => new TiaOpenness.Shared.ApprovalSettings(false, 5));
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release, worker: worker, replacementTool: entry);
        using var session = fixture.EnterSession();
        string input = Path.Combine(fixture.Root, "candidate.xml");
        bool recorded = scenario == "verified-compile/recorded";
        var sources = Path.Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory), "tests/KeptCode/TiaMcp.KeptCode.Tests/Fixtures/VerifiedImport");
        File.Copy(Path.Combine(sources, recorded ? "14sp1-MCP_RC_DB2_bogus.xml" : compile ? "17-T6.xml" : "17-T4.xml"), input);
        var body = (await fixture.Call(tool, new { softwarePath = "PLC", blockPath = compile && !recorded ? "MCP_RC_DB6" : "MCP_RC_DB2", importPath = input,
            evidenceDirectory = fixture.Root, dryRun = false, compileAfterImport = compile, previewToken = "fixture" })).StructuredContent!;
        if (unknown)
        {
            Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
            Assert.Equal("unknown", (string?)body["meta"]?["outcome"]);
            if (priorChange) Assert.True((bool?)body["data"]?["evidence"]?["mayHaveChanged"]);
            Assert.NotEmpty(toolpipeline::TiaMcp.FoundationHost.UnsettledCalls.For(worker).Snapshot());
            return;
        }
        Assert.True((string?)body["error"]?["code"] == (compile ? "COMPILE_ERRORS" : "NATIVE_OPERATION_FAILED"), body.ToJsonString());
        Assert.Equal("failed", (string?)body["meta"]?["outcome"]);
        Assert.Equal("completed", (string?)body["meta"]?["execution"]);
        var evidence = body["data"]!["evidence"]!;
        Assert.Equal(compile, (bool?)evidence["mayHaveChanged"]);
        Assert.NotNull(evidence["backup"]);
        if (compile)
        {
            Assert.Equal(1, (int?)body["error"]?["details"]?["errorCount"]);
            Assert.Equal(recorded ? 0 : 2, (int?)body["error"]?["details"]?["warningCount"]);
            if (scenario == "verified-compile/error" || recorded) Assert.Contains("Data type NoSuchType_XYZ is unknown.", (string?)body["error"]?["message"]);
            Assert.Single(evidence["cleanupRequired"]!.AsArray());
            Assert.Equal(scenario == "verified-compile/count" ? "Warning" : "Error", (string?)evidence["compileState"]);
        }
        else
        {
            Assert.Contains("Ghost", (string?)body["error"]?["message"]);
            Assert.NotNull(evidence["refusalProof"]);
            Assert.Null(evidence["cleanupRequired"]);
        }
        Assert.Empty(toolpipeline::TiaMcp.FoundationHost.UnsettledCalls.For(worker).Snapshot());
    }

    [Theory]
    [InlineData("multi-interface", "Online", "succeeded", "Second LAN")]
    [InlineData("standard-plc", "Online", "succeeded", "LAN")]
    [InlineData("missing-rh", "NotReachable", "rejected-before-operation", "RHOnlineProvider service not available")]
    [InlineData("non-rh-plc", "Offline", "rejected-before-operation", "RHOnlineProvider service not available")]
    [InlineData("apply-false", "Offline", "rejected-before-operation", "ApplyConfiguration returned false")]
    [InlineData("disconnect", null, "succeeded", "already offline")]
    [InlineData("disconnect-refused", null, "unknown", "Offline transition refused")]
    public async Task Online_transition_keeps_its_native_state_message_and_public_outcome(string scenario, string? state, string outcome, string message)
    {
        using var release = Host.HardwareContract.UseRelease("20");
        var wire = await Reply(scenario);
        bool disconnect = scenario.StartsWith("disconnect", StringComparison.Ordinal);
        string tool = disconnect ? "DisconnectOnlinePlc" : "ConnectOnlinePlc";
        Assert.False((bool?)wire["OutcomeUnknown"]);
        var native = wire["Value"]!;
        Assert.False(native["Meta"]?.AsObject().ContainsKey("writeOutcomeKnown") == true);
        Assert.DoesNotContain("mayHaveChanged", native["Meta"]?.ToJsonString() ?? "");
        Assert.Equal(state == "Online", (bool?)wire["MayHaveChanged"]);
        Assert.Contains(message, (string?)native["Message"]);
        var service = new Services.OnlineDownloadService((operation, _) => {
            Assert.Equal("plc-online.ExecuteOnline", operation);
            return wire;
        }, () => "Fixture.ap20");
        var body = Host.RuntimeToolContract.Run(tool, false, true, () => disconnect ? (object)service.GoOffline("PLC")
            : service.GoOnline("PLC", "192.0.2.3", null, null, null, scenario == "missing-rh" ? "primary" : scenario == "non-rh-plc" ? "backup" : "",
                null, false)).StructuredContent!;
        Assert.Equal(state, (string?)body["data"]?["state"]);
        Assert.Equal(outcome, (string?)body["meta"]?["outcome"]);
    }

    [Theory]
    [InlineData("SetDeviceItemIoAddress", false)]
    [InlineData("SetDeviceItemIoAddress", true)]
    [InlineData("SetDeviceItemAttribute", false)]
    [InlineData("SetPlcCpuSettings", false)]
    public async Task Hardware_setter_preserves_Siemens_detail(string tool, bool unknown)
    {
        using var release = Host.HardwareContract.UseRelease("20");
        var wire = await Reply(tool + (unknown ? "/changed" : ""));
        var service = new Services.HardwareDevicesService((_, _) => wire, () => true, () => "Fixture.ap20");
        var address = new Host.AddressesTools(new Host.HardwareAddressingService((_, _) => wire, () => true, () => "Fixture.ap20"));
        var response = tool switch {
            "SetDeviceItemIoAddress" => address.SetDeviceItemIoAddress("PLC/IO", "Input", 8, false),
            "SetDeviceItemAttribute" => service.SetDeviceItemAttribute("PLC/IO", "Value", "8"),
            _ => service.SetCpuCommonSettings("PLC/IO", """{"exactAttributes":{"Value":8}}""")
        };
        var body = Host.HardwareContract.Map(tool, response, true, false).StructuredContent!;
        string reason = tool == "SetDeviceItemIoAddress" ? "The start address is outside the CPU address range." : "The selected attribute is read-only.";
        Assert.Equal(unknown ? "unknown" : "completed", (string?)body["meta"]?["execution"]);
        Assert.Contains(reason, (string?)body["data"]?["evidence"]?["nativeMessage"]);
        Assert.Contains(reason, (string?)body["error"]?["message"]);
        if (!unknown)
        {
            Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
            Assert.Contains(reason, (string?)body["error"]?["details"]?["nativeMessage"]);
        }
    }

    [Theory, InlineData("ManagePlcSoftwareUnit", "name"), InlineData("ManageDeviceUserGroup", "newName")]
    public async Task Name_collisions_are_preconditions(string tool, string parameter)
    {
        using var release = Host.HardwareContract.UseRelease("17");
        var wire = await Reply(tool);
        var response = new Host.ResponseMessage { Message = (string)wire["Message"]!, Meta = wire["Meta"]!.AsObject() };
        var body = (tool == "ManageDeviceUserGroup" ? Host.HardwareToolContract.MapResult(tool, response, true, true)
            : Host.PlcToolContract.Map(tool, response, false, true)).StructuredContent!;
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Equal(parameter, (string?)body["error"]?["details"]?["parameter"]);
        Assert.Contains("Existing", (string?)body["error"]?["message"]);
    }

    public static IEnumerable<object[]> Symbols => from tool in new[] { "CreatePlcTag", "CreatePlcUserConstant", "CreatePlcTagTable", "ImportPlcTagTable" }
        from state in new[] { "unchanged", "changed", "unreadable" } select new object[] { tool, state };

    [Theory, MemberData(nameof(Symbols))]
    public async Task Issued_symbol_refusal_reaches_the_client_with_the_Siemens_reason(string tool, string state)
    {
        var definition = FoundationTools.Definitions.Single(d => d.Name == tool);
        using var worker = new SymbolWorker(tool + "/" + state, definition.Operation);
        var host = new FoundationV4Tool(new FoundationTool(definition, worker), "20", () => new(false, 1));
        var args = new JsonObject { ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = "C:/Fixture.ap20" };
        string? file = null;
        if (tool == "ImportPlcTagTable")
        {
            file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(file, "<Document><Engineering version='V20'/><SW.Tags.PlcTagTable ID='0'>" +
                "<AttributeList><Name>Signals</Name></AttributeList></SW.Tags.PlcTagTable></Document>");
            args["softwarePath"] = "PLC"; args["folderPath"] = ""; args["importPath"] = file;
        }
        else
        {
            args["plc"] = "PLC"; args["name"] = tool == "CreatePlcTagTable" ? "Signals" : "Ready";
            if (tool == "CreatePlcTagTable") args["group"] = "";
            else
            {
                args["table"] = "Signals"; args["dataType"] = "Bool";
                if (tool == "CreatePlcTag") args["address"] = "%M0.0";
                else args["value"] = "true";
            }
        }
        JsonNode body;
        try { body = (await host.InvokeAsync(SharedPipelineTests.Scope.Request(tool, args))).StructuredContent!; }
        finally { if (file != null) File.Delete(file); }
        Assert.Equal(1, worker.Calls);
        if (state != "unchanged")
        {
            Assert.Equal("unknown", (string?)body["meta"]?["outcome"]);
            Assert.Equal("unknown", (string?)body["meta"]?["execution"]);
            Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
            return;
        }
        Assert.Equal("failed", (string?)body["meta"]?["outcome"]);
        Assert.Equal("completed", (string?)body["meta"]?["execution"]);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.Contains("Siemens rejected the symbol declaration.", (string?)body["error"]?["message"]);
        Assert.True((bool?)body["data"]?["evidence"]?["verifiedAbsent"]);
        Assert.False((bool?)body["data"]?["evidence"]?["mayHaveChanged"]);
    }

    private sealed class SymbolWorker(string tool, string operation) : IFoundationWorker
    {
        internal int Calls;
        public async Task<JsonNode?> Call(string name, JsonObject arguments, CancellationToken token)
        {
            Assert.Equal(operation, name);
            Assert.False((bool?)arguments["dryRun"]);
            Calls++;
            var wire = await Reply(tool);
            throw WorkerOperationException.FromChannelFailure(new ChannelFailureException((string)wire["Message"]!, (int)wire["Code"]!,
                Enum.Parse<ChannelOutcome>((string)wire["Outcome"]!), (string?)wire["Evidence"]));
        }
        public void Dispose() { }
    }

    [Theory]
    [InlineData("CreatePlcInstanceDb", "CreateInstanceDB refused.")]
    [InlineData("CreateVersionControlWorkspace", "Create refused.")]
    public async Task Verified_creation_refusal_survives_the_service_and_host_mapping(string tool, string reason)
    {
        using var release = Host.HardwareContract.UseRelease("20");
        var wire = await Reply(tool);
        var body = tool == "CreatePlcInstanceDb"
            ? Host.PlcToolContract.Run(tool, true, true, () => new Services.PlcOrganisationPortService((operation, _) => {
                Assert.Equal("plc-software.ExecutePlcOrganisation", operation);
                return wire;
            }, () => true, () => "Fixture.ap20").CreatePlcInstanceDb("PLC", "Motor", "DriveDB", dryRun: false)).StructuredContent!
            : Host.LibraryToolContract.Run(tool, true, true, () => new Services.VersionControlPortService((operation, _) => {
                Assert.Equal("version-control.ExecuteVersionControl", operation);
                return wire;
            }, () => "Fixture.ap20").CreateVersionControlWorkspace("Line", "Workspace", false)).StructuredContent!;
        Assert.Equal("failed", (string?)body["meta"]?["outcome"]);
        Assert.Equal("completed", (string?)body["meta"]?["execution"]);
        Assert.False((bool?)body["data"]?["evidence"]?["mayHaveChanged"]);
        Assert.Contains(reason, (string?)body["error"]?["message"]);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
    }
}
