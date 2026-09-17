using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    // Offline documentation and pre-check over exported PLC documents. No TIA Portal session:
    //   - RenderBlock: one SimaticML/SCL document -> Markdown with the interface table and one section
    //     per network (SCL listing for StructuredText, Mermaid flowchart + part listing for LAD/FBD).
    //   - RenderHandbook: a directory of exports -> one Markdown handbook (index, call cross-reference,
    //     per-block sections).
    //   - LintScl: heuristic SCL pre-check (unbalanced blocks/brackets, '=' vs ':=', missing ';', GOTO,
    //     nesting, line length, ...). Findings are hints, never a compiler verdict.
    // Parsing of headers/interfaces/SCL text is shared with OfflineAnalysisLogic; FlgNet graphs are
    // read here because the analysis layer only keeps them as diffable XML lines.
    public static class PlcDocumentationLogic
    {
        public const int MaxNetworksPerBlock = 2000;
        public const int MaxHandbookFiles = 2000;

        // Pin names that carry a value OUT of a part; everything else is treated as an input pin.
        private static readonly HashSet<string> OutputPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "out", "out1", "out2", "Q", "Q1", "Q2", "ENO", "ET", "CV", "QU", "QD", "RET_VAL", "Ret_Val", "RET", "ERROR", "STATUS", "BUSY", "DONE", "VALID", "NDR", "ERR", "OUT", "OUTPUT", "ENDCONDITION", "LEN", "RLO"
        };

        // ------------------------------------------------------------------ models

        public sealed class GraphNode
        {
            public string Id = "";
            public string Kind = "";      // part | access | rail
            public string Label = "";
            public string Name = "";      // part name / symbol
            public string Instance = "";
        }

        public sealed class GraphEdge
        {
            public string From = "";
            public string To = "";
            public string FromPin = "";
            public string ToPin = "";
        }

        public sealed class NetworkGraph
        {
            public List<GraphNode> Nodes = new List<GraphNode>();
            public List<GraphEdge> Edges = new List<GraphEdge>();
            public int OpenConnections;
        }

        public sealed class RenderedNetwork
        {
            public int Index;
            public string Title = "";
            public string Comment = "";
            public string Language = "";
            public List<string> SourceLines = new List<string>();
            public NetworkGraph? Graph;
            public string Listing = "";
        }

        public sealed class RenderedBlock
        {
            public OfflineAnalysisLogic.BlockDocument Document = new OfflineAnalysisLogic.BlockDocument();
            public List<RenderedNetwork> Networks = new List<RenderedNetwork>();
            public string Markdown = "";
            public int GraphicalNetworks;
            public int TextNetworks;
        }

        public sealed class LintFinding
        {
            public string Rule = "";
            public string Severity = "";   // error | warning | info
            public int Line;
            public string Message = "";
            public string Text = "";
        }

        public sealed class LintOptions
        {
            public HashSet<string> Disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int MaxLineLength = 120;
            public int MaxNesting = 5;
            public string[] Markers = OfflineAnalysisLogic.DefaultMarkers;
        }

        // ------------------------------------------------------------------ rendering one block

        public static RenderedBlock RenderFile(string path, string mermaidDirection = "LR")
        {
            var doc = OfflineAnalysisLogic.ParseFile(path);
            var text = File.ReadAllText(path, Encoding.UTF8);
            return Render(doc, text, mermaidDirection);
        }

        public static RenderedBlock RenderText(string text, string sourcePath, string mermaidDirection = "LR", string? s7res = null)
            => Render(OfflineAnalysisLogic.ParseText(text, sourcePath, s7res), text, mermaidDirection);

        public static RenderedBlock Render(OfflineAnalysisLogic.BlockDocument doc, string rawText, string mermaidDirection = "LR")
        {
            if (mermaidDirection != "LR" && mermaidDirection != "TD") throw new ArgumentException("mermaidDirection must be LR or TD.");
            var result = new RenderedBlock { Document = doc };
            var graphical = new Dictionary<int, XElement>();
            var body = (rawText ?? "").TrimStart('\uFEFF');
            if (doc.Format == "SimaticML" && OfflineAnalysisLogic.LooksLikeXml(body))
            {
                var root = XDocument.Parse(body).Root;
                if (root != null)
                {
                    var index = 0;
                    foreach (var unit in root.Descendants().Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit"))
                    {
                        index++;
                        var source = unit.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList")?
                            .Elements().FirstOrDefault(e => e.Name.LocalName == "NetworkSource")?.Elements().FirstOrDefault();
                        if (source != null && source.Name.LocalName == "FlgNet") graphical[index] = source;
                    }
                }
            }
            if (doc.Networks.Count > MaxNetworksPerBlock) throw new InvalidDataException("Block has " + doc.Networks.Count + " networks; limit is " + MaxNetworksPerBlock + ".");
            foreach (var net in doc.Networks)
            {
                var r = new RenderedNetwork { Index = net.Index, Title = net.Title, Comment = net.Comment, Language = net.Language };
                if (graphical.TryGetValue(net.Index, out var flg))
                {
                    r.Graph = ParseFlgNet(flg);
                    r.Listing = Listing(r.Graph);
                    result.GraphicalNetworks++;
                }
                else
                {
                    r.SourceLines = net.SourceLines;
                    result.TextNetworks++;
                }
                result.Networks.Add(r);
            }
            result.Markdown = ToMarkdown(result, mermaidDirection);
            return result;
        }

        // ------------------------------------------------------------------ FlgNet graph

        public static NetworkGraph ParseFlgNet(XElement flgNet)
        {
            var g = new NetworkGraph();
            var byUid = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
            var parts = flgNet.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
            if (parts != null)
            {
                foreach (var e in parts.Elements())
                {
                    var uid = (string?)e.Attribute("UId") ?? "";
                    if (uid.Length == 0) continue;
                    GraphNode node;
                    if (e.Name.LocalName == "Access")
                    {
                        var label = AccessLabel(e);
                        node = new GraphNode { Id = "A" + uid, Kind = "access", Label = label, Name = label };
                    }
                    else
                    {
                        var name = (string?)e.Attribute("Name") ?? e.Name.LocalName;
                        var instance = e.Elements().FirstOrDefault(x => x.Name.LocalName == "Instance");
                        var instanceName = instance == null ? "" : AccessLabel(instance);
                        var template = e.Elements().Where(x => x.Name.LocalName == "TemplateValue").Select(x => x.Value.Trim()).Where(v => v.Length > 0).ToList();
                        var callInfo = e.Elements().FirstOrDefault(x => x.Name.LocalName == "CallInfo");
                        var callName = callInfo == null ? "" : ((string?)callInfo.Attribute("Name") ?? "");
                        if (callInfo != null && instance == null)
                        {
                            var callInstance = callInfo.Elements().FirstOrDefault(x => x.Name.LocalName == "Instance");
                            if (callInstance != null) instanceName = AccessLabel(callInstance);
                        }
                        var label = callName.Length > 0 ? name + " \"" + callName + "\"" : name;
                        if (template.Count > 0) label += "<" + string.Join(",", template) + ">";
                        if (instanceName.Length > 0) label += " [" + instanceName + "]";
                        node = new GraphNode { Id = "P" + uid, Kind = "part", Label = label, Name = callName.Length > 0 ? callName : name, Instance = instanceName };
                    }
                    byUid[uid] = node;
                    g.Nodes.Add(node);
                }
            }
            var rail = new GraphNode { Id = "RAIL", Kind = "rail", Label = "power rail", Name = "Powerrail" };
            var railUsed = false;
            var wires = flgNet.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
            if (wires != null)
            {
                foreach (var wire in wires.Elements().Where(e => e.Name.LocalName == "Wire"))
                {
                    var pins = new List<(GraphNode? node, string pin, string kind)>();
                    foreach (var con in wire.Elements())
                    {
                        var local = con.Name.LocalName;
                        if (local == "Powerrail") { pins.Add((rail, "", "rail")); railUsed = true; continue; }
                        if (local == "OpenCon") { g.OpenConnections++; pins.Add((null, "", "open")); continue; }
                        var uid = (string?)con.Attribute("UId") ?? "";
                        byUid.TryGetValue(uid, out var node);
                        pins.Add((node, (string?)con.Attribute("Name") ?? "", local == "IdentCon" ? "ident" : "name"));
                    }
                    if (pins.Count < 2) continue;
                    var source = pins[0];
                    if (source.kind == "ident")
                    {
                        // Operand wire: direction from the pin name on the part side.
                        var partPin = pins.Skip(1).FirstOrDefault(p => p.kind == "name");
                        if (partPin.node == null || source.node == null) continue;
                        if (IsOutputPin(partPin.pin, partPin.node.Name)) g.Edges.Add(new GraphEdge { From = partPin.node.Id, To = source.node.Id, FromPin = partPin.pin });
                        else g.Edges.Add(new GraphEdge { From = source.node.Id, To = partPin.node.Id, ToPin = partPin.pin });
                        continue;
                    }
                    foreach (var sink in pins.Skip(1))
                    {
                        if (source.node == null || sink.node == null) continue;
                        if (sink.kind == "ident")
                        {
                            g.Edges.Add(new GraphEdge { From = source.node.Id, To = sink.node.Id, FromPin = source.pin });
                            continue;
                        }
                        g.Edges.Add(new GraphEdge { From = source.node.Id, To = sink.node.Id, FromPin = source.pin, ToPin = sink.pin });
                    }
                }
            }
            if (railUsed) g.Nodes.Insert(0, rail);
            return g;
        }

        // The "operand" pin of coils (Coil, SCoil, RCoil, ...) is where the result goes; on contacts/boxes it is an input.
        public static bool IsOutputPin(string pin, string partName = "")
            => pin.Length > 0 && (OutputPins.Contains(pin) || pin.StartsWith("OUT", StringComparison.OrdinalIgnoreCase) || pin.StartsWith("Q", StringComparison.Ordinal) && pin.Length <= 3
                || pin.Equals("operand", StringComparison.OrdinalIgnoreCase) && partName.IndexOf("Coil", StringComparison.OrdinalIgnoreCase) >= 0);

        public static string AccessLabel(XElement access)
        {
            var scope = (string?)access.Attribute("Scope") ?? "";
            var symbol = access.Elements().FirstOrDefault(e => e.Name.LocalName == "Symbol");
            if (symbol != null)
            {
                var comps = symbol.Elements().Where(e => e.Name.LocalName == "Component").Select(ComponentText).ToList();
                var path = string.Join(".", comps);
                if (scope == "LocalVariable" || scope == "LocalConstant") return "#" + path;
                if (scope == "GlobalVariable" || scope == "GlobalConstant") return "\"" + comps[0] + "\"" + (comps.Count > 1 ? "." + string.Join(".", comps.Skip(1)) : "");
                return path;
            }
            var constant = access.Elements().FirstOrDefault(e => e.Name.LocalName == "Constant");
            if (constant != null)
            {
                var value = constant.Elements().FirstOrDefault(e => e.Name.LocalName == "ConstantValue")?.Value.Trim() ?? "";
                var name = (string?)constant.Attribute("Name");
                return name != null ? "\"" + name + "\"" : value;
            }
            var address = access.Elements().FirstOrDefault(e => e.Name.LocalName == "Address");
            if (address != null)
            {
                var area = (string?)address.Attribute("Area") ?? "";
                var type = (string?)address.Attribute("Type") ?? "";
                var bitOffset = (string?)address.Attribute("BitOffset");
                var prefix = area == "Input" ? "I" : area == "Output" ? "Q" : area == "Memory" ? "M" : area == "PeripheryInput" ? "I" : area == "PeripheryOutput" ? "Q" : area;
                if (int.TryParse(bitOffset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits))
                {
                    var size = type == "Bool" ? "" : type == "Byte" ? "B" : type == "Word" || type == "Int" ? "W" : type == "DWord" || type == "DInt" || type == "Real" ? "D" : "";
                    return "%" + prefix + size + (bits / 8) + (type == "Bool" ? "." + (bits % 8) : "");
                }
                return "%" + prefix + "?";
            }
            var component = access.Elements().FirstOrDefault(e => e.Name.LocalName == "Component");
            if (component != null) return (scope == "LocalVariable" ? "#" : "") + ComponentText(component);
            var label = access.Elements().FirstOrDefault(e => e.Name.LocalName == "Label");
            if (label != null) return "label " + ((string?)label.Attribute("Name") ?? "");
            return scope.Length > 0 ? scope : "?";
        }

        private static string ComponentText(XElement component)
        {
            var name = (string?)component.Attribute("Name") ?? "";
            var indexes = component.Elements().Where(e => e.Name.LocalName == "Access").Select(AccessLabel).ToList();
            return indexes.Count > 0 ? name + "[" + string.Join(",", indexes) + "]" : name;
        }

        // "Contact(#Trig) -> TON<Time>[#tonInst](PT=T#500ms) -> Coil(#OutTonQ)": parts in wire order, operands inline.
        public static string Listing(NetworkGraph g)
        {
            var nodes = g.Nodes.ToDictionary(n => n.Id, n => n);
            var order = TopologicalOrder(g).Where(id => nodes[id].Kind == "part").ToList();
            var parts = new List<string>();
            foreach (var id in order)
            {
                var operands = g.Edges.Where(e => e.To == id && nodes.TryGetValue(e.From, out var f) && f.Kind == "access")
                    .Select(e => (e.ToPin.Length > 0 && e.ToPin != "operand" ? e.ToPin + "=" : "") + nodes[e.From].Label).ToList();
                var outputs = g.Edges.Where(e => e.From == id && nodes.TryGetValue(e.To, out var t) && t.Kind == "access")
                    .Select(e => (e.FromPin.Length > 0 && e.FromPin != "operand" ? e.FromPin + "=>" : "") + nodes[e.To].Label).ToList();
                var text = nodes[id].Label;
                if (operands.Count > 0) text += "(" + string.Join(", ", operands) + ")";
                if (outputs.Count > 0) text += "{" + string.Join(", ", outputs) + "}";
                parts.Add(text);
            }
            return string.Join(" -> ", parts);
        }

        private static List<string> TopologicalOrder(NetworkGraph g)
        {
            var incoming = g.Nodes.ToDictionary(n => n.Id, n => 0);
            foreach (var e in g.Edges) if (incoming.ContainsKey(e.To) && incoming.ContainsKey(e.From) && e.From != e.To) incoming[e.To]++;
            var ready = new Queue<string>(g.Nodes.Where(n => incoming[n.Id] == 0).Select(n => n.Id));
            var order = new List<string>();
            var seen = new HashSet<string>();
            while (ready.Count > 0)
            {
                var id = ready.Dequeue();
                if (!seen.Add(id)) continue;
                order.Add(id);
                foreach (var e in g.Edges.Where(e => e.From == id && incoming.ContainsKey(e.To)))
                {
                    incoming[e.To]--;
                    if (incoming[e.To] <= 0) ready.Enqueue(e.To);
                }
            }
            foreach (var n in g.Nodes) if (seen.Add(n.Id)) order.Add(n.Id);   // cycles (feedback wires) appended
            return order;
        }

        public static string Mermaid(NetworkGraph g, string direction = "LR")
        {
            var sb = new StringBuilder();
            sb.Append("flowchart ").Append(direction).Append('\n');
            foreach (var n in g.Nodes)
            {
                var label = MermaidLabel(n.Label);
                if (n.Kind == "rail") sb.Append("  ").Append(n.Id).Append("((\"").Append(label).Append("\"))\n");
                else if (n.Kind == "access") sb.Append("  ").Append(n.Id).Append("([\"").Append(label).Append("\"])\n");
                else sb.Append("  ").Append(n.Id).Append("[\"").Append(label).Append("\"]\n");
            }
            foreach (var e in g.Edges)
            {
                var pin = e.FromPin.Length > 0 && e.ToPin.Length > 0 ? e.FromPin + "→" + e.ToPin : e.FromPin.Length > 0 ? e.FromPin : e.ToPin;
                if (pin == "operand" || pin == "in" || pin == "out→in") pin = "";
                sb.Append("  ").Append(e.From);
                if (pin.Length > 0) sb.Append(" -- \"").Append(MermaidLabel(pin)).Append("\" --> "); else sb.Append(" --> ");
                sb.Append(e.To).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        private static string MermaidLabel(string text) => (text ?? "").Replace("\"", "#quot;").Replace("<", "#lt;").Replace(">", "#gt;");

        // ------------------------------------------------------------------ markdown

        public static string ToMarkdown(RenderedBlock block, string direction = "LR")
        {
            var doc = block.Document;
            var sb = new StringBuilder();
            sb.Append("# ").Append(Heading(doc)).Append("\n\n");
            if (doc.Title.Length > 0) sb.Append("**").Append(doc.Title.Replace("*", "\\*")).Append("**\n\n");
            if (doc.Comment.Length > 0) sb.Append(doc.Comment).Append("\n\n");
            var facts = doc.Attributes.Where(kv => kv.Key != "Number").Select(kv => "`" + kv.Key + "` = " + kv.Value).ToList();
            if (facts.Count > 0) sb.Append("Attributes: ").Append(string.Join("; ", facts)).Append("\n\n");

            if (doc.Members.Count > 0)
            {
                sb.Append("## Interface\n\n| Section | Name | Datatype |\n|---|---|---|\n");
                foreach (var m in doc.Members) sb.Append("| ").Append(m.Section).Append(" | `").Append(m.Path).Append("` | ").Append(m.Datatype.Replace("|", "\\|")).Append(" |\n");
                sb.Append('\n');
            }
            if (block.Networks.Count > 0)
            {
                sb.Append("## Networks (").Append(block.Networks.Count).Append(")\n\n");
                foreach (var net in block.Networks)
                {
                    sb.Append("### Network ").Append(net.Index);
                    if (net.Title.Length > 0) sb.Append(": ").Append(net.Title);
                    if (net.Language.Length > 0) sb.Append(" _(").Append(net.Language).Append(")_");
                    sb.Append("\n\n");
                    if (net.Comment.Length > 0) sb.Append(net.Comment).Append("\n\n");
                    if (net.Graph != null)
                    {
                        if (net.Listing.Length > 0) sb.Append("`").Append(net.Listing.Replace("`", "'")).Append("`\n\n");
                        sb.Append("```mermaid\n").Append(Mermaid(net.Graph, direction)).Append("\n```\n\n");
                        if (net.Graph.OpenConnections > 0) sb.Append("_").Append(net.Graph.OpenConnections).Append(" open connection(s) (unused output pins)._\n\n");
                    }
                    else if (net.SourceLines.Count > 0)
                    {
                        var lang = string.Equals(net.Language, "SCL", StringComparison.OrdinalIgnoreCase) || string.Equals(doc.Language, "SCL", StringComparison.OrdinalIgnoreCase) ? "scl" : "text";
                        sb.Append("```").Append(lang).Append('\n');
                        foreach (var line in net.SourceLines) sb.Append(line).Append('\n');
                        sb.Append("```\n\n");
                    }
                    else sb.Append("_(empty network)_\n\n");
                }
            }
            else if (doc.Format != "SimaticML" && doc.Canonical.Count > 0)
            {
                sb.Append("## Source\n\n```scl\n");
                foreach (var line in doc.Canonical) sb.Append(line).Append('\n');
                sb.Append("```\n\n");
            }
            var calls = doc.BlockCalls.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
            if (calls.Count > 0) sb.Append("## Calls\n\n").Append(string.Join("\n", calls.Select(c => "- `" + c + "`"))).Append("\n\n");
            var instr = doc.Instructions.GroupBy(i => i, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            if (instr.Count > 0) sb.Append("## Instructions\n\n").Append(string.Join(", ", instr.Select(g => "`" + g.Key + "`×" + g.Count()))).Append("\n\n");
            if (doc.Warnings.Count > 0) sb.Append("## Parser notes\n\n").Append(string.Join("\n", doc.Warnings.Select(w => "- " + w))).Append("\n\n");
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        // ------------------------------------------------------------------ handbook over a directory

        public sealed class HandbookEntry
        {
            public string File = "";
            public RenderedBlock? Block;
            public string Error = "";
        }

        public sealed class Handbook
        {
            public List<HandbookEntry> Entries = new List<HandbookEntry>();
            public string Markdown = "";
            public int Failed;
        }

        public static Handbook RenderHandbook(string directory, bool recursive, string[] extensions, string title, string direction = "LR")
        {
            var files = OfflineAnalysisLogic.EnumerateDocuments(directory, recursive, extensions);
            if (files.Count > MaxHandbookFiles) throw new InvalidDataException(files.Count + " documents exceed the handbook limit of " + MaxHandbookFiles + ".");
            var hb = new Handbook();
            foreach (var file in files)
            {
                var entry = new HandbookEntry { File = file };
                try { entry.Block = RenderFile(file, direction); }
                catch (Exception ex) { entry.Error = ex.Message; hb.Failed++; }
                hb.Entries.Add(entry);
            }
            hb.Markdown = HandbookMarkdown(hb, directory, title);
            return hb;
        }

        public static string HandbookMarkdown(Handbook hb, string directory, string title)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(string.IsNullOrWhiteSpace(title) ? "PLC program documentation" : title).Append("\n\n");
            sb.Append("Source directory: `").Append(directory).Append("` — ").Append(hb.Entries.Count).Append(" document(s)");
            if (hb.Failed > 0) sb.Append(", ").Append(hb.Failed).Append(" failed to parse");
            sb.Append(". Generated offline from exported documents; not a Siemens compile verdict.\n\n");

            var blocks = hb.Entries.Where(e => e.Block != null).Select(e => e.Block!).OrderBy(b => TypeRank(b.Document.BlockType)).ThenBy(b => b.Document.BlockName, StringComparer.OrdinalIgnoreCase).ToList();
            sb.Append("## Index\n\n| Block | Type | Number | Language | Networks | Interface members | Calls |\n|---|---|---|---|---|---|---|\n");
            foreach (var b in blocks)
            {
                var d = b.Document;
                sb.Append("| [`").Append(d.BlockName).Append("`](#").Append(Anchor(Heading(d))).Append(") | ").Append(d.BlockType).Append(" | ")
                  .Append(d.Attributes.TryGetValue("Number", out var n) ? n : "").Append(" | ").Append(d.Language).Append(" | ").Append(d.Networks.Count).Append(" | ")
                  .Append(d.Members.Count).Append(" | ").Append(d.BlockCalls.Distinct().Count()).Append(" |\n");
            }
            sb.Append('\n');

            var byName = blocks.GroupBy(b => b.Document.BlockName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var calledBy = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in blocks)
                foreach (var call in b.Document.BlockCalls.Distinct())
                {
                    var target = call.Contains(':') ? call.Substring(call.IndexOf(':') + 1) : call;
                    if (target.Contains('.')) target = target.Substring(target.LastIndexOf('.') + 1);
                    if (!calledBy.TryGetValue(target, out var set)) calledBy[target] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(b.Document.BlockName);
                }
            if (calledBy.Count > 0)
            {
                sb.Append("## Call cross-reference\n\n| Called block | In program export | Callers |\n|---|---|---|\n");
                foreach (var kv in calledBy)
                    sb.Append("| `").Append(kv.Key).Append("` | ").Append(byName.ContainsKey(kv.Key) ? "yes" : "no (library/system/other export)").Append(" | ").Append(string.Join(", ", kv.Value.Select(c => "`" + c + "`"))).Append(" |\n");
                sb.Append('\n');
            }
            foreach (var entry in hb.Entries.Where(e => e.Block == null))
                sb.Append("- Failed: `").Append(entry.File).Append("` — ").Append(entry.Error).Append('\n');
            if (hb.Failed > 0) sb.Append('\n');

            foreach (var b in blocks)
            {
                sb.Append("---\n\n");
                // Demote headings of the single-block render by one level.
                foreach (var line in b.Markdown.Split('\n'))
                    sb.Append(line.StartsWith("#", StringComparison.Ordinal) ? "#" + line : line).Append('\n');
                sb.Append("Source file: `").Append(b.Document.SourcePath).Append("`\n\n");
            }
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        public static string Heading(OfflineAnalysisLogic.BlockDocument doc)
        {
            var number = doc.Attributes.TryGetValue("Number", out var num) ? " " + num : "";
            var name = doc.BlockName.Length > 0 ? doc.BlockName : Path.GetFileNameWithoutExtension(doc.SourcePath);
            return doc.BlockType + number + " `" + name + "`" + (doc.Language.Length > 0 ? " (" + doc.Language + ")" : "");
        }

        private static int TypeRank(string type) => type == "OB" ? 0 : type == "FB" ? 1 : type == "FC" ? 2 : type == "GlobalDB" ? 3 : type == "InstanceDB" ? 4 : type.StartsWith("Plc", StringComparison.Ordinal) ? 5 : 6;

        private static string Anchor(string heading)
        {
            // GitHub-style slug of the block heading: lower-case, punctuation dropped, spaces to hyphens.
            var s = Regex.Replace(heading.ToLowerInvariant(), @"[^\w\- ]", "").Replace(' ', '-');
            return s;
        }

        // ------------------------------------------------------------------ SCL lint

        private static readonly Dictionary<string, string> BlockOpeners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["IF"] = "END_IF", ["CASE"] = "END_CASE", ["FOR"] = "END_FOR", ["WHILE"] = "END_WHILE", ["REPEAT"] = "END_REPEAT",
            ["REGION"] = "END_REGION", ["FUNCTION"] = "END_FUNCTION", ["FUNCTION_BLOCK"] = "END_FUNCTION_BLOCK",
            ["ORGANIZATION_BLOCK"] = "END_ORGANIZATION_BLOCK", ["DATA_BLOCK"] = "END_DATA_BLOCK", ["TYPE"] = "END_TYPE", ["STRUCT"] = "END_STRUCT",
            ["VAR"] = "END_VAR", ["VAR_INPUT"] = "END_VAR", ["VAR_OUTPUT"] = "END_VAR", ["VAR_IN_OUT"] = "END_VAR", ["VAR_TEMP"] = "END_VAR", ["VAR_STAT"] = "END_VAR",
        };
        private static readonly HashSet<string> Closers = new HashSet<string>(BlockOpeners.Values, StringComparer.OrdinalIgnoreCase);
        private static readonly Regex WordRegex = new Regex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
        private static readonly Regex AssignmentSuspect = new Regex(@"^\s*[#""]?[A-Za-z_][\w\.""#\[\]]*\s*=(?!=)", RegexOptions.Compiled);
        private static readonly string[] StatementKeywords = { "IF", "ELSIF", "WHILE", "UNTIL", "CASE", "FOR", "RETURN", "EXIT", "CONTINUE", "ELSE", "THEN", "DO", "OF", "REPEAT", "REGION", "GOTO", "BEGIN" };
        private static readonly HashSet<string> LineEndingKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "THEN", "DO", "ELSE", "OF", "BEGIN", "REPEAT", "STRUCT", "VAR", "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT", "VAR_TEMP", "VAR_STAT", "CONSTANT", "RETAIN", "NON_RETAIN" };

        public static LintOptions ParseLintOptions(string? rulesJson)
        {
            var o = new LintOptions();
            if (string.IsNullOrWhiteSpace(rulesJson)) return o;
            JsonNode? node;
            try { node = JsonNode.Parse(rulesJson!); }
            catch (JsonException ex) { throw new ArgumentException("rulesJson is not valid JSON: " + ex.Message); }
            if (!(node is JsonObject obj)) throw new ArgumentException("rulesJson must be a JSON object, e.g. {\"disable\":[\"SCL007\"],\"maxLineLength\":120,\"maxNesting\":5}.");
            if (obj["disable"] is JsonArray dis) foreach (var d in dis) if (d != null) o.Disabled.Add(d.ToString());
            if (obj["maxLineLength"] != null) o.MaxLineLength = obj["maxLineLength"]!.GetValue<int>();
            if (obj["maxNesting"] != null) o.MaxNesting = obj["maxNesting"]!.GetValue<int>();
            if (obj["markers"] is JsonArray mk) o.Markers = mk.Select(m => m?.ToString() ?? "").Where(m => m.Length > 0).ToArray();
            if (o.MaxLineLength < 40 || o.MaxLineLength > 1000) throw new ArgumentException("maxLineLength must be between 40 and 1000.");
            if (o.MaxNesting < 1 || o.MaxNesting > 50) throw new ArgumentException("maxNesting must be between 1 and 50.");
            return o;
        }

        public static List<LintFinding> LintScl(string source, LintOptions? options = null)
        {
            options ??= new LintOptions();
            var findings = new List<LintFinding>();
            var lines = (source ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            void Add(string rule, string severity, int line, string message, string text)
            {
                if (options.Disabled.Contains(rule)) return;
                findings.Add(new LintFinding { Rule = rule, Severity = severity, Line = line, Message = message, Text = text.Length > 200 ? text.Substring(0, 200) : text });
            }

            var stack = new Stack<(string keyword, int line)>();
            var inBlockComment = false;
            var paren = 0; var bracket = 0; var parenLine = 0;
            var nesting = 0; var reportedNesting = false;
            string? previousCode = null; var previousLine = 0;
            var whitespaceLines = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var lineNo = i + 1;
                if (raw.Length > options.MaxLineLength) Add("SCL007", "info", lineNo, "Line longer than " + options.MaxLineLength + " characters (" + raw.Length + ").", raw);
                if (raw.Contains('\t') || raw.Length > 0 && char.IsWhiteSpace(raw[raw.Length - 1])) whitespaceLines++;

                var stripped = OfflineAnalysisLogic.StripComments(raw, ref inBlockComment, out var comment);
                if (comment.Length > 0)
                    foreach (var marker in options.Markers)
                        if (Regex.IsMatch(comment, @"\b" + Regex.Escape(marker) + @"\b")) { Add("SCL010", "info", lineNo, "Annotation marker '" + marker + "' in comment.", comment.Trim()); break; }
                var code = StripStrings(stripped);
                if (code.Trim().Length == 0) continue;

                foreach (var ch in code)
                {
                    if (ch == '(') { if (paren == 0) parenLine = lineNo; paren++; }
                    else if (ch == ')') { paren--; if (paren < 0) { Add("SCL002", "error", lineNo, "Closing ')' without opening parenthesis.", raw.Trim()); paren = 0; } }
                    else if (ch == '[') bracket++;
                    else if (ch == ']') { bracket--; if (bracket < 0) { Add("SCL002", "error", lineNo, "Closing ']' without opening bracket.", raw.Trim()); bracket = 0; } }
                }
                if (code.Contains(";;")) Add("SCL009", "info", lineNo, "Empty statement ';;'.", raw.Trim());

                var words = WordRegex.Matches(code).Cast<Match>().Select(m => m.Value).ToList();
                var firstWord = words.Count > 0 ? words[0].ToUpperInvariant() : "";
                foreach (var w in words)
                {
                    var upper = w.ToUpperInvariant();
                    if (upper == "GOTO") Add("SCL005", "warning", lineNo, "GOTO makes control flow hard to review; prefer structured statements.", raw.Trim());
                    if (BlockOpeners.TryGetValue(upper, out var closer))
                    {
                        // "VAR CONSTANT" / "VAR RETAIN" is still one VAR block; "TYPE" only opens at statement start.
                        if (upper == "TYPE" && firstWord != "TYPE") continue;
                        stack.Push((upper, lineNo));
                        if (upper == "IF" || upper == "CASE" || upper == "FOR" || upper == "WHILE" || upper == "REPEAT")
                        {
                            nesting++;
                            if (nesting > options.MaxNesting && !reportedNesting) { reportedNesting = true; Add("SCL006", "info", lineNo, "Nesting depth " + nesting + " exceeds " + options.MaxNesting + ".", raw.Trim()); }
                        }
                        continue;
                    }
                    if (Closers.Contains(upper))
                    {
                        if (stack.Count == 0) { Add("SCL001", "error", lineNo, upper + " without a matching opener.", raw.Trim()); continue; }
                        var top = stack.Peek();
                        var expected = BlockOpeners[top.keyword];
                        if (!expected.Equals(upper, StringComparison.OrdinalIgnoreCase))
                        {
                            Add("SCL001", "error", lineNo, upper + " closes " + top.keyword + " opened at line " + top.line + " (expected " + expected + ").", raw.Trim());
                            // Recover: unwind to the nearest matching opener if any.
                            var unwound = stack.ToList();
                            var idx = unwound.FindIndex(s => BlockOpeners[s.keyword].Equals(upper, StringComparison.OrdinalIgnoreCase));
                            if (idx >= 0) for (var k = 0; k <= idx; k++) { var popped = stack.Pop(); if (IsNestingKeyword(popped.keyword)) nesting--; }
                            continue;
                        }
                        stack.Pop();
                        if (IsNestingKeyword(top.keyword)) nesting--;
                    }
                }

                if (firstWord == "WHILE" && Regex.IsMatch(code, @"\bWHILE\s+TRUE\b", RegexOptions.IgnoreCase)) Add("SCL011", "warning", lineNo, "WHILE TRUE loop; make sure an EXIT/RETURN condition exists (cycle time!).", raw.Trim());
                if (firstWord == "REGION" && words.Count == 1 && code.Trim().Equals("REGION", StringComparison.OrdinalIgnoreCase)) Add("SCL012", "info", lineNo, "REGION without a name.", raw.Trim());

                if (AssignmentSuspect.IsMatch(code) && !StatementKeywords.Contains(firstWord) && paren == 0 && !code.Contains(":=") && !Regex.IsMatch(code, @"[<>]=|<>|=>"))
                    Add("SCL003", "warning", lineNo, "'=' at statement level; SCL assignment is ':=' (comparison '=' is only valid inside an expression).", raw.Trim());

                var trimmed = code.Trim();
                var startsCloser = firstWord == "ELSE" || firstWord == "ELSIF" || firstWord == "UNTIL" || firstWord.StartsWith("END_", StringComparison.Ordinal);
                if (previousCode != null && startsCloser && !previousCode.EndsWith(";", StringComparison.Ordinal))
                {
                    var prevWords = WordRegex.Matches(previousCode).Cast<Match>().Select(m => m.Value.ToUpperInvariant()).ToList();
                    var prevLast = prevWords.Count > 0 ? prevWords[prevWords.Count - 1] : "";
                    var prevFirst = prevWords.Count > 0 ? prevWords[0] : "";
                    var prevIsHeader = LineEndingKeywords.Contains(prevLast) || prevFirst.StartsWith("END_", StringComparison.Ordinal) || prevFirst == "ELSE" || prevFirst == "REGION" || prevFirst == "CASE" || Regex.IsMatch(previousCode.Trim(), @"^[\w#""'\.\[\]\s,]+:$");
                    if (!prevIsHeader && !previousCode.Trim().EndsWith(":", StringComparison.Ordinal))
                        Add("SCL004", "warning", previousLine, "Statement before " + firstWord + " does not end with ';'.", lines[previousLine - 1].Trim());
                }
                previousCode = trimmed; previousLine = lineNo;
            }
            foreach (var open in stack.Reverse())
                Add("SCL001", "error", open.line, open.keyword + " opened here is never closed (missing " + BlockOpeners[open.keyword] + ").", lines[open.line - 1].Trim());
            if (paren > 0) Add("SCL002", "error", parenLine, paren + " '(' never closed (first unclosed at line " + parenLine + ").", lines[Math.Max(0, parenLine - 1)].Trim());
            if (bracket > 0) Add("SCL002", "error", lines.Length, bracket + " '[' never closed.", "");
            if (whitespaceLines > 0) Add("SCL008", "info", 0, whitespaceLines + " line(s) with tab characters or trailing whitespace.", "");
            if (inBlockComment) Add("SCL013", "error", lines.Length, "Block comment '(*' is never closed.", "");
            return findings.OrderBy(f => f.Line).ThenBy(f => f.Rule, StringComparer.Ordinal).ToList();
        }

        private static bool IsNestingKeyword(string keyword) => keyword == "IF" || keyword == "CASE" || keyword == "FOR" || keyword == "WHILE" || keyword == "REPEAT";

        // Replaces the contents of '...' string literals (and "..." symbol names) so quotes/keywords inside do not count.
        public static string StripStrings(string code)
        {
            var sb = new StringBuilder(code.Length);
            char? quote = null;
            foreach (var ch in code)
            {
                if (quote == null)
                {
                    if (ch == '\'' || ch == '"') { quote = ch; sb.Append(ch); continue; }
                    sb.Append(ch);
                }
                else
                {
                    if (ch == quote) { quote = null; sb.Append(ch); continue; }
                    sb.Append(ch == '"' ? 'x' : ch == '\'' ? 'x' : char.IsLetterOrDigit(ch) ? 'x' : ch == '(' || ch == ')' || ch == '[' || ch == ']' || ch == ';' || ch == '=' ? 'x' : ch);
                }
            }
            return sb.ToString();
        }

        public static JsonObject FindingJson(LintFinding f) => new JsonObject
        {
            ["rule"] = f.Rule, ["severity"] = f.Severity, ["line"] = f.Line, ["message"] = f.Message, ["text"] = f.Text
        };

        public static JsonObject RuleCatalog() => new JsonObject
        {
            ["SCL001"] = "error: block keyword pairing (IF/END_IF, CASE, FOR, WHILE, REPEAT, REGION, FUNCTION..., VAR/END_VAR, STRUCT, TYPE)",
            ["SCL002"] = "error: unbalanced parentheses/brackets",
            ["SCL003"] = "warning: '=' at statement level where ':=' is expected",
            ["SCL004"] = "warning: statement before ELSE/ELSIF/UNTIL/END_* without ';'",
            ["SCL005"] = "warning: GOTO used",
            ["SCL006"] = "info: nesting depth above maxNesting",
            ["SCL007"] = "info: line longer than maxLineLength",
            ["SCL008"] = "info: tabs or trailing whitespace (aggregated)",
            ["SCL009"] = "info: empty statement ';;'",
            ["SCL010"] = "info: annotation marker (TODO/FIXME/...) in a comment",
            ["SCL011"] = "warning: WHILE TRUE loop",
            ["SCL012"] = "info: REGION without a name",
            ["SCL013"] = "error: unterminated block comment"
        };
    }
}
