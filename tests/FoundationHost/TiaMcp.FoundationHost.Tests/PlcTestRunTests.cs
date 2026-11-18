extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.Testing;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;
using Tools = toolpipeline::TiaMcpServer.ModelContextProtocol.PlcTestLibraryTools;
using Simulator = toolpipeline::TiaMcpServer.Siemens.Services.PlcSimAdvancedPortService;
using Suite = toolpipeline::TiaMcp.Shared.TestSuitePortService;
using Engine = toolpipeline::TiaMcp.FoundationHost;
using Audit = TiaOpenness.Shared.AuditInvocation;
using Pending = TiaOpenness.Shared.PendingApproval;
using Settings = TiaOpenness.Shared.ApprovalSettings;
using Approval = TiaOpenness.Shared.ApprovalOutcome;

// Synthetic values use the real PlcSimAdvancedAdapter and TestSuiteAdapter reply fields and nesting.
[Collection(BundleSelectionCollection.Name)]
public sealed class PlcTestRunTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_unknown_outcomes_stop_finalize_and_mark_the_session_unsettled(bool suite)
    {
        using var fixture = new Fixture(bound: suite); fixture.Save("a", suite: suite); fixture.Save("b");
        fixture.Reply = _ => {
            var reply = suite ? SuiteReply("Success") : ScenarioReply("passed");
            if (suite) { reply["Meta"]!["writeOutcomeUnknown"] = true; reply["Meta"]!["mayHaveChanged"] = true;
                reply["Meta"]!["operationSuccess"] = false; }
            else reply["Data"]!["operatingModeRestoreError"] = "Synthetic restore failed at C:\\private\\mode.cs\n   at Native.Restore()";
            return reply;
        };
        var result = await fixture.Execute();
        Assert.Equal("unknown", (string?)result["meta"]?["outcome"]); Assert.Equal(1, fixture.Calls);
        var run = fixture.Read(); Assert.Equal("stopped", run.Status); Assert.NotNull(run.FinishedUtc);
        Assert.Equal(new[] { "unknown", "skipped" }, run.Cases.Select(c => c.Verdict));
        Assert.StartsWith("NOT_EXECUTED:", run.Cases[1].Message);
        Assert.Single(fixture.Worker.Unsettled.Snapshot());
        if (!suite) Assert.Contains("restore failed", (string)run.Cases[0].Evidence["message"]!);
        Assert.True(File.Exists((string)result["data"]!["junitFile"]!));
    }

    [Theory]
    [InlineData("Error", "writeOutcomeUnknown")]
    [InlineData("Warning", "connectionUnavailable")]
    [InlineData("Success", "missing-result")]
    public async Task Test_suite_uncertainty_overrides_returned_verdict_and_stops_the_batch(string state, string reason)
    {
        using var fixture = new Fixture(bound: true); fixture.Save("a", suite: true); fixture.Save("b");
        fixture.Reply = _ => {
            var reply = SuiteReply(state);
            reply["Meta"]![reason == "missing-result" ? "nativeResultReturned" : reason] = reason != "missing-result";
            return reply;
        };
        var result = await fixture.Execute(); var run = fixture.Read();
        Assert.Equal("unknown", (string?)result["meta"]?["outcome"]);
        Assert.Equal("stopped", run.Status); Assert.NotNull(run.FinishedUtc); Assert.Equal(1, fixture.Calls);
        Assert.Equal(new[] { "unknown", "skipped" }, run.Cases.Select(c => c.Verdict));
        Assert.StartsWith("NOT_EXECUTED:", run.Cases[1].Message);
        Assert.Single(fixture.Worker.Unsettled.Snapshot());
        Assert.True(File.Exists((string)result["data"]!["junitFile"]!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_exception_strings_are_normalized_without_paths_or_stack_traces(bool suite)
    {
        using var fixture = new Fixture(bound: suite); fixture.Save("a", suite: suite);
        fixture.Reply = _ => new JsonObject { ["Ok"] = false, ["Message"] = "Synthetic native failure", ["Data"] = new JsonObject(),
            ["Meta"] = new JsonObject { ["success"] = false, ["operationSuccess"] = false, ["apiCallSuccess"] = false,
                ["mayHaveChanged"] = false, ["operationNotStarted"] = true, ["status"] = suite ? "ReadOrWriteFailed" : "ApiNotFound",
                ["error"] = "System.InvalidOperationException: license missing at C:\\private\\project.ap21; inspect the license\n"
                    + "   at Native.Run() in D:\\src\\native.cs:line 58" } };
        var result = await fixture.Execute();
        var run = fixture.Read(); var item = Assert.Single(run.Cases);
        Assert.Equal("error", item.Verdict);
        Assert.Equal(new[] { "code", "message", "details" }, item.Evidence.Select(p => p.Key));
        Assert.Contains("license missing", (string)item.Evidence["message"]!);
        Assert.Contains("project.ap21; inspect the license", (string)item.Evidence["message"]!);
        Assert.DoesNotContain(":\\", run.CanonicalJson); Assert.DoesNotContain("at Native.Run", run.CanonicalJson);
        Assert.True(File.Exists((string)result["data"]!["junitFile"]!));
        Assert.Single(XDocument.Load((string)result["data"]!["junitFile"]!).Descendants("error"));
    }

    [Theory]
    [InlineData(false, "cancel")]
    [InlineData(false, "timeout")]
    [InlineData(false, "worker")]
    [InlineData(false, "missing")]
    [InlineData(true, "cancel")]
    [InlineData(true, "timeout")]
    [InlineData(true, "worker")]
    [InlineData(true, "missing")]
    [InlineData(false, "busy")]
    [InlineData(true, "busy")]
    [InlineData(false, "not-sent")]
    [InlineData(true, "not-sent")]
    public async Task Case_call_exceptions_always_finalize_and_stop_with_the_shared_outcome(bool suite, string failure)
    {
        using var fixture = new Fixture(bound: suite); fixture.Save("a", suite: suite); fixture.Save("b");
        fixture.Reply = _ => failure switch {
            "cancel" => throw new OperationCanceledException("Synthetic cancellation"),
            "timeout" => throw new TimeoutException("Synthetic timeout"),
            "worker" => throw new WorkerOperationException("Synthetic lost write", -32603, "unknown"),
            "busy" => throw new AdapterPreconditionException("Synthetic busy refusal", "worker-call-running", null),
            "not-sent" => throw NotSent(),
            _ => null!
        };
        var result = await fixture.Execute(); var run = fixture.Read();
        Assert.Equal("stopped", run.Status); Assert.NotNull(run.FinishedUtc); Assert.Equal(1, fixture.Calls);
        bool refused = failure is "busy" or "not-sent";
        Assert.Equal(refused ? "skipped" : "unknown", run.Cases[0].Verdict);
        Assert.Equal("skipped", run.Cases[1].Verdict);
        Assert.Equal(refused ? "rejected-before-operation" : "unknown", (string?)result["meta"]?["outcome"]);
        Assert.Equal(refused ? 0 : 1, fixture.Worker.Unsettled.Snapshot().Count);
        Assert.True(File.Exists((string)result["data"]!["junitFile"]!));
    }

    private static Exception NotSent()
    {
        var error = new OperationCanceledException("Synthetic cancellation before dispatch");
        error.Data["foundationRequestSent"] = false;
        return error;
    }

    [Fact]
    public async Task Busy_refusal_after_a_completed_case_is_partial_without_an_unknown_session()
    {
        using var fixture = new Fixture(); fixture.Save("a"); fixture.Save("b"); fixture.Save("c");
        fixture.Reply = call => call == 1 ? ScenarioReply("passed")
            : throw new AdapterPreconditionException("Synthetic busy refusal", "worker-call-running", null);
        var result = await fixture.Execute();
        Assert.Equal("partial", (string?)result["meta"]?["outcome"]);
        Assert.Equal("stopped", fixture.Read().Status); Assert.Equal(2, fixture.Calls);
        Assert.Equal(new[] { "passed", "skipped", "skipped" }, fixture.Read().Cases.Select(c => c.Verdict));
        Assert.Empty(fixture.Worker.Unsettled.Snapshot());
        Assert.True(File.Exists((string)result["data"]!["junitFile"]!));
    }

    [Fact]
    public async Task Running_late_call_is_refused_before_any_approval()
    {
        using var fixture = new Fixture(); fixture.Save("a"); string hash = await fixture.Preview();
        using var running = Engine.LateCalls.For(fixture.Worker).CreateInvocation("SaveProject", "owned-late-fixture", CancellationToken.None);
        using (running.Enter()) Engine.LateCalls.Detach(new JsonObject { ["requestId"] = "owned-late-fixture", ["tool"] = "SaveProject" });
        var result = await fixture.Call(new { dryRun = false, expectedPlanHash = hash });
        Assert.Equal("PRECONDITION_FAILED", (string?)result["error"]?["code"]);
        Assert.Equal("worker-call-running", (string?)result["error"]?["details"]?["condition"]);
        Assert.Equal(0, fixture.Approvals); Assert.Equal(0, fixture.Calls); Assert.False(Directory.Exists(fixture.Runs));
    }

    [Fact]
    public async Task Invalid_unnamed_files_are_plan_notes_but_named_invalid_ids_are_refused()
    {
        using var fixture = new Fixture(); fixture.Save("a");
        File.WriteAllText(Path.Combine(fixture.Library, "cases", "broken.json"), "{broken");
        foreach (var arguments in new object[] { new { }, new { tags = new[] { "fat" } } })
        {
            var preview = await fixture.Call(arguments); Assert.True((bool?)preview["ok"], preview.ToJsonString());
            Assert.Equal("broken", (string?)Assert.Single(preview["data"]!["plan"]!["skippedFiles"]!.AsArray())?["id"]);
        }
        var result = await fixture.Execute(); Assert.Equal(1, fixture.Calls); Assert.True((bool?)result["ok"]);
        var rejected = await fixture.Call(new { ids = new[] { "broken" }, expectedPlanHash = "caller-error" });
        Assert.Equal("INVALID_ARGUMENT", (string?)rejected["error"]?["code"]);
        var caller = await fixture.Call(new { tags = new[] { "" }, dryRun = false, expectedPlanHash = "caller-error" });
        Assert.Equal("INVALID_ARGUMENT", (string?)caller["error"]?["code"]);
    }

    [Fact]
    public async Task Native_unsupported_rejection_is_skipped_without_blocking_later_cases()
    {
        using var fixture = new Fixture(bound: true); fixture.Save("a", suite: true); fixture.Save("b");
        fixture.Reply = call => call == 1 ? new JsonObject { ["Message"] = "Synthetic unsupported selection", ["Meta"] = new JsonObject {
            ["rejectionCode"] = "UNSUPPORTED_CAPABILITY", ["operationNotStarted"] = true, ["mayHaveChanged"] = false } } : ScenarioReply("passed");
        var result = await fixture.Execute(); Assert.True((bool?)result["ok"], result.ToJsonString());
        Assert.Equal(new[] { "skipped", "passed" }, fixture.Read().Cases.Select(c => c.Verdict));
        Assert.Equal(4, (int)result["data"]!["exitCode"]!);
    }

    [Theory]
    [InlineData("Success", "succeeded", "passed")]
    [InlineData("Information", "succeeded", "passed")]
    [InlineData("Warning", "failed", "failed")]
    [InlineData("Error", "failed", "failed")]
    public async Task Native_test_suite_result_flags_follow_the_reused_tool_outcome(string state, string outcome, string verdict)
    {
        using var fixture = new Fixture(bound: true); fixture.Save("a", suite: true); fixture.Save("b");
        var reply = SuiteReply(state);
        fixture.Reply = call => call == 1 ? reply.DeepClone().AsObject() : ScenarioReply("passed");
        JsonNode expected;
        using (fixture.Pipeline.EnterSession())
            expected = toolpipeline::TiaMcpServer.ModelContextProtocol.OptionalPackageContract.Map("RunTestSuiteCase",
                new toolpipeline::TiaMcpServer.ModelContextProtocol.ResponseMessage {
                    Message = (string?)reply["Message"], Meta = reply["Meta"]!.DeepClone().AsObject() }, true).StructuredContent!;
        var result = await fixture.Execute();
        Assert.Equal(outcome, (string?)expected["meta"]?["outcome"]);
        Assert.Equal("completed", (string?)expected["meta"]?["execution"]);
        Assert.Equal("succeeded", (string?)result["meta"]?["outcome"]);
        var run = fixture.Read();
        Assert.Equal("completed", run.Status); Assert.Equal(2, fixture.Calls);
        Assert.Equal(new[] { verdict, "passed" }, run.Cases.Select(c => c.Verdict));
        Assert.Equal(verdict == "failed" ? 3 : 0, (int)result["data"]!["exitCode"]!);
        Assert.Empty(fixture.Worker.Unsettled.Snapshot());
        Assert.Equal(verdict == "failed" ? 1 : 0, XDocument.Load((string)result["data"]!["junitFile"]!).Descendants("failure").Count());
    }

    [Fact]
    public async Task Plcsim_api_evidence_keeps_the_filename_and_diagnostic_suffix_under_details()
    {
        using var fixture = new Fixture(); fixture.Save("a");
        fixture.Reply = _ => {
            var reply = ScenarioReply("passed");
            reply["Data"]!["api"] = new JsonObject {
                ["apiPath"] = @"C:\private\Siemens.Simatic.Simulation.Runtime.Api.x64.dll",
                ["apiSource"] = @"Loaded C:\private\runtime.dll successfully; API version 8",
                ["apiVersion"] = "8", ["assemblyVersion"] = "8.0.0.0" };
            return reply;
        };
        var result = await fixture.Execute(); var evidence = fixture.Read().Cases[0].Evidence;
        Assert.Equal("Siemens.Simatic.Simulation.Runtime.Api.x64.dll", (string?)evidence["details"]?["api"]?["apiPath"]);
        Assert.Equal("Loaded runtime.dll successfully; API version 8", (string?)evidence["details"]?["api"]?["apiSource"]);
        Assert.DoesNotContain("private", fixture.Read().CanonicalJson);
        Assert.True(File.Exists((string)result["data"]!["junitFile"]!));
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
    public void Batch_runner_is_a_host_tool_on_every_release(string release)
    {
        Assert.True(PortedFamilies.Contains("RunPlcTestLibrary") && PortedFamilies.Available(release, "RunPlcTestLibrary"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_is_local_and_contains_an_ordered_hash_bound_plan(bool suite)
    {
        using var fixture = new Fixture();
        fixture.Save("b", suite: suite); fixture.Save("a");
        var result = await fixture.Call(new { tags = new[] { "FAT" } });
        Assert.True((bool?)result["ok"], result.ToJsonString());
        Assert.NotEmpty((string)result["data"]!["planHash"]!);
        Assert.Equal(new[] { "a", "b" }, result["data"]!["plan"]!["cases"]!.AsArray().Select(e => (string)e!["id"]!));
        Assert.True((bool?)result["data"]!["plan"]!["cases"]![0]!["runnable"]);
        Assert.Equal(1, (int)result["data"]!["plan"]!["cases"]![0]!["plan"]!["stepCount"]!);
        Assert.Equal(0, fixture.Calls); Assert.Equal(0, fixture.Worker.StatusCalls);
        Assert.False(Directory.Exists(fixture.Runs));
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("malformed")]
    [InlineData("delete")]
    [InlineData("add")]
    [InlineData("whitespace")]
    public async Task Changed_case_files_refuse_the_preview_before_any_execution(string change)
    {
        using var fixture = new Fixture(); fixture.Save("a");
        string hash = await fixture.Preview();
        string path = Path.Combine(fixture.Library, "cases", "a.json");
        if (change == "delete") File.Delete(path);
        else if (change == "malformed") File.WriteAllText(path, "{");
        else if (change == "whitespace") File.AppendAllText(path, "\n");
        else fixture.Save(change == "add" ? "b" : "a", title: "changed");
        var result = await fixture.Call(new { dryRun = false, expectedPlanHash = hash });
        Assert.Equal(change is "delete" or "malformed" ? "INVALID_ARGUMENT" : "PLAN_STALE", (string?)result["error"]?["code"]);
        Assert.Equal(0, fixture.Calls); Assert.False(Directory.Exists(fixture.Runs));
    }

    [Fact]
    public async Task Scenario_execution_requires_a_nonempty_plan_hash()
    {
        using var fixture = new Fixture(); fixture.Save("a");
        var result = await fixture.Call(new { dryRun = false });
        Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]?["code"]);
        Assert.Equal(0, fixture.Calls);
    }

    [Theory]
    [InlineData(false, false, "INVALID_ARGUMENT")]
    [InlineData(false, true, "PROJECT_NOT_BOUND")]
    [InlineData(true, false, "INVALID_ARGUMENT")]
    public async Task Runnable_test_suite_requires_project_binding_and_expected_file(bool bound, bool expected, string code)
    {
        using var fixture = new Fixture(bound: bound); fixture.Save("suite", suite: true);
        string hash = await fixture.Preview();
        var result = await fixture.Call(new { dryRun = false, expectedPlanHash = hash, expectedProjectFile = expected ? Fixture.Project : "" });
        Assert.Equal(code, (string?)result["error"]?["code"]); Assert.Equal(0, fixture.Calls);
    }

    [Fact]
    public async Task One_approval_covers_three_cases_and_history_binds_the_audit_request()
    {
        using var fixture = new Fixture(); fixture.Save("a"); fixture.Save("b"); fixture.Save("c");
        var result = await fixture.Execute();
        Assert.True((bool?)result["ok"], result.ToJsonString());
        Assert.Equal(1, fixture.Approvals); Assert.Equal(3, fixture.Calls);
        Assert.Equal("succeeded", (string?)result["meta"]?["outcome"]);
        var run = fixture.Read();
        Assert.Equal((string?)result["meta"]?["requestId"], (string?)run.Audit["requestId"]);
        Assert.Equal((string?)result["data"]?["planHash"], run.PlanHash);
        Assert.Null(run.Project); Assert.Equal(0, run.ExitCode);
        Assert.All(fixture.Requests, r => {
            Assert.Equal("RunPlcSimAdvancedTestScenario", (string?)r["request"]?["operation"]);
            Assert.True((bool?)r["request"]?["arguments"]?["confirmRun"]);
            Assert.False((bool?)r["request"]?["arguments"]?["dryRun"]);
        });
        string file = (string)result["data"]!["junitFile"]!;
        Assert.Equal(PlcTestJUnitWriter.Write(run), File.ReadAllText(file));
        Assert.Equal(3, XDocument.Load(file).Descendants("testcase").Count());
        Assert.All(result["data"]!["cases"]!.AsArray(), c => Assert.Null(c!["evidence"]));
    }

    [Fact]
    public async Task Denied_approval_runs_nothing_and_writes_no_history()
    {
        using var fixture = new Fixture(); fixture.Save("a"); fixture.Deny = true;
        var result = await fixture.Execute();
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)result["error"]?["code"]);
        Assert.Equal(1, fixture.Approvals); Assert.Equal(0, fixture.Calls); Assert.False(Directory.Exists(fixture.Runs));
    }

    [Fact]
    public async Task Default_services_use_the_current_host_session_without_nested_tool_approval()
    {
        using var fixture = new Fixture(bound: true, defaultServices: true);
        fixture.Save("a"); fixture.Save("suite", suite: true);
        fixture.Reply = call => call == 1 ? ScenarioReply("passed") : SuiteReply("Success");
        var result = await fixture.Execute();
        Assert.True((bool?)result["ok"], result.ToJsonString());
        Assert.Equal(2, fixture.Calls); Assert.Equal(1, fixture.Approvals);
        Assert.Equal(new[] { "passed", "passed" }, fixture.Read().Cases.Select(c => c.Verdict));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("error")]
    public async Task Failed_or_error_case_does_not_stop_later_cases(string verdict)
    {
        using var fixture = new Fixture(); fixture.Save("a"); fixture.Save("b");
        fixture.Reply = call => ScenarioReply(call == 1 ? verdict : "passed");
        var result = await fixture.Execute();
        Assert.True((bool?)result["ok"], result.ToJsonString());
        Assert.Equal(2, fixture.Calls); Assert.Equal(3, (int)result["data"]!["exitCode"]!);
        Assert.Equal("succeeded", (string?)result["meta"]?["outcome"]);
        Assert.Empty(fixture.Worker.Unsettled.Snapshot());
        var run = fixture.Read();
        Assert.Equal(new[] { verdict, "passed" }, run.Cases.Select(c => c.Verdict));
        Assert.Equal("completed", run.Status);
        Assert.True(JsonNode.DeepEquals(ScenarioReply(verdict)["Data"]!["steps"], run.Cases[0].Evidence["details"]!["steps"]));
        Assert.Equal(new[] { "code", "message", "details" }, run.Cases[0].Evidence.Select(p => p.Key));
        Assert.NotEmpty(run.Cases[0].Message);
    }

    [Fact]
    public async Task Unknown_case_stops_without_replaying_and_skips_the_remaining_cases()
    {
        using var fixture = new Fixture(); fixture.Save("a"); fixture.Save("b"); fixture.Save("c");
        fixture.Reply = _ => ScenarioReply("unknown");
        var result = await fixture.Execute();
        Assert.Equal("unknown", (string?)result["meta"]?["outcome"]);
        Assert.Equal(1, fixture.Calls); Assert.Equal(5, (int)result["data"]!["exitCode"]!);
        var run = fixture.Read(); Assert.Equal("stopped", run.Status);
        Assert.Equal(new[] { "unknown", "skipped", "skipped" }, run.Cases.Select(c => c.Verdict));
        Assert.All(run.Cases.Skip(1), c => Assert.StartsWith("NOT_EXECUTED:", c.Message));
        Assert.True(File.Exists((string)result["data"]!["junitFile"]!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_before_execution_is_an_error_and_the_next_case_runs(bool suite)
    {
        using var fixture = new Fixture(bound: suite); fixture.Save("a", suite: suite); fixture.Save("b");
        fixture.Reply = call => call == 2 ? ScenarioReply("passed") : suite
            ? new JsonObject { ["Message"] = "Synthetic LicenseNotFoundException", ["Meta"] = new JsonObject {
                ["apiCallSuccess"] = false, ["operationSuccess"] = false, ["operationNotStarted"] = true,
                ["mayHaveChanged"] = false, ["errorCode"] = "NATIVE_OPERATION_FAILED" } }
            : new JsonObject { ["Ok"] = false, ["Message"] = "Synthetic API missing", ["Data"] = new JsonObject(),
                ["Meta"] = new JsonObject { ["operationSuccess"] = false, ["success"] = false, ["status"] = "ApiNotFound" } };
        var result = await fixture.Execute();
        Assert.True((bool?)result["ok"], result.ToJsonString()); Assert.Equal(2, fixture.Calls);
        var run = fixture.Read(); Assert.Equal(3, run.ExitCode);
        Assert.Equal(new[] { "error", "passed" }, run.Cases.Select(c => c.Verdict));
        Assert.DoesNotContain("NOT_EXECUTED", run.Cases[0].Message);
    }

    [Fact]
    public async Task Test_suite_run_all_keeps_the_existing_service_selection_arguments()
    {
        using var fixture = new Fixture(bound: true); fixture.Save("suite", suite: true);
        var library = new PlcTestLibrary(fixture.Library);
        var document = library.Read("suite")["testCase"]!.AsObject();
        document["testSuite"]!.AsObject().Remove("names"); document["testSuite"]!["runAll"] = true;
        library.Save(PlcTestCaseDocument.Parse(document.ToJsonString()), overwrite: true);
        fixture.Reply = _ => SuiteReply("Success");
        var result = await fixture.Execute(); Assert.True((bool?)result["ok"], result.ToJsonString());
        var request = Assert.Single(fixture.Requests);
        Assert.Equal("[]", (string?)request["request"]?["NamesJson"]);
        Assert.True((bool?)request["request"]?["RunAll"]);
    }

    [Theory]
    [InlineData("14sp1", "application", "case")]
    [InlineData("15.1", "application", "case")]
    [InlineData("16", "application", "case")]
    [InlineData("17", "system", "case")]
    [InlineData("17", "application", "testSet")]
    [InlineData("18", "application", "testSet")]
    [InlineData("19", "application", "testSet")]
    public async Task Unsupported_entries_are_skipped_locally_but_an_entirely_unsupported_run_is_refused(string release, string category, string kind)
    {
        using var fixture = new Fixture(release); fixture.Save("suite", suite: true, category: category, kind: kind);
        var refused = await fixture.Call(new { });
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)refused["error"]?["code"]);
        Assert.Equal(0, fixture.Calls);
        fixture.Save("scenario");
        var preview = await fixture.Call(new { });
        var skipped = preview["data"]!["plan"]!["cases"]!.AsArray().Single(e => (string?)e?["id"] == "suite")!;
        Assert.False((bool?)skipped["runnable"]); Assert.Equal("skipped", (string?)skipped["verdict"]);
        var result = await fixture.Execute();
        Assert.True((bool?)result["ok"], result.ToJsonString());
        Assert.Equal(1, fixture.Calls); Assert.Equal(4, (int)result["data"]!["exitCode"]!);
        Assert.StartsWith("UNSUPPORTED_CAPABILITY:", fixture.Read().Cases.Single(c => c.Id == "suite").Message);
        Assert.Null(fixture.Read().Project);
    }

    [Theory]
    [InlineData("17", "application", "case", "Success", "passed")]
    [InlineData("18", "system", "case", "Information", "passed")]
    [InlineData("19", "styleGuide", "case", "Warning", "failed")]
    [InlineData("20", "application", "testSet", "Error", "failed")]
    [InlineData("21", "application", "testSet", "Success", "passed")]
    public async Task Supported_test_suite_calls_keep_service_arguments_and_native_verdicts(string release, string category, string kind,
        string state, string verdict)
    {
        using var fixture = new Fixture(release, bound: true); fixture.Save("suite", suite: true, category: category, kind: kind);
        fixture.Reply = _ => SuiteReply(state);
        var result = await fixture.Execute();
        Assert.True((bool?)result["ok"], result.ToJsonString()); Assert.Equal(1, fixture.Calls);
        var request = Assert.Single(fixture.Requests);
        Assert.Equal("RunTestSuiteCase", (string?)request["request"]?["Operation"]);
        Assert.Equal(category, (string?)request["request"]?["Category"]);
        Assert.Equal(kind, (string?)request["request"]?["Kind"]);
        Assert.True((bool?)request["request"]?["ConfirmExternalExecution"]);
        Assert.Equal("[\"TC_A\",\"TC_B\"]", (string?)request["request"]?["NamesJson"]);
        Assert.False((bool?)request["request"]?["DryRun"]);
        Assert.Equal(Fixture.Project, (string?)request["expectedProjectFile"]);
        var run = fixture.Read(); Assert.Equal(verdict, Assert.Single(run.Cases).Verdict);
        Assert.Equal(Path.GetFileName(Fixture.Project), (string?)run.Project?["file"]);
        Assert.True(JsonNode.DeepEquals(SuiteReply(state)["Meta"]!["result"], run.Cases[0].Evidence["details"]!["result"]),
            "Expected: " + SuiteReply(state)["Meta"]!["result"] + "\nActual: " + run.Cases[0].Evidence["details"]!["result"]);
        Assert.Equal(new[] { "code", "message", "details" }, run.Cases[0].Evidence.Select(p => p.Key));
    }

    [Fact]
    public async Task Interrupted_second_reply_preserves_the_checkpoint_and_finalizes_a_stopped_record()
    {
        using var fixture = new Fixture(); fixture.Save("a"); fixture.Save("b");
        fixture.Reply = call => {
            var persisted = fixture.Read(); Assert.Equal("running", persisted.Status);
            Assert.Equal(call == 1 ? "skipped" : "passed", persisted.Cases[0].Verdict);
            if (call == 2) throw new InvalidOperationException("Synthetic host crash before the second reply.");
            return ScenarioReply("passed");
        };
        var result = await fixture.Execute();
        Assert.False((bool?)result["ok"]);
        var run = fixture.Read(); Assert.Equal("stopped", run.Status); Assert.Equal(5, run.ExitCode);
        Assert.Equal("passed", run.Cases[0].Verdict); Assert.NotNull(run.FinishedUtc);
        Assert.Equal("unknown", run.Cases[1].Verdict);
        Assert.True(File.Exists((string)result["data"]!["junitFile"]!));
        using var scope = fixture.Pipeline.EnterSession();
        var history = fixture.Tools.ManagePlcTestLibrary("readRun").StructuredContent!;
        Assert.Equal(5, (int)history["data"]!["run"]!["exitCode"]!);
    }

    [Fact]
    public async Task Empty_or_invalid_selection_is_refused_without_history_or_worker_calls()
    {
        using var fixture = new Fixture();
        var empty = await fixture.Call(new { }); Assert.Equal("INVALID_ARGUMENT", (string?)empty["error"]?["code"]);
        fixture.Save("a");
        var filtered = await fixture.Call(new { tags = new[] { "absent" } });
        Assert.Equal("INVALID_ARGUMENT", (string?)filtered["error"]?["code"]);
        Assert.Equal(0, fixture.Calls); Assert.False(Directory.Exists(fixture.Runs));
    }

    // Reports consume a persisted record from the real runner, not an independently authored evidence format.
    internal static PlcTestRunRecord ReportRecord(PlcTestCaseDocument document, JsonObject reply)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "report-run-record-" + Guid.NewGuid().ToString("N"));
        using var worker = new Worker(document.Kind == "testSuite");
        var registry = LegacyHostToolRegistry.Create(worker, "21", false, hostPorts: false);
        using var pipeline = new Engine.EngineHostPipeline("21", worker, registry);
        using var scope = pipeline.EnterSession();
        var runner = new toolpipeline::TiaMcpServer.ModelContextProtocol.PlcTestLibraryRunner(
            new Simulator((_, _) => reply.DeepClone()), new Suite((_, _) => reply.DeepClone(), () => Fixture.Project));
        try
        {
            runner.Execute(new[] { new toolpipeline::TiaMcpServer.ModelContextProtocol.PlcTestLibraryRunner.Entry {
                Case = document, Hash = document.Sha256 } }, root, "21", "plan-hash", Fixture.Project,
                new[] { document.Id }, Array.Empty<string>());
            return PlcTestRunRecord.Read(root, "latest");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    internal static JsonObject ScenarioReply(string verdict)
    {
        bool passed = verdict == "passed";
        var step = new JsonObject { ["index"] = 1, ["kind"] = verdict == "error" ? "wait" : verdict == "unknown" ? "write" : "assert",
            ["ok"] = passed };
        if (verdict == "error") { step["waitMs"] = 1; step["error"] = "Synthetic wait failure"; }
        else if (verdict == "unknown")
        { step["written"] = new JsonArray(); step["error"] = "Synthetic write outcome unknown"; step["outcomeUnknown"] = true; }
        else step["checks"] = new JsonArray(new JsonObject { ["name"] = "\"Running\"", ["type"] = "Bool",
            ["expected"] = true, ["actual"] = passed, ["ok"] = passed });
        return new JsonObject { ["Ok"] = passed, ["Message"] = "Synthetic scenario " + verdict,
            ["Data"] = new JsonObject { ["verdict"] = passed ? "passed" : "failed", ["executed"] = true,
                ["executedSteps"] = 1, ["assertionsPassed"] = passed ? 1 : 0, ["failedSteps"] = passed ? 0 : 1,
                ["steps"] = new JsonArray(step) },
            ["Meta"] = new JsonObject { ["success"] = passed, ["operationSuccess"] = passed, ["outcomeUnknown"] = verdict == "unknown" } };
    }

    internal static JsonObject SuiteReply(string state) => new() { ["Message"] = "Synthetic TestResults",
        ["Meta"] = new JsonObject { ["success"] = true, ["apiCallSuccess"] = true,
            ["testPassed"] = state is "Success" or "Information", ["operationSuccess"] = true,
            ["operationStarted"] = true, ["nativeResultReturned"] = true, ["nativeState"] = state,
            ["nativeStateType"] = NativeResultStates.TestSuite, ["mayHaveChanged"] = true,
            ["result"] = new JsonObject { ["state"] = state, ["errorCount"] = state == "Error" ? 1 : 0,
                ["warningCount"] = state == "Warning" ? 1 : 0, ["messageCount"] = 1,
                ["messages"] = new JsonArray(new JsonObject { ["path"] = "TC_A", ["state"] = state,
                    ["description"] = "Synthetic native result", ["dateTimeUtc"] = "2026-11-17T08:30:00.0000000Z",
                    ["errorCount"] = 0, ["warningCount"] = 0, ["messages"] = new JsonArray() }) } } };

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(AppContext.BaseDirectory, "plc-run-" + Guid.NewGuid().ToString("N"));
        private readonly string previousRelease = Engine.EngineHostConfiguration.ReleaseKey;
        private readonly Func<Pending, Settings, CancellationToken, Task<Approval>>? previousApproval = Host.ApprovalWaitOverrideForTests;
        internal const string Project = "D:\\Fixture\\Line.ap21";
        internal string Library => Path.Combine(root, "test-library");
        internal string Runs => Path.Combine(root, "test-runs");
        internal readonly Worker Worker;
        internal readonly Engine.EngineHostPipeline Pipeline;
        internal readonly Tools Tools;
        internal readonly List<JsonObject> Requests = new();
        internal Func<int, JsonObject> Reply = _ => ScenarioReply("passed");
        internal int Calls, Approvals;
        internal bool Deny;
        internal Fixture(string release = "21", bool bound = false, bool defaultServices = false)
        {
            Worker = new Worker(bound) { RawCall = CallWorker };
            Tools = defaultServices ? new Tools(() => Library, () => Runs)
                : new Tools(() => Library, () => Runs, new Simulator(CallWorker), new Suite(CallWorker, () => Project));
            var shared = LegacyHostToolRegistry.Create(Worker, release, false, hostPorts: false);
            Pipeline = new Engine.EngineHostPipeline(release, Worker, shared);
            Host.ApprovalWaitOverrideForTests = (pending, _, _) => {
                Approvals++; return Task.FromResult(new Approval(pending, false, Deny ? "denied" : null)); };
            using var scope = Pipeline.EnterSession();
            ((Engine.WorkerToolInvoker)Host.ToolInvoker).SetLocalTarget(typeof(Tools), Tools);
        }
        private JsonNode CallWorker(string operation, JsonObject args)
        {
            Assert.Contains(operation, new[] { "runtime-channels.ExecutePlcSimAdvanced", "optional-products.ExecuteOptionalProduct" });
            Requests.Add(args.DeepClone().AsObject()); return Reply(++Calls);
        }
        internal void Save(string id, bool suite = false, string category = "application", string kind = "case", string title = "Synthetic case")
        {
            var document = new JsonObject { ["schemaVersion"] = 1, ["id"] = id, ["title"] = title, ["tags"] = new JsonArray("fat"),
                ["kind"] = suite ? "testSuite" : "plcsimScenario" };
            if (suite) document["testSuite"] = new JsonObject { ["category"] = category, ["kind"] = kind,
                ["names"] = new JsonArray("TC_A", "TC_B"), ["runAll"] = false };
            else document["scenario"] = new JsonObject { ["instance"] = "OwnedFixture", ["mode"] = "default",
                ["steps"] = new JsonArray(new JsonObject { ["assert"] = new JsonObject { ["\"Running\""] = true } }) };
            new PlcTestLibrary(Library).Save(PlcTestCaseDocument.Parse(document.ToJsonString()), overwrite: true);
        }
        internal async Task<JsonNode> Call(object arguments)
        {
            var result = await ToolHintOnceTests.Invoke(Pipeline.Tools.Single(t => t.ProtocolTool.Name == "RunPlcTestLibrary"), arguments);
            Assert.Equal("RunPlcTestLibrary", (string?)result["meta"]?["tool"]);
            return result;
        }
        internal async Task<string> Preview() => (string)(await Call(new { }))["data"]!["planHash"]!;
        internal async Task<JsonNode> Execute()
        {
            string hash = await Preview();
            var log = new TiaOpenness.Shared.AuditLog(Path.Combine(root, "audit"));
            using var audit = Audit.UseLog(log);
            return await Call(new { dryRun = false, expectedPlanHash = hash, expectedProjectFile = Worker.Binding == null ? "" : Project });
        }
        internal PlcTestRunRecord Read() => PlcTestRunRecord.Read(Runs, "latest");
        public void Dispose()
        {
            Host.ApprovalWaitOverrideForTests = previousApproval; Pipeline.Dispose(); Worker.Dispose();
            Engine.EngineHostConfiguration.ReleaseKey = previousRelease;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Worker(bool bound) : IFoundationWorker, Engine.IEngineWorker,
        toolpipeline::TiaMcpServer.ModelContextProtocol.IHostToolServices
    {
        internal int StatusCalls;
        internal Func<string, JsonObject, JsonNode>? RawCall;
        public bool Faulted => false;
        public JsonNode? Binding => bound ? new JsonObject { ["projectFile"] = Fixture.Project } : null;
        public object SessionKey { get; } = new();
        public Engine.UnsettledCalls Unsettled => Engine.UnsettledCalls.For(this);
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = true } };
        public Task<JsonObject> Status(CancellationToken token) { StatusCalls++; return Task.FromResult(Snapshot()); }
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException("No worker restart expected.");
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new Lane());
        public Task<Engine.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
            => throw new InvalidOperationException("No engine dispatch expected.");
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
            => Task.FromResult<JsonNode?>(RawCall!(operation, arguments));
        public Task<JsonNode?> Observe(string operation, JsonObject arguments, CancellationToken token)
            => throw new InvalidOperationException("No host observation expected: " + operation);
        public void Dispose() { }
        private sealed class Lane : IDisposable { public void Dispose() { } }
    }
}
