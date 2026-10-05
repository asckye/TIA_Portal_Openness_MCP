using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// Offline documentation family: FlgNet -> graph/Mermaid, SCL rendering, handbook index and the SCL lint rules.
    /// Every group keeps a negative sentinel so a broken checker cannot pass silently.
    /// </summary>
    internal static class PlcDocumentationTests
    {
        // Cut from skill/lad-cookbook/MCPVerify_FB_LAD_v3.xml (network 1: Contact -> TON -> Coil) plus one SCL network.
        private const string LadFbXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <SW.Blocks.FB ID=""0"">
    <AttributeList>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Input""><Member Name=""Trig"" Datatype=""Bool"" /></Section>
          <Section Name=""Output""><Member Name=""OutTonQ"" Datatype=""Bool"" /></Section>
          <Section Name=""Static""><Member Name=""tonInst"" Datatype=""TON_TIME"" Version=""1.0"" /></Section>
        </Sections>
      </Interface>
      <Name>DocFb</Name>
      <Number>123</Number>
      <ProgrammingLanguage>LAD</ProgrammingLanguage>
    </AttributeList>
    <ObjectList>
      <MultilingualText ID=""1"" CompositionName=""Title""><ObjectList><MultilingualTextItem ID=""2"" CompositionName=""Items""><AttributeList><Culture>en-US</Culture><Text>Doc block</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
      <SW.Blocks.CompileUnit ID=""3"" CompositionName=""CompileUnits"">
        <AttributeList>
          <NetworkSource>
            <FlgNet xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"">
              <Parts>
                <Access Scope=""LocalVariable"" UId=""21""><Symbol><Component Name=""Trig"" /></Symbol></Access>
                <Access Scope=""TypedConstant"" UId=""22""><Constant><ConstantValue>T#500ms</ConstantValue></Constant></Access>
                <Access Scope=""LocalVariable"" UId=""23""><Symbol><Component Name=""OutTonQ"" /></Symbol></Access>
                <Part Name=""Contact"" UId=""24"" />
                <Part Name=""TON"" Version=""1.0"" UId=""25"">
                  <Instance Scope=""LocalVariable"" UId=""26""><Component Name=""tonInst"" /></Instance>
                  <TemplateValue Name=""time_type"" Type=""Type"">Time</TemplateValue>
                </Part>
                <Part Name=""Coil"" UId=""27"" />
              </Parts>
              <Wires>
                <Wire UId=""28""><Powerrail /><NameCon UId=""24"" Name=""in"" /></Wire>
                <Wire UId=""29""><IdentCon UId=""21"" /><NameCon UId=""24"" Name=""operand"" /></Wire>
                <Wire UId=""30""><NameCon UId=""24"" Name=""out"" /><NameCon UId=""25"" Name=""IN"" /></Wire>
                <Wire UId=""31""><IdentCon UId=""22"" /><NameCon UId=""25"" Name=""PT"" /></Wire>
                <Wire UId=""32""><NameCon UId=""25"" Name=""Q"" /><NameCon UId=""27"" Name=""in"" /></Wire>
                <Wire UId=""33""><IdentCon UId=""23"" /><NameCon UId=""27"" Name=""operand"" /></Wire>
                <Wire UId=""34""><NameCon UId=""25"" Name=""ET"" /><OpenCon UId=""35"" /></Wire>
              </Wires>
            </FlgNet>
          </NetworkSource>
          <ProgrammingLanguage>LAD</ProgrammingLanguage>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID=""6"" CompositionName=""Title""><ObjectList><MultilingualTextItem ID=""7"" CompositionName=""Items""><AttributeList><Culture>en-US</Culture><Text>Timer</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
        </ObjectList>
      </SW.Blocks.CompileUnit>
      <SW.Blocks.CompileUnit ID=""8"" CompositionName=""CompileUnits"">
        <AttributeList>
          <NetworkSource>
            <StructuredText xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4"">
              <Token Text=""IF"" /><Blank /><Access Scope=""LocalVariable""><Symbol><Component Name=""Trig"" /></Symbol></Access><Blank /><Token Text=""THEN"" /><NewLine />
              <Blank Num=""4"" /><Access Scope=""Call""><CallInfo Name=""FC_Helper"" BlockType=""FC"" /></Access><Token Text=""("" /><Token Text="")"" /><Token Text="";"" /><NewLine />
              <Token Text=""END_IF"" /><Token Text="";"" /><NewLine />
            </StructuredText>
          </NetworkSource>
          <ProgrammingLanguage>SCL</ProgrammingLanguage>
        </AttributeList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FB>
</Document>";

        private const string GoodScl = @"FUNCTION_BLOCK ""FB_Good""
VAR_INPUT
    Start : Bool;
END_VAR
VAR_TEMP
    i : Int;
END_VAR
BEGIN
    // a comment with = inside and a (* nested *) block
    IF #Start THEN
        FOR #i := 0 TO 3 DO
            #Out := 'a = b';
        END_FOR;
    ELSIF NOT #Start THEN
        #Out := '';
    END_IF;
    CASE #i OF
        1: #Out := 'x';
        2..3: #Out := 'y';
    END_CASE;
END_FUNCTION_BLOCK
";

        internal static void Run(Action<bool, string> check)
        {
            bool Fails(Action action) { try { action(); return false; } catch { return true; } }
            var dir = Path.Combine(Path.GetTempPath(), "tia-plc-doc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string Write(string name, string content) { var p = Path.Combine(dir, name); File.WriteAllText(p, content, new UTF8Encoding(false)); return p; }
            try
            {
                // ---- FlgNet graph
                var xml = XDocument.Parse(LadFbXml);
                var flg = xml.Descendants().First(e => e.Name.LocalName == "FlgNet");
                var g = PlcDocumentationLogic.ParseFlgNet(flg);
                check(g.Nodes.Count == 7 && g.Nodes[0].Kind == "rail", "graph has the rail + 3 accesses + 3 parts");
                check(g.Edges.Any(e => e.From == "RAIL" && e.To == "P24"), "power rail feeds the contact");
                check(g.Edges.Any(e => e.From == "P24" && e.To == "P25" && e.FromPin == "out" && e.ToPin == "IN"), "contact out -> TON IN with pin names");
                check(g.Edges.Any(e => e.From == "A21" && e.To == "P24"), "operand access flows INTO the contact");
                check(g.Edges.Any(e => e.From == "P27" && e.To == "A23"), "coil operand is an output edge (part -> access)");
                check(g.Edges.Any(e => e.From == "A22" && e.To == "P25" && e.ToPin == "PT"), "constant feeds TON.PT");
                check(g.OpenConnections == 1, "ET -> OpenCon is counted as an open connection");
                var ton = g.Nodes.First(n => n.Id == "P25");
                check(ton.Label == "TON<Time> [#tonInst]" && ton.Instance == "#tonInst", "TON label carries template and instance: " + ton.Label);
                check(PlcDocumentationLogic.AccessLabel(flg.Descendants().First(e => e.Name.LocalName == "Access" && (string?)e.Attribute("UId") == "22")) == "T#500ms", "typed constant label");
                var listing = PlcDocumentationLogic.Listing(g);
                check(listing == "Contact(#Trig) -> TON<Time> [#tonInst](PT=T#500ms) -> Coil{#OutTonQ}", "listing follows the wires: " + listing);
                var mermaid = PlcDocumentationLogic.Mermaid(g);
                check(mermaid.StartsWith("flowchart LR", StringComparison.Ordinal) && mermaid.Contains("P24 --> P25") == false && mermaid.Contains("P24 -- \"out→IN\" --> P25"), "mermaid edges carry pin labels");
                check(mermaid.Contains("#quot;") == false && mermaid.Contains("A22([\"T#500ms\"])"), "mermaid access node uses stadium shape");
                check(PlcDocumentationLogic.IsOutputPin("Q") && PlcDocumentationLogic.IsOutputPin("ENO") && !PlcDocumentationLogic.IsOutputPin("IN") && !PlcDocumentationLogic.IsOutputPin("operand", "Contact") && PlcDocumentationLogic.IsOutputPin("operand", "SCoil"), "output pin classification");

                // ---- render block (LAD + SCL networks)
                var path = Write("DocFb.xml", LadFbXml);
                var rendered = PlcDocumentationLogic.RenderFile(path);
                check(rendered.GraphicalNetworks == 1 && rendered.TextNetworks == 1, "one graphical and one text network");
                var md = rendered.Markdown;
                check(md.StartsWith("# FB 123 `DocFb` (LAD)", StringComparison.Ordinal), "markdown header: " + md.Split('\n')[0]);
                check(md.Contains("| Static | `tonInst` | TON_TIME |"), "interface table row");
                check(md.Contains("### Network 1: Timer _(LAD)_") && md.Contains("```mermaid"), "LAD network section with mermaid block");
                check(md.Contains("```scl\nIF #Trig THEN\n    \"FC_Helper\"();\nEND_IF;"), "SCL network rendered from tokens");
                check(md.Contains("- `Block:FC_Helper`"), "calls section lists the FC call");
                check(md.Contains("_1 open connection(s)"), "open connections noted");
                check(Fails(() => PlcDocumentationLogic.RenderFile(path, "XY")), "[sentinel] invalid mermaid direction refused");
                check(Fails(() => PlcDocumentationLogic.RenderFile(Path.Combine(dir, "missing.xml"))), "[sentinel] missing file refused");

                // ---- handbook
                Write("FC_Helper.scl", "FUNCTION \"FC_Helper\" : Void\nBEGIN\n  ;\nEND_FUNCTION\n");
                Write("broken.xml", "<Document><SW.Blocks.FC><AttributeList><Name>Broken</Name></AttributeList><ObjectList><SW.Blocks.CompileUnit><AttributeList><NetworkSource><FlgNet>");
                var hb = PlcDocumentationLogic.RenderHandbook(dir, false, OfflineAnalysisLogic.DefaultExtensions, "Test handbook");
                check(hb.Entries.Count == 3 && hb.Failed == 1, "handbook parses 2 of 3 documents (broken XML is reported, not fatal)");
                check(hb.Markdown.StartsWith("# Test handbook", StringComparison.Ordinal), "handbook title");
                check(hb.Markdown.Contains("| [`DocFb`](#fb-123-docfb-lad) | FB | 123 | LAD | 2 | 3 | 1 |"), "index row for DocFb: " + string.Join(" ", hb.Markdown.Split((char)10).Where(l => l.Contains("DocFb`](#"))));
                check(hb.Markdown.Contains("| `FC_Helper` | yes | `DocFb` |"), "call cross-reference resolves the callee inside the export");
                check(hb.Markdown.Contains("## FB 123 `DocFb` (LAD)"), "block headings demoted one level in the handbook");
                check(hb.Markdown.Contains("- Failed: `") && hb.Markdown.Contains("broken.xml"), "failed document listed");

                // ---- lint: clean source
                var clean = PlcDocumentationLogic.LintScl(GoodScl);
                check(clean.Count(f => f.Severity == "error") == 0, "[sentinel] well-formed SCL has no errors: " + string.Join(" | ", clean.Select(f => f.Rule + "@" + f.Line + " " + f.Message)));
                check(clean.All(f => f.Rule != "SCL003"), "string literal 'a = b' and comment '=' do not trigger SCL003");
                check(clean.All(f => f.Rule != "SCL004"), "CASE labels and THEN/DO headers are not reported as missing ';'");

                // ---- lint: defects
                var bad = PlcDocumentationLogic.LintScl("IF #a THEN\n  #x = 1;\n  #y := (1 + 2;\nEND_WHILE;\nFOR #i := 0 TO 2 DO\n  #z := 3\nEND_FOR;\nGOTO lbl;\nWHILE TRUE DO\nEND_WHILE;\n(* open");
                string Rules() => string.Join(",", bad.Select(f => f.Rule + "@" + f.Line));
                check(bad.Any(f => f.Rule == "SCL003" && f.Line == 2), "'=' assignment flagged: " + Rules());
                check(bad.Any(f => f.Rule == "SCL002" && f.Line == 3), "unclosed '(' flagged at its line");
                check(bad.Any(f => f.Rule == "SCL001" && f.Line == 4 && f.Message.Contains("END_WHILE closes IF")), "mismatched closer names the opener");
                check(bad.Any(f => f.Rule == "SCL004" && f.Line == 6), "missing ';' before END_FOR flagged on the statement line");
                check(bad.Any(f => f.Rule == "SCL005" && f.Line == 8), "GOTO flagged");
                check(bad.Any(f => f.Rule == "SCL011" && f.Line == 9), "WHILE TRUE flagged");
                check(bad.Any(f => f.Rule == "SCL013"), "unterminated block comment flagged");
                check(bad.SequenceEqual(bad.OrderBy(f => f.Line).ThenBy(f => f.Rule, StringComparer.Ordinal)), "findings sorted by line");

                var unclosed = PlcDocumentationLogic.LintScl("IF #a THEN\n  #b := 1;\n");
                check(unclosed.Any(f => f.Rule == "SCL001" && f.Line == 1 && f.Message.Contains("never closed")), "unclosed IF reported at the opener line");
                var deep = PlcDocumentationLogic.LintScl("IF a THEN IF b THEN IF c THEN ; END_IF; END_IF; END_IF;", PlcDocumentationLogic.ParseLintOptions("{\"maxNesting\":2}"));
                check(deep.Count(f => f.Rule == "SCL006") == 1, "nesting above maxNesting reported once");
                var longLine = PlcDocumentationLogic.LintScl("#a := 1; " + new string(' ', 130) + "\n", PlcDocumentationLogic.ParseLintOptions("{\"disable\":[\"SCL008\"]}"));
                check(longLine.Any(f => f.Rule == "SCL007") && longLine.All(f => f.Rule != "SCL008"), "line length reported, disabled rule suppressed");
                check(PlcDocumentationLogic.LintScl("// TODO fix me\n#a := 1;\n").Any(f => f.Rule == "SCL010"), "TODO marker in comment reported");
                check(Fails(() => PlcDocumentationLogic.ParseLintOptions("[1]")), "[sentinel] rulesJson must be an object");
                check(Fails(() => PlcDocumentationLogic.ParseLintOptions("{\"maxNesting\":0}")), "[sentinel] maxNesting range enforced");
                check(PlcDocumentationLogic.StripStrings("a := 'x;(y'; \"Tag(1)\"") == "a := 'xxxx'; \"xxxxxx\"", "string/quoted contents neutralized: " + PlcDocumentationLogic.StripStrings("a := 'x;(y'; \"Tag(1)\""));
                check(PlcDocumentationLogic.RuleCatalog().Count == 13, "rule catalog lists 13 rules");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
