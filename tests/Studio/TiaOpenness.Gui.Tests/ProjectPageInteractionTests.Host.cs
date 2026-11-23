using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed partial class ProjectPageInteractionTests
{
    [Fact]
    public void Lease_observation_distinguishes_a_live_lock_from_released_and_unknown_processes()
    {
        string root = Path.Combine(McpOperatorFixture.RepositoryRoot, "bin-build");
        var folder = Directory.CreateDirectory(Path.Combine(root, "workbench-lease-" + Guid.NewGuid().ToString("N")));
        var start = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        string file = Path.Combine(folder.FullName, $"2719-{start.UtcTicks}.lease");
        ProjectLeaseState Observe() => McpProjectClient.ObserveLease(folder.FullName, 2719, start.UtcDateTime.ToString("O"));
        try
        {
            Assert.Equal(ProjectLeaseState.Available, Observe());
            Assert.False(File.Exists(file));
            using (File.Open(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) Assert.Equal(ProjectLeaseState.Held, Observe());
            Assert.Equal(ProjectLeaseState.Available, Observe());
            Assert.Equal(ProjectLeaseState.Unknown, McpProjectClient.ObserveLease(folder.FullName, 2719, null));
            Assert.Equal(0, new FileInfo(file).Length);
        }
        finally { File.Delete(file); folder.Delete(); }
    }

    [Fact]
    public void SimaticML_call_sections_and_literal_parameters_keep_their_identity()
    {
        string path = Path.Combine(McpOperatorFixture.RepositoryRoot, "tests", "FoundationHost", "TiaMcp.FoundationHost.Tests", "Fixtures",
            "LadRung", "21", "call-fb.xml");
        string xml = File.ReadAllText(path);
        const string listing = """
            ── 程序段 1  [LAD]
               当 ["Enable"] 时: CALL "FB_User"["DB_User"](Command="Command,Value", Result="ResultValue", State="StateValue", Unspecified=⟨FUNC(A, B)⟩)
            """;
        var actual = Assert.Single(Assert.Single(ProjectLadderNetwork.FromXml(xml)).Rungs);
        Assert.Equal("FB_User", actual.Box);
        Assert.Equal("DB_User", actual.Instance.Trim('"'));
        Assert.Equal("CommandValue", Assert.Single(actual.Inputs).Parameter.Trim('"'));
        Assert.Equal(new[] { "ResultValue", "StateValue" }, actual.Outputs.Select(p => p.Parameter.Trim('"')));
        var networks = ProjectLadderNetwork.FromListing(listing.Split('\n'));
        var rung = Assert.Single(Assert.Single(ProjectLadderNetwork.WithMetadata(networks, [],
            (_, block, pin) => block == actual.Box ? actual.Pins.SingleOrDefault(p => p.Name == pin)?.Section : null)).Rungs);
        Assert.Equal("\"DB_User\"", rung.Instance);
        Assert.Equal("\"Command,Value\"", Assert.Single(rung.Inputs).Parameter);
        Assert.Equal(new[] { "Result", "↔ State" }, rung.Outputs.Select(p => p.Label));
        Assert.Equal("⟨FUNC(A, B)⟩", Assert.Single(rung.Unclassified).Parameter);
    }

    [Fact]
    public void The_visual_network_atlas_button_opens_the_page_for_the_focused_block()
    {
        wpf.Run(() =>
        {
            var model = ProjectPageTestSupport.Model();
            var window = new MainWindow(model, false);
            try
            {
                window.Navigate("Engineering");
                WpfContext.ShowWindow(window);
                var blocks = window.BlocksContent;
                var main = model.Engineering.Blocks.Single(b => b.Name == "Main");
                blocks.LocateControlBlock(main);
                var service = new AtlasProbe();
                string? opened = null;
                blocks.SetAtlasService(service, path => opened = path);
                WpfContext.Drain();
                Assert.True(blocks.ViewLadderAtlas.IsVisible && blocks.ViewLadderAtlas.IsEnabled);
                UnifiedDesktopTests.ClickControl(blocks.ViewLadderAtlas);
                Assert.True(service.Request!.SingleBlock);
                Assert.Equal(main.Path, Assert.Single(service.Request.BlockPaths));
                string html = Path.Combine(McpOperatorFixture.RepositoryRoot, "tests", "FoundationHost", "TiaMcp.Engine.Tests",
                    "Fixtures", "PlcRender", "Parts", "BoxParts.xml.html");
                service.Completion.SetResult(new Services.Stubs.AtlasResult(html, null));
                WpfContext.Drain();
                Assert.Equal(html, opened);
                Assert.True(blocks.Atlas!.Done);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Contacts_and_coils_only_use_unambiguous_global_names_and_addresses()
    {
        var source = ProjectLadderNetwork.FromListing(["── 程序段 1 · Start drive  [LAD]", "Run ( )  ⇐  %I0.0 · /#Start · Shared"]);
        TagInfo Tag(string name, string address) => new() { Name = name, LogicalAddress = address };
        var tags = new[] { Tag("Start", "%I0.0"), Tag("Run", "%Q0.0"), Tag("Shared", "%I0.1"), Tag("Shared", "%I1.1") };
        var network = Assert.Single(ProjectLadderNetwork.WithMetadata(source, tags, (_, _, _) => null));
        Assert.Equal("Network 1", network.NumberLabel);
        Assert.Equal("Start drive", network.Title);
        Assert.Equal("LAD", network.Language);
        var elements = Assert.Single(network.Rungs).Elements;
        Assert.Equal(new[] { "Start", "#Start", "Shared", "Run" }, elements.Select(e => e.Operand));
        Assert.Equal(new[] { "%I0.0", "", "", "%Q0.0" }, elements.Select(e => e.Address));
        Assert.True(elements[^1].IsCoil);
    }

    internal sealed class DocumentSession(Func<string, IReadOnlyDictionary<string, object?>, string> response) : IMcpSessionClient
    {
        internal const string Project = @"D:\Fixture\Review.ap21";
        internal readonly List<(string Tool, IReadOnlyDictionary<string, object?> Args)> Calls = [];
        internal string Release = "21";
        internal bool AdvertiseTools { get; init; } = true;
        internal string? FailureCode;
        internal int Epoch = 1;
        public string SessionKey => "fixture";
        public JsonObject State => new() { ["releaseKey"] = Release,
            ["binding"] = new JsonObject { ["projectFile"] = Project, ["bindingEpoch"] = Epoch, ["processId"] = 4204 } };
        public JsonObject? ToolSchema(string tool)
        {
            if (!AdvertiseTools) return null;
            string[] parameters = tool switch {
                "DownloadPlc" => ["softwarePath", "expectedPlanHash", "expectedProjectFile", "confirmPhysicalTarget"],
                "ListPlcSoftwareUnits" => ["includeContents"], "RenderPlcBlockDocument" => ["blockPath"], "GetUnifiedAlarmCommon" => ["category"],
                "ImportPlcBlockVerified" => ["previewToken"], "ExportPlcBlock" => ["unitName", "stripVolatile", "expectedProjectFile"],
                _ => ["softwarePath"] };
            var properties = new JsonObject();
            foreach (var p in parameters) properties[p] = new JsonObject();
            return new() { ["properties"] = properties };
        }
        public Task<JsonObject> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken token = default)
        {
            Calls.Add((tool, new Dictionary<string, object?>(arguments)));
            if (FailureCode != null)
                return Task.FromResult(new JsonObject { ["schemaVersion"] = 4, ["ok"] = false, ["data"] = null,
                    ["error"] = new JsonObject { ["code"] = FailureCode, ["message"] = "Device response" }, ["meta"] = new JsonObject { ["tool"] = tool } });
            bool apply = arguments.TryGetValue("dryRun", out var dry) && dry is false;
            var envelope = new JsonObject { ["schemaVersion"] = 4, ["ok"] = true, ["data"] = JsonNode.Parse(response(tool, arguments)), ["error"] = null,
                ["meta"] = new JsonObject { ["tool"] = tool, ["requestId"] = "fixture", ["releaseKey"] = Release,
                    ["timestamp"] = "2026-11-20T00:00:00.0000000Z", ["outcome"] = "succeeded", ["execution"] = apply ? "completed" : "read-only",
                    ["completeness"] = "complete", ["behaviorPolicy"] = "not-applicable", ["warnings"] = new JsonArray(), ["paging"] = null } };
            _ = TiaOpenness.Core.EngineToolResult.Read(envelope.ToJsonString());
            return Task.FromResult(envelope);
        }
        public event EventHandler<JsonObject>? StateChanged { add { } remove { } }
        public event EventHandler? Detached { add { } remove { } }
        public Task<IReadOnlyList<OperatorSession>> DiscoverAsync(string releaseKey, CancellationToken token = default) => throw new NotSupportedException();
        public Task AttachAsync(OperatorSession session, CancellationToken token = default) => throw new NotSupportedException();
        public Task CreateAsync(string releaseKey, CancellationToken token = default) => throw new NotSupportedException();
        public Task DetachAsync() => Task.CompletedTask;
        public void CancelPending() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Native_inventories_remain_scoped_paged_and_release_aware()
    {
        var session = new DocumentSession((tool, args) => tool switch {
            "GetProjectTree" => """{"value":"PlcSoftware: PLC [softwarePath=Line/PLC]\nHmiSoftware: Panel [softwarePath=Line/Panel]"}""",
            "ListPlcSoftwareUnits" when Equals(args["offset"], 0) =>
                """{"evidence":{"records":[{"name":"Motion","namespacePreset":"Cell","blockGroup":{"blocks":["Home"]}}],"nextOffset":1}}""",
            "ListPlcSoftwareUnits" => """{"evidence":{"records":[{"name":"Fan","blockGroup":{"blocks":[]}}],"nextOffset":null}}""",
            "ListHmiScreens" => """{"items":["Overview","Setup"]}""",
            "ListHmiTags" => """{"items":["Start","Stop","Stopped"]}""",
            "GetUnifiedAlarmCommon" => """{"evidence":{"expectedCount":17,"records":[{"name":"Warning"}],"nextOffset":1}}""",
            "RenderPlcBlockDocument" => """{"evidence":{"markdown":"# Mixer\n| Section | Name | Datatype |\n|---|---|---|\n| Input | `Start` | Bool |\n"}}""",
            _ => throw new InvalidOperationException(tool) });
        using var client = new McpProjectClient(session);
        var devices = await client.ListDevicesAsync();
        var hmi = Assert.Single(devices, d => d.Category == "Hmi");
        Assert.Equal("Line/Panel", hmi.Id);
        var units = await client.ListUnitsAsync("Line/PLC");
        Assert.Equal(new[] { "Motion", "Fan" }, units.Select(x => x.Name));
        Assert.Equal("Home", Assert.Single(units[0].Children).Name);
        Assert.Equal(new[] { "Overview", "Setup" }, await client.ListScreensAsync(hmi.Id));
        Assert.Equal(((int?)3, (int?)17), await client.HmiCountsAsync(hmi));
        Assert.Equal(new BlockMember("Input", "Start", "Bool", ""), Assert.Single(await client.ReadInterfaceAsync("Line/PLC", "Mixing/Mixer")));
        Assert.All(session.Calls.Where(c => c.Tool == "ListPlcSoftwareUnits"), c => Assert.Equal("Line/PLC", c.Args["softwarePath"]));
        session.Release = "15.1";
        int count = session.Calls.Count;
        Assert.Empty(await client.ListUnitsAsync("Line/PLC"));
        Assert.Empty(await client.ReadInterfaceAsync("Line/PLC", "Mixing/Mixer"));
        Assert.Equal(count, session.Calls.Count);
    }

    [Fact]
    public async Task Unit_inventory_keeps_host_errors_visible_except_for_an_absent_provider()
    {
        var session = new DocumentSession((_, _) => "{}") { FailureCode = "UNSUPPORTED_CAPABILITY" };
        using var client = new McpProjectClient(session);
        Assert.Empty(await client.ListUnitsAsync("Line/PLC"));
        Assert.Contains("UNSUPPORTED_CAPABILITY", Assert.Single(client.Receipts).Json);
        session.FailureCode = "ACCESS_DENIED";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListUnitsAsync("Line/PLC"));
        Assert.Equal("ACCESS_DENIED", error.Data["McpErrorCode"]);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Download_check_binds_the_preview_and_never_submits_it_implicitly(bool ready, bool matchingProject)
    {
        string hash = new('a', 64);
        var session = new DocumentSession((tool, args) => tool switch {
            "ListTransferRoutes" => """{"modes":[{"pcInterfaces":[{"name":"Ethernet"}]},{"pcInterfaces":[{"name":"Ethernet"}]}]}""",
            "CheckDownloadReadiness" => new JsonObject { ["ready"] = ready, ["issues"] = new JsonArray() }.ToJsonString(),
            "DownloadPlc" when Equals(args["dryRun"], true) => new JsonObject { ["planHash"] = hash,
                ["targetIdentity"] = new JsonObject { ["projectFile"] = matchingProject ? DocumentSession.Project : "different.ap21" } }.ToJsonString(),
            "DownloadPlc" => """{"downloadState":"Success"}""",
            _ => throw new InvalidOperationException(tool) });
        using var client = new McpProjectClient(session);
        Assert.Equal("Ethernet", Assert.Single(await client.DownloadAdaptersAsync("Line/PLC")));
        Task<ProjectDownloadPlan> Preview() => client.PreviewDownloadAsync("Line/PLC", "192.0.2.4", "Ethernet", "softwareOnlyChanges");
        if (!ready)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(Preview);
            Assert.DoesNotContain(session.Calls, c => c.Tool == "DownloadPlc");
            return;
        }
        if (!matchingProject) { await Assert.ThrowsAsync<InvalidDataException>(Preview); Assert.True((bool)session.Calls.Last().Args["dryRun"]!); return; }
        var plan = await Preview();
        Assert.True((bool)session.Calls.Last().Args["dryRun"]!);
        session.Epoch++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SubmitDownloadAsync(plan));
        Assert.Single(session.Calls, c => c.Tool == "DownloadPlc");
        await client.SubmitDownloadAsync(await Preview());
        var applied = Assert.Single(session.Calls, c => c.Tool == "DownloadPlc" && Equals(c.Args["dryRun"], false));
        Assert.Equal(hash, applied.Args["expectedPlanHash"]); Assert.Equal(DocumentSession.Project, applied.Args["expectedProjectFile"]);
        Assert.Equal("192.0.2.4", applied.Args["targetIpAddress"]); Assert.Equal(true, applied.Args["confirmPhysicalTarget"]);
    }

    [Fact]
    public async Task Unit_export_and_verified_import_preserve_scope_and_operator_review()
    {
        var session = new DocumentSession((tool, args) => tool switch {
            "ExportPlcBlock" => """{"outputFile":"unit.xml"}""",
            "ImportPlcBlockVerified" when Equals(args["dryRun"], true) =>
                """{"evidence":{"token":"reviewed","plannedPath":"planned.xml","backup":{"path":"before.xml"}}}""",
            "ImportPlcBlockVerified" => """{"evidence":{"verificationSuccess":true}}""",
            _ => throw new InvalidOperationException(tool) });
        using var client = new McpProjectClient(session);
        Assert.Equal("unit.xml", await client.ExportBlockXmlAsync("Line/PLC", "Home", "fixture", "Motion", true));
        Assert.All(session.Calls, call => { Assert.Equal("Motion", call.Args["unitName"]); Assert.Equal(true, call.Args["stripVolatile"]); });
        session.Calls.Clear();
        Assert.False(await client.ImportVerifiedAsync("Line/PLC", "A/Home", "review.xml", "evidence", _ => false));
        Assert.True((bool)Assert.Single(session.Calls).Args["dryRun"]!);
        Assert.True(await client.ImportVerifiedAsync("Line/PLC", "A/Home", "review.xml", "evidence",
            details => details.Contains("before.xml", StringComparison.Ordinal)));
        var applied = Assert.Single(session.Calls, call => Equals(call.Args["dryRun"], false));
        Assert.Equal("reviewed", applied.Args["previewToken"]); Assert.Equal("A/Home", applied.Args["blockPath"]);
    }

    [Fact]
    public void Pending_block_review_requires_matching_project_plc_and_full_block_path()
    {
        var row = new ApprovalRequest("request", "MCP", "stdio", "local", "21", DocumentSession.Project, "Line/PLC", [], [], "", 60,
            DateTimeOffset.UtcNow.AddMinutes(1), ApprovalState.Pending, """{"blockPath":"A/Home"}""");
        bool Match(ApprovalRequest r) => MainWindow.MatchesBlockReview(r, "21", DocumentSession.Project, "Line/PLC", "A/Home");
        Assert.True(Match(row));
        Assert.False(Match(row with { Project = "other.ap21" }));
        Assert.False(Match(row with { Plc = "Line/PLC2" }));
        Assert.False(Match(row with { ParametersJson = """{"blockPath":"B/Home"}""" }));
        Assert.False(Match(row with { State = ApprovalState.Completed }));
        Assert.False(Match(row with { Actor = "workbench" }));
    }
}
