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

        [Theory]
        [InlineData("INVALID_ARGUMENT")][InlineData("PRECONDITION_FAILED")]
        [InlineData("SESSION_RESET_REQUIRED")][InlineData("CONFIRMATION_REQUIRED")]
        public void Recovery_is_typed_idempotent_bounded_and_selects_the_embedded_operation(string code)
        {
            var body = JsonNode.Parse("{\"ok\":false,\"error\":{\"code\":\"" + code + "\"},\"meta\":{\"tool\":\"fixture\",\"releaseKey\":\"21\",\"warnings\":[]}}")!;
            JsonObject Example(string _, string release, string selected) => new() { ["request"] = new JsonObject { ["params"] = new JsonObject {
                ["arguments"] = new JsonObject { ["action"] = "read", ["path"] = "<exact target>", ["context"] = selected.Length == 0 ? "default" : "operation-selected" } } } };
            Assert.True(RecoveryHints.Attach(body, Example));
            Assert.False(RecoveryHints.Attach(body, Example));
            Assert.Single(body["meta"]!["warnings"]!.AsArray());
            Assert.Equal("read", (string?)body["meta"]!["warnings"]![0]!["details"]!["getToolUsage"]!["operation"]);
            Assert.Equal("operation-selected", (string?)body["meta"]!["warnings"]![0]!["details"]!["exampleArguments"]!["context"]);
            body["meta"]!["warnings"] = new JsonArray();
            Assert.True(RecoveryHints.Attach(body, (_, _, _) => new JsonObject { ["request"] = new JsonObject { ["params"] = new JsonObject {
                ["arguments"] = new JsonObject { ["text"] = new string('x', RecoveryHints.ExampleByteLimit + 1) } } } }));
            Assert.True((bool?)body["meta"]!["warnings"]![0]!["details"]!["exampleOmitted"]);
            Assert.Null(body["meta"]!["warnings"]![0]!["details"]!["exampleArguments"]);
        }
    }
}
