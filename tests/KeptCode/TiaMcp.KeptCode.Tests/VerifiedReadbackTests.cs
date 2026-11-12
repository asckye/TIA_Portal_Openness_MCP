using System;
using System.IO;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class VerifiedReadbackTests
    {
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
            internal Fixture(string xml) { candidateXml = xml; Current = xml; After = xml; }
            private void Export(string path) => File.WriteAllText(path, Current);
            private void Import(string path) { Assert.True(File.Exists(path)); Imports++; Current = After; }
            internal JsonObject Run(bool preview, string token = "")
            {
                var meta = new JsonObject();
                PlcVerifiedImport.Execute(candidateXml, "PLC/FC_Fixture", directory, preview, token, Export, Import, () => { }, meta, () => { });
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
            Assert.Throws<InvalidOperationException>(() => fixture.Run(false, (string)preview["token"]!));
            Assert.Equal(0, fixture.Imports);
        }
    }
}
