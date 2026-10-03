using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using TiaMcp.Versioning;

namespace TiaMcp.PlcFoundation
{
    // Pure request validation, linked into the offline suite without Siemens references.
    internal static class PlcFoundationPolicy
    {
        internal static void RequireRelease(string compiledKey, string requestedKey)
        {
            TiaVersionCatalog.Get(requestedKey);
            if (!string.Equals(compiledKey, requestedKey, StringComparison.Ordinal))
                throw new InvalidOperationException("A separately compiled PLC foundation module is required for " + requestedKey + ".");
        }

        internal static string Segment(string name) => Uri.EscapeDataString(name);

        internal static string RequireName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("/") || name.Contains("\\"))
                throw new ArgumentException("A nonempty single object name is required.", nameof(name));
            return name;
        }

        internal static FileInfo XmlInput(string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists || !file.Extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("An existing Openness XML file is required.", nameof(path));
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 };
            using (var reader = XmlReader.Create(file.FullName, settings))
            {
                var document = XDocument.Load(reader);
                if (document.Root == null || document.Root.Name.LocalName != "Document")
                    throw new ArgumentException("Expected an Openness Document root; no format/version conversion is performed.", nameof(path));
            }
            return file;
        }

        internal static FileInfo XmlOutput(string path)
        {
            var file = new FileInfo(path);
            if (!file.Extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
                file.Directory == null || !file.Directory.Exists || file.Exists)
                throw new ArgumentException("Choose a new .xml file in an existing output directory; existing files are never overwritten.", nameof(path));
            return file;
        }

        internal static T Exact<T>(System.Collections.Generic.IEnumerable<T> items, Func<T, string> path, string selected)
        {
            if (selected == null) throw new ArgumentNullException(nameof(selected));
            var matches = items.Where(item => string.Equals(path(item), selected, StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length == 0) throw new InvalidOperationException("Object not found at exact path: " + selected);
            if (matches.Length != 1) throw new InvalidOperationException("Ambiguous object path: " + selected);
            return matches[0];
        }
    }

}
