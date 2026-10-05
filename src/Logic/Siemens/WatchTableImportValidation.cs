using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcpServer.Siemens
{
    internal static class WatchTableImportValidation
    {
        internal static int Validate(string file)
        {
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
            var xml = XDocument.Load(reader);
            if (xml.Root?.Name.LocalName != "Document") throw new ArgumentException("Expected a native SimaticML Document.");
            if (xml.Descendants().Any(e => e.Name.LocalName.IndexOf("PlcForce", StringComparison.OrdinalIgnoreCase) >= 0 || e.Name.LocalName.StartsWith("Force", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Force table content is not permitted.");
            var objects = xml.Root.Elements().Where(e => e.Attribute("ID") != null).ToArray();
            if (objects.Length == 0 || objects.Any(e => e.Name.LocalName != "SW.WatchAndForceTables.PlcWatchTable"))
                throw new ArgumentException("Only native PLC watch table objects are accepted; mixed payloads refused.");
            return objects.Length;
        }
    }
}
