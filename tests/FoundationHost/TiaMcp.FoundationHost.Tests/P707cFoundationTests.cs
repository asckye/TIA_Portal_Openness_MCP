using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Xunit;

public sealed class P707cFoundationTests
{
    [Theory]
    [InlineData("Failure", false)]
    [InlineData("PartialSuccess", true)]
    [InlineData("987654", true)]
    [InlineData(null, true)]
    public void FileOnlyDocumentResultsRetainRawStateAndResetOnlyForUncertainty(string? state, bool reset)
    {
        string root = Path.Combine(Path.GetTempPath(), "p707c-foundation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "single");
            PlcDocumentExportResult Run(bool preview, string hash = "") => PlcDocumentExportPolicy.Run("21", "C:/Example.ap21", "PLC_1", "Block", path,
                "LAD", true, preview, hash, () => { }, (_, _) => throw new NativeResultException(NativeResultStates.Documents, state));
            var plan = Run(true);
            var single = Run(false, plan.PlanHash);
            Assert.Equal("failed", single.Status);
            Assert.Equal(reset, single.RequiresSessionReset);
            Assert.Equal(state, single.NativeResult!.State);
            DocumentExportContract.Validate(JsonSerializer.SerializeToNode(single), false);
            var definition = FoundationTools.Definitions.Single(d => d.ResponseMember == "DocumentExport");
            var wire = FoundationV4Result.Worker("21", definition, "native-state", new JsonObject { ["dryRun"] = false },
                JsonSerializer.SerializeToNode(single), JsonSerializer.SerializeToNode(single)).StructuredContent!;
            Assert.Equal(reset ? "OUTCOME_UNKNOWN" : "NATIVE_OPERATION_FAILED", (string?)wire["error"]?["code"]);
            Assert.Equal(reset, (bool?)wire["meta"]?["requiresSessionReset"]);
            Assert.Equal(state, (string?)wire["error"]?["details"]?["evidence"]?["nativeState"]);

            string batchPath = Path.Combine(root, "batch");
            var source = new PlcBatchDocumentExportSource { Path = "Block", Language = "LAD", Consistent = true,
                Export = (_, _) => throw new NativeResultException(NativeResultStates.Documents, state) };
            PlcBatchDocumentExportResult Batch(bool preview, string hash = "") => PlcBatchDocumentExportPolicy.Run("21", "C:/Example.ap21", "PLC_1", "", false,
                batchPath, 128, preview, hash, new[] { source }, () => { });
            var batchPlan = Batch(true);
            var batch = Batch(false, batchPlan.PlanHash);
            Assert.Equal(reset, batch.RequiresSessionReset);
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
    [InlineData("987654", "unknown")]
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
