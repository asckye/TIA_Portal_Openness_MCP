using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    internal static class ApprovalTestDataRoot
    {
        internal static string Root { get; private set; } = "";

        [ModuleInitializer]
        internal static void Initialize()
        {
            Root = Path.Combine(Path.GetTempPath(), "tia-engine-approval-tests-" + Guid.NewGuid().ToString("N"));
            var config = Path.Combine(Root, "config"); Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "approval.settings"), "enabled=true\ntimeoutSeconds=1\n", new UTF8Encoding(false));
            Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", Root);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { Directory.Delete(Root, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            };
        }
    }

    public sealed class ApprovalTests
    {
        public sealed class BatchGateProbe
        {
            [global::ModelContextProtocol.Server.McpServerTool(Name = "CreateApprovalFixture"), TiaMcpServer.ModelContextProtocol.ToolClassification("L2", "Meta", "WRITE", batchWrite: true)]
            public static global::ModelContextProtocol.Protocol.CallToolResult Write(string target, bool dryRun = true, string outcome = "success")
            {
                InfrastructureContractsTests.BatchProbes.Calls.Add(target + ":" + dryRun);
                if (!dryRun && outcome == "throw") throw new InvalidOperationException("private fixture input");
                if (!dryRun && outcome == "cancel") throw new OperationCanceledException("private fixture input");
                if (!dryRun && outcome == "refusal") return TiaMcpServer.ModelContextProtocol.McpServer.V4Reject("CreateApprovalFixture",
                    new Error("Fixture rejected.", new PreconditionFailedDetails("fixture", null)));
                if (!dryRun && outcome == "failure") return TiaMcpServer.ModelContextProtocol.McpServer.V4Result("CreateApprovalFixture", null,
                    new Error("Fixture failed.", new InternalErrorDetails(null)), Outcome.Failed, Execution.Completed, Completeness.None);
                if (!dryRun && outcome == "unknown") return TiaMcpServer.ModelContextProtocol.McpServer.TargetFailure("CreateApprovalFixture", new IOException("Fixture I/O interruption; token=private-fixture-input"), true);
                return TiaMcpServer.ModelContextProtocol.McpServer.V4Result("CreateApprovalFixture", new System.Text.Json.Nodes.JsonObject { ["target"] = target }, completed: !dryRun);
            }
        }
        public static class SessionApprovalProbe
        {
            internal static int Calls;
            [global::ModelContextProtocol.Server.McpServerTool(Name = "SaveProject"), TiaMcpServer.ModelContextProtocol.ToolClassification("L0", "Project", "SESSION")]
            public static global::ModelContextProtocol.Protocol.CallToolResult SaveProject(string mode = "preview", bool confirm = false,
                string expectedPlanHash = "", string expectedProjectFile = "") => Entered("SaveProject");
            [global::ModelContextProtocol.Server.McpServerTool(Name = "SaveProjectCopy"), TiaMcpServer.ModelContextProtocol.ToolClassification("L0", "Project", "SESSION")]
            public static global::ModelContextProtocol.Protocol.CallToolResult SaveProjectCopy(string newProjectPath, string mode = "preview", bool confirm = false,
                string expectedPlanHash = "", string expectedProjectFile = "") => Entered("SaveProjectCopy");
            [global::ModelContextProtocol.Server.McpServerTool(Name = "CloseProject"), TiaMcpServer.ModelContextProtocol.ToolClassification("L0", "Project", "SESSION")]
            public static global::ModelContextProtocol.Protocol.CallToolResult CloseProject(bool saveChanges = false, bool discardChanges = false,
                bool confirmDiscard = false, string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
                => Entered("CloseProject");
            private static global::ModelContextProtocol.Protocol.CallToolResult Entered(string name)
            { Calls++; return TiaMcpServer.ModelContextProtocol.McpServer.V4Result(name, new System.Text.Json.Nodes.JsonObject { ["called"] = true }); }
        }
        private static string Scratch() => Path.GetFullPath(Path.Combine("bin-build", "P6-44", "approval-tests", Guid.NewGuid().ToString("N")));
        private static PendingApproval Request(int seconds = 3) => PendingApproval.Create("engine", "21", "WriteFixture",
            "{\"blockPath\":\"PLC/Block\",\"password\":\"private-input\"}", "{\"projectFile\":\"P.ap21\",\"bindingEpoch\":1}", seconds);

        private static Task<global::ModelContextProtocol.Protocol.CallToolResult> CallToolWithDecision(string name, string arguments)
        {
            var previous = TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests;
            TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests = (pending, _, _) =>
            {
                Assert.Equal(name, pending.Tool);
                return Task.FromResult(new ApprovalOutcome(pending, false, null));
            };
            try { return Task.FromResult(TiaMcpServer.ModelContextProtocol.McpServer.CallTool(name, Args(arguments))); }
            finally { TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests = previous; }
        }

        private static TiaMcp.Logic.V4.Inputs.ToolArguments Args(string json)
            => new TiaMcp.Logic.V4.Inputs.ToolArguments(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json));

        [Fact]
        public async Task Dry_run_writes_do_not_queue_for_approval()
        {
            using var fixture = new InfrastructureContractsTests();
            TiaMcpServer.ModelContextProtocol.McpServer.ConfigureToolBridge(new TiaMcpServer.ModelContextProtocol.ToolCatalog(new[] {
                typeof(TiaMcpServer.ModelContextProtocol.McpServer), typeof(BatchGateProbe) }), () => false, new System.Collections.Generic.HashSet<string>());

            Assert.False(TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWrite("CreateApprovalFixture", "{\"target\":\"preview\",\"dryRun\":true}"));
            Assert.False(TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWrite("CreateApprovalFixture", "{\"target\":\"schema-default-preview\"}"));
            Assert.False(TiaMcpServer.ModelContextProtocol.McpServer.ApprovalResultWrite("CallTool",
                "{\"name\":\"CreateApprovalFixture\",\"arguments\":{\"target\":\"preview\",\"dryRun\":true}}"));
            Assert.False(TiaMcpServer.ModelContextProtocol.McpServer.ApprovalResultWrite("CallTool",
                "{\"name\":\"CreateApprovalFixture\",\"arguments\":{\"target\":\"schema-default-preview\"}}"));
            Assert.True(TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWrite("CreateApprovalFixture", "{\"target\":\"apply\",\"dryRun\":false}"));
            Assert.True(TiaMcpServer.ModelContextProtocol.McpServer.ApprovalResultWrite("CallTool",
                "{\"name\":\"CreateApprovalFixture\",\"arguments\":{\"target\":\"apply\",\"dryRun\":false}}"));
            int waits = 0;
            var previous = TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests;
            TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests = (pending, _, _) =>
            {
                waits++;
                return Task.FromResult(new ApprovalOutcome(pending, false, "denied"));
            };
            try
            {
                Assert.Null(await TiaMcpServer.ModelContextProtocol.McpServer.WaitForApproval("CreateApprovalFixture", "{\"target\":\"schema-default-preview\"}", CancellationToken.None));
                Assert.Equal(0, waits);
                var apply = await TiaMcpServer.ModelContextProtocol.McpServer.WaitForApproval("CreateApprovalFixture", "{\"target\":\"apply\",\"dryRun\":false}", CancellationToken.None);
                Assert.Equal("denied", apply?.Reason);
                Assert.Equal(1, waits);
            }
            finally { TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests = previous; }
        }

        [Theory]
        [InlineData("SaveProject")]
        [InlineData("SaveProjectCopy")]
        [InlineData("CloseProject")]
        public async Task Session_save_and_close_targets_are_gated_but_connect_and_disconnect_are_not(string tool)
        {
            using var fixture = new InfrastructureContractsTests();
            SessionApprovalProbe.Calls = 0;
            TiaMcpServer.ModelContextProtocol.McpServer.ConfigureToolBridge(new TiaMcpServer.ModelContextProtocol.ToolCatalog(new[] {
                typeof(TiaMcpServer.ModelContextProtocol.McpServer), typeof(SessionApprovalProbe) }),
                () => false, new System.Collections.Generic.HashSet<string>());
            Assert.True(TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWrite(tool, "{}"));
            Assert.True(TiaMcpServer.ModelContextProtocol.McpServer.ApprovalResultWrite("CallTool",
                "{\"name\":\"" + tool + "\",\"arguments\":{}}"));
            foreach (string sessionTool in new[] { "ConnectPortal", "DisconnectPortal" })
            {
                Assert.False(TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWrite(sessionTool, "{}"));
                Assert.False(TiaMcpServer.ModelContextProtocol.McpServer.ApprovalResultWrite("CallTool",
                    "{\"name\":\"" + sessionTool + "\",\"arguments\":{}}"));
                Assert.Null(await TiaMcpServer.ModelContextProtocol.McpServer.WaitForApproval(sessionTool, "{}", CancellationToken.None));
            }
            if (!OperatingSystem.IsWindows()) return;

            var before = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            bool previous = TiaMcpServer.ModelContextProtocol.McpServer.EnterMcpApprovalContext();
            try
            {
                new ApprovalSettings(true, 2).Save(ApprovalSettings.SettingsPath);
                var callArguments = new System.Text.Json.Nodes.JsonObject { ["mode"] = "apply", ["confirm"] = true,
                    ["expectedPlanHash"] = new string('a', 64), ["expectedProjectFile"] = "C:/fixture.ap21" };
                if (tool == "SaveProjectCopy") callArguments["newProjectPath"] = "C:/copy.ap21";
                string arguments = callArguments.ToJsonString();
                var absent = TiaMcpServer.ModelContextProtocol.McpServer.ResultBody(
                    TiaMcpServer.ModelContextProtocol.McpServer.CallTool(tool, Args(arguments)))!;
                Assert.Equal("CONFIRMATION_REQUIRED", (string?)absent["error"]?["code"]);
                Assert.Equal("not-started", (string?)absent["meta"]?["execution"]);

                var granted = TiaMcpServer.ModelContextProtocol.McpServer.ResultBody(
                    await CallToolWithDecision(tool, arguments))!;
                Assert.NotEqual("CONFIRMATION_REQUIRED", (string?)granted["error"]?["code"]);
                Assert.Equal(1, SessionApprovalProbe.Calls);

                new ApprovalSettings(false, 2).Save(ApprovalSettings.SettingsPath);
                var disabled = TiaMcpServer.ModelContextProtocol.McpServer.ResultBody(
                    TiaMcpServer.ModelContextProtocol.McpServer.CallTool(tool, Args(arguments)))!;
                Assert.Single(disabled["meta"]!["warnings"]!.AsArray(), row => (string?)row?["code"] == "APPROVAL_DISABLED");
                Assert.Equal(2, SessionApprovalProbe.Calls);
            }
            finally
            {
                TiaMcpServer.ModelContextProtocol.McpServer.LeaveMcpApprovalContext(previous);
                before.Save(ApprovalSettings.SettingsPath);
            }
        }
        [Fact]
        public void CallTool_write_target_refuses_before_dispatch_when_workbench_is_unavailable()
        {
            using var fixture = new InfrastructureContractsTests();
            Assert.True(ApprovalSettings.Load(ApprovalSettings.SettingsPath).Enabled);
            var catalog = new TiaMcpServer.ModelContextProtocol.ToolCatalog(new[] { typeof(TiaMcpServer.ModelContextProtocol.McpServer), typeof(BatchGateProbe) });
            TiaMcpServer.ModelContextProtocol.McpServer.ConfigureToolBridge(catalog, () => false, new System.Collections.Generic.HashSet<string>());
            bool previous = TiaMcpServer.ModelContextProtocol.McpServer.EnterMcpApprovalContext();
            try
            {
                var args = new TiaMcp.Logic.V4.Inputs.ToolArguments(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                    "{\"target\":\"bridge\",\"dryRun\":false}"));
                var result = TiaMcpServer.ModelContextProtocol.McpServer.CallTool("CreateApprovalFixture", args);
                var body = result.StructuredContent!;
                Assert.Equal("CONFIRMATION_REQUIRED", (string?)body["error"]?["code"]);
                Assert.Equal("workbench-unavailable", (string?)body["error"]?["details"]?["reason"]);
                Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
                Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
                Assert.Equal(new[] { "bridge:True" }, InfrastructureContractsTests.BatchProbes.Calls);
            }
            finally { TiaMcpServer.ModelContextProtocol.McpServer.LeaveMcpApprovalContext(previous); }
        }

        [Theory]
        [InlineData("success", "succeeded")]
        [InlineData("refusal", "rejected-before-operation")]
        [InlineData("failure", "failed")]
        [InlineData("unknown", "unknown")]
        [InlineData("throw", "unknown")]
        [InlineData("cancel", "unknown")]
        public void Disabled_approval_warning_covers_bridge_and_batch_write_exits(string outcome, string expected)
        {
            using var fixture = new InfrastructureContractsTests();
            TiaMcpServer.ModelContextProtocol.McpServer.ConfigureToolBridge(new TiaMcpServer.ModelContextProtocol.ToolCatalog(new[] {
                typeof(TiaMcpServer.ModelContextProtocol.McpServer), typeof(BatchGateProbe) }), () => false, new System.Collections.Generic.HashSet<string>());
            var before = ApprovalSettings.Load(ApprovalSettings.SettingsPath); new ApprovalSettings(false).Save(ApprovalSettings.SettingsPath);
            bool previous = TiaMcpServer.ModelContextProtocol.McpServer.EnterMcpApprovalContext();
            try
            {
                var args = new TiaMcp.Logic.V4.Inputs.ToolArguments(System.Text.Json.JsonSerializer.SerializeToElement(new { target = "fixture", dryRun = false, outcome }));
                var direct = TiaMcpServer.ModelContextProtocol.McpServer.ResultBody(TiaMcpServer.ModelContextProtocol.McpServer.CallTool("CreateApprovalFixture", args))!;
                Assert.Equal(expected, (string?)direct["meta"]?["outcome"]);
                Assert.Single(direct["meta"]!["warnings"]!.AsArray(), row => (string?)row?["code"] == "APPROVAL_DISABLED");
                Assert.DoesNotContain("private fixture input", direct.ToJsonString());
                Assert.DoesNotContain("private-fixture-input",direct.ToJsonString());
                if(outcome=="unknown") Assert.Contains("Fixture I/O interruption",(string?)direct["error"]?["details"]?["evidence"]?["workerMessage"]);
                var operation = new TiaMcp.Logic.V4.Inputs.ToolCall("CreateApprovalFixture", args);
                var second = new TiaMcp.Logic.V4.Inputs.ToolCall("CreateApprovalFixture", new TiaMcp.Logic.V4.Inputs.ToolArguments(
                    System.Text.Json.JsonSerializer.SerializeToElement(new { target = "second" })));
                var preview = TiaMcpServer.ModelContextProtocol.McpServer.ResultBody(TiaMcpServer.ModelContextProtocol.McpServer.PreviewToolBatch(new[] { operation, second }, "Fixture"))!;
                Assert.True((bool?)preview["ok"], preview.ToJsonString());
                var batch = TiaMcpServer.ModelContextProtocol.McpServer.ResultBody(TiaMcpServer.ModelContextProtocol.McpServer.ApplyToolBatch((string)preview["data"]!["token"]!))!;
                var item = batch["data"]!["items"]![0]!["result"]!;
                Assert.Equal(expected, (string?)item["meta"]?["outcome"]);
                Assert.Single(item["meta"]!["warnings"]!.AsArray(), row => (string?)row?["code"] == "APPROVAL_DISABLED");
                Assert.Single(batch["data"]!["items"]![1]!["result"]!["meta"]!["warnings"]!.AsArray(), row => (string?)row?["code"] == "APPROVAL_DISABLED");
            }
            finally { TiaMcpServer.ModelContextProtocol.McpServer.LeaveMcpApprovalContext(previous); before.Save(ApprovalSettings.SettingsPath); }
        }
        [Fact]
        public async Task File_export_cannot_disable_approval_even_when_ordinary_file_tools_are_not_gated()
        {
            string path = System.IO.Path.Combine(Scratch(), "approval.settings"); new ApprovalSettings().Save(path);
            foreach (string name in new[] { "approval.settings", "approval.settings::$DATA", "APPROV~1.SET", "approval.settings. ", "approval.settings\t\r\n" })
            {
                string arguments = System.Text.Json.JsonSerializer.Serialize(new { exportId = "fixture",
                    outputPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name) });
                var outcome = await TiaMcpServer.ModelContextProtocol.McpServer.WaitForApproval("SaveExportContent", arguments, CancellationToken.None);
                Assert.NotNull(outcome);
                var result = TiaMcpServer.ModelContextProtocol.McpServer.ApprovalRefusal(outcome!);
                Assert.Equal("denied", (string?)result.StructuredContent?["error"]?["details"]?["reason"]);
                Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
                Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
                Assert.True(ApprovalSettings.Load(path).Enabled);
            }
            string ordinary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "export.txt"); File.WriteAllText(ordinary, "fixture");
            Assert.False(ApprovalSettings.IsAdministrativeTarget(ordinary));
        }

        [Theory]
        [InlineData("granted")]
        [InlineData("denied")]
        [InlineData("timeout")]
        [InlineData("disabled")]
        public void Engine_write_audit_uses_response_id_and_starts_only_after_approval(string decision)
        {
            using var fixture = new InfrastructureContractsTests();
            TiaMcpServer.ModelContextProtocol.McpServer.ConfigureToolBridge(new TiaMcpServer.ModelContextProtocol.ToolCatalog(new[] {
                typeof(TiaMcpServer.ModelContextProtocol.McpServer), typeof(BatchGateProbe) }), () => false, new System.Collections.Generic.HashSet<string>());
            var previousSettings = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            var previousWait = TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests;
            bool priorContext = false;
            string requestId = Guid.NewGuid().ToString("N");
            var audit = new AuditLog(Scratch());
            bool enabled = decision != "disabled";
            try
            {
                new ApprovalSettings(enabled, 1).Save(ApprovalSettings.SettingsPath);
                using var auditScope = AuditInvocation.UseLog(audit);
                using var correlation = TiaMcpServer.ModelContextProtocol.InvocationJournal.UseCorrelation(requestId);
                TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests = (pending, _, _) =>
                {
                    if (enabled) audit.Approval(pending.RequestId, pending.Host, pending.ReleaseKey, pending.Tool,
                        decision == "granted" ? "granted" : decision, pending.PlanHash);
                    return Task.FromResult(new ApprovalOutcome(pending, !enabled, decision == "granted" || !enabled ? null : decision));
                };
                priorContext = TiaMcpServer.ModelContextProtocol.McpServer.EnterMcpApprovalContext();
                InfrastructureContractsTests.BatchProbes.Calls.Clear();
                var arguments = new TiaMcp.Logic.V4.Inputs.ToolArguments(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                    "{\"target\":\"audit\",\"dryRun\":false}"));
                var result = TiaMcpServer.ModelContextProtocol.McpServer.CallTool("CreateApprovalFixture", arguments);
                var body = TiaMcpServer.ModelContextProtocol.McpServer.ResultBody(result)!;
                Assert.Equal(requestId, (string?)body["meta"]!["requestId"]);
                var rows = audit.Read();
                string[] expected = decision switch
                {
                    "granted" => new[] { "request", "approval-granted", "start", "end" },
                    "denied" => new[] { "request", "approval-denied", "end" },
                    "timeout" => new[] { "request", "approval-timeout", "end" },
                    _ => new[] { "request", "start", "end" }
                };
                Assert.Equal(expected, rows.Select(row => row.Event));
                Assert.All(rows, row => Assert.Equal(requestId, row.RequestId));
                Assert.False(string.IsNullOrWhiteSpace(rows[0].PlanHash));
                Assert.All(rows.Where(row => row.Event.StartsWith("approval-", StringComparison.Ordinal)), row => Assert.Equal(rows[0].PlanHash, row.PlanHash));
                Assert.Equal(enabled ? decision == "granted" ? 2 : 1 : 1, InfrastructureContractsTests.BatchProbes.Calls.Count);
                Assert.True(audit.Verify().Passed);
            }
            finally
            {
                TiaMcpServer.ModelContextProtocol.McpServer.LeaveMcpApprovalContext(priorContext);
                TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests = previousWait;
                previousSettings.Save(ApprovalSettings.SettingsPath);
            }
        }

        [Fact]
        public void Approval_wait_preserves_native_budget_but_cannot_pause_indefinitely()
        {
            var now = TimeSpan.Zero; var budget = new TiaMcpServer.Isolation.ApprovalWaitBudget(() => now);
            budget.Signal("begin", 120); now += TimeSpan.FromSeconds(100); Assert.Equal(now, budget.Excluded);
            budget.Signal("end", 120); now += TimeSpan.FromSeconds(10); Assert.Equal(TimeSpan.FromSeconds(100), budget.Excluded);
            budget.Signal("begin", 1); now += TimeSpan.FromSeconds(10); Assert.Equal(TimeSpan.FromSeconds(102), budget.Excluded);
            budget.Signal("end", 1); budget.Signal("begin", 999999); now += TimeSpan.FromDays(1);
            Assert.Equal(TimeSpan.FromSeconds(102), budget.Excluded);
        }
        [Fact]
        public async Task Approval_frames_refuse_oversize_truncation_and_self_decision_shape()
        {
            using var oversized = new MemoryStream(BitConverter.GetBytes(ApprovalFrames.MaximumBytes + 1));
            await Assert.ThrowsAsync<InvalidDataException>(() => ApprovalFrames.Read<ApprovalDecision>(oversized, CancellationToken.None));
            using var shortFrame = new MemoryStream(new byte[] { 10, 0, 0, 0, 1 });
            await Assert.ThrowsAsync<EndOfStreamException>(() => ApprovalFrames.Read<ApprovalDecision>(shortFrame, CancellationToken.None));
            using var selfApprove = new MemoryStream(); await ApprovalFrames.Write(selfApprove, new ApprovalDecision { Decision = "granted" }, CancellationToken.None);
            selfApprove.Position = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => ApprovalFrames.Read<PendingApproval>(selfApprove, CancellationToken.None));
            using var missingVersion = new MemoryStream();
            byte[] missing = System.Text.Encoding.UTF8.GetBytes("{\"RequestId\":\"r1\",\"PlanHash\":\"hash\",\"ArgumentDigest\":\"digest\",\"Decision\":\"granted\"}");
            missingVersion.Write(BitConverter.GetBytes(missing.Length)); missingVersion.Write(missing); missingVersion.Position = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => ApprovalFrames.Read<ApprovalDecision>(missingVersion, CancellationToken.None));
        }
        [Theory]
        [InlineData("request")]
        [InlineData("hash")]
        [InlineData("arguments")]
        [InlineData("version")]
        public void A_decision_cannot_authorize_another_request_or_plan(string mismatch)
        {
            var request = Request(); var decision = new ApprovalDecision { RequestId = request.RequestId, PlanHash = request.PlanHash,
                ArgumentDigest = request.ArgumentDigest, Decision = "granted" };
            Assert.True(decision.Matches(request));
            switch (mismatch) { case "request": decision.RequestId = "other"; break; case "hash": decision.PlanHash = new string('b', 64); break;
                case "arguments": decision.ArgumentDigest = new string('c', 64); break; default: decision.Version = 2; break; }
            Assert.False(decision.Matches(request));
        }
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Applied_batch_gates_each_write_while_internal_previews_remain_read_only(bool enabled)
        {
            using var fixture = new InfrastructureContractsTests();
            TiaMcpServer.ModelContextProtocol.McpServer.ConfigureToolBridge(new TiaMcpServer.ModelContextProtocol.ToolCatalog(new[] {
                typeof(TiaMcpServer.ModelContextProtocol.McpServer), typeof(BatchGateProbe), typeof(InfrastructureContractsTests.BatchProbes) }),
                () => false, new System.Collections.Generic.HashSet<string>());
            var before = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            new ApprovalSettings(enabled, 1).Save(ApprovalSettings.SettingsPath);
            var mcp = false;
            try
            {
                var operations = new[] { new TiaMcp.Logic.V4.Inputs.ToolCall("CreateApprovalFixture",
                    new TiaMcp.Logic.V4.Inputs.ToolArguments(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{\"target\":\"A\"}"))),
                    new TiaMcp.Logic.V4.Inputs.ToolCall("CreateApprovalFixture", new TiaMcp.Logic.V4.Inputs.ToolArguments(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{\"target\":\"B\"}"))) };
                var preview = TiaMcpServer.ModelContextProtocol.McpServer.ResultBody(TiaMcpServer.ModelContextProtocol.McpServer.PreviewToolBatch(operations, "Fixture"))!;
                Assert.True((bool)preview["ok"]!, preview.ToJsonString());
                mcp = TiaMcpServer.ModelContextProtocol.McpServer.EnterMcpApprovalContext();
                var result = TiaMcpServer.ModelContextProtocol.McpServer.ResultBody(TiaMcpServer.ModelContextProtocol.McpServer.ApplyToolBatch((string)preview["data"]!["token"]!))!;
                var first = result["data"]!["items"]![0]!["result"]!;
                if (enabled)
                {
                    Assert.Equal("CONFIRMATION_REQUIRED", (string?)first["error"]?["code"]);
                    Assert.Equal("workbench-unavailable", (string?)first["error"]?["details"]?["reason"]);
                    Assert.Equal("rejected-before-operation", (string?)first["meta"]?["outcome"]);
                    Assert.Equal("not-started", (string?)first["meta"]?["execution"]);
                    Assert.Equal("NOT_EXECUTED", (string?)result["data"]!["items"]![1]!["result"]?["error"]?["code"]);
                    Assert.DoesNotContain(InfrastructureContractsTests.BatchProbes.Calls, call => call.EndsWith(":False", StringComparison.Ordinal));
                }
                else
                {
                    Assert.Contains("A:False", InfrastructureContractsTests.BatchProbes.Calls); Assert.Contains("B:False", InfrastructureContractsTests.BatchProbes.Calls);
                    Assert.Contains(first["meta"]!["warnings"]!.AsArray(), warning => (string?)warning?["code"] == "APPROVAL_DISABLED");
                }
                var invalidBridge = TiaMcpServer.ModelContextProtocol.McpServer.ResultBody(TiaMcpServer.ModelContextProtocol.McpServer.CallTool("CreateApprovalFixture",
                    new TiaMcp.Logic.V4.Inputs.ToolArguments(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{\"__invalid\":true,\"dryRun\":false}"))))!;
                Assert.Equal("INVALID_ARGUMENT", (string?)invalidBridge["error"]?["code"]);
                Assert.Equal(!enabled, invalidBridge["meta"]!["warnings"]!.AsArray().Any(warning => (string?)warning?["code"] == "APPROVAL_DISABLED"));
                Assert.Equal("INVALID_ARGUMENT", (string?)TiaMcpServer.ModelContextProtocol.McpServer.ResultBody(
                    TiaMcpServer.ModelContextProtocol.McpServer.ReadToolBatch(new[] { new TiaMcp.Logic.V4.Inputs.ToolCall("ReadFixture",
                        new TiaMcp.Logic.V4.Inputs.ToolArguments(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{}"))), operations[0] }))?["error"]?["code"]);
            }
            finally { TiaMcpServer.ModelContextProtocol.McpServer.LeaveMcpApprovalContext(mcp); before.Save(ApprovalSettings.SettingsPath); }
        }
        [Theory]
        [InlineData("denied")]
        [InlineData("timeout")]
        [InlineData("workbench-unavailable")]
        [InlineData("plan-confirmation")]
        public void Confirmation_details_are_closed_and_roundtrip(string reason)
        {
            var details = new ConfirmationRequiredDetails(reason, new string('a', 64), "r1");
            var error = V4Json.Deserialize<Error>(V4Json.Serialize(new Error("Refused.", details)));
            Assert.Equal(reason, Assert.IsType<ConfirmationRequiredDetails>(error.Details).Reason);
            Assert.Throws<ArgumentException>(() => new ConfirmationRequiredDetails("unknown", details.PlanHash, "r1"));
            Assert.Throws<ArgumentException>(() => new ConfirmationRequiredDetails("denied", null, "r1"));
            Assert.Throws<System.Text.Json.JsonException>(() => V4Json.Deserialize<Error>("{\"code\":\"CONFIRMATION_REQUIRED\",\"message\":\"Refused.\",\"details\":{\"planHash\":null}}"));
        }
        [Fact]
        public void Digest_binds_identity_and_all_arguments_and_redacts_display_only()
        {
            var request = Request();
            Assert.DoesNotContain("private-input", request.ParametersJson);
            Assert.NotEqual(request.ArgumentDigest, PendingApproval.Create("engine", "21", "WriteFixture", "{\"blockPath\":\"PLC/Block\",\"password\":\"changed\"}", "{\"projectFile\":\"P.ap21\",\"bindingEpoch\":1}", 3).ArgumentDigest);
            Assert.NotEqual(request.ArgumentDigest, PendingApproval.Create("engine", "21", "WriteFixture", "{\"blockPath\":\"PLC/Block\",\"password\":\"private-input\"}", "{\"projectFile\":\"Other.ap21\",\"bindingEpoch\":1}", 3).ArgumentDigest);
            Assert.Equal(PendingApproval.Create("engine", "21", "WriteFixture", "{\"a\":1,\"b\":2}", null, 3).PlanHash,
                PendingApproval.Create("engine", "21", "WriteFixture", "{\"b\":2,\"a\":1}", null, 3).PlanHash);
        }
        [Theory]
        [InlineData("granted", "none")]
        [InlineData("denied", "none")]
        [InlineData("granted", "request")]
        [InlineData("granted", "hash")]
        [InlineData("granted", "arguments")]
        [InlineData("granted", "version")]
        public async Task Pipe_decision_is_bound_once(string decision, string mismatch)
        {
            if (!OperatingSystem.IsWindows()) return;
            string name = "tia-approval-test-" + Guid.NewGuid().ToString("N");
            using var server = ApprovalPipe.CreateServer(name, ApprovalPipe.CurrentSid, true);
            var request = Request(); var audit = new AuditLog(Scratch());
            using var traceText = new StringWriter(); using var trace = new System.Diagnostics.TextWriterTraceListener(traceText);
            System.Diagnostics.Trace.Listeners.Add(trace);
            var response = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                var frame = await ApprovalFrames.Read<PendingApproval>(server, CancellationToken.None);
                Assert.True(ApprovalPipe.PeerIsCurrentUser(server, ApprovalPipe.CurrentSid));
                await ApprovalFrames.Write(server, new ApprovalDecision { RequestId = mismatch == "request" ? "other" : frame.RequestId,
                    PlanHash = mismatch == "hash" ? new string('b', 64) : frame.PlanHash,
                    ArgumentDigest = mismatch == "arguments" ? new string('c', 64) : frame.ArgumentDigest,
                    Version = mismatch == "version" ? 2 : 1, Decision = decision }, CancellationToken.None);
            });
            var result = await ApprovalClient.Wait(request, new ApprovalSettings(), CancellationToken.None, name, audit);
            System.Diagnostics.Trace.Listeners.Remove(trace);
            Assert.False(result.Reason == "workbench-unavailable", traceText.ToString());
            await response.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(decision == "granted" && mismatch == "none" ? null : "denied", result.Reason);
            Assert.Equal("denied", (await ApprovalClient.Wait(request, new ApprovalSettings(), CancellationToken.None, name, audit)).Reason);
            Assert.True(audit.Verify().Passed);
        }
        [Fact]
        public async Task Absent_and_timeout_are_not_started_and_audited()
        {
            if (!OperatingSystem.IsWindows()) return;
            string name = "tia-approval-test-" + Guid.NewGuid().ToString("N"); var audit = new AuditLog(Scratch());
            var absent = await ApprovalClient.Wait(Request(), new ApprovalSettings(), CancellationToken.None, name, audit);
            Assert.Equal("workbench-unavailable", absent.Reason);
            using var server = ApprovalPipe.CreateServer(name, ApprovalPipe.CurrentSid, true);
            var connected = server.WaitForConnectionAsync();
            var timeout = await ApprovalClient.Wait(Request(1), new ApprovalSettings(true, 1), CancellationToken.None, name, audit);
            await connected.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("timeout", timeout.Reason);
            Assert.Contains(audit.Read(), r => r.Event == "approval-timeout");
            var rejected = TiaMcpServer.ModelContextProtocol.McpServer.ApprovalRefusal(timeout);
            var body = TiaMcpServer.ModelContextProtocol.McpServer.ResultBody(rejected)!;
            Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
            Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
            Assert.Equal(timeout.Request.RequestId, (string?)body["meta"]?["requestId"]);
        }
        [Fact]
        public async Task Switch_persists_and_warning_retains_the_original_result()
        {
            string root = Scratch(), path = Path.Combine(root, "approval.settings"); var log = new AuditLog(Path.Combine(root, "audit"));
            Assert.True(ApprovalSettings.Load(path).Enabled);
            new ApprovalSettings(false, 300).Save(path, log);
            var settings = ApprovalSettings.Load(path); Assert.False(settings.Enabled); Assert.Equal(300, settings.TimeoutSeconds);
            var disabled = await ApprovalClient.Wait(Request(), settings, CancellationToken.None, "absent", log);
            Assert.True(disabled.Disabled); Assert.Null(disabled.Reason);
            var result = TiaMcpServer.ModelContextProtocol.McpServer.FinishApproval(TiaMcpServer.ModelContextProtocol.McpServer.V4Reject("WriteFixture",
                new Error("No project.", new ProjectNotBoundDetails())), disabled);
            var body = TiaMcpServer.ModelContextProtocol.McpServer.ResultBody(result)!;
            Assert.Equal("PROJECT_NOT_BOUND", (string?)body["error"]?["code"]);
            Assert.Contains(body["meta"]!["warnings"]!.AsArray(), r => (string?)r?["code"] == "APPROVAL_DISABLED");
            new ApprovalSettings(true, 300).Save(path, log);
            Assert.Equal(2, log.Read().Count(r => r.Event == "approval-switch"));
            File.WriteAllText(path, "enabled=false\ncorrupted=true\n"); Assert.True(ApprovalSettings.Load(path).Enabled);
        }
        [Fact]
        public void Protected_acl_grants_only_current_sid_and_refuses_another_sid_simulation()
        {
            if (!OperatingSystem.IsWindows()) return;
            string sid = ApprovalPipe.CurrentSid;
            var security = new RawSecurityDescriptor(ApprovalPipe.SecurityDescriptor(sid));
            Assert.True((security.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0);
            Assert.Equal(new SecurityIdentifier(sid), security.Owner);
            var ace = Assert.IsType<CommonAce>(Assert.Single(security.DiscretionaryAcl!.Cast<GenericAce>()));
            Assert.Equal(new SecurityIdentifier(sid), ace.SecurityIdentifier);
            Assert.Equal(AceQualifier.AccessAllowed, ace.AceQualifier);
            Assert.NotEqual(new SecurityIdentifier("S-1-5-21-1-2-3-1001"), ace.SecurityIdentifier);
            Assert.NotEqual(ApprovalPipe.Name(Scratch(), sid), ApprovalPipe.Name(Scratch(), "S-1-5-21-1-2-3-1001"));
        }
    }
}
