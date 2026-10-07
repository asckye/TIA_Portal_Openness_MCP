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

        internal static FileInfo XmlInput(string path) => XmlInput(path, out _);
        internal static FileInfo XmlInput(string path, out XDocument document)
        {
            // Openness receives the FileInfo as constructed: V14 SP1 refuses a caller spelling with forward slashes
            // ("The argument 'path' cannot be a specific path"), so pass the normalized full path.
            var file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists || !file.Extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
                throw new AdapterPreconditionException("An existing Openness XML file is required.", "importPath");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 };
            using (var reader = XmlReader.Create(file.FullName, settings))
            {
                try { document = XDocument.Load(reader); }
                catch (XmlException ex) { throw new AdapterPreconditionException("Import input must be valid Openness XML.", "importPath", true, ex); }
                if (document.Root == null || document.Root.Name.LocalName != "Document")
                    throw new AdapterPreconditionException("Expected an Openness Document root; no format/version conversion is performed.", "importPath");
            }
            return file;
        }

        internal static void RequireImportAvailable(XDocument document, string prefix, bool overwrite, Func<string, bool> exists)
        {
            if (overwrite) return;
            var objects = document.Root!.Elements().Where(e => e.Name.LocalName.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (objects.Length == 0) throw new AdapterPreconditionException("Import input has no supported target object.", "importPath");
            foreach (var item in objects)
            {
                var names = item.Elements().Where(e => e.Name.LocalName == "AttributeList")
                    .Elements().Where(e => e.Name.LocalName == "Name").ToArray();
                if (names.Length != 1 || string.IsNullOrWhiteSpace(names[0].Value))
                    throw new AdapterPreconditionException("Import input requires an explicit object Name.", "importPath");
                if (exists(names[0].Value)) throw new AdapterPreconditionException("Object '" + names[0].Value + "' already exists; overwrite=false refuses replacement.", "overwrite");
            }
        }

        internal static bool SymbolExists<T>(System.Collections.Generic.IEnumerable<T> items, Func<string, T?> find, Func<T, string> name, string selected) where T : class
        {
            if (find(selected) != null) return true;
            // SDK Find forwards the spelling to TIA; its XML does not promise casing.
            // Probe the selected composition with one existing name. If it cannot
            // demonstrate case-insensitive lookup, retain the conservative scan.
            using (var enumerator = items.GetEnumerator())
            {
                if (!enumerator.MoveNext()) return false;
                string sample = name(enumerator.Current);
                string alternate = sample.ToUpperInvariant();
                if (alternate == sample) alternate = sample.ToLowerInvariant();
                if (alternate != sample)
                {
                    var match = find(alternate);
                    if (match != null && string.Equals(name(match), sample, StringComparison.OrdinalIgnoreCase)) return false;
                }
            }
            return items.Any(item => string.Equals(name(item), selected, StringComparison.OrdinalIgnoreCase));
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
