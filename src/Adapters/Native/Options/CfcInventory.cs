using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Xml;

namespace TiaMcp.Adapters.Native.Options;

internal static class CfcInventory
{
        internal static bool IsChartElement(string localName)
            => localName.IndexOf("Chart", StringComparison.OrdinalIgnoreCase) >= 0 && localName.IndexOf("Folder", StringComparison.OrdinalIgnoreCase) < 0
               && localName.IndexOf("List", StringComparison.OrdinalIgnoreCase) < 0 && !localName.EndsWith("s", StringComparison.OrdinalIgnoreCase);
        internal sealed class ExportInventory { public string[] Charts { get; set; } = Array.Empty<string>(); public string[] Elements { get; set; } = Array.Empty<string>(); public string[] Entries { get; set; } = Array.Empty<string>(); }
        internal static ExportInventory InspectExport(string zipPath)
        {
            var charts = new List<string>(); var elements = new HashSet<string>(StringComparer.Ordinal); var entries = new List<string>();
            using (var archive = ZipFile.OpenRead(zipPath))
                foreach (var entry in archive.Entries)
                {
                    entries.Add(entry.FullName);
                    if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                    using var stream = entry.Open();
                    using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true, IgnoreComments = true });
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element) continue;
                        if (elements.Count < 200) elements.Add(reader.LocalName);
                        if (!IsChartElement(reader.LocalName)) continue;
                        var name = reader.GetAttribute("Name") ?? reader.GetAttribute("name");
                        if (!string.IsNullOrEmpty(name) && !charts.Contains(name, StringComparer.Ordinal)) charts.Add(name);
                    }
                }
            return new ExportInventory { Charts = charts.ToArray(), Elements = elements.OrderBy(e => e, StringComparer.Ordinal).ToArray(), Entries = entries.ToArray() };
        }
}
