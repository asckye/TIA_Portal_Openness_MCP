using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Runtime;
using TiaMcpServer.Siemens;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class V4DomainBoundaryTests
    {
        private static readonly (Type Type, string Json)[] Objects =
        {
            (typeof(Artifact), """{"id":"A"}"""),
            (typeof(NetworkPlan), """{"operations":[]}"""),
            (typeof(BlockEdit), """{"action":"setBlockText","field":"Title","culture":"en-US","expectedValue":"","value":"A"}"""),
            (typeof(TemplateRow), """{"fileName":"A.xml","values":{}}"""),
            (typeof(PlcAliasRow), """{"source":["A"],"destination":["B"]}"""),
            (typeof(PlcSimScenario), """{"instance":"A","steps":[{"waitMs":1}]}"""),
            (typeof(DccPartnerSpec), """{"chartInterface":"A"}"""),
            (typeof(MotionTarget), """{"address":0}"""),
            (typeof(TestScope), """{"kind":"project"}"""),
            (typeof(TeamcenterItemSpec), """{"itemName":"A","teamcenterItemType":"T"}"""),
            (typeof(TeamcenterRevisionSpec), "{}"),
            (typeof(SivarcReference), """{"kind":"masterCopy","path":"F/A"}"""),
            (typeof(LibrarySelection), """{"folder":""}"""),
            (typeof(DynamizationMapping), """{"kind":"Simple","properties":{}}"""),
            (typeof(XPathRule), """{"id":"A","xpath":"//Name"}"""),
            (typeof(LintRules), "{}"),
            (typeof(MonitoringOptions), "{}"),
            (typeof(TemplateIntent), "{}"),
            (typeof(OpenPipeRequest), """{"message":"ReadTag","params":{"tags":[]}}""")
        };
        private static object Read(Type type, string json) => typeof(V4Json).GetMethod(nameof(V4Json.Deserialize))!.MakeGenericMethod(type).Invoke(null, new object[] { json })!;
        public static IEnumerable<object[]> ClosedCases()
        {
            foreach (var sample in Objects)
            {
                yield return new object[] { sample.Type, sample.Json, "unknown" };
                yield return new object[] { sample.Type, sample.Json, "duplicate" };
                yield return new object[] { sample.Type, sample.Json, "casing" };
                yield return new object[] { sample.Type, sample.Json, "doubleEncoded" };
                yield return new object[] { sample.Type, sample.Json, "null" };
            }
        }
        [Theory]
        [MemberData(nameof(ClosedCases))]
        public void V4_boundary_is_closed(Type type, string json, string mutation)
        {
            var value = Read(type, json);
            // Exercise all typed accessors as well as construction, including omitted defaults.
            foreach (var property in value.GetType().GetProperties()) _ = property.GetValue(value);
            var obj = JsonNode.Parse(json)!.AsObject();
            string name = obj.Count == 0 ? "field" : obj.First().Key;
            string invalid = mutation switch
            {
                "unknown" => json.Insert(1, "\"unknown\":0" + (obj.Count == 0 ? "" : ",")),
                "duplicate" => "{\"" + name + "\":0,\"" + name + "\":0}",
                "casing" => "{\"" + char.ToUpperInvariant(name[0]) + name.Substring(1) + "\":0}",
                "doubleEncoded" => JsonSerializer.Serialize(json),
                _ => "null"
            };
            Assert.NotNull(Record.Exception(() => Read(type, invalid)));
        }

        [Theory]
        [InlineData("{\"id\":\"A\",\"dependencies\":null}")]
        [InlineData("{\"id\":\"A\",\"priority\":1.2}")]
        [InlineData("{\"id\":\"A\",\"priority\":2147483648}")]
        [InlineData("{\"id\":\"A\",\"target\":null}")]
        public void Optional_is_not_nullable_and_int32_is_exact(string json) => Assert.NotNull(Record.Exception(() => V4Json.Deserialize<Artifact>(json)));

        [Theory]
        [InlineData("{\"type\":\"SetPlcCpuSettings\",\"cpuPath\":\"PLC_1/CPU_1\",\"settings\":{\"exactAttributes\":{\"Name\":{}}}}")]
        [InlineData("{\"type\":\"EnsureSubnet\",\"anchorDeviceItemPath\":\"PLC_1/CPU_1\",\"subnetName\":\"PN\",\"subnetType\":\"PN\",\"interfaceIndex\":0}")]
        public void Network_discriminator_closes_nested_fields(string operation) => Assert.NotNull(Record.Exception(() => DomainValidation.Read<NetworkPlan>("{\"operations\":[" + operation + "]}")));

        [Theory]
        [InlineData("SetPlcCpuSettings", true)]
        [InlineData("SetCpuCommonSettings", false)]
        public void Cpu_network_operation_uses_only_the_v4_name(string type, bool accepted)
        {
            string operation = "{\"type\":" + JsonSerializer.Serialize(type) + ",\"cpuPath\":\"PLC_1/CPU_1\",\"settings\":{\"exactAttributes\":{\"Name\":\"CPU_1\"}}}";
            string plan = "{\"operations\":[" + operation + "]}";
            Assert.Equal(accepted, HardwareNetworkPlanValidator.Validate(plan)["ok"]!.GetValue<bool>());
            Assert.Equal(accepted, DomainValidation.Contract<NetworkPlan>().Read(plan, "plan").IsValid);
            Assert.Equal(accepted, DomainValidation.Contract<CpuSettingsOperation>().Read(operation, "operation").IsValid);
        }

        [Fact]
        public void Scalars_and_steps_preserve_values_order_and_dictionary_case()
        {
            const string json = """{"instance":"PLC_1","steps":[{"write":{"A":1,"a":null}},{"write":{"A":2}},{"waitMs":2},{"assert":{"A":2}}]}""";
            var scenario = DomainValidation.Read<PlcSimScenario>(json);
            Assert.Equal(4, scenario.Steps.Count);
            Assert.Equal(1, ((PlcSimWriteStep)scenario.Steps[0]).Write["A"].Number);
            Assert.Equal(JsonValueKind.Null, ((PlcSimWriteStep)scenario.Steps[0]).Write["a"].Kind);
            Assert.Equal(2, ((PlcSimWriteStep)scenario.Steps[1]).Write["A"].Number);
            Assert.Equal(json, V4Json.Serialize(scenario));
            Assert.NotNull(Record.Exception(() => DomainValidation.Read<PlcSimScenario>(json.Replace("\"A\":1", "\"A\":[]"))));
            Assert.NotNull(Record.Exception(() => DomainValidation.Read<PlcSimScenario>(json.Replace("{\"waitMs\":2}", "{\"waitMs\":2,\"write\":{\"A\":1}}"))));
        }
        [Fact]
        public void Empty_rules_retain_the_current_default_policy()
        {
            var rules = DomainValidation.Read<LintRules>("{}"); var old = PlcDocumentationLogic.ParseLintOptions("{}");
            Assert.Equal(old.MaxLineLength, rules.MaxLineLength); Assert.Equal(old.MaxNesting, rules.MaxNesting);
            Assert.Equal(old.Markers, rules.Markers); Assert.Empty(DomainValidation.Read<XPathRule[]>("[]"));
            Assert.NotNull(Record.Exception(() => DomainValidation.Read<LintRules>("\"{}\"")));
        }
        [Theory]
        [InlineData("deviceItems", "actor", true)]
        [InlineData("plcTag", "torque", false)]
        [InlineData("addresses", "encoder", true)]
        [InlineData("addresses", "sensor", false)]
        [InlineData("address", "outputCam", true)]
        [InlineData("address", "actor", false)]
        public void Motion_overload_restrictions_match_the_current_validator(string mode, string aspect, bool accepted)
        {
            // MotionProDiagClassicHmiLogic.cs:45-53,168-171.
            string json = mode switch { "deviceItems" => """{"devicePath":["PLC_1"],"itemPath":["A"],"secondItemPath":["B"]}""",
                "plcTag" => """{"plcTagPath":"T"}""", "addresses" => """{"inputBitAddress":0,"outputBitAddress":8}""", _ => """{"address":0}""" };
            var target = DomainValidation.Read<MotionTarget>(json);
            Assert.Equal(accepted, Record.Exception(() => MotionProDiagClassicHmiLogic.RequireConnectionMode(aspect, mode)) == null);
            Assert.Equal(accepted, Record.Exception(() => DomainValidation.Motion(target, aspect, "connect", "21", true, false)) == null);
        }
        [Theory]
        [InlineData("19", true, false)]
        [InlineData("21", false, false)]
        [InlineData("20", true, true)]
        public void Missing_version_package_or_write_access_is_rejected(string release, bool available, bool readOnly)
        {
            var partner = DomainValidation.Read<DccPartnerSpec>("""{"chartInterface":"I"}""");
            Assert.Throws<NotSupportedException>(() => DomainValidation.DccPartner(partner, "connect", release, available, readOnly));
        }
        [Fact]
        public void Motion_preserves_explicit_default_overload_and_ident_action()
        {
            const string dual = "{\"devicePath\":[\"PLC_1\"],\"itemPath\":[\"A\"],\"secondItemPath\":[\"B\"]}";
            var omitted = DomainValidation.Read<MotionTarget>(dual);
            var explicitDefault = DomainValidation.Read<MotionTarget>(dual.Insert(1, "\"connectOption\":\"Default\","));
            Assert.Equal(omitted.ConnectOption, explicitDefault.ConnectOption);
            Assert.False(omitted.HasConnectOption); Assert.True(explicitDefault.HasConnectOption);
            DomainValidation.Motion(DomainValidation.Read<MotionTarget>("{\"devicePath\":[\"PLC_1\"],\"itemPath\":[\"A\"]}"), "", "connectIdent", "21", true, false);
            Assert.Throws<ArgumentException>(() => DomainValidation.Motion(omitted, "", "connectIdent", "21", true, false));
        }
        [Fact]
        public void Action_limits_and_null_references_are_retained()
        {
            var references = DomainValidation.Read<Dictionary<string, SivarcReference?>>("""{"LibraryScreen":null}""");
            var devices = DomainValidation.Read<Dictionary<string, bool>>("""{"HMI_1":false}""");
            DomainValidation.Sivarc(references, devices, "screens", "rule", "update", "21", true, false);
            Assert.Null(references["LibraryScreen"]); Assert.False(devices["HMI_1"]);
            Assert.Equal("{\"LibraryScreen\":null}", V4Json.Serialize(references));
            Assert.Throws<ArgumentException>(() => DomainValidation.Sivarc(references, devices, "screens", "rule", "read", "21", true, true));
            Assert.Throws<ArgumentException>(() => DomainValidation.Sivarc(new Dictionary<string, SivarcReference?>(), devices, "tags", "rule", "update", "21", true, false));
            Assert.Throws<ArgumentException>(() => DomainValidation.DccPartner(DomainValidation.Read<DccPartnerSpec>("{\"chartInterface\":\"I\"}"), "read", "21", true, false));
            var scope = DomainValidation.Read<TestScope[]>("[{\"kind\":\"project\"}]");
            Assert.Throws<ArgumentException>(() => DomainValidation.TestScopes(scope, "application", "setScope", "case", "21", true, false));
            Assert.Throws<ArgumentException>(() => DomainValidation.Teamcenter(DomainValidation.Read<TeamcenterItemSpec>("{\"itemName\":\"A\",\"teamcenterItemType\":\"T\"}"), null, "save", "21", true, false));
            DomainValidation.Library(DomainValidation.Read<LibrarySelection[]>("[{\"folder\":\"\"}]"), new[] { "HarmonizeNames", "HarmonizePaths" }, "harmonizeProject", "21", true, false, "", "", new[] { "PLC_1" });
        }
        [Theory]
        [InlineData("Simple")]
        [InlineData("Range")]
        [InlineData("Bitmask")]
        public void Mapping_requires_writable_version_metadata(string kind)
        {
            var entries = DomainValidation.Read<DynamizationMapping[]>("[{\"kind\":\"" + kind + "\",\"properties\":{\"Value\":1}}]");
            Assert.Throws<NotSupportedException>(() => DomainValidation.Dynamization(entries, "update", "20", true, false, new Dictionary<string, Type>()));
            Assert.Throws<NotSupportedException>(() => DomainValidation.Dynamization(entries, "update", "21", true, false, new Dictionary<string, Type> { [kind] = typeof(V4DomainEquivalenceTests.MappingFixture) }, false));
            Assert.Throws<ArgumentException>(() => DomainValidation.Dynamization(entries, "read", "21", true, true, new Dictionary<string, Type>()));
        }
        [Theory]
        [InlineData("{\"message\":\"ReadTag\",\"params\":{\"tags\":[\"中文\"]}}", "ReadTag")]
        [InlineData("{\"message\":\"WriteTag\",\"params\":{\"tags\":[{\"name\":\"A\",\"value\":1}]}}", "WriteTag")]
        [InlineData("{\"message\":\"ReadAlarm\",\"params\":{\"systemNames\":[],\"filter\":\"\",\"languageId\":1033}}", "ReadAlarm")]
        [InlineData("{\"message\":\"BrowseTags\",\"params\":{\"filter\":\"*Motor*\",\"pageSize\":50}}", "BrowseTags")]
        public void OpenPipe_restores_protocol_casing(string json, string message)
        {
            var request = DomainValidation.Read<OpenPipeRequest>(json); string wire = request.ToWire("fixed");
            var parsed = RuntimeChannelsLogic.PrepareRawRequest(wire);
            Assert.Equal(message, parsed.message); Assert.Equal("fixed", parsed.cookie); Assert.Equal(wire, parsed.line);
            Assert.Equal(message != "WriteTag", request.IsReadOnly);
            var node = JsonNode.Parse(wire)!; Assert.Null(node["message"]); Assert.NotNull(node["Params"]);
            if (message == "WriteTag") { Assert.NotNull(node["Params"]!["Tags"]![0]!["Name"]); Assert.NotNull(node["Params"]!["Tags"]![0]!["Value"]); }
        }
        [Fact]
        public void Schema_contains_closed_unions_limits_and_exact_dictionary_keys()
        {
            Assert.Equal(8, DomainSchemas.Get<MotionTarget>()["oneOf"]!.AsArray().Count);
            Assert.False(DomainSchemas.Get<Artifact>()["additionalProperties"]!.GetValue<bool>());
            Assert.Equal(256, DomainSchemas.Get<Artifact[]>()["maxItems"]!.GetValue<int>());
            Assert.Equal(3, DomainSchemas.Get<BlockEdit>()["oneOf"]!.AsArray().Count);
            Assert.Equal(3, DomainSchemas.Get<PlcSimStep>()["oneOf"]!.AsArray().Count);
            Assert.Equal(5, DomainSchemas.Get<OpenPipeRequest>()["oneOf"]!.AsArray().Count);
            Assert.NotNull(DomainSchemas.Get<GraphicGeometry>()["properties"]!["Left"]);
            var schema = DomainSchemas.Get<Artifact>(); schema["additionalProperties"] = true;
            Assert.False(DomainSchemas.Get<Artifact>()["additionalProperties"]!.GetValue<bool>());
        }
    }
}
