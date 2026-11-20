extern alias adaptertests;
extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.FoundationHost;
using TiaMcp.WorkerChannel;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class WriteOutcomeContractTests
{
    [Theory, InlineData("ManageNetworkDomain"), InlineData("ManageCommunicationConnection"), InlineData("ManageProjectUserManagement"),
        InlineData("ManageGlobalLibrary"), InlineData("ManagePlcBlockDocuments"), InlineData("SetOpcUaInterfaceEnabled")]
    public void Completed_write_discloses_its_redacted_readback_failure_in_both_representations(string tool)
    {
        var reply = new Host.ResponseMessage {
            Message = "The write completed but its readback failed.",
            Meta = new JsonObject { ["operationSuccess"] = true, ["mayHaveChanged"] = tool != "ManageGlobalLibrary",
                ["mayHaveWrittenFiles"] = tool == "ManageGlobalLibrary", ["writeReturned"] = true, ["readbackFailed"] = true,
                ["dataComplete"] = false, ["phase"] = "readback", ["nativeMessage"] = "Failed token=secretValue" }
        };
        if (tool == "ManagePlcBlockDocuments")
        {
            reply.Meta!["nativeState"] = "Success"; reply.Meta["nativeResultReturned"] = true; reply.Meta["phase"] = "findImportedBlocks";
        }
        var result = tool switch {
            "ManageNetworkDomain" or "ManageCommunicationConnection" => Host.HardwareToolContract.MapResult(tool, reply, false, true),
            "ManageProjectUserManagement" => Host.SecurityToolContract.MapResult(tool, reply, false, true),
            "ManageGlobalLibrary" => Host.LibraryToolContract.Map(tool, reply, true),
            _ => Host.EngineeringToolContract.Map(tool, reply, true)
        };
        var body = result.StructuredContent!;
        Assert.True((bool?)body["ok"]);
        Assert.Equal("completed", (string?)body["meta"]?["execution"]);
        Assert.Equal("partial", (string?)body["meta"]?["completeness"]);
        var warning = Assert.Single(body["meta"]!["warnings"]!.AsArray(), w => (string?)w?["details"]?["reason"] == "readback-failed");
        Assert.Equal("INCOMPLETE_DATA", (string?)warning!["code"]);
        Assert.Equal("The write completed but its readback failed.", (string?)warning["message"]);
        Assert.Equal(tool == "ManagePlcBlockDocuments" ? "findImportedBlocks" : "readback", (string?)warning["details"]?["phase"]);
        Assert.Contains("Failed", (string)warning["details"]!["nativeMessage"]!);
        Assert.DoesNotContain("secretValue", (string)warning["details"]!["nativeMessage"]!);
        Assert.DoesNotContain("secretValue", body.ToJsonString());
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text)));
    }

    [Fact]
    public void A_failed_native_document_import_cannot_be_completed_by_a_readback_warning()
    {
        var reply = new Host.ResponseMessage { Meta = new JsonObject { ["operationSuccess"] = false, ["mayHaveChanged"] = true,
            ["nativeResultReturned"] = true, ["writeReturned"] = true, ["nativeState"] = "Error", ["nativeMessages"] = new JsonArray("Import refused.") } };
        var body = Host.EngineeringToolContract.Map("ManagePlcBlockDocuments", reply, true).StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.DoesNotContain(body["meta"]!["warnings"]!.AsArray(), w => (string?)w?["details"]?["reason"] == "readback-failed");
    }

    [Theory, InlineData("rollbackOutcome"), InlineData("connectionUnavailable"), InlineData("portalLost"), InlineData("writeNotReturned"),
        InlineData("writeOutcomeUnknown"), InlineData("rollbackCompleted"), InlineData("unrestoredWrite")]
    public void Readback_warning_needs_a_returned_write_and_no_rollback_or_session_loss(string flag)
    {
        var meta = new JsonObject { ["operationSuccess"] = true, ["mayHaveChanged"] = true, ["writeReturned"] = flag != "writeNotReturned",
            ["readbackFailed"] = true, ["phase"] = "countAfter", ["dataComplete"] = false };
        if (flag == "rollbackOutcome") meta[flag] = "unknown";
        else if (flag != "writeNotReturned") meta[flag] = true;
        var body = Host.HardwareToolContract.MapResult("ManageCommunicationConnection", new Host.ResponseMessage { Meta = meta }, false, true)
            .StructuredContent!;
        Assert.DoesNotContain(body["meta"]!["warnings"]!.AsArray(), w => (string?)w?["details"]?["reason"] == "readback-failed");
    }

    [Theory, InlineData("ManageTransferArea"), InlineData("ManageDeviceUsers"), InlineData("ExchangeUnifiedTags"),
        InlineData("ManageLibraryType"), InlineData("ImportDeviceAml")]
    public void Unknown_worker_reply_preserves_only_bounded_redacted_diagnostics(string tool)
    {
        var meta = new JsonObject {
            ["phase"] = "import", ["nativeResult"] = "Error", ["errorCount"] = 2, ["deviceCountBefore"] = 3, ["deviceCountAfter"] = 3,
            ["countBefore"] = 4, ["countAfter"] = 4, ["writeReturned"] = false, ["readbackFailed"] = false,
            ["messages"] = new JsonArray(Enumerable.Range(1, 22).Select(i => (JsonNode?)JsonValue.Create("TIA refused token=secretValue")).ToArray()),
            ["nativeMessage"] = "Native refusal token=secretValue " + new string('a', 1400),
            ["before"] = new JsonObject { ["comment"] = "private user comment" },
            ["cleanupRequired"] = new JsonArray(new JsonObject { ["target"] = "PLC", ["previousType"] = "System.String",
                ["previousValue"] = "private user comment" })
        };
        var error = new WorkerOperationException("Worker returned unknown.", -32603, "unknown",
            new JsonObject { ["Value"] = new JsonObject { ["Meta"] = meta.DeepClone() } }.ToJsonString());
        CallToolResult result = tool switch {
            "ManageTransferArea" => Host.HardwareToolContract.Invoke(tool, false, true, () => throw error),
            "ManageDeviceUsers" => Host.SecurityToolContract.Invoke(tool, false, true, () => throw error, "secretValue"),
            "ExchangeUnifiedTags" => Host.HmiExchangeContract.Run(tool, true, true, () => throw error),
            "ManageLibraryType" => Host.LibraryToolContract.Run(tool, true, true, () => throw error),
            _ => Host.HardwareContract.Run(tool, () => throw error, true, true)
        };
        var body = result.StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        var evidence = body["error"]!["details"]!["evidence"]!;
        Assert.Equal("Error", (string?)evidence["nativeResult"]);
        Assert.Equal(2, (int?)evidence["errorCount"]);
        Assert.Equal(3, (int?)evidence["deviceCountAfter"]);
        Assert.Equal(4, (int?)evidence["countBefore"]);
        Assert.Equal(false, (bool?)evidence["writeReturned"]);
        Assert.Equal(false, (bool?)evidence["readbackFailed"]);
        Assert.Equal(16, evidence["messages"]!.AsArray().Count);
        var cleanup = Assert.Single(evidence["cleanupRequired"]!.AsArray())!.AsObject();
        Assert.Equal("System.String", (string?)cleanup["previousType"]);
        Assert.False(cleanup.ContainsKey("previousValue"));
        Assert.InRange(((string)evidence["nativeMessage"]!).Length, 1, 1024);
        Assert.DoesNotContain("secretValue", body.ToJsonString());
        Assert.DoesNotContain("private user comment", body.ToJsonString());
    }

    [Theory, InlineData("ManageDeviceUsers"), InlineData("ManageTransferArea"), InlineData("ManageSyslogServers"),
        InlineData("ManageLibraryType"), InlineData("ExchangeUnifiedTags")]
    public void Restored_native_refusals_retain_the_native_reason(string tool)
    {
        var response = new Host.ResponseMessage { Meta = new JsonObject {
            ["operationSuccess"] = false, ["apiCallSuccess"] = false, ["status"] = "ReadOrWriteFailed",
            ["rollbackCompleted"] = true, ["rollbackOutcome"] = "restored", ["mayHaveChanged"] = false,
            ["nativeMessage"] = "TIA refused the write.\n   password=fixtureSecret"
        } };
        var body = (tool switch {
            "ManageTransferArea" => Host.HardwareToolContract.MapResult(tool, response, false, true),
            "ExchangeUnifiedTags" => Host.HmiExchangeContract.Map(tool, response, true, true),
            "ManageLibraryType" => Host.LibraryToolContract.Map(tool, response, true),
            _ => Host.SecurityToolContract.MapResult(tool, response, false, true)
        }).StructuredContent!;
        Assert.Equal("failed", (string?)body["meta"]?["outcome"]);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("TIA refused the write.", (string?)body["error"]?["message"]);
        Assert.DoesNotContain("fixtureSecret", body.ToJsonString());
    }

    [Fact]
    public void AutomationML_native_false_retains_returned_state_and_counts_through_the_host_catch()
    {
        var reply = adaptertests::TiaMcp.Adapters.Contracts.Tests.WriteRefusalTests.RefusedAutomationMlImport();
        Assert.False(reply.OutcomeUnknown);
        Assert.True(reply.MayHaveChanged);
        Assert.Equal(false, reply.Meta["operationSuccess"]);
        Assert.Equal(true, reply.Meta["apiCallSuccess"]);
        var response = new Host.ResponseMessage { Message = reply.Message, Meta = JsonSerializer.SerializeToNode(reply.Meta)!.AsObject() };
        var body = Host.HardwareContract.Run("ImportDeviceAml", () => response, true, true).StructuredContent!;
        Assert.Equal("failed", (string?)body["meta"]?["outcome"]);
        Assert.Equal("completed", (string?)body["meta"]?["execution"]);
        Assert.Equal("partial", (string?)body["meta"]?["completeness"]);
        var evidence = body["error"]!["details"]!["evidence"]!;
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal(false, (bool?)evidence["nativeResult"]);
        Assert.Equal(1, (int?)evidence["deviceCountBefore"]);
        Assert.Equal(1, (int?)evidence["deviceCountAfter"]);
        var message = (string)Assert.Single(evidence["logExcerpt"]!.AsArray())!;
        Assert.StartsWith("CAx import returned false.", message);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(message), 1, 4096);
        Assert.DoesNotContain("end of native log", message);
    }

    [Theory, InlineData("ManagePlcUserGroup"), InlineData("DeleteEmptyPlcBlockGroup")]
    public void Missing_native_block_group_is_not_found_for_both_worker_names(string tool)
    {
        var step = adaptertests::TiaMcp.Adapters.Contracts.Tests.WriteReadbackTests.MissingBlockGroup();
        var reply = new Host.ResponseMessage { Message = step.Message, Meta = JsonSerializer.SerializeToNode(step.Meta)!.AsObject() };
        var body = Host.PlcToolContract.Map(tool, reply, true, true).StructuredContent!;
        Assert.Equal("NOT_FOUND", (string?)body["error"]?["code"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
    }

    [Theory, InlineData(0, 0, false, "failed"), InlineData(1, 0, false, "partial"), InlineData(0, 1, false, "partial"),
        InlineData(0, 0, true, "unknown"), InlineData(0, 3, false, "partial")]
    public void Vci_refusals_use_confirmed_mapping_results_and_unanswered_exports(int mapped, int foundAfter, bool unanswered, string expected)
    {
        int exports = mapped + foundAfter + (unanswered ? 1 : 0);
        int failed = Math.Max(1, foundAfter);
        var reply = new Host.ResponseMessage { Meta = new JsonObject { ["success"] = false, ["failed"] = failed, ["exportsIssued"] = exports,
            ["alreadyMapped"] = 2, ["queryRefusals"] = failed - foundAfter - (unanswered ? 1 : 0),
            ["mapped"] = mapped, ["mappedAfterFailure"] = foundAfter, ["mayHaveChanged"] = exports > 0, ["writeOutcomeUnknown"] = unanswered,
            ["nativeMessage"] = "VCI refused the object" } };
        var body = Host.LibraryToolContract.Map("ConnectProjectToWorkspace", reply, true).StructuredContent!;
        Assert.Equal(expected, (string?)body["meta"]?["outcome"]);
        if (unanswered) Assert.Equal("VCI refused the object", (string?)body["error"]?["details"]?["evidence"]?["nativeMessage"]);
        if (expected == "partial")
        {
            var details = body["error"]!["details"]!;
            Assert.Equal(mapped, (int?)details["succeeded"]);
            Assert.Equal(mapped == 0 ? 0 : failed, (int?)details["failed"]);
            Assert.Equal(0, (int?)details["notExecuted"]);
            Assert.Equal("VCI refused the object", (string?)body["data"]?["evidence"]?["nativeMessage"]);
            if (mapped == 0)
            {
                Assert.Contains("mappings exist but the initial export failed", (string?)body["error"]?["message"]);
                Assert.Contains("VCI refused the object", (string?)body["error"]?["message"]);
            }
        }
        if (expected == "failed") Assert.Equal("VCI refused the object", (string?)body["error"]?["details"]?["nativeMessage"]);
    }

    [Theory, InlineData(0, 3, 0, "failed", "NATIVE_OPERATION_FAILED"), InlineData(1, 2, 0, "partial", "PARTIAL_FAILURE"),
        InlineData(0, 1, 1, "unknown", "OUTCOME_UNKNOWN")]
    public void Sync_uses_confirmed_exports_and_unanswered_writes(int ok, int failed, int unconfirmed, string outcome, string code)
    {
        var reply = new Host.ResponseStringList { Meta = new JsonObject { ["success"] = false, ["synchronized"] = ok, ["failed"] = failed,
            ["exportsIssued"] = ok + failed, ["mayHaveChanged"] = true, ["unconfirmed"] = unconfirmed,
            ["writeOutcomeUnknown"] = unconfirmed > 0, ["nativeMessage"] = "VCI refused the object" } };
        var body = Host.LibraryToolContract.Map("SynchronizeVersionControlWorkspace", reply, true).StructuredContent!;
        Assert.Equal(outcome, (string?)body["meta"]?["outcome"]);
        Assert.Equal(code, (string?)body["error"]?["code"]);
        var details = body["error"]!["details"]!;
        if (outcome == "failed") Assert.Equal("VCI refused the object", (string?)details["nativeMessage"]);
        if (outcome == "partial")
        {
            Assert.Equal(ok, (int?)details["succeeded"]);
            Assert.Equal(failed, (int?)details["failed"]);
        }
    }

    [Fact]
    public void Interrupted_sync_after_issue_remains_unknown()
    {
        var reply = adaptertests::TiaMcp.Adapters.Contracts.Tests.VciSyncTests.InterruptedSync();
        var body = MapVci("SynchronizeVersionControlWorkspace", reply, true);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.Equal("unknown", (string?)body["meta"]?["execution"]);
    }

    [Theory, InlineData("ConnectProjectToWorkspace"), InlineData("SynchronizeVersionControlWorkspace"), InlineData("GetVersionControlStatus")]
    public void Workspace_selection_refusals_remain_before_operation(string tool)
    {
        foreach (bool empty in new[] { true, false })
        {
            var reply = adaptertests::TiaMcp.Adapters.Contracts.Tests.VciSyncTests.WorkspaceRefusal(empty);
            var body = MapVci(tool, reply, tool != "GetVersionControlStatus");
            Assert.Equal(empty ? "PRECONDITION_FAILED" : "NOT_FOUND", (string?)body["error"]?["code"]);
            Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
            if (empty) Assert.Equal("no-workspace", (string?)body["error"]?["details"]?["condition"]);
        }
    }

    [Theory, InlineData(true), InlineData(false)]
    public void Unknown_objects_are_disclosed_in_both_sync_modes(bool preview)
    {
        var reply = adaptertests::TiaMcp.Adapters.Contracts.Tests.VciSyncTests.SkippedUnknown(preview);
        var body = MapVci("SynchronizeVersionControlWorkspace", reply, !preview);
        Assert.Equal("partial", (string?)body["meta"]?["completeness"]);
        Assert.Equal(3, (int?)body["data"]?["evidence"]?["skippedUnknown"]);
        Assert.False((bool?)body["data"]?["evidence"]?["dataComplete"]);
        Assert.Equal("Status probe 0", (string?)body["data"]?["evidence"]?["statusProbe"]?["nativeMessage"]);
        Assert.DoesNotContain("in sync", (string?)body["data"]?["summary"]);
        Assert.Contains("changedOnly=false", (string?)body["data"]?["summary"]);
    }

    private static JsonNode MapVci(string tool, TiaMcp.Adapters.Contracts.VersionControlResult reply, bool writes)
        => Host.LibraryToolContract.Map(tool, new Host.ResponseStringList { Message = reply.Message,
            Meta = JsonSerializer.SerializeToNode(reply.Meta)!.AsObject() }, writes).StructuredContent!;
}
