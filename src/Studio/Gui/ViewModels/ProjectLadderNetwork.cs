extern alias PlcRendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaOpenness.Contracts.Models;
using LadTextRenderer = PlcRendering::TiaMcpServer.ModelContextProtocol.LadTextRenderer;

namespace TiaOpenness.Gui.ViewModels;

public sealed record LadderElement(string Operand, string Symbol, string Address = "")
{
    public bool IsCoil => Symbol.StartsWith('(');
    public string CoilMark => Symbol is "(S)" or "(R)" ? Symbol[1].ToString() : "";
}

public sealed record LadderPin(string Name, string Parameter, string Section = "")
{
    public bool Input => Section is "Input" or "In";
    public bool Output => Section is "Output" or "Out" or "Return" or "InOut";
    public string Label => Section == "InOut" ? "↔ " + Name : Name;
}

public sealed record LadderRung(string Description, IReadOnlyList<LadderElement> Elements)
{
    public bool HasElements => Elements.Count > 0;
    public string Enable { get; init; } = "";
    public string Box { get; init; } = "";
    public string Instance { get; init; } = "";
    public IReadOnlyList<LadderPin> Pins { get; init; } = [];
    public IEnumerable<LadderPin> Inputs => Pins.Where(p => p.Input);
    public IEnumerable<LadderPin> Outputs => Pins.Where(p => p.Output);
    public IEnumerable<LadderPin> Unclassified => Pins.Where(p => !p.Input && !p.Output);
    public bool HasUnclassified => Unclassified.Any();
    public bool IsBox => Box.Length != 0;
    public bool IsText => !HasElements && !IsBox;
}

public sealed record ProjectLadderNetwork(string Title, IReadOnlyList<LadderRung> Rungs)
{
    public string NumberLabel { get; init; } = "";
    public string Language { get; init; } = "";
    public string CallIdentity => Rungs.Count == 1 && Rungs[0].IsBox
        ? string.Join(" · ", new[] { "CALL " + Rungs[0].Box, Rungs[0].Instance }.Where(s => s.Length != 0)) : "";
    private static readonly Regex Header = new(@"^程序段 (\d+)(?: · (.*?))?\s+\[([^\]]+)\]$");
    private static readonly Regex Call = new("""^当 \[(.+)\] 时: CALL "([^"]+)"(?:\[(.+)\])?\((.*)\)$""");
    private static readonly Regex Coil = new(@"^(.+) (\([ SR]\))  ⇐  (.+)$");
    private static readonly Regex Symbol = new("""^/?(?:[#%\w.\[\]]|"(?:[^"]|"")+")+$""");

    internal static IReadOnlyList<ProjectLadderNetwork> FromListing(IReadOnlyList<string> lines)
    {
        var networks = new List<ProjectLadderNetwork>();
        List<LadderRung>? rungs = null;
        foreach (string source in lines)
        {
            string line = source.Trim();
            if (line.Length == 0) continue;
            if (rungs == null || line.StartsWith("── ", StringComparison.Ordinal))
            {
                rungs = [];
                bool header = line.StartsWith("── ", StringComparison.Ordinal);
                string title = header ? line[3..] : "";
                var fields = Header.Match(title);
                networks.Add(fields.Success
                    ? new(fields.Groups[2].Value, rungs) { NumberLabel = "Network " + fields.Groups[1].Value, Language = fields.Groups[3].Value }
                    : new(title, rungs));
                if (header) continue;
            }
            var call = Call.Match(line);
            if (call.Success && ParsePins(call.Groups[4].Value) is { } pins)
            {
                rungs.Add(new(line, []) { Enable = call.Groups[1].Value, Box = call.Groups[2].Value, Instance = call.Groups[3].Value, Pins = pins });
                continue;
            }
            var elements = new List<LadderElement>();
            var coil = Coil.Match(line);
            if (coil.Success)
            {
                var contacts = coil.Groups[3].Value.Split(" · ");
                if (contacts.All(Symbol.IsMatch))
                {
                    elements.AddRange(contacts.Select(c => new LadderElement(c.TrimStart('/'), c.StartsWith('/') ? "┤/├" : "┤ ├")));
                    elements.Add(new(coil.Groups[1].Value, coil.Groups[2].Value));
                }
            }
            rungs.Add(new(line, elements));
        }
        return networks;
    }

    // Parameter values can contain quoted commas, array subscripts and nested power-flow expressions.
    private static List<LadderPin>? ParsePins(string text)
    {
        var pins = new List<LadderPin>();
        if (text.Length == 0) return pins;
        int start = 0, depth = 0;
        char quote = '\0';
        for (int i = 0; i <= text.Length; i++)
        {
            char ch = i == text.Length ? ',' : text[i];
            if (quote != '\0')
            {
                if (ch == '$') i++;
                else if (ch == quote) quote = '\0';
            }
            else if (ch is '"' or '\'') quote = ch;
            else if (ch is '(' or '[' or '⟨') depth++;
            else if (ch is ')' or ']' or '⟩') depth--;
            else if (ch == ',' && depth == 0)
            {
                string item = text[start..i].Trim();
                int equals = item.IndexOf('=');
                if (equals <= 0 || equals + 1 >= item.Length) return null;
                pins.Add(new(item[..equals], item[(equals + 1)..]));
                start = i + 1;
            }
        }
        return start == text.Length + 1 ? pins : null;
    }

    internal static IReadOnlyList<ProjectLadderNetwork> WithMetadata(IReadOnlyList<ProjectLadderNetwork> networks,
        IReadOnlyList<TagInfo> tags, Func<int, string, string, string?> section)
    {
        var names = tags.ToLookup(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var addresses = tags.Where(t => !string.IsNullOrWhiteSpace(t.LogicalAddress)).ToLookup(t => t.LogicalAddress, StringComparer.OrdinalIgnoreCase);
        LadderElement Resolve(LadderElement e)
        {
            if (e.Operand.StartsWith('%'))
            {
                var matches = addresses[e.Operand].ToArray();
                return e with { Operand = matches.Length == 1 ? matches[0].Name : "", Address = e.Operand };
            }
            if (e.Operand.StartsWith('#')) return e;
            var sameName = names[e.Operand.Trim('"')].ToArray();
            return sameName.Length == 1 ? e with { Address = sameName[0].LogicalAddress ?? "" } : e;
        }
        return networks.Select((network, index) => network with { Rungs = network.Rungs.Select(r => r with
        {
            Elements = r.Elements.Select(Resolve).ToArray(),
            Pins = r.Pins.Select(pin => pin with { Section = section(index, r.Box, pin.Name) ?? "" }).ToArray(),
        }).ToArray() }).ToArray();
    }

    internal static IReadOnlyList<ProjectLadderNetwork> FromXml(string xml)
    {
        var sections = XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit")
            .Select(unit => unit.Descendants().Where(e => e.Name.LocalName == "CallInfo")
                .SelectMany(call => call.Elements().Where(p => p.Name.LocalName == "Parameter")
                    .Select(p => (Block: (string?)call.Attribute("Name"), Pin: (string?)p.Attribute("Name"), Section: (string?)p.Attribute("Section"))))
                .ToLookup(p => (p.Block, p.Pin), p => p.Section)).ToArray();
        string? Section(int index, string block, string pin)
        {
            var values = index < sections.Length ? sections[index][(block, pin)].Distinct().ToArray() : [];
            return values.Length == 1 ? values[0] : null;
        }
        return WithMetadata(FromListing(LadTextRenderer.Render(xml).ReplaceLineEndings("\n").Split('\n')), [], Section);
    }
}
