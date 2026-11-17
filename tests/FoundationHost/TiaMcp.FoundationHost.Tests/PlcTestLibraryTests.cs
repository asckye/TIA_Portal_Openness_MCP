extern alias toolpipeline;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Logic.Testing;
using TiaMcpServer.ModelContextProtocol;
using Xunit;
using Tools = toolpipeline::TiaMcpServer.ModelContextProtocol.PlcTestLibraryTools;

// Replies below use synthetic values with the real PlcSimAdvancedAdapter and TestSuiteAdapter field names/nesting.
[Collection(BundleSelectionCollection.Name)]
public sealed class PlcTestLibraryTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "plc-test-library-" + Guid.NewGuid().ToString("N"));
    private string Library => Path.Combine(root, "test-library");
    private string Runs => Path.Combine(root, "test-runs");
    private PlcTestLibrary Store => new(Library);
    private Tools Tool => new(() => Library, () => Runs);

    private const string Scenario = """
        {"schemaVersion":1,"id":"conveyor-start","title":"Conveyor starts when Start is pressed","requirement":"REQ-012",
        "tags":["fat","conveyor"],"kind":"plcsimScenario","scenario":{"instance":"PLC_1","mode":"singleStep","stopOnFailure":true,
        "steps":[{"write":{"\"Start\"":true}},{"waitMs":200},{"assert":{"\"Running\"":true},"note":"motor contactor on"}]}}
        """;
    private const string Suite = """
        {"schemaVersion":1,"id":"app-interlocks","title":"Application test cases for the interlocks","tags":["fat"],
        "kind":"testSuite","testSuite":{"category":"application","kind":"case","names":["TC_Interlock_A","TC_Interlock_B"],"runAll":false}}
        """;

    [Fact]
    public void Data_locations_use_the_data_root_and_local_application_fallback()
    {
        string bundle = Path.Combine(root, "bundle");
        Directory.CreateDirectory(Path.Combine(bundle, "manifest"));
        File.WriteAllText(Path.Combine(bundle, "manifest", "package-manifest.json"), "{}");
        var locations = TiaOpenness.Shared.DataLocations.Resolve(bundle, null!, root, root);
        Assert.Equal(Path.Combine(bundle, "data", "test-library"), locations.TestLibraryDirectory);
        Assert.Equal(Path.Combine(bundle, "data", "test-runs"), locations.TestRunsDirectory);
        string absentBase = Path.Combine(Path.GetPathRoot(root)!, "tia-absent-" + Guid.NewGuid().ToString("N"));
        var fallback = TiaOpenness.Shared.DataLocations.Resolve(absentBase, null!, root, root);
        Assert.Equal(Path.Combine(root, "TiaMcp", "test-library"), fallback.TestLibraryDirectory);
        Assert.Equal(Path.Combine(root, "TiaMcp", "test-runs"), fallback.TestRunsDirectory);
    }

    [Theory]
    [InlineData(Scenario)]
    [InlineData(Suite)]
    public async Task Pipeline_saves_and_reads_a_typed_document_without_worker_dispatch(string json)
    {
        string previous = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        try
        {
            using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
            var shared = TiaMcp.FoundationHost.LegacyHostToolRegistry.Create(worker, "21", false, hostPorts: false);
            using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
            using (pipeline.EnterSession())
            {
                var invoker = (toolpipeline::TiaMcp.FoundationHost.WorkerToolInvoker)toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer.ToolInvoker;
                invoker.SetLocalTarget(typeof(Tools), Tool);
            }
            var tool = pipeline.Tools.Single(t => t.ProtocolTool.Name == "ManagePlcTestLibrary");
            var result = await ToolHintOnceTests.Invoke(tool, new { action = "save", testCase = JsonNode.Parse(json) });
            Assert.True((bool?)result["ok"], result.ToJsonString());
            string id = PlcTestCaseDocument.Parse(json).Id;
            var read = await ToolHintOnceTests.Invoke(tool, new { action = "read", id });
            Assert.Equal(id, (string?)read["data"]?["testCase"]?["id"]);
            Assert.Equal(0, worker.Calls);
        }
        finally { toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previous; }
    }

    [Theory]
    [InlineData(Scenario)]
    [InlineData(Suite)]
    public void Both_documents_round_trip_as_identical_canonical_bytes(string json)
    {
        var document = PlcTestCaseDocument.Parse(json);
        Store.Save(document);
        string path = Path.Combine(Library, "cases", document.Id + ".json");
        byte[] first = File.ReadAllBytes(path);
        var read = Store.Read(document.Id.ToUpperInvariant());
        Assert.Equal(document.Sha256, (string?)read["sha256"]);
        Assert.Equal(document.CanonicalJson, Encoding.UTF8.GetString(first));
        Assert.False(first.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.DoesNotContain('\r', document.CanonicalJson);
        Store.Save(PlcTestCaseDocument.Parse(read["testCase"]!.ToJsonString()), overwrite: true);
        Assert.Equal(first, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("scenario")]
    [InlineData("step")]
    [InlineData("suite")]
    public void Unknown_properties_are_refused_at_each_closed_boundary(string target)
    {
        var json = JsonNode.Parse(target == "suite" ? Suite : Scenario)!;
        var obj = target switch { "scenario" => json["scenario"], "step" => json["scenario"]!["steps"]![0],
            "suite" => json["testSuite"], _ => json };
        obj!["unknownField"] = true;
        Assert.ThrowsAny<Exception>(() => PlcTestCaseDocument.Parse(json.ToJsonString()));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Unsafe_or_long_ids_are_refused(string id)
    {
        var json = JsonNode.Parse(Scenario)!;
        json["id"] = id;
        Assert.ThrowsAny<Exception>(() => PlcTestCaseDocument.Parse(json.ToJsonString()));
        Assert.Throws<ArgumentException>(() => Store.Read(id));
    }

    [Theory]
    [InlineData("both")]
    [InlineData("neither")]
    [InlineData("empty")]
    [InlineData("tooMany")]
    [InlineData("wrongSet")]
    public void Invalid_suite_selections_are_refused(string form)
    {
        var json = JsonNode.Parse(Suite)!;
        var suite = json["testSuite"]!.AsObject();
        switch (form)
        {
            case "both": suite["runAll"] = true; break;
            case "neither": suite.Remove("names"); break;
            case "empty": suite["names"] = new JsonArray(); break;
            case "tooMany": suite["names"] = new JsonArray(Enumerable.Range(0, 51).Select(i => (JsonNode)("TC_" + i)).ToArray()); break;
            case "wrongSet": suite["kind"] = "testSet"; suite["category"] = "system"; break;
        }
        Assert.ThrowsAny<Exception>(() => PlcTestCaseDocument.Parse(json.ToJsonString()));
    }

    [Fact]
    public void Scenarios_use_the_existing_500_step_limit_and_only_the_MCP_step_kinds()
    {
        var json = JsonNode.Parse(Scenario)!;
        json["scenario"]!["steps"] = new JsonArray(Enumerable.Range(0, 501).Select(_ => (JsonNode)new JsonObject { ["waitMs"] = 1 }).ToArray());
        Assert.ThrowsAny<Exception>(() => PlcTestCaseDocument.Parse(json.ToJsonString()));
        json["scenario"]!["steps"]!.AsArray().RemoveAt(500);
        Assert.Equal(500, PlcTestCaseDocument.Parse(json.ToJsonString()).Document["scenario"]!["steps"]!.AsArray().Count);
        json["scenario"]!["steps"]![0] = new JsonObject { ["cycles"] = 1 };
        Assert.ThrowsAny<Exception>(() => PlcTestCaseDocument.Parse(json.ToJsonString()));
    }

    [Fact]
    public void List_keeps_unreadable_and_invalid_files_visible_and_pages_filtered_cases()
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, "21");
        Store.Save(PlcTestCaseDocument.Parse(Scenario));
        Store.Save(PlcTestCaseDocument.Parse(Suite));
        string bad = Path.Combine(Library, "cases", "broken.json");
        File.WriteAllText(bad, "not json");
        string locked = Path.Combine(Library, "cases", "locked.json");
        using var handle = new FileStream(locked, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var rows = Store.List(new[] { "CONVEYOR" })["items"]!.AsArray();
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows.Count(r => (bool?)r?["valid"] == false && !string.IsNullOrEmpty((string?)r?["error"])));
        var page = Tool.ManagePlcTestLibrary("list", tags: new[] { "CONVEYOR" }, limit: 1).StructuredContent!;
        Assert.Equal(3, (int?)page["data"]?["matchingCount"]);
        Assert.Single(page["data"]!["items"]!.AsArray());
        Assert.Equal(1, (int?)page["meta"]?["paging"]?["nextOffset"]);
        Assert.Equal("INVALID_ARGUMENT", (string?)Tool.ManagePlcTestLibrary("list", offset: 5).StructuredContent!["error"]?["code"]);
    }

    [Fact]
    public void Save_needs_overwrite_and_delete_returns_the_restorable_document()
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, "21");
        var original = PlcTestCaseDocument.Parse(Scenario);
        var saved = Tool.ManagePlcTestLibrary("save", testCase: original).StructuredContent!;
        Assert.True((bool?)saved["ok"], saved.ToJsonString());
        var refused = Tool.ManagePlcTestLibrary("save", testCase: original).StructuredContent!;
        Assert.False((bool?)refused["ok"]);
        var changed = original.Document;
        changed["title"] = "Updated";
        var overwritten = Store.Save(PlcTestCaseDocument.Parse(changed.ToJsonString()), overwrite: true);
        Assert.Equal(original.Sha256, (string?)overwritten["previousSha256"]);
        var deleted = Tool.ManagePlcTestLibrary("delete", id: original.Id).StructuredContent!;
        Assert.Equal("Updated", (string?)deleted["data"]?["testCase"]?["title"]);
        Assert.Empty(Store.List()["items"]!.AsArray());
        Store.Save(PlcTestCaseDocument.Parse(deleted["data"]!["testCase"]!.ToJsonString()));
        Assert.Single(Store.List()["items"]!.AsArray());
    }

    public static IEnumerable<object[]> Verdicts()
    {
        yield return new object[] { "plcsimScenario", """{"meta":{"operationSuccess":true},"data":{"verdict":"passed","steps":[]}}""", "passed" };
        yield return new object[] { "plcsimScenario", """{"ok":false,"meta":{"operationSuccess":false},"data":{"verdict":"passed"}}""", "error" };
        yield return new object[] { "plcsimScenario",
            """{"meta":{"operationSuccess":false},"data":{"verdict":"failed","steps":[{"kind":"assert","ok":false}]}}""", "failed" };
        yield return new object[] { "plcsimScenario", """{"data":{"verdict":"failed","steps":[{"kind":"write","ok":false}]}}""", "error" };
        yield return new object[] { "plcsimScenario", """{"meta":{"operationSuccess":false,"error":"Licence unavailable"}}""", "error" };
        yield return new object[] { "plcsimScenario", """{"meta":{"outcomeUnknown":true},"data":{"verdict":"failed"}}""", "unknown" };
        yield return new object[] { "plcsimScenario", """{"data":{"steps":[{"kind":"write","ok":false,"outcomeUnknown":true}]}}""", "unknown" };
        yield return new object[] { "testSuite", """{"meta":{"testPassed":true,"apiCallSuccess":true,"result":{"state":"Success"}}}""", "passed" };
        yield return new object[] { "testSuite", """{"meta":{"testPassed":true,"apiCallSuccess":true,"result":{"state":"Information"}}}""", "passed" };
        yield return new object[] { "testSuite", """{"meta":{"testPassed":false,"apiCallSuccess":true,"result":{"state":"Warning"}}}""", "failed" };
        yield return new object[] { "testSuite", """{"meta":{"testPassed":false,"apiCallSuccess":true,"result":{"state":"Error"}}}""", "failed" };
        yield return new object[] { "testSuite", """{"meta":{"apiCallSuccess":false,"exceptionType":"LicenseNotFoundException"}}""", "error" };
        yield return new object[] { "testSuite", """{"meta":{"outcomeUnknown":true}}""", "unknown" };
        yield return new object[] { "testSuite", """{"error":{"code":"UNSUPPORTED_CAPABILITY"}}""", "skipped" };
    }

    [Theory, MemberData(nameof(Verdicts))]
    public void Verdict_mapping_uses_native_reply_fields(string kind, string json, string expected)
        => Assert.Equal(expected, PlcTestRunRecord.MapVerdict(kind, JsonNode.Parse(json)!.AsObject()));

    [Theory]
    [InlineData("completed", "passed", 0)]
    [InlineData("completed", "skipped,passed", 4)]
    [InlineData("completed", "failed,skipped", 3)]
    [InlineData("completed", "error,passed", 3)]
    [InlineData("completed", "unknown,failed,skipped", 5)]
    [InlineData("running", "passed", 5)]
    [InlineData("stopped", "skipped", 5)]
    public void Exit_codes_follow_precedence(string status, string verdicts, int expected)
        => Assert.Equal(expected, PlcTestRunRecord.CalculateExitCode(status, verdicts.Split(',')));

    [Fact]
    public void History_is_newest_first_and_recomputes_incomplete_exit_codes()
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, "21");
        var old = Run("20261116-083000-1a2b3c4d");
        old.Status = "completed"; old.Save(Runs);
        var latest = Run("20261117-083000-1a2b3c4d");
        latest.Status = "running"; latest.Save(Runs);
        var listed = Tool.ManagePlcTestLibrary("listRuns", limit: 1).StructuredContent!;
        Assert.Equal(latest.RunId, (string?)listed["data"]?["items"]?[0]?["runId"]);
        Assert.Equal(5, (int?)listed["data"]?["items"]?[0]?["exitCode"]);
        var read = Tool.ManagePlcTestLibrary("readRun").StructuredContent!;
        Assert.Equal(latest.RunId, (string?)read["data"]?["run"]?["runId"]);
        Assert.Equal(5, (int?)read["data"]?["run"]?["exitCode"]);
        Assert.Equal(0, PlcTestRunRecord.Read(Runs, old.RunId).ExitCode);
        Assert.Equal(latest.CanonicalJson, PlcTestRunRecord.Read(Runs, latest.RunId).CanonicalJson);
        Assert.Equal("NOT_FOUND", (string?)Tool.ManagePlcTestLibrary("read", id: "missing").StructuredContent!["error"]?["code"]);
    }

    [Fact]
    public void Failed_temporary_write_preserves_the_previous_readable_record_and_cleans_temp()
    {
        var run = Run("20261117-083000-1a2b3c4d"); run.Save(Runs);
        string path = Path.Combine(Runs, run.RunId, "run.json");
        Assert.Throws<IOException>(() => PlcTestRunRecord.WriteAtomic(path, "broken", writeTemporary: (temp, _) => {
            File.WriteAllText(temp, "partial"); throw new IOException("Synthetic disk failure"); }));
        Assert.Equal(run.CanonicalJson, PlcTestRunRecord.Read(Runs, run.RunId).CanonicalJson);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        run.Status = "completed"; run.Save(Runs);
        Assert.Equal("completed", PlcTestRunRecord.Read(Runs, run.RunId).Status);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Tool_is_registered_on_every_release_without_a_project(string release)
    {
        using var fixture = new LibraryDiscoveryFixture(release);
        var tool = Assert.Single(fixture.Tools, t => t.ProtocolTool.Name == "ManagePlcTestLibrary");
        Assert.False(PreflightLogic.NeedsProject("FILE", "ManagePlcTestLibrary"));
        var schema = tool.ProtocolTool.InputSchema;
        Assert.Equal("object", schema.GetProperty("properties").GetProperty("testCase").GetProperty("type").GetString());
        var described = TiaOpenness.Shared.ToolUsageCatalog.Describe("ManagePlcTestLibrary", release, "full-engine", tool.ProtocolTool.Description!,
            JsonNode.Parse(schema.GetRawText())!.AsObject(), operation: "save", roster: fixture.Tools.Select(t => t.ProtocolTool.Name));
        Assert.Equal("save", (string?)described["example"]?["request"]?["params"]?["arguments"]?["action"]);
        var result = await fixture.Call("ManagePlcTestLibrary", new { action = "save", testCase = new { schemaVersion = 1, id = "../bad" } });
        Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]?["code"]);
        var usage = await fixture.Call("GetToolUsage", new { toolName = "ManagePlcTestLibrary", operation = "save" });
        Assert.True((bool?)usage["ok"], usage.ToJsonString());
        Assert.Equal("save", (string?)usage["data"]?["example"]?["request"]?["params"]?["arguments"]?["action"]);
    }

    [Fact]
    public void JUnit_matches_the_authored_golden_for_all_five_verdicts_and_is_culture_invariant()
    {
        var run = Run("20261117-083000-1a2b3c4d");
        run.FinishedUtc = run.StartedUtc.AddMilliseconds(72345);
        run.PlanHash = "hash&<\"";
        run.Cases.Add(new() { Id = "passed", Title = "输送带启动", Verdict = "passed", Requirement = "REQ-\"<&" });
        run.Cases.Add(new() { Id = "failed", Title = "Assert", Verdict = "failed", DurationMs = 1830,
            Message = "expected <true> & \"on\"", Evidence = JsonNode.Parse("""
            {"steps":[{"index":2,"kind":"assert","ok":false,"checks":[{"name":"\"Running\"","type":"Bool","expected":true,"actual":false,"ok":false}]}]}
            """)!.AsObject() });
        run.Cases.Add(new() { Id = "skipped", Title = "Skipped", Verdict = "skipped", Message = "NOT_EXECUTED: unknown earlier outcome" });
        run.Cases.Add(new() { Id = "error", Title = "Licence", Kind = "testSuite", Verdict = "error", Message = "no licence",
            Evidence = new JsonObject { ["errorCode"] = "LICENSE_MISSING" } });
        run.Cases.Add(new() { Id = "unknown", Title = "Unknown", Kind = "testSuite", Verdict = "unknown", Message = "outcome unknown" });
        const string golden = """
            <?xml version="1.0" encoding="utf-8"?>
            <testsuites name="TIA PLC tests" tests="5" failures="1" errors="2" skipped="1" time="72.345" timestamp="2026-11-17T08:30:00Z">
              <testsuite name="plcsimScenario" tests="3" failures="1" errors="0" skipped="1" time="1.830" timestamp="2026-11-17T08:30:00Z">
                <properties>
                  <property name="runId" value="20261117-083000-1a2b3c4d" />
                  <property name="releaseKey" value="21" />
                  <property name="hostVersion" value="4.0.0.0" />
                  <property name="planHash" value="hash&amp;&lt;&quot;" />
                  <property name="status" value="completed" />
                </properties>
                <testcase classname="tia.plcsimScenario" name="passed" time="0.000">
                  <properties>
                    <property name="title" value="输送带启动" />
                    <property name="requirement" value="REQ-&quot;&lt;&amp;" />
                  </properties>
                </testcase>
                <testcase classname="tia.plcsimScenario" name="failed" time="1.830">
                  <properties>
                    <property name="title" value="Assert" />
                  </properties>
                  <failure type="AssertionFailed" message="expected &lt;true&gt; &amp; &quot;on&quot;">step 2 assert "Running": expected true, actual false</failure>
                </testcase>
                <testcase classname="tia.plcsimScenario" name="skipped" time="0.000">
                  <properties>
                    <property name="title" value="Skipped" />
                  </properties>
                  <skipped message="NOT_EXECUTED: unknown earlier outcome" />
                </testcase>
              </testsuite>
              <testsuite name="testSuite" tests="2" failures="0" errors="2" skipped="0" time="0.000" timestamp="2026-11-17T08:30:00Z">
                <properties>
                  <property name="runId" value="20261117-083000-1a2b3c4d" />
                  <property name="releaseKey" value="21" />
                  <property name="hostVersion" value="4.0.0.0" />
                  <property name="planHash" value="hash&amp;&lt;&quot;" />
                  <property name="status" value="completed" />
                </properties>
                <testcase classname="tia.testSuite" name="error" time="0.000">
                  <properties>
                    <property name="title" value="Licence" />
                  </properties>
                  <error type="LICENSE_MISSING" message="no licence">no licence</error>
                </testcase>
                <testcase classname="tia.testSuite" name="unknown" time="0.000">
                  <properties>
                    <property name="title" value="Unknown" />
                  </properties>
                  <error type="OUTCOME_UNKNOWN" message="outcome unknown">outcome unknown</error>
                </testcase>
              </testsuite>
            </testsuites>
            """;
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            string xml = PlcTestJUnitWriter.Write(run);
            Assert.Equal(golden.Replace("\r\n", "\n") + "\n", xml);
            Assert.Equal("5", XDocument.Parse(xml).Root!.Attribute("tests")!.Value);
            Assert.DoesNotContain(Environment.MachineName, xml);
            Assert.DoesNotContain(Environment.UserName, xml);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("Warning")]
    public void JUnit_suite_failures_include_only_top_level_error_and_warning_messages(string state)
    {
        var run = Run("20261117-083000-1a2b3c4d");
        run.Cases.Add(new() { Id = "suite", Kind = "testSuite", Verdict = "failed", Evidence = new JsonObject {
            ["result"] = new JsonObject { ["state"] = state, ["messages"] = JsonNode.Parse("""
                [{"path":"TC_A","state":"Error","description":"failed < &","messages":[{"state":"Error","description":"nested"}]},
                {"path":"TC_B","state":"Warning","description":"warning"},{"path":"TC_C","state":"Information","description":"info"}]
                """) }
        } });
        var failure = XDocument.Parse(PlcTestJUnitWriter.Write(run)).Descendants("failure").Single();
        Assert.Equal("TestSuite." + state, failure.Attribute("type")!.Value);
        Assert.Equal("TC_A: failed < &\nTC_B: warning", failure.Value);
    }

    private static PlcTestRunRecord Run(string id) => new() {
        RunId = id, StartedUtc = DateTimeOffset.Parse("2026-11-17T08:30:00Z", CultureInfo.InvariantCulture),
        ReleaseKey = "21", HostVersion = "4.0.0.0", Status = "completed"
    };

    private sealed class LibraryDiscoveryFixture : IDisposable
    {
        private readonly HardwareAddressingAdmissionTests.UnboundWorker worker = new();
        private readonly toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline pipeline;
        private readonly string previous = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        internal IReadOnlyList<ModelContextProtocol.Server.McpServerTool> Tools => pipeline.Tools.ToArray();
        internal LibraryDiscoveryFixture(string release)
        {
            toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
            IReadOnlyList<ModelContextProtocol.Server.McpServerTool>? roster = null;
            // Production gives GetToolUsage the merged host catalog, including host-only F01 tools.
            var shared = TiaMcp.FoundationHost.LegacyHostToolRegistry.Create(worker, release, false, usageRoster: () => roster!,
                exampleProfile: name => TiaOpenness.Shared.ToolUsageCatalog.ExampleSource(name, release), hostPorts: false);
            pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, shared);
            roster = Tools;
        }
        internal async Task<JsonNode> Call(string name, object args)
        {
            var result = await ToolHintOnceTests.Invoke(Tools.Single(t => t.ProtocolTool.Name == name), args);
            if (result["data"]?["export"]?["id"] is not JsonNode id) return result;
            var text = new StringBuilder((string?)result["data"]?["text"]);
            int? next = (int?)result["meta"]?["paging"]?["nextOffset"];
            while (next != null)
            {
                var page = await ToolHintOnceTests.Invoke(Tools.Single(t => t.ProtocolTool.Name == "GetExportContent"),
                    new { exportId = (string)id!, offset = next.Value, limit = 20000 });
                text.Append((string?)page["data"]?["text"]);
                next = (int?)page["meta"]?["paging"]?["nextOffset"];
            }
            return JsonNode.Parse(text.ToString())!;
        }
        public void Dispose()
        {
            pipeline.Dispose(); worker.Dispose();
            toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previous;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
