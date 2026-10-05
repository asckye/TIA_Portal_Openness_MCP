using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using TiaGitAddIn.Models.Lad;
using TiaGitAddIn.Models.Sact;
using TiaGitAddIn.Services;
using TiaGitAddIn.Services.SimaticMl;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static partial class PlcLadderDrawing
    {
        private sealed class DiagramPin
        {
            internal string Name = "", Label = "", Value = "", Type = "";
            internal bool Output, Connected;
            internal int Y;
        }

        private sealed class DiagramNode
        {
            internal XElement Xml = null!;
            internal string Id = "", Name = "", Title = "", Symbol = "", Operand = "", LowerOperand = "", Instance = "", Comment = "", Finding = "", Preset = "";
            internal List<DiagramPin> Pins = new List<DiagramPin>();
            internal int Column, Row, X, Y, Left, Right, Top, Bottom, BoxWidth, Pitch, Above, Below, CellWidth;
            internal bool IsBox => Symbol.Length == 0;
            internal bool IsCall => Xml.Name.LocalName == "Call";
            internal int SymbolHalfWidth => Symbol == "compare" ? 42 : Symbol == "not" ? 36 : 30;
            internal IEnumerable<DiagramPin> Side(bool output) => Pins.Where(p => p.Output == output);
        }

        private static readonly HashSet<string> BoxInstructions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Move", "Add", "Sub", "Mul", "Div", "Mod", "Inc", "Dec", "Neg", "Abs", "Calculate", "Convert", "Conv",
            "And", "Xor", "Invert", "Shl", "Shr", "Rol", "Ror", "Min", "Max", "Limit", "Sel", "Mux", "Demux",
            "Sqr", "Sqrt", "Exp", "Ln", "Sin", "Cos", "Tan", "Asin", "Acos", "Atan", "Trunc", "Round", "Ceil", "Floor",
            "Scale_X", "Norm_X", "TON", "TOF", "TP", "TONR", "CTU", "CTD", "CTUD", "PBox", "NBox"
        };

        internal static string NetworkSvg(NetworkSourceDefinition network, Func<string, string?> callLink, ISet<string>? unknownParts = null)
        {
            var xml = XElement.Parse(network.RawXml!);
            var wires = xml.Descendants().Where(e => e.Name.LocalName == "Wire").ToArray();
            var components = SimaticMlToSactMapper.MapNetworkBody(network);
            var routingIds = new HashSet<string>(xml.Descendants().Where(e => IsRoutingPart(e, wires)).Select(e => (string)e.Attribute("UId")!), StringComparer.Ordinal);
            var joinedWires = JoinRoutingWires(routingIds, wires).ToArray();
            var accesses = xml.Descendants().Where(e => e.Name.LocalName == "Access" && e.Attribute("UId") != null)
                .GroupBy(e => (string)e.Attribute("UId")!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => LadTextRenderer.Operand(g.First()), StringComparer.Ordinal);
            var nodes = new List<DiagramNode>();
            foreach (var element in xml.Descendants().Where(e => e.Name.LocalName == "Part" || e.Name.LocalName == "Call"))
            {
                string id = (string?)element.Attribute("UId") ?? throw new ArgumentException("Instruction UId is missing.");
                if (routingIds.Contains(id)) continue;
                components.TryGetValue(id, out var component);
                var call = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "CallInfo");
                var instance = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "Instance");
                var node = new DiagramNode { Xml = element, Id = id, Name = (string?)call?.Attribute("Name") ?? (string?)element.Attribute("Name") ?? "?",
                    Comment = component?.Comment ?? "",
                    Instance = instance == null ? "" : LadTextRenderer.Operand(instance).text };
                node.Symbol = SymbolKind(element);
                node.Title = node.Name == "PBox" ? "P_TRIG" : node.Name == "NBox" ? "N_TRIG"
                    : !node.IsCall && BoxInstructions.Contains(node.Name) ? node.Name.ToUpperInvariant() : node.Name;
                node.Preset = string.Join(" → ", element.Descendants().Where(e => e.Name.LocalName == "TemplateValue" && (string?)e.Attribute("Type") == "Type").Select(e => e.Value).Distinct());
                if (!node.IsCall && node.IsBox && !BoxInstructions.Contains(node.Name)) unknownParts?.Add(node.Name);
                var parameters = element.Descendants().Where(e => e.Name.LocalName == "Parameter").ToArray();
                var pinNames = DefaultPins(node).Concat(parameters.Select(e => (string?)e.Attribute("Name") ?? ""))
                    .Concat(wires.SelectMany(w => w.Elements().Where(e => e.Name.LocalName == "NameCon" && (string?)e.Attribute("UId") == id)
                        .Select(e => (string?)e.Attribute("Name") ?? ""))).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (string name in pinNames)
                {
                    var declaration = parameters.FirstOrDefault(p => string.Equals((string?)p.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase));
                    string section = (string?)declaration?.Attribute("Section") ?? "";
                    var pin = new DiagramPin { Name = name, Output = section == "Output" || section == "Return"
                        || (section.Length == 0 && IsOutputPin(name)), Type = (string?)declaration?.Attribute("Type") ?? "" };
                    pin.Label = name.Equals("pre", StringComparison.OrdinalIgnoreCase) ? "" : !node.IsCall || name.Equals("en", StringComparison.OrdinalIgnoreCase) || name.Equals("eno", StringComparison.OrdinalIgnoreCase) ? name.ToUpperInvariant() : name;
                    if (node.Name == "PBox" || node.Name == "NBox") pin.Label = name.Equals("in", StringComparison.OrdinalIgnoreCase) ? "CLK" : name.Equals("out", StringComparison.OrdinalIgnoreCase) ? "Q" : pin.Label;
                    foreach (var wire in wires.Where(w => w.Elements().Any(e => e.Name.LocalName == "NameCon" && (string?)e.Attribute("UId") == id && string.Equals((string?)e.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase))))
                    {
                        pin.Connected |= wire.Elements().Any(e => e.Name.LocalName == "Powerrail" || e.Name.LocalName == "NameCon" && (string?)e.Attribute("UId") != id);
                        var access = wire.Elements().FirstOrDefault(e => e.Name.LocalName == "IdentCon");
                        if (access == null || !accesses.TryGetValue((string?)access.Attribute("UId") ?? "", out var value)) continue;
                        pin.Value = value.text;
                        if (name.Equals("operand", StringComparison.OrdinalIgnoreCase))
                        {
                            node.Operand = value.text;
                            if (node.Symbol.StartsWith("contact-", StringComparison.Ordinal) && value.literal)
                            {
                                bool truth = value.text == "1" || value.text.Equals("true", StringComparison.OrdinalIgnoreCase);
                                bool falsity = value.text == "0" || value.text.Equals("false", StringComparison.OrdinalIgnoreCase);
                                node.Finding = truth || falsity ? ((truth != (node.Symbol == "contact-nc")) ? "Finding: constant closed contact" : "Finding: constant open contact") : "Finding: constant contact";
                            }
                        }
                    }
                    if (node.Symbol == "compare" && name.Equals("in1", StringComparison.OrdinalIgnoreCase)) node.Operand = pin.Value;
                    if (node.Symbol == "compare" && name.Equals("in2", StringComparison.OrdinalIgnoreCase)) node.LowerOperand = pin.Value;
                    bool edge = node.Symbol.EndsWith("positive", StringComparison.Ordinal) || node.Symbol.EndsWith("negative", StringComparison.Ordinal) || node.Name == "PBox" || node.Name == "NBox";
                    if (edge && name.Equals("bit", StringComparison.OrdinalIgnoreCase)) node.LowerOperand = pin.Value;
                    else if (!name.Equals("operand", StringComparison.OrdinalIgnoreCase)) node.Pins.Add(pin);
                }
                // A pre connector carries rung flow, not a visible parameter. Route it through EN or the Boolean entry.
                var pre = node.Pins.FirstOrDefault(p => p.Name.Equals("pre", StringComparison.OrdinalIgnoreCase));
                var entry = EntryPin(node);
                if (pre != null && entry != null)
                {
                    entry.Connected |= pre.Connected;
                    if (!node.IsCall && !BoxInstructions.Contains(node.Name) && !entry.Name.Equals("en", StringComparison.OrdinalIgnoreCase)) entry.Label = "";
                    node.Pins.RemoveAll(p => p.Name.Equals("pre", StringComparison.OrdinalIgnoreCase));
                }
                // Actual XML names/sections take precedence over the comparison layout's generic IN/OUT labels.
                node.Pins = node.Pins.OrderBy(p => p.Name.Equals("en", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("eno", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
                foreach (var pin in node.Pins.Where(p => p.Output && !p.Connected && p.Value.Length == 0)) pin.Value = "...";
                Measure(node);
                nodes.Add(node);
            }
            // Contract pure routing before applying the shared layout, so routing names cannot become false roots.
            var layout = NetworkLayout(nodes, joinedWires);
            var positions = layout.Elements.ToDictionary(e => e.UId, StringComparer.Ordinal);
            int nextRow = layout.Elements.Select(e => e.Row).DefaultIfEmpty(-1).Max() + 1;
            foreach (var node in nodes)
            {
                positions.TryGetValue(node.Id, out var position);
                node.Column = position?.Column ?? 1;
                node.Row = position?.Row ?? nextRow++;
            }
            var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            int width = 52;
            foreach (var column in nodes.GroupBy(n => n.Column).OrderBy(g => g.Key))
            {
                int cellWidth = column.Max(n => n.CellWidth);
                foreach (var node in column) node.X = width + cellWidth / 2;
                width += cellWidth;
            }
            int height = 22;
            foreach (var row in nodes.GroupBy(n => n.Row).OrderBy(g => g.Key))
            {
                int above = row.Max(n => n.Above), below = row.Max(n => n.Below);
                foreach (var node in row)
                {
                    node.Y = height + above;
                    node.Left = node.X - (node.IsBox ? node.BoxWidth / 2 : node.SymbolHalfWidth);
                    node.Right = node.X + (node.IsBox ? node.BoxWidth / 2 : node.SymbolHalfWidth);
                    node.Top = node.Y - HeaderHeight(node) - 16;
                    node.Bottom = node.Y + (Math.Max(node.Side(false).Count(), node.Side(true).Count()) - 1) * node.Pitch + 18;
                    foreach (bool output in new[] { false, true })
                    {
                        int index = 0;
                        foreach (var pin in node.Side(output)) pin.Y = node.Y + index++ * node.Pitch;
                    }
                }
                height += above + below + 28;
            }
            width = Math.Max(640, width + 28); height = Math.Max(120, height + 12);
            var b = new StringBuilder();
            b.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" aria-label=\"Static ladder network\" width=\"").Append(width)
                .Append("\" height=\"").Append(height).Append("\" viewBox=\"0 0 ").Append(width).Append(' ').Append(height).Append("\">");
            Path(b, "power-rail", "M 24 16 V " + (height - 16));
            foreach (var wire in joinedWires) DrawWire(b, wire, byId);
            foreach (var node in nodes)
            {
                string? target = node.Xml.Name.LocalName == "Call" ? callLink(node.Name) : null;
                if (target != null) b.Append("<a href=\"").Append(E(target)).Append("\">");
                b.Append("<g data-uid=\"").Append(E(node.Id)).Append("\" class=\"").Append(node.IsBox ? "instruction" : node.Symbol).Append("\"><title>")
                    .Append(E(string.Join("\n", new[] { node.Name, node.Operand, node.Comment, node.Finding }.Where(s => s.Length > 0)))).Append("</title>");
                if (node.IsBox) DrawBox(b, node);
                else DrawSymbol(b, node);
                b.Append("</g>");
                if (target != null) b.Append("</a>");
            }
            return b.Append("</svg>").ToString();
        }

        private static int HeaderHeight(DiagramNode node) => 28 + (Wrap(node.Title, 22).Count() - 1) * 16 + Wrap(node.Preset, 22).Count() * 16;

        private static void Measure(DiagramNode node)
        {
            node.BoxWidth = Math.Max(132, Math.Min(200, Math.Max(node.Title.Length, node.Pins.Select(p => p.Label.Length * 2).DefaultIfEmpty(0).Max()) * 7 + 28));
            int operandLines = Wrap(node.Operand, 22).Count(), commentLines = Wrap(node.Comment, 24).Count();
            int instanceLines = Wrap(node.Instance, 24).Count();
            node.Pitch = Math.Max(28, node.Pins.Select(p => Wrap(p.Value, 18).Count() * 15 + 8).DefaultIfEmpty(28).Max());
            int lowerLines = Wrap(node.LowerOperand, 22).Count();
            node.Above = node.IsBox ? HeaderHeight(node) + 28 + instanceLines * 16 : Math.Max(42, operandLines * 16 + (node.Symbol == "compare" ? 36 : 22));
            node.Below = node.IsBox ? Math.Max(0, Math.Max(node.Side(false).Count(), node.Side(true).Count()) - 1) * node.Pitch + 30 + (commentLines + lowerLines) * 16
                : Math.Max(34, (commentLines + lowerLines) * 16 + (node.Symbol == "compare" ? 46 : 26));
            int left = node.Side(false).Select(p => Math.Min(18, p.Value.Length) * 7 + 22).DefaultIfEmpty(28).Max();
            int right = node.Side(true).Select(p => Math.Min(18, p.Value.Length) * 7 + 22).DefaultIfEmpty(28).Max();
            node.CellWidth = node.IsBox ? node.BoxWidth + 2 * Math.Max(left, right) + 24
                : Math.Max(140, Math.Min(24, Math.Max(node.LowerOperand.Length, Math.Max(node.Operand.Length, node.Comment.Length))) * 7 + 24);
        }

        private static string SymbolKind(XElement node)
        {
            if (node.Name.LocalName == "Call") return "";
            bool negated = node.Elements().Any(e => e.Name.LocalName == "Negated" && ((string?)e.Attribute("Name") == "operand" || e.Attribute("Name") == null));
            return (string?)node.Attribute("Name") switch
            {
                "Contact" => negated ? "contact-nc" : "contact-no",
                "PContact" or "Contact_P" => "contact-positive", "NContact" or "Contact_N" => "contact-negative",
                "Coil" => negated ? "coil-negated" : "coil",
                "PCoil" => "coil-positive", "NCoil" => "coil-negative",
                "SCoil" or "SetCoil" => "coil-set", "RCoil" or "ResetCoil" => "coil-reset",
                "Not" => "not", "Eq" or "Ne" or "Gt" or "Ge" or "Lt" or "Le" => "compare", _ => ""
            };
        }

        private static bool IsRoutingPart(XElement element, XElement[] wires)
        {
            if (element.Name.LocalName != "Part") return false;
            string name = (string?)element.Attribute("Name") ?? "";
            if (name is "O" or "Or" or "Wire" or "BranchWire") return true;
            if (SymbolKind(element).Length > 0 || BoxInstructions.Contains(name)) return false;
            // Unknown instructions with operands, parameters or type/instance metadata must remain visible boxes.
            if (element.Elements().Any(e => e.Name.LocalName != "TemplateValue" || (string?)e.Attribute("Type") != "Cardinality")) return false;
            string? id = (string?)element.Attribute("UId");
            var connected = wires.Where(w => w.Elements().Any(e => e.Name.LocalName == "NameCon" && (string?)e.Attribute("UId") == id)).ToArray();
            if (connected.Any(w => w.Elements().Any(e => e.Name.LocalName == "IdentCon"))) return false;
            var pins = connected.SelectMany(w => w.Elements().Where(e => e.Name.LocalName == "NameCon" && (string?)e.Attribute("UId") == id))
                .Select(e => ((string?)e.Attribute("Name") ?? "").ToLowerInvariant()).ToArray();
            bool Port(string pin, string prefix) => pin.StartsWith(prefix, StringComparison.Ordinal) && pin.Substring(prefix.Length).All(char.IsDigit);
            return pins.Any(p => Port(p, "in")) && pins.Any(p => Port(p, "out")) && pins.All(p => Port(p, "in") || Port(p, "out"));
        }

        private static IEnumerable<XElement> JoinRoutingWires(IEnumerable<string> routingIds, IEnumerable<XElement> wires)
        {
            var joined = wires.ToList();
            foreach (string id in routingIds)
            {
                var connected = joined.Where(w => w.Elements().Any(e => e.Name.LocalName == "NameCon" && (string?)e.Attribute("UId") == id)).ToArray();
                if (connected.Length == 0) continue;
                var connections = connected.SelectMany(w => w.Elements()).Where(e => e.Name.LocalName != "NameCon" || (string?)e.Attribute("UId") != id)
                    .GroupBy(e => e.Name.LocalName + ":" + (string?)e.Attribute("UId") + ":" + (string?)e.Attribute("Name"))
                    .Select(g => new XElement(g.First()));
                var merged = new XElement(connected[0].Name, connections);
                int index = joined.IndexOf(connected[0]);
                joined.RemoveAll(w => connected.Contains(w));
                joined.Insert(index, merged);
            }
            return joined;
        }

        private static LadNetworkLayout NetworkLayout(List<DiagramNode> nodes, XElement[] wires)
        {
            var graph = new Dictionary<string, SactComponentData>(StringComparer.Ordinal);
            if (wires.Any(w => w.Elements().Any(e => e.Name.LocalName == "Powerrail")))
                graph.Add("Powerrail", new SactComponentData { UId = "Powerrail", Name = "BranchWireData", DisplayName = "Powerrail", IsStartElement = true });
            var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            foreach (var node in nodes)
                graph.Add(node.Id, new SactComponentData { UId = node.Id, Name = "LadBoxData", DisplayName = node.Name, Comment = node.Comment });
            int edge = 0;
            foreach (var wire in wires)
            {
                if (wire.Elements().Any(e => e.Name.LocalName == "IdentCon")) continue;
                var ends = new List<(string id, string pin, bool output)>();
                foreach (var connection in wire.Elements())
                {
                    if (connection.Name.LocalName == "Powerrail") ends.Add(("Powerrail", "", true));
                    else if (connection.Name.LocalName == "NameCon" && byId.TryGetValue((string?)connection.Attribute("UId") ?? "", out var node))
                    {
                        string name = (string?)connection.Attribute("Name") ?? "";
                        var pin = node.Pins.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        ends.Add((node.Id, name, pin?.Output ?? IsOutputPin(name)));
                    }
                }
                // A collapsed merge can have several sources; each contributes an edge to every downstream input.
                foreach (var source in ends.Where(e => e.output))
                    foreach (var target in ends.Where(e => !e.output && e.id != source.id))
                    {
                        string output = "render-" + edge + "-out", input = "render-" + edge++ + "-in";
                        graph[source.id].OutputConnectors.Add(new SactConnectorData { UId = output, PinName = source.pin, PartnerUId = input });
                        graph[target.id].InputConnectors.Add(new SactConnectorData { UId = input, PinName = target.pin, PartnerUId = output });
                    }
            }
            return LadLayoutEngine.LayoutBody(graph, CompareState.Equal);
        }

        private static IEnumerable<string> DefaultPins(DiagramNode node)
        {
            if (node.IsCall || !node.IsBox || !BoxInstructions.Contains(node.Name)) return Array.Empty<string>();
            switch (node.Name.ToUpperInvariant())
            {
                case "PBOX": case "NBOX": return new[] { "in", "out" };
                case "TON": case "TOF": case "TP": return new[] { "IN", "PT", "Q", "ET" };
                case "TONR": return new[] { "IN", "R", "PT", "Q", "ET" };
                case "CTU": return new[] { "CU", "R", "PV", "Q", "CV" };
                case "CTD": return new[] { "CD", "LD", "PV", "Q", "CV" };
                case "CTUD": return new[] { "CU", "CD", "R", "LD", "PV", "QU", "QD", "CV" };
            }
            var pins = new List<string> { "en", "eno" };
            int count = node.Name.Equals("Move", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
            var cardinality = node.Xml.Elements().FirstOrDefault(e => e.Name.LocalName == "TemplateValue" && (string?)e.Attribute("Type") == "Cardinality");
            if (cardinality != null && int.TryParse(cardinality.Value, out int value)) count = value;
            if (count < 1 || count > PlcProgramRenderer.MaxElements) throw new ArgumentException("Instruction pin cardinality exceeds the render budget.");
            if (node.Name.Equals("Move", StringComparison.OrdinalIgnoreCase))
            {
                pins.Add("in");
                pins.AddRange(Enumerable.Range(1, count).Select(i => "out" + i));
            }
            else if (new[] { "ADD", "SUB", "MUL", "DIV", "MOD", "AND", "XOR" }.Contains(node.Name.ToUpperInvariant()))
            {
                pins.AddRange(Enumerable.Range(1, count).Select(i => "in" + i));
                pins.Add("out");
            }
            return pins;
        }

        private static DiagramPin? EntryPin(DiagramNode node)
            => node.Pins.FirstOrDefault(p => p.Name.Equals("en", StringComparison.OrdinalIgnoreCase))
                ?? node.Pins.FirstOrDefault(p => !p.Output && (p.Type.Equals("Bool", StringComparison.OrdinalIgnoreCase)
                    || p.Type.Length == 0 && new[] { "IN", "CLK", "CU", "CD" }.Contains(p.Name.ToUpperInvariant())));

        private static bool IsOutputPin(string name)
            => new[] { "out", "eno", "q", "qu", "qd", "et", "cv", "ret_val", "output" }.Any(p => name.Equals(p, StringComparison.OrdinalIgnoreCase))
                || name.StartsWith("out", StringComparison.OrdinalIgnoreCase);

        private static void DrawWire(StringBuilder b, XElement wire, Dictionary<string, DiagramNode> nodes)
        {
            if (wire.Elements().Any(e => e.Name.LocalName == "IdentCon")) return;
            var ends = new List<(int x, int y, bool output)>();
            foreach (var connection in wire.Elements().Where(e => e.Name.LocalName == "NameCon"))
            {
                if (!nodes.TryGetValue((string?)connection.Attribute("UId") ?? "", out var node)) continue;
                string name = (string?)connection.Attribute("Name") ?? "";
                var pin = name.Equals("pre", StringComparison.OrdinalIgnoreCase) ? EntryPin(node) : null;
                pin ??= node.Pins.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                bool output = pin?.Output ?? IsOutputPin(name);
                ends.Add((output ? node.Right : node.Left, node.IsBox ? pin?.Y ?? node.Y : node.Y, output));
            }
            bool rail = wire.Elements().Any(e => e.Name.LocalName == "Powerrail");
            if (rail && ends.Count > 0) ends.Insert(0, (24, ends[0].y, true));
            if (ends.Count == 1 && wire.Elements().Any(e => e.Name.LocalName == "OpenCon" || e.Name.LocalName == "Openbranch"))
            {
                var end = ends[0]; int x = end.x + (end.output ? 18 : -18);
                if (end.output) return; // Unconnected box outputs are shown as "..." by DrawBox.
                Path(b, "wire", $"M {end.x} {end.y} H {x}");
                b.Append("<circle class=\"open-pin\" cx=\"").Append(x).Append("\" cy=\"").Append(end.y).Append("\" r=\"3\"/>");
                return;
            }
            if (ends.Count < 2) return;
            var sources = ends.Where(e => e.output).ToArray(); var targets = ends.Where(e => !e.output).ToArray();
            int bus = rail ? 24 : sources.Length > 0 && targets.Length > 0 ? (sources.Max(e => e.x) + targets.Min(e => e.x)) / 2 : ends.Min(e => e.x) - 20;
            foreach (var end in ends) Path(b, "wire", $"M {end.x} {end.y} H {bus}");
            int top = ends.Min(e => e.y), bottom = ends.Max(e => e.y);
            if (top != bottom) Path(b, "branch", $"M {bus} {top} V {bottom}");
            if (ends.Count > 2)
                foreach (int y in ends.Select(e => e.y).Distinct()) b.Append("<circle class=\"junction\" cx=\"").Append(bus).Append("\" cy=\"").Append(y).Append("\" r=\"2.5\"/>");
        }

        private static void DrawSymbol(StringBuilder b, DiagramNode node)
        {
            int x = node.X, y = node.Y;
            Text(b, "operand", x, y - (node.Symbol == "compare" ? 39 : 25) - (Wrap(node.Operand, 22).Count() - 1) * 16, node.Operand, "middle", 22);
            if (node.Symbol == "compare" || node.Symbol == "not")
            {
                int plate = node.Symbol == "compare" ? 26 : 22, halfHeight = node.Symbol == "compare" ? 26 : 15;
                Path(b, "symbol", $"M {node.Left} {y} H {x - plate} M {x + plate} {y} H {node.Right} M {x - plate} {y - halfHeight} V {y + halfHeight} M {x + plate} {y - halfHeight} V {y + halfHeight}");
                string mark = node.Name switch { "Eq" => "==", "Ne" => "<>", "Gt" => ">", "Ge" => ">=", "Lt" => "<", "Le" => "<=", _ => "NOT" };
                Text(b, "symbol-mark", x, y + (node.Symbol == "compare" ? -4 : 5), mark, "middle");
                if (node.Symbol == "compare") Text(b, "instruction-type", x, y + 17, node.Preset, "middle", 12);
            }
            else if (node.Symbol.StartsWith("contact-", StringComparison.Ordinal))
            {
                Path(b, "symbol", $"M {x - 30} {y} H {x - 8} M {x + 8} {y} H {x + 30} M {x - 8} {y - 15} V {y + 15} M {x + 8} {y - 15} V {y + 15}");
                if (node.Symbol == "contact-nc") Path(b, "symbol", $"M {x - 7} {y + 12} L {x + 7} {y - 12}");
                if (node.Symbol == "contact-positive" || node.Symbol == "contact-negative") Text(b, "symbol-mark", x, y + 5, node.Symbol == "contact-positive" ? "P" : "N", "middle");
            }
            else
            {
                Path(b, "symbol", $"M {x - 30} {y} H {x - 15} M {x + 15} {y} H {x + 30} M {x - 6} {y - 15} Q {x - 24} {y} {x - 6} {y + 15} M {x + 6} {y - 15} Q {x + 24} {y} {x + 6} {y + 15}");
                if (node.Symbol == "coil-negated") Path(b, "symbol", $"M {x - 6} {y + 12} L {x + 6} {y - 12}");
                string mark = node.Symbol switch { "coil-set" => "S", "coil-reset" => "R", "coil-positive" => "P", "coil-negative" => "N", _ => "" };
                Text(b, "symbol-mark", x, y + 5, mark, "middle");
            }
            int lowerY = y + (node.Symbol == "compare" ? 49 : 34);
            Text(b, node.Symbol == "compare" ? "compare-operand" : "edge-memory", x, lowerY, node.LowerOperand, "middle", 22);
            Text(b, "symbol-comment", x, lowerY + Wrap(node.LowerOperand, 22).Count() * 16, node.Comment, "middle", 24);
            if (node.Finding.Length > 0) b.Append("<circle class=\"finding\" cx=\"").Append(x + 23).Append("\" cy=\"").Append(y - 18).Append("\" r=\"4\"><title>").Append(E(node.Finding)).Append("</title></circle>");
        }

        private static void DrawBox(StringBuilder b, DiagramNode node)
        {
            int header = HeaderHeight(node), bottom = Math.Max(node.Top + header + 32, node.Bottom);
            b.Append("<rect class=\"box\" x=\"").Append(node.Left).Append("\" y=\"").Append(node.Top).Append("\" width=\"").Append(node.BoxWidth).Append("\" height=\"").Append(bottom - node.Top).Append("\"/>");
            Path(b, "box-divider", $"M {node.Left} {node.Top + header} H {node.Right}");
            Text(b, "instance", node.X, node.Top - 12 - (Wrap(node.Instance, 24).Count() - 1) * 16, node.Instance, "middle", 24);
            Text(b, "instruction-name", node.X, node.Top + 19, node.Title, "middle", 22);
            if (node.Preset.Length > 0) Text(b, "instruction-type", node.X, node.Top + header - 7 - (Wrap(node.Preset, 22).Count() - 1) * 16, node.Preset, "middle", 22);
            foreach (var pin in node.Pins)
            {
                int edge = pin.Output ? node.Right : node.Left, outer = edge + (pin.Output ? 16 : -16);
                Path(b, "pin", $"M {edge} {pin.Y} H {outer}");
                Text(b, "pin-name", edge + (pin.Output ? -8 : 8), pin.Y + 4, pin.Label, pin.Output ? "end" : "start");
                Text(b, "pin-value", outer + (pin.Output ? 5 : -5), pin.Y + 4, pin.Value, pin.Output ? "start" : "end", 18);
            }
            Text(b, "edge-memory", node.X, bottom + 20, node.LowerOperand, "middle", 22);
            Text(b, "symbol-comment", node.X, bottom + 20 + Wrap(node.LowerOperand, 22).Count() * 16, node.Comment, "middle", 24);
        }

        private static void Path(StringBuilder b, string kind, string path)
            => b.Append("<path class=\"").Append(kind).Append("\" d=\"").Append(path).Append("\"/>");

        private static void Text(StringBuilder b, string kind, int x, int y, string value, string anchor, int wrap = 100)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            b.Append("<text class=\"").Append(kind).Append("\" x=\"").Append(x).Append("\" y=\"").Append(y).Append("\" text-anchor=\"").Append(anchor).Append("\">");
            var lines = Wrap(value, wrap).ToArray();
            if (lines.Length == 1) b.Append(E(lines[0]));
            else for (int i = 0; i < lines.Length; i++) b.Append("<tspan x=\"").Append(x).Append("\" dy=\"").Append(i == 0 ? 0 : 16).Append("\">").Append(E(lines[i])).Append("</tspan>");
            b.Append("</text>");
        }

        private static IEnumerable<string> Wrap(string text, int width)
        {
            var elements = StringInfo.GetTextElementEnumerator(text);
            var line = new StringBuilder(); int count = 0;
            while (elements.MoveNext())
            {
                string next = elements.GetTextElement();
                if (next == "\n") { yield return line.ToString(); line.Clear(); count = 0; continue; }
                if (next == "\r") continue;
                line.Append(next);
                if (++count == width) { yield return line.ToString(); line.Clear(); count = 0; }
            }
            if (line.Length > 0) yield return line.ToString();
        }
    }
}
