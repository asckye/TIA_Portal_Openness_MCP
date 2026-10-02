using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    public static class PlcOfflineReferences
    {
        sealed class Entry
        {
            public string Id = "", Name = "", Kind = "";
            public List<(string Name, int Network)> Calls = new List<(string, int)>();
            public List<(string Name, int Network, string Scope)> Symbols = new List<(string, int, string)>();
        }
        public static JsonObject Analyze(string directory, string action, string target, int maxDepth, int offset, int limit)
        {
            if (!Path.IsPathRooted(directory) || !Directory.Exists(directory)) throw new ArgumentException("Existing absolute export directory required.");
            if (!new[] { "summary", "callers", "callees", "callPaths", "unreachable", "references" }.Contains(action)) throw new ArgumentException("Unknown reference action.");
            OfflineAnalysisLogic.ValidatePage(offset, limit);
            if (maxDepth < 1 || maxDepth > 50) throw new ArgumentException("maxDepth must be 1..50.");
            directory = Path.GetFullPath(directory);
            var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var files = new List<string>(); var pending = new Stack<string>(); pending.Push(directory); int visitedFolders = 0;
            // No symlink recursion and no unbounded materialization before checking limits.
            while (pending.Count > 0)
            {
                var folder = pending.Pop();
                if (++visitedFolders > 4000) throw new ArgumentException("Directory traversal limit exceeded.");
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Reparse directories refused.");
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    if (!new[] { ".xml", ".scl", ".s7dcl" }.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Reparse files refused.");
                    files.Add(file); if (files.Count > 2000) throw new ArgumentException("Maximum 2000 export documents.");
                }
                foreach (var sub in Directory.EnumerateDirectories(folder)) { pending.Push(sub); if (pending.Count > 2000) throw new ArgumentException("Directory traversal limit exceeded."); }
            }
            if (files.Count == 0) throw new ArgumentException("No exported PLC documents found; no references were queried.");
            var entries = new List<Entry>(); var failures = new JsonArray(); var warnings = new JsonArray(); long bytes = 0;
            foreach (var path in files.OrderBy(p => p, StringComparer.Ordinal))
            {
                bytes += new FileInfo(path).Length;
                if (bytes > 64L * 1024 * 1024) throw new ArgumentException("Export set exceeds 64 MiB.");
                var id = path.Substring(prefix.Length).Replace('\\', '/');
                try
                {
                    if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Text-only SCL/S7DCL is not indexed by this structural query. Export SimaticML for explicit CallInfo and Symbol records.");
                    var d = PlcDocumentEditing.Parse(PlcDocumentEditing.Read(path));
                    var entry = new Entry { Id = id, Name = PlcDocumentEditing.Name(d), Kind = PlcDocumentEditing.Block(d).Name.LocalName };
                    var networkIndex = 0;
                    foreach (var n in PlcDocumentEditing.Networks(d))
                    {
                        foreach (var call in n.Descendants().Where(e => e.Name.LocalName == "CallInfo"))
                        {
                            var name = (string?)call.Attribute("Name") ?? "";
                            if (name.Length > 0) entry.Calls.Add((name, networkIndex));
                            else warnings.Add(id + ": unnamed CallInfo in network " + networkIndex);
                        }
                        foreach (var access in n.Descendants().Where(e => e.Name.LocalName == "Access"))
                        {
                            var scope = (string?)access.Attribute("Scope") ?? "";
                            if (scope != "GlobalVariable" && scope != "GlobalConstant") continue;
                            var symbol = access.Elements().SingleOrDefault(e => e.Name.LocalName == "Symbol");
                            if (symbol == null) continue;
                            // Keep quoted components separate: tag "A.B" differs from member "A"."B".
                            var name = string.Join(".", symbol.Elements().Where(e => e.Name.LocalName == "Component").Select(e => "\"" + ((string?)e.Attribute("Name") ?? "").Replace("\"", "\"\"") + "\""));
                            if (name.Length > 0) entry.Symbols.Add((name, networkIndex, scope));
                        }
                        networkIndex++;
                    }
                    entries.Add(entry);
                }
                catch (Exception ex) { failures.Add(new JsonObject { ["file"] = id, ["reason"] = ex.Message }); }
            }
            if (entries.Count == 0) throw new ArgumentException("No supported single-block SimaticML files could be indexed.");
            var names = entries.GroupBy(e => e.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            var byId = entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
            var edges = new List<(string From, string To, string Name, int Network)>(); var unresolved = new JsonArray();
            foreach (var e in entries)
                foreach (var call in e.Calls)
                {
                    if (names.TryGetValue(call.Name, out var found) && found.Length == 1) edges.Add((e.Id, found[0].Id, call.Name, call.Network));
                    else unresolved.Add(new JsonObject { ["caller"] = e.Id, ["name"] = call.Name, ["networkIndex"] = call.Network,
                        ["reason"] = found == null ? "Not in supplied exports (may be a system instruction or missing block)" : "Ambiguous name across supplied exports/scopes" });
                }
            string Resolve()
            {
                if (byId.ContainsKey(target)) return target;
                if (names.TryGetValue(target, out var found) && found.Length == 1) return found[0].Id;
                throw new ArgumentException("Target is absent or ambiguous; use the exact file identifier from summary.");
            }
            var rows = new JsonArray(); var truncated = false; var cycles = 0;
            if (action == "summary")
                foreach (var e in entries) rows.Add(new JsonObject { ["id"] = e.Id, ["name"] = e.Name, ["kind"] = e.Kind, ["explicitCalls"] = e.Calls.Count, ["globalSymbolAccesses"] = e.Symbols.Count });
            else if (action == "references")
            {
                if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Exact quoted component path required, e.g. \"Motor\".\"Run\".");
                foreach (var e in entries) foreach (var symbol in e.Symbols.Where(s => s.Name == target)) rows.Add(new JsonObject {
                    ["block"] = e.Id, ["symbol"] = symbol.Name, ["networkIndex"] = symbol.Network, ["scope"] = symbol.Scope,
                    ["accessKind"] = "Unknown", ["note"] = "Read/write direction is not inferred from pin names." });
            }
            else if (action == "callers" || action == "callees")
            {
                var id = Resolve();
                foreach (var e in edges.Where(e => action == "callers" ? e.To == id : e.From == id)) rows.Add(new JsonObject { ["caller"] = e.From, ["callee"] = e.To, ["networkIndex"] = e.Network });
            }
            else
            {
                var roots = entries.Where(e => e.Kind == "SW.Blocks.OB").Select(e => e.Id).ToArray();
                if (roots.Length == 0) throw new ArgumentException("No OB roots in supplied exports; reachability cannot be evaluated.");
                var adjacency = edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).Distinct().ToArray());
                var reached = new HashSet<string>(roots); var queue = new Queue<string>(roots);
                while (queue.Count > 0) { var from = queue.Dequeue(); if (adjacency.TryGetValue(from, out var tos)) foreach (var to in tos) if (reached.Add(to)) queue.Enqueue(to); }
                if (action == "unreachable")
                    foreach (var e in entries.Where(e => !reached.Contains(e.Id) && (e.Kind == "SW.Blocks.FC" || e.Kind == "SW.Blocks.FB"))) rows.Add(new JsonObject { ["id"] = e.Id, ["classification"] = "No explicit call path from supplied OBs; not proof of unused code" });
                else
                {
                    var wanted = Resolve(); int visits = 0;
                    void Walk(string id, List<string> path)
                    {
                        if (++visits > 10000 || rows.Count >= 2000) { truncated = true; return; }
                        if (path.Contains(id)) { cycles++; return; }
                        var next = new List<string>(path) { id };
                        if (id == wanted) { rows.Add(new JsonObject { ["path"] = new JsonArray(next.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()) }); return; }
                        if (!adjacency.TryGetValue(id, out var children)) return;
                        if (next.Count - 1 >= maxDepth) { truncated = true; return; }
                        foreach (var child in children) { if (truncated && visits > 10000) break; Walk(child, next); }
                    }
                    foreach (var root in roots) Walk(root, new List<string>());
                }
            }
            var page = new JsonArray(rows.Skip(offset).Take(limit).Select(n => n!.DeepClone()).ToArray());
            return new JsonObject { ["action"] = action, ["fileCount"] = files.Count, ["indexedBlocks"] = entries.Count,
                ["failures"] = failures, ["warnings"] = warnings, ["unresolvedCalls"] = unresolved, ["rowCount"] = rows.Count,
                ["offset"] = offset, ["rows"] = page, ["hasMore"] = offset + page.Count < rows.Count, ["truncated"] = truncated, ["cyclesStopped"] = cycles,
                ["inputParsedCompletely"] = failures.Count == 0, ["coverageComplete"] = false, ["dataComplete"] = false,
                ["nativeCrossReferencesQueried"] = false, ["safeToDelete"] = false,
                ["scope"] = "Only explicit SimaticML CallInfo and global Symbol nodes in supplied files. Indirect/dynamic calls, HMI references, external clients and omitted exports are not covered. No destructive decision may be inferred from an empty result." };
        }
    }
}
