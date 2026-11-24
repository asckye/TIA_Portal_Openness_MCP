using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TiaMcp.Adapters.Contracts;

namespace TiaMcpServer.ModelContextProtocol
{
    // Independent implementation using Siemens SimaticML. No third-party product code.
    public static class PlcDocumentEditing
    {
        public const int MaxBytes = 16 * 1024 * 1024;
        private static readonly string[] ComputedBlockAttributes = {
            "CodeModifiedDate", "InterfaceModifiedDate", "ParameterModified", "StructureModified", "LoadMemoryLength", "WorkMemoryLength"
        };
        public static string Hash(byte[] bytes) {
            using var sha = System.Security.Cryptography.SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "")
                .ToLowerInvariant();
        }
        public static string HashText(string text) => Hash(Encoding.UTF8.GetBytes(text));
        public static string Read(string path)
        {
            if (!Path.IsPathRooted(path)) throw new ArgumentException("An absolute XML file path is required.");
            var f = new FileInfo(path);
            if (!f.Exists || f.Length > MaxBytes) throw new ArgumentException("XML file is missing or exceeds 16 MiB.");
            return File.ReadAllText(path, Encoding.UTF8);
        }
        public static XElement Child(XElement e, string name) => e.Elements().Single(x => x.Name.LocalName == name);
        public static string Value(XElement e, string name) => e.Elements().SingleOrDefault(x => x.Name.LocalName == name)?.Value ?? "";
        public static XElement Block(XDocument d) => d.Root!.Elements().Single(x => x.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal)
            || x.Name.LocalName == "SW.Types.PlcStruct");
        public static XDocument Parse(string xml) => ParseDocument(xml, false);
        public static XDocument ParseBlockImport(string xml) => ParseDocument(xml, false, blockImport: true);
        private static XDocument ParseDocument(string xml, bool editable, bool blockImport = false)
        {
            if (Encoding.UTF8.GetByteCount(xml) > MaxBytes) throw new ArgumentException("XML exceeds 16 MiB.");
            using var reader = XmlReader
                .Create(new StringReader(xml), new XmlReaderSettings {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes });
            var d = XDocument.Load(reader, LoadOptions.None);
            if (d.Root?.Name.LocalName != "Document") throw new ArgumentException("One SimaticML Document is required.");
            var objects = d.Root.Elements().Where(x => x.Name.LocalName != "Engineering" && x.Name.LocalName != "DocumentInfo").ToArray();
            if (blockImport && objects.Any(x => x.Name.LocalName.StartsWith("SW.Types.", StringComparison.Ordinal)))
                throw new ArgumentException("ImportPlcBlockVerified imports blocks only; import a UDT with ImportPlcType overwrite.");
            if (objects.Length != 1 || !new[] { "SW.Blocks.FC", "SW.Blocks.FB", "SW.Blocks.OB", "SW.Blocks.GlobalDB", "SW.Blocks.InstanceDB" }
                    .Contains(objects[0].Name.LocalName) && !(editable && objects[0].Name.LocalName == "SW.Types.PlcStruct"))
                throw new ArgumentException("Exactly one supported PLC block per XML file is required.");
            if (string.IsNullOrWhiteSpace(Value(Child(objects[0], "AttributeList"), "Name"))) throw new ArgumentException("Block Name is missing.");
            return d;
        }
        public static string Name(XDocument d) => Value(Child(Block(d), "AttributeList"), "Name");
        public static XElement[] Networks(XDocument d) => Block(d).Descendants().Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit").ToArray();
        public static bool LibraryBound(XDocument d) => Child(Block(d), "AttributeList").Elements().Any(e =>
            new[] { "LibraryType", "LibraryTypeGuid", "LibraryTypeVersionGuid" }.Contains(e.Name.LocalName) && !string.IsNullOrWhiteSpace(e.Value));

        // Preserve namespaces, program strings and wiring while normalizing identifiers and AttributeList ordering.
        public static string Canonical(XDocument document) => Canonical(document, null);

        public static string Canonical(XDocument document, XDocument? verificationPlan)
        {
            var block = new XElement(Block(document));
            // TIA emits schema order, while PrepareImport appends omitted attributes. Sort before assigning object IDs.
            foreach (var list in block.Descendants().Where(e => e.Name.LocalName == "AttributeList").ToArray())
            {
                var children = list.Elements().ToArray();
                var sorted = children.OrderBy(e => e.Name.LocalName, StringComparer.Ordinal).ThenBy(e => (string?)e.Attribute("Name"), StringComparer.Ordinal)
                    .ToArray();
                if (children.SequenceEqual(sorted)) continue;
                var nodes = list.Nodes().ToArray();
                list.RemoveNodes();
                int child = 0;
                foreach (var node in nodes) list.Add(node is XElement ? sorted[child++] : node);
            }
            var ids = ObjectIds(block);
            // Validate all IDs first. Removing TIA-added objects can shift the canonical IDs of retained objects.
            if (verificationPlan != null && RemoveAddedBlockText(block, Block(verificationPlan))) ids = ObjectIds(block);
            foreach (var a in block.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName == "ID" || a.Name.LocalName == "RefId"))
                if (ids.TryGetValue(a.Value, out var replacement)) a.Value = replacement;
            foreach (var network in block.Descendants().Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit"))
            {
                var uids = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var a in network.DescendantsAndSelf().Attributes("UId"))
                {
                    if (!uids.TryGetValue(a.Value, out var mapped)) { mapped = "u" + uids.Count; uids.Add(a.Value, mapped); }
                    a.Value = mapped;
                }
            }
            Child(block, "AttributeList").Elements().Where(e => new[] { "ModifiedDate", "CompileDate", "CreationDate" }.Contains(e.Name.LocalName)
                || ComputedBlockAttributes.Contains(e.Name.LocalName)).Remove();
            foreach (var e in block.DescendantsAndSelf())
            {
                // Formatting whitespace between elements is not program text. Leaf values
                // (including whitespace-only Text/Token values) and xml:space remain exact.
                if (e.HasElements && !e.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))
                    && !e.AncestorsAndSelf().Any(p => (string?)p.Attribute(XNamespace.Xml + "space") == "preserve"))
                    e.Nodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).Remove();
                if (e.Name.LocalName == "Member" && Regex.IsMatch(e.Name.NamespaceName, @"/SW/Interface/v[0-9]+\z", RegexOptions.CultureInvariant))
                {
                    if ((string?)e.Attribute("Remanence") == "NonRetain") e.Attribute("Remanence")!.Remove();
                    if ((string?)e.Attribute("Accessibility") == "Public") e.Attribute("Accessibility")!.Remove();
                    if ((string?)e.Attribute("Informative") == "false") e.Attribute("Informative")!.Remove();
                }
                var attrs = e.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToArray();
                e.RemoveAttributes(); e.Add(attrs);
            }
            return block.ToString(SaveOptions.DisableFormatting);
        }

        private static Dictionary<string, string> ObjectIds(XElement block)
        {
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in block.DescendantsAndSelf())
                if (e.Attribute("ID") is XAttribute id)
                {
                    if (ids.ContainsKey(id.Value)) throw new ArgumentException("Duplicate object ID.");
                    ids.Add(id.Value, "o" + ids.Count);
                }
            return ids;
        }

        private static bool RemoveAddedBlockText(XElement block, XElement plan)
        {
            var list = block.Element("ObjectList");
            if (list == null || block.Elements("ObjectList").Skip(1).Any()) return false;
            bool changed = false;
            foreach (string field in new[] { "Title", "Comment" })
            {
                bool HasField(XElement text) => (string?)text.Attribute("CompositionName") == field;
                if (plan.Elements("ObjectList").Elements("MultilingualText").Any(HasField)) continue;
                var added = list.Elements("MultilingualText").Where(HasField).Take(2).ToArray();
                if (added.Length == 1 && EmptyBlockText(added[0])) { added[0].Remove(); changed = true; }
            }
            if (!list.HasAttributes && !list.HasElements && OnlyChildElements(list)) { list.Remove(); changed = true; }
            return changed;
        }

        private static bool OnlyChildElements(XElement element) =>
            element.Nodes().All(n => n is XElement || n is XText t && string.IsNullOrWhiteSpace(t.Value));

        private static bool OnlyObjectAttributes(XElement element) => element.Attributes().All(a => a.Name == "ID" || a.Name == "CompositionName");

        // Recognize the block-level multilingual wrapper and its culture records. Preserve extensions and all nonempty Text.
        private static bool EmptyBlockText(XElement text)
        {
            if (!OnlyObjectAttributes(text) || !OnlyChildElements(text)) return false;
            var list = text.Element("ObjectList");
            if (list == null) return !text.HasElements;
            return text.Elements().Count() == 1 && !list.HasAttributes && OnlyChildElements(list) && list.Elements().All(EmptyTextItem);
        }

        private static bool EmptyTextItem(XElement item)
        {
            if (item.Name != "MultilingualTextItem" || !OnlyObjectAttributes(item) || !OnlyChildElements(item)
                || (string?)item.Attribute("CompositionName") is not (null or "Items")) return false;
            var attributes = item.Element("AttributeList");
            if (attributes == null) return !item.HasElements;
            if (item.Elements().Count() != 1 || attributes.HasAttributes || !OnlyChildElements(attributes)) return false;
            return attributes.Elements().All(field => !field.HasAttributes && (field.Name == "Culture" && field.Nodes().All(n => n is XText)
                || field.Name == "Text" && field.Nodes().All(n => n is XText t && t.Value.Length == 0)));
        }

        private static IEnumerable<XElement> DbMembers(XElement block)
        {
            if (block.Name.LocalName != "SW.Blocks.GlobalDB") yield break;
            var sections = block.Element("AttributeList")?.Element("Interface")?.Elements();
            if (sections == null) yield break;
            foreach (var root in sections.Where(e => e.Name.LocalName == "Sections"
                && Regex.IsMatch(e.Name.NamespaceName, @"/SW/Interface/v[0-9]+\z", RegexOptions.CultureInvariant)))
            {
                XNamespace ns = root.Name.Namespace;
                foreach (var member in root.Elements(ns + "Section").SelectMany(DeclaredMembers)) yield return member;
            }
        }

        private static IEnumerable<XElement> DeclaredMembers(XElement container)
        {
            foreach (var member in container.Elements(container.Name.Namespace + "Member"))
            {
                yield return member;
                if ((string?)member.Attribute("Datatype") == "Struct")
                    foreach (var child in DeclaredMembers(member)) yield return child;
            }
        }

        private static string MemberLocation(XElement element) => string.Join("/", element.AncestorsAndSelf().Reverse()
            .Where(e => e.Name.LocalName == "Member" || e.Name.LocalName == "Section" && (string?)e.Attribute("Name") != "None")
            .Select(e => ObjectPathEncoding.EncodeSegment((string?)e.Attribute("Name") ?? "")));

        private static string? ReferencedType(XElement member)
        {
            string? type = (string?)member.Attribute("Datatype");
            return type != null && type.Length > 2 && type[0] == '"' && type[type.Length - 1] == '"'
                ? type.Substring(1, type.Length - 2) : null;
        }

        private static bool Unexpanded(XElement member) => member.Element(member.Name.Namespace + "Sections") == null
            && member.Element(member.Name.Namespace + "Member") == null;

        public static IEnumerable<string> UnexpandedTypes(string planned) => DbMembers(XElement.Parse(planned, LoadOptions.PreserveWhitespace))
            .Where(Unexpanded).Select(ReferencedType).OfType<string>().Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal);

        // Capture the declaration from the bound PLC before import. Only its interface participates in the preview token.
        public static XElement? TypeDeclaration(string xml, string name)
        {
            var document = ParseDocument(xml, true);
            if (Block(document).Name.LocalName != "SW.Types.PlcStruct" || Name(document) != name) return null;
            var block = XElement.Parse(Canonical(document), LoadOptions.PreserveWhitespace);
            var sections = block.Element("AttributeList")?.Element("Interface")?.Elements().SingleOrDefault();
            return DeclarationSection(sections);
        }

        private static XElement? DeclarationSection(XElement? sections)
        {
            if (sections?.Name.LocalName != "Sections") return null;
            XNamespace ns = sections.Name.Namespace;
            var content = sections.Nodes().Where(n => n is not XComment && !(n is XElement e && e.Name == ns + "AttributeList")).Take(2).ToArray();
            return content.Length == 1 && content[0] is XElement section && section.Name == ns + "Section"
                && (string?)section.Attribute("Name") == "None" ? section : null;
        }

        public static string NormalizeCompiledReadback(string readback, string planned, IReadOnlyDictionary<string, XElement> declarations)
            => NormalizeCompiledReadback(readback, planned, declarations, out _);

        public static string NormalizeCompiledReadback(string readback, string planned, IReadOnlyDictionary<string, XElement> declarations,
            out string? difference)
        {
            difference = null;
            if (declarations.Count == 0) return readback;
            var block = XElement.Parse(readback, LoadOptions.PreserveWhitespace);
            var shapes = declarations.ToDictionary(pair => pair.Key, pair => DeclarationShape(pair.Value), StringComparer.Ordinal);
            var unexpanded = DbMembers(XElement.Parse(planned, LoadOptions.PreserveWhitespace)).Where(Unexpanded)
                .ToLookup(m => (m.Name, MemberLocation(m), ReferencedType(m)));
            bool removed = false;
            foreach (var member in DbMembers(block))
            {
                string? type = ReferencedType(member);
                if (type == null || !shapes.TryGetValue(type, out var expected)) continue;
                string path = MemberLocation(member);
                if (!unexpanded.Contains((member.Name, path, type))) continue;
                var expansion = member.Element(member.Name.Namespace + "Sections");
                if (expansion == null || expansion.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name != "ID")) continue;
                var section = DeclarationSection(expansion);
                if (section?.Name != expected.Name) continue;
                var actual = DeclarationShape(section);
                RemoveReadbackOnlyTypeExpansions(expected, actual);
                if (XNode.DeepEquals(expected, actual)) { expansion.Remove(); removed = true; }
                else difference ??= DescribeDifference(expected, actual, path);
            }
            // IDs on a derived member can shift the canonical IDs of later block objects.
            return removed ? Canonical(new XDocument(new XElement("Document", block))) : readback;
        }

        private static void RemoveReadbackOnlyTypeExpansions(XElement declaration, XElement readback)
        {
            XNamespace ns = declaration.Name.Namespace;
            var pending = new Stack<(XElement Declared, XElement Compiled)>();
            pending.Push((declaration, readback));
            while (pending.Count > 0)
            {
                var (declared, compiled) = pending.Pop();
                if (declared.Name != compiled.Name) continue;
                if (declared.Name == ns + "Member" && compiled.Element(ns + "Sections") is XElement expansion
                    && declared.Element(ns + "Sections") == null
                    && (ReferencedType(declared) != null || !string.IsNullOrWhiteSpace((string?)declared.Attribute("Version")))
                    && (string?)declared.Attribute("Name") == (string?)compiled.Attribute("Name")
                    && (string?)declared.Attribute("Datatype") == (string?)compiled.Attribute("Datatype"))
                    expansion.Remove();
                // Pair children in declaration order. DeepEquals still checks every remaining node and attribute.
                foreach (var pair in declared.Elements().Zip(compiled.Elements(), (left, right) => (left, right))) pending.Push(pair);
            }
        }

        public static string DeclarationFingerprint(XElement section) => HashText(DeclarationShape(section).ToString(SaveOptions.DisableFormatting));

        private static XElement DeclarationShape(XElement section)
        {
            var shape = new XElement(section);
            XNamespace ns = section.Name.Namespace;
            // Compare the declared interface in order, including nested Sections, Struct members and array start values.
            shape.Descendants(ns + "AttributeList").Remove();
            shape.Descendants(ns + "Comment").Remove();
            shape.DescendantNodes().OfType<XComment>().Remove();
            shape.DescendantsAndSelf().Attributes().Where(a => a.IsNamespaceDeclaration || a.Name == "ID" || a.Name == "Informative").Remove();
            foreach (var member in shape.Descendants(ns + "Member"))
            {
                member.ReplaceAttributes(member.Attribute("Name"), member.Attribute("Datatype"), member.Attribute("Version"), member.Attribute("Remanence"));
                RemoveImplicitDefault(member);
                // Start values describe their owner; only the order among sibling declarations matters.
                var starts = member.Elements(ns + "StartValue").ToArray();
                starts.Remove();
                member.AddFirst(starts);
            }
            return shape;
        }

        private static void RemoveImplicitDefault(XElement member)
        {
            var start = member.Element(member.Name.Namespace + "StartValue");
            if (start == null || start.HasAttributes || start.HasElements) return;
            string? type = (string?)member.Attribute("Datatype");
            if (type == "Bool" && string.Equals(start.Value, "false", StringComparison.OrdinalIgnoreCase)
                || start.Value == "0" && type is "SInt" or "USInt" or "Int" or "UInt" or "DInt" or "UDInt" or "LInt" or "ULInt")
                start.Remove();
        }

        public static string ReadbackDifference(string planned, string readback) => DescribeDifference(
            XElement.Parse(planned, LoadOptions.PreserveWhitespace), XElement.Parse(readback, LoadOptions.PreserveWhitespace), "");

        private static string DescribeDifference(XElement expected, XElement actual, string prefix)
        {
            string At(XElement node, string aspect)
            {
                string path = MemberLocation(node);
                if (prefix.Length > 0) path = path.Length == 0 ? prefix : prefix + "/" + path;
                if (path.Length == 0)
                {
                    var first = node.Name == "ObjectList" ? node.Elements().FirstOrDefault() : node;
                    var text = first?.AncestorsAndSelf("MultilingualText").FirstOrDefault();
                    if (text?.Parent?.Name == "ObjectList" && text.Parent.Parent?.Parent == null
                        && (string?)text.Attribute("CompositionName") is "Comment" or "Title")
                        return (string?)text.Attribute("CompositionName") + ": blockAttribute";
                    path = node.Name.LocalName;
                }
                return (path.Length <= 200 ? path : "..." + path.Substring(path.Length - 197)) + ": " + aspect;
            }
            while (true)
            {
                if (expected.Name != actual.Name) return At(expected, "structure");
                if (expected.Name.LocalName == "Member")
                {
                    string? name = (string?)expected.Attribute("Name");
                    string? observed = (string?)actual.Attribute("Name");
                    if (name != observed)
                    {
                        var plannedSiblings = expected.Parent!.Elements(expected.Name);
                        var actualSiblings = actual.Parent!.Elements(actual.Name);
                        bool found = actualSiblings.Any(e => (string?)e.Attribute("Name") == name);
                        bool known = plannedSiblings.Any(e => (string?)e.Attribute("Name") == observed);
                        if (found && known) return At(expected, "structure");
                        if (found) return At(actual, "extraMember");
                        return At(expected, known ? "missingMember" : "name");
                    }
                    if ((string?)expected.Attribute("Datatype") != (string?)actual.Attribute("Datatype")) return At(expected, "type");
                    if ((string?)expected.Attribute("Remanence") != (string?)actual.Attribute("Remanence")) return At(expected, "remanence");
                }
                if (expected.Name.LocalName == "StartValue") return At(expected, "startValue");
                if (expected.Parent?.Name == "AttributeList" && expected.Parent.Parent?.Parent == null && expected.Name != "Interface")
                    return At(expected, "blockAttribute");
                if (expected.Attributes().Any(a => (string?)actual.Attribute(a.Name) != a.Value)
                    || expected.Attributes().Count() != actual.Attributes().Count()) return At(expected, "structure");
                var left = expected.Elements().ToArray();
                var right = actual.Elements().ToArray();
                int index = 0;
                while (index < left.Length && index < right.Length && XNode.DeepEquals(left[index], right[index])) index++;
                if (index < left.Length && index < right.Length && left[index].Name == right[index].Name)
                {
                    expected = left[index]; actual = right[index]; continue;
                }
                if (index == left.Length && index == right.Length) return At(expected, "structure");
                bool missing = index == right.Length || index < left.Length && !right.Skip(index).Any(e => e.Name == left[index].Name);
                var changed = missing ? left[index] : right[index];
                if (changed.Name.LocalName == "Member") return At(changed, missing ? "missingMember" : "extraMember");
                if (changed.Name.LocalName == "StartValue") return At(changed, "startValue");
                if (expected.Name == "AttributeList" && expected.Parent?.Parent == null) return At(changed, "blockAttribute");
                return At(changed, "structure");
            }
        }

        public static JsonObject Inspect(string xml)
        {
            var d = ParseDocument(xml, true); var rows = new JsonArray(); var index = 0;
            foreach (var net in Networks(d))
            {
                var attributes = Child(net, "AttributeList");
                var source = attributes.Elements().SingleOrDefault(e => e.Name.LocalName == "NetworkSource");
                rows.Add(new JsonObject { ["networkIndex"] = index++, ["language"] = Value(attributes, "ProgrammingLanguage"),
                    ["sourceKinds"] =
                        new JsonArray((source?.Elements() ?? Enumerable.Empty<XElement>()).Select(e => (JsonNode)JsonValue.Create(e.Name.LocalName)!)
                            .ToArray()),
                    ["textTargets"] = TextTargets(net), ["nativeGranularLogicWriteAvailable"] = false,
                    ["route"] =
                        "PatchPlcBlockDocument can update existing multilingual title/comment entries; logic changes require a full reviewed block document."
                    });
            }
            var members = new JsonArray();
            var interfaceElement = Child(Block(d), "AttributeList").Elements().SingleOrDefault(e => e.Name.LocalName == "Interface");
            bool libraryBound = LibraryBound(d);
            if (interfaceElement != null)
            {
                var targets = libraryBound ? null : new MemberLookup(interfaceElement);
                foreach (var member in interfaceElement.Descendants().Where(e => e.Name.LocalName == "Member"))
                {
                    string? section = (string?)MemberSection(member)?.Attribute("Name");
                    string path = EncodedMemberPath(MemberNames(member));
                    if (targets != null && (section == null || targets.Lookup(section, path) != member || ReadOnlyMember(member))) continue;
                    var start = member.Elements().SingleOrDefault(e => e.Name.LocalName == "StartValue");
                    if (targets != null)
                    {
                        if (ComplexMember(member) || start?.HasElements == true) start = null;
                        if (start == null && member.Elements().Any(e => e.Name.LocalName == "Comment"
                            && string.Equals((string?)e.Attribute("Informative"), "true", StringComparison.OrdinalIgnoreCase))) continue;
                    }
                    var comments = new JsonObject();
                    foreach (var text in member.Elements().Where(e => e.Name.LocalName == "Comment")
                            .Elements().Where(e => e.Name.LocalName == "MultiLanguageText"))
                        comments[(string)text.Attribute("Lang")!] = text.Value;
                    members.Add(new JsonObject { ["section"] = section, ["memberPath"] = path,
                        ["datatype"] = (string?)member.Attribute("Datatype"), ["startValue"] = start?.Value,
                        ["attributeXml"] = member.Elements().SingleOrDefault(e => e.Name.LocalName == "AttributeList")?.ToString(SaveOptions.DisableFormatting),
                        ["comments"] = comments
                        });
                }
            }
            return new JsonObject { ["name"] = Name(d), ["kind"] = Block(d).Name.LocalName, ["libraryBound"] = libraryBound,
                ["documentFingerprint"] = HashText(Canonical(d)), ["networks"] = rows, ["members"] = members, ["blockTextTargets"] = TextTargets(Block(d)),
                ["supportedPatchActions"] = libraryBound ? new JsonArray()
                    : new JsonArray("setBlockText", "setNetworkText", "setMemberStartValue", "setMemberComment"),
                ["nativeImportValidated"] = false, ["scope"] = libraryBound ? "Library-connected members are read-only. Use the library update workflow." :
                    "setMemberStartValue requires a non-null startValue. Members with null startValue support comment edits only. " +
                    "Exported structure only; does not establish target CPU compatibility, consistency or native import safety." };
        }
        private static JsonArray TextTargets(XElement owner)
        {
            var result = new JsonArray();
            foreach (var text in owner.Elements().Where(e => e.Name.LocalName == "ObjectList").Elements().Where(e => e.Name.LocalName == "MultilingualText"))
                foreach (var item in text.Descendants().Where(e => e.Name.LocalName == "MultilingualTextItem"))
                {
                    var a = Child(item, "AttributeList");
                    result
                        .Add(new JsonObject {
                            ["field"] = (string?)text.Attribute("CompositionName"), ["culture"] = Value(a, "Culture"), ["text"] = Value(a, "Text") });
                }
            return result;
        }
        public static string Patch(string xml, string changesJson, string expectedFingerprint)
        {
            var d = ParseDocument(xml, true);
            if (HashText(Canonical(d)) != expectedFingerprint) throw new InvalidOperationException("Document changed; inspect again before editing.");
            if (LibraryBound(d)) throw new InvalidOperationException("Library-connected blocks require the library update workflow.");
            var changes = JsonNode.Parse(changesJson) as JsonArray ?? throw new ArgumentException("changesJson must be an array.");
            if (changes.Count < 1 || changes.Count > 100) throw new ArgumentException("Supply 1..100 changes.");
            var used = new HashSet<(XElement Target, string Culture)>();
            HashSet<string>? cultures = null;
            MemberLookup? members = null;
            XElement SelectMember(string section, string[] path) =>
                (members ??= new MemberLookup(Child(Child(Block(d), "AttributeList"), "Interface"))).FindMember(section, path);
            foreach (var node in changes)
            {
                var c = node as JsonObject ?? throw new ArgumentException("Each change must be an object.");
                string Get(string key) => c[key]?.GetValue<string>() ?? throw new ArgumentException(key + " is required.");
                var action = Get("action"); XElement target;
                var allowed = action == "setNetworkText" ? new[] { "action", "networkIndex", "field", "culture", "expectedValue", "value" }
                    : action == "setBlockText" ? new[] { "action", "field", "culture", "expectedValue", "value" }
                    : action == "setMemberStartValue" ? new[] { "action", "section", "memberPath", "expectedValue", "value" }
                    : action == "setMemberComment" ? new[] { "action", "section", "memberPath", "culture", "expectedValue", "value" }
                        : throw new ArgumentException("Unknown patch action.");
                if (c.Any(p => !allowed.Contains(p.Key))) throw new ArgumentException("Unknown patch property.");
                if (action == "setMemberComment")
                {
                    var member = SelectMember(Get("section"), MemberPath(Get("memberPath")));
                    var culture = Get("culture");
                    if (!Regex.IsMatch(culture, @"\A[a-zA-Z]{2}(-[a-zA-Z]{4})?-[a-zA-Z]{2}\z"))
                        throw new ArgumentException("culture must be a culture name such as zh-CN.");
                    cultures ??= new HashSet<string>(d.Descendants().Where(e => e.Name.LocalName == "MultilingualTextItem")
                        .Select(e => Value(Child(e, "AttributeList"), "Culture"))
                        .Concat(d.Descendants().Where(e => e.Name.LocalName == "MultiLanguageText")
                            .Select(e => (string?)e.Attribute("Lang") ?? "")).Where(c => c.Length > 0), StringComparer.OrdinalIgnoreCase);
                    if (cultures.Count == 0) cultures.Add(culture);
                    if (!cultures.TryGetValue(culture, out var documentCulture))
                        throw new ArgumentException("culture " + culture
                                + " does not occur in this document; use a project language that the export contains.");
                    if (!used.Add((member, culture.ToUpperInvariant())))
                        throw new InvalidOperationException("Target is complex, repeated or its old value changed.");
                    SetMemberComment(member, documentCulture, Get("expectedValue"), Get("value"));
                    continue;
                }
                if (action == "setMemberStartValue")
                {
                    var section = Get("section"); var path = MemberPath(Get("memberPath"));
                    var current = SelectMember(section, path);
                    if (ComplexMember(current))
                        throw new ArgumentException("Select a scalar member.");
                    target = current.Elements().SingleOrDefault(e => e.Name.LocalName == "StartValue")
                        ?? throw new InvalidOperationException("Selected member has no explicit StartValue.");
                    if (!used.Add((target, ""))) throw new InvalidOperationException("Target is complex, repeated or its old value changed.");
                    SetMemberStartValue(current, Get("expectedValue"), Get("value"));
                    continue;
                }
                else
                {
                    var owner = Block(d);
                    if (action == "setNetworkText")
                    {
                        if (owner.Name.LocalName == "SW.Types.PlcStruct") throw new ArgumentException("UDT documents have no networks.");
                        var index = c["networkIndex"]?.GetValue<int>() ?? throw new ArgumentException("networkIndex is required.");
                        var networks = Networks(d);
                        if (index < 0 || index >= networks.Length) throw new ArgumentException("networkIndex outside document.");
                        owner = networks[index];
                    }
                    var field = Get("field");
                    if (field != "Title" && field != "Comment") throw new ArgumentException("field must be Title or Comment.");
                    var text = Child(owner, "ObjectList").Elements()
                        .Single(e => e.Name.LocalName == "MultilingualText" && (string?)e.Attribute("CompositionName") == field);
                    var item = text.Descendants()
                        .Single(e => e.Name.LocalName == "MultilingualTextItem" && string
                                .Equals(Value(Child(e, "AttributeList"), "Culture"), Get("culture"), StringComparison.OrdinalIgnoreCase));
                    target = Child(Child(item, "AttributeList"), "Text");
                }
                if (target.HasElements || !used.Add((target, "")) || target.Value != Get("expectedValue"))
                    throw new InvalidOperationException("Target is complex, repeated or its old value changed.");
                target.Value = Get("value");
            }
            return d.ToString();
        }
        private static XElement? MemberSection(XElement member) => member.Ancestors().FirstOrDefault(e => e.Name.LocalName == "Section");

        private static IEnumerable<string> MemberNames(XElement element) => element.AncestorsAndSelf().Reverse()
            .Where(e => e.Name.LocalName == "Member").Select(e => (string?)e.Attribute("Name") ?? "");

        private static string[] MemberPath(string path) => Array.ConvertAll(path.Split('/'), ObjectPathEncoding.DecodeSegment);
        private static string EncodedMemberPath(IEnumerable<string> names) => string.Join("/", names.Select(ObjectPathEncoding.EncodeSegment));

        private static bool ReadOnlyMember(XElement member) => member.AncestorsAndSelf().Any(e => e.Attributes()
            .Any(a => (a.Name.LocalName == "ReadOnly" || a.Name.LocalName == "Informative") && a.Value.Equals("true", StringComparison.OrdinalIgnoreCase)));

        private static bool ComplexMember(XElement member) => member.Elements().Any(e => e.Name.LocalName is "Member" or "Sections");

        internal sealed class MemberLookup
        {
            private readonly ILookup<string, XElement> sections;
            private readonly Dictionary<(string Section, string Path), XElement?> targets = new();

            internal MemberLookup(XElement face)
            {
                sections = face.Descendants().Where(e => e.Name.LocalName == "Section").ToLookup(e => (string?)e.Attribute("Name") ?? "");
                foreach (var member in face.Descendants().Where(e => e.Name.LocalName == "Member"))
                {
                    var section = MemberSection(member);
                    if (section?.Attribute("Name") is not XAttribute name) continue;
                    var names = MemberNames(member).ToArray();
                    Add(name.Value, EncodedMemberPath(names), member);
                    // Legacy paths omit the enclosing instance of a unique section.
                    if (sections[name.Value].Count() == 1)
                    {
                        int prefix = MemberNames(section).Count();
                        if (prefix != 0) Add(name.Value, EncodedMemberPath(names.Skip(prefix)), member);
                    }
                }
            }

            private void Add(string section, string path, XElement member)
            {
                var key = (section, path);
                targets[key] = targets.TryGetValue(key, out var existing) && existing != member ? null : member;
            }

            internal XElement? Lookup(string section, string path) => targets.TryGetValue((section, path), out var member) ? member : null;

            internal XElement FindMember(string section, string[] path)
            {
                if (!sections.Contains(section)) throw new ArgumentException("Section is absent or ambiguous.");
                string selector = EncodedMemberPath(path);
                var member = Lookup(section, selector) ?? throw new ArgumentException("Member path is absent or ambiguous: " + selector
                    + ". Use an unambiguous section and memberPath from GetPlcBlockEditCapabilities.");
                if (ReadOnlyMember(member)) throw new ArgumentException("Read-only/informative member cannot be edited.");
                return member;
            }
        }

        public static bool ElementaryMember(XElement member)
        {
            string type = (string?)member.Attribute("Datatype") ?? "";
            if (Regex.IsMatch(type, @"\A(?:W?String)\[\d+\]\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
            return new[] { "Bool", "Byte", "Word", "DWord", "LWord", "SInt", "Int", "DInt", "LInt", "USInt", "UInt", "UDInt", "ULInt",
                "Real", "LReal", "Char", "WChar", "String", "WString", "Time", "LTime", "S5Time", "Date", "Time_Of_Day", "LTime_Of_Day",
                "Date_And_Time", "LDT" }.Contains(type, StringComparer.OrdinalIgnoreCase);
        }
        internal static MemberLookup CreateMemberLookup(XDocument document) => new MemberLookup(Child(Child(Block(document), "AttributeList"), "Interface"));

        public static void SetMemberStartValue(XDocument document, string section, string[] path, string expected, string value)
            => SetMemberStartValue(CreateMemberLookup(document).FindMember(section, path), expected, value);

        internal static void SetMemberStartValue(XElement member, string expected, string value)
        {
            if (ComplexMember(member))
                throw new ArgumentException("Select a scalar member.");
            var target = member.Elements().SingleOrDefault(e => e.Name.LocalName == "StartValue");
            if (target?.HasElements == true || (target?.Value ?? "") != expected)
                throw new InvalidOperationException("Target is complex, repeated or its old value changed.");
            if (target == null)
            {
                if (!ElementaryMember(member)) throw new ArgumentException("Missing StartValue requires an elementary scalar member.");
                target = new XElement(member.Name.Namespace + "StartValue");
                member.Add(target);
            }
            target.Value = value;
        }
        private static void SetMemberComment(XElement member, string culture, string expectedValue, string value)
        {
            var comment = member.Elements().SingleOrDefault(e => e.Name.LocalName == "Comment");
            if (((string?)comment?.Attribute("Informative"))?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                throw new ArgumentException("Read-only/informative member cannot be edited.");
            var entry = comment?.Elements()
                .SingleOrDefault(e => e.Name.LocalName == "MultiLanguageText" && string
                    .Equals((string?)e.Attribute("Lang"), culture, StringComparison.OrdinalIgnoreCase));
            if (entry?.HasElements == true || (entry?.Value ?? "") != expectedValue)
                throw new InvalidOperationException("Target is complex, repeated or its old value changed.");
            if (value.Length == 0)
            {
                entry?.Remove();
                if (comment != null && !comment.HasElements) comment.Remove();
                return;
            }
            if (comment == null)
            {
                comment = new XElement(member.Name.Namespace + "Comment");
                var attributes = member.Elements().SingleOrDefault(e => e.Name.LocalName == "AttributeList");
                if (attributes != null) attributes.AddAfterSelf(comment); else member.AddFirst(comment);
            }
            if (entry == null)
            {
                entry = new XElement(member.Name.Namespace + "MultiLanguageText", new XAttribute("Lang", culture));
                comment.Add(entry);
            }
            entry.Value = value;
        }
        public static string PrepareImport(string beforeXml, string candidateXml, JsonArray preserved)
        {
            var before = ParseDocument(beforeXml, editable: false, blockImport: true);
            var candidate = ParseDocument(candidateXml, editable: false, blockImport: true);
            if (Name(before) != Name(candidate) || Block(before).Name != Block(candidate).Name)
                throw new ArgumentException("Import must address the same exact block name and kind.");
            if (LibraryBound(before) || LibraryBound(candidate))
                throw new ArgumentException("Library-bound block import refused: SimaticML would lose its type connection.");
            if (Block(before).Name.LocalName == "SW.Blocks.InstanceDB")
                throw new ArgumentException("Instance DB overwrite requires a separate dependency-aware workflow.");
            var ba = Child(Block(before), "AttributeList"); var ca = Child(Block(candidate), "AttributeList");
            if (Block(before).Name.LocalName != "SW.Blocks.GlobalDB" && !new[] { "LAD", "FBD", "SCL", "STL" }.Contains(Value(ba, "ProgrammingLanguage")))
                throw new ArgumentException("Only standard LAD/FBD/SCL/STL code blocks are supported; Safety and unknown languages require their own " +
                    "workflow.");
            if (Networks(before).Length > 0 && Networks(candidate).Length == 0)
                throw new ArgumentException("Refuse replacing executable logic with an empty block.");
            Child(ca, "Interface"); // Missing interface is a replacement/reset, not a merge.
            foreach (var key in new[] { "Name", "Number", "MemoryLayout", "ProgrammingLanguage", "SecondaryType" })
                if (Value(ca, key).Length > 0 && Value(ba, key) != Value(ca, key))
                    throw new ArgumentException("This import does not change block identity/layout/language: " + key);
            foreach (var e in ba.Elements().Where(e => !e.HasElements && !new[] { "ModifiedDate", "CompileDate", "CreationDate" }.Contains(e.Name.LocalName)))
                if (!ca.Elements().Any(c => c.Name == e.Name)) { ca.Add(new XElement(e)); preserved.Add(e.Name.LocalName); }
            string MemberKey(XElement member) => string.Join("/", member.AncestorsAndSelf().Reverse()
                .Where(e => e.Name.LocalName == "Section" || e.Name.LocalName == "Member" || e.Name.LocalName == "Subelement")
                .Select(e => e.Name.LocalName + ":" + ((string?)e.Attribute("Name") ?? (string?)e.Attribute("Path") ?? "")));
            var oldMembers = Child(ba, "Interface").Descendants().Where(e => e.Name.LocalName == "Member")
                .GroupBy(MemberKey).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            foreach (var member in Child(ca, "Interface").Descendants().Where(e => e.Name.LocalName == "Member"))
            {
                var key = MemberKey(member);
                if (!oldMembers.TryGetValue(key, out var originals)) continue;
                if (originals.Length != 1) throw new ArgumentException("Ambiguous interface member: " + key);
                var original = originals[0];
                if ((string?)original.Attribute("Datatype") != (string?)member.Attribute("Datatype")) continue;
                var oldAttributes = original.Elements().SingleOrDefault(e => e.Name.LocalName == "AttributeList");
                if (oldAttributes == null) continue;
                var newAttributes = member.Elements().SingleOrDefault(e => e.Name.LocalName == "AttributeList");
                if (newAttributes == null) { newAttributes = new XElement(oldAttributes.Name); member.AddFirst(newAttributes); }
                foreach (var attribute in oldAttributes.Elements())
                    if (!newAttributes.Elements().Any(e => e.Name == attribute.Name && (string?)e.Attribute("Name") == (string?)attribute.Attribute("Name")))
                    {
                        newAttributes.Add(new XElement(attribute)); preserved
                            .Add(key + "/" + attribute.Name.LocalName + ":" + (string?)attribute.Attribute("Name"));
                    }
            }
            return candidate.ToString();
        }
    }
}
