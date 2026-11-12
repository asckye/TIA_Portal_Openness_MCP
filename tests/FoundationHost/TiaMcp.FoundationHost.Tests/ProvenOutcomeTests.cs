extern alias enginehost;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class ProvenOutcomeTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static void Verdict(JsonNode body, string outcome, string execution, string? code)
    {
        Assert.Equal(outcome, (string?)body["meta"]?["outcome"]);
        Assert.Equal(execution, (string?)body["meta"]?["execution"]);
        Assert.Equal(code, (string?)body["error"]?["code"]);
    }

    private static JsonObject BatchExport(bool exported = false, bool uncertain = false)
    {
        JsonObject Item(string name, string status) => new() {
            ["ObjectPath"] = name, ["OutputFile"] = "C:/fixture/exports/" + name + ".xml", ["Status"] = status,
            ["XmlContent"] = "native-xml", ["Warnings"] = new JsonArray(),
            ["Evidence"] = status == "not-attempted" ? new JsonObject() : new JsonObject {
                ["outputFile"] = "C:/fixture/exports/" + name + ".xml", ["stagedFile"] = "C:/fixture/staging/export.xml",
                ["recoveryDirectory"] = "C:/fixture/staging", ["exportPhase"] = "native-export" } };
        var failed = Item("FC_Fixture", "failed");
        if (uncertain) failed["Failure"] = "native-outcome-uncertain";
        var items = new JsonArray();
        if (exported) items.Add(Item("FC_Published", "exported"));
        items.Add(failed); items.Add(Item("FC_Later", "not-attempted"));
        return new JsonObject {
            ["Executed"] = true, ["ProjectFile"] = "C:/fixture/Fixture.ap21", ["SoftwarePath"] = "PLC",
            ["GroupPath"] = "", ["Recursive"] = true, ["InventoryHash"] = Hash, ["InventoryComplete"] = true,
            ["RequiresSessionReset"] = true, ["Items"] = items };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_batch_export_item_is_a_known_failure(bool exported)
    {
        var definition = FoundationTools.Definitions.Single(d => d.Name == "ExportPlcBlocks");
        var body = FoundationV4Result.Worker("21", definition, "fixture", new() { ["dryRun"] = false },
            BatchExport(exported), null).StructuredContent!;
        Verdict(body, exported ? "partial" : "failed", exported ? "partial" : "completed",
            exported ? "PARTIAL_FAILURE" : "NATIVE_OPERATION_FAILED");
        var items = body["data"]!["items"]!.AsArray();
        int index = exported ? 1 : 0;
        Verdict(items[index]!["result"]!, "failed", "completed", "NATIVE_OPERATION_FAILED");
        Verdict(items[index + 1]!["result"]!, "rejected-before-operation", "not-started", "NOT_EXECUTED");
    }

    [Fact]
    public void Uncertain_batch_export_item_stays_unknown()
    {
        var definition = FoundationTools.Definitions.Single(d => d.Name == "ExportPlcBlocks");
        var body = FoundationV4Result.Worker("21", definition, "fixture", new() { ["dryRun"] = false },
            BatchExport(uncertain: true), null).StructuredContent!;
        Verdict(body, "unknown", "unknown", "OUTCOME_UNKNOWN");
        Verdict(body["data"]!["items"]![0]!["result"]!, "unknown", "unknown", "OUTCOME_UNKNOWN");
    }

    private static WorkerOperationException LostReply()
    {
        var error = new WorkerOperationException("Worker reply lost.", -32603, "unknown", BatchExport().ToJsonString());
        error.Data["foundationRequestSent"] = true;
        error.Data["workerOutcomeUnknown"] = true;
        return error;
    }

    [Theory]
    [InlineData("foundation")]
    [InlineData("optional")]
    [InlineData("plc")]
    public void Lost_read_reply_is_a_read_failure_on_every_path(string path)
    {
        var error = LostReply();
        var body = (path switch {
            "foundation" => FoundationV4Result.Failure("21", "AnalyzePlcCrossReferences", "fixture", true, false, null, error),
            "optional" => Host.OptionalPackageContract.Run("ListTestSuiteCases", false, () => throw error),
            _ => Host.PlcToolContract.Failure("DescribePlcBlockLogic", error, false, true)
        }).StructuredContent!;
        Verdict(body, "read-failed", "read-only", "NATIVE_OPERATION_FAILED");
    }

    [Theory]
    [InlineData("foundation")]
    [InlineData("optional")]
    [InlineData("plc")]
    public void Lost_write_reply_stays_unknown(string path)
    {
        var error = LostReply();
        var body = (path switch {
            "foundation" => FoundationV4Result.Failure("21", "ImportPlcBlockVerified", "fixture", true, true, null, error),
            "optional" => Host.OptionalPackageContract.Run("ExchangeTestSuiteCase", true, () => throw error),
            _ => Host.PlcToolContract.Failure("ImportPlcBlockVerified", error, true, true)
        }).StructuredContent!;
        Verdict(body, "unknown", "unknown", "OUTCOME_UNKNOWN");
    }

    [Fact]
    public void Cross_reference_export_failure_is_a_read_failure()
    {
        var report = PlcCrossReferenceAnalysis.Report("PLC", "C:/fixture/exports", BatchExport());
        var body = JsonNode.Parse(V4Json.Serialize(PlcCrossReferenceAnalysis.Result("21", "fixture", report)))!;
        Verdict(body, "read-failed", "read-only", "IO_FAILED");
        Assert.False((bool)report["analysisQueried"]!);
    }

    private static JsonObject DocumentImport(string status, bool executed = true) => new() {
        ["Policy"] = "single-document-global-db-v1", ["PlanHash"] = Hash, ["Status"] = status,
        ["Executed"] = executed, ["Attempted"] = executed, ["MayHaveChanged"] = executed,
        ["RequiresSessionReset"] = status == "unknown", ["Error"] = status == "unknown" ? "Import reply lost." : "",
        ["Release"] = "21", ["ProjectFile"] = "C:/fixture/Fixture.ap21", ["ProcessId"] = 123,
        ["TargetIdentity"] = "PLC/DB_Fixture", ["SoftwarePath"] = "PLC", ["GroupPath"] = "",
        ["InputDirectory"] = "C:/fixture/documents", ["DeclaredName"] = "DB_Fixture", ["Kind"] = "GlobalDB", ["Language"] = "DB",
        ["CodeSha256"] = Hash, ["ResourceSha256"] = Hash, ["Inventory"] = new JsonArray(),
        ["ExistsVerified"] = executed, ["ContentVerified"] = false, ["NativeState"] = executed ? "Success" : "notRun",
        ["ImportedIdentities"] = executed ? new JsonArray("PLC/DB_Fixture") : new JsonArray(), ["Messages"] = new JsonArray() };

    private static JsonObject BatchDocumentImport(string status) => new() {
        ["Policy"] = "batch-document-global-db-v1", ["PlanHash"] = Hash, ["Status"] = status,
        ["Attempted"] = true, ["MayHaveChanged"] = true, ["RequiresSessionReset"] = status == "unknown",
        ["Error"] = status == "unknown" ? "Import reply lost." : "", ["Items"] = new JsonArray(new JsonObject {
            ["Name"] = "DB_Fixture", ["Status"] = status, ["Preview"] = DocumentImport("planned", false),
            ["Outcome"] = DocumentImport(status), ["PostInventory"] = new JsonArray("DB_Fixture") }) };

    [Fact]
    public void Applied_batch_document_import_succeeds()
    {
        var definition = FoundationTools.Definitions.Single(d => d.Name == "ImportPlcBlocksDocuments");
        var body = FoundationV4Result.Worker("21", definition, "fixture", new() { ["dryRun"] = false },
            BatchDocumentImport("succeeded"), null).StructuredContent!;
        Verdict(body, "succeeded", "completed", null);
    }

    [Fact]
    public void Uncertain_batch_document_import_stays_unknown()
    {
        var definition = FoundationTools.Definitions.Single(d => d.Name == "ImportPlcBlocksDocuments");
        var body = FoundationV4Result.Worker("21", definition, "fixture", new() { ["dryRun"] = false },
            BatchDocumentImport("unknown"), null).StructuredContent!;
        Verdict(body, "unknown", "unknown", "OUTCOME_UNKNOWN");
    }

    [Fact]
    public void Document_import_status_unknown_stays_unknown()
    {
        var definition = FoundationTools.Definitions.Single(d => d.Name == "ImportPlcBlockDocuments");
        var body = FoundationV4Result.Worker("21", definition, "fixture", new() { ["dryRun"] = false },
            DocumentImport("unknown"), null).StructuredContent!;
        Verdict(body, "unknown", "unknown", "OUTCOME_UNKNOWN");
    }

    private static JsonObject ReadbackMismatch() => new() {
        ["mayHaveChanged"] = true, ["operationSuccess"] = false, ["verificationSuccess"] = false, ["automaticRollback"] = false,
        ["importReturned"] = true, ["compileReturned"] = true, ["compileState"] = "Success", ["phase"] = "complete",
        ["status"] = "DocumentReadbackMismatch", ["afterPath"] = "C:/fixture/after.xml", ["plannedPath"] = "C:/fixture/planned.xml",
        ["cleanupRequired"] = new JsonArray(new JsonObject {
            ["target"] = "PLC/FC_Fixture", ["restoreFrom"] = "C:/fixture/before.xml", ["action"] = "Compare and restore manually if needed." }) };

    [Fact]
    public void Readback_mismatch_is_a_known_failure()
    {
        var body = Host.PlcToolContract.Map("ImportPlcBlockVerified", new Host.ResponseMessage { Meta = ReadbackMismatch() },
            true, true).StructuredContent!;
        Verdict(body, "failed", "completed", "NATIVE_OPERATION_FAILED");
        Assert.Equal("The import returned, but the re-exported document differs from the plan. "
            + "Compare the planned and after documents; the backup is retained.", (string?)body["error"]?["message"]);
        Assert.NotEmpty(body["data"]!["evidence"]!["cleanupRequired"]!.AsArray());
    }

    [Fact]
    public void Thrown_import_with_cleanup_stays_unknown()
    {
        var evidence = ReadbackMismatch();
        evidence["phase"] = "import";
        evidence.Remove("importReturned");
        evidence.Remove("status");
        var body = Host.PlcToolContract.Map("ImportPlcBlockVerified", new Host.ResponseMessage { Meta = evidence }, true, true).StructuredContent!;
        Verdict(body, "unknown", "unknown", "OUTCOME_UNKNOWN");
    }

    [Theory]
    [InlineData("writeOutcomeUnknown")]
    [InlineData("connectionUnavailable")]
    [InlineData("unrestoredWrite")]
    public void Readback_mismatch_with_unresolved_write_stays_unknown(string flag)
    {
        var evidence = ReadbackMismatch();
        evidence[flag] = true;
        var body = Host.PlcToolContract.Map("ImportPlcBlockVerified", new Host.ResponseMessage { Meta = evidence }, true, true).StructuredContent!;
        Verdict(body, "unknown", "unknown", "OUTCOME_UNKNOWN");
    }

    [Fact]
    public void Event_refused_before_any_write_keeps_the_native_reason()
    {
        const string reason = "ScriptCode is not supported for event script action.";
        var body = Host.HmiInspectionContract.Map("ManageUnifiedEvent", new Host.ResponseMessage { Meta = new JsonObject {
            ["mayHaveChanged"] = false, ["operationSuccess"] = false, ["apiCallSuccess"] = false, ["dataComplete"] = false,
            ["status"] = "ReadOrWriteFailed", ["appliedProperties"] = new JsonArray(), ["lastAttemptedProperty"] = "ScriptCode",
            ["nativeMessage"] = reason } }, true, true).StructuredContent!;
        Verdict(body, "rejected-before-operation", "not-started", "PRECONDITION_FAILED");
        Assert.Equal(reason, (string?)body["error"]?["message"]);
    }

    [Fact]
    public void Event_write_started_without_proof_stays_unknown()
    {
        var body = Host.HmiInspectionContract.Map("ManageUnifiedEvent", new Host.ResponseMessage { Meta = new JsonObject {
            ["mayHaveChanged"] = true, ["operationSuccess"] = false, ["apiCallSuccess"] = false, ["status"] = "ReadOrWriteFailed",
            ["appliedProperties"] = new JsonArray("ScriptCode"), ["nativeMessage"] = "Write reply lost." } }, true, true).StructuredContent!;
        Verdict(body, "unknown", "unknown", "OUTCOME_UNKNOWN");
    }

    [Fact]
    public void Compile_summary_line_is_not_counted_as_an_error()
    {
        var response = new Host.ResponseMessage { Meta = new JsonObject {
            ["operationSuccess"] = false, ["effectiveState"] = "Error", ["rootState"] = "Error",
            ["rootCounts"] = new JsonObject { ["errors"] = 2, ["warnings"] = 1 },
            ["declaredTotals"] = new JsonArray(new JsonObject { ["treePath"] = "messages/3", ["errors"] = 2, ["warnings"] = 1 }),
            ["nodes"] = new JsonArray(
                new JsonObject { ["treePath"] = "messages/0", ["parentPath"] = "messages", ["State"] = "Error",
                    ["Path"] = "PLC/FC_Fixture", ["Description"] = "Input symbol is missing." },
                new JsonObject { ["treePath"] = "messages/1", ["parentPath"] = "messages", ["State"] = "Error",
                    ["Path"] = "PLC/FC_Fixture", ["Description"] = "Output symbol is missing." },
                new JsonObject { ["treePath"] = "messages/2", ["parentPath"] = "messages", ["State"] = "Warning",
                    ["Description"] = "Unused local symbol." },
                new JsonObject { ["treePath"] = "messages/3", ["parentPath"] = "messages", ["State"] = "Error",
                    ["Description"] = "Compiling finished (errors: 2; warnings: 1)" }) } };
        var body = Host.HardwareToolContract.MapResult("CompileDevice", response, false, true).StructuredContent!;
        Verdict(body, "failed", "completed", "COMPILE_ERRORS");
        Assert.Equal(2, (int)body["error"]!["details"]!["errorCount"]!);
        Assert.Equal(2, body["error"]!["details"]!["errors"]!.AsArray().Count);
    }
}
