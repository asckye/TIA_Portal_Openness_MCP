using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.LegacyHost;

// No Siemens dependency. Never call the production path helper: it scans and reparses files.
internal static class OfflineSymbolManifest
{
    internal const int MaxFiles = 32, MaxFileBytes = 1024 * 1024, MaxTotalBytes = 8 * 1024 * 1024;
    internal const int MaxDepth = 48, MaxTextCharacters = 1024 * 1024, MaxNodes = 50000, MaxSymbols = 10000;
    internal const string ExpectedOrigin = "caller-owned-immutable-export-snapshot";
    internal const string InterfaceNamespace = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
    private static readonly HashSet<string> ElementaryTypes = new(StringComparer.OrdinalIgnoreCase)
    { "Bool", "Byte", "Word", "DWord", "LWord", "SInt", "Int", "DInt", "LInt", "USInt", "UInt", "UDInt", "ULInt", "Real", "LReal", "Char", "WChar", "Time", "LTime", "Date", "Time_Of_Day", "LTime_Of_Day", "Date_And_Time", "DTL", "String", "WString" };

    internal static JsonObject Build(string inputRoot, IReadOnlyList<string> files, string expectedOrigin, CancellationToken cancellationToken = default)
    {
        var errors = new JsonArray(); var sources = new JsonArray(); var symbols = new JsonArray(); var references = new JsonArray();
        var result = new JsonObject
        {
            ["format"] = "tia-plc-symbol-manifest-bounded-v1", ["offlineOnly"] = true,
            ["wholeXmlSchemaValidated"] = false, ["importValidated"] = false, ["programSemanticsValidated"] = false,
            ["nativeCertified"] = false, ["referencesResolved"] = false, ["symbolInventoryComplete"] = false,
            ["warnings"] = new JsonArray("Declaration extraction only; exports may omit defaults. Array subelements and external type definitions are not expanded. Dot-joined display names are not certified TIA binding identities."), ["symbols"] = symbols, ["files"] = sources,
            ["unresolvedReferences"] = references, ["errors"] = errors,
            ["scope"] = "Explicit files in a caller-owned immutable snapshot. Paths are not a sandbox against concurrent filesystem mutation. No directory enumeration, imports, writes, network or worker calls."
        };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(expectedOrigin == ExpectedOrigin, "input-origin-required");
            Require(!string.IsNullOrWhiteSpace(inputRoot) && inputRoot.Length <= 4096 && Path.IsPathFullyQualified(inputRoot), "absolute-input-root-required");
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputRoot));
            Require(Directory.Exists(root) && root != Path.GetPathRoot(root), "invalid-input-root");
            CheckChain(root);
            Require(files is not null && files.Count is > 0 and <= MaxFiles, "file-count-limit");
            var selected = new List<(string Relative, string Full)>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in files!)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(relative is not null && relative.Length is > 0 and <= 512 && !Path.IsPathRooted(relative), "invalid-relative-path");
                // Portable slash-only spelling prevents platform-specific aliases, ADS, traversal and case collisions.
                Require(!relative!.Any(c => c < 32 || "\\:*?\"<>|".Contains(c)), "invalid-relative-path");
                var parts = relative!.Split('/');
                Require(parts.Length <= 16 && parts.All(p => p.Length > 0 && p != "." && p != ".." && p == p.Trim() && !p.EndsWith('.')), "invalid-relative-path");
                Require(relative.EndsWith(".xml", StringComparison.OrdinalIgnoreCase), "xml-extension-required");
                Require(names.Add(relative), "duplicate-or-case-colliding-path");
                var full = Path.GetFullPath(Path.Combine(root, relative));
                Require(full.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "path-outside-root");
                CheckChain(full);
                Require(File.Exists(full) && (File.GetAttributes(full) & (FileAttributes.Directory | FileAttributes.Device)) == 0, "regular-file-required");
                selected.Add((relative, full));
            }
            long total = 0;
            foreach (var file in selected.OrderBy(x => x.Relative, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = new JsonObject { ["path"] = file.Relative, ["ok"] = false }; sources.Add(source);
                try
                {
                    CheckChain(file.Full);
                    var before = new FileInfo(file.Full); var initialLength = before.Length; var initialWrite = before.LastWriteTimeUtc;
                    // Export XML is nonempty. Reject before open: common Unix FIFOs/devices/sockets report zero length and opening may block.
                    // This is not proof of regular-file type and does not close concurrent replacement races.
                    Require(initialLength > 0, "empty-or-special-file-rejected");
                    using var stream = new FileStream(file.Full, FileMode.Open, FileAccess.Read, FileShare.Read);
                    Require(stream.Length == initialLength, "source-changed-during-read");
                    Require(stream.Length <= MaxFileBytes, "file-byte-limit");
                    Require(total + stream.Length <= MaxTotalBytes, "total-byte-limit");
                    using var buffer = new MemoryStream(); var chunk = new byte[8192]; int read;
                    while ((read = stream.Read(chunk, 0, chunk.Length)) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); total += read;
                        Require(buffer.Length + read <= MaxFileBytes, "file-byte-limit");
                        Require(total <= MaxTotalBytes, "total-byte-limit"); buffer.Write(chunk, 0, read);
                    }
                    CheckChain(file.Full);
                    var after = new FileInfo(file.Full);
                    Require(buffer.Length == initialLength && stream.Length == initialLength && after.Length == initialLength && after.LastWriteTimeUtc == initialWrite, "source-changed-during-read");
                    var bytes = buffer.ToArray(); source["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); source["bytes"] = bytes.Length;
                    var document = Parse(bytes, cancellationToken);
                    var extracted = Extract(document, file.Relative);
                    Require(symbols.Count + extracted.Symbols.Count <= MaxSymbols, "symbol-count-limit");
                    source["engineeringVersion"] = extracted.Version;
                    source["engineeringVersionStatus"] = new[] { "V14", "V14 SP1", "V15", "V15.1", "V16", "V17", "V18", "V19", "V20", "V21" }.Contains(extracted.Version) ? "recognized-marker-only" : "unknown-or-missing-marker";
                    source["interfaceNamespace"] = extracted.InterfaceNamespace;
                    source["versionCompatibility"] = "not-evaluated; observed metadata only";
                    foreach (var symbol in extracted.Symbols) symbols.Add(symbol);
                    foreach (var reference in extracted.References) references.Add(reference);
                    source["ok"] = true;
                }
                catch (OperationCanceledException) { throw; }
                catch (ManifestInputException ex) { source["error"] = ex.Code; errors.Add(new JsonObject { ["file"] = file.Relative, ["code"] = ex.Code }); }
                catch (XmlException) /* swallow(parse-fallback): malformed or prohibited XML is reported per file using the stable manifest error code */ { source["error"] = "invalid-or-prohibited-xml"; errors.Add(new JsonObject { ["file"] = file.Relative, ["code"] = "invalid-or-prohibited-xml" }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { source["error"] = "file-read-failed"; errors.Add(new JsonObject { ["file"] = file.Relative, ["code"] = "file-read-failed" }); }
                if (total > MaxTotalBytes) break;
            }
            // Preserve every occurrence. Even identical duplicates are visible and make the manifest non-unique.
            foreach (var group in symbols.OfType<JsonObject>().GroupBy(x => x["symbol"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                errors.Add(new JsonObject { ["code"] = "duplicate-or-case-colliding-symbol", ["symbol"] = group.Key, ["occurrences"] = group.Count() });
        }
        catch (OperationCanceledException) { throw; }
        catch (ManifestInputException ex) { errors.Add(new JsonObject { ["code"] = ex.Code }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { errors.Add(new JsonObject { ["code"] = "invalid-or-unreadable-input-scope" }); }
        result["ok"] = errors.Count == 0 && symbols.Count > 0;
        result["symbolCount"] = symbols.Count; result["fileCount"] = sources.Count;
        return result;
    }

    private static void CheckChain(string full)
    {
        for (var path = full; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "symlink-or-reparse-point-rejected");
    }
    private static XDocument Parse(byte[] bytes, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxTextCharacters, MaxCharactersFromEntities = 1, IgnoreComments = false, IgnoreProcessingInstructions = false };
        // The bounded first pass checks depth/node/attribute limits before allocating an XDocument tree.
        using (var stream = new MemoryStream(bytes))
        using (var reader = XmlReader.Create(stream, settings))
        {
            int nodes = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(reader.Depth <= MaxDepth && ++nodes <= MaxNodes && reader.AttributeCount <= 64, "xml-complexity-limit");
                Require(reader.Value.Length <= 16384, "xml-text-limit");
                if (reader.HasAttributes) { while (reader.MoveToNextAttribute()) Require(reader.Value.Length <= 1024, "xml-attribute-limit"); reader.MoveToElement(); }
            }
        }
        using var input = new MemoryStream(bytes); using var bounded = XmlReader.Create(input, settings);
        return XDocument.Load(bounded, LoadOptions.None);
    }
    private static (List<JsonObject> Symbols, List<JsonObject> References, string Version, string InterfaceNamespace) Extract(XDocument document, string file)
    {
        var root = document.Root; Require(root?.Name == XName.Get("Document"), "unsupported-document-root");
        var versionNodes = root!.Elements("Engineering").ToArray(); Require(versionNodes.Length <= 1, "ambiguous-engineering-version");
        string version = Field(versionNodes.SingleOrDefault()?.Attribute("version")?.Value ?? "");
        var objects = root.Elements().Where(x => x.Name == "SW.Tags.PlcTagTable" || x.Name == "SW.Blocks.GlobalDB").ToArray();
        Require(objects.Length > 0, "unsupported-document-kind");
        Require(root.Elements().All(x => x.Name == "Engineering" || x.Name == "DocumentInfo" || objects.Contains(x)), "unsupported-document-object");
        var output = new List<JsonObject>(); var references = new List<JsonObject>(); string observedNamespace = "";
        var consumedDeclarations = new HashSet<XElement>(objects);
        void Add(string name, string type, string kind, string address = "")
        {
            Require(output.Count < MaxSymbols, "symbol-count-limit");
            Require(!string.IsNullOrWhiteSpace(type), "missing-datatype");
            var symbol = new JsonObject { ["symbol"] = Field(name), ["dataType"] = Field(type), ["sourceKind"] = kind, ["sourceFile"] = file, ["logicalAddress"] = Field(address) }; output.Add(symbol);
            if (!ElementaryTypes.Contains(type) && !string.Equals(type, "Struct", StringComparison.OrdinalIgnoreCase))
                references.Add(new JsonObject { ["symbol"] = name, ["dataType"] = type, ["sourceFile"] = file, ["status"] = "unresolved-type-or-compound-declaration" });
        }
        foreach (var obj in objects)
        {
            if (obj.Name == "SW.Tags.PlcTagTable")
            {
                Require(obj.Elements().All(x => x.Name == "AttributeList" || x.Name == "ObjectList") && obj.Elements("AttributeList").Count() <= 1, "unsupported-tag-table-shape");
                var list = Single(obj, "ObjectList");
                Require(list.Elements().All(x => x.Name == "SW.Tags.PlcTag"), "unsupported-tag-list-shape");
                foreach (var tag in list.Elements())
                {
                    consumedDeclarations.Add(tag);
                    Require(tag.Elements().Count() == 1, "unsupported-tag-shape");
                    var attrs = Single(tag, "AttributeList");
                    Require(attrs.Elements().All(x => (x.Name == "Name" || x.Name == "DataTypeName" || x.Name == "LogicalAddress") && !x.HasElements), "unsupported-tag-field");
                    Require(attrs.Elements().GroupBy(x => x.Name).All(g => g.Count() == 1), "duplicate-tag-field");
                    Add(Symbol(Single(attrs, "Name").Value), Single(attrs, "DataTypeName").Value, "PlcTag", attrs.Element("LogicalAddress")?.Value ?? "");
                }
            }
            else
            {
                var attrs = Single(obj, "AttributeList"); var dbNameElement = Single(attrs, "Name");
                Require(!dbNameElement.HasElements, "unsupported-db-name-shape");
                var dbName = Symbol(dbNameElement.Value);
                var iface = Single(attrs, "Interface");
                Require(iface.Elements().Count() == 1 && iface.Elements().Single().Name == XName.Get("Sections", InterfaceNamespace), "unsupported-interface-namespace-or-version");
                var sections = iface.Elements().Single(); observedNamespace = InterfaceNamespace;
                Require(sections.Descendants().All(x => x.Name.NamespaceName == InterfaceNamespace), "unsupported-interface-namespace");
                Require(sections.Elements().Count() == 1 && sections.Elements().Single().Name == XName.Get("Section", InterfaceNamespace) && (string?)sections.Elements().Single().Attribute("Name") == "Static", "unsupported-or-nonstatic-section");
                var section = sections.Elements().Single(); consumedDeclarations.Add(section);
                Require(section.Elements().All(x => x.Name == XName.Get("Member", InterfaceNamespace)), "unsupported-static-member-shape");
                void Member(XElement member, string prefix)
                {
                    consumedDeclarations.Add(member);
                    Require(member.Elements().All(x => x.Name == XName.Get("Member", InterfaceNamespace) || x.Name == XName.Get("AttributeList", InterfaceNamespace) || x.Name == XName.Get("StartValue", InterfaceNamespace) || x.Name == XName.Get("Comment", InterfaceNamespace)), "unsupported-nested-member-shape");
                    var name = Symbol(member.Attribute("Name")?.Value ?? ""); var path = prefix + "." + name;
                    Add(path, member.Attribute("Datatype")?.Value ?? "", "GlobalDBMember");
                    foreach (var child in member.Elements(XName.Get("Member", InterfaceNamespace))) Member(child, path);
                }
                foreach (var member in section.Elements(XName.Get("Member", InterfaceNamespace))) Member(member, dbName);
            }
        }
        // Declaration-like nodes hidden in metadata, wrappers or wrong-case names cannot disappear silently.
        var declarationNames = new HashSet<string>(new[] { "SW.Tags.PlcTagTable", "SW.Tags.PlcTag", "SW.Blocks.GlobalDB", "Member", "Section" }, StringComparer.OrdinalIgnoreCase);
        Require(root.Descendants().Where(x => declarationNames.Contains(x.Name.LocalName)).All(consumedDeclarations.Contains), "unsupported-hidden-declaration");
        Require(output.Count > 0, "no-symbols-in-file");
        return (output, references, version, observedNamespace);
    }
    private static XElement Single(XElement parent, XName name)
    { var elements = parent.Elements(name).ToArray(); Require(elements.Length == 1, "missing-ambiguous-or-unsupported-structure"); return elements[0]; }
    private static string Field(string value) { Require(value.Length <= 1024, "field-text-limit"); return value; }
    private static string Symbol(string value)
    {
        // Preserve decoded declaration spelling exactly. Trimming/quote removal would merge distinct input names.
        value = Field(value);
        Require(!string.IsNullOrWhiteSpace(value), "empty-symbol-name"); return value;
    }
    private static void Require(bool condition, string code) { if (!condition) throw new ManifestInputException(code); }
    private sealed class ManifestInputException(string code) : Exception { internal string Code { get; } = code; }
}
