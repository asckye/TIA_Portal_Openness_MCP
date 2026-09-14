using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    internal static class MigrationReadTests
    {
        public sealed class Tag
        {
            public string Name { get; set; } = ""; public string TagTableName => "Table"; public string TagType { get; set; } = "Simple";
            public string Connection { get; set; } = ""; public string PlcName { get; set; } = ""; public string PlcTag { get; set; } = "";
            public string Address => ""; public string DataType { get; set; } = "Bool"; public string HmiDataType => "Bool";
            public int InitialValue => 0; public bool Persistent => false; public string AcquisitionMode => "Cyclic";
            public string AcquisitionCycle => "1 s"; public string AccessMode => "ReadWrite"; public string Scope => "Local";
            public int MaxLength => 0; public bool DBNameMultiplexing => false; public List<Tag> Members { get; } = new List<Tag>();
        }
        public sealed class Table { public string Name => "Table"; public List<Tag> Tags { get; } = new List<Tag>(); }
        public sealed class Hmi { public List<Table> TagTables { get; } = new List<Table>(); public List<Hmi> TagTableGroups { get; } = new List<Hmi>(); public List<Tag> Tags { get; } = new List<Tag>(); public List<object> Connections { get; } = new List<object>(); }
        public sealed class Broken { public string Good => "kept"; public string Bad => throw new InvalidOperationException("failed getter"); public Broken Parent => throw new Exception("Parent must never be read"); }
        public sealed class Dynamic { public string PropertyName { get; set; } = ""; public string Value { get; set; } = ""; }
        public sealed class Module
        {
            public string Name => "Navigation"; public string? LastDirectory; public string Body = "export function Navigate(name) { return Tags(name).Read(); }";
            public IEnumerable<FileInfo> Export(DirectoryInfo directory, string name)
            {
                LastDirectory = directory.FullName;
                var js = new FileInfo(Path.Combine(directory.FullName, name + ".js")); File.WriteAllText(js.FullName, Body);
                var yml = new FileInfo(Path.Combine(directory.FullName, name + ".yml")); File.WriteAllText(yml.FullName, "ScriptModule:\n  Name: Navigation\n");
                return new[] { js, yml };
            }
        }
        private static IEnumerable<JsonObject> Rows(int count, Action<int>? onRead = null, Action? onDispose = null)
        { try { for (int i = 0; i < count; i++) { onRead?.Invoke(i); yield return MigrationRead.Scalar("/" + i, i); } } finally { onDispose?.Invoke(); } }
        private static bool Throws(Action action) { try { action(); return false; } catch { return true; } }
        private static List<JsonObject> Drain(MigrationPages pages, object project, Func<IEnumerable<JsonObject>> read, out JsonObject last, int size = 7)
        {
            string cursor = ""; var result = new List<JsonObject>(); int steps = 0;
            do { last = pages.Read(project, "{}", cursor, size, 5000, read); result.AddRange(last["records"]!.AsArray().Select(n => (JsonObject)n!.DeepClone())); cursor = last["nextCursor"]?.ToString() ?? ""; if (++steps > 2000) throw new Exception("Non-progressing cursor"); } while (cursor != "");
            return result;
        }
        internal static void Run(Action<bool,string> check)
        {
            Console.WriteLine("== Read-only migration collection, continuation and original script evidence ==");
            using var pages = new MigrationPages(); var project = new object(); int reads = 0, disposals = 0;
            var first = pages.Read(project, "{}", "", 2, 5000, () => Rows(5, _ => reads++, () => disposals++));
            check(reads == 2 && first["truncated"]!.GetValue<bool>() && !first["dataComplete"]!.GetValue<bool>(), "page stops without eagerly reading all source items");
            var replay = pages.Read(project, "{}", first["pageCursor"]!.ToString(), 2, 5000, () => throw new Exception());
            check(replay.ToJsonString() == first.ToJsonString() && reads == 2, "page retry replays identical evidence without reading again");
            var second = pages.Read(project, "{}", first["nextCursor"]!.ToString(), 2, 5000, () => throw new Exception());
            check(second["records"]![0]!["path"]!.ToString() == "/2", "continuation resumes at next unread element");
            check(Throws(() => pages.Read(project, "{\"different\":true}", second["nextCursor"]!.ToString(), 2, 5000, () => Rows(0))), "cursor scope mismatch is rejected");
            var end = pages.Read(project, "{}", second["nextCursor"]!.ToString(), 2, 5000, () => throw new Exception());
            check(end["dataComplete"]!.GetValue<bool>() && end["cumulativeCount"]!.GetValue<int>() == 5 && disposals == 1, "end of collection disposes iterator and reports complete count");
            check(Throws(() => pages.Read(project, "{}", first["pageCursor"]!.ToString(), 2, 5000, () => Rows(0))), "old consumed cursor is explicit error, not another traversal");
            var now = DateTime.UtcNow; using var expiry = new MigrationPages(() => now);
            var exp = expiry.Read(project, "{}", "", 1, 5000, () => Rows(5)); now = now.AddMinutes(31);
            check(Throws(() => expiry.Read(project, "{}", exp["nextCursor"]!.ToString(), 1, 5000, () => Rows(5))), "expired cursor fails explicitly");
            var changed = pages.Read(project, "{}", "", 1, 5000, () => Rows(5));
            check(Throws(() => pages.Read(new object(), "{}", changed["nextCursor"]!.ToString(), 1, 5000, () => Rows(5))), "project replacement invalidates pending iterator");
            var cancel = pages.Read(project, "{}", "", 1, 5000, () => Rows(5, null, () => disposals++));
            check(pages.Cancel(cancel["nextCursor"]!.ToString()) && disposals == 2, "explicit cursor release cleans up without project action");
            var graph = Drain(pages, project, () => MigrationRead.Graph(new Broken(), "/item"), out var broken);
            check(broken["apiCallSuccess"]!.GetValue<bool>() && !broken["dataComplete"]!.GetValue<bool>() && broken["failureCount"]!.GetValue<int>() == 1 && graph.Any(r => r["value"]?.ToString() == "kept"), "failed property is recorded while sibling values remain readable");
            check(!graph.Any(r => r["path"]!.ToString().Contains("Parent")), "owner backlinks are excluded before invocation");
            var c = new List<Dynamic> { new Dynamic { PropertyName = "A/B", Value = "Tags(\"drive\")" } };
            var selected = MigrationRead.Branch(c, "[{\"name\":\"A/B\",\"key\":\"PropertyName\"},{\"property\":\"Value\"}]");
            check((string?)selected == "Tags(\"drive\")", "exact collection-member selector handles slash and property key");
            check(Throws(() => MigrationRead.Branch(c, "[{\"method\":\"Clear\"}]")) && c.Count == 1, "branch rejects arbitrary methods and mutation");
            check(Throws(() => MigrationRead.Named(c, "a/b", "PropertyName")), "exact name lookup is case sensitive");
            var hmi = new Hmi(); var table = new Table(); hmi.TagTables.Add(table);
            var root = new Tag { Name = "Drive", PlcName = "PLC_1", PlcTag = "DB.Motor", Connection = "PLC_Connection" };
            table.Tags.Add(root); hmi.Tags.Add(root);
            var node = root; for (int i = 0; i < 20; i++) { var child = new Tag { Name = "m" + i }; node.Members.Add(child); node = child; }
            var tags = Drain(pages, project, () => UnifiedTagDefinitions.Read(hmi), out var complete, 3);
            check(tags.Count(r => r["kind"]?.ToString() == "tag") == 21 && complete["dataComplete"]!.GetValue<bool>(), "deep tag members span pages without truncation or repeated roots");
            var memberSource = tags.First(r => r["kind"]?.ToString() == "tagSource" && r["path"]!.ToString().Contains("Members"));
            check(memberSource["origin"]!.ToString() == "PLC" && memberSource["originBasis"]!.ToString() == "inferredFromRoot" && memberSource["own"]!["PlcTag"]!.ToString() == "", "member own fields stay empty; root source inference is explicit");
            check(tags.Count(r => r["kind"]?.ToString() == "tagAlias") == 1, "table/root duplicate is recorded as alias");
            var bounds = UnifiedTagDefinitions.Bounds("Array[-2..3, 1..7] of Bool");
            check(bounds["dimensions"]!.AsArray().Count == 2 && bounds["dimensions"]![0]!["lower"]!.GetValue<long>() == -2, "array signed bounds and dimensions retain declared range");
            check(UnifiedTagDefinitions.Bounds("UserArrayType")["status"]!.ToString() == "unsupported", "unknown bounds are not guessed from count");
            string code = "import * as C from 'Colors';\nconst limit=3;\nexport function f(a, {b}={b:1}) { const rx=/}/; return Tags('Drive.Speed').Read() + Tags(a + '.State').Read(); }\nexport const g = (x) => `value:${x}`;";
            var js = JavaScriptEvidence.Analyze(code, "Module.js");
            check(js["parseComplete"]!.GetValue<bool>() && js["rawText"]!.ToString() == code && js["functions"]!.AsArray().Count == 2, "AST preserves original text and finds declarations plus arrows");
            check(js["functions"]![0]!["parameters"]!.AsArray().Count == 2 && js["functions"]![0]!["body"]!.ToString().Contains("/}/"), "function params and full body survive braces in regex literals");
            check(js["imports"]![0]!["module"]!.ToString() == "Colors" && js["globalDefinitions"]!.AsArray().Count == 3, "imports and global statements retained");
            check(js["references"]![0]!["literalName"]!.ToString() == "Drive.Speed" && js["references"]![1]!["resolution"]!.ToString() == "runtimeExpressionUnresolved", "literal and runtime-concatenated tag references cannot be confused");
            var invalid = JavaScriptEvidence.Analyze("function {", "bad.js");
            check(invalid["bodyReadSuccess"]!.GetValue<bool>() && invalid["status"]!.ToString() == "unsupported" && invalid["rawText"]!.ToString() == "function {", "parse failure preserves body while reporting incomplete analysis");
            var longBody = "export function longText(){ return '" + new string('x', 70000) + "'; }";
            check(JavaScriptEvidence.Analyze(longBody, "long.js")["functions"]![0]!["rawText"]!.ToString() == longBody.Substring(7), "script over 64 KiB is not silently shortened");
            var module = new Module(); var native = UnifiedNativeRead.Export(module, "/Scripts/Navigation", false).ToList();
            check(native.Count(r => r["kind"]?.ToString() == "nativeFile") == 2 && native.Any(r => r["bodyReadSuccess"]?.GetValue<bool>() == true), "native export yields original YAML and actual JS body");
            check(module.LastDirectory != null && !Directory.Exists(module.LastDirectory), "temporary export files removed after complete read");
            var yaml = UnifiedNativeRead.Document("Interface:\n  speed:\n    DataType: Int\n    Value: drive.Speed\n", "Type.yml", ".yml").ToList();
            check(yaml.Count == 2 && yaml.Any(r => r["path"]!.ToString().EndsWith("/speed/Value") && r["value"]!.ToString() == "drive.Speed"), "native internal values preserve exact binding paths and types");
            var xml = UnifiedNativeRead.Document("<!DOCTYPE x [<!ENTITY ext SYSTEM 'file:///c:/private'>]><x>&ext;</x>", "Type.xml", ".xml").ToList();
            check(xml.Single()["status"]!.ToString() == "failed", "native XML never resolves external entities");
        }
    }
}
