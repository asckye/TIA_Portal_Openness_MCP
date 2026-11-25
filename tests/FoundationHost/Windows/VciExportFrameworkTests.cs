extern alias toolpipeline;
using System.Diagnostics;
using System.Text.Json.Nodes;
using TiaMcp.Engine.Tests.Shared;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Services = toolpipeline::TiaMcpServer.Siemens.Services;

namespace TiaMcp.FoundationHost.Tests;

public sealed class VciExportFrameworkTests
{
    private static async Task<JsonNode> StatusReplies(string release)
    {
        string file = Path.Combine(AppContext.BaseDirectory, "vci-net48", "TiaMcp.VciExport.Net48.Tests.exe");
        using var process = Process.Start(new ProcessStartInfo(file, "--status-replies") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TestDeadlines.Hang);
        Assert.True(process.ExitCode == 0, await errors);
        return JsonNode.Parse(await output)![release == "16" ? "v16" : "v17To19"]!;
    }

    private static Host.VersionControlTools Tools(JsonNode scenario) => new(new Services.VersionControlPortService(
        (_, _) => scenario["reply"], () => "Line.ap" + Host.HardwareContract.ReleaseKey));

    private static void Calls(JsonNode scenario, string mapping, params string[] expected) =>
        Assert.Equal(expected, scenario["calls"]![mapping]!.AsArray().Select(call => (string?)call));

    [Theory, InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19")]
    public async Task Status_refreshes_before_reading_and_keeps_each_native_error(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var replies = await StatusReplies(release);
        var edited = replies["edited"]!;
        var editedBody = Tools(edited).GetVersionControlStatusV4(changedOnly: false).StructuredContent!;
        var editedRow = editedBody["data"]!["evidence"]!["objects"]!.AsArray().Single()!;
        Assert.Equal(release == "16" ? "Unequal" : "Equal", (string?)editedRow["compareState"]);
        Assert.Null(editedRow["error"]);
        Calls(edited, "Motor", "UpdateStatus", "GetStatus");
        var status = replies["status"]!;
        var body = Tools(status).GetVersionControlStatusV4(changedOnly: false).StructuredContent!;
        var evidence = body["data"]!["evidence"]!;
        var objects = evidence["objects"]!.AsArray().ToDictionary(row => (string)row!["name"]!);
        Assert.Equal("Unequal", (string?)objects["Motor"]!["compareState"]);
        Assert.Equal("WorkspaceFileMissing", (string?)objects["Missing"]!["compareState"]);
        Assert.Equal("Equal", (string?)objects["Refreshed"]!["compareState"]);
        Assert.Equal("Equal", (string?)objects["Next"]!["compareState"]);
        Assert.Null(objects["Next"]!["error"]);
        Assert.Equal("Unknown", (string?)objects["Pending"]!["compareState"]);
        Assert.Null(objects["Pending"]!["error"]);
        Assert.Equal("Unknown", (string?)objects["Deleted"]!["compareState"]);
        Assert.StartsWith("Error when calling method 'UpdateStatus' of type 'Siemens.Engineering.VersionControl.IndividualObjectSynchronizationStatus'. "
            + "The file is deleted", (string?)objects["Deleted"]!["error"]);
        string error = (string)objects["Deleted"]!["error"]!;
        Assert.Contains(body["data"]!["items"]!.AsArray(), item => ((string)item!).StartsWith("Deleted | Unknown(" + error + ")", StringComparison.Ordinal));
        Assert.DoesNotContain("fixture-password", body.ToJsonString());
        Assert.Equal(2, (int?)evidence["unknown"]);
        Assert.Equal(2, (int?)evidence["differing"]);
        Assert.Equal(false, (bool?)evidence["dataComplete"]);
        foreach (string name in objects.Keys) Calls(status, name, "UpdateStatus", "GetStatus");
        var changed = Tools(replies["changedStatus"]!).GetVersionControlStatusV4().StructuredContent!;
        Assert.Equal(new[] { "Motor", "Missing", "Pending", "Deleted" },
            changed["data"]!["evidence"]!["objects"]!.AsArray().Select(row => (string?)row!["name"]));
        Assert.Contains("Deleted | Unknown(" + error + ")", (string?)changed["data"]!["items"]![3]);
    }

    [Theory, InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19")]
    public async Task Only_an_ordinary_native_refresh_refusal_allows_a_status_read(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var failures = (await StatusReplies(release))["probeFailures"]!;
        var data = Tools(failures).GetVersionControlStatusV4(changedOnly: false).StructuredContent!["data"]!;
        var objects = data["evidence"]!["objects"]!.AsArray().ToDictionary(row => (string)row!["name"]!);
        Assert.Equal("WorkspaceFileMissing", (string?)objects["MissingFile"]!["compareState"]);
        Assert.Null(objects["MissingFile"]!["error"]);
        Assert.Contains("The file is deleted", (string?)objects["BothRefused"]!["error"]);
        Assert.DoesNotContain("Status unavailable", (string?)objects["BothRefused"]!["error"]);
        Assert.Equal("Status unavailable", (string?)objects["ReadRefused"]!["error"]);
        foreach (string name in new[] { "MissingFile", "ReadRefused", "BothRefused" }) Calls(failures, name, "UpdateStatus", "GetStatus");
        foreach (string name in new[] { "Disposed", "Aborted", "Disconnected", "StreamClosed", "Nonrecoverable", "Unexpected" })
            Calls(failures, name, "UpdateStatus");
        foreach (var (name, row) in objects.Where(pair => pair.Key != "MissingFile"))
        {
            Assert.Equal("Unknown", (string?)row!["compareState"]);
            string message = (string)row["error"]!;
            Assert.False(string.IsNullOrEmpty(message));
            Assert.Contains(data["items"]!.AsArray(), item => ((string)item!).StartsWith(name + " | Unknown(" + message + ")", StringComparison.Ordinal));
        }
        Assert.DoesNotContain("fixture-password", data.ToJsonString());
    }

    [Fact]
    public async Task Modern_status_read_errors_keep_the_existing_host_diagnostic_policy()
    {
        using var selected = Host.HardwareContract.UseRelease("20");
        var status = (await StatusReplies("19"))["status"]!;
        var body = Tools(status).GetVersionControlStatusV4(changedOnly: false).StructuredContent!;
        var row = body["data"]!["evidence"]!["objects"]!.AsArray().Single(item => (string?)item!["name"] == "Deleted");
        Assert.Equal("Unknown", (string?)row!["compareState"]);
        Assert.Equal("Native diagnostic details are retained in the server log.", (string?)row["error"]);
        Assert.DoesNotContain("The file is deleted", body.ToJsonString());
        Assert.DoesNotContain("fixture-password", body.ToJsonString());
    }

    [Theory, InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19")]
    public async Task Sync_reports_equal_without_a_write_and_keeps_unknown_selection_and_file_proof(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var replies = await StatusReplies(release);
        foreach (bool preview in new[] { true, false })
        {
            var scenario = replies[preview ? "equalPreview" : "equalApply"]!;
            var body = Tools(scenario).SynchronizeVersionControlWorkspaceV4(dryRun: preview, changedOnly: false).StructuredContent!;
            var evidence = body["data"]!["evidence"]!;
            Assert.True((bool?)body["ok"]);
            Assert.Contains("reported Equal", (string?)body["data"]!["summary"]);
            Assert.Equal(1, (int?)evidence["skippedEqual"]);
            Assert.Equal(0, (int?)evidence["exportsIssued"]);
            Assert.Equal(0, (int?)evidence["failed"]);
            Assert.False((bool?)evidence["mayHaveChanged"]);
            Calls(scenario, "Motor", "UpdateStatus", "GetStatus");
        }
        var changed = replies["changedSync"]!;
        var changedBody = Tools(changed).SynchronizeVersionControlWorkspaceV4(dryRun: false).StructuredContent!;
        var counts = changedBody["data"]!["evidence"]!;
        Assert.Equal(2, (int?)counts["skippedEqual"]);
        Assert.Equal(2, (int?)counts["skippedUnknown"]);
        Assert.Equal(2, (int?)counts["synchronized"]);
        Calls(changed, "Motor", "UpdateStatus", "GetStatus", "Synchronize:ProjectToWorkspace");
        Calls(changed, "Deleted", "UpdateStatus", "GetStatus");

        var all = replies["allSync"]!;
        var allBody = Tools(all).SynchronizeVersionControlWorkspaceV4(dryRun: false, changedOnly: false).StructuredContent!;
        var result = allBody["data"]!["evidence"]!;
        Assert.Equal("PARTIAL_FAILURE", (string?)allBody["error"]!["code"]);
        Assert.Equal(4, (int?)result["skippedEqual"]);
        Assert.Equal(2, (int?)result["exportsIssued"]);
        Assert.Equal(1, (int?)result["synchronized"]);
        Assert.Equal(1, (int?)result["failed"]);
        Assert.Equal(0, (int?)result["unconfirmed"]);
        Assert.False((bool?)result["writeOutcomeUnknown"]);
        Assert.Equal("Unknown", (string?)result["failures"]![0]!["statusAfter"]);
        Calls(all, "Deleted", "UpdateStatus", "GetStatus", "Synchronize:ProjectToWorkspace", "UpdateStatus", "GetStatus");
    }

    [Fact]
    public async Task Refused_exports_are_checked_on_the_worker_framework()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "vci-net48", "TiaMcp.VciExport.Net48.Tests.exe");
        using var process = Process.Start(new ProcessStartInfo(file) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TestDeadlines.Hang);
        string summary = await output;
        Assert.True(process.ExitCode == 0, summary + Environment.NewLine + await errors);
        Assert.Contains(".NET Framework 4.8", summary);
        var lines = summary.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        foreach (string scenario in new[] { "unchanged", "changed target", "changed unrelated file", "junction", "unreadable subfolder" })
            Assert.Contains("PASS " + scenario, lines);
    }
}
