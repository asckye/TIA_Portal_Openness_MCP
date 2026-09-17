using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// Offline analysis (compare / annotations / metrics) over inline fixtures cut from the LAD/SCL cookbook exports.
    /// Every group has a negative sentinel: a valid input must keep passing, otherwise the check itself is broken.
    /// </summary>
    internal static class OfflineAnalysisTests
    {
        // Cut from skill/lad-cookbook/MCPVerify_FC_LAD.xml (networks 1 + 6), with a FC call network added.
        private const string LadFcXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo>
    <Created>{CREATED}</Created>
    <ExportSetting>None</ExportSetting>
    <InstalledProducts>
      <Product>
        <DisplayName>Totally Integrated Automation Portal</DisplayName>
        <DisplayVersion>V21</DisplayVersion>
      </Product>
    </InstalledProducts>
  </DocumentInfo>
  <SW.Blocks.FC ID=""{ID0}"">
    <AttributeList>
      <AutoNumber>false</AutoNumber>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Input"">
            <Member Name=""A"" Datatype=""Bool"" />
            <Member Name=""B"" Datatype=""Bool"" />{EXTRA_INPUT}
          </Section>
          <Section Name=""Output"">
            <Member Name=""OUT_AND"" Datatype=""Bool"" />
            <Member Name=""DST"" Datatype=""{DST_TYPE}"" />
          </Section>
          <Section Name=""InOut"" />
          <Section Name=""Temp"" />
          <Section Name=""Constant"" />
          <Section Name=""Return"">
            <Member Name=""Ret_Val"" Datatype=""Void"" />
          </Section>
        </Sections>
      </Interface>
      <MemoryLayout>Optimized</MemoryLayout>
      <Name>MCPVerify_FC_LAD</Name>
      <Namespace />
      <Number>901</Number>
      <ProgrammingLanguage>LAD</ProgrammingLanguage>
      <SetENOAutomatically>false</SetENOAutomatically>
    </AttributeList>
    <ObjectList>
      <MultilingualText ID=""{ID1}"" CompositionName=""Comment"">
        <ObjectList>
          <MultilingualTextItem ID=""{ID2}"" CompositionName=""Items"">
            <AttributeList>
              <Culture>zh-CN</Culture>
              <Text>MCP 验证：LAD 原生指令</Text>
            </AttributeList>
          </MultilingualTextItem>
        </ObjectList>
      </MultilingualText>
      <SW.Blocks.CompileUnit ID=""{ID3}"" CompositionName=""CompileUnits"">
        <AttributeList>
          <NetworkSource>
            <FlgNet xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"">
              <Parts>
                <Access Scope=""LocalVariable"" UId=""{U21}"">
                  <Symbol>
                    <Component Name=""A"" />
                  </Symbol>
                </Access>
                <Access Scope=""LocalVariable"" UId=""{U22}"">
                  <Symbol>
                    <Component Name=""B"" />
                  </Symbol>
                </Access>
                <Access Scope=""LocalVariable"" UId=""{U23}"">
                  <Symbol>
                    <Component Name=""OUT_AND"" />
                  </Symbol>
                </Access>
                <Part Name=""Contact"" UId=""{U24}"" />
                <Part Name=""Contact"" UId=""{U25}"" />
                <Part Name=""Coil"" UId=""{U26}"" />
              </Parts>
              <Wires>
                <Wire UId=""{U27}"">
                  <Powerrail />
                  <NameCon UId=""{U24}"" Name=""in"" />
                </Wire>
                <Wire UId=""{U28}"">
                  <IdentCon UId=""{U21}"" />
                  <NameCon UId=""{U24}"" Name=""operand"" />
                </Wire>
                <Wire UId=""{U29}"">
                  <NameCon UId=""{U24}"" Name=""out"" />
                  <NameCon UId=""{U25}"" Name=""in"" />
                </Wire>
                <Wire UId=""{U30}"">
                  <IdentCon UId=""{U22}"" />
                  <NameCon UId=""{U25}"" Name=""operand"" />
                </Wire>
                <Wire UId=""{U31}"">
                  <NameCon UId=""{U25}"" Name=""out"" />
                  <NameCon UId=""{U26}"" Name=""in"" />
                </Wire>
                <Wire UId=""{U32}"">
                  <IdentCon UId=""{U23}"" />
                  <NameCon UId=""{U26}"" Name=""operand"" />
                </Wire>
              </Wires>
            </FlgNet>
          </NetworkSource>
          <ProgrammingLanguage>LAD</ProgrammingLanguage>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID=""{ID4}"" CompositionName=""Comment"">
            <ObjectList>
              <MultilingualTextItem ID=""{ID5}"" CompositionName=""Items"">
                <AttributeList>
                  <Culture>zh-CN</Culture>
                  <Text>串联 AND：A &amp; B -> OUT_AND TODO verify with safety</Text>
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
          <MultilingualText ID=""{ID6}"" CompositionName=""Title"">
            <ObjectList>
              <MultilingualTextItem ID=""{ID7}"" CompositionName=""Items"">
                <AttributeList>
                  <Culture>zh-CN</Culture>
                  <Text>{N1_TITLE}</Text>
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
        </ObjectList>
      </SW.Blocks.CompileUnit>
      <SW.Blocks.CompileUnit ID=""{ID8}"" CompositionName=""CompileUnits"">
        <AttributeList>
          <NetworkSource>
            <FlgNet xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"">
              <Parts>
                <Access Scope=""LiteralConstant"" UId=""{U21}"">
                  <Constant>
                    <ConstantType>Int</ConstantType>
                    <ConstantValue>42</ConstantValue>
                  </Constant>
                </Access>
                <Access Scope=""LocalVariable"" UId=""{U22}"">
                  <Symbol>
                    <Component Name=""DST"" />
                  </Symbol>
                </Access>
                <Part Name=""Move"" UId=""{U23}"" DisabledENO=""true"">
                  <TemplateValue Name=""Card"" Type=""Cardinality"">1</TemplateValue>
                </Part>
                <Call UId=""{U24}"">
                  <CallInfo Name=""FC_Scale"" BlockType=""FC"" />
                </Call>
              </Parts>
              <Wires>
                <Wire UId=""{U25}"">
                  <Powerrail />
                  <NameCon UId=""{U23}"" Name=""en"" />
                </Wire>
                <Wire UId=""{U26}"">
                  <IdentCon UId=""{U21}"" />
                  <NameCon UId=""{U23}"" Name=""in"" />
                </Wire>
                <Wire UId=""{U27}"">
                  <NameCon UId=""{U23}"" Name=""out1"" />
                  <IdentCon UId=""{U22}"" />
                </Wire>
              </Wires>
            </FlgNet>
          </NetworkSource>
          <ProgrammingLanguage>LAD</ProgrammingLanguage>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID=""{ID9}"" CompositionName=""Title"">
            <ObjectList>
              <MultilingualTextItem ID=""{ID10}"" CompositionName=""Items"">
                <AttributeList>
                  <Culture>zh-CN</Culture>
                  <Text>N6-Move</Text>
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
        </ObjectList>
      </SW.Blocks.CompileUnit>{EXTRA_NETWORK}
      <MultilingualText ID=""{ID11}"" CompositionName=""Title"">
        <ObjectList>
          <MultilingualTextItem ID=""{ID12}"" CompositionName=""Items"">
            <AttributeList>
              <Culture>zh-CN</Culture>
              <Text>MCP LAD 原生指令验证</Text>
            </AttributeList>
          </MultilingualTextItem>
        </ObjectList>
      </MultilingualText>
    </ObjectList>
  </SW.Blocks.FC>
</Document>";

        private const string ExtraNetwork = @"
      <SW.Blocks.CompileUnit ID=""99"" CompositionName=""CompileUnits"">
        <AttributeList>
          <NetworkSource>
            <FlgNet xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"">
              <Parts><Part Name=""Coil"" UId=""21"" /></Parts>
              <Wires />
            </FlgNet>
          </NetworkSource>
          <ProgrammingLanguage>LAD</ProgrammingLanguage>
        </AttributeList>
        <ObjectList />
      </SW.Blocks.CompileUnit>";

        // StructuredText/v4 token stream in the shape of templates/mcp-full-e2e-verify/.../MCPVerify_FC_SCL_E2E.xml.
        private const string SclFbXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <DocumentInfo><Created>2026-05-11T00:00:00.0000000Z</Created></DocumentInfo>
  <SW.Blocks.FB ID=""0"">
    <AttributeList>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Input""><Member Name=""Enable"" Datatype=""Bool"" /><Member Name=""InInt"" Datatype=""Int"" /></Section>
          <Section Name=""Output""><Member Name=""OutInt"" Datatype=""Int"" /></Section>
          <Section Name=""Static""><Member Name=""cfg"" Datatype=""Struct""><Member Name=""limit"" Datatype=""Int"" /></Member></Section>
        </Sections>
      </Interface>
      <MemoryLayout>Optimized</MemoryLayout>
      <Name>FB_SclDemo</Name>
      <Number>10</Number>
      <ProgrammingLanguage>SCL</ProgrammingLanguage>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit ID=""3"" CompositionName=""CompileUnits"">
        <AttributeList><NetworkSource><StructuredText xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4"">
<LineComment UId=""1""><Text>FIXME clamp negative input</Text></LineComment>
<NewLine UId=""2"" />
<Token Text=""IF"" UId=""3"" />
<Blank UId=""4"" />
<Access Scope=""LocalVariable"" UId=""5""><Symbol UId=""6""><Component Name=""Enable"" UId=""7"" /></Symbol></Access>
<Blank UId=""8"" />
<Token Text=""THEN"" UId=""9"" />
<NewLine UId=""10"" />
<Blank Num=""4"" UId=""11"" />
<Token Text=""IF"" UId=""12"" />
<Blank UId=""13"" />
<Access Scope=""LocalVariable"" UId=""14""><Symbol UId=""15""><Component Name=""InInt"" UId=""16"" /></Symbol></Access>
<Blank UId=""17"" />
<Token Text=""&gt;"" UId=""18"" />
<Blank UId=""19"" />
<Access Scope=""LiteralConstant"" UId=""20""><Constant UId=""21""><ConstantValue UId=""22"">0</ConstantValue></Constant></Access>
<Blank UId=""23"" />
<Token Text=""THEN"" UId=""24"" />
<NewLine UId=""25"" />
<Blank Num=""8"" UId=""26"" />
<Access Scope=""LocalVariable"" UId=""27""><Symbol UId=""28""><Component Name=""OutInt"" UId=""29"" /></Symbol></Access>
<Blank UId=""30"" />
<Token Text="":="" UId=""31"" />
<Blank UId=""32"" />
<Access Scope=""Call"" UId=""33""><CallInfo Name=""LIMIT"" BlockType=""FC""><Token Text=""("" UId=""34"" /><Parameter Name=""MN"" UId=""35""><Token Text="":="" UId=""36"" /><Access Scope=""LiteralConstant"" UId=""37""><Constant UId=""38""><ConstantValue UId=""39"">0</ConstantValue></Constant></Access></Parameter><Token Text="")"" UId=""40"" /></CallInfo></Access>
<Token Text="";"" UId=""41"" />
<NewLine UId=""42"" />
<Blank Num=""4"" UId=""43"" />
<Token Text=""END_IF"" UId=""44"" />
<Token Text="";"" UId=""45"" />
<NewLine UId=""46"" />
<Token Text=""END_IF"" UId=""47"" />
<Token Text="";"" UId=""48"" />
<NewLine UId=""49"" />
</StructuredText></NetworkSource><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList>
        <ObjectList />
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FB>
</Document>";

        // Cut from skill/lad-cookbook/MCPVerify_FB_LAD_v3.s7dcl / .s7res (networks 1 + 2).
        private const string LadFbS7dcl = "﻿{\n    S7_BlockComment := \"MLC_3pC\";\n    S7_BlockNumber := \"59989\";\n    S7_BlockTitle := \"MLC_jr\";\n    S7_Optimized := \"TRUE\";\n    S7_PreferredLanguage := \"LAD\";\n    S7_Version := \"0.1\"\n}\nFUNCTION_BLOCK \"MCPVerify_FB_LAD_v3\"\n    VAR_INPUT\n        Trig : Bool;\n        PulseBit : Bool;\n    END_VAR\n    VAR_OUTPUT\n        OutTonQ : Bool;\n        OutPulse : Bool;\n    END_VAR\n    VAR\n        tonInst : TON_TIME;\n    END_VAR\n    VAR_TEMP\n        PulseWork : Bool;\n    END_VAR\n\n    {\n        S7_Language := \"LAD\";\n        S7_NetworkComment := \"MLC_4zs\";\n        S7_NetworkTitle := \"MLC_463\"\n    }\n    NETWORK\n        RUNG wire#powerrail\n            Contact( #Trig )\n            { S7_Templates := \"time_type := Time\" }\n            #tonInst.TON(\n                pt := T#500ms,\n                et =>  \n            )\n            Coil( #OutTonQ )\n        END_RUNG\n    END_NETWORK\n    {\n        S7_Language := \"LAD\";\n        S7_NetworkComment := \"MLC_4iu\";\n        S7_NetworkTitle := \"MLC_3qd\"\n    }\n    NETWORK\n        RUNG wire#powerrail\n            Contact( #PulseBit )\n            P_Trig( #PulseWork )\n            Coil( #OutPulse )\n        END_RUNG\n    END_NETWORK\nEND_FUNCTION_BLOCK\n";

        private const string LadFbS7res = "﻿MultiLingualTexts:\n  - id: MLC_jr\n    zh-CN: MCPVerify_FB_LAD_v3\n  - id: MLC_3pC\n    zh-CN: TON 实例在 FB.Static\n  - id: MLC_463\n    zh-CN: N1\n  - id: MLC_4zs\n    zh-CN: N1 TON Static\n  - id: MLC_3qd\n    zh-CN: N2 TODO rename\n  - id: MLC_4iu\n    zh-CN: N2 PBox\n";

        // Cut from skill/scl-cookbook/MCPVerify_FC_SCL_v3.scl with nesting and comments added.
        private const string SclFcText = "﻿FUNCTION \"MCPVerify_FC_SCL_v3\" : Void\n{ S7_Optimized_Access := 'TRUE' }\nVERSION : 0.1\n   VAR_INPUT \n      Seed : DInt;\n      Sel : Int;\n   END_VAR\n\n   VAR_OUTPUT \n      OutVal : DInt;\n   END_VAR\n\n   VAR_TEMP \n      acc : DInt;\n      i : Int;\n   END_VAR\n\n\nBEGIN\n\t// TODO seed validation\n\tacc := Seed;\n\tFOR i := 0 TO 3 DO\n\t    IF Sel = 0 THEN\n\t        acc := acc + i; (* HACK\n\t        spans lines *)\n\t    END_IF;\n\tEND_FOR;\n\tOutVal := \"FC_Scale\"(in := acc);\n\tOutVal := LIMIT(MN := 0, IN := OutVal, MX := 100);\n\tacc := 'NOTE inside string is code';\nEND_FUNCTION\n";

        private static string Lad(string created = "2026-05-11T00:00:00.0000000Z", int idBase = 0, int uidBase = 21, string extraInput = "", string dstType = "Int", string n1Title = "N1-串联", string extraNetwork = "")
        {
            var s = LadFcXml.Replace("{CREATED}", created).Replace("{EXTRA_INPUT}", extraInput).Replace("{DST_TYPE}", dstType).Replace("{N1_TITLE}", n1Title).Replace("{EXTRA_NETWORK}", extraNetwork);
            for (var i = 0; i <= 12; i++) s = s.Replace("{ID" + i + "}", (idBase + i).ToString("X"));
            for (var u = 21; u <= 32; u++) s = s.Replace("{U" + u + "}", (uidBase + u - 21).ToString());
            return s;
        }

        internal static void Run(Action<bool, string> check)
        {
            bool Fails(Action action) { try { action(); return false; } catch { return true; } }
            var dir = Path.Combine(Path.GetTempPath(), "tia-offline-analysis-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string Write(string name, string content) { var p = Path.Combine(dir, name); File.WriteAllText(p, content, new UTF8Encoding(false)); return p; }
            try
            {
                // ---- compare: identical / volatile-only
                var baseline = Write("a.xml", Lad());
                var same = Write("b.xml", Lad());
                var cmp = OfflineAnalysisLogic.CompareFiles(baseline, same, 0, 100);
                check(cmp["identicalAfterNormalization"]!.GetValue<bool>(), "identical files are identical after normalization");
                check(cmp["hunkCount"]!.GetValue<int>() == 0 && cmp["structurallyIdentical"]!.GetValue<bool>(), "identical files produce no hunks and no structural differences");
                check(cmp["normalizationRules"] is JsonArray rules && rules.Count == OfflineAnalysisLogic.NormalizationRules.Length, "normalization rules are reported");

                var volatileOnly = Write("c.xml", Lad(created: "2026-06-30T12:34:56.7890000Z", idBase: 0x40, uidBase: 100));
                cmp = OfflineAnalysisLogic.CompareFiles(baseline, volatileOnly, 0, 100);
                check(cmp["identicalAfterNormalization"]!.GetValue<bool>(), "timestamp-only + renumbered ID/UId change is identical after normalization");
                check(File.ReadAllText(baseline) != File.ReadAllText(volatileOnly), "[sentinel] the volatile-only fixture really differs byte-wise");

                // ---- compare: member added → structural + hunk
                var memberAdded = Write("d.xml", Lad(extraInput: "\n            <Member Name=\"C\" Datatype=\"Bool\" />"));
                cmp = OfflineAnalysisLogic.CompareFiles(baseline, memberAdded, 0, 100);
                check(!cmp["identicalAfterNormalization"]!.GetValue<bool>(), "added interface member is not identical");
                var added = cmp["structural"]!["interface"]!["added"] as JsonArray;
                check(added != null && added.Count == 1 && added[0]!["section"]!.GetValue<string>() == "Input" && added[0]!["path"]!.GetValue<string>() == "C" && added[0]!["datatype"]!.GetValue<string>() == "Bool", "structural diff names the added Input member C : Bool");
                check(((JsonArray)cmp["structural"]!["interface"]!["removed"]!).Count == 0, "no removed member reported for a pure addition");
                check(cmp["structural"]!["differenceCount"]!.GetValue<int>() == 1, "exactly one structural difference for one added member");
                check(cmp["hunkCount"]!.GetValue<int>() == 1, "one hunk for one added member line");
                var hunkLines = (cmp["hunks"] as JsonArray)?[0]?["lines"] as JsonArray;
                check(hunkLines != null && hunkLines.Any(l => l!.GetValue<string>() == "+[Interface] Input C : Bool"), "hunk shows the added canonical interface line with '+'");
                check(cmp["lineStats"]!["added"]!.GetValue<int>() == 1 && cmp["lineStats"]!["removed"]!.GetValue<int>() == 0, "line stats: 1 added, 0 removed");

                // ---- compare: type change, title change, network count
                var typeChanged = Write("e.xml", Lad(dstType: "DInt"));
                cmp = OfflineAnalysisLogic.CompareFiles(baseline, typeChanged, 0, 100);
                var tc = cmp["structural"]!["interface"]!["typeChanged"] as JsonArray;
                check(tc != null && tc.Count == 1 && tc[0]!["path"]!.GetValue<string>() == "DST" && tc[0]!["left"]!.GetValue<string>() == "Int" && tc[0]!["right"]!.GetValue<string>() == "DInt", "structural diff reports DST Int -> DInt");

                var titleChanged = Write("f.xml", Lad(n1Title: "N1-AND", extraNetwork: ExtraNetwork));
                cmp = OfflineAnalysisLogic.CompareFiles(baseline, titleChanged, 0, 100);
                var titles = cmp["structural"]!["networks"]!["titlesChanged"] as JsonArray;
                check(titles != null && titles.Count == 1 && titles[0]!["index"]!.GetValue<int>() == 1 && titles[0]!["right"]!.GetValue<string>() == "N1-AND", "network 1 title change reported");
                check(cmp["structural"]!["networks"]!["count"]!["left"]!.GetValue<int>() == 2 && cmp["structural"]!["networks"]!["count"]!["right"]!.GetValue<int>() == 3, "network count 2 -> 3 reported");
                check(((JsonArray)cmp["structural"]!["networks"]!["added"]!).Select(n => n!.GetValue<int>()).SequenceEqual(new[] { 3 }), "added network index 3 reported");

                // ---- compare: pagination and argument validation
                cmp = OfflineAnalysisLogic.CompareFiles(baseline, titleChanged, 0, 1);
                check(cmp["hunkCount"]!.GetValue<int>() >= 2 && ((JsonArray)cmp["hunks"]!).Count == 1 && !cmp["dataComplete"]!.GetValue<bool>(), "limit=1 pages hunks and reports dataComplete=false");
                cmp = OfflineAnalysisLogic.CompareFiles(baseline, titleChanged, 0, 100);
                check(cmp["dataComplete"]!.GetValue<bool>(), "[sentinel] full page reports dataComplete=true");
                check(Fails(() => OfflineAnalysisLogic.ValidatePage(0, 0)) && Fails(() => OfflineAnalysisLogic.ValidatePage(-1, 10)) && Fails(() => OfflineAnalysisLogic.ValidatePage(0, 501)), "page validation refuses limit 0 / 501 and negative offset");
                check(!Fails(() => OfflineAnalysisLogic.ValidatePage(0, 500)), "[sentinel] limit 500 accepted");
                check(Fails(() => OfflineAnalysisLogic.CompareFiles(baseline, Path.Combine(dir, "missing.xml"), 0, 10)), "missing file refused");
                check(Fails(() => OfflineAnalysisLogic.ParseFile("relative.xml")), "relative path refused");

                // ---- s7dcl + s7res parsing, and cross-format structural compare
                var s7dcl = Write("MCPVerify_FB_LAD_v3.s7dcl", LadFbS7dcl);
                Write("MCPVerify_FB_LAD_v3.s7res", LadFbS7res);
                var fb = OfflineAnalysisLogic.ParseFile(s7dcl);
                check(fb.Format == "S7DCL" && fb.BlockName == "MCPVerify_FB_LAD_v3" && fb.BlockType == "FB" && fb.Language == "LAD", "s7dcl header parsed (name/type/language)");
                check(fb.Members.Count == 6 && fb.Members.Any(m => m.Section == "Static" && m.Path == "tonInst" && m.Datatype == "TON_TIME") && fb.Members.Any(m => m.Section == "Temp" && m.Path == "PulseWork"), "s7dcl interface sections mapped (Input/Output/Static/Temp)");
                check(fb.Networks.Count == 2 && fb.Networks[0].Title == "N1" && fb.Networks[0].Comment == "N1 TON Static" && fb.Networks[1].Title == "N2 TODO rename", "s7dcl network titles/comments resolved from .s7res");
                check(fb.Title == "MCPVerify_FB_LAD_v3" && fb.Attributes["S7_BlockNumber"] == "59989" && fb.Attributes["S7_Version"] == "0.1", "s7dcl block title/attributes parsed");
                check(!fb.Canonical.Any(l => l.Contains("MLC_")), "canonical s7dcl form has no raw MLC ids");
                check(fb.BlockCalls.Contains("Instance:tonInst.TON") && fb.Instructions.Contains("Contact") && fb.Instructions.Contains("P_Trig"), "s7dcl instance call and instructions extracted");

                var s7dclB = Write("copy.s7dcl", LadFbS7dcl.Replace("MLC_3qd", "MLC_zz9"));
                Write("copy.s7res", LadFbS7res.Replace("MLC_3qd", "MLC_zz9"));
                cmp = OfflineAnalysisLogic.CompareFiles(s7dcl, s7dclB, 0, 100);
                check(cmp["identicalAfterNormalization"]!.GetValue<bool>(), "re-generated MLC ids with same texts are identical after normalization");

                var fbXml = Write("fb.xml", SclFbXml);
                cmp = OfflineAnalysisLogic.CompareFiles(fbXml, s7dcl, 0, 100);
                check(cmp["formatMismatch"]!.GetValue<bool>() && cmp["warning"] != null, "cross-format compare flags formatMismatch with a warning");
                check(cmp["structural"]!["blockName"]!["equal"]!.GetValue<bool>() == false, "cross-format structural compare still compares block names");

                // ---- .scl parsing
                var scl = Write("MCPVerify_FC_SCL_v3.scl", SclFcText);
                var fc = OfflineAnalysisLogic.ParseFile(scl);
                check(fc.Format == "SCL" && fc.BlockType == "FC" && fc.BlockName == "MCPVerify_FC_SCL_v3" && fc.Language == "SCL", "scl header parsed");
                check(fc.Members.Any(m => m.Section == "Return" && m.Path == "Ret_Val" && m.Datatype == "Void") && fc.Members.Count == 6, "scl FUNCTION return type becomes Return/Ret_Val like SimaticML");
                check(fc.Attributes["S7_Optimized"] == "TRUE" && fc.Attributes["S7_Version"] == "0.1", "scl S7_Optimized_Access/VERSION mapped to s7dcl attribute keys");
                check(fc.Networks.Count == 1 && fc.Networks[0].Language == "SCL", "scl BEGIN body is one implicit SCL network");
                check(fc.MaxNesting == 2, "scl max nesting FOR>IF = 2, got " + fc.MaxNesting);
                check(fc.BlockCalls.Contains("Block:FC_Scale") && fc.Instructions.Contains("LIMIT") && !fc.Instructions.Contains("IF") && !fc.Instructions.Contains("FOR"), "scl quoted call, LIMIT instruction, no keywords as instructions");
                check(fc.SourceLines == 11 && fc.CommentLines == 3, "scl source/comment line counts (11/3), got " + fc.SourceLines + "/" + fc.CommentLines);

                // ---- annotations
                var rows = OfflineAnalysisLogic.ScanText(baseline, File.ReadAllText(baseline), OfflineAnalysisLogic.DefaultMarkers);
                var todoRow = rows.FirstOrDefault(r => r.Marker == "TODO");
                var expectedLine = File.ReadAllLines(baseline).Select((l, i) => (l, i)).First(t => t.l.Contains("TODO verify")).i + 1;
                check(rows.Count == 1 && todoRow != null && todoRow.Line == expectedLine && todoRow.Network == 1 && todoRow.Kind == "networkComment" && todoRow.BlockName == "MCPVerify_FC_LAD", "xml network comment TODO found with correct line/network/kind");
                rows = OfflineAnalysisLogic.ScanText(fbXml, SclFbXml, OfflineAnalysisLogic.DefaultMarkers);
                check(rows.Count == 1 && rows[0].Marker == "FIXME" && rows[0].Kind == "sclComment" && rows[0].Network == 1 && rows[0].Line == 21, "xml SCL LineComment FIXME found at line 21, got " + (rows.Count > 0 ? rows[0].Line.ToString() : "none"));
                rows = OfflineAnalysisLogic.ScanText(scl, SclFcText, OfflineAnalysisLogic.DefaultMarkers);
                check(rows.Count == 2 && rows[0].Marker == "TODO" && rows[0].Line == 20 && rows[0].Network == 1 && rows[1].Marker == "HACK" && rows[1].Line == 24, "scl // TODO (line 20) and (* HACK (line 24) found; NOTE in string literal ignored");
                rows = OfflineAnalysisLogic.ScanText(s7dcl, LadFbS7dcl, OfflineAnalysisLogic.DefaultMarkers, LadFbS7res);
                check(rows.Count == 1 && rows[0].Marker == "TODO" && rows[0].Kind == "networkTitle" && rows[0].Network == 2 && rows[0].Line == 44, "s7dcl MLC network title TODO resolved via s7res (network 2, line 44), got " + (rows.Count > 0 ? rows[0].Line + "/" + rows[0].Network : "none"));
                check(OfflineAnalysisLogic.ScanText(scl, "// todo lower-case\n// TODO_Block suffix\n", OfflineAnalysisLogic.DefaultMarkers).Count == 0, "[sentinel] lower-case and suffixed words are not markers");
                check(OfflineAnalysisLogic.ScanText(scl, "// CHECK me\n", new[] { "CHECK" }).Count == 1, "custom marker list honoured");
                check(OfflineAnalysisLogic.ParseMarkers("").SequenceEqual(OfflineAnalysisLogic.DefaultMarkers) && OfflineAnalysisLogic.ParseMarkers("[\"A\",\"B\"]").SequenceEqual(new[] { "A", "B" }), "markersJson default and parse");
                check(Fails(() => OfflineAnalysisLogic.ParseMarkers("[]")) && Fails(() => OfflineAnalysisLogic.ParseMarkers("{\"a\":1}")) && Fails(() => OfflineAnalysisLogic.ParseMarkers("[\"a b\"]")), "markersJson refuses empty array, object and non-word marker");
                check(OfflineAnalysisLogic.ParseExtensions("[\"XML\",\".s7dcl\"]").SequenceEqual(new[] { ".xml", ".s7dcl" }), "extensionsJson normalized to lower-case with dot");
                var scanned = OfflineAnalysisLogic.EnumerateDocuments(dir, true, OfflineAnalysisLogic.DefaultExtensions);
                check(scanned.Count == 10 && scanned.Count == Directory.GetFiles(dir).Count(f => !f.EndsWith(".s7res")), "directory enumeration filters by extension (s7res excluded)");
                var csv = OfflineAnalysisLogic.ToCsv(new[] { new OfflineAnalysisLogic.AnnotationRow { File = "x.xml", BlockName = "B", Network = 2, Line = 5, Marker = "TODO", Kind = "k", Text = "say \"hi\", now" } });
                check(csv.StartsWith("file,blockName,network,line,marker,kind,text") && csv.Contains("x.xml,B,2,5,TODO,k,\"say \"\"hi\"\", now\""), "csv header and quoting");

                // ---- metrics
                var m = OfflineAnalysisLogic.Metrics(OfflineAnalysisLogic.ParseFile(baseline));
                check(m["networkCount"]!.GetValue<int>() == 2 && m["networksWithTitle"]!.GetValue<int>() == 2 && m["networksWithComment"]!.GetValue<int>() == 1, "LAD metrics: 2 networks, 2 titles, 1 comment");
                check(m["interfaceMembers"]!["Input"]!.GetValue<int>() == 2 && m["interfaceMembers"]!["Output"]!.GetValue<int>() == 2 && m["interfaceMembers"]!["Return"]!.GetValue<int>() == 1 && m["interfaceMembers"]!["total"]!.GetValue<int>() == 5, "LAD metrics: members per section");
                var calls = m["blockCalls"] as JsonArray;
                check(calls != null && calls.Count == 1 && calls[0]!["name"]!.GetValue<string>() == "FC_Scale" && calls[0]!["kind"]!.GetValue<string>() == "FC", "LAD metrics: CallInfo FC_Scale counted");
                var instr = (m["instructions"] as JsonArray)!.ToDictionary(i => i!["name"]!.GetValue<string>(), i => i!["count"]!.GetValue<int>());
                check(instr["Contact"] == 2 && instr["Coil"] == 1 && instr["Move"] == 1 && m["instructionCount"]!.GetValue<int>() == 4, "LAD metrics: Part instructions counted");
                check(m["sourceLines"]!.GetValue<int>() == 0 && m["commentRatio"] == null && m["maxNesting"] == null && m["nestingDerivable"]!.GetValue<bool>() == false, "LAD metrics: no SCL source lines, nesting not derivable");

                m = OfflineAnalysisLogic.Metrics(OfflineAnalysisLogic.ParseFile(fbXml));
                check(m["language"]!.GetValue<string>() == "SCL" && m["sourceLines"]!.GetValue<int>() == 6 && m["commentLines"]!.GetValue<int>() == 1 && Math.Abs(m["commentRatio"]!.GetValue<double>() - 0.167) < 0.001, "SCL xml metrics: 6 source lines, 1 comment, ratio 0.167; got " + m["sourceLines"] + "/" + m["commentLines"]);
                check(m["maxNesting"]!.GetValue<int>() == 2 && m["nestingDerivable"]!.GetValue<bool>(), "SCL xml metrics: nested IF depth 2");
                check(m["interfaceMembers"]!["Static"]!.GetValue<int>() == 2 && m["interfaceMembers"]!["total"]!.GetValue<int>() == 5, "SCL xml metrics: nested Struct members counted (cfg + cfg.limit)");
                check(((JsonArray)m["blockCalls"]!).Any(c => c!["name"]!.GetValue<string>() == "LIMIT"), "SCL xml metrics: rendered CallInfo call captured");

                m = OfflineAnalysisLogic.Metrics(fc);
                check(m["sourceLines"]!.GetValue<int>() == 11 && m["maxNesting"]!.GetValue<int>() == 2 && m["blockCallCount"]!.GetValue<int>() == 1, "scl text metrics");

                // ---- Myers diff: minimal and correct against a DP LCS on random inputs
                var rng = new Random(20260917);
                var diffOk = true;
                for (var round = 0; round < 200 && diffOk; round++)
                {
                    var a = RandomSeq(rng); var b = RandomSeq(rng);
                    var entries = OfflineAnalysisLogic.Diff(a, b);
                    var rebuilt = entries.Where(e => e.Op != OfflineAnalysisLogic.DiffOp.Delete).Select(e => e.Text).ToList();
                    var kept = entries.Where(e => e.Op != OfflineAnalysisLogic.DiffOp.Insert).Select(e => e.Text).ToList();
                    var equalCount = entries.Count(e => e.Op == OfflineAnalysisLogic.DiffOp.Equal);
                    if (!rebuilt.SequenceEqual(b) || !kept.SequenceEqual(a) || equalCount != Lcs(a, b)) { diffOk = false; Console.WriteLine("  diff mismatch: a=" + string.Join("", a) + " b=" + string.Join("", b) + " equal=" + equalCount + " lcs=" + Lcs(a, b)); }
                }
                check(diffOk, "Myers diff reproduces both sides and is minimal (LCS) on 200 random cases");
                check(OfflineAnalysisLogic.Diff(new List<string>(), new List<string>()).Count == 0, "[sentinel] empty diff");
                var hunks = OfflineAnalysisLogic.BuildHunks(OfflineAnalysisLogic.Diff(new[] { "a", "b", "c", "d", "e", "f", "g" }, new[] { "a", "b", "X", "d", "e", "f", "Y" }), 1);
                check(hunks.Count == 2 && hunks[0]!["leftStart"]!.GetValue<int>() == 2 && hunks[0]!["removed"]!.GetValue<int>() == 1 && hunks[0]!["added"]!.GetValue<int>() == 1, "hunks split by context distance");

                // ---- masking
                check(OfflineAnalysisLogic.Mask("id=3f2504e0-4f89-11d3-9a0c-0305e82c3301 at 2026-05-11T00:00:00.0000000Z") == "id=<guid> at <timestamp>", "GUID and ISO timestamp masked");
                check(OfflineAnalysisLogic.Mask("Number=901 T#500ms") == "Number=901 T#500ms", "[sentinel] ordinary values are not masked");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static List<string> RandomSeq(Random rng)
        {
            var n = rng.Next(0, 12);
            var list = new List<string>();
            for (var i = 0; i < n; i++) list.Add(((char)('a' + rng.Next(0, 4))).ToString());
            return list;
        }

        private static int Lcs(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            var dp = new int[a.Count + 1, b.Count + 1];
            for (var i = 1; i <= a.Count; i++)
                for (var j = 1; j <= b.Count; j++)
                    dp[i, j] = a[i - 1] == b[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);
            return dp[a.Count, b.Count];
        }
    }
}
