extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4.Inputs;
using TiaOpenness.Shared;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Services = toolpipeline::TiaMcpServer.Siemens.Services;

namespace TiaMcp.FoundationHost.Tests;

[Collection(BundleSelectionCollection.Name)]
public sealed class V151BatchRegressionTests
{
    [Theory, InlineData(true), InlineData(false)]
    public void Existing_table_on_V151_reports_the_entry_access_limit(bool monitoring)
    {
        using var fixture = new PreviewHostFixture("15.1");
        using var session = fixture.Pipeline.EnterSession();
        var data = new Dictionary<string, object?> { ["watchTableName"] = "MCP_RC_Watch" };
        var report = PlcWatchReport.EntryAccessLimit("15.1", data, monitoring);
        Assert.NotNull(report);
        Assert.Null(PlcWatchReport.EntryAccessLimit("17", data, monitoring));
        var service = new Services.PlcOrganisationPortService((_, _) => new JsonObject { ["Report"] = JsonSerializer.SerializeToNode(report) },
            () => true, () => "C:/Project.ap15_1");
        service.LiveRead = (_, _, _, _, _) => throw new InvalidOperationException("Unexpected S7 read.");
        var tools = new Host.PlcWatchTools(service);
        var body = (monitoring ? tools.MonitorWatchTableLiveS7V4("MCP_RC_PLC3", "MCP_RC_Watch", "192.168.0.1")
            : tools.ReadPlcWatchTableCurrentValuesReadOnlyV4("MCP_RC_PLC3", "MCP_RC_Watch")).StructuredContent!;
        Assert.Equal(monitoring, (bool?)body["ok"]);
        Assert.Equal(monitoring ? "partial" : "none", (string?)body["meta"]?["completeness"]);
        Assert.Null(body["data"]?["entryCount"]);
        Assert.DoesNotContain("tableMembers", body.ToJsonString());
        Assert.Contains("V17", (string?)(monitoring ? body["data"]?["summary"] : body["error"]?["message"]));
        if (!monitoring) Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
    }

    [Theory, InlineData("15.1", 15, "V15.1"), InlineData("14sp1", 14, "V14 SP1")]
    public void Doctor_reports_the_full_release_in_both_languages(string release, int major, string display)
    {
        var checks = toolpipeline::TiaMcpServer.Runtime.EnvironmentDoctor.RunHost(release, major, (true, "install", "api.dll", null), [], null);
        var install = checks.Single(row => row.Id == "tia-install");
        var match = checks.Single(row => row.Id == "engine-version");
        Assert.Equal("detected " + display, install.DetailEn);
        Assert.Equal("检测到 " + display, install.DetailZh);
        Assert.Equal($"exe built for {display}, machine has {display}", match.DetailEn);
        Assert.Equal($"该 exe 为 {display} 构建，本机装的是 {display}", match.DetailZh);
    }

    [Fact]
    public void Missing_simulated_instance_identifies_the_argument_and_cause()
    {
        using var fixture = new PreviewHostFixture("15.1");
        using var session = fixture.Pipeline.EnterSession();
        const string reason = "PLCSIM Advanced instance 'MCP_RC_SIM' is not registered.";
        var service = new Services.PlcSimAdvancedPortService((_, _) => new JsonObject {
            ["Ok"] = false, ["Data"] = new JsonObject { ["registeredBefore"] = false, ["instance"] = "MCP_RC_SIM" },
            ["Meta"] = new JsonObject { ["status"] = "NOT_FOUND", ["operationSuccess"] = false, ["parameter"] = "instanceName",
                ["nativeMessage"] = reason } });
        var body = new Host.PlcSimAdvancedTools(service).ManagePlcSimAdvancedInstanceV4("MCP_RC_SIM", "powerOn").StructuredContent!;
        Assert.Equal("NOT_FOUND", (string?)body["error"]?["code"]);
        Assert.Equal("/instanceName", (string?)body["error"]?["details"]?["target"]);
        Assert.Equal(reason, (string?)body["error"]?["message"]);
    }

    [Theory, InlineData(0), InlineData(1)]
    public void Missing_confirmation_points_to_the_item_before_any_preview(int index)
    {
        using var fixture = new PreviewHostFixture("15.1");
        using var session = fixture.Pipeline.EnterSession();
        var group = new JsonObject { ["softwarePath"] = "MCP_RC_PLC3", ["action"] = "createGroup", ["name"] = "MCP_RC_DSrc2", ["dryRun"] = true };
        if (index == 1) group["confirm"] = true;
        ToolCall Call(string name, object args) => new(name, new ToolArguments(JsonSerializer.SerializeToElement(args)));
        var calls = new[] { Call("ManagePlcExternalSources", group), Call("ManagePlcTagDefinition", new { softwarePath = "MCP_RC_PLC3",
            tablePath = "MCP_RC_Tags", name = "MCP_RC_DTag2", kind = "tag", action = "create", dataType = "Bool", addressOrValue = "%M110.1" }) };
        var body = Host.McpServer.PreviewToolBatch(calls, "Fixture").StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal($"/operations/{index}/arguments/confirm", (string?)body["error"]?["details"]?["parameter"]);
        Assert.Null(body["data"]?["previewToken"]);
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Fact]
    public void Export_completeness_tracks_each_returned_slice()
    {
        using var fixture = new PreviewHostFixture("15.1");
        using var session = fixture.Pipeline.EnterSession();
        string id = toolpipeline::TiaMcp.FoundationHost.SessionExportStore.PutAndSlice("fixture", "PLC", "retained", 4).id;
        var tools = new Host.ExportTools();
        foreach (int offset in new[] { 0, 4 })
        {
            var body = tools.GetExport(id, offset, 4).StructuredContent!;
            Assert.Equal(offset == 4, (bool?)body["meta"]?["paging"]?["complete"]);
            Assert.Equal(offset == 4 ? "complete" : "partial", (string?)body["meta"]?["completeness"]);
        }
    }

    [Theory, InlineData("15.1", false, 0), InlineData("17", false, 0), InlineData("18", true, 0),
        InlineData("20", true, 1), InlineData("21", true, 2)]
    public void Compatibility_selects_links_and_cross_references_for_the_release(string release, bool enabled, int count)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var prior = Host.HostToolServices.Override;
        try
        {
            Host.HostToolServices.Override = (operation, _) => operation == "assemblies" ? new JsonArray() : throw new InvalidOperationException(operation);
            var meta = new Host.EngineeringDiagnosticsTools().ReadOpennessCompatibility().Meta!;
            Assert.Equal(enabled, (bool?)meta["nativeCrossReferencesEnabled"]);
            var issues = meta["knownIssues"]!.AsArray();
            Assert.Equal(count, issues.Count);
            Assert.All(issues, link => Assert.Contains("/v" + release, (string?)link));
        }
        finally { Host.HostToolServices.Override = prior; }
    }

    [Theory, InlineData("ApplyToolBatch", true), InlineData("RunReadOnlyToolBatch", true),
        InlineData("ApplyToolBatch", false), InlineData("RunReadOnlyToolBatch", false)]
    public void Completed_batch_has_one_locatable_journal_row(string batch, bool forwarded)
    {
        const string id = "95abad0a20a0438484eab77e6b2225bb";
        var rows = new List<string>();
        Host.InvocationJournal.ConfigureOutput(rows.Add);
        try
        {
            string result = JsonSerializer.Serialize(new { schemaVersion = 4, ok = true, meta = new { requestId = id } });
            using var parent = Host.InvocationJournal.Observe(id, forwarded ? "CallTool" : batch, "host", "15.1", batch == "ApplyToolBatch",
                () => forwarded ? JsonSerializer.Serialize(new { name = batch, arguments = new { } }) : "{}");
            using (var child = Host.InvocationJournal.Observe(id, "ListPlcTags", "host", "15.1", false, () => "{}")) child.Complete(() => result);
            parent.Complete(() => result);
            var finished = rows.Select(row => JsonNode.Parse(row)!).Where(row => (string?)row["phase"] == "RETURNED");
            Assert.Equal(batch, (string?)Assert.Single(finished, row => (string?)row["requestId"] == id)["tool"]);
        }
        finally { Host.InvocationJournal.ConfigureOutput(null); }
        var missing = WorkbenchControlTool.Map(new WorkbenchControlResponse { Status = WorkbenchControlStatus.Refused,
            Refusal = new() { Code = WorkbenchControlError.TargetAmbiguous, Condition = "call-not-found", Target = "call" } },
            "15.1", "ShowWorkbenchCall", id, WorkbenchControlOperation.DisplayCall).StructuredContent!;
        Assert.Equal("NOT_FOUND", (string?)missing["error"]?["code"]);
        Assert.Equal("NOT_FOUND", (string?)missing["data"]?["controlRefusal"]?["code"]);
    }

    [Fact]
    public void Upload_precheck_returns_one_not_found_code_and_the_address_parameter()
    {
        using var fixture = new PreviewHostFixture("15.1");
        using var session = fixture.Pipeline.EnterSession();
        var report = new Host.ResponseMessage { Meta = new JsonObject { ["v4FailureCode"] = "NOT_FOUND", ["status"] = "ReadOrWriteFailed",
            ["mayHaveChanged"] = false, ["operationSuccess"] = false, ["parameter"] = "targetIpAddress", ["knownTargetAddresses"] = "192.168.0.1",
            ["nativeMessage"] = "No such target on the selected interface." } };
        var body = Host.RuntimeToolContract.Map("UploadStationFromPlc", report, true, true).StructuredContent!;
        Assert.Equal("NOT_FOUND", (string?)body["error"]?["code"]);
        Assert.Equal("/targetIpAddress", (string?)body["error"]?["details"]?["target"]);
        Assert.Equal("192.168.0.1", (string?)body["data"]?["knownTargetAddresses"]);
    }

    [Fact]
    public void Callers_with_duplicate_block_names_returns_the_matching_export_files()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "duplicates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string xml = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SimaticMl", "17", "MCP_RC_FC.xml");
            foreach (string name in new[] { "First.xml", "Second.xml" }) File.Copy(xml, Path.Combine(directory, name));
            var tool = new Host.PlcOfflineTools();
            var body = tool.AnalyzePlcReferencesV4(directory, "callers", "MCP_RC_FC").StructuredContent!;
            Assert.Equal("TARGET_AMBIGUOUS", (string?)body["error"]?["code"]);
            Assert.Equal("MCP_RC_FC", (string?)body["error"]?["details"]?["target"]);
            Assert.Equal(new[] { "First.xml", "Second.xml" }, body["error"]!["details"]!["candidates"]!.Deserialize<string[]>());
            Assert.True((bool?)tool.AnalyzePlcReferencesV4(directory, "callers", "First.xml").StructuredContent?["ok"]);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Monitoring_plan_accepts_global_symbols_and_preserves_absolute_address_refusals()
    {
        var tool = new Host.PlcWatchTools(new Services.PlcOrganisationPortService((_, _) => throw new InvalidOperationException("Unexpected dispatch."),
            () => true, () => "C:/Project.ap15_1"));
        var plan = tool.PlanOnlineReadOnlyMonitoringV4("MCP_RC_PLC3", ["MCP_RC_DB.Run", "MCP_RC_TagC", "\"Global tag\""]).StructuredContent!;
        Assert.True((bool?)plan["ok"]);
        Assert.Equal(3, plan["data"]!["acceptedTags"]!.AsArray().Count);
        var refused = tool.PlanOnlineReadOnlyMonitoringV4("MCP_RC_PLC3", ["%M110.1"]).StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)refused["error"]?["code"]);
    }
}
