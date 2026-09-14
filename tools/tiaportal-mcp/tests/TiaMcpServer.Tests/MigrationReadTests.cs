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
            using (var capacity = new MigrationPages(() => now))
            {
                int closed = 0;
                var active = Enumerable.Range(0, 16).Select(_ => capacity.Read(project, "{}", "", 1, 5000, () => Rows(3, null, () => closed++))).ToList();
                check(Throws(() => capacity.Read(project, "{}", "", 1, 5000, () => Rows(3))), "16 unfinished collections still enforce active capacity");
                var oldest = capacity.Read(project, "{}", active[0]["nextCursor"]!.ToString(), 10, 5000, () => throw new Exception());
                check(closed == 1 && oldest["nextCursor"] == null && oldest["releaseCursor"] != null, "completion immediately releases the live iterator and returns an explicit release handle");
                var retained = capacity.Read(project, "{}", "", 10, 5000, () => Rows(1));
                for (int i = 0; i < 30; i++) capacity.Read(project, "{}", "", 10, 5000, () => Rows(1));
                var retainedReplay = capacity.Read(project, "{}", retained["pageCursor"]!.ToString(), 10, 5000, () => throw new Exception());
                check(retainedReplay.ToJsonString() == retained.ToJsonString(), "completed page replays identically alongside 15 unfinished collections");
                for (int i = 0; i < 10; i++) capacity.Read(project, "{}", "", 10, 5000, () => Rows(1));
                check(Throws(() => capacity.Read(project, "{}", oldest["pageCursor"]!.ToString(), 10, 5000, () => Rows(0))) && capacity.Read(project, "{}", retained["pageCursor"]!.ToString(), 10, 5000, () => throw new Exception()).ToJsonString() == retained.ToJsonString(), "completed LRU evicts old replay pages while retaining recently replayed pages");
                check(capacity.Read(project, "{}", active[1]["nextCursor"]!.ToString(), 1, 5000, () => throw new Exception())["records"]![0]!["path"]!.ToString() == "/1", "completed cache pressure never evicts or restarts active iterators");
                now = now.AddMinutes(11);
                check(Throws(() => capacity.Read(project, "{}", retained["pageCursor"]!.ToString(), 10, 5000, () => Rows(0))), "completed replay expires after 10 idle minutes");
                check(capacity.Read(project, "{}", active[2]["nextCursor"]!.ToString(), 1, 5000, () => throw new Exception())["records"]![0]!["path"]!.ToString() == "/1", "active cursors retain their independent 30 minute idle lifetime");
            }
            NativeExportTests.Run(check);
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
            var markerHmi = new Hmi();
            var internalRoot = new Tag { Name = "InternalRoot", Connection = "<内部变量>" };
            internalRoot.Members.Add(new Tag { Name = "member" });
            var plcRoot = new Tag { Name = "PlcRoot", Connection = "PLC_Connection", PlcTag = "DB.Value" };
            plcRoot.Members.Add(new Tag { Name = "localMember", Connection = "<内部变量>" });
            markerHmi.Tags.AddRange(new[] { internalRoot, plcRoot,
                new Tag { Name = "Whitespace", Connection = " \t<内部变量> " },
                new Tag { Name = "NearMarker", Connection = "<内部变量>_PLC" },
                new Tag { Name = "Unknown", Connection = "Connection_A" },
                new Tag { Name = "Conflict", Connection = "<内部变量>", PlcTag = "DB.Value" } });
            var markerRows = Drain(pages, project, () => UnifiedTagDefinitions.Read(markerHmi), out var markerPage, 11);
            JsonObject Source(string path) => markerRows.Single(r => r["kind"]?.ToString() == "tagSource" && r["path"]?.ToString() == path + "/$source");
            var internalSource = Source("/Tags/InternalRoot");
            check(internalSource["origin"]!.ToString() == "internal" && internalSource["originBasis"]!.ToString() == "ownConnectionMarker" && internalSource["classificationComplete"]!.GetValue<bool>(), "localized internal connection marker proves internal origin");
            check(Source("/Tags/Whitespace")["origin"]!.ToString() == "internal" && Source("/Tags/Whitespace")["own"]!["Connection"]!.ToString() == " \t<内部变量> ", "marker comparison tolerates surrounding whitespace without changing evidence");
            check(markerRows.Single(r => r["path"]?.ToString() == "/Tags/InternalRoot/Connection")["value"]!.ToString() == "<内部变量>", "raw connection tagField preserves localized marker exactly");
            var inheritedInternal = Source("/Tags/InternalRoot/Members/member");
            check(inheritedInternal["origin"]!.ToString() == "internal" && inheritedInternal["originBasis"]!.ToString() == "inferredFromRoot" && inheritedInternal["own"]!["Connection"]!.ToString() == "", "blank member inherits root origin without fabricating member connection");
            check(Source("/Tags/PlcRoot/Members/localMember")["originBasis"]!.ToString() == "ownConnectionMarker" && Source("/Tags/PlcRoot/Members/localMember")["origin"]!.ToString() == "internal", "explicit member internal marker overrides PLC root inference");
            check(Source("/Tags/NearMarker")["origin"]!.ToString() == "unresolvedConnection" && Source("/Tags/Unknown")["origin"]!.ToString() == "unresolvedConnection", "ordinary connection names and near matches are not internal markers");
            check(Source("/Tags/Conflict")["classificationStatus"]!.ToString() == "conflictingEvidence" && Source("/Tags/Conflict")["own"]!["PlcTag"]!.ToString() == "DB.Value", "contradictory internal marker and PLC symbol remain explicit raw evidence");
            check(markerPage["readComplete"]!.GetValue<bool>() && !markerPage["classificationComplete"]!.GetValue<bool>() && markerPage["readFailureCount"]!.GetValue<int>() == 0 && markerPage["classificationFailureCount"]!.GetValue<int>() == 3 && !markerPage["dataComplete"]!.GetValue<bool>(), "unresolved classification does not mean definition fields were unread");
            check(markerRows.Where(r => r["kind"]?.ToString() == "tagSource").All(r => r["definitionFieldsComplete"]!.GetValue<bool>() && r["definitionFieldCount"]!.GetValue<int>() == 17), "all 17 definition fields remain complete even when source classification is unresolved");
            check(!broken["readComplete"]!.GetValue<bool>() && broken["readFailureCount"]!.GetValue<int>() == 1 && broken["classificationFailureCount"]!.GetValue<int>() == 0, "real property failures are counted separately from classification failures");
            // Synthetic regression at the reported volume; this is not a field-project verification.
            var largeHmi = new Hmi(); var largeRoot = new Tag { Name = "Root", PlcTag = "DB.Structure", Connection = "PLC_Connection" }; largeHmi.Tags.Add(largeRoot);
            for (int i = 1; i < 4341; i++) largeRoot.Members.Add(new Tag { Name = "m" + i, Connection = i <= 113 ? "<内部变量>" : "" });
            using var largePages = new MigrationPages(); string largeCursor = ""; int tagCount = 0, fieldCount = 0, internalCount = 0, pageCount = 0; JsonObject largePage;
            do {
                largePage = largePages.Read(project, "{}", largeCursor, 500, 20000, () => UnifiedTagDefinitions.Read(largeHmi));
                foreach (var row in largePage["records"]!.AsArray()) {
                    if (row!["kind"]?.ToString() == "tag") tagCount++;
                    if (row["kind"]?.ToString() == "tagField") fieldCount++;
                    if (row["kind"]?.ToString() == "tagSource" && row["origin"]?.ToString() == "internal") internalCount++;
                }
                largeCursor = largePage["nextCursor"]?.ToString() ?? "";
                if (++pageCount > 1000) throw new Exception("Non-progressing tag pagination");
            } while (largeCursor != "");
            check(tagCount == 4341 && fieldCount == 4341 * 17 && internalCount == 113 && largePage["dataComplete"]!.GetValue<bool>() && !largePage["truncated"]!.GetValue<bool>() && pageCount > 1, "4341 synthetic definitions retain all 17 fields and classify 113 internal members through pagination");
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
