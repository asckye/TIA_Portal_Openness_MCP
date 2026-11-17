using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Logic.Exchange
{
    public sealed class PlcTagTableEdit
    {
        public XDocument Document { get; }
        public JsonObject Summary { get; }
        internal PlcTagTableEdit(XDocument document, JsonObject summary) { Document = document; Summary = summary; }
    }

    public static class PlcTagTableExchange
    {
        private static readonly string[] FixedColumns = { "Kind", "Name", "DataType", "Address", "Value" };
        private static readonly string[] Kinds = { "Tag", "Constant", "SystemConstant" };
        private static readonly string[] Elements = { "SW.Tags.PlcTag", "SW.Tags.PlcUserConstant", "SW.Tags.PlcSystemConstant" };
        private static readonly Regex Culture = new Regex(@"^[a-zA-Z]{2}(-[a-zA-Z]{4})?-[a-zA-Z]{2}$", RegexOptions.CultureInvariant);

        public static XDocument Parse(string xml)
        {
            if (Encoding.UTF8.GetByteCount(xml) > TableFile.MaximumBytes) throw new ArgumentException("XML exceeds 16 MiB.", nameof(xml));
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = TableFile.MaximumBytes });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            Table(document); return document;
        }

        private static XElement Table(XDocument document)
        {
            if (document.Root?.Name.LocalName != "Document") throw new ArgumentException("One SimaticML Document is required.");
            var objects = document.Root.Elements().Where(e => e.Name.LocalName != "Engineering" && e.Name.LocalName != "DocumentInfo").ToArray();
            if (objects.Length != 1 || objects[0].Name.LocalName != "SW.Tags.PlcTagTable")
                throw new ArgumentException("Exactly one PLC tag table per XML file is required.");
            if (string.IsNullOrWhiteSpace(Value(Attributes(objects[0]), "Name"))) throw new ArgumentException("Table Name is missing.");
            return objects[0];
        }
        public static string Name(XDocument document) => Value(Attributes(Table(document)), "Name");
        private static XElement? Child(XElement element, string name) => element.Elements().SingleOrDefault(e => e.Name.LocalName == name);
        private static XElement Attributes(XElement element) => Child(element, "AttributeList")
            ?? throw new ArgumentException("Missing AttributeList: " + element.Name.LocalName);
        private static string Value(XElement element, string name) => Child(element, name)?.Value ?? "";
        private static XElement[] Items(XDocument document) => Child(Table(document), "ObjectList")?.Elements()
            .Where(e => Elements.Contains(e.Name.LocalName)).ToArray() ?? Array.Empty<XElement>();
        private static string Kind(XElement item) => Kinds[Array.IndexOf(Elements, item.Name.LocalName)];
        private static string Key(string kind, string name) => kind + "\0" + name;
        private static string Field(string column) => column == "DataType" ? "DataTypeName" : column == "Address" ? "LogicalAddress" : column;
        private static IEnumerable<XElement> Comments(XElement item) => Child(item, "ObjectList")?.Elements()
            .Where(e => e.Name.LocalName == "MultilingualText" && (string?)e.Attribute("CompositionName") == "Comment")
            .SelectMany(e => Child(e, "ObjectList")?.Elements().Where(i => i.Name.LocalName == "MultilingualTextItem")
                ?? Enumerable.Empty<XElement>()) ?? Enumerable.Empty<XElement>();
        private static bool CommentColumn(string column) => column.StartsWith("Comment[", StringComparison.Ordinal) && column.EndsWith("]");
        private static string Language(string column) => column.Substring(8, column.Length - 9);
        private static string Cell(XElement item, string column)
        {
            if (column == "Kind") return Kind(item);
            if (Kind(item) == "Constant" && column == "ExternalAccessible") return "";
            if (CommentColumn(column)) return Comments(item).Where(c => Value(Attributes(c), "Culture") == Language(column))
                .Select(c => Value(Attributes(c), "Text")).SingleOrDefault() ?? "";
            return Value(Attributes(item), Field(column));
        }

        public static TableData ToTable(XDocument export)
        {
            var items = Items(export);
            T AtRow<T>(int row, int column, Func<T> read)
            {
                try { return read(); }
                catch (InvalidOperationException) { throw TableFile.Error("table", row, column, "Duplicate XML attribute or culture item."); }
            }
            var columns = FixedColumns.Concat(items.SelectMany((item, index) => AtRow(index + 2, 1,
                () => Attributes(item).Elements().Where(e => !e.HasElements).Select(e => e.Name.LocalName).ToArray()))
                .Where(n => !new[] { "Name", "DataTypeName", "LogicalAddress", "Value" }.Contains(n))
                .Distinct(StringComparer.Ordinal)).ToList();
            var languages = items.SelectMany((item, index) => AtRow(index + 2, 1,
                () => Comments(item).Select(c => Value(Attributes(c), "Culture")).ToArray())).Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal).ToArray();
            if (languages.Any(c => !Culture.IsMatch(c))) throw new ArgumentException("Invalid comment culture in template.");
            columns.AddRange(languages.Select(c => "Comment[" + c + "]"));
            var table = new TableData(columns.ToArray(), items.Select((item, row) => columns.Select((column, index) =>
                AtRow(row + 2, index + 1, () => Cell(item, column))).ToArray()));
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in table.Rows)
                if (row[1].Length == 0 || !keys.Add(row[1]))
                    throw new ArgumentException("Empty or duplicate template row key.");
            TableFile.Validate(table); return table;
        }

        public static PlcTagTableEdit FromTable(XDocument template, TableData table)
        {
            TableFile.Validate(table);
            var original = ToTable(template);
            foreach (string column in FixedColumns)
                if (!table.Columns.Contains(column)) throw new ArgumentException("Missing column: " + column);
            foreach (string column in table.Columns)
                if (!original.Columns.Contains(column)) throw new ArgumentException("Unknown template column: " + column);
            var document = new XDocument(template);
            var items = Items(document);
            var byKey = items.ToDictionary(item => Key(Kind(item), Cell(item, "Name")), StringComparer.Ordinal);
            var byName = items.ToDictionary(item => Cell(item, "Name"), StringComparer.OrdinalIgnoreCase);
            var orders = items.GroupBy(Kind).ToDictionary(group => group.Key,
                group => Attributes(group.First()).Elements().Select(e => e.Name.LocalName).ToArray(), StringComparer.Ordinal);
            foreach (var constant in items.Where(item => Kind(item) == "Constant")) Child(Attributes(constant), "ExternalAccessible")?.Remove();
            var rows = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var rowNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int kindIndex = Array.IndexOf(table.Columns, "Kind"), nameIndex = Array.IndexOf(table.Columns, "Name");
            for (int r = 0; r < table.Rows.Count; r++)
            {
                var row = table.Rows[r]; string kind = row[kindIndex], name = row[nameIndex];
                if (!Kinds.Contains(kind) || string.IsNullOrWhiteSpace(name)) throw TableFile.Error("table", r + 2, 1, "Invalid Kind/Name key.");
                if (rows.ContainsKey(Key(kind, name))) throw TableFile.Error("table", r + 2, nameIndex + 1, "Duplicate row key.");
                if (!rowNames.Add(name)) throw new ArgumentException("Name collision (case-insensitive): " + name + ".");
                if (byName.TryGetValue(name, out var named))
                {
                    if (Kind(named) != kind) throw new ArgumentException("Kind change refused for existing name: " + name + ".");
                    if (Cell(named, "Name") != name) throw new ArgumentException("Name collision (case-insensitive): " + name + ".");
                }
                rows.Add(Key(kind, name), row);
            }
            // A changed key cannot identify an existing object. Refuse a name-only replacement of an omitted row.
            var comparedColumns = table.Columns.Select((name, index) => new { name, index }).Where(c => c.name != "Name").ToArray();
            var omittedSignatures = new HashSet<string>(items.Where(item => !rows.ContainsKey(Key(Kind(item), Cell(item, "Name"))))
                .Select(item => string.Join("\0", comparedColumns.Select(c => Cell(item, c.name)))), StringComparer.Ordinal);
            foreach (var pair in rows.Where(p => !byKey.ContainsKey(p.Key)))
                if (omittedSignatures.Contains(string.Join("\0", comparedColumns.Select(c => pair.Value[c.index]))))
                    throw new ArgumentException("Renamed row key refused; keep the original Name and add new rows separately.");
            var ids = new HashSet<ulong>();
            foreach (var id in document.Descendants().Attributes("ID"))
                if (!ulong.TryParse(id.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong number) || !ids.Add(number))
                    throw new ArgumentException("Template IDs must be unique hexadecimal numbers.");
            ulong next = 0;
            string NewId()
            {
                while (ids.Contains(next)) next++;
                ids.Add(next); return (next++).ToString("X", CultureInfo.InvariantCulture);
            }
            int updated = 0, added = 0, unchanged = 0;
            var changes = new JsonArray();
            JsonObject Values(IEnumerable<string> values) => new JsonObject(table.Columns.Zip(values,
                (column, value) => new KeyValuePair<string, JsonNode?>(column, JsonValue.Create(value))));
            foreach (var pair in rows)
            {
                string[] row = pair.Value; string kind = row[kindIndex];
                bool existing = byKey.TryGetValue(pair.Key, out var item);
                var before = existing ? table.Columns.Select(c => Cell(item!, c)).ToArray() : null;
                bool same = before != null && before.SequenceEqual(row, StringComparer.Ordinal);
                if (kind == "SystemConstant" && !same) throw new ArgumentException("SystemConstant rows are read-only.");
                if (same) { unchanged++; continue; }
                if (existing) updated++;
                else
                {
                    added++;
                    item = new XElement(Table(document).Name.Namespace + Elements[Array.IndexOf(Kinds, kind)], new XAttribute("ID", NewId()),
                        new XAttribute("CompositionName", kind == "Tag" ? "Tags" : "UserConstants"),
                        new XElement(Table(document).Name.Namespace + "AttributeList"));
                    var list = Child(Table(document), "ObjectList");
                    if (list == null) { list = new XElement(Table(document).Name.Namespace + "ObjectList"); Table(document).Add(list); }
                    list.Add(item);
                }
                var attrs = Attributes(item!);
                string[] order = orders.TryGetValue(kind, out var existingOrder) ? existingOrder
                    : kind == "Tag" ? new[] { "DataTypeName", "LogicalAddress", "Name" } : new[] { "DataTypeName", "Name", "Value" };
                foreach (var column in table.Columns.Select((name, index) => new { name, index })
                    .Where(c => c.name != "Kind").OrderBy(c => {
                        int index = Array.IndexOf(order, Field(c.name)); return index < 0 ? int.MaxValue : index; }))
                {
                    if (kind == "Constant" && column.name == "ExternalAccessible") continue;
                    string value = row[column.index];
                    if (before != null && before[column.index] == value) continue;
                    if (CommentColumn(column.name)) { SetComment(item!, Language(column.name), value, NewId); continue; }
                    if ((column.name == "Address" && kind != "Tag" || column.name == "Value" && kind == "Tag") && value.Length != 0)
                        throw new ArgumentException("Address is for tags; Value is for constants.");
                    string field = Field(column.name);
                    var element = Child(attrs, field);
                    if (element?.HasElements == true) throw new ArgumentException("Only scalar attributes can be edited: " + field);
                    if (value.Length == 0) element?.Remove();
                    else if (element != null) element.Value = value;
                    else
                    {
                        element = new XElement(attrs.Name.Namespace + field, value);
                        int index = Array.IndexOf(order, field);
                        var following = attrs.Elements().FirstOrDefault(e => index >= 0 && Array.IndexOf(order, e.Name.LocalName) > index);
                        if (following == null) attrs.Add(element); else following.AddBeforeSelf(element);
                    }
                }
                if (changes.Count < 50) changes.Add(new JsonObject { ["kind"] = kind, ["name"] = row[nameIndex],
                    ["old"] = before == null ? null : Values(before), ["new"] = Values(row) });
            }
            return new PlcTagTableEdit(document, new JsonObject { ["updated"] = updated, ["added"] = added, ["unchanged"] = unchanged,
                ["keptFromTemplate"] = byKey.Keys.Count(key => !rows.ContainsKey(key)), ["changedRows"] = changes,
                ["changedRowsTruncated"] = updated + added > changes.Count });
        }

        private static void SetComment(XElement item, string language, string value, Func<string> newId)
        {
            var entry = Comments(item).SingleOrDefault(c => Value(Attributes(c), "Culture") == language);
            if (value.Length == 0) { entry?.Remove(); return; }
            if (entry != null) { Child(Attributes(entry), "Text")!.Value = value; return; }
            var objects = Child(item, "ObjectList");
            if (objects == null) { objects = new XElement(item.Name.Namespace + "ObjectList"); item.Add(objects); }
            var comment = objects.Elements().SingleOrDefault(e => e.Name.LocalName == "MultilingualText"
                && (string?)e.Attribute("CompositionName") == "Comment");
            if (comment == null)
            {
                comment = new XElement(item.Name.Namespace + "MultilingualText", new XAttribute("ID", newId()),
                    new XAttribute("CompositionName", "Comment"), new XElement(item.Name.Namespace + "ObjectList"));
                objects.Add(comment);
            }
            var list = Child(comment, "ObjectList");
            if (list == null) { list = new XElement(item.Name.Namespace + "ObjectList"); comment.Add(list); }
            list.Add(new XElement(item.Name.Namespace + "MultilingualTextItem", new XAttribute("ID", newId()),
                new XAttribute("CompositionName", "Items"), new XElement(item.Name.Namespace + "AttributeList",
                    new XElement(item.Name.Namespace + "Culture", language), new XElement(item.Name.Namespace + "Text", value))));
        }
    }
}
