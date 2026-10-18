using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.ModelContextProtocol;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class RecoveryHintTests
    {
        [Fact]
        public void Refused_engine_call_carries_the_same_release_example_as_GetToolUsage_and_success_has_none()
        {
            using var fixture = new InfrastructureContractsTests();
            McpServer.ConfigureToolBridge(new ToolCatalog(new[] { typeof(McpServer), typeof(ToolUsageTools) }), () => false, new HashSet<string>());
            var usageTools = new ToolUsageTools();
            var refused = McpServer.FinishApproval(usageTools.GetToolUsage(limit: 0), null);
            var body = McpServer.ResultBody(refused)!;
            Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
            var hint = body["meta"]!["warnings"]!.AsArray().Single(w => (string?)w?["code"] == "RECOVERY_GUIDANCE")!;
            var usage = McpServer.ResultBody(usageTools.GetToolUsage(toolName: "GetToolUsage"))!;
            Assert.True(JsonNode.DeepEquals(usage["data"]!["example"]!["request"]!["params"]!["arguments"], hint["details"]!["exampleArguments"]));
            Assert.Equal(McpServer.ReleaseKey, (string?)hint["details"]?["releaseKey"]);
            V4Json.Deserialize<Envelope>(body.ToJsonString());
            var success = McpServer.ResultBody(McpServer.FinishApproval(usageTools.GetToolUsage(toolName: "GetToolUsage"), null))!;
            Assert.True((bool?)success["ok"]);
            Assert.DoesNotContain(success["meta"]!["warnings"]!.AsArray(), w => (string?)w?["code"] == "RECOVERY_GUIDANCE");
        }

    }
}
