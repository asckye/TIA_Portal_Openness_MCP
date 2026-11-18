extern alias toolpipeline;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.Testing;
using TiaOpenness.Shared;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Tools = toolpipeline::TiaMcpServer.ModelContextProtocol.PlcTestLibraryTools;

// Persisted records come from PlcTestLibraryRunner with synthetic native replies. No native execution.
[Collection(BundleSelectionCollection.Name)]
public sealed class PlcTestReportTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "plc-test-reports-" + Guid.NewGuid().ToString("N"));
    private const string Scenario = """
        {"schemaVersion":1,"id":"start","title":"Conveyor start","requirement":"REQ-012","kind":"plcsimScenario",
        "scenario":{"instance":"PLC_1","mode":"singleStep","stopOnFailure":true,
        "steps":[{"write":{"password":"fixture-secret"}},{"assert":{"Running":true},"note":"保持 this text verbatim"}]}}
        """;
    private const string Suite = """
        {"schemaVersion":1,"id":"interlock","title":"Interlock","kind":"testSuite",
        "testSuite":{"category":"application","kind":"case","names":["TC_Interlock"],"runAll":false}}
        """;

    internal static PlcTestRunRecord Record(string verdict = "failed", string status = "completed")
    {
        var reply = PlcTestRunTests.ScenarioReply(verdict);
        reply["Data"]!["api"] = JsonNode.Parse("""
            {"apiPath":"C:\\Program Files\\Siemens\\Automation\\PLCSIM Advanced\\API\\7.0\\Siemens.Simatic.Simulation.Runtime.Api.x64.dll",
            "apiSource":"installed","apiVersion":"7.0","assemblyVersion":"7.0.0.0","networkMode":"TCPIP"}
            """);
        reply["Data"]!["steps"]![0]!["index"] = 2;
        if (reply["Data"]!["steps"]![0]!["checks"] is JsonArray checks) checks[0]!["name"] = "Running";
        if (verdict == "skipped")
        {
            reply["Meta"]!["status"] = "UNSUPPORTED_CAPABILITY";
            reply["Data"]!["steps"] = new JsonArray();
        }
        var run = PlcTestRunTests.ReportRecord(PlcTestCaseDocument.Parse(Scenario), reply);
        Assert.Equal(new[] { "code", "message", "details" }, run.Cases[0].Evidence.Select(pair => pair.Key));
        Assert.NotNull(run.Cases[0].Evidence["details"]?["api"]);
        Assert.Null(run.Cases[0].Evidence["api"]);
        return PresentationTimes(run, status);
    }

    // Only display metadata is fixed for deterministic assertions; evidence is read unchanged from run.json.
    private static PlcTestRunRecord PresentationTimes(PlcTestRunRecord run, string status = "completed")
    {
        run.RunId = "20261117-083000-1a2b3c4d"; run.Status = status;
        run.StartedUtc = DateTimeOffset.Parse("2026-11-17T08:30:00Z", System.Globalization.CultureInfo.InvariantCulture);
        run.FinishedUtc = DateTimeOffset.Parse("2026-11-17T08:31:12.345Z", System.Globalization.CultureInfo.InvariantCulture);
        run.HostVersion = "4.0.0.0";
        run.Project = new JsonObject { ["file"] = "Line1.ap21" };
        run.Audit["requestId"] = "fixture-request";
        run.Cases[0].DurationMs = 1830;
        return run;
    }

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
        var passed = Record("passed").Cases[0]; passed.Id = "passed-case"; passed.Title = "Must be omitted";
        run.Cases.Add(passed);
        var report = Build(run, "repro");
        string redactedDocument = Scenario.Replace("\"fixture-secret\"", JsonSerializer.Serialize(CallJournalPayload.Hidden));
        Assert.Contains(redactedDocument, report.Markdown);
        Assert.Equal(redactedDocument, (string?)report.Document["cases"]?[0]?["caseDocument"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(redactedDocument), report.Document["cases"]?[0]?["testCase"]));
        Assert.DoesNotContain("fixture-secret", report.Markdown + report.Document.ToJsonString());
        var call = report.Document["cases"]![0]!["call"]!;
        Assert.Equal("RunPlcSimAdvancedTestScenario", (string?)call["name"]);
        Assert.Equal(CallJournalPayload.Hidden, (string?)call["arguments"]?["scenario"]?["steps"]?[0]?["write"]?["password"]);
        Assert.True((bool)call["arguments"]!["confirmRun"]!);
        Assert.False((bool)call["arguments"]!["dryRun"]!);
        Assert.Equal("<planHash from preview>", (string?)call["arguments"]?["expectedPlanHash"]);
        Assert.Equal("<expectedProjectFile from preview>", (string?)call["arguments"]?["expectedProjectFile"]);
        var preview = report.Document["cases"]![0]!["previewCall"]!;
        Assert.Equal("RunPlcSimAdvancedTestScenario", (string?)preview["name"]);
        Assert.True((bool)preview["arguments"]!["dryRun"]!);
        Assert.False((bool)preview["arguments"]!["confirmRun"]!);
        Assert.Equal(CallJournalPayload.Hidden, (string?)preview["arguments"]?["scenario"]?["steps"]?[0]?["write"]?["password"]);
        Assert.Contains("expected true, actual false", report.Markdown);
        Assert.DoesNotContain(@":\", report.Markdown);
        foreach (string hidden in new[] { "private-user", "passed-case", "Must be omitted" })
            Assert.DoesNotContain(hidden, report.Markdown + report.Document.ToJsonString());
    }

    [Fact]
    public void Test_suite_repro_has_exact_selection_and_warning_messages()
    {
        var reply = PlcTestRunTests.SuiteReply("Warning");
        reply["Meta"]!["result"]!["messages"]![0]!["path"] = "TC_Interlock";
        reply["Meta"]!["result"]!["messages"]![0]!["description"] = "Open deviation";
        var run = PresentationTimes(PlcTestRunTests.ReportRecord(PlcTestCaseDocument.Parse(Suite), reply));
        var report = Build(run, "repro");
        var args = report.Document["cases"]![0]!["call"]!["arguments"]!;
        Assert.Equal("application", (string?)args["category"]);
        Assert.Equal("case", (string?)args["kind"]);
        Assert.Equal("TC_Interlock", (string?)args["names"]?[0]);
        Assert.False((bool)args["runAll"]!);
        Assert.True((bool)args["confirmExternalExecution"]!);
        Assert.False((bool)args["dryRun"]!);
        Assert.Equal("<planHash from preview>", (string?)args["expectedPlanHash"]);
        Assert.Equal("<expectedProjectFile from preview>", (string?)args["expectedProjectFile"]);
        Assert.True((bool)report.Document["cases"]![0]!["previewCall"]!["arguments"]!["dryRun"]!);
        Assert.Contains("TC_Interlock: Open deviation", report.Markdown);
    }

    [Fact]
    public void Runner_record_preserves_multiline_assertions_and_nonsensitive_source_text_in_reports()
    {
        var document = PlcTestCaseDocument.Parse(Scenario).Document;
        document["scenario"]!["steps"]![1]!["note"] = "first line\nsecond line";
        var original = PlcTestCaseDocument.Parse(document.ToJsonString());
        var reply = PlcTestRunTests.ScenarioReply("failed");
        reply["Data"]!["steps"]![0]!["checks"]!.AsArray().Add(new JsonObject {
            ["name"] = "Interlock", ["type"] = "Bool", ["expected"] = true, ["actual"] = false, ["ok"] = false });
        var run = PlcTestRunTests.ReportRecord(original, reply);
        Assert.Contains("\n", run.Cases[0].Message);
        var report = PlcTestReportBuilder.Build(run, "repro", new(), new Dictionary<string, string> {
            [original.Id] = original.CanonicalJson }, CallJournalPayload.RedactArguments);
        Assert.Equal(run.Cases[0].Message, (string?)report.Document["cases"]?[0]?["message"]);
        Assert.Contains("Interlock", (string?)report.Document["cases"]?[0]?["details"]);
        string redacted = original.CanonicalJson.Replace("\"fixture-secret\"", JsonSerializer.Serialize(CallJournalPayload.Hidden));
        Assert.Equal(redacted, (string?)report.Document["cases"]?[0]?["caseDocument"]);
        Assert.Contains(redacted, report.Markdown);
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
        var run = Record(); var another = Record("passed").Cases[0]; another.Id = "aaa"; another.CaseSha256 = "1234";
        run.Cases.Add(another);
        string expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("aaa:1234\nstart:" + run.Cases[0].CaseSha256)));
        Assert.Equal(expected, PlcTestReportBuilder.LibraryHash(run));
        run.Cases.Reverse();
        Assert.Equal(expected, PlcTestReportBuilder.LibraryHash(run));
    }

    [Theory]
    [InlineData("fat")]
    [InlineData("sat")]
    [InlineData("repro")]
    public void Reports_scrub_paths_with_spaces_in_real_describe_evidence_and_all_run_text(string format)
    {
        const string local = @"C:\Users\Max Mustermann\Documents\x.json";
        const string unc = @"\\private-host\Shared Documents\x.json";
        var run = Record();
        run.HostVersion = local;
        run.Cases[0].Title = local;
        run.Cases[0].Requirement = unc;
        run.Cases[0].Message = "Source: " + local;
        run.Cases[0].Evidence["details"]!["steps"]![0]!["checks"]![0]!["actual"] = local;
        var audit = new JsonObject { ["reason"] = local };
        var report = Build(run, format, audit);
        string output = report.Markdown + report.Document.ToJsonString();
        foreach (string privateText in new[] { "Max Mustermann", "Program Files", "private-host", "Shared Documents", @"C:\", @"\\" })
            Assert.DoesNotContain(privateText, report.Markdown);
        Assert.DoesNotContain("Max Mustermann", output);
        Assert.DoesNotContain("Program Files", output);
        Assert.Equal("x.json", (string?)report.Document["environment"]?["hostVersion"]);
        Assert.Equal("x.json", (string?)report.Document["cases"]?[0]?["title"]);
        Assert.Equal("x.json", (string?)report.Document["cases"]?[0]?["requirement"]);
        Assert.Equal("Siemens.Simatic.Simulation.Runtime.Api.x64.dll", (string?)report.Document["environment"]?["plcsimApi"]?["apiPath"]);
        Assert.Equal("installed", (string?)report.Document["environment"]?["plcsimApi"]?["apiSource"]);
        Assert.Equal("7.0", (string?)report.Document["environment"]?["plcsimApi"]?["apiVersion"]);
        Assert.Equal("7.0.0.0", (string?)report.Document["environment"]?["plcsimApi"]?["assemblyVersion"]);
        Assert.Equal("TCPIP", (string?)report.Document["environment"]?["plcsimApi"]?["networkMode"]);
        Assert.Contains("x.json", (string?)report.Document["cases"]?[0]?["details"]);
        if (format != "repro")
        {
            Assert.Equal("x.json", (string?)report.Document["audit"]?["reason"]);
            Assert.Contains("| start | x.json | x.json | failed |", report.Markdown);
        }
        Assert.Contains("Max Mustermann", run.Cases[0].Title);
    }

    [Fact]
    public void Known_data_and_output_root_prefixes_are_scrubbed_including_spaces()
    {
        var run = Record();
        run.Cases[0].Title = "/known data root/with spaces/input.json";
        run.Cases[0].Message = "/known output root/with spaces/output.json";
        var report = PlcTestReportBuilder.Build(run, "fat", new(), Documents, CallJournalPayload.RedactArguments);
        Assert.Equal("input.json", (string?)report.Document["cases"]?[0]?["title"]);
        Assert.Equal("output.json", (string?)report.Document["cases"]?[0]?["message"]);
        Assert.DoesNotContain("known data root", report.Markdown + report.Document.ToJsonString());
        Assert.DoesNotContain("known output root", report.Markdown + report.Document.ToJsonString());
    }

    [Fact]
    public void Tool_junit_scrubs_paths_before_xml_escaping_without_changing_the_shared_writer_or_run()
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, "21");
        const string local = @"C:\Users\Max Mustermann\Documents\x.json";
        var run = Record();
        run.Cases[0].Title = local; run.Cases[0].Requirement = local; run.Cases[0].Message = local;
        run.Cases[0].Evidence["details"]!["steps"]![0]!["checks"]![0]!["actual"] = local;
        run.Save(Path.Combine(root, "runs"));
        var tool = new Tools(() => root, () => Path.Combine(root, "runs"));
        var result = tool.ExportPlcTestReport(format: "junit", exportDirectory: Path.Combine(root, "report"));
        Assert.False(result.IsError, result.StructuredContent?.ToJsonString());
        string xml = File.ReadAllText((string)result.StructuredContent!["data"]!["files"]![0]!);
        Assert.DoesNotContain("Max Mustermann", xml);
        Assert.DoesNotContain(@"C:\", xml);
        var test = System.Xml.Linq.XDocument.Parse(xml).Descendants("testcase").Single();
        Assert.Equal("x.json", test.Element("failure")!.Attribute("message")!.Value);
        Assert.All(test.Element("properties")!.Elements("property"), p => Assert.Equal("x.json", p.Attribute("value")!.Value));
        Assert.Contains("x.json", test.Element("failure")!.Value);
        Assert.Contains("Max Mustermann", PlcTestJUnitWriter.Write(run));
        Assert.Equal(local, PlcTestRunRecord.Read(Path.Combine(root, "runs"), run.RunId).Cases[0].Title);
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("publish")]
    public void Report_pair_failure_leaves_neither_file_or_staging_directory(string failure)
    {
        string stem = Path.Combine(root, "report", "operation");
        int moves = 0;
        void Write(string path, string text)
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
            if (failure == "stage" && path.EndsWith(".json", StringComparison.Ordinal)) throw new IOException("stage failure");
        }
        void Move(string from, string to)
        {
            Assert.True(File.Exists(Path.Combine(stem + ".tmp", "report.json")));
            if (to.EndsWith(".json", StringComparison.Ordinal)) throw new IOException("second publication failure");
            File.Move(from, to); moves++;
        }
        Assert.Throws<IOException>(() => Tools.PublishReport(stem, new[] { (".md", "Markdown"), (".json", "{}") }, Write, Move));
        Assert.Equal(failure == "stage" ? 0 : 1, moves);
        Assert.False(File.Exists(stem + ".md"));
        Assert.False(File.Exists(stem + ".json"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(stem)!));
    }

    [Fact]
    public void Report_publication_collision_does_not_replace_or_delete_an_existing_file()
    {
        string stem = Path.Combine(root, "report", "operation");
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        File.WriteAllText(stem + ".md", "existing report");
        Assert.Throws<IOException>(() => Tools.PublishReport(stem, new[] { (".md", "replacement"), (".json", "{}") }));
        Assert.Equal("existing report", File.ReadAllText(stem + ".md"));
        Assert.False(File.Exists(stem + ".json"));
        Assert.False(Directory.Exists(stem + ".tmp"));
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
        if (format == "junit") Assert.Equal(PlcTestReportBuilder.WriteJUnit(run), File.ReadAllText(paths[0]));
        else Assert.Equal(run.RunId, (string?)JsonNode.Parse(File.ReadAllText(paths[1]))?["runId"]);
        var previous = paths.ToDictionary(path => path, File.ReadAllBytes);
        var again = tool.ExportPlcTestReport(format: format, exportDirectory: Path.Combine(root, "report"));
        Assert.False(again.IsError, again.StructuredContent?.ToJsonString());
        var next = again.StructuredContent!["data"]!["files"]!.AsArray().Select(p => (string)p!).ToArray();
        Assert.Equal(paths.Length * 2, paths.Concat(next).Distinct().Count());
        Assert.All(previous, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
        Assert.All(paths.Concat(next), path => Assert.Matches("-[0-9a-f]{32}\\.(xml|md|json)$", path));
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "report")));
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
            Assert.Equal(PlcTestReportBuilder.WriteJUnit(run), File.ReadAllText(file));
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
