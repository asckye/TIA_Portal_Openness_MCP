extern alias adaptertests;
extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.PlcWorker;
using TiaMcp.WorkerChannel;
using TiaMcpServer.ModelContextProtocol;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Fixture = adaptertests::TiaMcp.Adapters.Contracts.Tests.MutationEvidenceTests;
using Xunit;

public sealed class VerifiedImportOutcomeTests
{
    public static IEnumerable<object[]> Releases() => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(key => new object[] { key });

    [Theory, MemberData(nameof(Releases))]
    public void Stale_preview_survives_step_dispatch_recovery_and_host_mapping(string release)
    {
        string repository = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        string directory = Directory.CreateDirectory(Path.Combine(repository, "bin-build", "verified-" + Guid.NewGuid().ToString("N"))).FullName;
        const string xml = "<Document><SW.Blocks.FC ID='0'><AttributeList><Name>FC_A</Name><Number>7</Number>" +
            "<ProgrammingLanguage>LAD</ProgrammingLanguage><Interface><Sections/></Interface></AttributeList></SW.Blocks.FC></Document>";
        int imports = 0, exports = 0;
        var state = new WorkerSessionOutcomeState();
        var recovery = new WorkerSessionRecovery(state, () => false, () => { }, () => { }, () => { }, () => { });
        var request = new ChannelRequest(1, "adapter.plc-software.ExecutePlcOrganisation", """{"dryRun":false}""", (_, _) => { });
        try
        {
            var response = recovery.Dispatch(request, _ => {
                var step = Fixture.RunStep(release, "ImportPlcBlockVerified", meta => {
                    var plan = new JsonObject();
                    try { return PlcVerifiedImport.Execute(xml, "PLC_1/FC_A", directory, false, "stale", path => {
                        exports++; File.WriteAllText(path, xml);
                    }, path => imports++, () => { }, plan); }
                    finally { foreach (var pair in plan) meta[pair.Key] = PlcVerifiedImport.PlainValue(pair.Value); }
                });
                var reply = new PlcSoftwareReply { Message = step.Message, Meta = step.Meta, MayHaveChanged = step.MayHaveChanged,
                    OutcomeUnknown = step.OutcomeUnknown };
                return WorkerOperationResponse.Complete(reply, false, state);
            });
            response = recovery.Synchronize(request, response);
            Assert.Null(response.Failure);
            Assert.False(state.Uncertain);
            Assert.Equal(1, exports);
            Assert.Equal(0, imports);
            var wire = JsonSerializer.Deserialize<PlcSoftwareReply>(response.ResultJson)!;
            var body = Host.PlcToolContract.Map("ImportPlcBlockVerified", wire, true, true).StructuredContent!;
            Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
            Assert.Equal("preview-stale", (string?)body["error"]?["details"]?["condition"]);
            Assert.Equal("previewToken", (string?)body["error"]?["details"]?["parameter"]);
            var evidence = body["data"]!["evidence"]!;
            Assert.False((bool?)evidence["mayHaveChanged"]);
            Assert.Equal("tokenCheck", (string?)evidence["phase"]);
            foreach (string key in new[] { "binding", "before", "planned" })
                Assert.Matches("^[0-9a-f]{64}$", (string)evidence["tokenInputs"]![key]!);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Verified_V20_import_completes_through_the_worker_after_matching_document_readback()
    {
        const string xml = "<Document><SW.Blocks.FC ID='0'><AttributeList><Name>FC_A</Name><Number>7</Number>" +
            "<ProgrammingLanguage>LAD</ProgrammingLanguage><Interface><Sections/></Interface></AttributeList></SW.Blocks.FC></Document>";
        string repo = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        string directory = Path.Combine(repo, "bin-build", "reviewed-import-" + Guid.NewGuid().ToString("N"));
        int imports = 0;
        var preview = new JsonObject();
        void Export(string path) => File.WriteAllText(path, xml);
        try
        {
            PlcVerifiedImport.Execute(xml, "PLC/FC_A", directory, true, "", Export, _ => imports++, () => { }, preview);
            var step = Fixture.RunStep("20", "ImportPlcBlockVerified", meta => {
                var result = new JsonObject();
                string message = PlcVerifiedImport.Execute(xml, "PLC/FC_A", directory, false, (string)preview["token"]!, Export,
                    _ => imports++, () => { }, result);
                foreach (var pair in result) meta[pair.Key] = PlcVerifiedImport.PlainValue(pair.Value);
                return message;
            });
            var state = new WorkerSessionOutcomeState();
            var completed = WorkerOperationResponse.Complete(step, false, state);
            Assert.False(state.Uncertain);
            Assert.Null(completed.Failure);
            Assert.Equal(1, imports);
            var body = Host.PlcToolContract.Map("ImportPlcBlockVerified", step, true, true).StructuredContent!;
            Assert.True((bool?)body["ok"]);
            Assert.Equal("succeeded", (string?)body["meta"]?["outcome"]);
            Assert.True((bool?)body["data"]?["evidence"]?["verificationSuccess"]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Inconsistent_verified_block_is_a_precondition_only_before_override_import(bool inconsistentBefore)
    {
        const string tool = "ImportPlcBlockVerified";
        const string xml = "<Document><SW.Blocks.FC ID='0'><AttributeList><Name>FC_A</Name><Number>7</Number>" +
            "<ProgrammingLanguage>LAD</ProgrammingLanguage><Interface><Sections/></Interface></AttributeList></SW.Blocks.FC></Document>";
        string repo = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var directory = Directory.CreateDirectory(Path.Combine(repo, "bin-build", "consistency-" + Guid.NewGuid().ToString("N")));
        int imports = 0;
        bool consistent = true;
        void Export(string path)
        {
            if (!consistent) throw new PlcSoftwareException("BlockInconsistent", "Fixture block is inconsistent.");
            File.WriteAllText(path, xml);
        }
        void Override(string path) { imports++; consistent = false; }
        try
        {
            var preview = new JsonObject();
            PlcVerifiedImport.Execute(xml, "PLC/FC_A", directory.FullName, true, "", Export, Override, () => { }, preview);
            consistent = !inconsistentBefore;
            var step = Fixture.RunStep("21", tool, meta => {
                var result = new JsonObject();
                try
                {
                    return PlcVerifiedImport.Execute(xml, "PLC/FC_A", directory.FullName, false, (string)preview["token"]!,
                        Export, Override, () => { }, result);
                }
                finally { foreach (var pair in result) meta[pair.Key] = PlcVerifiedImport.PlainValue(pair.Value); }
            });
            var state = new WorkerSessionOutcomeState();
            var reply = new PlcSoftwareReply { Message = step.Message, Meta = step.Meta, MayHaveChanged = step.MayHaveChanged,
                OutcomeUnknown = step.OutcomeUnknown };
            var recovery = new WorkerSessionRecovery(state, () => false, () => { }, () => { }, () => { }, () => { });
            var request = new ChannelRequest(1, "adapter.plc-software.ExecutePlcOrganisation", """{"dryRun":false}""", (_, _) => { });
            var response = recovery.Dispatch(request, _ => WorkerOperationResponse.Complete(reply, false, state));
            var wire = JsonSerializer.Deserialize<PlcSoftwareReply>(response.ResultJson)!;
            var body = Host.PlcToolContract.Map(tool, wire, true, true).StructuredContent!;
            var synchronized = recovery.Synchronize(request, response);
            Assert.Equal(inconsistentBefore ? 0 : 1, imports);
            Assert.False(state.Uncertain);
            Assert.False(wire.OutcomeUnknown);
            Assert.Equal(inconsistentBefore ? "exportBefore" : "exportReadback", step.Meta["phase"]);
            Assert.Equal(inconsistentBefore ? "PRECONDITION_FAILED" : null, (string?)body["error"]?["code"]);
            Assert.Equal(inconsistentBefore ? "rejected-before-operation" : "succeeded", (string?)body["meta"]?["outcome"]);
            if (inconsistentBefore)
            {
                Assert.Null(synchronized.Failure);
                Assert.Equal("block-inconsistent", (string?)body["error"]?["details"]?["condition"]);
                Assert.Equal("blockPath", (string?)body["error"]?["details"]?["parameter"]);
            }
            else
            {
                Assert.Null(synchronized.Failure);
                Assert.Equal("completed", (string?)body["meta"]?["execution"]);
                var warning = Assert.Single(body["meta"]!["warnings"]!.AsArray(), w => (string?)w?["details"]?["reason"] == "readback-failed");
                Assert.Equal("INCOMPLETE_DATA", (string?)warning?["code"]);
                Assert.Equal("exportReadback", (string?)warning?["details"]?["phase"]);
                Assert.Equal("Fixture block is inconsistent.", (string?)warning?["details"]?["nativeMessage"]);
                Assert.Equal(true, step.Meta["importReturned"]);
                Assert.Equal(true, step.Meta["mayHaveChanged"]);
                Assert.NotEmpty(body["data"]!["evidence"]!["cleanupRequired"]!.AsArray());
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("ConnectOnlinePlc", "{}", false)]
    [InlineData("ConnectOnlinePlc", """{"dryRun":true}""", false)]
    [InlineData("AttachOpenProject", "{}", false)]
    [InlineData("AttachOpenProject", """{"dryRun":true}""", false)]
    [InlineData("OpenProject", """{"dryRun":false}""", false)]
    [InlineData("OpenProject", """{"dryRun":true}""", true)]
    [InlineData("OpenProject", "{}", true)]
    public void Dispatched_session_and_online_failures_remain_unknown_unless_the_schema_defines_a_preview(
        string tool, string arguments, bool preview)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, "21");
        var shared = LegacyHostToolRegistry.Create(worker, "21", false, hostPorts: false);
        using var catalog = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
            new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(worker) {
                Catalog = new toolpipeline::TiaMcp.FoundationHost.FoundationCatalog("21", shared) });
        using var calls = Host.InvocationJournal.BeginNativeCallScope();
        Host.InvocationJournal.NativeCallStarted();
        var failure = new InvalidOperationException("Fixture failure after dispatch.");
        var body = Host.McpServer.TargetFailure(tool, failure, true, arguments).StructuredContent!;
        Assert.Equal(preview ? "NATIVE_OPERATION_FAILED" : "OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.Equal(preview ? "read-failed" : "unknown", (string?)body["meta"]?["outcome"]);
        Assert.Equal(preview ? "read-only" : "unknown", (string?)body["meta"]?["execution"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Preview_and_dispatched_apply_classify_host_errors_independently(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        using var catalog = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
            new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(worker) { Catalog = new PortedPortalAdmissionTests.Catalog(release) });
        foreach (bool preview in new[] { true, false })
        {
            var body = Host.McpServer.TargetFailure("DeletePlcBlock", new InvalidOperationException("Fixture error"), true,
                new JsonObject { ["dryRun"] = preview }.ToJsonString()).StructuredContent!;
            Assert.Equal(!preview, (string?)body["error"]?["code"] == "OUTCOME_UNKNOWN");
        }
    }

    [Fact]
    public void Missing_alarm_text_lists_keep_the_native_reason_after_a_file_export_was_attempted()
    {
        const string tool = "ExchangePlcAlarmTextLists";
        const string reason = "This PLC has no alarm text lists.";
        var reply = Fixture.RunStep("20", tool, meta => {
            meta["action"] = "export";
            meta["mayHaveWrittenFiles"] = true;
            throw AdapterPreconditionException.Failed(reason, "plc-text-lists", "softwarePath");
        });
        Assert.False(reply.OutcomeUnknown);
        var body = Host.EngineeringToolContract.Map(tool, reply, true).StructuredContent!;
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("plc-text-lists", (string?)body["error"]?["details"]?["condition"]);
        Assert.Equal(reason, (string?)body["error"]?["message"]);
    }

    [Theory]
    [InlineData("ExchangeUnifiedTags", "tagCountBefore")]
    [InlineData("ExchangeUnifiedScriptModules", "moduleCountBefore")]
    public void Empty_exports_succeed_and_file_errors_retain_the_native_reason(string tool, string count)
    {
        var meta = new JsonObject { ["action"] = "export", ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = false,
            ["operationSuccess"] = true, [count] = 0, ["files"] = new JsonArray() };
        var reply = Host.HmiExchangeContract.Map(tool, new { Meta = meta }, true, true).StructuredContent!;
        Assert.True((bool?)reply["ok"]);
        Assert.Empty(reply["data"]!["evidence"]!["files"]!.AsArray());
        meta["operationSuccess"] = false;
        meta["mayHaveWrittenFiles"] = true;
        meta["nativeMessage"] = "Output unavailable.";
        var failed = Host.HmiExchangeContract.Map(tool, new { Meta = meta }, true, true).StructuredContent!;
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)failed["error"]?["code"]);
        Assert.Equal("Output unavailable.", (string?)failed["error"]?["details"]?["nativeMessage"]);
        Assert.True((bool?)failed["data"]?["evidence"]?["mayHaveWrittenFiles"]);
    }

    [Fact]
    public void Verified_absence_distinguishes_single_block_refusal_from_an_unanswered_import()
    {
        var reason = "TIA refused the FC body.";
        var wire = new JsonObject { ["phase"] = "import", ["verifiedAbsent"] = true, ["mayHaveChanged"] = false, ["nativeMessage"] = reason };
        foreach (string status in new[] { "unknown", "rejected-before-operation" })
        {
            var failure = new WorkerOperationException(reason, -32603, status, wire.ToJsonString());
            var body = FoundationV4Result.Failure("21", "ImportPlcBlock", "single-block", true, true, null, failure).StructuredContent!;
            Assert.Equal(status == "unknown" ? "OUTCOME_UNKNOWN" : "NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
            Assert.Equal(reason, (string?)body["data"]?["evidence"]?["nativeMessage"]);
        }
    }

    [Fact]
    public void Verified_absence_after_second_batch_import_keeps_only_the_completed_block()
    {
        string repository = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        string root = Path.Combine(repository, "bin-build", "tia-refused-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "DB.xml"), BatchImportTests.Xml("DB_A", "GlobalDB", 12));
            File.WriteAllText(Path.Combine(root, "FC.xml"), BatchImportTests.Xml("FC_A", "FC", 13));
            var request = new TiaMcp.Adapters.PlcBatchImportRequest { Release = "17", Project = @"C:\Projects\Project.ap17", ProcessId = 12,
                Software = "PLC", Directory = root, Program = true, Order = new[] { "DB.xml", "FC.xml" } };
            int imports = 0;
            TiaMcp.Adapters.PlcBatchImportObject[] Import(FileInfo file, TiaMcp.Adapters.PlcBatchImportObject planned)
            {
                imports++;
                if (planned.Name == "DB_A") return new[] { planned };
                var error = new IOException("Native import refused.");
                error.Data["verifiedAbsent"] = true;
                error.Data["nativeMessage"] = "TIA refused the network in FC_A.";
                throw error;
            }
            TiaMcp.Adapters.PlcBatchImportResult Run() => TiaMcp.Adapters.PlcBatchImportPolicy.Run(request,
                Array.Empty<TiaMcp.Adapters.PlcBatchImportObject>(), () => { }, Import);
            request.ExpectedHash = Run().PlanHash;
            request.DryRun = false; request.Confirm = true; request.ExpectedProject = request.Project;
            var batch = Run();
            Assert.Equal(2, imports);
            Assert.False(batch.NativeOutcomeUnknown);
            Assert.Equal("DB_A", Assert.Single(batch.RemainingChanged).Name);
            BatchImportContract.Validate(JsonSerializer.SerializeToNode(batch)!.AsObject(), false);
            var data = JsonSerializer.SerializeToNode(batch, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            var body = JsonNode.Parse(TiaMcp.Logic.V4.V4Json.Serialize(
                TiaMcp.Logic.V4.PlcBatchImportResultMapping.Result(data, "17", "ImportPlcProgramFromDirectory", "batch", false)))!;
            Assert.Equal("partial", (string?)body["meta"]?["outcome"]);
            Assert.Equal("TIA refused the network in FC_A.", (string?)body["data"]?["items"]?[1]?["result"]?["error"]?["details"]?["nativeMessage"]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Unknown_AML_and_SiVArc_operations_retain_the_native_failure_without_claiming_session_loss()
    {
        string reason = "Native target rejected the operation.";
        var response = new Host.ResponseMessage { Meta = new JsonObject { ["nativeMessage"] = reason, ["phase"] = "import",
            ["writeOutcomeUnknown"] = true, ["operationSuccess"] = false, ["mayHaveChanged"] = true } };
        var reported = new WorkerOperationException("Worker returned unknown.", -32603, "unknown", new JsonObject {
            ["Meta"] = response.Meta!.DeepClone() }.ToJsonString());
        foreach (var result in new[] { Host.HardwareContract.Map("ImportDeviceAml", response, true, true),
            Host.HardwareContract.Run("ImportDeviceAml", () => throw reported, true, true),
            Host.LibraryToolContract.Map("ManageSivarcRuleContainer", response, true, true),
            Host.LibraryToolContract.Run("ManageSivarcRuleContainer", true, true, () => throw reported),
            Host.LibraryToolContract.Map("GenerateSivarc", response, true, true),
            Host.LibraryToolContract.Run("GenerateSivarc", true, true, () => throw reported) })
        {
            var body = result.StructuredContent!;
            Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
            Assert.Equal(reason, (string?)body["error"]?["details"]?["evidence"]?["nativeMessage"]);
            Assert.DoesNotContain("Rebuild the session", (string)body["error"]!["message"]!);
        }
    }

    [Fact]
    public void Verified_OPC_export_and_archive_file_failure_have_known_project_outcomes()
    {
        var meta = new JsonObject { ["action"] = "export", ["success"] = true, ["mayHaveChanged"] = false,
            ["mayHaveWrittenFiles"] = true, ["file"] = new JsonObject { ["bytes"] = 87, ["sha256"] = new string('a', 64) } };
        var opc = Host.EngineeringToolContract.Map("ExportOpcUaInterface", new Host.ResponseMessage { Meta = meta }, true).StructuredContent!;
        Assert.True((bool?)opc["ok"]);
        Assert.Equal(87, (int?)opc["data"]?["evidence"]?["file"]?["bytes"]);

        meta["action"] = "archive"; meta["success"] = false; meta["apiCallSuccess"] = false;
        meta["nativeMessage"] = "Archive is not supported in this PLC context.";
        var archive = Host.OptionalPackageContract.Map("ManageSinumerikArchive", new Host.ResponseMessage { Meta = meta }, true).StructuredContent!;
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)archive["error"]?["code"]);
        Assert.Equal((string?)meta["nativeMessage"], (string?)archive["error"]?["details"]?["nativeMessage"]);
    }

    [Fact]
    public void Completed_document_import_has_a_warning_when_readback_export_fails()
    {
        var meta = new { operationSuccess = true, mayHaveChanged = true, existsVerified = true, dataComplete = false,
            readbackWarning = "Verification export failed on FC_A." };
        var body = Host.EngineeringToolContract.Map("ManagePlcBlockDocuments", new { Meta = meta }, true).StructuredContent!;
        Assert.True((bool?)body["ok"]);
        Assert.Equal("succeeded", (string?)body["meta"]?["outcome"]);
        Assert.Equal("partial", (string?)body["meta"]?["completeness"]);
        Assert.Contains(body["meta"]!["warnings"]!.AsArray(), item => (string?)item?["message"] == meta.readbackWarning);
    }

    [Fact]
    public void Status_warning_is_written_to_both_representations_when_warnings_were_omitted()
    {
        var extra = new TextContentBlock { Text = "Additional result" };
        var body = new JsonObject { ["meta"] = new JsonObject(), ["data"] = new JsonObject { ["completed"] = true } };
        var result = FoundationV4Result.WarnStatusUnavailable(new CallToolResult { StructuredContent = body,
            Content = new[] { new TextContentBlock { Text = body.ToJsonString() }, extra } });
        var warning = Assert.Single(result.StructuredContent!["meta"]!["warnings"]!.AsArray());
        Assert.Contains("session status read failed", (string)warning!["message"]!);
        Assert.True((bool?)result.StructuredContent["data"]?["completed"]);
        Assert.Equal(2, result.Content.Count);
        Assert.Same(extra, result.Content[1]);
        Assert.True(JsonNode.DeepEquals(result.StructuredContent, JsonNode.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text)));
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Completed_shared_worker_call_keeps_its_reply_and_reports_status_warning(string release)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        string adapter = Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(adapter, "offline session fixture");
        using var worker = new WorkerClient(release, exe, AppContext.BaseDirectory, true) { Bundled = false, SharedSession = true };
        using var scope = FoundationWorkerRequest.Use(Guid.NewGuid().ToString("N"), "MCP_B5_Write");
        var reply = await worker.Call("FixtureCompleteBeforeStatusFailure", new JsonObject(), CancellationToken.None);
        Assert.True((bool?)reply?["Executed"]);
        Assert.True((bool?)reply?["MayHaveChanged"]);
        Assert.True(scope.StatusUnavailable);
        Assert.Null(worker.SharedStatus);
        Assert.Empty(worker.Unsettled.Snapshot());
        var result = FoundationV4Result.WarnStatusUnavailable(FoundationV4Result.HostSessionState(release, scope.Id,
            new JsonObject { ["completed"] = reply.DeepClone() }));
        Assert.True((bool?)result.StructuredContent?["ok"]);
        Assert.True((bool?)result.StructuredContent?["data"]?["completed"]?["Executed"]);
        Assert.Contains(result.StructuredContent!["meta"]!["warnings"]!.AsArray(), row =>
            ((string?)row?["message"])?.Contains("session status read failed", StringComparison.Ordinal) == true);
        Assert.True((bool?)(await worker.Observe("status", new JsonObject(), CancellationToken.None))?["readiness"]?["ready"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Online_refusal_and_unknown_step_preserve_their_evidence(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        const string reason = "ConnectOnlinePlc not attempted: route unavailable.";
        foreach (string tool in new[] { "ConnectOnlinePlc", "DownloadPlc", "DisconnectOnlinePlc" })
        foreach (string flag in new[] { "writeAttempted", "mayHaveChanged" })
        {
            var meta = new Dictionary<string, object?> { [flag] = false, ["success"] = false };
            object reply = tool == "DownloadPlc" ? new OnlineDownloadResult { Meta = meta, Message = reason }
                : new OnlineStateResult { Meta = meta, State = "Unknown", Message = reason };
            var body = Host.RuntimeToolContract.Map(tool, reply, false, true).StructuredContent!;
            Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
            Assert.Equal(reason, (string?)body["error"]?["message"]);
            Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        }
        var failure = new WorkerOperationException("Worker outcome unknown", -32603, "unknown",
            """{"Value":{"Meta":{"phase":"import","nativeMessage":"Native FC_A import failed."}}}""");
        foreach (var reply in new[] { Host.PlcToolContract.Failure("ImportPlcBlock", failure, true, true),
            Host.HmiInspectionContract.Run("InvokeObject", true, true, () => throw failure) })
        {
            var body = reply.StructuredContent!;
            Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
            Assert.Equal("import", (string?)body["error"]?["details"]?["evidence"]?["phase"]);
            Assert.Equal("Native FC_A import failed.", (string?)body["error"]?["details"]?["evidence"]?["nativeMessage"]);
        }
    }
}
