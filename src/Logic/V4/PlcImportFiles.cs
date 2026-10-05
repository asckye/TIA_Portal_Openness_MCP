using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.Logic.V4
{
    public static class PlcImportFiles
    {
        public static IReadOnlyList<PlcImportInput> Read(string release, string tool, PlcImportRequest request, IDictionary<string, Stream> locks)
        {
            bool documents = PlcImportContract.IsDocuments(tool), directory = PlcImportContract.IsDirectory(tool);
            if (!Path.IsPathRooted(request.InputPath)) PlcImportSession.Invalid("inputPath");
            var root = Path.GetFullPath(request.InputPath);
            SafePath(directory || documents ? (FileSystemInfo)new DirectoryInfo(root) : new FileInfo(root));
            var regex = new Regex(request.RegexName, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            var selection = new List<string>();
            if (directory)
            {
                if (request.ImportOrder.Length == 0) PlcImportSession.Invalid("importOrder");
                int entries = 0, dirs = 0;
                void Scan(DirectoryInfo dir)
                {
                    if (++dirs > 1024) PlcImportSession.Invalid("directory-budget");
                    foreach (var item in dir.EnumerateFileSystemInfos())
                    {
                        if (++entries > 4096) PlcImportSession.Invalid("entry-budget"); SafePath(item);
                        if (item is DirectoryInfo child) { if (tool == "ImportPlcProgramFromDirectory") Scan(child); continue; }
                        if (item.Extension.Equals(documents ? ".s7dcl" : ".xml", StringComparison.OrdinalIgnoreCase) && regex.IsMatch(item.Name))
                            selection.Add(documents ? Path.GetFileNameWithoutExtension(item.Name) : item.FullName.Substring(root.TrimEnd('\\', '/').Length + 1).Replace('\\', '/'));
                        if (selection.Count > request.MaxItems) PlcImportSession.Refuse("The input selection exceeds maxItems; nothing is truncated.", new LimitExceededDetails("maxItems", request.MaxItems, selection.Count));
                    }
                }
                Scan(new DirectoryInfo(root));
                if (request.ImportOrder.Length != selection.Count || request.ImportOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selection.Count
                    || !request.ImportOrder.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(selection.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal)) PlcImportSession.Invalid("importOrder");
                selection = request.ImportOrder.ToList();
            }
            else selection.Add(documents ? request.FileNameWithoutExtension : root);
            var result = new List<PlcImportInput>();
            byte[] Bytes(string path)
            {
                path = Path.GetFullPath(path); SafePath(new FileInfo(path));
                if (!locks.TryGetValue(path, out var stream)) { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); locks.Add(path, stream); }
                return PlcImportSession.Read(stream);
            }
            foreach (var selected in selection)
            {
                if (documents)
                {
                    if (release != "20" && release != "21") PlcImportSession.Unsupported(release, "documents");
                    if (!Regex.IsMatch(selected, @"^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)) PlcImportSession.Invalid("document-name");
                }
                var path = documents ? Path.Combine(root, selected + ".s7dcl") : directory ? Path.Combine(root, selected.Replace('/', Path.DirectorySeparatorChar)) : selected;
                var bytes = Bytes(path);
                PlcImportObject target; string contentHash; string[] files;
                if (documents)
                {
                    var text = Text(bytes);
                    var declaration = Regex.Matches(text, @"(?m)^\s*(FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK)\s+""?([A-Za-z_][A-Za-z0-9_]*)""?\b", RegexOptions.CultureInvariant);
                    if (declaration.Count != 1 || declaration[0].Groups[2].Value != selected) PlcImportSession.Invalid("document-declaration");
                    var kind = declaration[0].Groups[1].Value;
                    target = new PlcImportObject { Name = selected, Kind = kind == "FUNCTION" ? "FC" : kind == "FUNCTION_BLOCK" ? "FB" : kind == "ORGANIZATION_BLOCK" ? "OB" : "GlobalDB", GroupPath = request.BlockGroupPath };
                    string resource = Path.Combine(root, selected + ".s7res");
                    bool exists = File.Exists(resource);
                    if (release == "20" && !exists) PlcImportSession.Invalid("document-resource");
                    files = exists ? new[] { path, resource } : new[] { path };
                    var resourceBytes = exists ? Bytes(resource) : null;
                    contentHash = DocumentHash(bytes, resourceBytes);
                }
                else
                {
                    if (!Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)) PlcImportSession.Invalid("inputPath");
                    var document = Xml(bytes);
                    var native = Object(document);
                    var producer = document.Root!.Elements("Engineering").SingleOrDefault();
                    if ((string?)producer?.Attribute("version") != (release == "14sp1" ? "V14 SP1" : "V" + release)) PlcImportSession.Unsupported(release, "exact-producer");
                    string kind = native.Name.LocalName.Substring(native.Name.LocalName.LastIndexOf('.') + 1);
                    if (kind == "PlcStruct") kind = "UDT"; if (kind == "PlcTagTable") kind = "TagTable";
                    if (!new[] { "FC", "FB", "OB", "GlobalDB", "InstanceDB", "UDT", "TagTable" }.Contains(kind)) PlcImportSession.Unsupported(release, "xml-object-kind");
                    if (tool != "ImportPlcProgramFromDirectory" && (tool.Contains("Type") ? kind != "UDT" : tool.Contains("Tag") ? kind != "TagTable" : kind == "UDT" || kind == "TagTable")) PlcImportSession.Invalid("xml-object-kind");
                    var attrs = native.Elements("AttributeList").Single();
                    string name = attrs.Elements("Name").Single().Value;
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 128) PlcImportSession.Invalid("xml-object-name");
                    var numberText = attrs.Elements("Number").SingleOrDefault()?.Value;
                    int? number = null;
                    if (numberText != null && kind != "UDT" && kind != "TagTable") { if (!int.TryParse(numberText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value)) PlcImportSession.Invalid("xml-number"); number = value; }
                    if (kind != "UDT" && kind != "TagTable" && release == "14sp1" && attrs.Elements("ProgrammingLanguage").Any(e => e.Value == "SCL")) PlcImportSession.Unsupported(release, "interface-only-scl");
                    target = new PlcImportObject { Name = name, Kind = kind, Number = number, GroupPath = kind == "UDT" ? request.TypeGroupPath : kind == "TagTable" ? request.TagFolderPath : request.BlockGroupPath };
                    files = new[] { path }; contentHash = XmlHash(bytes);
                }
                result.Add(new PlcImportInput { Path = path, Files = files, Target = target, ContentHash = contentHash, Documents = documents });
            }
            return result;
        }

        public static void SafePath(FileSystemInfo entry)
        {
            for (FileSystemInfo? current = entry; current != null; current = current is DirectoryInfo dir ? dir.Parent : ((FileInfo)current).Directory)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) PlcImportSession.Invalid("reparse-point");
        }
        public static XDocument Xml(byte[] bytes)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 };
            using (var stream = new MemoryStream(bytes, false)) using (var reader = XmlReader.Create(stream, settings))
                while (reader.Read()) if (reader.Depth > 64) PlcImportSession.Invalid("xml-depth");
            using var input = new MemoryStream(bytes, false); using var xml = XmlReader.Create(input, settings);
            return XDocument.Load(xml, LoadOptions.PreserveWhitespace);
        }
        private static XElement Object(XDocument document)
        {
            if (document.Root?.Name != XName.Get("Document")) PlcImportSession.Invalid("xml-root");
            return document.Root!.Elements().Where(e => e.Name.LocalName != "Engineering" && e.Name.LocalName != "DocumentInfo").Single();
        }
        public static string XmlHash(byte[] bytes)
        {
            var document = Xml(bytes); var original = Object(document);
            if (original.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal))
                return PlcImportSession.ByteHash(Encoding.UTF8.GetBytes(PlcDocumentEditing.Canonical(document)));
            // UDT/tag content keeps all values and attributes; only object IDs and display dates normalize.
            var node = new XElement(original); var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in node.DescendantsAndSelf().Attributes("ID")) { if (ids.ContainsKey(id.Value)) PlcImportSession.Invalid("duplicate-object-id"); ids.Add(id.Value, "o" + ids.Count); }
            foreach (var id in node.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName == "ID" || a.Name.LocalName == "RefId")) if (ids.TryGetValue(id.Value, out string? value)) id.Value = value;
            node.Elements("AttributeList").Elements().Where(e => new[] { "ModifiedDate", "CompileDate", "CreationDate" }.Contains(e.Name.LocalName)).Remove();
            foreach (var element in node.DescendantsAndSelf())
            {
                if (element.HasElements && !element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))
                    && !element.AncestorsAndSelf().Any(e => (string?)e.Attribute(XNamespace.Xml + "space") == "preserve")) element.Nodes().OfType<XText>().Remove();
                var attributes = element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToArray(); element.RemoveAttributes(); element.Add(attributes);
            }
            return PlcImportSession.ByteHash(Encoding.UTF8.GetBytes(node.ToString(SaveOptions.DisableFormatting)));
        }
        private static string Text(byte[] bytes) { using var reader = new StreamReader(new MemoryStream(bytes, false), new UTF8Encoding(false, true), true); return reader.ReadToEnd().Replace("\r\n", "\n").Replace("\r", "\n"); }
        public static string DocumentHash(byte[] code, byte[]? resource) => DeviceCreationSession.Hash(new { code = Text(code), resource = resource == null ? null : Text(resource) });
    }
}
