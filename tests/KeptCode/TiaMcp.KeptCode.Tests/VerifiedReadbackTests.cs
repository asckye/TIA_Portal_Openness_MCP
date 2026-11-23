using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class VerifiedReadbackTests
    {
        private static object? Plain(JsonNode? node)
        {
            var method = typeof(PlcVerifiedImport).GetMethod("PlainValue");
            Assert.NotNull(method);
            var plain = method.CreateDelegate<Func<JsonNode?, object?>>();
            return plain(node);
        }

        [Fact]
        public void Plain_value_keeps_booleans_and_numbers()
        {
            var node = JsonNode.Parse("""
                {"flag":false,"count":7,"fraction":1.25,"text":"fixture","nothing":null,"nested":{"flag":true},"items":[false,9]}
                """);
            var value = Assert.IsType<Dictionary<string, object?>>(Plain(node));
            Assert.False(Assert.IsType<bool>(value["flag"]));
            Assert.Equal(7L, Assert.IsType<long>(value["count"]));
            Assert.Equal(1.25, Assert.IsType<double>(value["fraction"]));
            Assert.Equal("fixture", Assert.IsType<string>(value["text"]));
            Assert.Null(value["nothing"]);
            Assert.True(Assert.IsType<bool>(Assert.IsType<Dictionary<string, object?>>(value["nested"])["flag"]));
            var items = Assert.IsType<object?[]>(value["items"]);
            Assert.False(Assert.IsType<bool>(items[0]));
            Assert.Equal(9L, Assert.IsType<long>(items[1]));
            Assert.Equal(7L, Assert.IsType<long>(Plain(JsonValue.Create(7))));
            Assert.False(Assert.IsType<bool>(Plain(JsonValue.Create(false))));
            Assert.Equal(1.25, Assert.IsType<double>(Plain(JsonValue.Create(1.25))));
            Assert.Null(Plain(null));
        }

        [Fact]
        public void Negative_verdict_survives_complete()
        {
            var meta = Assert.IsType<Dictionary<string, object?>>(Plain(JsonNode.Parse(
                """{"verificationSuccess":false,"operationSuccess":false}""")));
            LegacyResponseMetadata.Complete(meta);
            Assert.Equal(false, meta["operationSuccess"]);
            Assert.Equal(false, meta["verificationSuccess"]);
            Assert.Equal(false, meta["success"]);
        }

        [Fact]
        public void Patch_round_trips_an_encoded_member_and_matches_the_exported_culture()
        {
            string xml = Xml(false).Replace("Name=\"Speed\"", "Name=\"Line/A%1\"");
            var inspected = PlcDocumentEditing.Inspect(xml);
            string changes = """
                [{"action":"setMemberStartValue","section":"Input","memberPath":"Line%2FA%251","expectedValue":"1","value":"2"},
                 {"action":"setMemberComment","section":"Input","memberPath":"Line%2FA%251","culture":"en-us","expectedValue":"","value":"Motor"},
                 {"action":"setNetworkText","networkIndex":0,"field":"Title","culture":"en-us","expectedValue":"Original","value":"Edited"}]
                """;
            var patched = PlcDocumentEditing.Patch(xml, changes, (string)inspected["documentFingerprint"]!);
            Assert.Contains("<StartValue>2</StartValue>", patched);
            Assert.Contains("<Text>Edited</Text>", patched);
            Assert.Contains("<MultiLanguageText Lang=\"en-US\">Motor</MultiLanguageText>", patched);
            Assert.Contains("Line%2FA%251", inspected.ToJsonString());
            string udt = xml.Replace("SW.Blocks.FC", "SW.Types.PlcStruct");
            Assert.Contains("ImportPlcType", Assert.Throws<AdapterPreconditionException>(() => PlcVerifiedImport.CandidateName(udt)).Message);
        }

        private static string Xml(bool readOnly) => """
            <Document><Engineering version="V21"/><SW.Blocks.FC ID="0"><AttributeList>
            <Name>FC_Fixture</Name><Number>7</Number><ProgrammingLanguage>LAD</ProgrammingLanguage>
            <MemoryLayout>Optimized</MemoryLayout><HeaderAuthor>Engineer</HeaderAuthor>
            <CodeModifiedDate>2026-01-01T00:00:00.0000000Z</CodeModifiedDate>
            <InterfaceModifiedDate>2026-01-01T00:00:00.0000000Z</InterfaceModifiedDate>
            <ParameterModified>2026-01-01T00:00:00.0000000Z</ParameterModified>
            <StructureModified>2026-01-01T00:00:00.0000000Z</StructureModified>
            <LoadMemoryLength>3757</LoadMemoryLength><WorkMemoryLength>1024</WorkMemoryLength>
            <Interface><Sections xmlns="urn:interface"><Section Name="Input">
            <Member Name="Speed" Datatype="Int"><StartValue>1</StartValue></Member>
            </Section></Sections></Interface></AttributeList><ObjectList>
            <SW.Blocks.CompileUnit ID="1"><AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage>
            <NetworkSource><FlgNet xmlns="urn:flg"><Parts><Access Scope="GlobalVariable" UId="10">
            <Symbol><Component Name="Motor"/></Symbol></Access></Parts><Wires/></FlgNet></NetworkSource>
            </AttributeList><ObjectList><MultilingualText ID="2" CompositionName="Title"><ObjectList>
            <MultilingualTextItem ID="3" CompositionName="Items"><AttributeList>
            <Culture>en-US</Culture><Text>Original</Text></AttributeList></MultilingualTextItem>
            </ObjectList></MultilingualText></ObjectList></SW.Blocks.CompileUnit>
            </ObjectList></SW.Blocks.FC></Document>
            """.Replace("<CodeModifiedDate>", readOnly ? "<CodeModifiedDate ReadOnly=\"true\">" : "<CodeModifiedDate>");

        private sealed class Fixture : IDisposable
        {
            private readonly string directory = Path.Combine(Path.GetTempPath(), "tia-verified-readback-" + Guid.NewGuid().ToString("N"));
            private readonly string candidateXml;
            internal string Current;
            internal string After;
            internal int Imports;
            internal Func<string, string, bool>? ExportType;
            internal readonly List<string> Calls = new();
            internal Fixture(string xml) { candidateXml = xml; Current = xml; After = xml; }
            private void Export(string path) { Calls.Add(Path.GetFileName(path)); File.WriteAllText(path, Current); }
            private void Import(string path) { Calls.Add("import"); Assert.True(File.Exists(path)); Imports++; Current = File.ReadAllText(path); }
            private void Compile() { Calls.Add("compile"); Current = After; }
            internal JsonObject Run(bool preview, string token = "", JsonObject? meta = null)
            {
                meta ??= new JsonObject();
                PlcVerifiedImport.Execute(candidateXml, "PLC/" + PlcVerifiedImport.CandidateName(candidateXml), directory,
                    preview, token, Export, Import, () => { }, meta, Compile, ExportType);
                return meta;
            }
            public void Dispose()
            {
                string target = Path.GetFullPath(directory);
                Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), target, StringComparison.OrdinalIgnoreCase);
                if (Directory.Exists(target)) Directory.Delete(target, true);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Reimport_of_an_unchanged_block_matches_despite_computed_attributes(bool readOnly)
        {
            using var fixture = new Fixture(Xml(readOnly));
            fixture.After = fixture.Current.Replace("2026-01-01", "2026-02-02").Replace("3757", "4096").Replace("1024", "2048");
            var preview = fixture.Run(true);
            var result = fixture.Run(false, (string)preview["token"]!);
            Assert.Equal("DocumentReadbackMatched", (string?)result["status"]);
            Assert.True((bool?)result["verificationSuccess"]);
            Assert.Null(result["cleanupRequired"]);
            Assert.Equal((string?)result["plannedFingerprint"], (string?)result["actualFingerprint"]);
            Assert.Equal(1, fixture.Imports);
        }

        [Fact]
        public void V17_global_db_readback_matches_when_TIA_materializes_member_defaults()
        {
            const string xml = """
                <Document><Engineering version="V17"/><SW.Blocks.GlobalDB ID="0"><AttributeList>
                <Name>MCP_RC_DB2</Name><Number>9802</Number><ProgrammingLanguage>DB</ProgrammingLanguage>
                <Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v4"><Section Name="Static">
                <Member Name="Ready" Datatype="Bool"><StartValue>FALSE</StartValue></Member>
                <Member Name="Count" Datatype="Int"><StartValue>0</StartValue></Member>
                </Section></Sections></Interface></AttributeList></SW.Blocks.GlobalDB></Document>
                """;
            using var fixture = new Fixture(xml);
            fixture.After = xml.Replace("<Member ", "<Member Remanence=\"NonRetain\" ");
            var preview = fixture.Run(true);
            var result = fixture.Run(false, (string)preview["token"]!);
            Assert.Equal(1, fixture.Imports);
            Assert.Equal("DocumentReadbackMatched", (string?)result["status"]);
            Assert.True((bool?)result["verificationSuccess"]);
            Assert.Equal((string?)result["plannedFingerprint"], (string?)result["actualFingerprint"]);
        }

        [Theory]
        [InlineData("", true)]
        [InlineData("member", false)]
        [InlineData("type", false)]
        [InlineData("value", false)]
        [InlineData("retain", false)]
        [InlineData("declared", false)]
        [InlineData("noPriorExpansion", false)]
        public void Compiled_V14_DB_verifies_referenced_members_against_the_preimport_export(string change, bool matches)
        {
            string folder = Path.Combine(Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
                "tests/FoundationHost/TiaMcp.FoundationHost.Tests/Fixtures/V14Sp1");
            var document = XDocument.Load(Path.Combine(folder, "155-global-db.xml"));
            PlcDocumentEditing.Child(PlcDocumentEditing.Child(PlcDocumentEditing.Block(document), "AttributeList"), "Name").Value = "MCP_RC_DB";
            var section = document.Descendants().Single(e => e.Name.LocalName == "Section");
            XNamespace ns = section.Name.Namespace;
            section.ReplaceNodes(new XElement(ns + "Member", new XAttribute("Name", "Count"), new XAttribute("Datatype", "Int"),
                new XElement(ns + "StartValue", "3")), new XElement(ns + "Member", new XAttribute("Name", "Motor"),
                new XAttribute("Datatype", "\"MCP_RC_UDT\"")));
            var motor = section.Elements().Last();
            var xml = document.ToString();
            var members = CompiledUdtMembers(folder, ns);
            Assert.Equal(2, members.Length);
            motor.Add(new XElement(ns + "Sections", new XElement(ns + "Section", new XAttribute("Name", "None"), members)));
            using var fixture = new Fixture(xml) { Current = change == "noPriorExpansion" ? xml : document.ToString() };
            var declaration = ProjectUdt(folder);
            fixture.ExportType = (name, path) => {
                // This case has neither a prior expansion nor an independent declaration.
                if (change == "noPriorExpansion" || name != "MCP_RC_UDT") return false;
                declaration.Save(path);
                return true;
            };
            foreach (var member in section.Elements()) member.SetAttributeValue("Remanence", "NonRetain");
            switch (change)
            {
                case "member": members[0].SetAttributeValue("Name", "Ready"); break;
                case "type": members[1].SetAttributeValue("Datatype", "DInt"); break;
                case "value": members[1].Add(new XElement(ns + "StartValue", "4")); break;
                case "retain": motor.SetAttributeValue("Remanence", "Retain"); break;
                case "declared": section.Elements().First().Element(ns + "StartValue")!.Value = "4"; break;
            }
            fixture.After = document.ToString();
            var preview = fixture.Run(true);
            var applied = fixture.Run(false, (string)preview["token"]!, new JsonObject { ["softwarePath"] = "MCP_RC_PLC",
                ["blockPath"] = "MCP_RC_Grp/MCP_RC_DB" });
            Assert.Equal(matches, (bool?)applied["verificationSuccess"]);
            Assert.Equal(matches ? "DocumentReadbackMatched" : "DocumentReadbackMismatch", (string?)applied["status"]);
            if (!matches) Assert.Equal("MCP_RC_PLC/MCP_RC_Grp/MCP_RC_DB", (string?)applied["cleanupRequired"]![0]!["target"]);
        }

        private static XElement[] CompiledUdtMembers(string folder, XNamespace ns)
        {
            var diff = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "171-interface-diff.json")))!.AsArray();
            return diff.Select(line => (string)line!).Where(line => line.StartsWith("+[Interface] None ", StringComparison.Ordinal))
                .Select(line => line.Substring("+[Interface] None ".Length).Split(" : "))
                .Select(pair => new XElement(ns + "Member", new XAttribute("Name", pair[0]), new XAttribute("Datatype", pair[1]))).ToArray();
        }

        private static XDocument ProjectUdt(string folder)
        {
            var udt = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "065-067-declarations.json")))!["udt"]!;
            return PlcUdtXmlBuilder.BuildDocument((string)udt["name"]!, udt["members"]!.AsArray()
                .Select(m => new PlcUdtMemberDefinition((string)m!["name"]!, (string)m["datatype"]!)), "14sp1");
        }

        [Theory]
        [InlineData("full", null)]
        [InlineData("oldWitness", null)]
        [InlineData("exportIds", null)]
        [InlineData("struct", null)]
        [InlineData("nested", null)]
        [InlineData("nestedType", "Static/Motor/Drive/Config/Speed: type")]
        [InlineData("rename", "Static/Motor/Run: name")]
        [InlineData("reorder", "Static/Motor/Run: order")]
        [InlineData("extra", "Static/Motor/Extra: extra member")]
        [InlineData("missing", "Static/Motor/Run: missing member")]
        [InlineData("type", "Static/Motor/Speed: type")]
        [InlineData("value", "Static/Motor/Speed: startValue")]
        [InlineData("topValue", "Static/Count: startValue")]
        [InlineData("topType", "Static/Count: type")]
        [InlineData("retain", "Static/Motor: remanence")]
        [InlineData("block", "MemoryLayout: block attribute")]
        public void Verified_import_compares_compiled_members_with_export_metadata(string change, string? difference)
        {
            string folder = Path.Combine(Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
                "tests/FoundationHost/TiaMcp.FoundationHost.Tests/Fixtures/V14Sp1");
            var document = XDocument.Load(Path.Combine(folder, "compiled-udt-db.xml"));
            var motor = document.Descendants().Single(e => (string?)e.Attribute("Name") == "Motor");
            XNamespace ns = motor.Name.Namespace;
            var count = motor.ElementsBeforeSelf(ns + "Member").Single();
            var expansion = motor.Element(ns + "Sections")!;
            var members = expansion.Element(ns + "Section")!.Elements(ns + "Member").ToArray();
            if (change == "exportIds")
            {
                members[0].SetAttributeValue("ID", "1");
                PlcDocumentEditing.Block(document).Add(new XElement("ObjectList",
                    new XElement("MultilingualText", new XAttribute("ID", "2"), new XAttribute("CompositionName", "Comment"))));
            }
            var declaration = XDocument.Load(Path.Combine(folder, "compiled-udt-declaration.xml"));
            var declared = declaration.Descendants(ns + "Member").ToArray();
            if (change == "oldWitness") declared.Elements(ns + "AttributeList").Remove();
            if (change is "nested" or "nestedType")
            {
                XElement Drive() => new XElement(ns + "Member", new XAttribute("Name", "Drive"), new XAttribute("Datatype", "Struct"),
                    new XElement(ns + "Member", new XAttribute("Name", "Config"), new XAttribute("Datatype", "\"ConfigType\""),
                        new XElement(ns + "Sections", new XElement(ns + "Section", new XAttribute("Name", "None")))));
                foreach (var speed in new[] { members[1], declared[1] })
                {
                    var drive = Drive();
                    speed.ReplaceWith(drive);
                    drive.Descendants(ns + "Section").Single().Add(speed);
                }
            }
            if (change == "struct")
            {
                var group = new XElement(ns + "Member", new XAttribute("Name", "Group"), new XAttribute("Datatype", "Struct"));
                motor.ReplaceWith(group);
                group.Add(motor);
            }
            var planned = new XDocument(document);
            var plannedMotor = planned.Descendants(ns + "Member").Single(e => (string?)e.Attribute("Name") == "Motor");
            plannedMotor.Element(ns + "Sections")!.Remove();
            planned.Descendants(ns + "Member").Attributes("Remanence").Remove();
            planned.Descendants(ns + "Member").Elements(ns + "AttributeList").Remove();
            using var fixture = new Fixture(planned.ToString()) { Current = document.ToString() };
            fixture.ExportType = (name, path) => { Assert.Equal("MCP_RC_UDT", name); declaration.Save(path); return true; };
            switch (change)
            {
                case "rename": members[0].SetAttributeValue("Name", "Ready"); break;
                case "reorder": members[0].Remove(); members[1].AddAfterSelf(members[0]); break;
                case "extra": members[0].AddBeforeSelf(new XElement(ns + "Member", new XAttribute("Name", "Extra"), new XAttribute("Datatype", "Int"))); break;
                case "missing": members[0].Remove(); break;
                case "type": case "nestedType": members[1].SetAttributeValue("Datatype", "DInt"); break;
                case "value": members[1].Add(new XElement(ns + "StartValue", "4")); break;
                case "topValue": count.Element(ns + "StartValue")!.Value = "4"; break;
                case "topType": count.SetAttributeValue("Datatype", "DInt"); break;
                case "retain": motor.SetAttributeValue("Remanence", "Retain"); break;
                case "block": document.Descendants("MemoryLayout").Single().Value = "Optimized"; break;
            }
            fixture.After = document.ToString();
            var preview = fixture.Run(true);
            var result = fixture.Run(false, (string)preview["token"]!);
            Assert.Equal(difference == null ? "DocumentReadbackMatched" : "DocumentReadbackMismatch", (string?)result["status"]);
            Assert.Equal(difference, (string?)result["readbackDifference"]);
            Assert.Equal(difference == null, (string?)result["plannedFingerprint"] == (string?)result["actualFingerprint"]);
            if (difference != null)
            {
                var reply = PlcToolContract.Map("ImportPlcBlockVerified", new { meta = result }, true, true).StructuredContent!;
                Assert.Equal("NATIVE_OPERATION_FAILED", (string?)reply["error"]!["code"]);
                Assert.Equal(difference, (string?)reply["data"]!["evidence"]!["readbackDifference"]);
            }
        }

        [Fact]
        public void Block_and_witness_share_the_verified_export_options()
        {
            string source = File.ReadAllText(Path.Combine(Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
                "src/Adapters/Native/Plc/PlcVerifiedImportBoundary.cs"));
            Assert.Contains("const ExportOptions verifiedOptions = ExportOptions.WithDefaults | ExportOptions.WithReadOnly;", source);
            Assert.Matches(@"\.Export\(block,\s*new FileInfo\(TiaOpenness.Shared.NativeInputPolicy.FullPath\(path\)\),\s*verifiedOptions\)", source);
            Assert.Contains("type.Export(file, verifiedOptions)", source);
        }

        [Theory]
        [InlineData("uncompiledBefore", true)]
        [InlineData("member", false)]
        [InlineData("type", false)]
        [InlineData("value", false)]
        [InlineData("retain", false)]
        [InlineData("declared", false)]
        [InlineData("missingDeclaration", false)]
        [InlineData("wrongDeclaration", false)]
        [InlineData("implicitDefaults", true)]
        [InlineData("declaredDefault", true)]
        [InlineData("changedDefault", false)]
        [InlineData("removedDefault", false)]
        [InlineData("childRetain", false)]
        [InlineData("extra", false)]
        [InlineData("missing", false)]
        [InlineData("order", false)]
        public void B169_compiled_DB_is_verified_against_the_project_UDT_declaration(string change, bool matches)
        {
            string folder = Path.Combine(Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
                "tests/FoundationHost/TiaMcp.FoundationHost.Tests/Fixtures/V14Sp1");
            var specs = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "065-067-declarations.json")))!;
            var db = specs["globaldb"]!;
            var declaration = ProjectUdt(folder);
            var document = PlcGlobalDbXmlBuilder.BuildDocument((string)db["dbName"]!, (int)db["dbNumber"]!, db["staticMembers"]!.AsArray()
                .Select(m => new PlcDbMemberDefinition((string)m!["name"]!, (string)m["datatype"]!, startValue: (string?)m["startValue"] ?? "")), "14sp1");
            var section = document.Descendants().Single(e => e.Name.LocalName == "Section");
            XNamespace ns = section.Name.Namespace;
            var motor = section.Elements().Last();
            var xml = document.ToString();
            var members = CompiledUdtMembers(folder, ns);
            Assert.Equal(2, members.Length);
            motor.Add(new XElement(ns + "Sections", new XElement(ns + "Section", new XAttribute("Name", "None"), members)));
            using var fixture = new Fixture(xml);
            if (change is "declaredDefault" or "changedDefault" or "removedDefault")
                declaration.Descendants(ns + "Member").Last().Add(new XElement(ns + "StartValue", "7"));
            fixture.ExportType = (name, path) => {
                Assert.Equal("MCP_RC_UDT", name);
                fixture.Calls.Add("export type");
                if (change == "missingDeclaration") return false;
                if (change == "wrongDeclaration") declaration.Descendants("Name").Single().Value = "OtherType";
                declaration.Save(path);
                return true;
            };
            foreach (var member in section.Elements()) member.SetAttributeValue("Remanence", "NonRetain");
            switch (change)
            {
                case "extra": members[1].AddAfterSelf(new XElement(ns + "Member", new XAttribute("Name", "Extra"), new XAttribute("Datatype", "Int"))); break;
                case "missing": members[0].Remove(); break;
                case "order": members[0].Remove(); members[1].AddAfterSelf(members[0]); break;
                case "childRetain": members[0].SetAttributeValue("Remanence", "Retain"); break;
                case "implicitDefaults": members[0].Add(new XElement(ns + "StartValue", "FALSE")); members[1].Add(new XElement(ns + "StartValue", "0")); break;
                case "declaredDefault": members[1].Add(new XElement(ns + "StartValue", "7")); break;
                case "changedDefault": members[1].Add(new XElement(ns + "StartValue", "8")); break;
                case "member": members[0].SetAttributeValue("Name", "Ready"); break;
                case "type": members[1].SetAttributeValue("Datatype", "DInt"); break;
                case "value": members[1].Add(new XElement(ns + "StartValue", "4")); break;
                case "retain": motor.SetAttributeValue("Remanence", "Retain"); break;
                case "declared": section.Elements().First().Element(ns + "StartValue")!.Value = "4"; break;
            }
            fixture.After = document.ToString();
            var preview = fixture.Run(true);
            var applied = fixture.Run(false, (string)preview["token"]!, new JsonObject { ["softwarePath"] = "MCP_RC_PLC",
                ["blockPath"] = "MCP_RC_Grp/MCP_RC_DB" });
            Assert.Equal(matches, (bool?)applied["verificationSuccess"]);
            Assert.Equal(matches ? "DocumentReadbackMatched" : "DocumentReadbackMismatch", (string?)applied["status"]);
            if (!matches) Assert.Equal("MCP_RC_PLC/MCP_RC_Grp/MCP_RC_DB", (string?)applied["cleanupRequired"]![0]!["target"]);
            Assert.Equal(new[] { "before.xml", "export type", "before.xml", "export type", "import", "compile", "after.xml" }, fixture.Calls);
            if (change == "uncompiledBefore")
            {
                Assert.DoesNotContain(XDocument.Load((string)applied["backup"]!["path"]!).Descendants(ns + "Section"),
                    s => (string?)s.Attribute("Name") == "None");
                Assert.DoesNotContain(XDocument.Load((string)applied["plannedPath"]!).Descendants(ns + "Section"),
                    s => (string?)s.Attribute("Name") == "None");
                Assert.Null(applied["cleanupRequired"]);
                Assert.Single(XDocument.Load((string)applied["afterPath"]!).Descendants(ns + "Section"), s => (string?)s.Attribute("Name") == "None");
            }
        }

        [Fact]
        public void Changed_UDT_default_between_preview_and_apply_refuses_before_import()
        {
            const string typeName = "MotorType";
            var candidate = PlcGlobalDbXmlBuilder.BuildDocument("Motors", 1, new[] { new PlcDbMemberDefinition("Motor", "\"MotorType\"") }, "14sp1");
            var declaration = PlcUdtXmlBuilder.BuildDocument(typeName, new[] { new PlcUdtMemberDefinition("Speed", "Int") }, "14sp1");
            using var fixture = new Fixture(candidate.ToString());
            fixture.ExportType = (name, path) => { Assert.Equal(typeName, name); declaration.Save(path); return true; };
            var preview = fixture.Run(true);
            var member = declaration.Descendants().Single(e => e.Name.LocalName == "Member");
            member.Add(new XElement(member.Name.Namespace + "StartValue", "11"));
            var error = Assert.Throws<AdapterPreconditionException>(() => fixture.Run(false, (string)preview["token"]!));
            Assert.Equal("preview-stale", error.Condition);
            Assert.Equal(0, fixture.Imports);
        }

        [Fact]
        public void Cleanup_target_without_software_path_is_a_relative_block_identity()
        {
            using var fixture = new Fixture(Xml(false));
            var preview = fixture.Run(true);
            fixture.After = fixture.Current.Replace("<Text>Original</Text>", "<Text>Changed</Text>");
            var result = fixture.Run(false, (string)preview["token"]!, new JsonObject { ["blockPath"] = "Group/Block" });
            Assert.Equal("Group/Block", (string?)result["cleanupRequired"]![0]!["target"]);
        }

        [Fact]
        public void Changed_network_text_still_mismatches()
        {
            using var fixture = new Fixture(Xml(false));
            fixture.After = fixture.Current.Replace("<Text>Original</Text>", "<Text>Changed</Text>");
            var preview = fixture.Run(true);
            var result = fixture.Run(false, (string)preview["token"]!);
            Assert.Equal("DocumentReadbackMismatch", (string?)result["status"]);
            Assert.False((bool?)result["verificationSuccess"]);
            Assert.NotEmpty(result["cleanupRequired"]!.AsArray());
            Assert.NotEqual((string?)result["plannedFingerprint"], (string?)result["actualFingerprint"]);
        }

        [Fact]
        public void Changed_writable_attribute_still_mismatches()
        {
            using var fixture = new Fixture(Xml(false));
            fixture.After = fixture.Current.Replace("<HeaderAuthor>Engineer</HeaderAuthor>", "<HeaderAuthor>Other engineer</HeaderAuthor>");
            var preview = fixture.Run(true);
            var result = fixture.Run(false, (string)preview["token"]!);
            Assert.Equal("DocumentReadbackMismatch", (string?)result["status"]);
            Assert.False((bool?)result["verificationSuccess"]);
            Assert.NotEmpty(result["cleanupRequired"]!.AsArray());
        }

        [Theory]
        [InlineData("CodeModifiedDate", "2026-02-02T00:00:00.0000000Z")]
        [InlineData("InterfaceModifiedDate", "2026-02-02T00:00:00.0000000Z")]
        [InlineData("ParameterModified", "2026-02-02T00:00:00.0000000Z")]
        [InlineData("StructureModified", "2026-02-02T00:00:00.0000000Z")]
        [InlineData("LoadMemoryLength", "4096")]
        [InlineData("WorkMemoryLength", "2048")]
        public void Preview_token_ignores_computed_attributes_and_accepts_the_reviewed_import(string attribute, string value)
        {
            using var fixture = new Fixture(Xml(false));
            var first = fixture.Run(true);
            var current = PlcDocumentEditing.Parse(fixture.Current);
            PlcDocumentEditing.Child(PlcDocumentEditing.Child(PlcDocumentEditing.Block(current), "AttributeList"), attribute).Value = value;
            fixture.Current = current.ToString();
            var second = fixture.Run(true);
            Assert.NotEqual((string?)first["backup"]!["sha256"], (string?)second["backup"]!["sha256"]);
            Assert.Equal((string?)first["token"], (string?)second["token"]);
            fixture.After = fixture.Current;
            var applied = fixture.Run(false, (string)first["token"]!);
            Assert.Equal("DocumentReadbackMatched", (string?)applied["status"]);
            Assert.Equal(1, fixture.Imports);
        }

        [Theory]
        [InlineData("<Text>Original</Text>", "<Text>Changed</Text>")]
        [InlineData("<HeaderAuthor>Engineer</HeaderAuthor>", "<HeaderAuthor>Other engineer</HeaderAuthor>")]
        [InlineData("<StartValue>1</StartValue>", "<StartValue>2</StartValue>")]
        public void Preview_token_still_refuses_changed_block_content(string before, string after)
        {
            using var fixture = new Fixture(Xml(false));
            var preview = fixture.Run(true);
            fixture.Current = fixture.Current.Replace(before, after);
            var refusal = Assert.Throws<AdapterPreconditionException>(() => fixture.Run(false, (string)preview["token"]!));
            Assert.Equal("preview-stale", refusal.Condition);
            Assert.Equal("previewToken", refusal.ParamName);
            Assert.Equal(0, fixture.Imports);
        }
    }
}
