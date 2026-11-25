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
    public async Task Stored_equal_keeps_the_refusal_in_status_and_sync_without_changing_selection(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var replies = await StatusReplies(release);
        var freshStatus = Tools(replies["freshStatus"]!).GetVersionControlStatusV4(changedOnly: false).StructuredContent!;
        var freshPreview = Tools(replies["freshPreview"]!).SynchronizeVersionControlWorkspaceV4(dryRun: true).StructuredContent!;
        var freshApply = Tools(replies["freshApply"]!).SynchronizeVersionControlWorkspaceV4(dryRun: false, changedOnly: false).StructuredContent!;
        foreach (var fresh in new[] { freshStatus, freshPreview, freshApply })
        {
            Assert.Equal(0, (int?)fresh["data"]!["evidence"]!["unverifiedEqual"]);
            Assert.DoesNotContain("refreshMessage", fresh.ToJsonString());
        }
        string? expectedMessage = null;
        foreach (var key in new[] { "storedStatus", "storedFiltered", "storedPreview", "storedApply", "mixedPreview", "mixedApply" })
        {
            var scenario = replies[key]!;
            bool status = key is "storedStatus" or "storedFiltered";
            var tools = Tools(scenario);
            var body = (status ? tools.GetVersionControlStatusV4(changedOnly: key == "storedFiltered")
                : tools.SynchronizeVersionControlWorkspaceV4(dryRun: key.EndsWith("Preview"), changedOnly: key.EndsWith("Preview"))).StructuredContent!;
            var data = body["data"]!;
            var evidence = data["evidence"]!;
            var fresh = status ? freshStatus : key.EndsWith("Preview") ? freshPreview : freshApply;
            Assert.Equal((string)fresh["meta"]!["completeness"]!, (string?)body["meta"]!["completeness"]);
            Assert.True((bool?)body["ok"]);
            Assert.Equal(1, (int?)evidence["unverifiedEqual"]);
            Assert.True((bool?)evidence["dataComplete"]);
            string summary = (string)data["summary"]!;
            Assert.Contains("1 object(s) reported Equal from the status TIA stored", summary);
            Assert.Contains("refused the refresh", summary);
            Assert.Contains("Check the workspace files if an object was edited", summary);
            Assert.DoesNotContain("in sync", summary);
            Assert.DoesNotContain("nothing to commit", summary);
            Assert.DoesNotContain("already equal", summary);
            Assert.DoesNotContain("fixture-password", body.ToJsonString());
            Calls(scenario, "Stored", "UpdateStatus", "GetStatus");
            var rows = evidence[status ? "objects" : "items"]!.AsArray();
            if (status)
            {
                Assert.Equal(0, (int?)evidence["differing"]);
                Assert.Equal(0, (int?)evidence["unknown"]);
            }
            else
            {
                Assert.Equal(1, (int?)evidence["skippedEqual"]);
                Assert.Equal(key == "mixedApply" ? 1 : 0, (int?)evidence["exportsIssued"]);
            }
            if (key == "storedFiltered")
            {
                Assert.Empty(rows);
                Assert.Empty(data["items"]!.AsArray());
                continue;
            }
            var row = rows.Single(r => (string?)r!["name"] == "Stored")!;
            Assert.Equal("Equal", (string?)row[status ? "compareState" : "stateBefore"]);
            Assert.Null(row["error"]);
            string message = (string)row["refreshMessage"]!;
            Assert.Contains("The file is deleted", message);
            expectedMessage ??= message;
            Assert.Equal(expectedMessage, message);
            Assert.Contains(data["items"]!.AsArray(), item => ((string)item!).EndsWith(message, StringComparison.Ordinal));
        }
        var statusRows = Tools(replies["status"]!).GetVersionControlStatusV4(changedOnly: false).StructuredContent!["data"]!["evidence"]!["objects"]!;
        Assert.False(statusRows.AsArray().Single(row => (string?)row!["name"] == "Refreshed")!.AsObject().ContainsKey("refreshMessage"));
        var longRow = Tools(replies["longRefusal"]!).GetVersionControlStatusV4(changedOnly: false).StructuredContent!["data"]!["evidence"]!["objects"]![0]!;
        string bounded = (string)longRow["refreshMessage"]!;
        Assert.StartsWith("The file is deleted", bounded);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(bounded), 1, 4096);
        var equal = Tools(replies["equalPreview"]!).SynchronizeVersionControlWorkspaceV4(dryRun: true, changedOnly: false).StructuredContent!["data"]!;
        Assert.Equal(release == "16" ? 0 : 1, (int?)equal["evidence"]!["unverifiedEqual"]);
        if (release == "16")
        {
            Assert.Contains("are in sync", (string?)equal["summary"]);
            Assert.DoesNotContain("refreshMessage", equal.ToJsonString());
        }
    }

    [Fact]
    public async Task A_failed_sync_keeps_its_stored_readback_and_does_not_confirm_the_write()
    {
        using var selected = Host.HardwareContract.UseRelease("17");
        var scenario = (await StatusReplies("17"))["storedReadback"]!;
        var data = Tools(scenario).SynchronizeVersionControlWorkspaceV4(dryRun: false, changedOnly: false).StructuredContent!["data"]!;
        var evidence = data["evidence"]!;
        Assert.Equal(0, (int?)evidence["skippedEqual"]);
        Assert.Equal(1, (int?)evidence["unverifiedEqual"]);
        Assert.Equal(1, (int?)evidence["exportsIssued"]);
        Assert.True((bool?)evidence["writeOutcomeUnknown"]);
        var attempt = evidence["items"]![0]!;
        var readback = evidence["failures"]![0]!;
        Assert.Equal("Unequal", (string?)attempt["stateBefore"]);
        Assert.Equal("Equal", (string?)readback["statusAfter"]);
        Assert.Contains("The file is deleted", (string?)readback["refreshMessage"]);
        Assert.Equal((string)attempt["refreshMessage"]!, (string?)readback["refreshMessage"]);
        Assert.Contains("The file is deleted", (string?)data["items"]![0]);
        Assert.Contains("Check the workspace files", (string?)data["summary"]);

        var distinct = (await StatusReplies("17"))["distinctReadback"]!;
        var result = Tools(distinct).SynchronizeVersionControlWorkspaceV4(dryRun: false, changedOnly: false).StructuredContent!["data"]!;
        var selection = (string)result["evidence"]!["items"]![0]!["refreshMessage"]!;
        var after = (string)result["evidence"]!["failures"]![0]!["refreshMessage"]!;
        Assert.Contains("Selection refresh refused", selection);
        Assert.Contains("The file is deleted", after);
        Assert.Contains(selection, (string?)result["items"]![0]);
        Assert.Contains(after, (string?)result["items"]![0]);
        Assert.DoesNotContain("fixture-password", result.ToJsonString());
    }

    [Fact]
    public async Task Fatal_fallback_errors_replace_the_refresh_refusal_on_their_own_mapping()
    {
        using var selected = Host.HardwareContract.UseRelease("17");
        var replies = await StatusReplies("17");
        var scenario = replies["probeFailures"]!;
        var body = Tools(scenario).GetVersionControlStatusV4(changedOnly: false).StructuredContent!;
        var rows = body["data"]!["evidence"]!["objects"]!.AsArray().ToDictionary(row => (string)row!["name"]!);
        foreach (var kind in new[] { "Disposed", "Aborted", "Disconnected", "Nonrecoverable" })
        {
            var fallback = rows["Fallback" + kind]!;
            Assert.Equal("Unknown", (string?)fallback["compareState"]);
            Assert.Equal((string?)rows[kind]!["error"], (string?)fallback["error"]);
            Assert.DoesNotContain("The file is deleted", (string?)fallback["error"]);
            Assert.False(fallback.AsObject().ContainsKey("refreshMessage"));
            Calls(scenario, "Fallback" + kind, "UpdateStatus", "GetStatus");
        }
        var sync = Tools(replies["probePreview"]!).SynchronizeVersionControlWorkspaceV4(dryRun: true).StructuredContent!;
        Assert.Equal((string)rows["BothRefused"]!["error"]!, (string?)sync["data"]!["evidence"]!["statusProbe"]!["nativeMessage"]);
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
