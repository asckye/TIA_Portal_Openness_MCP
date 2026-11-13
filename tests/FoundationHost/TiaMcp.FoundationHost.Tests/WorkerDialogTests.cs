extern alias foundationhost;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using Xunit;
using Foundation = foundationhost::TiaMcp.FoundationHost;

public sealed class WorkerDialogTests
{
    [Fact]
    public void Dialog_text_is_bounded_and_redacted()
    {
        string secret = "dialog-secret-7f3";
        string line = PortalDialogPolicy.Report(new() { ["event"] = "Confirmation", ["utc"] = "2026-11-13T00:00:00Z",
            ["caption"] = "Export", ["text"] = "password=" + secret, ["detailText"] = @"C:\private\project.ap21",
            ["choices"] = new[] { "Yes" }, ["answer"] = "unanswered" });
        var dialog = Foundation.WorkerDialog.Parse(line);
        Assert.NotNull(dialog);
        Assert.DoesNotContain(secret, dialog.ToJsonString());
        Assert.DoesNotContain("private", dialog.ToJsonString());
        Assert.Equal("Export", (string?)dialog["caption"]);
        Assert.Null(Foundation.WorkerDialog.Parse("TIA_MCP_DIALOG " + new string('x', 2048)));
        Assert.Null(Foundation.WorkerDialog.Parse("TIA_MCP_DIALOG {bad}"));
        Assert.Null(Foundation.WorkerDialog.Parse("TIA notification: legacy text"));
        Assert.Null(Foundation.WorkerDialog.Parse("TIA_MCP_DIALOG {\"event\":\"Confirmation\",\"text\":{}}"));
    }
}
