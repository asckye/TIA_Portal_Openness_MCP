using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Adapters.Contracts
{
    public static class WatchTableImportValidation
    {
        public static int Validate(string file)
            => Validate(file, "watch");

        public static int Validate(string file, string tableKind)
            => ValidateNames(file, tableKind).Length;

        public static string[] ValidateNames(string file, string tableKind = "watch")
        {
            if (tableKind != "watch" && tableKind != "force") throw new ArgumentException("tableKind must be watch or force.", nameof(tableKind));
            using var reader = XmlReader
                .Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
            var xml = XDocument.Load(reader);
            if (xml.Root?.Name.LocalName != "Document") throw new ArgumentException("Expected a native SimaticML Document.");
            if (tableKind == "watch" && xml.Descendants()
                    .Any(e => e.Name.LocalName.IndexOf("PlcForce", StringComparison.OrdinalIgnoreCase) >= 0 || e.Name.LocalName
                        .StartsWith("Force", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Force table content is not permitted.");
            var objects = xml.Root.Elements().Where(e => e.Attribute("ID") != null).ToArray();
            string objectType = "SW.WatchAndForceTables.Plc" + (tableKind == "watch" ? "Watch" : "Force") + "Table";
            if (objects.Length == 0 || objects.Any(e => e.Name.LocalName != objectType))
                throw new ArgumentException("Only native PLC " + tableKind + " table objects are accepted; mixed payloads refused.");
            var names = objects.Select(table => table.Elements().Where(e => e.Name.LocalName == "AttributeList")
                .SelectMany(list => list.Elements().Where(e => e.Name.LocalName == "Name")).ToArray()).ToArray();
            if (names.Any(items => items.Length != 1 || string.IsNullOrWhiteSpace(items[0].Value)))
                throw new ArgumentException("Each native PLC table must have one nonempty AttributeList/Name.");
            if (names.Any(items => items[0].Value != items[0].Value.Trim()))
                throw new ArgumentException("Table names cannot have leading or trailing whitespace.");
            return names.Select(items => items[0].Value).ToArray();
        }
    }
}
