using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    // Independent implementation using Siemens SimaticML. No third-party product code.
    public static class PlcDocumentEditing
    {
        public const int MaxBytes = 16 * 1024 * 1024;
        public static string Hash(byte[] bytes) => Siemens.ArgumentRules.Hash(bytes);
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
        public static XElement Block(XDocument d) => d.Root!.Elements().Single(x => x.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
        public static XDocument Parse(string xml)
        {
            if (Encoding.UTF8.GetByteCount(xml) > MaxBytes) throw new ArgumentException("XML exceeds 16 MiB.");
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes });
            var d = XDocument.Load(reader, LoadOptions.None);
            if (d.Root?.Name.LocalName != "Document") throw new ArgumentException("One SimaticML Document is required.");
            var objects = d.Root.Elements().Where(x => x.Name.LocalName != "Engineering" && x.Name.LocalName != "DocumentInfo").ToArray();
            if (objects.Length != 1 || !new[] { "SW.Blocks.FC", "SW.Blocks.FB", "SW.Blocks.OB", "SW.Blocks.GlobalDB", "SW.Blocks.InstanceDB" }.Contains(objects[0].Name.LocalName))
                throw new ArgumentException("Exactly one supported PLC block per XML file is required.");
            if (string.IsNullOrWhiteSpace(Value(Child(objects[0], "AttributeList"), "Name"))) throw new ArgumentException("Block Name is missing.");
            return d;
        }
        public static string Name(XDocument d) => Value(Child(Block(d), "AttributeList"), "Name");
        public static XElement[] Networks(XDocument d) => Block(d).Descendants().Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit").ToArray();
        public static bool LibraryBound(XDocument d) => Child(Block(d), "AttributeList").Elements().Any(e =>
            new[] { "LibraryType", "LibraryTypeGuid", "LibraryTypeVersionGuid" }.Contains(e.Name.LocalName) && !string.IsNullOrWhiteSpace(e.Value));

        // Keep namespaces, all program strings and wiring. Only renumber identifiers;
        // never erase UId connections, GUID literals, timestamps in logic or member attributes.
        public static string Canonical(XDocument document)
        {
            var block = new XElement(Block(document));
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in block.DescendantsAndSelf())
                if (e.Attribute("ID") is XAttribute id)
                {
                    if (ids.ContainsKey(id.Value)) throw new ArgumentException("Duplicate object ID.");
                    ids.Add(id.Value, "o" + ids.Count);
                }
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
            Child(block, "AttributeList").Elements().Where(e => new[] { "ModifiedDate", "CompileDate", "CreationDate" }.Contains(e.Name.LocalName)).Remove();
            foreach (var e in block.DescendantsAndSelf())
            {
                // Formatting whitespace between elements is not program text. Leaf values
                // (including whitespace-only Text/Token values) and xml:space remain exact.
                if (e.HasElements && !e.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))
                    && !e.AncestorsAndSelf().Any(p => (string?)p.Attribute(XNamespace.Xml + "space") == "preserve"))
                    e.Nodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).Remove();
                var attrs = e.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToArray();
                e.RemoveAttributes(); e.Add(attrs);
            }
            return block.ToString(SaveOptions.DisableFormatting);
        }
        public static JsonObject Inspect(string xml)
        {
            var d = Parse(xml); var rows = new JsonArray(); var index = 0;
            foreach (var net in Networks(d))
            {
                var attributes = Child(net, "AttributeList");
                var source = attributes.Elements().SingleOrDefault(e => e.Name.LocalName == "NetworkSource");
                rows.Add(new JsonObject { ["networkIndex"] = index++, ["language"] = Value(attributes, "ProgrammingLanguage"),
                    ["sourceKinds"] = new JsonArray((source?.Elements() ?? Enumerable.Empty<XElement>()).Select(e => (JsonNode)JsonValue.Create(e.Name.LocalName)!).ToArray()),
                    ["textTargets"] = TextTargets(net), ["nativeGranularLogicWriteAvailable"] = false,
                    ["route"] = "PatchPlcBlockDocument can update existing multilingual title/comment entries; logic changes require a full reviewed block document." });
            }
            var members = new JsonArray();
            var interfaceElement = Child(Block(d), "AttributeList").Elements().SingleOrDefault(e => e.Name.LocalName == "Interface");
            if (interfaceElement != null)
                foreach (var member in interfaceElement.Descendants().Where(e => e.Name.LocalName == "Member"))
                {
                    var section = member.Ancestors().FirstOrDefault(e => e.Name.LocalName == "Section");
                    var path = string.Join("/", member.AncestorsAndSelf().Reverse().Where(e => e.Name.LocalName == "Member").Select(e => (string?)e.Attribute("Name") ?? ""));
                    members.Add(new JsonObject { ["section"] = (string?)section?.Attribute("Name"), ["memberPath"] = path,
                        ["datatype"] = (string?)member.Attribute("Datatype"), ["startValue"] = member.Elements().SingleOrDefault(e => e.Name.LocalName == "StartValue")?.Value,
                        ["attributeXml"] = member.Elements().SingleOrDefault(e => e.Name.LocalName == "AttributeList")?.ToString(SaveOptions.DisableFormatting) });
                }
            return new JsonObject { ["name"] = Name(d), ["kind"] = Block(d).Name.LocalName, ["libraryBound"] = LibraryBound(d),
                ["documentFingerprint"] = HashText(Canonical(d)), ["networks"] = rows, ["members"] = members, ["blockTextTargets"] = TextTargets(Block(d)),
                ["supportedPatchActions"] = new JsonArray("setBlockText", "setNetworkText", "setMemberStartValue"),
                ["nativeImportValidated"] = false, ["scope"] = "Exported structure only; does not establish target CPU compatibility, consistency or native import safety." };
        }
        static JsonArray TextTargets(XElement owner)
        {
            var result = new JsonArray();
            foreach (var text in owner.Elements().Where(e => e.Name.LocalName == "ObjectList").Elements().Where(e => e.Name.LocalName == "MultilingualText"))
                foreach (var item in text.Descendants().Where(e => e.Name.LocalName == "MultilingualTextItem"))
                {
                    var a = Child(item, "AttributeList");
                    result.Add(new JsonObject { ["field"] = (string?)text.Attribute("CompositionName"), ["culture"] = Value(a, "Culture"), ["text"] = Value(a, "Text") });
                }
            return result;
        }
        public static string Patch(string xml, string changesJson, string expectedFingerprint)
        {
            var d = Parse(xml);
            if (HashText(Canonical(d)) != expectedFingerprint) throw new InvalidOperationException("Document changed; inspect again before editing.");
            if (LibraryBound(d)) throw new InvalidOperationException("Library-connected blocks require the library update workflow.");
            var changes = JsonNode.Parse(changesJson) as JsonArray ?? throw new ArgumentException("changesJson must be an array.");
            if (changes.Count < 1 || changes.Count > 100) throw new ArgumentException("Supply 1..100 changes.");
            var used = new HashSet<XElement>();
            foreach (var node in changes)
            {
                var c = node as JsonObject ?? throw new ArgumentException("Each change must be an object.");
                string Get(string key) => c[key]?.GetValue<string>() ?? throw new ArgumentException(key + " is required.");
                var action = Get("action"); XElement target;
                var allowed = action == "setNetworkText" ? new[] { "action", "networkIndex", "field", "culture", "expectedValue", "value" }
                    : action == "setBlockText" ? new[] { "action", "field", "culture", "expectedValue", "value" }
                    : action == "setMemberStartValue" ? new[] { "action", "section", "memberPath", "expectedValue", "value" } : throw new ArgumentException("Unknown patch action.");
                if (c.Any(p => !allowed.Contains(p.Key))) throw new ArgumentException("Unknown patch property.");
                if (action == "setMemberStartValue")
                {
                    var members = Child(Child(Block(d), "AttributeList"), "Interface").Descendants().Where(e => e.Name.LocalName == "Section" && (string?)e.Attribute("Name") == Get("section")).ToArray();
                    if (members.Length != 1) throw new ArgumentException("Section is absent or ambiguous.");
                    XElement current = members[0];
                    foreach (var segment in Get("memberPath").Split('/'))
                        current = current.Elements().Single(e => e.Name.LocalName == "Member" && (string?)e.Attribute("Name") == segment);
                    if (current.AncestorsAndSelf().Any(e => e.Attributes().Any(a => (a.Name.LocalName == "ReadOnly" || a.Name.LocalName == "Informative") && a.Value.Equals("true", StringComparison.OrdinalIgnoreCase))))
                        throw new ArgumentException("Read-only/informative member cannot be edited.");
                    if (current.Elements().Any(e => e.Name.LocalName == "Member" || e.Name.LocalName == "Sections")) throw new ArgumentException("Select a scalar member.");
                    target = Child(current, "StartValue"); // Do not invent missing/default/array value shapes.
                }
                else
                {
                    var owner = Block(d);
                    if (action == "setNetworkText")
                    {
                        var index = c["networkIndex"]?.GetValue<int>() ?? throw new ArgumentException("networkIndex is required.");
                        var networks = Networks(d);
                        if (index < 0 || index >= networks.Length) throw new ArgumentException("networkIndex outside document.");
                        owner = networks[index];
                    }
                    var field = Get("field");
                    if (field != "Title" && field != "Comment") throw new ArgumentException("field must be Title or Comment.");
                    var text = Child(owner, "ObjectList").Elements().Single(e => e.Name.LocalName == "MultilingualText" && (string?)e.Attribute("CompositionName") == field);
                    var item = text.Descendants().Single(e => e.Name.LocalName == "MultilingualTextItem" && Value(Child(e, "AttributeList"), "Culture") == Get("culture"));
                    target = Child(Child(item, "AttributeList"), "Text");
                }
                if (target.HasElements || !used.Add(target) || target.Value != Get("expectedValue")) throw new InvalidOperationException("Target is complex, repeated or its old value changed.");
                target.Value = Get("value");
            }
            return d.ToString();
        }
        public static string PrepareImport(string beforeXml, string candidateXml, JsonArray preserved)
        {
            var before = Parse(beforeXml); var candidate = Parse(candidateXml);
            if (Name(before) != Name(candidate) || Block(before).Name != Block(candidate).Name) throw new ArgumentException("Import must address the same exact block name and kind.");
            if (LibraryBound(before) || LibraryBound(candidate)) throw new ArgumentException("Library-bound block import refused: SimaticML would lose its type connection.");
            if (Block(before).Name.LocalName == "SW.Blocks.InstanceDB") throw new ArgumentException("Instance DB overwrite requires a separate dependency-aware workflow.");
            var ba = Child(Block(before), "AttributeList"); var ca = Child(Block(candidate), "AttributeList");
            if (Block(before).Name.LocalName != "SW.Blocks.GlobalDB" && !new[] { "LAD", "FBD", "SCL", "STL" }.Contains(Value(ba, "ProgrammingLanguage")))
                throw new ArgumentException("Only standard LAD/FBD/SCL/STL code blocks are supported; Safety and unknown languages require their own workflow.");
            if (Networks(before).Length > 0 && Networks(candidate).Length == 0) throw new ArgumentException("Refuse replacing executable logic with an empty block.");
            Child(ca, "Interface"); // Missing interface is a replacement/reset, not a merge.
            foreach (var key in new[] { "Name", "Number", "MemoryLayout", "ProgrammingLanguage", "SecondaryType" })
                if (Value(ca, key).Length > 0 && Value(ba, key) != Value(ca, key)) throw new ArgumentException("This import does not change block identity/layout/language: " + key);
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
                    { newAttributes.Add(new XElement(attribute)); preserved.Add(key + "/" + attribute.Name.LocalName + ":" + (string?)attribute.Attribute("Name")); }
            }
            return candidate.ToString();
        }
    }
}
