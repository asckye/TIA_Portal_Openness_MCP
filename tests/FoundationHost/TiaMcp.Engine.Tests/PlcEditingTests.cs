using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.Engine.Tests
{
    internal static class PlcEditingTests
    {
        private const string Xml = @"<Document><Engineering version='V21'/><SW.Blocks.FC ID='0'><AttributeList>
<Name>Main</Name><Number>7</Number><ProgrammingLanguage>LAD</ProgrammingLanguage><MemoryLayout>Optimized</MemoryLayout><HeaderAuthor>Engineer</HeaderAuthor>
<Interface><Sections xmlns='urn:interface'><Section Name='Input'><Member Name='Speed' Datatype='Int'><AttributeList><BooleanAttribute Name='ExternalWritable'>false</BooleanAttribute></AttributeList><StartValue>1</StartValue></Member></Section></Sections></Interface>
</AttributeList><ObjectList><SW.Blocks.CompileUnit ID='1'><AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage><NetworkSource><FlgNet xmlns='urn:flg'><Parts>
<Access Scope='GlobalVariable' UId='10'><Symbol><Component Name='Motor'/><Component Name='Run'/></Symbol></Access><Access Scope='GlobalVariable' UId='11'><Symbol><Component Name='Other'/></Symbol></Access><Call UId='12'><CallInfo Name='Helper' BlockType='FC'/></Call></Parts><Wires><Wire UId='13'><IdentCon UId='10'/><NameCon UId='12' Name='in'/></Wire></Wires></FlgNet></NetworkSource></AttributeList>
<ObjectList><MultilingualText ID='2' CompositionName='Title'><ObjectList><MultilingualTextItem ID='3' CompositionName='Items'><AttributeList><Culture>zh-CN</Culture><Text>Original</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList>
</SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>";
        internal static void Run(Action<bool, string> check)
        {
            bool Fails(Action run) { try { run(); return false; } catch { return true; } }
            string Canon(string xml) => PlcDocumentEditing.Canonical(PlcDocumentEditing.Parse(xml));
            var hash = PlcDocumentEditing.HashText(Canon(Xml));
            var inspection = PlcDocumentEditing.Inspect(Xml);
            check(inspection["networks"]![0]!["language"]!.GetValue<string>() == "LAD", "PLC capability reads the compile-unit language");
            check(inspection["nativeImportValidated"]!.GetValue<bool>() == false, "PLC capability never claims native validation");
            check(inspection["networks"]![0]!["textTargets"]![0]!["culture"]!.GetValue<string>() == "zh-CN", "PLC capability exposes actual culture");
            check(Canon(Xml) == Canon(Xml.Replace("ID='0'", "ID='90'").Replace("ID='1'", "ID='91'").Replace("UId='10'", "UId='110'")), "PLC fingerprint normalizes consistent identifier renumbering");
            check(Canon(Xml) != Canon(Xml.Replace("<IdentCon UId='10'", "<IdentCon UId='11'")), "PLC fingerprint detects changed wire connections");
            check(Canon(Xml) != Canon(Xml.Replace("<Text>Original", "<Text>2026-10-01T00:00:00Z")), "PLC fingerprint keeps literal timestamps");
            check(Canon(Xml.Replace("Original", "11111111-1111-1111-1111-111111111111")) != Canon(Xml.Replace("Original", "22222222-2222-2222-2222-222222222222")), "PLC fingerprint keeps GUID literal changes");
            check(Fails(() => Canon(Xml.Replace("ID='1'", "ID='0'"))), "PLC fingerprint refuses duplicate object IDs");
            check(Fails(() => PlcDocumentEditing.Parse("<!DOCTYPE Document [<!ENTITY x SYSTEM 'file:///not-read'>]>" + Xml)), "PLC parser prohibits external entities");
            check(Fails(() => PlcDocumentEditing.Parse(Xml.Replace("</Document>", "<SW.Blocks.FC><AttributeList><Name>X</Name></AttributeList></SW.Blocks.FC></Document>"))), "PLC parser refuses multiple blocks");
            const string change = "[{\"action\":\"setNetworkText\",\"networkIndex\":0,\"field\":\"Title\",\"culture\":\"zh-CN\",\"expectedValue\":\"Original\",\"value\":\"Updated & <safe>\"}]";
            var edited = PlcDocumentEditing.Patch(Xml, change, hash);
            check(edited.Contains("Updated &amp; &lt;safe&gt;"), "PLC patch escapes XML text");
            check(Canon(edited).Replace("Updated &amp; &lt;safe&gt;", "Original") == Canon(Xml), "PLC patch preserves all unrelated XML and wiring");
            check(Fails(() => PlcDocumentEditing.Patch(Xml, change, "stale")), "PLC patch refuses stale fingerprint");
            check(Fails(() => PlcDocumentEditing.Patch(Xml, change.Replace("Original", "wrong"), hash)), "PLC patch refuses stale target text");
            check(Fails(() => PlcDocumentEditing.Patch(Xml, change.Replace("zh-CN", "en-US"), hash)), "PLC patch does not invent cultures");
            check(Fails(() => PlcDocumentEditing.Patch(Xml, change.Replace("\"networkIndex\":0", "\"networkIndex\":-1"), hash)), "PLC patch refuses negative network index");
            check(Fails(() => PlcDocumentEditing.Patch(Xml, change.Replace("\"value\":", "\"typo\":0,\"value\":"), hash)), "PLC patch refuses unknown properties");
            var memberChange = "[{\"action\":\"setMemberStartValue\",\"section\":\"Input\",\"memberPath\":\"Speed\",\"expectedValue\":\"1\",\"value\":\"2\"}]";
            var memberEdited = PlcDocumentEditing.Patch(Xml, memberChange, hash);
            check(Canon(memberEdited).Replace("<StartValue>2</StartValue>", "<StartValue>1</StartValue>") == Canon(Xml), "PLC member edit preserves access attributes");
            var readOnly = Xml.Replace("Name='Speed'", "Name='Speed' ReadOnly='true'");
            check(Fails(() => PlcDocumentEditing.Patch(readOnly, memberChange, PlcDocumentEditing.HashText(Canon(readOnly)))), "PLC patch refuses readonly members");
            var library = Xml.Replace("<Name>Main", "<LibraryTypeGuid>some-guid</LibraryTypeGuid><Name>Main");
            check(Fails(() => PlcDocumentEditing.Patch(library, change, PlcDocumentEditing.HashText(Canon(library)))), "PLC patch refuses library type detachment");
            var preserved = new JsonArray();
            var merged = PlcDocumentEditing.PrepareImport(Xml, edited.Replace("<HeaderAuthor>Engineer</HeaderAuthor>", ""), preserved);
            check(preserved.Any(n => n!.GetValue<string>() == "HeaderAuthor") && merged.Contains("Engineer"), "PLC import preserves omitted author");
            var flagsMerged = PlcDocumentEditing.PrepareImport(Xml, Xml.Replace("<AttributeList><BooleanAttribute Name='ExternalWritable'>false</BooleanAttribute></AttributeList>", ""), new JsonArray());
            check(Canon(flagsMerged) == Canon(Xml), "PLC import preserves omitted member access flags");
            check(Canon(Xml.Replace("<Text>Original</Text>", "<Text> </Text>")) != Canon(Xml.Replace("<Text>Original</Text>", "<Text></Text>")), "PLC fingerprint preserves whitespace-only literal values");
            check(Fails(() => PlcDocumentEditing.PrepareImport(Xml, Xml.Replace("<Number>7", "<Number>8"), new JsonArray())), "PLC import refuses renumbering");
            check(Fails(() => PlcDocumentEditing.PrepareImport(Xml, Xml.Replace("<Name>Main", "<Name>Other"), new JsonArray())), "PLC import refuses a different block");
            check(Fails(() => PlcDocumentEditing.PrepareImport(library, Xml, new JsonArray())), "PLC import refuses a library-connected original");
            var safety = Xml.Replace("<ProgrammingLanguage>LAD", "<ProgrammingLanguage>F_LAD");
            check(Fails(() => PlcDocumentEditing.PrepareImport(safety, safety, new JsonArray())), "PLC import refuses Safety and unknown code languages");
            MemberComments(check);

            var parent = Path.GetFullPath(Path.GetTempPath());
            var folder = Path.Combine(parent, "tia-plc-edit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var exports = Path.Combine(folder, "exports"); Directory.CreateDirectory(exports);
                File.WriteAllText(Path.Combine(exports, "Main.xml"), Xml.Replace("SW.Blocks.FC", "SW.Blocks.OB"));
                File.WriteAllText(Path.Combine(exports, "Helper.xml"), Xml.Replace("<Name>Main", "<Name>Helper").Replace("Name='Helper' BlockType", "Name='Main' BlockType"));
                File.WriteAllText(Path.Combine(exports, "Unreachable.xml"), Xml.Replace("<Name>Main", "<Name>Unreachable"));
                JsonObject Query(string action, string target = "", int depth = 10, int offset = 0, int limit = 100) => PlcOfflineReferences.Analyze(exports, action, target, depth, offset, limit);
                check(Query("callers", "Helper")["rowCount"]!.GetValue<int>() == 2, "Offline reference query returns explicit callers");
                check(Query("callees", "Main")["rows"]![0]!["callee"]!.GetValue<string>() == "Helper.xml", "Offline reference resolves names to file identities");
                check(Query("callPaths", "Helper")["rowCount"]!.GetValue<int>() == 1, "Offline reference finds OB-root call paths");
                check(Query("callPaths", "Unreachable")["cyclesStopped"]!.GetValue<int>() > 0, "Offline reference terminates cyclic graphs");
                check(Query("unreachable")["rowCount"]!.GetValue<int>() == 1, "Offline reachability reports candidate only");
                check(!Query("unreachable")["safeToDelete"]!.GetValue<bool>(), "Unreachable export is never safe-to-delete proof");
                check(Query("references", "\"Motor\".\"Run\"")["rowCount"]!.GetValue<int>() == 3, "Offline global symbol lookup preserves components");
                check(Query("summary", limit: 1)["hasMore"]!.GetValue<bool>(), "Offline reference pagination reports more rows");
                check(Fails(() => Query("callers", "Missing")), "Absent target is not an empty success");
                File.WriteAllText(Path.Combine(exports, "Partial.scl"), "FUNCTION X");
                check(!Query("summary")["inputParsedCompletely"]!.GetValue<bool>(), "Unsupported source is explicit partial coverage");
                File.WriteAllText(Path.Combine(exports, "Duplicate.xml"), Xml.Replace("<Name>Main", "<Name>Helper"));
                check(Fails(() => Query("callers", "Helper")), "Duplicate name requires exact file ID");
                check(Query("summary")["unresolvedCalls"]!.AsArray().Count > 0, "Ambiguous call edges are unresolved, not arbitrarily selected");

                string current = Xml; var calls = new List<string>();
                void Export(string path) { calls.Add("export"); File.WriteAllText(path, current); }
                void Import(string path) { calls.Add("import"); current = File.ReadAllText(path); }
                void Verify() { calls.Add("binding"); }
                var preview = new JsonObject();
                PlcVerifiedImport.Execute(edited, "binding-A", folder, true, "", Export, Import, Verify, preview);
                var token = preview["token"]!.GetValue<string>();
                check(!calls.Contains("import") && File.Exists(preview["backup"]!["path"]!.GetValue<string>()), "Verified import preview backs up without native write");
                var applied = new JsonObject(); calls.Clear();
                PlcVerifiedImport.Execute(edited, "binding-A", folder, false, token, Export, Import, Verify, applied);
                check(calls.SequenceEqual(new[] { "binding", "export", "binding", "import", "binding", "export" }), "Verified import orders backup, binding, write and readback");
                check(applied["verificationSuccess"]!.GetValue<bool>(), "Verified import accepts matching complete readback");
                current = Xml; calls.Clear();
                check(Fails(() => PlcVerifiedImport.Execute(edited, "binding-B", folder, false, token, Export, Import, Verify, new JsonObject())) && !calls.Contains("import"), "Verified import refuses changed project/session identity");
                current = Xml.Replace("Original", "Concurrent"); calls.Clear();
                check(Fails(() => PlcVerifiedImport.Execute(edited, "binding-A", folder, false, token, Export, Import, Verify, new JsonObject())) && !calls.Contains("import"), "Verified import refuses changed current content");
                current = Xml; calls.Clear();
                check(Fails(() => PlcVerifiedImport.Execute(edited, "binding-A", folder, false, token, Export, _ => { calls.Add("failed-import"); throw new Exception("channel lost"); }, Verify, new JsonObject()))
                    && calls.Last() == "failed-import", "Native write exception makes no readback/rollback call");
                calls.Clear(); var mismatch = new JsonObject();
                PlcVerifiedImport.Execute(edited, "binding-A", folder, false, token, Export, _ => { }, Verify, mismatch);
                check(!mismatch["operationSuccess"]!.GetValue<bool>() && mismatch["mayHaveChanged"]!.GetValue<bool>(), "API success with unchanged document is a failed verification");
                calls.Clear();
                check(Fails(() => PlcVerifiedImport.Execute(edited, "binding-A", folder, false, "", Export, Import, Verify, new JsonObject())) && calls.Count == 0, "Missing preview token fails before any native call");
                var compilePreview = new JsonObject();
                PlcVerifiedImport.Execute(edited, "binding-A", folder, true, "", Export, Import, Verify, compilePreview, () => calls.Add("compile"));
                check(!calls.Contains("compile"), "Import preview never compiles");
                var compileToken = compilePreview["token"]!.GetValue<string>(); calls.Clear();
                check(Fails(() => PlcVerifiedImport.Execute(edited, "binding-A", folder, false, compileToken, Export, Import, Verify, new JsonObject(), () => { calls.Add("compile-failed"); throw new Exception("compile failed"); }))
                    && calls.Last() == "compile-failed", "Compilation failure stops before export or rollback");
            }
            finally { if (!Path.GetFullPath(folder).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture path"); Directory.Delete(folder, true); }
        }

        private const string MemberXml = @"<Document><Engineering version='V21'/><SW.Blocks.GlobalDB ID='0'><AttributeList>
<Name>Motors</Name><Number>1</Number><MemoryLayout>Optimized</MemoryLayout><ProgrammingLanguage>DB</ProgrammingLanguage>
<Interface><Sections xmlns='http://www.siemens.com/automation/Openness/SW/Interface/v5'><Section Name='Static'>
<Member Name='Motor' Datatype='Struct'><AttributeList><BooleanAttribute Name='ExternalAccessible'>true</BooleanAttribute></AttributeList>
<Member Name='Speed' Datatype='Int'><AttributeList><BooleanAttribute Name='ExternalWritable'>true</BooleanAttribute></AttributeList><StartValue>1</StartValue></Member></Member>
<Member Name='Enabled' Datatype='Bool'><Comment><MultiLanguageText Lang='en-US'>Enabled</MultiLanguageText></Comment><StartValue>true</StartValue></Member>
</Section></Sections></Interface></AttributeList><ObjectList><MultilingualText ID='1' CompositionName='Title'><ObjectList>
<MultilingualTextItem ID='2' CompositionName='Items'><AttributeList><Culture>zh-CN</Culture><Text>Motors</Text></AttributeList></MultilingualTextItem>
</ObjectList></MultilingualText></ObjectList></SW.Blocks.GlobalDB></Document>";

        private static void MemberComments(Action<bool, string> check)
        {
            string Change(string path = "Motor/Speed", string culture = "zh-CN", string expected = "", string value = "Speed & <rpm>", string section = "Static") =>
                new JsonObject { ["action"] = "setMemberComment", ["section"] = section, ["memberPath"] = path,
                    ["culture"] = culture, ["expectedValue"] = expected, ["value"] = value }.ToJsonString();
            string Patch(string xml, params string[] changes) => PlcDocumentEditing.Patch(xml, "[" + string.Join(",", changes) + "]",
                PlcDocumentEditing.Inspect(xml)["documentFingerprint"]!.GetValue<string>());
            XElement Member(string xml, string name) => XDocument.Parse(xml).Descendants()
                .Single(e => e.Name.LocalName == "Member" && (string?)e.Attribute("Name") == name);
            XElement Comment(string xml, string name) => PlcDocumentEditing.Child(Member(xml, name), "Comment");
            void Refuses(string xml, string change, string message, string label)
            {
                try { Patch(xml, change); check(false, label); }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
                { check(ex.Message.Contains(message, StringComparison.Ordinal), label); }
            }

            var edited = Patch(MemberXml, Change());
            var speed = Member(edited, "Speed"); var comment = Comment(edited, "Speed");
            check(comment.Name.Namespace == speed.Name.Namespace && comment.Elements().Single().Name == speed.Name.Namespace + "MultiLanguageText"
                && (string?)comment.Elements().Single().Attribute("Lang") == "zh-CN" && comment.Value == "Speed & <rpm>",
                "Member comment uses the interface namespace and exact escaped text");
            check(speed.Elements().Select(e => e.Name.LocalName).SequenceEqual(new[] { "AttributeList", "Comment", "StartValue" }),
                "New member comment follows AttributeList");
            check(XNode.DeepEquals(Member(MemberXml, "Enabled"), Member(edited, "Enabled")), "Member comment preserves untouched siblings");
            var bilingual = Patch(MemberXml, Change("Enabled", value: "启用"));
            check(Comment(bilingual, "Enabled").Elements().Count() == 2
                && Comment(bilingual, "Enabled").Elements().Single(e => (string?)e.Attribute("Lang") == "en-US").Value == "Enabled",
                "Member comment keeps the existing culture when adding another");
            var replaced = Patch(edited, Change(expected: "Speed & <rpm>", value: "New"));
            check(Comment(replaced, "Speed").Value == "New", "Member comment replaces an exact old value");
            Refuses(edited, Change(expected: "stale"), "old value changed", "Member comment refuses stale expectedValue");
            check(Comment(Patch(MemberXml, Change("Motor")), "Motor").Value == "Speed & <rpm>", "Struct member itself accepts comments");
            check(Member(Patch(edited, Change(expected: "Speed & <rpm>", value: "")), "Speed").Elements().All(e => e.Name.LocalName != "Comment"),
                "Empty member comment removes the culture and empty Comment");
            check(Comment(Patch(bilingual, Change("Enabled", expected: "启用", value: "")), "Enabled").Value == "Enabled",
                "Removing one culture preserves the other comment");
            check(XNode.DeepEquals(Member(MemberXml, "Speed"), Member(Patch(MemberXml, Change(value: "")), "Speed")),
                "Removing an absent comment leaves no empty element");
            var noAttributes = MemberXml.Replace("<AttributeList><BooleanAttribute Name='ExternalWritable'>true</BooleanAttribute></AttributeList>", "");
            check(Member(Patch(noAttributes, Change()), "Speed").Elements().First().Name.LocalName == "Comment", "New comment without attributes is first");
            foreach (var flag in new[] { "ReadOnly", "Informative" })
            foreach (var name in new[] { "Speed", "Motor" })
                Refuses(MemberXml.Replace("Name='" + name + "'", "Name='" + name + "' " + flag + "='true'"), Change(),
                    "Read-only/informative", "Member comment refuses " + flag + " on " + name);
            Refuses(MemberXml.Replace("<Comment>", "<Comment Informative='true'>"), Change("Enabled"), "Read-only/informative", "Informative comment is refused");
            Refuses(MemberXml, Change(section: "Missing"), "Section is absent or ambiguous", "Member comment refuses unknown section");
            Refuses(MemberXml, Change("Missing"), "Sequence contains no matching element", "Member comment refuses unknown member");
            foreach (var culture in new[] { "zho-CN", "zh-419", "zh-CN\n" })
                Refuses(MemberXml, Change(culture: culture), "culture must be a culture name such as zh-CN.", "Member comment refuses malformed culture " + culture);
            Refuses(MemberXml, Change(culture: "de-DE"), "culture de-DE does not occur in this document; use a project language that the export contains.",
                "Member comment refuses a culture absent from the export");
            Refuses(MemberXml, Change() + "," + Change(), "repeated", "Member comment refuses duplicate member and culture");
            Refuses(MemberXml, Change(value: "") + "," + Change(value: ""), "repeated", "Duplicate absent comment edits use member and culture identity");
            Refuses(MemberXml, Change().Replace("\"value\":", "\"typo\":0,\"value\":"), "Unknown patch property", "Member comment refuses unknown properties");
            Refuses(MemberXml.Replace("<Name>Motors", "<LibraryTypeGuid>type</LibraryTypeGuid><Name>Motors"), Change(), "Library-connected",
                "Member comment refuses library-bound blocks");
            check(Comment(Patch(MemberXml, Change(culture: "en-US"), Change()), "Speed").Elements().Count() == 2,
                "One call may edit different cultures on the same member");
            var inspect = PlcDocumentEditing.Inspect(edited);
            check((string?)inspect["members"]![1]!["comments"]!["zh-CN"] == "Speed & <rpm>"
                && PlcDocumentEditing.Inspect(MemberXml)["members"]![1]!["comments"]!.AsObject().Count == 0
                && inspect["supportedPatchActions"]!.AsArray().Any(n => (string?)n == "setMemberComment"), "Inspection reports member comments and action");
            check(Comment(PlcDocumentEditing.PrepareImport(edited, replaced, new JsonArray()), "Speed").Value == "New",
                "Verified block import preserves candidate member comments");
            var fb = MemberXml.Replace("SW.Blocks.GlobalDB", "SW.Blocks.FB").Replace("<ProgrammingLanguage>DB", "<ProgrammingLanguage>SCL")
                .Replace("<Section Name='Static'>", "<Section Name='Input'><Member Name='Run' Datatype='Bool'/></Section><Section Name='Static'>");
            check(Comment(Patch(fb, Change("Run", section: "Input"), Change()), "Run").Value == "Speed & <rpm>"
                && Comment(Patch(fb, Change()), "Speed").Value == "Speed & <rpm>", "FB Input and Static comments work");
            UdtComments(check, MemberXml, Change, Patch, Member, Comment);
        }

        private static void UdtComments(Action<bool, string> check, string blockXml,
            Func<string, string, string, string, string, string> change, Func<string, string[], string> patch,
            Func<string, string, XElement> member, Func<string, string, XElement> comment)
        {
            var udt = XDocument.Parse(blockXml.Replace("SW.Blocks.GlobalDB", "SW.Types.PlcStruct").Replace("Name='Static'", "Name='None'"));
            udt.Root!.Elements().Single(e => e.Name.LocalName == "SW.Types.PlcStruct").Elements().Single(e => e.Name.LocalName == "ObjectList").Remove();
            udt.Descendants().Where(e => new[] { "Number", "MemoryLayout", "ProgrammingLanguage", "Comment" }.Contains(e.Name.LocalName)).Remove();
            var xml = udt.ToString();
            var edited = patch(xml, new[] { change("Motor", "zh-CN", "", "电机", "None"), change("Motor/Speed", "zh-CN", "", "转速", "None") });
            var inspect = PlcDocumentEditing.Inspect(edited);
            check((string?)inspect["kind"] == "SW.Types.PlcStruct" && inspect["networks"]!.AsArray().Count == 0
                && (string?)inspect["members"]![0]!["comments"]!["zh-CN"] == "电机" && comment(edited, "Speed").Value == "转速",
                "UDT top-level and nested member comments inspect correctly");
            var noCultures = XDocument.Parse(xml); noCultures.Descendants().Where(e => e.Name.LocalName == "Comment").Remove();
            check(comment(patch(noCultures.ToString(), new[] { change("Motor/Speed", "zh-Hans-CN", "", "转速", "None") }), "Speed").Value == "转速",
                "UDT without cultures accepts an explicit script culture");
            var start = "{\"action\":\"setMemberStartValue\",\"section\":\"None\",\"memberPath\":\"Motor/Speed\",\"expectedValue\":\"1\",\"value\":\"2\"}";
            check(PlcDocumentEditing.Value(member(patch(xml, new[] { start }), "Speed"), "StartValue") == "2", "UDT start value uses shared member lookup");
            var withTitle = blockXml.Replace("SW.Blocks.GlobalDB", "SW.Types.PlcStruct").Replace("Name='Static'", "Name='None'");
            var title = "{\"action\":\"setBlockText\",\"field\":\"Title\",\"culture\":\"zh-CN\",\"expectedValue\":\"Motors\",\"value\":\"New\"}";
            check((string?)PlcDocumentEditing.Inspect(patch(withTitle, new[] { title }))["blockTextTargets"]![0]!["text"] == "New", "UDT existing title can be patched");
            var network = "{\"action\":\"setNetworkText\",\"networkIndex\":0,\"field\":\"Title\",\"culture\":\"zh-CN\",\"expectedValue\":\"\",\"value\":\"New\"}";
            try { patch(xml, new[] { network }); check(false, "UDT network edit is refused"); }
            catch (ArgumentException ex) { check(ex.Message == "UDT documents have no networks.", "UDT network edit has explicit refusal"); }
            foreach (var pair in new[] { (xml, blockXml), (blockXml, xml), (xml.Replace("SW.Types.PlcStruct", "SW.Types.Other"), blockXml),
                    (blockXml, xml.Replace("SW.Types.PlcStruct", "SW.Types.Other")) })
            {
                try { PlcDocumentEditing.PrepareImport(pair.Item1, pair.Item2, new JsonArray()); check(false, "UDT block import is refused"); }
                catch (ArgumentException ex) { check(ex.Message == "ImportPlcBlockVerified imports blocks only; import a UDT with ImportPlcType overwrite.", "UDT import has explicit route"); }
            }
            try { PlcDocumentEditing.Parse(xml); check(false, "Ordinary parser remains blocks only"); }
            catch (ArgumentException ex) { check(ex.Message == "Exactly one supported PLC block per XML file is required.", "Ordinary parser retains UDT refusal"); }
            try { PlcDocumentEditing.Inspect(xml.Replace("SW.Types.PlcStruct", "SW.Types.Other")); check(false, "Other type roots are refused"); }
            catch (ArgumentException ex) { check(ex.Message == "Exactly one supported PLC block per XML file is required.", "Other type roots retain refusal"); }
            var folder = Path.Combine(Path.GetTempPath(), "tia-udt-references-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(Path.Combine(folder, "Motor.xml"), xml);
                File.WriteAllText(Path.Combine(folder, "Main.xml"), Xml);
                var references = PlcOfflineReferences.Analyze(folder, "summary", "", 10, 0, 100);
                check(references["rowCount"]!.GetValue<int>() == 1 && references["failures"]!.AsArray().Count == 1
                    && references["failures"]![0]!.ToJsonString().Contains("Exactly one supported PLC block per XML file is required.", StringComparison.Ordinal),
                    "Offline references still report UDT as unindexed");
            }
            finally { Directory.Delete(folder, true); }
        }
    }
}
