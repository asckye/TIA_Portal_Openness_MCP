using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Construction;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class V4ConstructionTests
    {
        private const string MemberJson = """{"name":"Ready","datatype":"Bool"}""";
        private const string St = """{"operations":[{"op":"assign","target":"#Ready","literalValue":"TRUE"}]}""";
        private const string Call = """{"callName":"Run","parameters":[{"name":"Start","section":"Input","dataType":"Bool","sourceKind":"constant","constantValue":"TRUE"},{"name":"Done","section":"Output","dataType":"Bool","symbolPath":["DB","Done"]}]}""";
        private const string Block = """{"blockName":"Test","blockNumber":1,"inputs":[],"outputs":[],"structuredText":ST}""";

        internal static ConstructionSpec Read(string kind, string json) => kind switch
        {
            "st" => V4Json.Deserialize<StructuredTextSpec>(json),
            "call" => V4Json.Deserialize<FlgNetCallSpec>(json),
            "lad" => V4Json.Deserialize<LadFcBlockSpec>(json),
            _ => PlcArtifactSpec.Deserialize(kind, json)
        };

        private static string Legacy(string kind, string json)
        {
            var result = kind switch
            {
                "udt" => PlcBuilderToolJson.BuildUdt(json), "globaldb" => PlcBuilderToolJson.BuildGlobalDb(json),
                "tagtable" => PlcBuilderToolJson.BuildTagTable(json), "st" => PlcBuilderToolJson.BuildStructuredText(json),
                "call" => PlcBuilderToolJson.BuildFlgNetCall(json), "fc" => PlcBuilderToolJson.ComposeFcBlock(json),
                "fb" => PlcBuilderToolJson.ComposeFbBlock(json), "lad" => PlcBuilderToolJson.ComposeLadFcBlock(json),
                _ => throw new ArgumentException(kind)
            };
            if (!result["ok"]!.GetValue<bool>()) throw new ArgumentException("Invalid builder XML.");
            return result["xml"]!.GetValue<string>();
        }

        public static IEnumerable<object[]> Examples()
        {
            // Sources are the unchanged builder parser/validation branches, not a new oracle.
            const string parser = "src/Logic/ModelContextProtocol/Builders/PlcBuilderToolJson.cs:";
            var samples = new[]
            {
                ("udt", "15", """{"name":"UDT_Status","members":[MEMBER]}""".Replace("MEMBER", MemberJson)),
                ("globaldb", "58", """{"dbName":"DB_Status","dbNumber":1,"staticMembers":[MEMBER]}""".Replace("MEMBER", MemberJson)),
                ("tagtable", "35", """{"tableName":"Tags","tags":[{"name":"Start","dataTypeName":"Bool","logicalAddress":"%I0.0"}]}"""),
                ("st", "89", St), ("call", "101", Call),
                ("fc", "117", Block.Replace("ST", St)), ("fb", "187", Block.Replace("ST", St)),
                ("lad", "141", """{"blockName":"Caller","blockNumber":42,"networks":[{"call":CALL}]}""".Replace("CALL", Call))
            };
            foreach (var (kind, line, json) in samples)
            {
                yield return new object[] { kind, parser + line, json, json, true };
                yield return new object[] { kind, parser + line, "{}", "{}", false };
            }
            yield return new object[] { "udt", parser + "15", """{"udtName":"Alias","members":[{"name":"x","dataType":"Bool","commentZh":"ok"}]}""",
                """{"name":"Alias","members":[{"name":"x","datatype":"Bool","commentZhCn":"ok"}]}""", true };
            yield return new object[] { "globaldb", parser + "58", """{"name":"D","number":1,"members":[{"name":"x","dataType":"Int","externalWritable":false,"startValue":"1"}]}""",
                """{"dbName":"D","dbNumber":1,"staticMembers":[{"name":"x","datatype":"Int","externalWritable":false,"startValue":"1"}]}""", true };
            yield return new object[] { "tagtable", parser + "35", """{"name":"Alias","tags":[{"name":"x","datatype":"Bool","address":"%I0.0"}]}""",
                """{"tableName":"Alias","tags":[{"name":"x","dataTypeName":"Bool","logicalAddress":"%I0.0"}]}""", true };
            yield return new object[] { "st", parser + "245", """{"operations":[{"op":"assignment","target":"a","value":"1"}]}""", St.Replace("#Ready", "a").Replace("TRUE", "1"), true };
            yield return new object[] { "call", parser + "350", Call.Replace("constantValue", "value").Replace("callName", "name"), Call, true };
            foreach (var kind in new[] { "fc", "fb" })
                yield return new object[] { kind, parser + "120", Block.Replace("ST", St).Replace("blockName", "name").Replace("blockNumber", "number"), Block.Replace("ST", St), true };
            var lad = samples.Last().Item3;
            yield return new object[] { "lad", parser + "159", lad.Replace("\"call\":", "\"callJson\":"), lad, true };
            foreach (var kind in new[] { "udt", "globaldb" })
            {
                var valid = samples.Single(s => s.Item1 == kind).Item3;
                var duplicate = valid.Replace(MemberJson, MemberJson + "," + MemberJson.Replace("Ready", "READY"));
                yield return new object[] { kind, "src/Logic/ModelContextProtocol/Builders/PlcUdtXmlBuilder.cs:252; PlcGlobalDbXmlBuilder.cs:274", duplicate, duplicate, false };
            }
            var badTag = samples[2].Item3.Replace("%I0.0", "I0.0");
            yield return new object[] { "tagtable", "src/Logic/ModelContextProtocol/Builders/PlcTagTableXmlBuilder.cs:233", badTag, badTag, false };
            var badSymbol = St.Replace("#Ready", "a + b");
            yield return new object[] { "st", "src/Logic/ModelContextProtocol/Builders/StructuredTextXmlBuilder.cs:142", badSymbol, badSymbol, false };
            var duplicatePort = Call.Replace("Done", "Start");
            yield return new object[] { "call", "src/Logic/ModelContextProtocol/Builders/FlgNetCallXmlBuilder.cs:325", duplicatePort, duplicatePort, false };
            foreach (var kind in new[] { "fc", "fb", "lad" })
            {
                var badNumber = samples.Single(s => s.Item1 == kind).Item3.Replace("\"blockNumber\":1", "\"blockNumber\":0").Replace("\"blockNumber\":42", "\"blockNumber\":0");
                yield return new object[] { kind, parser + "120", badNumber, badNumber, false };
            }
        }

        [Theory]
        [MemberData(nameof(Examples))]
        public void FullEngineDecisionAndXmlBytes(string kind, string source, string legacy, string canonical, bool accepted)
        {
            Assert.Contains(".cs:", source);
            string? oldXml = null, newXml = null;
            var oldError = Record.Exception(() => oldXml = Legacy(kind, legacy));
            var newError = Record.Exception(() => newXml = ConstructionAdapter.BuildCandidate(Read(kind, canonical), "21", ConstructionProfile.FullEngine).Xml);
            Assert.Equal(accepted, oldError == null);
            Assert.Equal(oldError == null, newError == null);
            if (accepted) Assert.Equal(Encoding.UTF8.GetBytes(oldXml!), Encoding.UTF8.GetBytes(newXml!));
        }

        public static IEnumerable<object[]> ClosedCases()
        {
            foreach (var row in Examples().Take(16).Where(r => (bool)r[4]))
            {
                string kind = (string)row[0], json = (string)row[3];
                yield return new object[] { kind, JsonSerializer.Serialize(json) };
                yield return new object[] { kind, "null" };
                yield return new object[] { kind, "[]" };
                var node = JsonNode.Parse(json)!.AsObject();
                node["unknown"] = 1; yield return new object[] { kind, node.ToJsonString() };
                node.Remove("unknown");
                var first = node.First(); node.Remove(first.Key); node[first.Key.ToUpperInvariant()] = first.Value;
                yield return new object[] { kind, node.ToJsonString() };
                yield return new object[] { kind, json.Insert(1, "\"x\":1,\"x\":2,") };
            }
        }

        [Theory]
        [MemberData(nameof(ClosedCases))]
        public void ClosedRootsReject(string kind, string json) => Assert.NotNull(Record.Exception(() => Read(kind, json)));

        [Theory]
        [InlineData("{\"op\":\"newline\",\"indent\":1}")]
        [InlineData("{\"op\":\"if\",\"condition\":\"x\",\"target\":\"y\"}")]
        [InlineData("{\"op\":\"assign\",\"target\":\"x\",\"source\":\"y\",\"literalValue\":\"1\"}")]
        [InlineData("{\"op\":\"assign\",\"target\":\"x\"}")]
        [InlineData("{\"op\":\"assignment\",\"target\":\"x\",\"value\":\"1\"}")]
        [InlineData("{\"op\":\"IF\",\"condition\":\"x\"}")]
        [InlineData("{\"op\":\"line\",\"items\":[{\"sym\":\"x\",\"raw\":\";\"}]}")]
        [InlineData("{\"op\":\"blank\",\"count\":1.5}")]
        [InlineData("{\"op\":\"blank\",\"count\":2147483648}")]
        [InlineData("{\"op\":\"token\",\"text\":null}")]
        public void StatementFieldsAreDiscriminated(string json) => Assert.NotNull(Record.Exception(() => V4Json.Deserialize<Statement>(json)));

        [Fact]
        public void MissingNullAndEmptyStayDistinct()
        {
            var absent = V4Json.Deserialize<Member>(MemberJson);
            var empty = V4Json.Deserialize<Member>(MemberJson.Replace("}", ",\"comment\":\"\"}"));
            Assert.Null(absent.Comment); Assert.Equal("", empty.Comment);
            Assert.DoesNotContain("comment", V4Json.Serialize(absent)); Assert.Contains("\"comment\":\"\"", V4Json.Serialize(empty));
            Assert.NotNull(Record.Exception(() => V4Json.Deserialize<Member>(MemberJson.Replace("}", ",\"comment\":null}"))));
            var optional = Block.Replace("ST", St).Replace("\"inputs\":[],\"outputs\":[],", "");
            Assert.Null(V4Json.Deserialize<FbBlockSpec>(optional).Inputs);
            Assert.Empty(V4Json.Deserialize<FbBlockSpec>(Block.Replace("ST", St)).Inputs!);
            Assert.NotNull(Record.Exception(() => V4Json.Deserialize<FcBlockSpec>(optional)));
            Assert.NotNull(Record.Exception(() => V4Json.Deserialize<FbBlockSpec>(optional.Replace("\"structuredText\":", "\"inputs\":null,\"structuredText\":"))));
        }

        [Theory]
        [InlineData("udt")][InlineData("tagtable")][InlineData("globaldb")][InlineData("fc")][InlineData("fb")]
        public void OuterKindSelectsOnlyMatchingArtifact(string kind)
        {
            var row = Examples().First(r => (string)r[0] == kind && (bool)r[4]);
            var dto = PlcArtifactSpec.Deserialize(kind, (string)row[3]);
            Assert.Equal(V4Json.Serialize(dto), V4Json.Serialize(PlcArtifactSpec.Deserialize(kind, V4Json.Serialize(dto))));
            Assert.NotNull(Record.Exception(() => PlcArtifactSpec.Deserialize(kind, Call)));
        }

        [Theory]
        [InlineData("UDT")][InlineData("type")][InlineData("lad")][InlineData("st")][InlineData("call")][InlineData("")]
        public void UnknownArtifactKindRejected(string kind) => Assert.Throws<ArgumentException>(() => PlcArtifactSpec.Deserialize(kind, "{}"));

        [Fact]
        public void LocalCallCannotSilentlyBecomeGlobal()
        {
            var json = """{"callName":"Run","parameters":[{"name":"x","section":"Input","dataType":"Bool","sourceKind":"local","symbolPath":["x"]}]}""";
            var dto = V4Json.Deserialize<FlgNetCallSpec>(json);
            Assert.Equal(CallSourceKind.Local, dto.Parameters[0].SourceKind);
            foreach (var profile in new[] { ConstructionProfile.FullEngine, ConstructionProfile.Foundation })
                Assert.Throws<ArgumentException>(() => ConstructionAdapter.BuildCandidate(dto, "21", profile));
        }

        [Fact]
        public void NestedFieldsCannotSmuggleRawXmlOrUnusedMembers()
        {
            Assert.NotNull(Record.Exception(() => Read("udt", """{"name":"U","members":[{"name":"x","datatype":"Bool","startValue":"1"}]}""")));
            Assert.NotNull(Record.Exception(() => Read("fc", Block.Replace("ST", St).Replace("\"inputs\":[]", "\"inputs\":[" + MemberJson.Replace("}", ",\"externalWritable\":false}") + "]"))));
            foreach (var field in new[] { "structuredTextInnerXml", "structuredTextXml", "sclInnerXml", "path", "outputPath", "inouts" })
                Assert.NotNull(Record.Exception(() => Read("fc", Block.Replace("ST", St).Insert(1, "\"" + field + "\":\"<!DOCTYPE x>\","))));
            Assert.NotNull(Record.Exception(() => Read("lad", """{"blockName":"L","blockNumber":1,"networks":[{"callJson":CALL}]}""".Replace("CALL", Call))));
        }

        [Fact]
        public void CandidateEvidenceNeverClaimsImportability()
        {
            var result = ConstructionAdapter.BuildCandidate(Read("call", Call), "21", ConstructionProfile.Foundation);
            Assert.False(result.ImportValidated); Assert.False(result.SchemaValidated); Assert.False(result.ProgramSemanticsValidated);
            Assert.Equal("NOT RUN", result.NativeAcceptance); Assert.Equal("21", result.OutputReleaseKey);
            Assert.Contains("Not import-ready", result.Warning);
        }

        [Theory]
        [InlineData("{\"operations\":[]}")]
        [InlineData("{\"firstUid\":0,\"operations\":[{\"op\":\"newline\"}]}")]
        [InlineData("{\"operations\":[{\"op\":\"symbol\",\"name\":\"\\\"DB\\\".a\"}]}")]
        [InlineData("{\"operations\":[{\"op\":\"token\",\"text\":\"x\\t\"}]}")]
        [InlineData("{\"operations\":[{\"op\":\"blank\",\"count\":0}]}")]
        public void FoundationNarrowSyntaxDoesNotReplaceFullEngineSyntax(string json)
        {
            // PlcBuilderToolJson.cs:217 and OfflineCompositionBuilders.cs:55, 70, 199, 225.
            var spec = V4Json.Deserialize<StructuredTextSpec>(json);
            Assert.Equal(Encoding.UTF8.GetBytes(Legacy("st", json)), Encoding.UTF8.GetBytes(
                ConstructionAdapter.BuildCandidate(spec, "21", ConstructionProfile.FullEngine).Xml));
            Assert.Throws<ArgumentException>(() => ConstructionAdapter.BuildCandidate(spec, "21", ConstructionProfile.Foundation));
        }

        [Theory]
        [InlineData("{\"op\":\"if\",\"condition\":\"Ready\",\"indent\":2}")]
        [InlineData("{\"op\":\"elsif\",\"condition\":\"Other\"}")]
        [InlineData("{\"op\":\"else\"}")]
        [InlineData("{\"op\":\"endif\"}")]
        [InlineData("{\"op\":\"assign\",\"target\":\"a\",\"source\":\"b\"}")]
        [InlineData("{\"op\":\"symbol\",\"name\":\"#a.b\"}")]
        [InlineData("{\"op\":\"global\",\"name\":\"DB.a\"}")]
        [InlineData("{\"op\":\"local\",\"name\":\"a\"}")]
        [InlineData("{\"op\":\"literal\",\"value\":\"TRUE\"}")]
        [InlineData("{\"op\":\"token\",\"text\":\"<Injected/>\"}")]
        [InlineData("{\"op\":\"blank\",\"count\":2}")]
        [InlineData("{\"op\":\"newline\"}")]
        [InlineData("{\"op\":\"line\",\"items\":[{\"sym\":\"a\"},{\"token\":\":=\"},{\"lit\":\"1\"},{\"raw\":\";\"}]}")]
        public void EveryStatementBranchRetainsEngineXml(string operation)
        {
            // PlcBuilderToolJson.cs:217-347: primitive emission, line spacing and terminator.
            var dto = V4Json.Deserialize<Statement>(operation);
            Assert.Equal(operation, V4Json.Serialize(dto).Replace("\\u003C", "<").Replace("\\u003E", ">"));
            string json = "{\"operations\":[" + operation + "]}";
            Assert.Equal(Encoding.UTF8.GetBytes(Legacy("st", json)), Encoding.UTF8.GetBytes(
                ConstructionAdapter.BuildCandidate(Read("st", json), "21", ConstructionProfile.FullEngine).Xml));
        }

        [Theory]
        [InlineData("{\"name\":\"x\",\"section\":\"Input\",\"dataType\":\"Bool\",\"sourceKind\":\"global\",\"constantValue\":\"1\"}")]
        [InlineData("{\"name\":\"x\",\"section\":\"Input\",\"dataType\":\"Bool\",\"sourceKind\":\"constant\",\"symbolPath\":[\"x\"]}")]
        [InlineData("{\"name\":\"x\",\"section\":\"Input\",\"dataType\":\"Bool\",\"sourceKind\":\"GLOBAL\",\"symbolPath\":[\"x\"]}")]
        [InlineData("{\"name\":\"x\",\"section\":\"Input\",\"dataType\":\"Bool\",\"sourceKind\":null,\"symbolPath\":[\"x\"]}")]
        [InlineData("{\"name\":\"x\",\"section\":\"Input\",\"dataType\":\"Bool\",\"symbolPath\":\"[\\\"x\\\"]\"}")]
        public void CallBranchesRejectIncompatibleFields(string json) => Assert.NotNull(Record.Exception(() => V4Json.Deserialize<CallParameter>(json)));
    }
}
