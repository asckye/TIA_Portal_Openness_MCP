using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Construction;
using TiaMcp.Logic.V4.Inputs;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class V4ConstructionInputTests
    {
        private const string Parameter = "spec";
        private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static ConstructionSpec Read(string kind, string json, ConstructionProfile profile)
        {
            T Value<T>() where T : ConstructionSpec
            {
                var result = new ConstructionInput<T>(profile).Read(json, Parameter);
                Assert.True(result.IsValid, V4Json.Serialize(result.Error));
                return result.Value!;
            }
            return kind switch
            {
                "udt" => Value<UdtSpec>(), "globaldb" => Value<GlobalDbSpec>(), "tagtable" => Value<PlcTagTableSpec>(),
                "st" => Value<StructuredTextSpec>(), "call" => Value<FlgNetCallSpec>(), "fc" => Value<FcBlockSpec>(),
                "fb" => Value<FbBlockSpec>(), "lad" => Value<LadFcBlockSpec>(), _ => throw new ArgumentException(kind)
            };
        }

        public static IEnumerable<object[]> Samples() => V4ConstructionTests.Examples().Select(row => new[] { row[0], row[1], row[3], row[4] });

        [Theory]
        [MemberData(nameof(Samples))]
        public void SharedContractPreservesExistingDecisionsAndXml(string kind, string source, string canonical, bool accepted)
        {
            Assert.Contains(".cs:", source);
            foreach (var profile in new[] { ConstructionProfile.FullEngine, ConstructionProfile.Foundation })
            {
                string? xml = null;
                var error = Record.Exception(() => xml = ConstructionAdapter.BuildCandidate(Read(kind, canonical, profile), "21", profile).Xml);
                Assert.Equal(accepted, error == null);
                if (accepted)
                    Assert.Equal(Encoding.UTF8.GetBytes(ConstructionAdapter.BuildCandidate(V4ConstructionTests.Read(kind, canonical), "21", profile).Xml), Encoding.UTF8.GetBytes(xml!));
            }
        }

        [Fact]
        public void AbstractIndentedStatementKeepsItsExistingUnion()
        {
            const string json = "{\"op\":\"token\",\"text\":\"RETURN;\",\"indent\":1}";
            Assert.IsType<TokenStatement>(V4Json.Deserialize<IndentedStatement>(json));
            var contract = new ConstructionInput<IndentedStatement>();
            Assert.IsType<TokenStatement>(contract.Read(json, Parameter).Value);
            Invalid(contract.Read("{\"op\":\"newline\"}", Parameter).Error!);
        }

        [Fact]
        public void SharedPresenceAndTypedValidationPreserveOmission()
        {
            var contract = new ConstructionInput<UdtSpec>();
            var missing = contract.Read((string?)null, Parameter, optional: true);
            Assert.True(missing.IsValid);
            Assert.Equal(InputPresence.Missing, missing.Presence);
            Assert.Null(missing.Value);
            var required = contract.Read(default(JsonElement), Parameter);
            Assert.False(required.IsValid);
            Assert.Equal(InputPresence.Missing, required.Presence);
            var explicitNull = contract.Read("null", Parameter, optional: true);
            Assert.Equal(InputPresence.Null, explicitNull.Presence);
            Invalid(explicitNull.Error!);
            var empty = contract.Read("{\"members\":[]}", Parameter);
            Assert.True(empty.IsValid);
            Assert.Equal(InputPresence.Value, empty.Presence);
            Assert.Null(empty.Value!.Name);
            Assert.Empty(empty.Value.Members);
            Assert.Equal("{\"members\":[]}", V4Json.Serialize(contract.Validate(empty.Value, Parameter).Value));
            Assert.False(new ConstructionInput<UdtSpec>(ConstructionProfile.Foundation).Validate(empty.Value, Parameter).IsValid);
        }

        [Theory]
        [InlineData("{\"members\":[],\"secret-credential\":1}")]
        [InlineData("{\"members\":[],\"members\":[]}")]
        [InlineData("{\"Members\":[]}")]
        [InlineData("{\"members\":null}")]
        [InlineData("{\"members\":\"[]\"}")]
        [InlineData("\"{\\\"members\\\":[]}\"")]
        [InlineData("{\"members\":[{\"name\":\"secret-credential\",\"datatype\":\"Bool\",\"startValue\":\"1\"}]}")]
        [InlineData("{\"members\":[{\"name\":\"x\",\"datatype\":\"Bool\",\"comment\":\"\",\"commentZhCn\":\"\"}]}")]
        [InlineData("{\"members\":[],\"name\":\"secret-credential\\u0000\"}")]
        [InlineData("{\"members\":[],\"name\":\"secret-credential")]
        public void InvalidInputUsesValueFreeSharedErrors(string json) => Invalid(new ConstructionInput<UdtSpec>().Read(json, Parameter).Error!);

        [Fact]
        public void SchemaIsTheSameClosedContractForNestedFields()
        {
            var contract = new ConstructionInput<FcBlockSpec>();
            var schema = new InputSchema(contract.Schema);
            foreach (var json in new[]
            {
                """{"blockName":"B","blockNumber":1,"inputs":[],"outputs":[],"structuredText":{"operations":[]}}""",
                """{"blockName":"B","blockNumber":1,"outputs":[],"structuredText":{"operations":[]}}""",
                """{"blockName":"B","blockNumber":1,"inputs":[{"name":"x","datatype":"Int","externalWritable":true}],"outputs":[],"structuredText":{"operations":[]}}""",
                """{"blockName":"B","blockNumber":1,"inputs":[],"outputs":[],"structuredText":{"operations":[{"op":"newline","indent":1}]}}"""
            })
            {
                using var document = JsonDocument.Parse(json);
                Assert.Equal(schema.Validate(document.RootElement, Parameter) == null, contract.Read(json, Parameter).IsValid);
            }
            Assert.Contains("\"additionalProperties\":false", V4Json.Serialize(contract.Schema));
        }

        [Theory]
        [InlineData(17, 17)][InlineData(20000, 65)]
        public void ParsedAndExternalDepthAreBounded(int depth, int actual)
        {
            string json = new string('[', depth) + "0" + new string(']', depth);
            var contract = new ConstructionInput<StructuredTextSpec>();
            Limit(contract.Read(json, Parameter).Error!, 16, actual);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = depth });
            Limit(contract.Read(document.RootElement, Parameter).Error!, 16, 17);
        }

        [Fact]
        public void SharedStringAndItemLimitsKeepLimitDetails()
        {
            Limit(new ConstructionInput<UdtSpec>().Read(V4Json.Serialize(new { name = new string('x', 4097), members = Array.Empty<object>() }), Parameter).Error!, 4096, 4097);
            Limit(new ConstructionInput<StructuredTextSpec>().Read(V4Json.Serialize(new { operations = Enumerable.Repeat(new { op = "newline" }, 1001).ToArray() }), Parameter).Error!, 1000, 1001);
        }

        [Fact]
        public void FoundationNormalizedCharacterExtensionPreservesEscapesAndWhitespace()
        {
            var operations = new JsonArray(Enumerable.Range(0, 64).Select(_ => (JsonNode)new JsonObject { ["op"] = "token", ["text"] = new string('a', 4096) }).ToArray());
            var root = new JsonObject { ["operations"] = operations };
            int excess = root.ToJsonString(Compact).Length - 262144;
            operations[63]!["text"] = new string('a', 4096 - excess);
            string exact = root.ToJsonString(Compact);
            var contract = new ConstructionInput<StructuredTextSpec>();
            var encoded = contract.Read(exact.Replace("a", "\\u0061"), Parameter);
            Assert.True(encoded.IsValid);
            Assert.True(contract.Validate(encoded.Value!, Parameter).IsValid);
            Assert.True(contract.Read(new string(' ', 262144) + exact, Parameter).IsValid);
            operations[63]!["text"] = new string('a', 4097 - excess);
            Limit(contract.Read(root.ToJsonString(Compact), Parameter).Error!, 262144, 262145);
        }

        [Fact]
        public void FoundationCombinedAndNetworkBudgetsUseSharedLimitDetails()
        {
            var block = new { blockName = "B", blockNumber = 1,
                inputs = Enumerable.Range(0, 600).Select(i => new { name = "a" + i, datatype = "Bool" }).ToArray(),
                outputs = Enumerable.Range(0, 401).Select(i => new { name = "b" + i, datatype = "Bool" }).ToArray(),
                structuredText = new { operations = new[] { new { op = "newline" } } } };
            Limit(new ConstructionInput<FcBlockSpec>(ConstructionProfile.Foundation).Read(V4Json.Serialize(block), Parameter).Error!, 1000, 1001);
            var lad = new { blockName = "B", blockNumber = 1, networks = Enumerable.Repeat(new { call = new { callName = "Run", parameters = Array.Empty<object>() } }, 65).ToArray() };
            Limit(new ConstructionInput<LadFcBlockSpec>(ConstructionProfile.Foundation).Read(V4Json.Serialize(lad), Parameter).Error!, 64, 65);
        }

        private static void Invalid(Error error)
        {
            Assert.Equal(ErrorCode.InvalidArgument, error.Code);
            Assert.Equal(Parameter, Assert.IsType<InvalidArgumentDetails>(error.Details).Parameter);
            Assert.DoesNotContain("secret-credential", V4Json.Serialize(error));
        }

        private static void Limit(Error error, int limit, int actual)
        {
            Assert.Equal(ErrorCode.LimitExceeded, error.Code);
            var details = Assert.IsType<LimitExceededDetails>(error.Details);
            Assert.Equal(Parameter, details.Parameter);
            Assert.Equal(limit, details.Limit);
            Assert.Equal(actual, details.Actual);
        }
    }
}
