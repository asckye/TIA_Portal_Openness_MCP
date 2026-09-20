using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace TiaMcpServer.Siemens
{
    // 2.7.50 (real machine + PublicAPI): a watch table entry with a tag cannot be created through the typed API -
    // PlcWatchTable.Entries is a PlcTableCommentEntryComposition whose Create() yields a comment row that refuses
    // Address / Name / ModifyValue ("'Address' is not supported by type PlcTableCommentEntry"). The official way to add
    // or change a tag row is the SimaticML round trip: export the table, edit the XML, import it with ImportOptions.Override.
    // This class holds the pure XML part so the offline suite can cover it; the Openness calls stay in Portal.Software.cs.
    internal static class WatchTableEntryXml
    {
        internal const string TableElement = "SW.WatchAndForceTables.PlcWatchTable";
        internal const string EntryElement = "SW.WatchAndForceTables.PlcWatchTableEntry";

        // Which attribute a caller's "address" belongs to: absolute addresses go to Address, symbols to Name.
        internal static string AttributeFor(string address) => PlcBlockServicesLogic.WatchEntryAddressAttribute(address);

        // TIA writes symbols quoted per segment: "Tag", "DB".Member, "DB".Struct.Member. A bare Tag / Struct.Member is quoted the same way.
        internal static string QuoteSymbol(string symbol)
        {
            var text = (symbol ?? "").Trim();
            if (text.Length == 0 || text.StartsWith("\"")) return text;
            var dot = text.IndexOf('.');
            return dot < 0 ? "\"" + text + "\"" : "\"" + text.Substring(0, dot) + "\"" + text.Substring(dot);
        }

        internal static bool SameSymbol(string a, string b)
            => string.Equals(QuoteSymbol(a), QuoteSymbol(b), StringComparison.OrdinalIgnoreCase);

        // A minimal SimaticML document for a new watch table (no entries yet).
        internal static XDocument NewTable(string tableName, int engineeringMajorVersion)
        {
            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Document",
                    new XElement("Engineering", new XAttribute("version", "V" + engineeringMajorVersion)),
                    new XElement(TableElement, new XAttribute("ID", "0"),
                        new XElement("AttributeList", new XElement("Name", tableName)),
                        new XElement("ObjectList"))));
        }

        internal static XElement Table(XDocument doc)
            => doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == TableElement)
               ?? throw new ArgumentException("The XML holds no " + TableElement + " object.");

        internal static string TableName(XDocument doc)
            => Table(doc).Element("AttributeList")?.Element("Name")?.Value ?? "";

        internal static IEnumerable<XElement> Entries(XDocument doc)
            => Table(doc).Element("ObjectList")?.Elements().Where(e => e.Name.LocalName == EntryElement) ?? Enumerable.Empty<XElement>();

        internal static XElement? FindEntry(XDocument doc, string address)
        {
            var attribute = AttributeFor(address);
            foreach (var entry in Entries(doc))
            {
                var list = entry.Element("AttributeList");
                var value = list?.Element(attribute)?.Value ?? "";
                if (value.Length == 0) continue;
                if (attribute == "Address" ? string.Equals(value, address.Trim(), StringComparison.OrdinalIgnoreCase) : SameSymbol(value, address)) return entry;
            }
            return null;
        }

        // Adds or updates the row for `address` and returns "updated" / "appended". Attribute order follows TIA's alphabetical export.
        internal static string Upsert(XDocument doc, string address, string modifyValue, string trigger)
        {
            var attribute = AttributeFor(address);
            var value = attribute == "Name" ? QuoteSymbol(address) : address.Trim();
            var entry = FindEntry(doc, address);
            string action;
            if (entry == null)
            {
                var objects = Table(doc).Element("ObjectList");
                if (objects == null) { objects = new XElement("ObjectList"); Table(doc).Add(objects); }
                entry = new XElement(EntryElement, new XAttribute("ID", NextId(doc)), new XAttribute("CompositionName", "Entries"), new XElement("AttributeList"));
                objects.Add(entry);
                action = "appended";
            }
            else action = "updated";
            var list = entry.Element("AttributeList")!;
            if (action == "appended") Set(list, attribute, value);   // an existing row keeps TIA's own spelling of the address / symbol
            Set(list, "ModifyIntention", "false");
            Set(list, "ModifyTrigger", trigger);
            Set(list, "ModifyValue", modifyValue);
            if (list.Element("MonitorTrigger") == null) Set(list, "MonitorTrigger", "Permanent");
            Reorder(list);
            return action;
        }

        // SimaticML IDs are hexadecimal; the next free one keeps every existing ID (comment rows carry MultilingualText children with IDs too).
        internal static string NextId(XDocument doc)
        {
            long max = -1;
            foreach (var id in doc.Descendants().Select(e => (string?)e.Attribute("ID")).Where(v => !string.IsNullOrEmpty(v)))
                if (long.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) && n > max) max = n;
            return (max + 1).ToString("X", CultureInfo.InvariantCulture);
        }

        private static void Set(XElement list, string name, string value)
        {
            var element = list.Element(name);
            if (element == null) list.Add(new XElement(name, value)); else element.Value = value;
        }

        private static void Reorder(XElement list)
        {
            var ordered = list.Elements().OrderBy(e => e.Name.LocalName, StringComparer.Ordinal).ToList();
            list.RemoveNodes();
            foreach (var e in ordered) list.Add(e);
        }
    }
}
