using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Xunit;

public sealed class P707cFoundationTests
{
    [Fact]
    public void Single_SD_export_warns_on_inconsistency_and_retains_a_thrown_native_reason()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            string output = Path.Combine(root.FullName, "RC-20-V-SD-MCP_RC_OBS");
            // Row 22 recorded only the exception type. Use the recorded row 89 diagnostic to exercise message transport.
            const string reason = "Compile the inconsistent object before export.";
            const string software = "devices/MCP_RC_PLC/MCP_RC_PLC";
            PlcDocumentExportResult Export(bool consistent, bool dry = true, string hash = "") => PlcDocumentExportPolicy.Run(
                "20", @"C:\Projects\项目1.ap20", software, "MCP_RC_OBS", output, "LAD", consistent, dry, hash, () => { },
                (_, _) => throw new InvalidOperationException(reason), move: (_, _) => throw new InvalidOperationException(),
                exportDiagnostic: NativeFailureText.BoundedDiagnostic);
            JsonNode Envelope(PlcDocumentExportResult result, bool preview)
            {
                var args = new JsonObject { ["dryRun"] = preview, ["softwarePath"] = software, ["blockPath"] = result.BlockPath,
                    ["exportPath"] = output, ["expectedProjectFile"] = result.ProjectFile, ["expectedPlanHash"] = result.PlanHash };
                var raw = JsonSerializer.SerializeToNode(result)!;
                WorkerProtocol.ValidateExchangeResult("ExportAsDocuments", args, raw);
                var entry = FoundationTools.Definitions.Single(d => d.Name == "ExportPlcBlockDocuments");
                return FoundationV4Result.Worker("20", entry, "document-export", args, raw, raw).StructuredContent!;
            }
            var preview = Envelope(Export(false), true);
            Assert.True((bool?)preview["ok"]);
            Assert.True((bool?)preview["data"]!["applyBlocked"]);
            Assert.Contains(preview["meta"]!["warnings"]!.AsArray(), w => (string?)w!["code"] == "NATIVE_WARNING" && (string?)w["message"] == reason);
            var plan = Export(true);
            Assert.DoesNotContain(Envelope(plan, true)["meta"]!["warnings"]!.AsArray(), w => (string?)w!["code"] == "NATIVE_WARNING");
            var failed = Envelope(Export(true, false, plan.PlanHash), false);
            Assert.Equal("NATIVE_OPERATION_FAILED", (string?)failed["error"]!["code"]);
            Assert.Equal(reason, (string?)failed["error"]!["message"]);
            Assert.Equal(reason, (string?)failed["error"]!["details"]!["nativeMessage"]);
            Assert.True(Directory.Exists((string?)failed["data"]!["recoveryDirectory"]));
            Assert.False(Directory.Exists(output));
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("Failure", false)]
    [InlineData("PartialSuccess", false)]
    [InlineData("987654", false)]
    [InlineData(null, false)]
    public void FileOnlyDocumentResultsRetainRawStateAndResetOnlyForUncertainty(string? state, bool reset)
    {
        // A short root: the batch staging path adds about 135 characters, and the release run's isolated TEMP is 31 characters
        // deeper than a user TEMP, which pushed the 32-hex root past the product's 259-character path bound.
        string root = Path.Combine(Path.GetTempPath(), "p7c-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "single");
            PlcDocumentExportResult Run(bool preview, string hash = "") => PlcDocumentExportPolicy.Run("21", "C:/Example.ap21", "PLC_1", "Block", path,
                "LAD", true, preview, hash, () => { }, (_, _) => throw new NativeResultException(NativeResultStates.Documents, state));
            var plan = Run(true);
            var single = Run(false, plan.PlanHash);
            Assert.Equal("failed", single.Status);
            Assert.Equal(reset, single.OutcomeUnknown);
            Assert.Equal(state, single.NativeResult!.State);
            DocumentExportContract.Validate(JsonSerializer.SerializeToNode(single), false);
            var definition = FoundationTools.Definitions.Single(d => d.ResponseMember == "DocumentExport");
            var wire = FoundationV4Result.Worker("21", definition, "native-state", new JsonObject { ["dryRun"] = false },
                JsonSerializer.SerializeToNode(single), JsonSerializer.SerializeToNode(single)).StructuredContent!;
            Assert.Equal(reset ? "OUTCOME_UNKNOWN" : "NATIVE_OPERATION_FAILED", (string?)wire["error"]?["code"]);
            Assert.Equal(reset, (string?)wire["meta"]?["outcome"] == "unknown");
            Assert.Equal(state, (string?)wire["error"]?["details"]?["evidence"]?["nativeState"]);

            string batchPath = Path.Combine(root, "batch");
            var source = new PlcBatchDocumentExportSource { Path = "Block", Language = "LAD", Consistent = true,
                Export = (_, _) => throw new NativeResultException(NativeResultStates.Documents, state) };
            PlcBatchDocumentExportResult Batch(bool preview, string hash = "") => PlcBatchDocumentExportPolicy.Run("21", "C:/Example.ap21", "PLC_1", "", false,
                batchPath, 128, preview, hash, new[] { source }, () => { });
            var batchPlan = Batch(true);
            var batch = Batch(false, batchPlan.PlanHash);
            Assert.Equal(reset, batch.OutcomeUnknown);
            Assert.Equal(state, batch.NativeResult!.State);
            var request = new JsonObject { ["softwarePath"] = "PLC_1", ["groupPath"] = "", ["exportPath"] = batchPath,
                ["dryRun"] = false, ["expectedPlanHash"] = batchPlan.PlanHash, ["expectedProjectFile"] = "C:/Example.ap21" };
            BatchDocumentExportContract.Validate(JsonSerializer.SerializeToNode(batch), request);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("Success", "succeeded")]
    [InlineData("Information", "succeeded")]
    [InlineData("Warning", "succeeded")]
    [InlineData("Error", "failed")]
    [InlineData("987654", "failed")]
    public void EveryFoundationReleaseUsesTheExactCompilerEnum(string state, string outcome)
    {
        foreach (var definition in FoundationTools.Definitions.Where(d => d.ResponseMember == "Compile"))
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        {
            var raw = new JsonObject { ["Executed"] = true, ["State"] = state, ["ErrorCount"] = state == "Error" ? 1 : 0, ["WarningCount"] = 0 };
            var wire = FoundationV4Result.Worker(release, definition, "compile-state", new JsonObject { ["dryRun"] = false }, raw, raw).StructuredContent!;
            Assert.Equal(outcome, (string?)wire["meta"]?["outcome"]);
            Assert.Equal(state == "Warning", wire["meta"]!["warnings"]!.AsArray().Any(w => (string?)w?["code"] == "NATIVE_WARNING"));
        }
    }
}
