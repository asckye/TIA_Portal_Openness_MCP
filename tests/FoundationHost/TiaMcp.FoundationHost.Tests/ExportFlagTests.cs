using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Xunit;

public sealed class ExportFlagTests
{
    [Theory]
    [InlineData("batch")]
    [InlineData("special")]
    [InlineData("document")]
    [InlineData("batch-document")]
    public void Failed_export_keeps_file_effects_without_an_unknown_project_write(string kind)
    {
        IWorkerOperationReply reply = kind switch {
            "batch" => new PlcBatchExportResult { Items = new[] { new PlcBatchExportItem { Status = "exported" } } },
            "special" => new PlcSpecialExportResult { Status = "failed", Evidence = new() { ["recoveryDirectory"] = "retained" } },
            "document" => new PlcDocumentExportResult { Status = "failed", RecoveryDirectory = "retained" },
            _ => new PlcBatchDocumentExportResult { Status = "failed", RecoveryDirectory = "retained" }
        };
        Assert.True(reply.MayHaveChanged);
        Assert.False(reply.RequiresSessionReset);
    }

    [Fact]
    public void Failed_export_with_retained_files_is_valid_and_rejects_a_forged_unknown_flag()
    {
        string root = Path.Combine(Path.GetTempPath(), "export-flag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PlcSpecialExportResult Run(bool preview, string hash = "") => PlcSpecialExportPolicy.Run("watch-table", "21",
                "C:/fixture.ap21", "PLC", "Watch", Path.Combine(root, "watch.xml"), true, preview, hash, () => { }, _ => { },
                (_, _) => {
                    var error = new IOException("Synthetic publication failure.");
                    error.Data["recoveryDirectory"] = root;
                    throw error;
                });
            var plan = Run(true);
            var failed = Run(false, plan.PlanHash);
            Assert.Equal("failed", failed.Status);
            Assert.False(failed.RequiresSessionReset);
            Assert.True(((IWorkerOperationReply)failed).MayHaveChanged);
            var wire = JsonSerializer.SerializeToNode(failed)!.AsObject();
            SpecialExportContract.Validate(wire, false);
            wire["RequiresSessionReset"] = true;
            Assert.Throws<InvalidDataException>(() => SpecialExportContract.Validate(wire, false));
        }
        finally { Directory.Delete(root, true); }
    }
}
