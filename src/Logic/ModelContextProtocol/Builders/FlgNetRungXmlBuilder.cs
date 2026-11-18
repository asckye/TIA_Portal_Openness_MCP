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
            internal Net(Pin source) { Source = source; }
        }

        private sealed class Element
        {
            internal XElement Part { get; }
            internal List<Net> Operands { get; } = new List<Net>();
            internal List<Net> Sources { get; } = new List<Net>();
            internal Element(XElement part) { Part = part; }
            internal Net Output(string pin)
            {
                var net = new Net(new Pin(Part, pin));
                Sources.Add(net);
                return net;
            }
        }

        internal static XElement Build(LadRung rung, FlgNetFormat format, IReadOnlyCollection<string> localMembers)
        {
            var ns = format.Namespace;
            var accesses = new List<XElement>();
            var elements = new List<Element>();
            var rail = new Net(new Pin());
            XElement Access(LadOperand operand)
            {
                string scope = operand.Kind == "global" ? "GlobalVariable" : operand.Kind == "local" ? "LocalVariable"
                    : operand.Kind == "literal" ? "LiteralConstant" : operand.Kind == "typed" ? "TypedConstant" : "GlobalConstant";
                XElement content;
                if (operand.Components != null)
                    content = new XElement(ns + "Symbol", operand.Components.Select(component =>
                    {
                        var xml = new XElement(ns + "Component", new XAttribute("Name", component.Name));
                        if (component.Index != null)
                        {
                            xml.Add(new XAttribute("AccessModifier", "Array"));
                            foreach (int index in component.Index)
                                xml.Add(new XElement(ns + "Access", new XAttribute("Scope", "LiteralConstant"),
                                    new XElement(ns + "Constant", new XElement(ns + "ConstantValue", index.ToString(CultureInfo.InvariantCulture)))));
                        }
                        return xml;
                    }));
                else if (operand.Kind == "constant") content = new XElement(ns + "Constant", new XAttribute("Name", operand.Value!));
                else content = new XElement(ns + "Constant",
                    operand.DataType == null ? null : new XElement(ns + "ConstantType", operand.DataType),
                    new XElement(ns + "ConstantValue", operand.Value));
                var access = new XElement(ns + "Access", new XAttribute("Scope", scope), content);
                accesses.Add(access);
                return access;
            }
            Element Part(string name, string? template = null, string? value = null)
            {
                var part = new XElement(ns + "Part", new XAttribute("Name", name));
                if (template != null) part.Add(new XElement(ns + "TemplateValue", new XAttribute("Name", template),
                    new XAttribute("Type", template == "Card" ? "Cardinality" : "Type"), value));
                var element = new Element(part);
                elements.Add(element);
                return element;
            }
            void Operand(Element element, string pin, LadOperand operand)
            {
                var net = new Net(new Pin(Access(operand)));
                net.Destinations.Add(new Pin(element.Part, pin));
                element.Operands.Add(net);
            }
            Net Series(IReadOnlyList<LadCondition> series, Net incoming)
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
                        if (condition.Kind == "nc") element.Part.Add(new XElement(ns + "Negated", new XAttribute("Name", "operand")));
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
            void Outputs(IReadOnlyList<LadOutput> outputs, Net incoming)
            {
                foreach (var output in outputs)
                {
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

            var head = Series(rung.Series, rail);
            Outputs(rung.Outputs, head);
            if (rung.FanOut != null)
                foreach (var branch in rung.FanOut) Outputs(branch.Outputs, Series(branch.Series, head));

            int uid = 21;
            string Next() => (uid++).ToString(CultureInfo.InvariantCulture);
            foreach (var access in accesses) access.Add(new XAttribute("UId", Next()));
            foreach (var element in elements) element.Part.Add(new XAttribute("UId", Next()));
            var wires = new XElement(ns + "Wires");
            void Wire(Net net)
            {
                if (net.Destinations.Count == 0) return;
                wires.Add(new XElement(ns + "Wire", new XAttribute("UId", Next()), net.Source.Emit(ns),
                    net.Destinations.Select(pin => pin.Emit(ns))));
            }
            Wire(rail);
            foreach (var element in elements)
            {
                foreach (var operand in element.Operands) Wire(operand);
                foreach (var source in element.Sources) Wire(source);
            }
            var result = new XElement(ns + "FlgNet", new XElement(ns + "Parts", accesses, elements.Select(e => e.Part)), wires);
            Verify(result);
            return result;
        }

        // Pin tables also serve the post-serialization structural check. Native
        // symbol/type resolution and import acceptance remain outside this compiler.
        private static string[] Inputs(XElement part)
        {
            switch ((string?)part.Attribute("Name"))
            {
                case "Contact": return new[] { "in", "operand" };
                case "PContact": case "NContact": return new[] { "in", "operand", "bit" };
                case "Eq": case "Ne": case "Gt": case "Ge": case "Lt": case "Le": return new[] { "pre", "in1", "in2" };
                case "Coil": case "SCoil": case "RCoil": return new[] { "in", "operand" };
                case "Move": return new[] { "en", "in" };
                case "O":
                    int cardinality = int.Parse(part.Elements().Single().Value, CultureInfo.InvariantCulture);
                    return Enumerable.Range(1, cardinality).Select(i => "in" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
                default: throw new InvalidOperationException("Unknown rung part.");
            }
        }

        internal static void Verify(XElement network)
        {
            var ns = network.Name.Namespace;
            var parts = network.Element(ns + "Parts")!.Elements().ToArray();
            var wires = network.Element(ns + "Wires")!.Elements().ToArray();
            var ids = parts.Concat(wires).Select(e => e.Attribute("UId")!.Value).ToArray();
            if (ids.Any(id => !int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1)
                || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new InvalidOperationException("Invalid rung UId or collision.");
            var byId = parts.ToDictionary(p => p.Attribute("UId")!.Value, StringComparer.Ordinal);
            var inputs = parts.Where(p => p.Name.LocalName == "Part").SelectMany(p => Inputs(p)
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
                            string name = target.Attribute("Name")!.Value;
                            bool output = name == "Move" ? pin == "eno" && target.Attribute("DisabledENO") == null
                                || Enumerable.Range(1, int.Parse(target.Elements(ns + "TemplateValue").Single().Value, CultureInfo.InvariantCulture))
                                    .Any(i => pin == "out" + i.ToString(CultureInfo.InvariantCulture))
                                : name != "Coil" && name != "SCoil" && name != "RCoil" && pin == "out";
                            if (!output || end != ends[0]) throw new InvalidOperationException("Unknown or misdirected rung pin.");
                        }
                    }
                }
            }
            if (inputs.Values.Any(count => count != 1)) throw new InvalidOperationException("Rung input must have exactly one source wire.");
        }
    }
}
