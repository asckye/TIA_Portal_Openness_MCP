using TiaMcp.Engine.Tests.Shared;
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

        [Theory]
        [InlineData("KeyNotFoundException: The given key 'connectionUnavailable' was not present in the dictionary.")]
        [InlineData("System.Reflection.TargetParameterCountException: Parameter count mismatch.")]
        public void Safe_diagnostic_keeps_the_word_after_an_exception_type(string message)
            => Assert.Equal(message, HostBehavior.SafeDiagnostic(message));

        [Theory]
        [InlineData("password=abc", "abc")]
        [InlineData("token: xyz", "xyz")]
        [InlineData("client.key : private", "private")]
        [InlineData("KeyNotFoundException: password=abc", "abc")]
        public void Safe_diagnostic_still_masks_credentials(string message, string secret)
            => Assert.DoesNotContain(secret, HostBehavior.SafeDiagnostic(message), StringComparison.Ordinal);

        [Theory]
        [InlineData("https://admin:Hunter2@tc-server/tc", "Hunter2")]
        [InlineData("{\"name\":\"password\",\"value\":\"Hunter2\"}", "Hunter2")]
        [InlineData("InvalidOperationException: {\"name\":\"password\",\"value\":\"Hunter2\"}", "Hunter2")]
        [InlineData("InvalidOperationException: [{\"name\":\"password\",\"value\":\"Hunter2\"}]", "Hunter2")]
        [InlineData("[{\"name\":\"password\",\"value\":\"Hunter2\",\"echo\":\"Hunter2\"}]", "Hunter2")]
        [InlineData("-----BEGIN PRIVATE KEY-----private-fixture-material-----END PRIVATE KEY-----", "private-fixture-material")]
        [InlineData("-----BEGIN RSA PRIVATE KEY-----\nprivate-fixture-material\n-----END RSA PRIVATE KEY-----", "PRIVATE KEY")]
        [InlineData("password=Hunter2 echoed Hunter2", "Hunter2")]
        [InlineData("password=NaN", "NaN")]
        [InlineData("password=Infinity", "Infinity")]
        [InlineData("Bearer fixture-token echoed fixture-token", "fixture-token")]
        [InlineData("{\"password\":\"Hunter2\"", "Hunter2")]
        public void Safe_diagnostic_retains_all_shared_text_privacy_rules(string message, string secret)
        {
            string? diagnostic = HostBehavior.SafeDiagnostic(message);
            Assert.DoesNotContain(secret, diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", diagnostic, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("Logon failed: password=4815162342 for user X", "Logon failed: password=•••• for user X")]
        [InlineData("Connection string: Server=s;apikey=1234567890;Database=d", "Connection string: Server=s;apikey=••••;Database=d")]
        [InlineData("password=true", "password=••••")]
        [InlineData("password=1", "password=••••")]
        [InlineData("{\"numericPin\":\"password=1234\"}", "{\"numericPin\":\"password=••••\"}")]
        public void Numeric_and_boolean_credential_pairs_are_masked(string message, string expected)
            => Assert.Equal(expected, HostBehavior.SafeDiagnostic(message));

        [Theory]
        [InlineData("{\"password\":1234}", "Refused 1234", "Refused ••••")]
        [InlineData("{\"password\":1}", "Refused 1", "Refused ••••")]
        [InlineData("{\"apiKey\":1234567890}", "Refused 1234567890", "Refused ••••")]
        [InlineData("{\"password\":true}", "Refused true", "Refused ••••")]
        [InlineData("{\"name\":\"password\",\"value\":1234}", "Refused 1234", "Refused ••••")]
        public void Numeric_and_boolean_argument_credentials_mask_unlabelled_echoes(string arguments, string message, string expected)
        {
            using var scope = NativeFailureDiagnostics.Begin("scalar-credential", JsonSerializer.Deserialize<JsonElement>(arguments));
            Assert.Equal(expected, HostBehavior.SafeDiagnostic(message));
        }

        [Theory]
        [InlineData("PasswordValidity", "9")]
        [InlineData("PasswordCount", "12")]
        [InlineData("PasswordLength", "16")]
        [InlineData("PasswordAttempts", "3")]
        [InlineData("PasswordEnabled", "true")]
        public void Numeric_and_boolean_policy_fields_keep_their_diagnostic_values(string name, string value)
        {
            using var scope = NativeFailureDiagnostics.Begin("policy", JsonSerializer.Deserialize<JsonElement>("{\"" + name + "\":" + value + "}"));
            string message = name + ": " + value + " out of range. Value " + value;
            Assert.Equal(message, HostBehavior.SafeDiagnostic(message));
        }

        [Theory]
        [InlineData("{\"a\":\"x\",\"a\":\"y\",\"password\":\"Hunter2\"}")]
        [InlineData("{\"o\":{\"a\":1,\"a\":2},\"password\":\"Hunter2\"}")]
        [InlineData("InvalidOperationException: {\"a\":1,\"a\":2,\"password\":\"Hunter2\"}")]
        public void Duplicate_json_keys_keep_native_failures_sanitized_without_throwing(string message)
        {
            Error? error = null;
            var thrown = Record.Exception(() => error = new Error(message,
                new NativeOperationFailedDetails(null, message, new Dictionary<string, JsonElement>())));
            Assert.Null(thrown);
            Assert.NotNull(error);
            Assert.Equal(ErrorCode.NativeOperationFailed, error!.Code);
            Assert.False(string.IsNullOrWhiteSpace(((NativeOperationFailedDetails)error.Details).NativeMessage));
            Assert.DoesNotContain("Hunter2", V4Json.Serialize(error), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("releaseKey", false), InlineData("ReLeAsE-Key", false), InlineData("password", true)]
        [InlineData("serverPassword", true), InlineData("passwd", true), InlineData("pwd", true), InlineData("sk", true)]
        [InlineData("apiKey", true), InlineData("keys", true), InlineData("tokenValue", true), InlineData("client.secret", true)]
        [InlineData("credentials", true), InlineData("Authorization", true), InlineData("PasswordValidity", true)]
        [InlineData("monkey", true), InlineData("name", false), InlineData("keyboard", false), InlineData("offset", false)]
        public void Diagnostic_and_journal_credential_names_use_the_same_rule(string name, bool expected)
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var diagnostic = typeof(NativeFailureDiagnostics).GetMethod("Credential", flags)!;
            var journal = typeof(TiaOpenness.Shared.CallJournalPayload).GetMethod("Sensitive", flags)!;
            Assert.Equal(expected, (bool)diagnostic.Invoke(null, new object[] { name })!);
            Assert.Equal(expected, (bool)journal.Invoke(null, new object[] { name })!);
        }

        [Fact]
        public void Structured_diagnostics_bound_nested_objects_and_mask_cross_field_echoes()
        {
            JsonNode nested = new JsonObject { ["echo"] = "Hunter2" };
            for (int index = 0; index < 17; index++) nested = new JsonObject { ["child"] = nested };
            nested["password"] = "Hunter2";
            string? diagnostic = HostBehavior.SafeDiagnostic(nested.ToJsonString());
            Assert.DoesNotContain("Hunter2", diagnostic, StringComparison.Ordinal);
            Assert.Contains("••••", diagnostic, StringComparison.Ordinal);
        }

        [Fact]
        public void Native_failure_fields_share_url_userinfo_redaction()
        {
            const string url = "https://admin:Hunter2@tc-server/tc";
            var error = new Error("Native connection failed.", new NativeOperationFailedDetails(null, url,
                new Dictionary<string, JsonElement> { ["endpoint"] = JsonSerializer.SerializeToElement(url),
                    ["exceptionType"] = JsonSerializer.SerializeToElement(url) }));
            Assert.DoesNotContain("Hunter2", V4Json.Serialize(error), StringComparison.Ordinal);
            Assert.Equal("https://••••@tc-server/tc", ((NativeOperationFailedDetails)error.Details).NativeMessage);
            Assert.NotNull(error.Innererror);
        }

        [Theory]
        [InlineData("PasswordError: hunter2", "hunter2")]
        [InlineData("apiTokenError: abc", "abc")]
        public void Credential_markers_are_not_exception_type_prefixes(string message, string secret)
            => Assert.DoesNotContain(secret, HostBehavior.SafeDiagnostic(message), StringComparison.Ordinal);

        [Theory]
        [InlineData("PasswordPolicySettingsException: Password policy cannot be changed.")]
        [InlineData("KeyNotFoundError: The given key was not present.")]
        public void Exception_suffix_keeps_the_native_reason(string message)
            => Assert.Equal(message, HostBehavior.SafeDiagnostic(message));

        [Theory]
        [InlineData("dbPwdError: hunter2", "hunter2")]
        [InlineData("ApiKeyValueError: abc", "abc")]
        public void Error_suffix_uses_all_pair_markers(string message, string secret)
            => Assert.DoesNotContain(secret, HostBehavior.SafeDiagnostic(message), StringComparison.Ordinal);

        [Theory]
        [InlineData("TargetInvocationException: Error when reading ---> X.PasswordPolicySettingsException: Error text",
            "TargetInvocationException: Error when reading")]
        [InlineData("TargetInvocationException: The read failed ---> KeyNotFoundException: The key was not present",
            "TargetInvocationException: The read failed")]
        public void Inner_exception_names_do_not_mask_the_outer_reason(string message, string expected)
            => Assert.Equal(expected, HostBehavior.SafeDiagnostic(message));

        [Fact]
        public void Exception_prefix_cannot_expose_a_second_line()
        {
            string? text = HostBehavior.SafeDiagnostic("FooException:\nhunter2");
            Assert.Equal("FooException:", text);
            Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Both_message_fields_use_diagnostic_context_without_evidence()
        {
            var data = new JsonObject { ["messageData"] = new JsonObject {
                ["text"] = "Reason\npassword=hunter2", ["detailText"] = "Detail\npassword=hunter2" },
                ["detailMessageData"] = new JsonArray(new JsonObject {
                    ["text"] = "Inner\npassword=hunter2", ["detailText"] = "Inner detail\npassword=hunter2" }) };
            HostBehavior.SanitizeEvidence(data, evidence: false);
            Assert.Equal("Reason", (string?)data["messageData"]?["text"]);
            Assert.Equal("Detail", (string?)data["messageData"]?["detailText"]);
            Assert.Equal("Inner", (string?)data["detailMessageData"]?[0]?["text"]);
            Assert.Equal("Inner detail", (string?)data["detailMessageData"]?[0]?["detailText"]);
            Assert.DoesNotContain("hunter2", data.ToJsonString(), StringComparison.Ordinal);
        }

        [Fact]
        public void Qualified_key_error_name_is_redacted()
            => Assert.DoesNotContain("hunter2", HostBehavior.SafeDiagnostic("PasswordStore.KeyNotFoundError: hunter2"), StringComparison.Ordinal);

        [Theory]
        [InlineData("PasswordException: hunter2")]
        [InlineData("TokenException: abc")]
        [InlineData("KeyNotFoundError: The key was not present")]
        public void Accepted_type_name_examples_remain_visible(string message)
            => Assert.Equal(message, HostBehavior.SafeDiagnostic(message));

        public static string Unannotated() => "offline";
        public static class ApprovalProbe
        {
            internal static int Calls;
            internal static readonly List<bool> PreviewCalls = new List<bool>();
            private static CallToolResult Result(string name, bool? dryRun = null) { Calls++; if (dryRun.HasValue) PreviewCalls.Add(dryRun.Value); return McpServer.V4Result(name, new JsonObject()); }
            [ModelContextProtocol.Server.McpServerTool(Name = "ImportDeviceAml")]
            public static CallToolResult Aml(bool dryRun = true) => Result("ImportDeviceAml", dryRun);
            [ModelContextProtocol.Server.McpServerTool(Name = "ExchangeSystemDiagnosticsSettings")]
            public static CallToolResult Diagnostics(string action, bool dryRun = true) => Result("ExchangeSystemDiagnosticsSettings", dryRun);
            [ModelContextProtocol.Server.McpServerTool(Name = "ExchangeUnifiedTags")]
            public static CallToolResult Tags(string action, bool dryRun = true) => Result("ExchangeUnifiedTags", dryRun);
            [ModelContextProtocol.Server.McpServerTool(Name = "ExchangeUnifiedScriptModules")]
            public static CallToolResult Scripts(string action, bool dryRun = true) => Result("ExchangeUnifiedScriptModules", dryRun);
            [ModelContextProtocol.Server.McpServerTool(Name = "RunPlcSimAdvancedTestScenario")]
            public static CallToolResult Scenario(bool dryRun = true) => Result("RunPlcSimAdvancedTestScenario", dryRun);
            [ModelContextProtocol.Server.McpServerTool(Name = "RunTestSuiteCase")]
            public static CallToolResult TestCase(bool dryRun = true) => Result("RunTestSuiteCase", dryRun);
            [ModelContextProtocol.Server.McpServerTool(Name = "EnsureOpennessUserGroup")]
            public static CallToolResult Group() => Result("EnsureOpennessUserGroup");
            [ModelContextProtocol.Server.McpServerTool(Name = "GetEnvironmentDiagnostics")]
            public static CallToolResult Environment(bool fix = false) => Result("GetEnvironmentDiagnostics");
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
        [InlineData("ExchangeTestSuiteCase")]
        [InlineData("ExchangeCfcCharts")]
        public void Documented_actions_supply_destructive_hints_without_an_enum(string name)
        {
            var tool = DeclaredToolMetadata.V4(Descriptor(name, "{\"action\":{\"type\":\"string\"}}"), true);
            Assert.NotEmpty(ToolMetadata.Parameter(name, "action")!.AllowedValues);
            Assert.False(tool.InputSchema.GetProperty("properties").GetProperty("action").TryGetProperty("enum", out _));
            Assert.False(tool.Annotations!.ReadOnlyHint);
            Assert.True(tool.Annotations.DestructiveHint);
            Assert.False(tool.Annotations.IdempotentHint);
            Assert.False(tool.Annotations.OpenWorldHint);
        }

        [Fact]
        public void Documented_read_only_actions_stay_non_destructive()
        {
            const string name = "AnalyzePlcReferences";
            Assert.NotEmpty(ToolMetadata.Parameter(name, "action")!.AllowedValues);
            var tool = DeclaredToolMetadata.V4(Descriptor(name, "{\"action\":{\"type\":\"string\"}}"), true);
            Assert.True(tool.Annotations!.ReadOnlyHint);
            Assert.False(tool.Annotations.DestructiveHint);
            Assert.True(tool.Annotations.IdempotentHint);
            Assert.False(tool.Annotations.OpenWorldHint);
        }

        [Theory]
        [InlineData("[]", false)]
        [InlineData("[\"export\"]", false)]
        [InlineData("[\"import\"]", true)]
        [InlineData("[\"DELETE\"]", true)]
        public void Schema_enum_keeps_precedence_over_documented_actions(string values, bool destructive)
        {
            var tool = DeclaredToolMetadata.V4(Descriptor("ExchangeCfcCharts", "{\"action\":{\"type\":\"string\",\"enum\":" + values + "}}"), true);
            Assert.False(tool.Annotations!.ReadOnlyHint);
            Assert.Equal(destructive, tool.Annotations.DestructiveHint);
            Assert.False(tool.Annotations.IdempotentHint);
            Assert.False(tool.Annotations.OpenWorldHint);
        }

        [Fact]
        public void Missing_action_parameter_does_not_inherit_documented_mutations()
        {
            Assert.False(DeclaredToolMetadata.V4(Descriptor("ExchangeCfcCharts"), true).Annotations!.DestructiveHint);
        }

        [Theory]
        [InlineData("offset/limit", "/offset", "/limit")]
        [InlineData("a, b", "/a", "/b")]
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
            Assert.Equal(parameter == null ? "" : "/" + parameter, new InvalidArgumentDetails(parameter, Array.Empty<string>()).Parameter);
            Assert.Equal(parameter ?? "arguments", new LimitExceededDetails(parameter, 1, 2).Parameter);
        }

        [Fact]
        public void Json_pointer_parameter_is_not_split_into_unrelated_fields()
        {
            var details = new InvalidArgumentDetails("/steps/0/arguments", Array.Empty<string>());
            Assert.Equal("/steps/0/arguments", details.Parameter);
            Assert.Equal(new[] { "/steps/0/arguments" }, details.Parameters);
            Assert.Equal("", new InvalidArgumentDetails("", Array.Empty<string>()).Parameter);
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
        public void Unknown_precheck_preserves_evidence_without_locking_the_session()
        {
            var value = Body(Outcome.Unknown, new Error("Unknown.", new OutcomeUnknownDetails("PLC operation", Empty)));
            var marked = EnvelopeMutation.Precheck(value, "new-id");
            Assert.Equal("unknown", (string?)marked["meta"]!["outcome"]);
            Assert.Equal("plc-operation", (string?)marked["error"]!["details"]!["stage"]);
            Assert.False(V4Json.Deserialize<Envelope>(marked.ToJsonString()).Meta.RequiresSessionReset);
            var locked = marked;
            Assert.False(V4Json.Deserialize<Envelope>(locked.ToJsonString()).Meta.RequiresSessionReset);
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
                await two.Task.WaitAsync(TestDeadlines.Hang);
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
