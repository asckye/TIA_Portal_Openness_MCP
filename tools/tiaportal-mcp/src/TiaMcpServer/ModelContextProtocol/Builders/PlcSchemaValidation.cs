using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace TiaMcpServer.ModelContextProtocol
{
    // PublicAPI ships fragment schemas, not a complete SimaticML Document schema.
    public static class PlcSchemaValidation
    {
        static XDocument Read(byte[] bytes)
        {
            if (bytes.Length > 16 * 1024 * 1024) throw new ArgumentException("XML exceeds 16 MiB.");
            using var stream = new MemoryStream(bytes);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
            return XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        static void RegularPath(string path)
        {
            if (!Path.IsPathRooted(path)) throw new ArgumentException("Absolute paths required.");
            if (new Uri(Path.GetFullPath(path)).IsUnc) throw new ArgumentException("Network paths refused; local files required.");
            for (var current = new FileInfo(Path.GetFullPath(path)) as FileSystemInfo; current != null;
                 current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Reparse paths refused.");
        }
        static byte[] ReadBounded(string path, int maximum)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new MemoryStream();
            var buffer = new byte[65536]; int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (output.Length + count > maximum) throw new ArgumentException("File byte budget exceeded.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        sealed class LocalSchemas : XmlResolver
        {
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            public override ICredentials Credentials { set { } }
            public override object GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
            {
                if (!absoluteUri.IsFile || absoluteUri.IsUnc || !Files.TryGetValue(Path.GetFullPath(absoluteUri.LocalPath), out var bytes))
                    throw new XmlException("Only cached XSD files in the supplied schema directory may be resolved.");
                return new MemoryStream(bytes, false);
            }
        }
        public static JsonObject Validate(string filePath, string schemaDirectory)
        {
            RegularPath(filePath); RegularPath(schemaDirectory);
            var file = new FileInfo(filePath);
            if (!file.Exists || file.Length > 16 * 1024 * 1024) throw new ArgumentException("Existing XML <=16 MiB required.");
            var document = Read(ReadBounded(file.FullName, 16 * 1024 * 1024));
            var schemaRoot = new DirectoryInfo(schemaDirectory);
            if (!schemaRoot.Exists) throw new DirectoryNotFoundException("PublicAPI Schemas directory missing.");
            var paths = schemaRoot.EnumerateFiles("*.xsd").Take(129).ToArray();
            if (paths.Length == 0 || paths.Length > 128) throw new ArgumentException("Schema directory must contain 1..128 XSD files.");
            var resolver = new LocalSchemas(); long total = 0;
            foreach (var xsd in paths)
            {
                RegularPath(xsd.FullName);
                var bytes = ReadBounded(xsd.FullName, 4 * 1024 * 1024);
                if ((total += bytes.Length) > 32 * 1024 * 1024) throw new ArgumentException("XSD byte budget exceeded.");
                Read(bytes); // Reject DTDs before the schema resolver sees them.
                resolver.Files.Add(xsd.FullName, bytes);
            }
            var rows = new JsonArray(); var skipped = new JsonArray(); var issues = new JsonArray(); int errorCount = 0, warningCount = 0;
            var nodes = document.Descendants().Where(e => e.Name.NamespaceName.StartsWith("http://www.siemens.com/automation/Openness/SW/", StringComparison.Ordinal)
                && (e.Parent == null || e.Parent.Name.Namespace != e.Name.Namespace)).ToArray();
            if (nodes.Length > 2000) throw new ArgumentException("Fragment budget exceeded.");
            var schemas = new Dictionary<string, XmlSchemaSet>();
            foreach (var node in nodes)
            {
                var ns = node.Name.NamespaceName;
                var prefix = "http://www.siemens.com/automation/Openness/SW/";
                var suffix = ns.Substring(prefix.Length);
                string? stem = null;
                if (Regex.IsMatch(suffix, @"^Interface/v[0-9]+$") && node.Name.LocalName == "Sections") stem = "SW.InterfaceSections_" + suffix.Split('/').Last();
                else if (Regex.IsMatch(suffix, @"^NetworkSource/(FlgNet|SCL|STL|Graph)/v[0-9]+$"))
                {
                    var parts = suffix.Split('/');
                    if (parts[1] == node.Name.LocalName) stem = "SW.PlcBlocks." + (parts[1] == "FlgNet" ? "LADFBD" : parts[1]) + "_" + parts[2];
                }
                var schemaPath = stem == null ? "" : Path.Combine(schemaRoot.FullName, stem + ".xsd");
                if (stem == null || !resolver.Files.ContainsKey(schemaPath)) { skipped.Add(new JsonObject { ["element"] = node.Name.ToString(), ["reason"] = "Exact schema unavailable or fragment unsupported" }); continue; }
                var beforeErrors = errorCount; var beforeWarnings = warningCount;
                void OnIssue(object? sender, ValidationEventArgs args)
                {
                    if (args.Severity == XmlSeverityType.Error) errorCount++; else warningCount++;
                    if (issues.Count < 200) issues.Add(new JsonObject { ["severity"] = args.Severity.ToString(), ["element"] = node.Name.ToString(), ["message"] = args.Message });
                }
                if (!schemas.TryGetValue(ns, out var set))
                {
                    set = new XmlSchemaSet { XmlResolver = resolver };
                    set.ValidationEventHandler += OnIssue;
                    set.Add(ns, schemaPath); set.Compile();
                    set.ValidationEventHandler -= OnIssue;
                    schemas[ns] = set;
                }
                // Validate a copy of this fragment as the document element, retaining namespaces.
                new XDocument(new XElement(node)).Validate(set, OnIssue, false);
                rows.Add(new JsonObject { ["element"] = node.Name.ToString(), ["sourceLine"] = ((IXmlLineInfo)node).LineNumber,
                    ["schema"] = Path.GetFileName(schemaPath), ["schemaSha256"] = PlcDocumentEditing.Hash(resolver.Files[schemaPath]),
                    ["passed"] = errorCount == beforeErrors && warningCount == beforeWarnings });
            }
            return new JsonObject { ["validatedFragments"] = rows, ["skippedFragments"] = skipped, ["issues"] = issues,
                ["errorCount"] = errorCount, ["warningCount"] = warningCount, ["issuesTruncated"] = errorCount + warningCount > issues.Count,
                ["fragmentSchemasPassed"] = rows.Count > 0 && errorCount == 0 && warningCount == 0 && skipped.Count == 0,
                ["wholeDocumentValidated"] = false, ["nativeImportValidated"] = false, ["networkAccessed"] = false,
                ["scope"] = "Only recognized interface/network fragments against supplied local XSDs. No full Document, cross-object semantic, CPU, native import or compilation validation. No missing schema is treated as a pass." };
        }
    }
}
