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

        [Theory]
        [InlineData("ImportDeviceAml", "{\"dryRun\":false}", true, 1)]
        [InlineData("ImportDeviceAml", "{\"dryRun\":true}", false, 1)]
        [InlineData("ImportDeviceAml", "{}", false, 1)]
        [InlineData("ExchangeSystemDiagnosticsSettings", "{\"action\":\"import\",\"dryRun\":false}", true, 1)]
        [InlineData("ExchangeSystemDiagnosticsSettings", "{\"action\":\"export\",\"dryRun\":false}", false, 1)]
        [InlineData("ExchangeSystemDiagnosticsSettings", "{\"action\":\"import\"}", false, 1)]
        [InlineData("ExchangeSystemDiagnosticsSettings", "{\"action\":\"import\",\"dryRun\":true}", false, 1)]
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
            ApprovalProbe.PreviewCalls.Clear();
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
                if (name is "ImportDeviceAml" or "ExchangeSystemDiagnosticsSettings" or "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules" or "RunPlcSimAdvancedTestScenario" or "RunTestSuiteCase")
                    Assert.Equal(new[] { gated || (bool?)JsonNode.Parse(json)!["dryRun"] != false }, ApprovalProbe.PreviewCalls);
            }
            finally { McpServer.LeaveMcpApprovalContext(context); McpServer.ApprovalWaitOverrideForTests = previous; settings.Save(TiaOpenness.Shared.ApprovalSettings.SettingsPath); }
        }




















        private static readonly IReadOnlyDictionary<string, JsonElement> Empty = new Dictionary<string, JsonElement>();

        private static JsonObject Body(Outcome outcome, Error error) => JsonNode.Parse(V4Json.Serialize(Envelope.Create<object?>(null, error,
            new Meta(DateTimeOffset.UtcNow, "21", "CallTool", "id", outcome, outcome == Outcome.Unknown ? Execution.Unknown : Execution.NotStarted,
                false, BehaviorPolicy.NotApplicable, Completeness.None, null, Array.Empty<Warning>()))))!.AsObject();
        private static Tool Descriptor(string name, string properties = "{}") => new Tool { Name = name, Description = "[L1][Project][SESSION] test",
            InputSchema = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\",\"properties\":" + properties + "}") };
    }
}
