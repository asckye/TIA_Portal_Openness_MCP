using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using TiaMcp.Logic.V4.Construction;

namespace TiaMcpServer.ModelContextProtocol
{
    internal sealed class FlgNetFormat
    {
        internal XNamespace Namespace { get; } = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";
    }

    // The typed boundary owns shape, local-member and part-budget validation. Nets
    // collect all destinations before emission so shared prefixes have one source wire.
    internal static class FlgNetRungXmlBuilder
    {
        private sealed class Pin
        {
            internal XElement? Owner { get; }
            internal string? Name { get; }
            internal Pin(XElement? owner = null, string? name = null) { Owner = owner; Name = name; }
            internal XElement Emit(XNamespace ns) => Owner == null ? new XElement(ns + "Powerrail")
                : Name == null ? new XElement(ns + "IdentCon", new XAttribute("UId", Owner.Attribute("UId")!.Value))
                : new XElement(ns + "NameCon", new XAttribute("UId", Owner.Attribute("UId")!.Value), new XAttribute("Name", Name));
        }

        private sealed class Net
        {
            internal Pin Source { get; }
            internal List<Pin> Destinations { get; } = new List<Pin>();
            internal bool OpenWhenUnused { get; }
            internal Net(Pin source, bool openWhenUnused = false) { Source = source; OpenWhenUnused = openWhenUnused; }
        }

        private sealed class Element
        {
            internal XElement Part { get; }
            internal List<Net> Operands { get; } = new List<Net>();
            internal List<Net> Sources { get; } = new List<Net>();
            internal Element(XElement part) { Part = part; }
            internal Net Output(string pin, bool openWhenUnused = false)
            {
                var net = new Net(new Pin(Part, pin), openWhenUnused);
                Sources.Add(net);
                return net;
            }
        }

        internal static XElement Build(LadRung rung, FlgNetFormat format, IReadOnlyCollection<string> localMembers)
            => new Compiler(format).Build(rung);

        private sealed class Compiler
        {
            private readonly XNamespace _ns;
            private readonly List<XElement> _accesses = new List<XElement>();
            private readonly List<Element> _elements = new List<Element>();
            private readonly Net _rail = new Net(new Pin());
            internal Compiler(FlgNetFormat format) { _ns = format.Namespace; }

            private XElement Access(LadOperand operand)
            {
                string scope = operand.Kind == "global" ? "GlobalVariable" : operand.Kind == "local" ? "LocalVariable"
                    : operand.Kind == "literal" ? "LiteralConstant" : operand.Kind == "typed" ? "TypedConstant" : "GlobalConstant";
                XElement content;
                if (operand.Components != null)
                    content = new XElement(_ns + "Symbol", Components(operand.Components));
                else if (operand.Kind == "constant") content = new XElement(_ns + "Constant", new XAttribute("Name", operand.Value!));
                else content = new XElement(_ns + "Constant",
                    operand.DataType == null ? null : new XElement(_ns + "ConstantType", operand.DataType),
                    new XElement(_ns + "ConstantValue", operand.Value));
                var access = new XElement(_ns + "Access", new XAttribute("Scope", scope), content);
                _accesses.Add(access);
                return access;
            }
            private IEnumerable<XElement> Components(IReadOnlyList<LadComponent> components)
            {
                return components.Select(component =>
                {
                    var xml = new XElement(_ns + "Component", new XAttribute("Name", component.Name));
                    if (component.Index != null)
                    {
                        xml.Add(new XAttribute("AccessModifier", "Array"));
                        foreach (int index in component.Index)
                            xml.Add(new XElement(_ns + "Access", new XAttribute("Scope", "LiteralConstant"),
                                new XElement(_ns + "Constant", new XElement(_ns + "ConstantValue", index.ToString(CultureInfo.InvariantCulture)))));
                    }
                    return xml;
                });
            }

            private void Instance(Element element, IEnumerable<XElement> components)
            {
                var owner = element.Part.Name.LocalName == "Call" ? element.Part.Elements().Single() : element.Part;
                owner.AddFirst(new XElement(_ns + "Instance", new XAttribute("Scope", "GlobalVariable"), components));
            }

            private Element Part(string name, string? template = null, string? value = null)
            {
                var part = new XElement(_ns + "Part", new XAttribute("Name", name));
                if (template != null) part.Add(new XElement(_ns + "TemplateValue", new XAttribute("Name", template),
                    new XAttribute("Type", template == "Card" ? "Cardinality" : "Type"), value));
                var element = new Element(part);
                _elements.Add(element);
                return element;
            }
            private void Operand(Element element, string pin, LadOperand operand)
            {
                var net = new Net(new Pin(Access(operand)));
                net.Destinations.Add(new Pin(element.Part, pin));
                element.Operands.Add(net);
            }
            private Net Series(IReadOnlyList<LadCondition> series, Net incoming)
            {
                foreach (var condition in series)
                {
                    if (condition.Paths != null)
                    {
                        var paths = condition.Paths.Select(path => Series(path, incoming)).ToArray();
                        var join = Part("O", "Card", paths.Length.ToString(CultureInfo.InvariantCulture));
                        for (int i = 0; i < paths.Length; i++)
                            paths[i].Destinations.Add(new Pin(join.Part, "in" + (i + 1).ToString(CultureInfo.InvariantCulture)));
                        incoming = join.Output("out");
                    }
                    else
                    {
                        bool compare = condition.Kind == "compare";
                        string name = compare ? char.ToUpperInvariant(condition.Op![0]) + condition.Op.Substring(1)
                            : condition.Kind == "rising" ? "PContact" : condition.Kind == "falling" ? "NContact" : "Contact";
                        var element = Part(name, compare ? "SrcType" : null, condition.DataType);
                        if (condition.Kind == "nc") element.Part.Add(new XElement(_ns + "Negated", new XAttribute("Name", "operand")));
                        incoming.Destinations.Add(new Pin(element.Part, compare ? "pre" : "in"));
                        if (compare)
                        {
                            Operand(element, "in1", condition.Left!);
                            Operand(element, "in2", condition.Right!);
                        }
                        else
                        {
                            Operand(element, "operand", condition.Operand!);
                            if (condition.Memory != null) Operand(element, "bit", condition.Memory);
                        }
                        incoming = element.Output("out");
                    }
                }
                return incoming;
            }
            private void Outputs(IReadOnlyList<LadOutput> outputs, Net incoming)
            {
                foreach (var output in outputs)
                {
                    if (output.Kind == "timer") { Timer(output, incoming); continue; }
                    if (output.Kind == "counter") { Counter(output, incoming); continue; }
                    if (output.Kind == "call") { Call(output, incoming); continue; }
                    bool move = output.Kind == "move";
                    var element = Part(move ? "Move" : output.Kind == "set" ? "SCoil" : output.Kind == "reset" ? "RCoil" : "Coil",
                        move ? "Card" : null, output.Out?.Count.ToString(CultureInfo.InvariantCulture));
                    incoming.Destinations.Add(new Pin(element.Part, move ? "en" : "in"));
                    if (move)
                    {
                        if (output.Then == null) element.Part.Add(new XAttribute("DisabledENO", "true"));
                        Operand(element, "in", output.In!);
                        for (int i = 0; i < output.Out!.Count; i++)
                            element.Output("out" + (i + 1).ToString(CultureInfo.InvariantCulture)).Destinations.Add(new Pin(Access(output.Out[i])));
                        if (output.Then != null) Outputs(output.Then, element.Output("eno"));
                    }
                    else Operand(element, "operand", output.Operand!);
                }
            }

            private void Destination(Net net, LadOperand? operand)
            {
                if (operand != null) net.Destinations.Add(new Pin(Access(operand)));
            }

            private void Timer(LadOutput output, Net incoming)
            {
                var element = Part(output.DataType!, "time_type", "Time");
                element.Part.Add(new XAttribute("Version", "1.0"));
                Instance(element, Components(output.Instance!.Components!));
                incoming.Destinations.Add(new Pin(element.Part, "IN"));
                Operand(element, "PT", output.Pt!);
                var q = element.Output("Q", true);
                Destination(element.Output("ET", true), output.Et);
                if (output.Q != null) Outputs(output.Q, q);
            }

            private void Counter(LadOutput output, Net incoming)
            {
                var reset = output.Reset == null ? null : Series(output.Reset, _rail);
                var load = output.Load == null ? null : Series(output.Load, _rail);
                var down = output.Down == null ? null : Series(output.Down, _rail);
                var element = Part(output.DataType!, "value_type", output.ValueType);
                element.Part.Add(new XAttribute("Version", "1.0"));
                Instance(element, Components(output.Instance!.Components!));
                incoming.Destinations.Add(new Pin(element.Part, output.DataType == "CTD" ? "CD" : "CU"));
                down?.Destinations.Add(new Pin(element.Part, "CD"));
                reset?.Destinations.Add(new Pin(element.Part, "R"));
                load?.Destinations.Add(new Pin(element.Part, "LD"));
                Operand(element, "PV", output.Pv!);
                var q = element.Output(output.DataType == "CTUD" ? "QU" : "Q", true);
                var qd = output.DataType == "CTUD" ? element.Output("QD", true) : null;
                Destination(element.Output("CV", true), output.Cv);
                if (output.Q != null) Outputs(output.Q, q);
                if (output.Qd != null) Outputs(output.Qd, qd!);
            }

            private void Call(LadOutput output, Net incoming)
            {
                var info = new XElement(_ns + "CallInfo", new XAttribute("Name", output.Block!),
                    new XAttribute("BlockType", output.BlockType!), output.Pins!.Select(pin => new XElement(_ns + "Parameter",
                        new XAttribute("Name", pin.Name), new XAttribute("Section", pin.Section), new XAttribute("Type", pin.DataType))));
                var element = new Element(new XElement(_ns + "Call", info));
                _elements.Add(element);
                if (output.InstanceDb != null)
                    Instance(element, new[] { new XElement(_ns + "Component", new XAttribute("Name", output.InstanceDb)) });
                incoming.Destinations.Add(new Pin(element.Part, "en"));
                foreach (var pin in output.Pins)
                    if (pin.Section == "Output") Destination(element.Output(pin.Name), pin.Value);
                    else Operand(element, pin.Name, pin.Value);
                if (output.Then != null) Outputs(output.Then, element.Output("eno"));
            }

            internal XElement Build(LadRung rung)
            {
                var head = Series(rung.Series, _rail);
                Outputs(rung.Outputs, head);
                if (rung.FanOut != null)
                    foreach (var branch in rung.FanOut) Outputs(branch.Outputs, Series(branch.Series, head));

                int uid = 21;
                string Next() => (uid++).ToString(CultureInfo.InvariantCulture);
                foreach (var access in _accesses) access.Add(new XAttribute("UId", Next()));
                foreach (var element in _elements)
                {
                    element.Part.Add(new XAttribute("UId", Next()));
                    var instance = element.Part.Descendants(_ns + "Instance").SingleOrDefault();
                    instance?.Add(new XAttribute("UId", Next()));
                }
                var wires = new XElement(_ns + "Wires");
                void Wire(Net net)
                {
                    if (net.Destinations.Count == 0 && !net.OpenWhenUnused) return;
                    string id = Next();
                    wires.Add(new XElement(_ns + "Wire", new XAttribute("UId", id), net.Source.Emit(_ns),
                        net.Destinations.Count == 0 ? new[] { new XElement(_ns + "OpenCon", new XAttribute("UId", Next())) }
                            : net.Destinations.Select(pin => pin.Emit(_ns))));
                }
                Wire(_rail);
                foreach (var element in _elements)
                {
                    foreach (var operand in element.Operands) Wire(operand);
                    foreach (var source in element.Sources) Wire(source);
                }
                var result = new XElement(_ns + "FlgNet", new XElement(_ns + "Parts", _accesses, _elements.Select(e => e.Part)), wires);
                Verify(result);
                return result;
            }
        }

        // Pin tables also serve the post-serialization structural check. Native
        // symbol/type resolution and import acceptance remain outside this compiler.
        private static string[] Inputs(XElement part)
        {
            if (part.Name.LocalName == "Call")
                return new[] { "en" }.Concat(part.Descendants(part.Name.Namespace + "Parameter")
                    .Where(p => (string?)p.Attribute("Section") != "Output").Select(p => p.Attribute("Name")!.Value)).ToArray();
            switch ((string?)part.Attribute("Name"))
            {
                case "Contact": return new[] { "in", "operand" };
                case "PContact": case "NContact": return new[] { "in", "operand", "bit" };
                case "Eq": case "Ne": case "Gt": case "Ge": case "Lt": case "Le": return new[] { "pre", "in1", "in2" };
                case "Coil": case "SCoil": case "RCoil": return new[] { "in", "operand" };
                case "Move": return new[] { "en", "in" };
                case "TON": case "TOF": case "TP": return new[] { "IN", "PT" };
                case "CTU": return new[] { "CU", "R", "PV" };
                case "CTD": return new[] { "CD", "LD", "PV" };
                case "CTUD": return new[] { "CU", "CD", "R", "LD", "PV" };
                case "O":
                    int cardinality = int.Parse(part.Elements().Single().Value, CultureInfo.InvariantCulture);
                    return Enumerable.Range(1, cardinality).Select(i => "in" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
                default: throw new InvalidOperationException("Unknown rung part.");
            }
        }

        private static bool IsOutput(XElement part, string pin)
        {
            var ns = part.Name.Namespace;
            if (part.Name.LocalName == "Call")
                return pin == "eno" || part.Descendants(ns + "Parameter").Any(p =>
                    (string?)p.Attribute("Name") == pin && (string?)p.Attribute("Section") == "Output");
            switch ((string?)part.Attribute("Name"))
            {
                case "Move": return pin == "eno" && part.Attribute("DisabledENO") == null
                    || Enumerable.Range(1, int.Parse(part.Elements(ns + "TemplateValue").Single().Value, CultureInfo.InvariantCulture))
                        .Any(i => pin == "out" + i.ToString(CultureInfo.InvariantCulture));
                case "TON": case "TOF": case "TP": return pin == "Q" || pin == "ET";
                case "CTU": case "CTD": return pin == "Q" || pin == "CV";
                case "CTUD": return pin == "QU" || pin == "QD" || pin == "CV";
                case "Coil": case "SCoil": case "RCoil": return false;
                default: return pin == "out";
            }
        }

        internal static void Verify(XElement network)
        {
            var ns = network.Name.Namespace;
            var parts = network.Element(ns + "Parts")!.Elements().ToArray();
            var wires = network.Element(ns + "Wires")!.Elements().ToArray();
            var ids = network.Descendants().Attributes("UId").Where(a => a.Parent!.Name.LocalName != "NameCon"
                && a.Parent.Name.LocalName != "IdentCon").Select(a => a.Value).ToArray();
            if (ids.Any(id => !int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1)
                || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new InvalidOperationException("Invalid rung UId or collision.");
            var byId = parts.ToDictionary(p => p.Attribute("UId")!.Value, StringComparer.Ordinal);
            var inputs = parts.Where(p => p.Name.LocalName == "Part" || p.Name.LocalName == "Call").SelectMany(p => Inputs(p)
                .Select(pin => p.Attribute("UId")!.Value + "/" + pin)).ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
            var sources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var wire in wires)
            {
                var ends = wire.Elements().ToArray();
                if (ends.Length < 2) throw new InvalidOperationException("Rung wire lacks a destination.");
                string Key(XElement end) => end.Name.LocalName + "/" + (string?)end.Attribute("UId") + "/" + (string?)end.Attribute("Name");
                if (!sources.Add(Key(ends[0]))) throw new InvalidOperationException("Repeated rung source wire.");
                foreach (var end in ends)
                {
                    if (end.Name.LocalName == "Powerrail")
                    {
                        if (end != ends[0]) throw new InvalidOperationException("Powerrail must be a source.");
                        continue;
                    }
                    if (end.Name.LocalName == "OpenCon")
                    {
                        if (end == ends[0]) throw new InvalidOperationException("Open connector must be a destination.");
                        continue;
                    }
                    if (!byId.TryGetValue(end.Attribute("UId")!.Value, out var target)
                        || (end.Name.LocalName == "IdentCon") != (target.Name.LocalName == "Access"))
                        throw new InvalidOperationException("Broken rung reference.");
                    if (end.Name.LocalName == "NameCon")
                    {
                        string pin = end.Attribute("Name")!.Value;
                        string key = end.Attribute("UId")!.Value + "/" + pin;
                        if (inputs.TryGetValue(key, out int count))
                        {
                            if (end == ends[0]) throw new InvalidOperationException("Input pin used as source.");
                            inputs[key] = count + 1;
                        }
                        else
                        {
                            if (!IsOutput(target, pin) || end != ends[0])
                                throw new InvalidOperationException("Unknown or misdirected rung pin.");
                        }
                    }
                }
            }
            if (inputs.Values.Any(count => count != 1)) throw new InvalidOperationException("Rung input must have exactly one source wire.");
        }
    }
}
