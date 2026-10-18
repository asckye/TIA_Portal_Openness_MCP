using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class PlcContractsTests
    {
        private static JsonObject Body(CallToolResult result)
        {
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            Assert.Equal(result.StructuredContent!.ToJsonString(), JsonNode.Parse(text)!.ToJsonString());
            var envelope = V4Json.Deserialize<Envelope>(text);
            Assert.Equal(!envelope.Ok, result.IsError);
            return result.StructuredContent!.AsObject();
        }

        [Theory]
        [InlineData("[]")]
        [InlineData("\"[]\"")]
        [InlineData("[null]")]
        [InlineData("[{\"Action\":\"setBlockText\"}]")]
        [InlineData("[{\"action\":\"setBlockText\",\"field\":\"Title\",\"culture\":\"en-US\",\"expectedValue\":\"old\",\"value\":\"new\",\"extra\":1}]")]
        [InlineData("[{\"action\":\"setNetworkText\",\"networkIndex\":1.5,\"field\":\"Title\",\"culture\":\"en-US\",\"expectedValue\":\"old\",\"value\":\"new\"}]")]
        public void RejectsInvalidBlockEdits(string json)
            => Assert.NotNull(DomainValidation.Contract<BlockEdit[]>().Read(json, "changes").Error);

        [Theory]
        [InlineData("null")]
        [InlineData("\"{}\"")]
        [InlineData("{\"pollMs\":1.5}")]
        [InlineData("{\"pollMs\":2147483648}")]
        [InlineData("{\"PollMs\":1000}")]
        [InlineData("{\"pollMs\":1,\"pollMs\":2}")]
        [InlineData("{\"unknown\":true}")]
        public void RejectsInvalidMonitoringOptions(string json)
            => Assert.NotNull(DomainValidation.Contract<MonitoringOptions>().Read(json, "options").Error);

        [Fact]
        public void BlockEditLimitAndCanonicalRoundtripArePreserved()
        {
            const string change = "{\"action\":\"setBlockText\",\"field\":\"Title\",\"culture\":\"en-US\",\"expectedValue\":\"old\",\"value\":\"new\"}";
            var contract = DomainValidation.Contract<BlockEdit[]>();
            var valid = contract.Read("[" + change + "]", "changes");
            Assert.Null(valid.Error);
            Assert.IsType<BlockTextEdit>(Assert.Single(valid.Value!));
            Assert.Null(contract.Read(V4Json.Serialize(valid.Value), "changes").Error);
            Assert.Equal(ErrorCode.LimitExceeded, contract.Read("[" + string.Join(",", Enumerable.Repeat(change, 101)) + "]", "changes").Error!.Code);
        }
    }
}
