extern alias toolpipeline;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Logic.Testing;
using TiaOpenness.Shared;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Tools = toolpipeline::TiaMcpServer.ModelContextProtocol.PlcTestLibraryTools;

// Hand-written run records; synthetic evidence uses the real adapter field names and nesting. No native execution.
[Collection(BundleSelectionCollection.Name)]
public sealed class PlcTestReportTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "plc-test-reports-" + Guid.NewGuid().ToString("N"));
    private const string Scenario = """
        {"schemaVersion":1,"id":"start","title":"Conveyor start","requirement":"REQ-012","kind":"plcsimScenario",
        "scenario":{"instance":"PLC_1","mode":"singleStep","stopOnFailure":true,
        "steps":[{"write":{"password":true}},{"assert":{"Running":true}}]}}
        """;
    private const string Suite = """
        {"schemaVersion":1,"id":"interlock","title":"Interlock","kind":"testSuite",
        "testSuite":{"category":"application","kind":"case","names":["TC_Interlock"],"runAll":false}}
        """;

    internal static PlcTestRunRecord Record(string verdict = "failed", string status = "completed") => new() {
        RunId = "20261117-083000-1a2b3c4d", Status = status,
        StartedUtc = DateTimeOffset.Parse("2026-11-17T08:30:00Z", System.Globalization.CultureInfo.InvariantCulture),
        FinishedUtc = DateTimeOffset.Parse("2026-11-17T08:31:12.345Z", System.Globalization.CultureInfo.InvariantCulture),
        ReleaseKey = "21", HostVersion = "4.0.0.0", Project = new JsonObject { ["file"] = @"D:\Users\private-user\Line1.ap21" },
        PlanHash = "plan-hash", Audit = new JsonObject { ["requestId"] = "fixture-request" },
        Cases = new() { new() {
            Id = "start", Title = "Conveyor start", Requirement = "REQ-012", Kind = "plcsimScenario",
            CaseSha256 = PlcTestCaseDocument.Parse(Scenario).Sha256, Verdict = verdict, DurationMs = 1830,
            Message = @"assertion failed; source D:\Users\private-user\source.json",
            Evidence = JsonNode.Parse("""
                {"api":{"path":"D:\\Users\\private-user\\SimulationRuntimeApi.dll","version":"7.0"},
                "steps":[{"index":2,"kind":"assert","ok":false,"checks":[
                {"name":"Running","type":"Bool","expected":true,"actual":false,"ok":false}]}]}
                """)!.AsObject()
        } }
    };

    private static Dictionary<string, string> Documents => new() { ["start"] = Scenario, ["interlock"] = Suite };
    private static PlcTestReport Build(PlcTestRunRecord run, string format, JsonObject? audit = null)
        => PlcTestReportBuilder.Build(run, format, audit ?? new JsonObject(), Documents, CallJournalPayload.RedactArguments);

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Report_registration_and_usage_cover_all_eight_releases(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var registry = TiaMcp.FoundationHost.LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, registry);
        using var session = pipeline.EnterSession();
        var descriptor = Host.McpServer.CatalogView.Find("ExportPlcTestReport")!;
        Assert.NotNull(descriptor);
        Assert.Equal("host", descriptor.Execution);
        Assert.False(TiaMcpServer.ModelContextProtocol.PreflightLogic.NeedsProject("FILE", descriptor.Name));
        var usage = ToolUsageCatalog.Describe(descriptor.Name, release, "plc-foundation", descriptor.RawDescription,
            JsonNode.Parse(descriptor.Tool.InputSchema.GetRawText())!.AsObject(), operation: "", roster: Host.McpServer.CatalogView.All.Keys);
        Assert.Equal("fat", (string?)usage["example"]?["request"]?["params"]?["arguments"]?["format"]);
        Assert.Contains("Not yet verified on a real PLCSIM", usage.ToJsonString());
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("fat", "Factory Acceptance Test")]
    [InlineData("sat", "Site Acceptance Test")]
    public void Acceptance_reports_include_headers_totals_failures_and_blank_sign_off(string format, string title)
    {
        var run = Record();
        var report = Build(run, format);
        foreach (string expected in new[] { title, run.RunId, "2026-11-17T08:30:00.000Z", "2026-11-17T08:31:12.345Z",
            "releaseKey", "21", "4.0.0.0", "Line1.ap21", "7.0", "librarySha256", "REQ-012", "1830", "failed",
            "expected true, actual false", "Supplier", "Customer", "Name | Role | Date | Signature", "Accepted deviations",
            "verify with `tia audit verify`", "exitCode: 3" }) Assert.Contains(expected, report.Markdown);
        Assert.Equal(1, (int)report.Document["totals"]!["failed"]!);
        Assert.Equal("", (string?)report.Document["signOff"]?["supplier"]?["signature"]);
        Assert.DoesNotContain("private-user", report.Markdown + report.Document.ToJsonString());
        Assert.DoesNotContain("INCOMPLETE RUN", report.Markdown);
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("running")]
    public void Incomplete_runs_cannot_be_used_for_acceptance(string status)
    {
        var report = Build(Record(status: status), "fat");
        Assert.Contains("INCOMPLETE RUN - not valid for acceptance", report.Markdown);
        Assert.Equal(5, (int)report.Document["exitCode"]!);
    }

    [Fact]
    public void Audit_binding_uses_existing_verifier_and_distinguishes_absent_request_and_broken_chain()
    {
        string audit = Path.Combine(root, "audit");
        var log = new AuditLog(audit);
        log.Append("request", "another-request", "private-host", "21", "RunPlcTestLibrary");
        var run = Record();
        var missing = Tools.ReportAudit(run, new[] { audit });
        Assert.Contains("no audit row found", Build(run, "fat", missing).Markdown);
        log.Append("end", "fixture-request", "private-host", "21", "RunPlcTestLibrary", outcome: "completed", planHash: run.PlanHash);
        var found = Tools.ReportAudit(run, new[] { audit });
        Assert.Equal("intact", (string?)found["chains"]?[0]?["state"]);
        Assert.Equal("fixture-request", (string?)found["rows"]?[0]?["requestId"]);
        Assert.DoesNotContain("private-host", Build(run, "fat", found).Markdown);
        File.AppendAllText(Directory.GetFiles(audit, "audit-*.jsonl").Single(), "{invalid\n");
        Assert.Equal("broken", (string?)Tools.ReportAudit(run, new[] { audit })["chains"]?[0]?["state"]);
    }

    [Fact]
    public void Repro_keeps_verbatim_case_redacts_call_and_omits_passed_cases_and_user_paths()
    {
        var run = Record();
        run.Cases.Add(new() { Id = "passed-case", Title = "Must be omitted", Verdict = "passed" });
        var report = Build(run, "repro");
        Assert.Contains(Scenario, report.Markdown);
        Assert.Equal(Scenario, (string?)report.Document["cases"]?[0]?["caseDocument"]);
        var call = report.Document["cases"]![0]!["call"]!;
        Assert.Equal("RunPlcSimAdvancedTestScenario", (string?)call["name"]);
        Assert.Equal(CallJournalPayload.Hidden, (string?)call["arguments"]?["scenario"]?["steps"]?[0]?["write"]?["password"]);
        Assert.True((bool)call["arguments"]!["confirmRun"]!);
        Assert.False((bool)call["arguments"]!["dryRun"]!);
        Assert.Contains("expected true, actual false", report.Markdown);
        Assert.DoesNotContain(@":\", report.Markdown);
        foreach (string hidden in new[] { "private-user", "passed-case", "Must be omitted" })
            Assert.DoesNotContain(hidden, report.Markdown + report.Document.ToJsonString());
    }

    [Fact]
    public void Test_suite_repro_has_exact_selection_and_warning_messages()
    {
        var run = Record();
        run.Cases.Clear();
        run.Cases.Add(new() { Id = "interlock", Kind = "testSuite", Title = "Interlock", Verdict = "failed",
            CaseSha256 = PlcTestCaseDocument.Parse(Suite).Sha256,
            Evidence = JsonNode.Parse("""
                {"result":{"state":"Warning","errorCount":0,"warningCount":1,"messages":[
                {"path":"TC_Interlock","state":"Warning","description":"Open deviation","messages":[]}]}}
                """)!.AsObject() });
        var report = Build(run, "repro");
        var args = report.Document["cases"]![0]!["call"]!["arguments"]!;
        Assert.Equal("application", (string?)args["category"]);
        Assert.Equal("case", (string?)args["kind"]);
        Assert.Equal("TC_Interlock", (string?)args["names"]?[0]);
        Assert.False((bool)args["runAll"]!);
        Assert.True((bool)args["confirmExternalExecution"]!);
        Assert.False((bool)args["dryRun"]!);
        Assert.Contains("TC_Interlock: Open deviation", report.Markdown);
    }

    [Fact]
    public void Repro_of_passed_run_has_no_cases_and_changed_case_is_refused()
    {
        var report = Build(Record("passed"), "repro");
        Assert.Contains("All cases passed", report.Markdown);
        Assert.Empty(report.Document["cases"]!.AsArray());
        var changed = Record(); changed.Cases[0].CaseSha256 = "changed";
        Assert.Contains("restore the original case", Assert.Throws<ArgumentException>(() => Build(changed, "repro")).Message);
    }

    [Theory]
    [InlineData(@"C:\Users\private-user\case.json")]
    [InlineData(@"\\private-host\share\case.json")]
    [InlineData("/home/private-user/case.json")]
    public void Repro_refuses_path_bearing_case_without_changing_the_verbatim_document(string path)
    {
        var document = PlcTestCaseDocument.Parse(Scenario).Document;
        document["scenario"]!["steps"]![1]!["note"] = path;
        var original = PlcTestCaseDocument.Parse(document.ToJsonString());
        var run = Record(); run.Cases[0].CaseSha256 = original.Sha256;
        var sources = new Dictionary<string, string> { ["start"] = original.CanonicalJson };
        var error = Assert.Throws<ArgumentException>(() => PlcTestReportBuilder.Build(run, "repro", new(), sources,
            CallJournalPayload.RedactArguments));
        Assert.Contains("verbatim path-free reproduction is unavailable", error.Message);
        Assert.Equal(path, (string?)PlcTestCaseDocument.Parse(sources["start"]).Document["scenario"]?["steps"]?[1]?["note"]);
    }

    [Fact]
    public void Library_hash_uses_sorted_id_hash_lines()
    {
        var run = Record(); run.Cases.Add(new() { Id = "aaa", CaseSha256 = "1234" });
        string expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("aaa:1234\nstart:" + run.Cases[0].CaseSha256)));
        Assert.Equal(expected, PlcTestReportBuilder.LibraryHash(run));
        run.Cases.Reverse();
        Assert.Equal(expected, PlcTestReportBuilder.LibraryHash(run));
    }

    [Theory]
    [InlineData("junit")]
    [InlineData("fat")]
    [InlineData("sat")]
    [InlineData("repro")]
    public void Tool_exports_stored_run_using_the_shared_writers(string format)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, "21");
        var run = Record(); run.Save(Path.Combine(root, "runs"));
        Directory.CreateDirectory(Path.Combine(root, "library", "cases"));
        File.WriteAllText(Path.Combine(root, "library", "cases", "start.json"), Scenario);
        var tool = new Tools(() => Path.Combine(root, "library"), () => Path.Combine(root, "runs"));
        var result = tool.ExportPlcTestReport(format: format, exportDirectory: Path.Combine(root, "report"));
        Assert.False(result.IsError, result.StructuredContent?.ToJsonString());
        var paths = result.StructuredContent!["data"]!["files"]!.AsArray().Select(p => (string)p!).ToArray();
        Assert.Equal(format == "junit" ? 1 : 2, paths.Length);
        Assert.All(paths, path => Assert.True(File.Exists(path)));
        if (format == "junit") Assert.Equal(PlcTestJUnitWriter.Write(run), File.ReadAllText(paths[0]));
        else Assert.Equal(run.RunId, (string?)JsonNode.Parse(File.ReadAllText(paths[1]))?["runId"]);
    }

    [Fact]
    public async Task Default_target_is_the_session_directory_under_output_root_without_worker_dispatch()
    {
        var run = Record("passed"); run.Save(Path.Combine(root, "runs"));
        string previous = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        try
        {
            using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
            var registry = TiaMcp.FoundationHost.LegacyHostToolRegistry.Create(worker, "21", false, hostPorts: false);
            using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, registry);
            using (pipeline.EnterSession())
            {
                toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.CurrentOrNull!.OutputRoot = () => Path.Combine(root, "exports");
                var invoker = (toolpipeline::TiaMcp.FoundationHost.WorkerToolInvoker)Host.McpServer.ToolInvoker;
                invoker.SetLocalTarget(typeof(Tools), new Tools(() => root, () => Path.Combine(root, "runs")));
            }
            var result = await ToolHintOnceTests.Invoke(pipeline.Tools.Single(t => t.ProtocolTool.Name == "ExportPlcTestReport"), new { format = "junit" });
            Assert.True((bool?)result["ok"], result.ToJsonString());
            string file = (string)result["data"]!["files"]![0]!;
            string session = Path.GetDirectoryName(file)!;
            Assert.Equal(Path.Combine(root, "exports"), Path.GetDirectoryName(session));
            Assert.Equal(PlcTestJUnitWriter.Write(run), File.ReadAllText(file));
            Assert.Equal(0, worker.Calls);
        }
        finally { toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previous; }
    }

    [Fact]
    public async Task Outside_output_target_requires_existing_workbench_approval_before_read_or_write()
    {
        string settings = ApprovalSettings.SettingsPath;
        byte[]? previous = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var oldWait = Host.McpServer.ApprovalWaitOverrideForTests;
        int cards = 0;
        Task<ApprovalOutcome> Wait(PendingApproval card, ApprovalSettings _, CancellationToken token)
        { cards++; return Task.FromResult(new ApprovalOutcome(card, false, "denied")); }
        try
        {
            new ApprovalSettings(true, 1).Save(settings);
            Host.McpServer.ApprovalWaitOverrideForTests = Wait;
            using var fixture = new VersionControlAdmissionTests.FileToolFixture("21", approvalWait: Wait);
            var result = await fixture.Call("ExportPlcTestReport", new { runId = "latest", format = "fat", exportDirectory = root });
            Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal(1, cards);
            Assert.False(Directory.Exists(root));
            Assert.Equal(0, fixture.Worker.Calls);
        }
        finally
        {
            Host.McpServer.ApprovalWaitOverrideForTests = oldWait;
            if (previous == null) File.Delete(settings); else File.WriteAllBytes(settings, previous);
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
