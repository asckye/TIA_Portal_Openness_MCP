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
    public static partial class PlcDocumentationLogic
    {
        public const int MaxNetworksPerBlock = 2000;
        public const int MaxHandbookFiles = 2000;

        // Pin names that carry a value OUT of a part; everything else is treated as an input pin.
        private static readonly HashSet<string> OutputPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "out", "out1", "out2", "Q", "Q1", "Q2", "ENO", "ET", "CV", "QU", "QD", "RET_VAL", "Ret_Val", "RET", "ERROR", "STATUS", "BUSY", "DONE", "VALID",
                "NDR", "ERR", "OUT", "OUTPUT", "ENDCONDITION", "LEN", "RLO"
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
            if (doc.Networks.Count > MaxNetworksPerBlock)
                throw new InvalidDataException("Block has " + doc.Networks.Count + " networks; limit is " + MaxNetworksPerBlock + ".");
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
                        var template = e.Elements().Where(x => x.Name.LocalName == "TemplateValue").Select(x => x.Value.Trim()).Where(v => v.Length > 0)
                            .ToList();
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
                        node = new GraphNode {
                            Id = "P" + uid, Kind = "part", Label = label, Name = callName.Length > 0 ? callName : name, Instance = instanceName };
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
                        if (IsOutputPin(partPin.pin, partPin.node.Name)) g.Edges
                            .Add(new GraphEdge { From = partPin.node.Id, To = source.node.Id, FromPin = partPin.pin });
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
            => pin.Length > 0
                && (OutputPins.Contains(pin) || pin.StartsWith("OUT", StringComparison.OrdinalIgnoreCase) || pin.StartsWith("Q", StringComparison.Ordinal)
                    && pin.Length <= 3
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
                if (scope == "GlobalVariable" || scope == "GlobalConstant") return "\"" + comps[0] + "\""
                    + (comps.Count > 1 ? "." + string.Join(".", comps.Skip(1)) : "");
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
                var prefix = area == "Input" ? "I" : area == "Output" ? "Q" : area == "Memory" ? "M" : area == "PeripheryInput" ? "I"
                    : area == "PeripheryOutput" ? "Q" : area;
                if (int.TryParse(bitOffset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits))
                {
                    var size = type == "Bool" ? "" : type == "Byte" ? "B" : type == "Word" || type == "Int" ? "W" : type == "DWord" || type == "DInt"
                        || type == "Real" ? "D" : "";
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
                foreach (var m in doc.Members) sb.Append("| ").Append(m.Section).Append(" | `").Append(m.Path).Append("` | ")
                    .Append(m.Datatype.Replace("|", "\\|")).Append(" |\n");
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
                        if (net.Graph.OpenConnections > 0) sb.Append("_").Append(net.Graph.OpenConnections)
                            .Append(" open connection(s) (unused output pins)._\n\n");
                    }
                    else if (net.SourceLines.Count > 0)
                    {
                        var lang = string.Equals(net.Language, "SCL", StringComparison.OrdinalIgnoreCase) || string
                            .Equals(doc.Language, "SCL", StringComparison.OrdinalIgnoreCase) ? "scl" : "text";
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
            var instr = doc.Instructions.GroupBy(i => i, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();
            if (instr.Count > 0) sb.Append("## Instructions\n\n").Append(string.Join(", ", instr.Select(g => "`" + g.Key + "`×" + g.Count()))).Append("\n\n");
            if (doc.Warnings.Count > 0) sb.Append("## Parser notes\n\n").Append(string.Join("\n", doc.Warnings.Select(w => "- " + w))).Append("\n\n");
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        // ------------------------------------------------------------------ SCL lint

        private static readonly Dictionary<string, string> BlockOpeners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["IF"] = "END_IF", ["CASE"] = "END_CASE", ["FOR"] = "END_FOR", ["WHILE"] = "END_WHILE", ["REPEAT"] = "END_REPEAT",
            ["REGION"] = "END_REGION", ["FUNCTION"] = "END_FUNCTION", ["FUNCTION_BLOCK"] = "END_FUNCTION_BLOCK",
            ["ORGANIZATION_BLOCK"] = "END_ORGANIZATION_BLOCK", ["DATA_BLOCK"] = "END_DATA_BLOCK", ["TYPE"] = "END_TYPE", ["STRUCT"] = "END_STRUCT",
            ["VAR"] = "END_VAR", ["VAR_INPUT"] = "END_VAR", ["VAR_OUTPUT"] = "END_VAR", ["VAR_IN_OUT"] = "END_VAR", ["VAR_TEMP"] = "END_VAR", ["VAR_STAT"] =
                "END_VAR",
        };
        private static readonly HashSet<string> Closers = new HashSet<string>(BlockOpeners.Values, StringComparer.OrdinalIgnoreCase);
        private static readonly Regex WordRegex = new Regex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
        private static readonly Regex AssignmentSuspect = new Regex(@"^\s*[#""]?[A-Za-z_][\w\.""#\[\]]*\s*=(?!=)", RegexOptions.Compiled);
        private static readonly string[] StatementKeywords = {
            "IF", "ELSIF", "WHILE", "UNTIL", "CASE", "FOR", "RETURN", "EXIT", "CONTINUE", "ELSE", "THEN", "DO", "OF", "REPEAT", "REGION", "GOTO", "BEGIN" };
        private static readonly HashSet<string> LineEndingKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "THEN", "DO", "ELSE", "OF", "BEGIN", "REPEAT", "STRUCT", "VAR", "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT", "VAR_TEMP", "VAR_STAT", "CONSTANT",
                "RETAIN", "NON_RETAIN"
        };
    }
}
