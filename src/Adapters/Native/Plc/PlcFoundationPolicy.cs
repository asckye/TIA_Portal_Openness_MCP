using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using TiaMcp.Adapters.Contracts;
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
                throw new AdapterPreconditionException("A nonempty single object name is required.", nameof(name));
            return name;
        }

        internal static FileInfo XmlInput(string path)
        {
            // Openness receives the FileInfo as constructed: V14 SP1 refuses a caller spelling with forward slashes
            // ("The argument 'path' cannot be a specific path"), so pass the normalized full path.
            var file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists || !file.Extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
                throw new AdapterPreconditionException("An existing Openness XML file is required.", "importPath");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 };
            using (var reader = XmlReader.Create(file.FullName, settings))
            {
                XDocument document;
                try { document = XDocument.Load(reader); }
                catch (XmlException ex) { throw new AdapterPreconditionException("Import input must be valid Openness XML.", "importPath", true, ex); }
                if (document.Root == null || document.Root.Name.LocalName != "Document")
                    throw new AdapterPreconditionException("Expected an Openness Document root; no format/version conversion is performed.", "importPath");
            }
            return file;
        }

        internal static FileInfo XmlOutput(string path)
        {
            var file = new FileInfo(path);
            if (!file.Extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
                file.Directory == null || !file.Directory.Exists || file.Exists)
                throw new AdapterPreconditionException("Choose a new .xml file in an existing output directory; existing files are never overwritten.", "exportPath");
            return file;
        }

        internal static T Exact<T>(System.Collections.Generic.IEnumerable<T> items, Func<T, string> path, string selected, string parameter = "path")
        {
            if (selected == null) throw new AdapterPreconditionException("A path value is required.", parameter);
            var matches = items.Where(item => string.Equals(path(item), selected, StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length == 0) throw new AdapterPreconditionException("Object not found at exact path: " + selected, parameter);
            if (matches.Length != 1) throw new AdapterPreconditionException("Ambiguous object path: " + selected, parameter);
            return matches[0];
        }
    }

}
