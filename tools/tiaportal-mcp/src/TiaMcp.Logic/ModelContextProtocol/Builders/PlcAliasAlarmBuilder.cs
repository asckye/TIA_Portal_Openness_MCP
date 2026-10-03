using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    public static class PlcAliasAlarmBuilder
    {
        private static readonly XNamespace Ns = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";
        public static string Build(string blockName, int blockNumber, string rowsJson)
        {
            var rows = JsonNode.Parse(rowsJson) as JsonArray ?? throw new ArgumentException("rowsJson must be an array.");
            if (rows.Count < 1 || rows.Count > 500) throw new ArgumentException("Use 1..500 Boolean mapping/alarm rows.");
            var networks = new List<PlcLadFcBlockXmlComposer.LadNetwork>(); var destinations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in rows)
            {
                var row = node as JsonObject ?? throw new ArgumentException("Every row must be an object.");
                if (row.Any(k => !new[] { "source", "destination", "invert", "acknowledge", "title", "comment" }.Contains(k.Key))) throw new ArgumentException("Unknown mapping/alarm property.");
                var source = Path(row["source"]); var destination = Path(row["destination"]);
                if (!destinations.Add(new JsonArray(destination.Select(p => (JsonNode)p).ToArray()).ToJsonString())) throw new ArgumentException("Repeated destination would introduce conflicting assignments.");
                bool invert = row["invert"]?.GetValue<bool>() ?? false;
                var acknowledge = row["acknowledge"] == null ? null : Path(row["acknowledge"]);
                string title = row["title"]?.GetValue<string>() ?? string.Join(".", destination);
                string comment = row["comment"]?.GetValue<string>() ?? "";
                networks.Add(new PlcLadFcBlockXmlComposer.LadNetwork(Network(source, destination, invert, acknowledge == null ? "Coil" : "SCoil"), title, comment));
                if (acknowledge != null) networks.Add(new PlcLadFcBlockXmlComposer.LadNetwork(Network(acknowledge, destination, false, "RCoil"), title + " — acknowledge/reset", "Reset evaluated after set: reset wins when both are true. Level-triggered acknowledgement."));
            }
            return PlcLadFcBlockXmlComposer.ComposeXml(blockName, blockNumber, Array.Empty<PlcBlockMemberDefinition>(), Array.Empty<PlcBlockMemberDefinition>(), networks,
                "Generated Boolean aliases / simple alarm bits. Requires native import/compile and application validation; no safety function certification.", blockName);
        }
        private static string[] Path(JsonNode? value)
        {
            var array = value as JsonArray ?? throw new ArgumentException("Symbol must be an array of exact GLOBAL components, e.g. [\"DB\",\"Alarm\"].");
            if (array.Count < 1 || array.Count > 32) throw new ArgumentException("Use 1..32 symbol components.");
            var parts = array.Select(p => p?.GetValue<string>() ?? "").ToArray();
            if (parts.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 128 || p.Contains("[") || p.Contains("]"))) throw new ArgumentException("Empty/oversized components and array-index syntax are unsupported; use an exported XML template for indexed operands.");
            return parts;
        }
        private static XElement Network(string[] source, string[] destination, bool invert, string coil)
        {
            XElement Access(int id, string[] parts) => new XElement(Ns + "Access", new XAttribute("Scope", "GlobalVariable"), new XAttribute("UId", id), new XElement(Ns + "Symbol", parts.Select(p => new XElement(Ns + "Component", new XAttribute("Name", p)))));
            XElement Connector(int id, string pin) => new XElement(Ns + "NameCon", new XAttribute("UId", id), new XAttribute("Name", pin));
            XElement Ident(int id) => new XElement(Ns + "IdentCon", new XAttribute("UId", id));
            XElement Wire(int id, params XElement[] connectors) => new XElement(Ns + "Wire", new XAttribute("UId", id), connectors);
            var contact = new XElement(Ns + "Part", new XAttribute("Name", "Contact"), new XAttribute("UId", 23));
            if (invert) contact.Add(new XElement(Ns + "Negated", new XAttribute("Name", "operand")));
            return new XElement(Ns + "FlgNet",
                new XElement(Ns + "Parts", Access(21, source), Access(22, destination), contact, new XElement(Ns + "Part", new XAttribute("Name", coil), new XAttribute("UId", 24))),
                new XElement(Ns + "Wires", Wire(25, new XElement(Ns + "Powerrail"), Connector(23, "in")), Wire(26, Ident(21), Connector(23, "operand")), Wire(27, Connector(23, "out"), Connector(24, "in")), Wire(28, Ident(22), Connector(24, "operand"))));
        }
    }
}
