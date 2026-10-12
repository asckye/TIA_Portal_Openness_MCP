using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class V4ProtocolRulesTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper output;
        public V4ProtocolRulesTests(Xunit.Abstractions.ITestOutputHelper output) { this.output = output; }

        public static string Unannotated() => "offline";
        public static class ApprovalProbe
        {
            internal static int Calls;
            private static CallToolResult Result(string name) { Calls++; return McpServer.V4Result(name, new JsonObject()); }
            [ModelContextProtocol.Server.McpServerTool(Name = "ImportDeviceAml")]
            public static CallToolResult Aml(bool dryRun = true) => Result("ImportDeviceAml");
            [ModelContextProtocol.Server.McpServerTool(Name = "ExchangeSystemDiagnosticsSettings")]
            public static CallToolResult Diagnostics(string action, bool dryRun = true) => Result("ExchangeSystemDiagnosticsSettings");
            [ModelContextProtocol.Server.McpServerTool(Name = "ExchangeUnifiedTags")]
            public static CallToolResult Tags(string action, bool dryRun = true) => Result("ExchangeUnifiedTags");
            [ModelContextProtocol.Server.McpServerTool(Name = "ExchangeUnifiedScriptModules")]
            public static CallToolResult Scripts(string action, bool dryRun = true) => Result("ExchangeUnifiedScriptModules");
            [ModelContextProtocol.Server.McpServerTool(Name = "RunPlcSimAdvancedTestScenario")]
            public static CallToolResult Scenario(bool dryRun = true) => Result("RunPlcSimAdvancedTestScenario");
            [ModelContextProtocol.Server.McpServerTool(Name = "RunTestSuiteCase")]
            public static CallToolResult TestCase(bool dryRun = true) => Result("RunTestSuiteCase");
            [ModelContextProtocol.Server.McpServerTool(Name = "EnsureOpennessUserGroup")]
            public static CallToolResult Group() => Result("EnsureOpennessUserGroup");
            [ModelContextProtocol.Server.McpServerTool(Name = "GetEnvironmentDiagnostics")]
            public static CallToolResult Environment(bool fix = false) => Result("GetEnvironmentDiagnostics");
        }

        [Theory]
        [InlineData("ImportDeviceAml", "{\"dryRun\":false}", true, 1)]
        [InlineData("ImportDeviceAml", "{\"dryRun\":true}", false, 1)]
        [InlineData("ImportDeviceAml", "{}", false, 1)]
        [InlineData("ExchangeSystemDiagnosticsSettings", "{\"action\":\"import\",\"dryRun\":false}", true, 1)]
        [InlineData("ExchangeSystemDiagnosticsSettings", "{\"action\":\"export\",\"dryRun\":false}", false, 1)]
        [InlineData("ExchangeSystemDiagnosticsSettings", "{\"action\":\"import\"}", false, 1)]
        [InlineData("ExchangeUnifiedTags", "{\"action\":\"import\",\"dryRun\":false}", true, 1)]
        [InlineData("ExchangeUnifiedTags", "{\"action\":\"export\",\"dryRun\":false}", false, 1)]
        [InlineData("ExchangeUnifiedTags", "{\"action\":\"import\",\"dryRun\":true}", false, 1)]
        [InlineData("ExchangeUnifiedScriptModules", "{\"action\":\"import\",\"dryRun\":false}", true, 1)]
        [InlineData("ExchangeUnifiedScriptModules", "{\"action\":\"export\",\"dryRun\":false}", false, 1)]
        [InlineData("ExchangeUnifiedScriptModules", "{\"action\":\"import\",\"dryRun\":true}", false, 1)]
        [InlineData("RunPlcSimAdvancedTestScenario", "{\"dryRun\":false}", true, 1)]
        [InlineData("RunPlcSimAdvancedTestScenario", "{\"dryRun\":true}", false, 1)]
        [InlineData("RunPlcSimAdvancedTestScenario", "{}", false, 1)]
        [InlineData("RunTestSuiteCase", "{\"dryRun\":false}", true, 1)]
        [InlineData("RunTestSuiteCase", "{\"dryRun\":true}", false, 1)]
        [InlineData("RunTestSuiteCase", "{}", false, 1)]
        [InlineData("EnsureOpennessUserGroup", "{}", true, 0)]
        [InlineData("GetEnvironmentDiagnostics", "{\"fix\":true}", true, 0)]
        [InlineData("GetEnvironmentDiagnostics", "{\"fix\":false}", false, 1)]
        [InlineData("GetEnvironmentDiagnostics", "{}", false, 1)]
        public void Mutating_actions_wait_for_approval_before_dispatch(string name, string json, bool gated, int calls)
        {
            using var fixture = new InfrastructureContractsTests();
            McpServer.ConfigureToolBridge(new ToolCatalog(new[] { typeof(McpServer), typeof(ApprovalProbe) }), () => true, new HashSet<string>());
            ApprovalProbe.Calls = 0;
            var settings = TiaOpenness.Shared.ApprovalSettings.Load(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
            var previous = McpServer.ApprovalWaitOverrideForTests;
            bool context = McpServer.EnterMcpApprovalContext();
            int waits = 0;
            try
            {
                new TiaOpenness.Shared.ApprovalSettings(true, 1).Save(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
                McpServer.ApprovalWaitOverrideForTests = (pending, _, _) => {
                    Assert.Equal(name, pending.Tool); waits++;
                    return Task.FromResult(new TiaOpenness.Shared.ApprovalOutcome(pending, false, "denied")); };
                Assert.Equal(gated, McpServer.ApprovalWrite(name, json));
                var body = McpServer.ResultBody(McpServer.CallTool(name, new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.Deserialize<JsonElement>(json))))!;
                Assert.Equal(gated ? "CONFIRMATION_REQUIRED" : null, (string?)body["error"]?["code"]);
                Assert.Equal(gated ? 1 : 0, waits);
                Assert.Equal(calls, ApprovalProbe.Calls);
            }
            finally { McpServer.LeaveMcpApprovalContext(context); McpServer.ApprovalWaitOverrideForTests = previous; settings.Save(TiaOpenness.Shared.ApprovalSettings.SettingsPath); }
        }
        [Fact]
        public void Sdk_default_annotations_are_observed_and_served_hints_are_deterministic()
        {
            var raw = ModelContextProtocol.Server.McpServerTool.Create(typeof(V4ProtocolRulesTests).GetMethod(nameof(Unannotated))!).ProtocolTool;
            string wire = JsonSerializer.Serialize(raw, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            output.WriteLine("SDK default descriptor: " + wire);
            var served = DeclaredToolMetadata.V4(Descriptor("ListPlcBlocks"));
            string first = JsonSerializer.Serialize(served, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            string second = JsonSerializer.Serialize(DeclaredToolMetadata.V4(served), ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            Assert.Equal(first, second);
            using var document = JsonDocument.Parse(first);
            Assert.Equal(4, document.RootElement.GetProperty("annotations").EnumerateObject().Count());
        }

        [Theory]
        [InlineData("READ", true, false)]
        [InlineData("OFFLINE", true, false)]
        [InlineData("ONLINE", true, true)]
        [InlineData("WRITE", false, false)]
        [InlineData("ONLINE-WRITE", false, true)]
        [InlineData("FILE", false, false)]
        [InlineData("SESSION", false, false)]
        [InlineData("EXECUTE", false, false)]
        [InlineData("UI", false, false)]
        public void Every_category_has_explicit_hints(string category, bool read, bool open)
        {
            string name = category switch { "READ" => "ListPlcBlocks", "OFFLINE" => "BuildPlcUdt", "ONLINE" => "ProbeS7CpuIdentity",
                "WRITE" => "CreateDevice", "ONLINE-WRITE" => "DownloadPlc", "FILE" => "SaveExportContent", "SESSION" => "AttachOpenProject",
                "EXECUTE" => "CompileDevice", _ => "ShowWorkbenchPage" };
            var tool = DeclaredToolMetadata.V4(Descriptor(name), foundation: true);
            Assert.Equal(read, tool.Annotations!.ReadOnlyHint);
            Assert.Equal(open, tool.Annotations.OpenWorldHint);
            Assert.NotNull(tool.Annotations.DestructiveHint);
            Assert.NotNull(tool.Annotations.IdempotentHint);
            Assert.NotNull(tool.OutputSchema);
            Assert.Contains("[" + category + "]", tool.Description);
        }

        [Fact]
        public void Reviewed_tools_are_explicit_and_online_connections_change_state()
        {
            foreach (string name in new[] { "ConnectOnlinePlc", "DisconnectOnlinePlc", "DisconnectOnlinePlcs" })
                Assert.False(DeclaredToolMetadata.V4(Descriptor(name), true).Annotations!.ReadOnlyHint);
            Assert.False(ToolMetadata.Find("DisconnectOnlinePlcs")!.Inferred);
            Assert.Equal("OFFLINE", ToolMetadata.Find("PlanOnlineReadOnlyDataProvider")!.Operation);
            Assert.Equal("SESSION", ToolMetadata.RuntimeOperation("AttachOpenProjectV4"));
        }

        [Fact]
        public void Shared_descriptor_controls_hints_and_mismatch_is_rejected()
        {
            Assert.True(DeclaredToolMetadata.V4(Descriptor("CloseProject")).Annotations!.DestructiveHint);
            var served = DeclaredToolMetadata.V4(Descriptor("CloseProject"), true);
            Assert.False(served.Annotations!.DestructiveHint);
            served.Description = "[L1][Project][READ] wrong category";
            Assert.Throws<InvalidOperationException>(() => DeclaredToolMetadata.ValidateV4(served));
            var preview = Descriptor("PlanPlcExternalSourceImport", "{\"dryRun\":{\"type\":\"boolean\"}}");
            DeclaredToolMetadata.V4(preview, true);
            Assert.False(preview.Annotations!.ReadOnlyHint);
            preview.Annotations.ReadOnlyHint = true;
            Assert.Throws<InvalidOperationException>(() => DeclaredToolMetadata.ValidateV4(preview));
        }

        [Fact]
        public void Destructive_hints_follow_actions_and_overwrites()
        {
            Assert.False(DeclaredToolMetadata.V4(Descriptor("CreateDevice"), true).Annotations!.DestructiveHint);
            Assert.True(DeclaredToolMetadata.V4(Descriptor("ImportPlcBlock", "{\"overwrite\":{\"type\":\"boolean\"}}"), true).Annotations!.DestructiveHint);
            Assert.True(DeclaredToolMetadata.V4(Descriptor("EnsureUnifiedHmiScreen"), true).Annotations!.DestructiveHint);
            Assert.True(DeclaredToolMetadata.V4(Descriptor("ExportPlcWatchTablesToDirectory"), true).Annotations!.DestructiveHint);
            foreach (string name in new[] { "RunPlcSimAdvancedTestScenario", "RunTestSuiteCase" })
            {
                var tool = DeclaredToolMetadata.V4(Descriptor(name));
                Assert.False(tool.Annotations!.ReadOnlyHint);
                Assert.True(tool.Annotations.DestructiveHint);
                Assert.False(tool.Annotations.IdempotentHint);
                Assert.True(tool.Annotations.OpenWorldHint);
            }
        }

        [Theory]
        [InlineData("offset/limit", "offset", "limit")]
        [InlineData("a, b", "a", "b")]
        public void Multiple_parameters_roundtrip_without_changing_message(string parameters, string first, string second)
        {
            const string message = "Input does not satisfy the declared contract.";
            var error = McpServer.InvalidInput(parameters);
            var read = V4Json.Deserialize<Error>(V4Json.Serialize(error));
            var details = Assert.IsType<InvalidArgumentDetails>(read.Details);
            Assert.Equal(message, error.Message);
            Assert.Equal(message, read.Message);
            Assert.Equal(first, details.Parameter);
            Assert.Equal(new[] { first, second }, details.Parameters);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("arguments")]
        [InlineData("properties.Name")]
        public void Parameter_validation_always_identifies_a_field(string? parameter)
        {
            Assert.Equal(parameter ?? "arguments", new InvalidArgumentDetails(parameter, Array.Empty<string>()).Parameter);
            Assert.Equal(parameter ?? "arguments", new LimitExceededDetails(parameter, 1, 2).Parameter);
        }

        [Fact]
        public void Json_pointer_parameter_is_not_split_into_unrelated_fields()
        {
            var details = new InvalidArgumentDetails("/steps/0/arguments", Array.Empty<string>());
            Assert.Equal("/steps/0/arguments", details.Parameter);
            Assert.Equal(new[] { "/steps/0/arguments" }, details.Parameters);
            Assert.Equal("arguments", new InvalidArgumentDetails("", Array.Empty<string>()).Parameter);
        }

        [Fact]
        public void Stale_preview_keeps_message_and_separates_token_from_hash()
        {
            string hash = new string('a', 64);
            var args = new JsonObject { ["expectedProjectFile"] = "project.ap21", ["expectedPlanHash"] = hash };
            var preview = new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["projectFile"] = "project.ap21", ["planHash"] = new string('b', 64) } };
            var error = HostBehavior.BatchPreviewRefusal("ImportPlcBlocksFromDirectory", args, preview)!;
            Assert.Equal(ErrorCode.PlanStale, error.Code);
            Assert.Equal("Plan or project changed; review a fresh preview before applying.", error.Message);
            Assert.Equal(hash, Assert.IsType<PlanStaleDetails>(error.Details).PlanHash);
            var token = new PlanStaleDetails(null, "preview", "opaque-token");
            var read = V4Json.Deserialize<Error>(V4Json.Serialize(new Error("A stored preview changed.", token)));
            Assert.Null(Assert.IsType<PlanStaleDetails>(read.Details).PlanHash);
            Assert.Equal("opaque-token", Assert.IsType<PlanStaleDetails>(read.Details).PreviewToken);
        }

        [Fact]
        public void Consumer_mode_keeps_future_codes_and_fields_while_producer_stays_closed()
        {
            var value = Body(Outcome.RejectedBeforeOperation, new Error("Refused.", new ProjectNotBoundDetails()));
            value["future"] = 1; value["meta"]!["newField"] = "evidence"; value["error"]!["code"] = "FUTURE_ERROR";
            value["meta"]!["warnings"]!.AsArray().Add(new JsonObject { ["code"] = "FUTURE_WARNING", ["message"] = "Diagnostic.", ["details"] = new JsonObject() });
            var consumer = V4Json.DeserializeConsumer(value.ToJsonString());
            Assert.False(consumer.Ok); Assert.Equal("FUTURE_ERROR", consumer.ErrorCode);
            Assert.Equal(1, consumer.Value.GetProperty("future").GetInt32());
            Assert.ThrowsAny<Exception>(() => V4Json.Deserialize<Envelope>(value.ToJsonString()));
        }

        [Fact]
        public void Unknown_precheck_preserves_evidence_and_reset_matches_actual_lock()
        {
            var value = Body(Outcome.Unknown, new Error("Unknown.", new OutcomeUnknownDetails("PLC operation", Empty)));
            var marked = EnvelopeMutation.Precheck(value, "new-id");
            Assert.Equal("unknown", (string?)marked["meta"]!["outcome"]);
            Assert.Equal("plc-operation", (string?)marked["error"]!["details"]!["stage"]);
            Assert.False(V4Json.Deserialize<Envelope>(marked.ToJsonString()).Meta.RequiresSessionReset);
            var locked = EnvelopeMutation.SessionReset(marked, true);
            Assert.True(V4Json.Deserialize<Envelope>(locked.ToJsonString()).Meta.RequiresSessionReset);
            Assert.Equal("Unknown.", (string?)locked["error"]!["message"]);
        }

        [Fact]
        public void Recovery_decoration_validates_a_claimed_v4_envelope()
        {
            var malformed = JsonNode.Parse("{\"schemaVersion\":4,\"ok\":false,\"error\":{\"code\":\"INVALID_ARGUMENT\"},\"meta\":{\"tool\":\"fixture\",\"releaseKey\":\"21\",\"warnings\":[]}}")!;
            Assert.ThrowsAny<Exception>(() => TiaMcp.Logic.ModelContextProtocol.RecoveryHints.Attach(malformed, (_, _, _) => null));
        }

        [Fact]
        public void Paging_units_timestamps_and_null_details_are_explicit()
        {
            Assert.Equal("2026-10-11T00:00:00.1234567Z", V4Json.Timestamp(DateTimeOffset.Parse("2026-10-11T01:00:00.1234567+01:00")));
            var page = new Paging(PagingMode.Offset, 0, 2, 2, null, null, 4, false, PagingUnit.Utf16);
            Assert.Contains("\"unit\":\"utf16\"", V4Json.Serialize(page));
            Assert.Contains("\"parameter\":null", V4Json.Serialize(new Error("Refused.", new PreconditionFailedDetails("ConnectPortal", null))));
            Assert.Contains("\"reason\":null", V4Json.Serialize(new Error("Unknown.", new OutcomeUnknownDetails("worker", Empty))));
            Assert.Throws<ArgumentException>(() => new CompileErrorMessage("", "Error."));
            Assert.Throws<ArgumentException>(() => new CompileErrorMessage("block", ""));
        }

        [Fact]
        public async Task Heartbeat_survives_delivery_failure_and_stops_at_completion()
        {
            var frames = new List<JsonObject>();
            var two = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            using (ProgressHeartbeat.ForTest((frame, token) =>
            {
                if (Interlocked.Increment(ref calls) == 1) return Task.FromException(new System.IO.IOException("Fixture transport failed."));
                lock (frames) { frames.Add(frame); if (frames.Count == 2) two.TrySetResult(true); }
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(5)))
            {
                await two.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            int ended = calls;
            await Task.Delay(20);
            Assert.Equal(ended, calls);
            lock (frames)
            {
                Assert.Equal("test-token", (string?)frames[0]["progressToken"]);
                Assert.True((long)frames[1]["progress"]! > (long)frames[0]["progress"]!);
            }
        }

        [Fact]
        public async Task Captured_progress_sender_serializes_native_and_heartbeat_delivery()
        {
            var frames = new List<JsonObject>();
            using (ProgressHeartbeat.ForTest(async (frame, token) =>
            {
                await Task.Delay(2, token);
                lock (frames) frames.Add(frame.DeepClone().AsObject());
            }, TimeSpan.FromMilliseconds(3)))
            {
                var sender = ProgressHeartbeat.CaptureSender()!;
                Task[] sends;
                using (ExecutionContext.SuppressFlow())
                    sends = Enumerable.Range(0, 10).Select(_ => Task.Run(() => sender(new JsonObject
                        { ["progressToken"] = "test-token", ["progress"] = 999, ["total"] = 1000 }))).ToArray();
                await Task.WhenAll(sends);
            }
            lock (frames)
            {
                Assert.True(frames.Count >= 10);
                Assert.All(frames, frame => Assert.False(frame.ContainsKey("total")));
                Assert.Equal(Enumerable.Range(1, frames.Count).Select(i => (long)i), frames.Select(frame => (long)frame["progress"]!));
            }
        }

        private static readonly IReadOnlyDictionary<string, JsonElement> Empty = new Dictionary<string, JsonElement>();
        [Fact]
        public void Object_options_in_offline_results_do_not_enter_native_export_precheck()
        {
            var data = new JsonObject { ["options"] = new JsonObject { ["pollMs"] = 100 }, ["status"] = "planned-read-only-provider" };
            string before = data.ToJsonString();
            HostBehavior.ExportPreview(data);
            Assert.Equal(before, data.ToJsonString());
        }
        private static JsonObject Body(Outcome outcome, Error error) => JsonNode.Parse(V4Json.Serialize(Envelope.Create<object?>(null, error,
            new Meta(DateTimeOffset.UtcNow, "21", "CallTool", "id", outcome, outcome == Outcome.Unknown ? Execution.Unknown : Execution.NotStarted,
                false, BehaviorPolicy.NotApplicable, Completeness.None, null, Array.Empty<Warning>()))))!.AsObject();
        private static Tool Descriptor(string name, string properties = "{}") => new Tool { Name = name, Description = "[L1][Project][SESSION] test",
            InputSchema = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\",\"properties\":" + properties + "}") };
    }
}
