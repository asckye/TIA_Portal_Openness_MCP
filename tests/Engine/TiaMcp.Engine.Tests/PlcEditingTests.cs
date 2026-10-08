using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.Engine.Tests
{
    internal static class PlcEditingTests
    {
        const string Xml = @"<Document><Engineering version='V21'/><SW.Blocks.FC ID='0'><AttributeList>
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
    }
}
