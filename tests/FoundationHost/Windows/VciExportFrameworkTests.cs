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
        // Equal rows whose refresh was refused are not proven and stay listed.
        string[] unproven = release == "16" ? Array.Empty<string>() : new[] { "Refreshed", "Next" };
        var changedNames = changed["data"]!["evidence"]!["objects"]!.AsArray().Select(row => (string)row!["name"]!).ToArray();
        Assert.Equal(new[] { "Motor", "Missing", "Pending", "Deleted" }, changedNames.Except(unproven));
        Assert.Equal(unproven, changedNames.Intersect(unproven));
        int deleted = Array.IndexOf(changedNames, "Deleted");
        Assert.Contains("Deleted | Unknown(" + error + ")", (string?)changed["data"]!["items"]![deleted]);
    }

    [Fact]
    public async Task Status_diagnostics_follow_the_mapping_path_when_two_rows_have_the_same_name()
    {
        using var selected = Host.HardwareContract.UseRelease("17");
        var scenario = (await StatusReplies("17"))["status"]!.DeepClone();
        var reply = scenario["reply"]!;
        var rows = new JsonArray();
        var lines = new JsonArray();
        foreach (string folder in new[] { "Blocks", "Types" })
        {
            string path = folder + "/Shared.xml";
            rows.Add(new JsonObject { ["name"] = "Shared", ["filePath"] = path, ["fileFormat"] = "xml", ["compareState"] = "Unknown",
                ["error"] = folder + " status unavailable" });
            lines.Insert(0, JsonValue.Create("Shared | Unknown(raw error) | file=" + path + " | format=xml"));
        }
        reply["Meta"]!["objects"] = rows;
        reply["Items"] = lines;
        var data = Tools(scenario).GetVersionControlStatusV4(changedOnly: false).StructuredContent!["data"]!;
        Assert.Equal(new[] { "Shared | Unknown(Types status unavailable) | file=Types/Shared.xml | format=xml",
            "Shared | Unknown(Blocks status unavailable) | file=Blocks/Shared.xml | format=xml" }, Strings(data["items"]));
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
    public async Task Stored_equal_requires_proof_and_keeps_the_refresh_refusal(string release)
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
            bool apply = key.EndsWith("Apply");
            Assert.Equal("partial", (string?)body["meta"]!["completeness"]);
            Assert.Equal(!apply, (bool?)body["ok"]);
            if (apply) Assert.Equal(key == "mixedApply" ? "PARTIAL_FAILURE" : "NATIVE_OPERATION_FAILED", (string?)body["error"]!["code"]);
            Assert.Equal(1, (int?)evidence["unverifiedEqual"]);
            Assert.False((bool?)evidence["dataComplete"]);
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
                Assert.Equal(0, (int?)evidence["skippedEqual"]);
                Assert.Equal(apply ? 1 : 0, (int?)evidence["notVerified"]);
                Assert.Equal(apply ? 0 : 1, (int?)evidence["wouldVerify"]);
                Assert.Equal(key == "mixedApply" ? 1 : 0, (int?)evidence["exportsIssued"]);
            }
            if (key == "storedFiltered")
            {
                Assert.Single(rows);
                Assert.Single(data["items"]!.AsArray());
                Assert.False((bool?)rows[0]!["verified"]);
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
            bool pending = release != "16";
            Assert.Equal(!pending || preview, (bool?)body["ok"]);
            Assert.Contains("reported Equal", (string?)body["data"]!["summary"]);
            Assert.Equal(pending ? 0 : 1, (int?)evidence["skippedEqual"]);
            Assert.Equal(pending && !preview ? 1 : 0, (int?)evidence["notVerified"]);
            Assert.Equal(0, (int?)evidence["exportsIssued"]);
            Assert.Equal(pending && !preview ? 1 : 0, (int?)evidence["failed"]);
            Assert.False((bool?)evidence["mayHaveChanged"]);
            Calls(scenario, "Motor", "UpdateStatus", "GetStatus");
        }
        var changed = replies["changedSync"]!;
        var changedBody = Tools(changed).SynchronizeVersionControlWorkspaceV4(dryRun: false).StructuredContent!;
        var counts = changedBody["data"]!["evidence"]!;
        Assert.Equal(release == "16" ? 2 : 0, (int?)counts["skippedEqual"]);
        Assert.Equal(release == "16" ? 0 : 2, (int?)counts["notVerified"]);
        Assert.Equal(2, (int?)counts["skippedUnknown"]);
        Assert.Equal(2, (int?)counts["synchronized"]);
        Calls(changed, "Motor", "UpdateStatus", "GetStatus", "Synchronize:ProjectToWorkspace");
        Calls(changed, "Deleted", "UpdateStatus", "GetStatus");

        var all = replies["allSync"]!;
        var allBody = Tools(all).SynchronizeVersionControlWorkspaceV4(dryRun: false, changedOnly: false).StructuredContent!;
        var result = allBody["data"]!["evidence"]!;
        Assert.Equal("PARTIAL_FAILURE", (string?)allBody["error"]!["code"]);
        Assert.Equal(release == "16" ? 4 : 0, (int?)result["skippedEqual"]);
        Assert.Equal(release == "16" ? 0 : 4, (int?)result["notVerified"]);
        Assert.Equal(2, (int?)result["exportsIssued"]);
        Assert.Equal(1, (int?)result["synchronized"]);
        Assert.Equal(release == "16" ? 1 : 5, (int?)result["failed"]);
        Assert.Equal(0, (int?)result["unconfirmed"]);
        Assert.False((bool?)result["writeOutcomeUnknown"]);
        Assert.Equal("Unknown", (string?)result["failures"]![0]!["statusAfter"]);
        Calls(all, "Deleted", "UpdateStatus", "GetStatus", "Synchronize:ProjectToWorkspace", "UpdateStatus", "GetStatus");
    }

    private static async Task<JsonNode> ExportReplies(string release)
    {
        var (folder, name) = release switch
        {
            "16" or "17" => ("vci-initial-net48", "TiaMcp.VciInitial.Net48.Tests.exe"),
            "18" or "19" => ("vci-net48", "TiaMcp.VciExport.Net48.Tests.exe"),
            _ => ("vci-modern-net48", "TiaMcp.VciModern.Net48.Tests.exe")
        };
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, folder, name), "--export-replies " + release) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TestDeadlines.Hang);
        Assert.True(process.ExitCode == 0, await errors);
        return JsonNode.Parse(await output)!;
    }

    [Theory, InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19")]
    public async Task Stored_equal_requires_content_proof_and_preserves_uncertain_writes(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var cases = (await ExportReplies(release))["proof"]!;
        foreach (var (key, scenario) in cases.AsObject())
        {
            if (key is "pull" or "push")
            {
                var result = scenario!["result"]!;
                Assert.Equal(key == "pull" ? 1 : 0, (int?)result["NotVerified"]);
                Assert.Equal(key == "push" ? 1 : 0, (int?)result["Synchronized"]);
                Assert.Equal(1, (int?)result["VerifiedEqual"]);
                Assert.Equal(2, result["Items"]!.AsArray().Count);
                Assert.DoesNotContain(Strings(scenario["nextCalls"]), call => call.StartsWith("Synchronize:"));
                Assert.DoesNotContain(Strings(scenario["calls"]), call => call == "Synchronize:WorkspaceToProject");
                continue;
            }
            var evidence = Tools(scenario!).SynchronizeVersionControlWorkspaceV4(dryRun: false).StructuredContent!;
            var data = evidence["data"]!["evidence"]!;
            var preview = Tools(new JsonObject { ["reply"] = scenario!["preview"]!.DeepClone() })
                .SynchronizeVersionControlWorkspaceV4().StructuredContent!["data"]!;
            Assert.Empty(Strings(scenario["previewEvents"]));
            Assert.True((bool?)scenario["cleaned"]);
            var row = data["items"]!.AsArray().Single(r => (string?)r!["name"] == "Motor")!;
            if (key == "disconnected")
            {
                Assert.True((bool?)evidence["ok"]);
                Assert.Equal(0, (int?)data["failed"]);
                Assert.Equal(1, (int?)data["skippedDisconnected"]);
                Assert.False((bool?)preview["evidence"]!["dataComplete"]);
                Assert.False((bool?)data["dataComplete"]);
                Assert.Contains("Motor.xml", (string?)row["reason"]);
                continue;
            }
            var pending = preview["evidence"]!["items"]!.AsArray().Single(r => (string?)r!["name"] == "Motor")!;
            Assert.Equal("verify-before-export", (string?)pending["outcome"]);
            Assert.EndsWith("Motor.xml", (string?)pending["filePath"]);
            Assert.Equal("Equal", (string?)pending["stateBefore"]);
            if (key is not ("missing" or "oversize")) Assert.Matches("^[a-f0-9]{64}$", (string)pending["workspaceFileSha256"]!);
            Assert.Equal(key == "compare-aborted" ? 2 : 1, (int?)preview["evidence"]!["wouldVerify"]);
            if (key == "none")
            {
                var changed = scenario["preview"]!.DeepClone();
                changed["Meta"]!["items"]![0]!["workspaceFileSha256"] = new string('f', 64);
                var other = Tools(new JsonObject { ["reply"] = changed }).SynchronizeVersionControlWorkspaceV4().StructuredContent!["data"]!;
                Assert.NotNull(preview["planHash"]);
                Assert.NotEqual((string?)preview["planHash"], (string?)other["planHash"]);
            }
            Assert.Equal(0, (int?)preview["evidence"]!["skippedEqual"]);
            Assert.False((bool?)scenario["status"]!["Meta"]!["objects"]![0]!["verified"]);
            bool proven = key is "none" or "defaults" or "new-match";
            bool wrote = key is "stale" or "external";
            bool uncertain = key is "disposed" or "abort" or "connection" or "nonrecoverable" or "changed-throw";
            Assert.Equal(proven || wrote, (bool?)evidence["ok"]);
            Assert.Equal(proven ? 1 : 0, (int?)data["verifiedEqual"]);
            Assert.Equal(proven ? 1 : 0, (int?)data["skippedEqual"]);
            Assert.Equal(proven || wrote ? 0 : key == "compare-aborted" ? 2 : 1, (int?)data["notVerified"]);
            Assert.Equal(uncertain ? 1 : 0, (int?)data["unconfirmed"]);
            Assert.Equal(proven ? "verified-equal" : wrote ? "synchronized" : "not-verified", (string?)row["outcome"]);
            if (wrote) Assert.True((bool?)row["fileChanged"]);
            if (proven) Assert.Equal("content-match", (string?)row["verification"]);
            if (key is "mixed" or "race")
            {
                Assert.Equal("PARTIAL_FAILURE", (string?)evidence["error"]!["code"]);
                Assert.Equal(1, (int?)data["synchronized"]);
                Assert.Equal(new[] { "export:None", "export:WithDefaults", "sync:First" }, Strings(scenario["events"]));
            }
            else if (!proven && !wrote)
            {
                Assert.Equal(uncertain ? "OUTCOME_UNKNOWN" : "NATIVE_OPERATION_FAILED", (string?)evidence["error"]!["code"]);
                Assert.Equal(uncertain, (bool?)data["mayHaveChanged"]);
            }
            if (key == "compare-aborted")
            {
                Assert.Equal(new[] { "export:None" }, Strings(scenario["events"]));
                Assert.All(data["items"]!.AsArray(), item => Assert.Equal("not-verified", (string?)item!["outcome"]));
                Assert.Equal(0, (int?)data["exportsIssued"]);
            }
            if (key == "rc7-inconsistent")
            {
                Assert.Equal("not-verified", (string?)row["outcome"]);
                Assert.Contains("Inconsistent objects: MCP_RC_FB.", (string?)row["nativeMessage"]);
                Assert.Contains("Compile the inconsistent block before export", (string?)row["nativeMessage"]);
                Assert.Contains("retry", (string?)evidence["error"]!["message"]);
                Assert.False((bool?)row["fileChanged"]);
            }
            if (key == "race") Assert.True((bool?)scenario["externalPreserved"]);
            var calls = Strings(scenario["calls"]).Count(c => c.StartsWith("Synchronize:"));
            Assert.Equal(key is "none" or "defaults" or "missing" or "oversize" or "compare-aborted" or "race" ? 0 : 1, calls);
            if (key == "none") Assert.Equal(new[] { "export:None" }, Strings(scenario["events"]));
            if (key == "defaults") Assert.Equal(new[] { "export:None", "export:WithDefaults" }, Strings(scenario["events"]));
            if (key == "no-write") Assert.Equal(4, Strings(scenario["events"]).Count(e => e.StartsWith("export:")));
        }
    }

    [Fact]
    public async Task Legacy_proofs_read_final_content_and_isolate_a_dead_proxy()
    {
        using var selected = Host.HardwareContract.UseRelease("17");
        var cases = (await ExportReplies("17"))["sequences"]!;
        Assert.Equal(1, (int?)cases["import"]!["NotVerified"]);
        Assert.Equal(0, (int?)cases["import"]!["VerifiedEqual"]);
        var disposed = cases["disposed"]!["Meta"]!;
        Assert.Equal(1, (int?)disposed["verifiedEqual"]);
        Assert.Equal(1, (int?)disposed["notVerified"]);
        Assert.Equal(0, (int?)disposed["exportsIssued"]);
        foreach (var (scenario, verification) in new[] { ("wrong-write", "no-match"), ("invalid-output", "not-comparable"), ("final-proxy", "not-comparable") })
        {
            var written = cases[scenario]!["Meta"]!;
            Assert.Equal(1, (int?)written["synchronized"]);
            Assert.Equal(0, (int?)written["notVerified"]);
            Assert.Equal(0, (int?)written["unconfirmed"]);
            var row = Assert.Single(written["items"]!.AsArray(), item => (string?)item!["name"] == "Motor");
            Assert.Equal("synchronized", (string?)row!["outcome"]);
            Assert.Equal(verification, (string?)row["verification"]);
            Assert.True((bool?)row["fileChanged"]);
        }
        foreach (var scenario in new[] { "changed-again", "missing-output" })
        {
            var unstable = cases[scenario]!["Meta"]!;
            Assert.Equal(0, (int?)unstable["synchronized"]);
            Assert.Equal(1, (int?)unstable["unconfirmed"]);
            Assert.Equal(1, (int?)unstable["notVerified"]);
        }
        Assert.Contains("Original abort", (string?)cases["cleanup"]!["Meta"]!["nativeMessage"]);
        var sync = Tools(new JsonObject { ["reply"] = cases["inconsistent"]!.DeepClone() }).SynchronizeVersionControlWorkspaceV4(dryRun: false);
        var line = Assert.Single(Strings(sync.StructuredContent!["data"]!["items"]), item => item.Contains(" | FAILED:"));
        Assert.Contains("Motor is not consistent", line);
        Assert.DoesNotContain("fixture-password", line);
        Assert.Contains("Unconfirmed write", (string?)cases["studio-unknown"]);
        Assert.Contains("not-attempted (1): Next", (string?)cases["studio-unknown"]);
        var pending = cases["studio-pending"]!;
        Assert.Equal(1, (int?)pending["NotAttempted"]);
        Assert.Equal(0, (int?)pending["Failed"]);
        Assert.Equal(0, (int?)pending["UnverifiedEqual"]);
        Assert.False((bool?)pending["DataComplete"]);
        var aborted = cases["studio-final-abort"]!;
        Assert.True((bool?)aborted["ComparisonInterrupted"]);
        Assert.False((bool?)aborted["DataComplete"]);
        var localDisposal = cases["studio-final-disposed"]!;
        Assert.False((bool?)localDisposal["ComparisonInterrupted"]);
        Assert.Equal(new[] { "Motor:not-verified", "Next:synchronized" },
            localDisposal["Items"]!.AsArray().Select(row => (string?)row!["Name"] + ":" + (string?)row["Outcome"]));
        var partial = cases["studio-rows"]!;
        Assert.Equal(0, (int?)partial["NotVerified"]);
        Assert.False((bool?)partial["DataComplete"]);
        Assert.Equal(1, (int?)partial["Synchronized"]);
        Assert.Equal(1, (int?)partial["Failed"]);
        Assert.Equal(1, (int?)partial["NotAttempted"]);
        Assert.Equal(new[] { "Done:synchronized", "Motor:unknown", "Next:not-attempted" },
            partial["Items"]!.AsArray().Select(row => (string?)row!["Name"] + ":" + (string?)row["Outcome"]));
        var stopped = cases["stop"]!;
        Assert.Equal(1, (int?)stopped["Meta"]!["notAttempted"]);
        Assert.Contains("Next | not-attempted", Strings(stopped["Items"]));
    }

    [Theory, InlineData("16", 2), InlineData("17", 2), InlineData("18", 1), InlineData("19", 1), InlineData("20", 1), InlineData("21", 1)]
    public async Task Connect_distinguishes_a_reuse_from_an_export(string release, int expectedExports)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var cases = await ExportReplies(release);
        var body = Tools(cases["mixed-reuse"]!).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!;
        Assert.True((bool?)body["ok"], body.ToJsonString());
        var evidence = body["data"]!["evidence"]!;
        Assert.Equal(2, (int?)evidence["mapped"]);
        Assert.Equal(expectedExports, (int?)evidence["exportsIssued"]);
        Assert.Equal(1, (int?)evidence["reused"]);
        Assert.Contains("MCP_RC_FC | mapped | reused existing file", Assert.Single(
            Strings(body["data"]!["items"]).Where(line => line.Contains("reused existing file"))));

        var reuse = Tools(cases["reuse"]!).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!;
        Assert.True((bool?)reuse["ok"], reuse.ToJsonString());
        var reuseEvidence = reuse["data"]!["evidence"]!;
        Assert.Equal(0, (int?)reuseEvidence["exportsIssued"]);
        Assert.Equal(1, (int?)reuseEvidence["reused"]);
    }

    [Theory, InlineData("18"), InlineData("20")]
    public async Task Status_summary_names_disconnected_mappings(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var cases = await ExportReplies(release);
        var scenario = release == "20" ? cases["disconnected-status"]!
            : new JsonObject { ["reply"] = cases["proof"]!["disconnected"]!["status"]!.DeepClone() };
        var status = Tools(scenario).GetVersionControlStatusV4().StructuredContent!;
        Assert.Contains("1 disconnected mapping(s) skipped: Motor.", (string?)status["data"]!["summary"]);
    }

    [Fact]
    public async Task Sync_summary_names_the_blocks_for_each_recovery_action()
    {
        using var selected = Host.HardwareContract.UseRelease("19");
        var proofs = (await ExportReplies("19"))["proof"]!;
        var inconsistent = proofs["rc7-inconsistent"]!;
        var reply = inconsistent["reply"]!;
        var alone = Tools(inconsistent).SynchronizeVersionControlWorkspaceV4(dryRun: false).StructuredContent!;
        foreach (var summary in new[] { (string)reply["Message"]!, (string)alone["data"]!["summary"]! })
        {
            Assert.Contains("Recompile the block(s) Motor and synchronize again.", summary);
            Assert.DoesNotContain("remove the stale workspace file", summary);
        }
        var mixed = new JsonObject { ["reply"] = inconsistent["mixedRecovery"]!.DeepClone() };
        var body = Tools(mixed).SynchronizeVersionControlWorkspaceV4(dryRun: false).StructuredContent!;
        var advice = (string)body["data"]!["summary"]!;
        Assert.Contains("Recompile the block(s) Motor and synchronize again.", advice);
        Assert.Contains("Not verified: Other. Export the block", advice);
        Assert.Contains("remove the stale workspace file", advice);
        Assert.DoesNotContain("notVerifiedAdvice", body.ToJsonString());
        var unavailable = Tools(proofs["export-refused"]!).SynchronizeVersionControlWorkspaceV4(dryRun: false).StructuredContent!;
        Assert.DoesNotContain("remove the stale workspace file", (string?)unavailable["data"]!["summary"]);
    }

    [Fact]
    public async Task V20_disposed_synchronize_reacquires_and_attempts_the_second_target()
    {
        var scenario = (await ExportReplies("20"))["seedDisposed"]!;
        var evidence = scenario["reply"]!["Meta"]!;
        Assert.Equal(1, (int?)evidence["unconfirmed"]);
        Assert.Equal(2, (int?)evidence["exportsIssued"]);
        Assert.Equal(1, (int?)evidence["synchronized"]);
        Assert.Equal(new[] { "GetStatus", "Synchronize:ProjectToWorkspace" }, Strings(scenario["states"]![1]));
    }

    [Theory, InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Connect_preserves_other_content_and_names_duplicate_mappings(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var cases = await ExportReplies(release);
        const string file = "MCP_RC_PLC_MCP_RC_FC5.xml";
        var conflicts = new List<string> { "rc7-comment", "rc7-member", "rc7-after-preview" };
        if (release is "20" or "21") conflicts.Add("rc7-sidecar");
        foreach (string key in conflicts)
        {
            var scenario = cases[key]!;
            string target = key switch { "rc7-sidecar" => Path.ChangeExtension(file, ".s7res"), "rc7-member" => "MCP_RC_PLC_MCP_RC_DB5.xml", _ => file };
            if (key == "rc7-after-preview") Assert.Contains(Strings(scenario["preview"]!["Items"]), line => line.Contains("would map"));
            else
            {
                Assert.Equal(1, (int?)scenario["preview"]!["Meta"]!["wouldRefuse"]);
                Assert.Contains(Strings(scenario["preview"]!["Items"]), line => line.Contains("would refuse") && line.Contains(target));
            }
            var body = Tools(scenario).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!;
            Assert.False((bool?)body["ok"]);
            Assert.Equal(0, (int?)body["data"]!["evidence"]!["exportsIssued"]);
            Assert.Contains(target, (string?)scenario["reply"]!["Meta"]!["nativeMessage"]);
            Assert.Contains("back it up", (string?)scenario["reply"]!["Meta"]!["nativeMessage"]);
            Assert.DoesNotContain(Strings(scenario["Calls"]), c => c.StartsWith("Create:") || c.StartsWith("Workspace."));
            Assert.True((bool?)scenario["preserved"]);
        }
        if (release is "18" or "19")
        {
            var raced = cases["rc7-after-create"]!;
            Assert.True((bool?)raced["commentAfterCreate"]);
            Assert.Equal(1, (int?)raced["reply"]!["Meta"]!["mappedAfterFailure"]);
            Assert.Contains(file, (string?)raced["reply"]!["Meta"]!["nativeMessage"]);
        }
        var reconnect = cases["rc7-reconnect"]!;
        Assert.Equal(1, (int?)reconnect["reply"]!["Meta"]!["mapped"]);
        Assert.Contains(Strings(reconnect["preview"]!["Items"]), line => line.Contains("would reuse"));
        Assert.Contains(Strings(reconnect["preview"]!["Items"]), line => line.Contains("would create a duplicate mapping") && line.Contains(file));
        Assert.True((bool?)reconnect["preserved"]);
        var mapped = Tools(reconnect).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!;
        var status = Tools(new JsonObject { ["reply"] = reconnect["status"]!.DeepClone() }).GetVersionControlStatusV4().StructuredContent!;
        foreach (var body in new[] { mapped, status })
            Assert.Contains(Strings(body["data"]!["items"]),
                line => line.Contains("duplicate mapping") && line.Contains(file) && line.Contains("mappings=2"));
    }

    private static string[] Strings(JsonNode? node) => node!.AsArray().Select(item => (string)item!).ToArray();

    [Theory, InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task First_export_follows_the_native_workspace_capability(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var replies = await ExportReplies(release);
        const string stem = "MCP_RC_PLC_MCP_RC_FC";
        const string file = stem + ".xml";
        foreach (string state in new[] { "equal", "unknown", "unequal" })
        {
            var scenario = replies[state]!;
            Assert.Empty(Strings(scenario["before"]));
            var body = Tools(scenario).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!;
            var evidence = body["data"]!["evidence"]!;
            if (release is "20" or "21")
            {
                Assert.Equal(new[] { "Workspace.ExportObject:" + stem + ":xml" }, Strings(scenario["Calls"]));
                Assert.True((bool?)body["ok"]);
                Assert.Null(evidence["filesWritten"]);
            }
            else if (release is "18" or "19")
            {
                Assert.Equal(new[] { "Create:" + file }, Strings(scenario["Calls"]));
                Assert.Equal(new[] { "Synchronize:ProjectToWorkspace" }, Strings(scenario["states"]![0]));
                Assert.Equal(1, (int?)evidence["mappedAfterFailure"]);
            }
            else
            {
                Assert.True((bool?)body["ok"]);
                Assert.Equal(1, (int?)evidence["mapped"]);
                Assert.Equal(new[] { "Export:" + file + ":None", "Create:" + file }, Strings(scenario["Calls"]));
                var expected = new List<string> { "UpdateStatus", "GetStatus" };
                if (state != "equal") expected.Add("Synchronize:ProjectToWorkspace");
                Assert.Equal(expected, Strings(scenario["states"]![0]));
                Assert.Contains("<Name>MCP_RC_FC</Name>", (string?)scenario["contents"]);
            }
        }
    }

    [Theory, InlineData("16"), InlineData("17")]
    public async Task Existing_targets_and_each_native_stage_have_definite_object_evidence(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var replies = await ExportReplies(release);
        var collision = replies["collision"]!;
        JsonNode Preview(string key) => Tools(new JsonObject { ["reply"] = collision[key]!.DeepClone() })
            .ConnectProjectToWorkspaceV4().StructuredContent!["data"]!;
        var absent = Preview("absent");
        var occupied = Preview("preview");
        Assert.False((bool?)absent["evidence"]!["plannedFiles"]![0]!["fileExists"]);
        Assert.True((bool?)occupied["evidence"]!["plannedFiles"]![0]!["fileExists"]);
        Assert.Matches("^[0-9a-f]{64}$", (string)absent["planHash"]!);
        Assert.NotEqual((string?)absent["planHash"], (string?)occupied["planHash"]);
        Assert.Empty(Strings(collision["Calls"]));
        Assert.Equal("existing contents", (string?)collision["contents"]);
        foreach (var key in new[] { "collision", "exportRefused", "createRefused", "syncRefused" })
        {
            var scenario = replies[key]!;
            var body = Tools(scenario).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!;
            var evidence = body["data"]!["evidence"]!;
            Assert.Equal(0, (int?)evidence["mapped"]);
            Assert.Equal(1, (int?)evidence["failed"]);
            Assert.Equal(0, (int?)evidence["unconfirmed"]);
            Assert.False((bool?)evidence["writeOutcomeUnknown"]);
            Assert.Equal(key == "collision" ? 0 : key == "exportRefused" ? 1 : 2, (int?)evidence["exportsIssued"]);
            bool written = key is "createRefused" or "syncRefused";
            Assert.Equal(written, (bool?)evidence["mayHaveChanged"]);
            Assert.Equal(written ? "PARTIAL_FAILURE" : "NATIVE_OPERATION_FAILED", (string?)body["error"]!["code"]);
            if (written)
            {
                Assert.Equal(1, (int?)evidence["filesWritten"]);
                Assert.Equal(key == "syncRefused" ? 1 : 0, (int?)evidence["mappedAfterFailure"]);
                Assert.Contains(Strings(body["data"]!["items"]), line => line.Contains("file written: MCP_RC_PLC_MCP_RC_FC.xml"));
                Assert.Contains(Strings(body["data"]!["items"]), line => line.Contains(key == "syncRefused" ? "mapping present" : "mapping absent"));
            }
            if (key == "exportRefused")
            {
                Assert.Null(scenario["contents"]);
                Assert.Single(Strings(scenario["Calls"]));
                Assert.Contains("object(s) is not consistent", (string?)evidence["nativeMessage"]);
            }
        }
    }

    [Theory, InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Sync_probes_are_bound_to_their_mapping_and_repair_preserves_existing_files(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var scenario = (await ExportReplies(release))["repair"]!;
        var preview = Tools(new JsonObject { ["reply"] = scenario["preview"]!.DeepClone() })
            .SynchronizeVersionControlWorkspaceV4(changedOnly: false).StructuredContent!["data"]!;
        var body = Tools(scenario).SynchronizeVersionControlWorkspaceV4(dryRun: false, changedOnly: false).StructuredContent!;
        foreach (var data in new[] { preview, body["data"]! })
        {
            Assert.DoesNotContain("fixture-password", data.ToJsonString());
            var probes = data["evidence"]!["statusProbe"]!["objects"]!.AsArray().ToDictionary(row => (string)row!["name"]!);
            if (release is "20" or "21")
            {
                Assert.Contains("First status unavailable", (string?)probes["Motor"]!["nativeMessage"]);
                Assert.Contains("Second status unavailable", (string?)probes["Speed"]!["nativeMessage"]);
                Assert.Null(data["evidence"]!["skippedDisconnected"]);
                continue;
            }
            Assert.Contains("The object is no longer available", (string?)probes["MCP_RC_PLC_MCP_RC_A"]!["nativeMessage"]);
            string deleted = (string)probes["MCP_RC_PLC_MCP_RC_B"]!["nativeMessage"]!;
            Assert.Contains("The file is deleted", deleted);
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(deleted), 1, 4096);
            Assert.Equal(1, (int?)data["evidence"]!["skippedDisconnected"]);
            Assert.Contains(Strings(data["items"]), line => line.Contains("MCP_RC_PLC_MCP_RC_A | mapping no longer connected to an existing object"));
        }
        var evidence = body["data"]!["evidence"]!;
        Assert.False((bool?)evidence["writeOutcomeUnknown"]);
        if (release is "20" or "21")
        {
            Assert.True((bool?)body["ok"]);
            Assert.Equal(new[] { "GetStatus", "GetStatus", "Synchronize:ProjectToWorkspace" }, Strings(scenario["states"]![0]));
            return;
        }
        Assert.Empty(Strings(scenario["previewCalls"]));
        Assert.Equal(Enumerable.Repeat("UpdateStatus", 4), Strings(scenario["states"]![0]));
        Assert.Equal(1, (int?)evidence["unverifiedEqual"]);
        Assert.False((bool?)scenario["storedFileExists"]);
        Assert.Equal(Enumerable.Repeat(new[] { "UpdateStatus", "GetStatus" }, 4).SelectMany(calls => calls), Strings(scenario["states"]![3]));
        var changed = Tools(new JsonObject { ["reply"] = scenario["changedPreview"]!.DeepClone() })
            .SynchronizeVersionControlWorkspaceV4().StructuredContent!["data"]!["evidence"]!;
        Assert.Equal(release is "16" or "17" ? 2 : 1, (int?)changed["wouldSynchronize"]);
        Assert.Equal("existing contents", (string?)scenario["original"]);
        Assert.Equal("PARTIAL_FAILURE", (string?)body["error"]!["code"]);
        if (release is "16" or "17")
        {
            Assert.Equal(new[] { "Export:MCP_RC_PLC_MCP_RC_B.xml:None" }, Strings(scenario["Calls"]));
            Assert.True((bool?)scenario["repaired"]);
            Assert.Equal(1, (int?)evidence["filesWritten"]);
            Assert.Equal(2, (int?)evidence["synchronized"]);
            Assert.Equal(1, (int?)evidence["failed"]);
            var files = preview["evidence"]!["plannedFiles"]!.AsArray();
            Assert.EndsWith(@"\Types\MCP_RC_PLC_MCP_RC_B.xml", (string?)files[0]!["filePath"]);
            Assert.False((bool?)files[0]!["fileExists"]);
            Assert.True((bool?)files[1]!["fileExists"]);
            var present = Tools(new JsonObject { ["reply"] = scenario["presentPreview"]!.DeepClone() })
                .SynchronizeVersionControlWorkspaceV4(changedOnly: false).StructuredContent!["data"]!;
            Assert.True((bool?)present["evidence"]!["plannedFiles"]![0]!["fileExists"]);
            Assert.NotEqual((string?)preview["planHash"], (string?)present["planHash"]);
        }
        else Assert.Empty(Strings(scenario["Calls"]));
    }

    [Theory, InlineData("16"), InlineData("17")]
    public async Task Initial_mapping_reuses_matching_files_and_preserves_uncertain_exports(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var cases = await ExportReplies(release);
        foreach (string key in new[] { "invalidPaths", "unreadableRoot" })
        {
            var paths = cases[key]!;
            Assert.Equal(3, (int?)paths["preview"]!["Meta"]!["wouldSynchronize"]);
            Assert.Equal(3, (int?)paths["reply"]!["Meta"]!["synchronized"]);
            Assert.Equal(3, (int?)paths["unknown"]!["Meta"]!["skippedUnknown"]);
            Assert.Equal(new[] { "Legacy.txt", @"MissingFolder\Table.xml", "Next.xml" }, Strings(paths["events"]));
        }
        var reuse = cases["reuse"]!;
        Assert.True((bool?)Tools(reuse).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!["ok"]);
        Assert.Equal(new[] { "Export:object.xml:None", "Create:MCP_RC_PLC_MCP_RC_FC.xml" }, Strings(reuse["Calls"]));
        Assert.Contains(Strings(reuse["preview"]!["Items"]), line => line.Contains("will be compared"));
        foreach (string key in new[] { "reuse", "differentXml" })
        {
            var preview = Tools(new JsonObject { ["reply"] = cases[key]!["preview"]!.DeepClone() }).ConnectProjectToWorkspaceV4().StructuredContent!;
            var plan = preview["data"]!["evidence"]!;
            Assert.Equal(1, (int?)plan["willCompare"]);
            Assert.Equal(0, (int?)plan["mapped"]);
            Assert.Null(plan["wouldRefuse"]);
        }
        Assert.Contains(Strings(cases["sameName"]!["preview"]!["Items"]), line => line.Contains("duplicate planned target"));
        var missing = Tools(cases["rc7-missing-workspace"]!).ConnectProjectToWorkspaceV4().StructuredContent!;
        Assert.False((bool?)missing["ok"]);
        Assert.Contains("workspace folder is missing", (string?)missing["error"]!["message"]);
        Assert.DoesNotContain("MCP_RC_Ws", (string?)missing["error"]!["message"]);
        Assert.Contains(Strings(cases["rc7-member"]!["preview"]!["Items"]), line => line.Contains("would create a duplicate mapping"));
        var stale = Tools(cases["rc7-member"]!).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!;
        Assert.Equal("PARTIAL_FAILURE", (string?)stale["error"]!["code"]);
        Assert.StartsWith("Some objects were mapped to the workspace.", (string?)stale["error"]!["message"]);
        string remedy = (string)stale["error"]!["message"]!;
        Assert.Contains("MCP_RC_PLC_MCP_RC_DB5.xml", remedy);
        Assert.Contains("disconnected mapping of the same object", remedy);
        Assert.Contains("back it up and remove it", remedy);
        Assert.Contains("content differs", remedy);
        Assert.True((bool?)cases["rc7-member"]!["preserved"]);
        var different = cases["differentXml"]!;
        Assert.False((bool?)Tools(different).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!["ok"]);
        Assert.Contains("<Name>Other</Name>", (string?)different["contents"]);
        Assert.Equal(new[] { "Export:object.xml:None", "Export:defaults.xml:WithDefaults" }, Strings(different["Calls"]));
        Assert.Equal(0, (int?)different["reply"]!["Meta"]!["exportsIssued"]);
        var afterRead = cases["newStatusRefused"]!;
        Assert.True((bool?)Tools(afterRead).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!["ok"]);
        Assert.Equal(new[] { "UpdateStatus", "GetStatus", "Synchronize:ProjectToWorkspace" }, Strings(afterRead["states"]![0]));
        foreach (string key in new[] { "seedDisposed", "seedAbort", "seedConnection", "seedNonrecoverable" })
        {
            var scenario = cases[key]!;
            Assert.True((bool?)scenario["reply"]!["OutcomeUnknown"]);
            Assert.Equal(1, (int?)scenario["reply"]!["Meta"]!["unconfirmed"]);
            Assert.Equal(new[] { "Export:MCP_RC_PLC_MCP_RC_FC.xml:None" }, Strings(scenario["Calls"]));
        }
    }

    [Theory, InlineData("18"), InlineData("19")]
    public async Task Legacy_native_sync_can_export_without_an_initial_file(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var scenario = (await ExportReplies(release))["nativeSuccess"]!;
        var body = Tools(scenario).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!;
        Assert.True((bool?)body["ok"]);
        Assert.Equal(new[] { "Create:MCP_RC_PLC_MCP_RC_FC.xml" }, Strings(scenario["Calls"]));
        Assert.Equal(new[] { "Synchronize:ProjectToWorkspace" }, Strings(scenario["states"]![0]));
        Assert.Equal(1, (int?)body["data"]!["evidence"]!["mapped"]);
        Assert.Contains("<Name>MCP_RC_FC</Name>", (string?)scenario["contents"]);
    }

    [Fact]
    public async Task A_repaired_file_survives_a_proven_native_sync_refusal()
    {
        using var selected = Host.HardwareContract.UseRelease("17");
        var scenario = (await ExportReplies("17"))["repairRefused"]!;
        var body = Tools(scenario).SynchronizeVersionControlWorkspaceV4(dryRun: false).StructuredContent!;
        var evidence = body["data"]!["evidence"]!;
        Assert.Equal("PARTIAL_FAILURE", (string?)body["error"]!["code"]);
        Assert.Equal(0, (int?)evidence["unconfirmed"]);
        Assert.Equal(1, (int?)evidence["filesWritten"]);
        Assert.Equal("present", (string?)evidence["writtenFilesAfterFailure"]![0]!["mappingState"]);
        Assert.Equal("MCP_RC_PLC_MCP_RC_FC.xml", (string?)evidence["writtenFilesAfterFailure"]![0]!["fileName"]);
        Assert.Contains("<Name>MCP_RC_FC</Name>", (string?)scenario["contents"]);
        Assert.Contains("Synchronize:ProjectToWorkspace", Strings(scenario["states"]));
    }

    private sealed class WorkerFailure(JsonNode reply) : Exception("Worker outcome unknown"), TiaMcp.Adapters.Contracts.IWorkerFailureEvidence
    {
        public string EvidenceJson => reply.ToJsonString();
    }

    [Theory]
    [InlineData("container-format-refusal", 1, 0)]
    [InlineData("leaf-format-refusal", 0, 1)]
    [InlineData("ungrouped-format-refusal", 0, 1)]
    public async Task Connect_walk_observes_container_and_leaf_format_refusals(string scenario, int mapped, int failures)
    {
        var fixture = (await ExportReplies("21"))[scenario]!;
        Assert.Equal(scenario == "ungrouped-format-refusal" ? new[] { "MCP_RC_PLC" } : new[] { "MCP_RC_PLC", "MCP_RC_FC" },
            Strings(fixture["queried"]));
        Assert.Equal(mapped, (int?)fixture["reply"]!["Meta"]!["mapped"]);
        Assert.Equal(failures, (int?)fixture["reply"]!["Meta"]!["queryRefusals"]);
    }

    [Fact]
    public async Task An_unreadable_mapping_after_create_retains_the_written_file_in_the_worker_error()
    {
        using var selected = Host.HardwareContract.UseRelease("17");
        var fixture = (await ExportReplies("17"))["lookupRefused"]!;
        var reply = fixture["reply"]!;
        Assert.True((bool?)reply["OutcomeUnknown"]);
        Assert.Equal(1, (int?)reply["Meta"]!["unconfirmed"]);
        Assert.Contains("<Name>MCP_RC_FC</Name>", (string?)fixture["contents"]);
        var tools = new Host.VersionControlTools(new Services.VersionControlPortService((_, _) => throw new WorkerFailure(reply), () => "Line.ap17"));
        var body = tools.ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]!["code"]);
        var evidence = body["error"]!["details"]!["evidence"]!;
        Assert.Equal(1, (int?)evidence["filesWritten"]);
        Assert.Equal("MCP_RC_PLC_MCP_RC_FC.xml", (string?)evidence["writtenFilesAfterFailure"]![0]!["fileName"]);
        Assert.Equal(1, (int?)evidence["notAttempted"]);
        Assert.Equal("walk-nodes", (string?)evidence["notAttemptedKind"]);
        Assert.Equal("Project/PendingDevice", Assert.Single(Strings(evidence["notAttemptedNames"])));
        Assert.Equal("unknown", (string?)evidence["writtenFilesAfterFailure"]![0]!["mappingState"]);
        Assert.Equal(1, (int?)evidence["writtenFilesAfterFailureTotal"]);
    }

    [Theory, InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Studio_compares_existing_files_before_creating_a_mapping(string release)
    {
        var cases = await ExportReplies(release);
        foreach (var key in new[] { "studio-conflict", "studio-reuse" })
        {
            var row = cases[key]!;
            bool reuse = key == "studio-reuse";
            Assert.True((bool?)row["previewPreserved"]);
            Assert.True((bool?)row["preserved"]);
            Assert.All(Strings(row["previewCalls"]), call => Assert.StartsWith("Export:", call));
            Assert.Equal(reuse ? 0 : 1, (int?)row["preview"]!["Failed"]);
            Assert.Equal(reuse ? 0 : 1, (int?)row["apply"]!["Failed"]);
            Assert.Equal(reuse ? 1 : 0, (int?)row["apply"]!["Mapped"]);
            if (reuse) Assert.Contains(row["apply"]!["Items"]!.AsArray(), item => (string?)item!["Outcome"] == "mapped | reused existing file");
            var writes = Strings(row["Calls"]).Where(call => !call.StartsWith("Export:", StringComparison.Ordinal));
            string stem = (string)row["stem"]!;
            string connect = release is "18" or "19" ? "Create:" + stem + ".xml" : "Workspace.ConnectObject:" + stem + ":xml";
            Assert.Equal(reuse ? new[] { connect } : Array.Empty<string>(), writes);
            Assert.DoesNotContain(row["states"]!.AsArray().SelectMany(Strings), call => call.StartsWith("Synchronize:"));
        }
    }

    [Theory, InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Connect_reports_the_comparison_export_refusal(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var row = (await ExportReplies(release))["rc7-inconsistent"]!;
        var body = Tools(row).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!;
        string? message = (string?)body["error"]!["message"];
        Assert.Contains("object(s) is not consistent", message);
        Assert.Contains("Recompile the block", message);
        Assert.Equal(0, (int?)body["data"]!["evidence"]!["exportsIssued"]);
        Assert.All(Strings(row["Calls"]), call => Assert.StartsWith("Export:", call));
        Assert.True((bool?)row["preserved"]);
    }

    [Theory, InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Successful_fallback_export_reports_the_workspace_content_mismatch(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var row = (await ExportReplies(release))["rc7-fallback-differs"]!;
        var body = Tools(row).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!;
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]!["code"]);
        var reason = (string)body["error"]!["message"]!;
        Assert.Contains("content differs", reason);
        Assert.DoesNotContain("Export without defaults refused", reason);
        Assert.Equal(new[] { "Export:object.xml:None", "Export:defaults.xml:WithDefaults" }, Strings(row["Calls"]));
        Assert.Equal(0, (int?)body["data"]!["evidence"]!["exportsIssued"]);
        Assert.True((bool?)row["preserved"]);
    }

    [Theory, InlineData("16"), InlineData("17")]
    public async Task Connect_reports_a_bounded_remedy_for_each_disconnected_file(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var cases = await ExportReplies(release);
        var body = Tools(cases["multipleDisconnected"]!).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!;
        Assert.Equal("PARTIAL_FAILURE", (string?)body["error"]!["code"]);
        var message = (string)body["error"]!["message"]!;
        foreach (var id in new[] { "00", "01" })
        {
            var remedy = "Target file already exists: MCP_RC_PLC_MCP_RC_FC_" + id + ".xml. After an overwriting import, "
                + "the old file belongs to the disconnected mapping of the same object. Compare the file, back it up and remove it from the workspace, "
                + "then connect again.";
            Assert.Contains(remedy, message);
            Assert.Equal(message.IndexOf(remedy, StringComparison.Ordinal), message.LastIndexOf(remedy, StringComparison.Ordinal));
        }

        var uncertain = Tools(cases["uncertainDisconnected"]!).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)uncertain["error"]!["code"]);
        string uncertainMessage = (string)uncertain["error"]!["message"]!;
        Assert.Contains("Inspect the target", uncertainMessage);
        Assert.Contains("MCP_RC_PLC_MCP_RC_FC_00.xml", uncertainMessage);
        Assert.Contains("MCP_RC_PLC_MCP_RC_FC_01.xml", uncertainMessage);

        var many = Tools(cases["manyDisconnected"]!).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!;
        Assert.Equal(32, (int?)many["data"]!["evidence"]!["failed"]);
        Assert.Equal(32, Strings(many["data"]!["items"]).Count(line => line.Contains("disconnected mapping of the same object")));
        var bounded = (string)many["error"]!["message"]!;
        Assert.Contains("then connect again.", bounded);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(bounded), 1, 4096);
    }

    [Theory, InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Connect_catch_all_sanitizes_inner_native_messages(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var cases = await ExportReplies(release);
        foreach (string key in new[] { "catch-direct", "catch-prior" })
        {
            var row = cases[key]!;
            var body = Tools(row).ConnectProjectToWorkspaceV4(dryRun: false).StructuredContent!;
            Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]!["code"]);
            Assert.Equal(key == "catch-prior" ? 1 : 0, (int?)row["reply"]!["Meta"]!["queryRefusals"]);
            foreach (var native in new[] { row["reply"]!["Meta"]!["nativeMessage"], body["error"]!["details"]!["nativeMessage"], body["error"]!["message"] })
            {
                var text = (string)native!;
                Assert.Contains("Catalog lookup refused", text);
                Assert.DoesNotContain("PrivateFixture", text);
                Assert.DoesNotContain("Panel.xml", text);
                Assert.DoesNotContain("fixture-credential", text);
            }
        }
    }

    [Theory, InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Connect_refuses_a_missing_or_file_workspace_root_before_walking(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var cases = await ExportReplies(release);
        foreach (var key in new[] { "rc7-missing-workspace", "file-workspace" })
        {
            var scenario = cases[key]!;
            foreach (var dryRun in new[] { true, false })
            {
                var row = scenario[dryRun ? "reply" : "apply"]!;
                var body = Tools(new JsonObject { ["reply"] = row.DeepClone() }).ConnectProjectToWorkspaceV4(dryRun: dryRun).StructuredContent!;
                Assert.Equal("NOT_FOUND", (string?)body["error"]!["code"]);
                if (key == "file-workspace")
                    Assert.Contains("A file occupies the workspace path. Select an existing workspace folder.", (string?)body["error"]!["message"]);
                else
                    Assert.Contains("The workspace folder is missing. Restore it or select an existing workspace.", (string?)body["error"]!["message"]);
                Assert.DoesNotContain("MCP_RC_Ws", (string?)body["error"]!["message"]);
                Assert.Equal(0, (int?)row["Meta"]!["exportsIssued"]);
                Assert.Empty(Strings(row["Items"]));
            }
        }
        Assert.Equal("occupied root", (string?)cases["file-workspace"]!["contents"]);
        Assert.Empty(Strings(cases["file-workspace"]!["calls"]));
    }

    [Fact]
    public async Task Studio_uses_its_hashed_mapping_name_on_first_export_and_repair()
    {
        var studio = (await ExportReplies("17"))["studio"]!;
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("MCP_RC_PLC\n默认变量表"));
        var file = "MCP_RC_PLC_默认变量表_" + Convert.ToHexStringLower(digest.AsSpan(0, 4)) + ".xml";
        Assert.Empty(Strings(studio["previewWrites"]));
        Assert.Equal(new[] { "Export:" + file + ":None", "Create:" + file }, Strings(studio["firstCalls"]));
        Assert.Equal(new[] { "UpdateStatus", "GetStatus" }, Strings(studio["status"]));
        Assert.Equal(1, (int?)studio["restored"]!["Synchronized"]);
        Assert.Equal(0, (int?)studio["collision"]!["Failed"]);
        Assert.Equal(1, (int?)studio["collision"]!["Mapped"]);
        Assert.Equal(0, (int?)studio["stale"]!["Failed"]);
        Assert.Equal(1, (int?)studio["stale"]!["SkippedDisconnected"]);
        Assert.Contains("mapping no longer connected", (string?)studio["stale"]!["Items"]![0]!["Error"]);
        Assert.Equal(new[] { "UpdateStatus" }, Strings(studio["staleCalls"]));
        Assert.Equal(new[] { "Export:" + file + ":None", "Create:" + file, "Export:" + file + ":None", "Export:object.xml:None", "Create:" + file },
            Strings(studio["Calls"]));
        Assert.Contains("<Name>默认变量表</Name>", (string?)studio["contents"]);
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
