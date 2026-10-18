using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Runtime;
using TiaMcpServer.Siemens;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class V4DomainEquivalenceTests : IDisposable
    {
        private readonly string directory = Path.Combine(AppContext.BaseDirectory, "domain-equivalence-" + Guid.NewGuid().ToString("N"));
        private const string Xml = "<Document><SW.Blocks.FC ID='0'><AttributeList><Name>Main</Name><Interface><Sections><Section Name='Input'><Member Name='Speed' Datatype='Int'><StartValue>1</StartValue></Member></Section></Sections></Interface></AttributeList><ObjectList><MultilingualText CompositionName='Title'><ObjectList><MultilingualTextItem><AttributeList><Culture>en-US</Culture><Text>Old</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText><SW.Blocks.CompileUnit ID='1'><AttributeList/><ObjectList><MultilingualText CompositionName='Comment'><ObjectList><MultilingualTextItem><AttributeList><Culture>en-US</Culture><Text>Old</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>";
        public V4DomainEquivalenceTests()
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "template.xml"), "<Document><Name>{{Name}}</Name></Document>");
        }
        public void Dispose() => Directory.Delete(directory, true);
        public sealed class MappingFixture { public int Value { get; set; } public int Lower { get; set; } public int Upper { get; set; } public int Mask { get; set; } public int ReadOnly => 1; }

        // Appendix B: each row is a current-parser sample after the section-2 casing /
        // field conversion. Rejections here are legacy decisions; new closed-boundary
        // restrictions are tested separately, rather than called parser equivalence.
        public static IEnumerable<object[]> Samples()
        {
            var rows = new (string Family, string Source, string Good, string Bad)[]
            {
                ("Artifact", "src/Shared/ImportDependencyPlanner.cs:32", """[{"id":"B","dependencies":["A"]},{"id":"A"}]""", """[{"id":"A"},{"id":"a"}]"""),
                ("Artifact", "src/Shared/ImportDependencyPlanner.cs:47", """[{"id":"A","target":"Z","priority":3},{"id":"B","priority":-1}]""", """[{"id":"A","dependencies":["B"]},{"id":"B","dependencies":["A"]}]"""),
                ("Artifact", "src/Shared/ImportDependencyPlanner.cs:54", """[{"id":"A","dependencies":[]}]""", """[{"id":"A","dependencies":["missing"]}]"""),
                ("Network", "src/Logic/ModelContextProtocol/Builders/HardwareNetworkPlanValidator.cs:78", """{"operations":[{"type":"EnsureSubnet","anchorDeviceItemPath":"PLC_1/CPU_1","subnetName":"PN_1","subnetType":"PN/IE","ip":"192.168.1.2","mask":"255.255.255.0"}]}""", """{"operations":[{"type":"EnsureSubnet","anchorDeviceItemPath":"CPU","subnetName":"PN_1","subnetType":"PN"}]}"""),
                ("Network", "src/Logic/ModelContextProtocol/Builders/HardwareNetworkPlanValidator.cs:91", """{"operations":[{"type":"AttachDeviceNodeToSubnet","deviceItemPath":"PLC_1/CPU_1","subnetName":"PN_1","interfaceIndex":0}]}""", """{"operations":[{"type":"AttachDeviceNodeToSubnet","deviceItemPath":"PLC_1/CPU_1","subnetName":"PN_1","interfaceIndex":-1}]}"""),
                ("Network", "src/Logic/ModelContextProtocol/Builders/HardwareNetworkPlanValidator.cs:107", """{"operations":[{"type":"SetPlcCpuSettings","cpuPath":"PLC_1/CPU_1","settings":{"exactAttributes":{"Name":"PLC_1"}}}]}""", """{"operations":[{"type":"SetPlcCpuSettings","cpuPath":"PLC_1/CPU_1","settings":{"exactAttributes":{"ip":"a"}}}]}"""),
                ("BlockEdit", "src/Logic/ModelContextProtocol/Builders/PlcDocumentEditing.cs:120", """[{"action":"setBlockText","field":"Title","culture":"en-US","expectedValue":"Old","value":"New & safe"}]""", """[{"action":"setBlockText","field":"Title","culture":"en-US","expectedValue":"stale","value":"New"}]"""),
                ("BlockEdit", "src/Logic/ModelContextProtocol/Builders/PlcDocumentEditing.cs:152", """[{"action":"setNetworkText","networkIndex":0,"field":"Comment","culture":"en-US","expectedValue":"Old","value":"New"}]""", """[{"action":"setNetworkText","networkIndex":1,"field":"Comment","culture":"en-US","expectedValue":"Old","value":"New"}]"""),
                ("BlockEdit", "src/Logic/ModelContextProtocol/Builders/PlcDocumentEditing.cs:137", """[{"action":"setMemberStartValue","section":"Input","memberPath":"Speed","expectedValue":"1","value":"2"}]""", """[{"action":"setMemberStartValue","section":"Input","memberPath":"Missing","expectedValue":"1","value":"2"}]"""),
                ("TemplateRow", "src/Logic/ModelContextProtocol/Builders/PlcTemplateExpansion.cs:21", """[{"fileName":"FC_1.xml","values":{"Name":"FC_1 & A"}}]""", """[{"fileName":"../FC_1.xml","values":{"Name":"FC_1"}}]"""),
                ("PlcAliasRow", "src/Logic/ModelContextProtocol/Builders/PlcAliasAlarmBuilder.cs:14", """[{"source":["Input"],"destination":["DB","Alarm"],"acknowledge":["Ack"],"invert":true}]""", """[{"source":["Input[0]"],"destination":["DB","Alarm"]}]"""),
                ("PlcSimScenario", "src/Logic/Runtime/PlcSimAdvancedLogic.cs:276", """{"instance":" PLC_1 ","mode":"default","steps":[{"write":{"Start":true}},{"waitMs":20},{"assert":{"Running":true},"tolerance":0.01,"note":"ready"}]}""", """{"instance":"PLC_1","steps":[{"waitMs":0}]}"""),
                ("DccPartner", "src/Logic/Siemens/DccLogic.cs:99", """{"block":"B","pin":"IN1"}""", """{"block":"B"}"""),
                ("DccPartner", "src/Logic/Siemens/DccLogic.cs:109", """{"chartInterface":"Input"}""", """{"chartInterface":"Input","pin":"IN1"}"""),
                ("MotionTarget", "src/Engine/Siemens/MotionProDiagClassicHmiLogic.cs:111", """{"devicePath":["PLC_1"],"itemPath":["CPU"]}""", """{"devicePath":["PLC_1"]}"""),
                ("MotionTarget", "src/Engine/Siemens/MotionProDiagClassicHmiLogic.cs:151", """{"devicePath":["PLC_1"],"itemPath":["CPU"],"secondItemPath":["Encoder"],"connectOption":"Default"}""", """{"devicePath":["PLC_1"],"itemPath":["CPU"],"secondItemPath":["Encoder"],"channelIndex":0}"""),
                ("MotionTarget", "src/Engine/Siemens/MotionProDiagClassicHmiLogic.cs:152", """{"devicePath":["PLC_1"],"itemPath":["CPU"],"channelIndex":0}""", """{"channelIndex":0}"""),
                ("MotionTarget", "src/Engine/Siemens/MotionProDiagClassicHmiLogic.cs:147", """{"devicePath":["PLC_1"],"itemPath":["CPU"],"channelType":"DigitalInput","channelIoType":"Input","channelNumber":0}""", """{"devicePath":["PLC_1"],"itemPath":["CPU"],"channelType":"DigitalInput"}"""),
                ("MotionTarget", "src/Engine/Siemens/MotionProDiagClassicHmiLogic.cs:155", """{"dbMemberPath":"DB.Axis"}""", """{"dbMemberPath":"DB.Axis","plcTagPath":"Tag"}"""),
                ("MotionTarget", "src/Engine/Siemens/MotionProDiagClassicHmiLogic.cs:156", """{"plcTagPath":"Tag"}""", """{"plcTagPath":"Tag","connectOption":"Default"}"""),
                ("MotionTarget", "src/Engine/Siemens/MotionProDiagClassicHmiLogic.cs:159", """{"inputBitAddress":0,"outputBitAddress":8,"connectOption":"Default"}""", """{"inputBitAddress":0}"""),
                ("MotionTarget", "src/Engine/Siemens/MotionProDiagClassicHmiLogic.cs:162", """{"address":0}""", """{"address":2147483648}"""),
                ("TestScope", "src/Logic/Siemens/TestSuiteLogic.cs:96", """[{"kind":"blocks","softwarePath":"PLC_1","groupPath":"Group"},{"kind":"deviceGroup","name":"Group"},{"kind":"project"}]""", """[{"kind":"project","name":"Group"}]"""),
                ("TeamcenterItemSpec", "src/Logic/Siemens/TeamcenterLogic.cs:93", """{"itemName":"Project","teamcenterItemType":"T4TiaProject","teamcenterProject":["P1"]}""", """{"itemName":"Project"}"""),
                ("RevisionSpec", "src/Logic/Siemens/TeamcenterLogic.cs:105", """{"revisionId":"B","comment":"new"}""", """{"revisionId":2}"""),
                ("SivarcReference", "src/Logic/Siemens/SivarcLogic.cs:162", """{"ProgramBlock":{"kind":"plcBlock","softwarePath":"PLC_1","path":"Blocks/FB1"},"LibraryScreen":null}""", """{"ProgramBlock":{"kind":"plcBlock","path":"FB1"}}"""),
                ("DeviceSelection", "src/Logic/Siemens/SivarcLogic.cs:144", """{"PLC_1":true,"HMI_1":false}""", """{"PLC_1":"true"}"""),
                ("LibrarySelection", "src/Logic/Siemens/LibraryDeepLogic.cs:80", """[{"folder":" / "},{"type":" /Folder/Type/ "}]""", """[{"type":"T"},{"type":"t"}]"""),
                ("DynamizationMapping", "src/Adapters/Native/Hmi/UnifiedUiModelLogic.cs:281", """[{"kind":"Simple","properties":{"Value":1}},{"kind":"Range","properties":{"Lower":0,"Upper":2}},{"kind":"Bitmask","properties":{"Mask":3}}]""", """[{"kind":"Simple","properties":{"ReadOnly":1}}]"""),
                ("XPathRule", "src/Logic/ModelContextProtocol/Builders/EngineeringQualityAudit.cs:23", """[{"id":"name","xpath":"//Name","minCount":0,"maxCount":2}]""", """[{"id":"name","xpath":"//Name","minCount":2,"maxCount":1}]"""),
                ("LintRules", "src/Logic/ModelContextProtocol/Builders/PlcDocumentationLogic.cs:526", """{"disabled":["SCL007"],"maxLineLength":80,"markers":["TODO"]}""", """{"maxLineLength":39}"""),
                ("LintRules", "src/Logic/ModelContextProtocol/Builders/PlcDocumentationLogic.cs:534", """{"disabled":["SCL007","scl007"],"markers":["","TODO"]}""", """{"maxNesting":51}"""),
                ("PlcSimScenario", "src/Logic/Runtime/PlcSimAdvancedLogic.cs:284", """{"instance":"PLC_1","mode":"DEFAULT","steps":[{"waitMs":1}]}""", """{"instance":"PLC_1","mode":"other","steps":[{"waitMs":1}]}"""),
                ("MonitoringOptions", "src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs:530", """{"pollMs":1000,"source":"watch-table-export"}""", "{"),
                ("TemplateIntent", "src/Engine/ModelContextProtocol/Tools/LibraryTools.cs:238", """{"screenType":"overview","targetRuntime":"Unified","preferredComponents":["Button"]}""", "{"),
                ("OpenPipeRequest", "src/Logic/Runtime/RuntimeChannelsLogic.cs:335", """{"message":"ReadTag","params":{"tags":["Tag_1"]},"clientCookie":"sample"}""", """{"message":"SubscribeTag","params":{"tags":["Tag_1"]}}""")
            };
            foreach (var row in rows)
            {
                yield return new object[] { row.Family, row.Source, row.Good, true };
                yield return new object[] { row.Family, row.Source, row.Bad, false };
            }
        }

        [Theory]
        [InlineData("readOnly")]
        [InlineData("libraryBound")]
        [InlineData("duplicateSelector")]
        public void Block_edit_document_preconditions_remain_in_validation(string condition)
        {
            // PlcDocumentEditing.cs:124,144-147,165.
            const string edit = "{\"action\":\"setMemberStartValue\",\"section\":\"Input\",\"memberPath\":\"Speed\",\"expectedValue\":\"1\",\"value\":\"2\"}";
            string xml = condition == "readOnly" ? Xml.Replace("Name='Speed'", "Name='Speed' ReadOnly='true'")
                : condition == "libraryBound" ? Xml.Replace("<Name>Main</Name>", "<Name>Main</Name><LibraryTypeGuid>A</LibraryTypeGuid>") : Xml;
            string changes = "[" + edit + (condition == "duplicateSelector" ? "," + edit : "") + "]";
            string hash = PlcDocumentEditing.HashText(PlcDocumentEditing.Canonical(PlcDocumentEditing.Parse(xml)));
            Assert.NotNull(Record.Exception(() => PlcDocumentEditing.Patch(xml, changes, hash)));
            Assert.NotNull(Record.Exception(() => DomainValidation.BlockEdits(DomainValidation.Read<BlockEdit[]>(changes), xml, hash)));
        }


        private static JsonObject LegacyOptionalObject(string json) => string.IsNullOrWhiteSpace(json)
            ? new JsonObject() : JsonNode.Parse(json) as JsonObject ?? new JsonObject();
    }
}
