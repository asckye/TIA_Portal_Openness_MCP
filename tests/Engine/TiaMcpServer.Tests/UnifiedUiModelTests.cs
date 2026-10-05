using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Pure-logic coverage for the UnifiedUiModel family: color conversion, nested property edits,
    // event/part/dynamization selection and refusal paths, exercised on fakes shaped like the official API.
    internal static class UnifiedUiModelTests
    {
        public enum FakeEventType { Click, Press, Release }
        public enum FakePropertyEventType { Change, QualityCodeChange }
        public enum FakeCondition { None, Range, Bitmask }
        public sealed class Script { public string ScriptCode { get; set; } = ""; public string GlobalDefinitionAreaScriptCode { get; set; } = ""; public bool Async { get; set; } }
        public sealed class Handler { public FakeEventType EventType { get; set; } public Script Script { get; set; } = new Script(); }
        public sealed class PropertyHandler { public FakePropertyEventType EventType { get; set; } public string PropertyName { get; set; } = ""; public Script Script { get; set; } = new Script(); }
        public sealed class HandlerComposition : List<Handler> { public Handler Create(FakeEventType type) { var h = new Handler { EventType = type }; Add(h); return h; } }
        public sealed class PropertyHandlerComposition : List<PropertyHandler> { public PropertyHandler Create(string name, FakePropertyEventType type) { var h = new PropertyHandler { PropertyName = name, EventType = type }; Add(h); return h; } }
        public sealed class Threshold { public string Name { get; set; } = ""; public Color Color { get; set; } = Color.FromArgb(unchecked((int)0xFF00FF00)); public bool Enable { get; set; } public object Parent { get; set; } = new object(); public void Delete() { } }
        public sealed class ThresholdComposition : List<Threshold> { public Threshold Create(string name) { var t = new Threshold { Name = name }; Add(t); return t; } public Threshold? Find(string name) => this.FirstOrDefault(t => t.Name == name); }
        public sealed class Trend { public byte LineWidth { get; set; } = 1; public ThresholdComposition Thresholds { get; } = new ThresholdComposition(); public void Delete() { } }
        public sealed class TrendComposition : List<Trend> { public Trend Create() { var t = new Trend(); Add(t); return t; } }
        public sealed class Column { public string Name { get; set; } = ""; }
        public sealed class ColumnComposition : List<Column> { }
        public class Entry { public object? Value { get; set; } public object? From { get; set; } public bool Flashing { get; set; } }
        public sealed class EntryComposition : List<Entry> { public T Create<T>() where T : Entry, new() { var e = new T(); Add(e); return e; } }
        public sealed class MappingTable { public FakeCondition ConditionType { get; set; } public EntryComposition Entries { get; } = new EntryComposition(); }
        public sealed class ValueConverter { public string Formula { get; set; } = ""; public bool IsFormulaSelected { get; set; } public MappingTable MappingTable { get; } = new MappingTable(); }
        public class TagDynamization
        {
            public string PropertyName { get; set; } = ""; public string Tag { get; set; } = ""; public bool ReadOnly { get; set; }
            public string Address { get; } = "%DB1.DBX0.0"; public ValueConverter ValueConverter { get; } = new ValueConverter();
            private bool stuck; public bool Stuck { get => stuck; set { } }
        }
        public sealed class DynamizationComposition : List<TagDynamization> { public T Create<T>(string propertyName) where T : TagDynamization, new() { var d = new T { PropertyName = propertyName }; Add(d); return d; } }
        public sealed class Item
        {
            public string Name { get; set; } = "Item_1"; public Color BackColor { get; set; } = Color.FromArgb(unchecked((int)0x80112233)); public uint Width { get; set; } = 10;
            public HandlerComposition EventHandlers { get; } = new HandlerComposition();
            public PropertyHandlerComposition PropertyEventHandlers { get; } = new PropertyHandlerComposition();
            public DynamizationComposition Dynamizations { get; } = new DynamizationComposition();
            public ThresholdComposition Thresholds { get; } = new ThresholdComposition();
            public TrendComposition Trends { get; } = new TrendComposition();
            public ColumnComposition Columns { get; } = new ColumnComposition();
            public Item? Parent { get; set; }
        }
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }
        internal static void Run(Action<bool, string> check)
        {
            Console.WriteLine("== Unified UI model: colors, nested edits, events, parts, dynamizations ==");
            // Colors
            check(UnifiedUiModelLogic.ParseColor(JsonValue.Create("#80112233")).ToArgb() == unchecked((int)0x80112233), "ARGB hex color parsed");
            check(UnifiedUiModelLogic.ParseColor(JsonValue.Create("#112233")).A == 255, "RGB hex color gets opaque alpha");
            check(UnifiedUiModelLogic.ColorText(Color.FromArgb(unchecked((int)0x80112233))) == "#80112233", "color rendered as #AARRGGBB");
            foreach (var bad in new[] { "red", "#12", "#GG112233", "" }) check(Fails<ArgumentException>(() => UnifiedUiModelLogic.ParseColor(JsonValue.Create(bad))), "invalid color refused: " + bad);
            check(Fails<ArgumentException>(() => UnifiedUiModelLogic.ParseColor(JsonValue.Create(5))), "numeric color refused");
            check(UnifiedUiModelLogic.SameValue(Color.FromArgb(255, 1, 2, 3), Color.FromArgb(unchecked((int)0xFF010203))) && !UnifiedUiModelLogic.SameValue(Color.Red, Color.Blue), "color readback compares ARGB only");
            var item = new Item();
            var scalars = UnifiedUiModelLogic.Scalars(item);
            check(scalars["values"]!["BackColor"]!.ToString() == "#80112233" && !((JsonArray)scalars["excludedComplexProperties"]!).Any(n => n!.ToString() == "BackColor") && scalars["values"]!["Width"]!.GetValue<uint>() == 10, "scalars include colors and drop them from exclusions");
            // Nested edits
            var edits = UnifiedUiModelLogic.PrepareNested(typeof(TagDynamization), (JsonObject)JsonNode.Parse("{\"Tag\":\"Motor\",\"ReadOnly\":true,\"ValueConverter\":{\"Formula\":\"x*2\",\"MappingTable\":{\"ConditionType\":\"Range\"}}}")!);
            check(edits.Count == 4 && edits.Any(e => e.Name == "ValueConverter.MappingTable.ConditionType" && Equals(e.Value, FakeCondition.Range)), "nested scalar edits resolved through readable sub-objects");
            var dyn = new TagDynamization(); var meta = new JsonObject();
            UnifiedUiModelLogic.ApplyNested(dyn, edits, meta);
            check(dyn.Tag == "Motor" && dyn.ReadOnly && dyn.ValueConverter.Formula == "x*2" && dyn.ValueConverter.MappingTable.ConditionType == FakeCondition.Range && meta["appliedProperties"]!.AsArray().Count == 4, "nested edits applied and read back");
            check(Fails<NotSupportedException>(() => UnifiedUiModelLogic.PrepareNested(typeof(TagDynamization), (JsonObject)JsonNode.Parse("{\"Address\":\"x\"}")!)), "read-only CLR property refused before any write");
            check(Fails<NotSupportedException>(() => UnifiedUiModelLogic.PrepareNested(typeof(TagDynamization), (JsonObject)JsonNode.Parse("{\"Missing\":1}")!)), "unknown property refused");
            check(Fails<ArgumentException>(() => UnifiedUiModelLogic.PrepareNested(typeof(Item), (JsonObject)JsonNode.Parse("{\"Name\":\"x\"}")!)), "rename via propertiesJson refused");
            check(Fails<NotSupportedException>(() => UnifiedUiModelLogic.PrepareNested(typeof(TagDynamization), (JsonObject)JsonNode.Parse("{\"ValueConverter\":{\"MappingTable\":{\"Entries\":{}}}}")!)), "collections refused as nested properties");
            check(Fails<NotSupportedException>(() => UnifiedUiModelLogic.PrepareNested(typeof(TagDynamization), (JsonObject)JsonNode.Parse("{\"ValueConverter\":\"text\"}")!)), "complex property needs a JSON object");
            check(Fails<ArgumentException>(() => UnifiedUiModelLogic.PrepareNested(typeof(Item), (JsonObject)JsonNode.Parse("{\"BackColor\":\"blue\"}")!)), "invalid color value refused at prepare");
            var colorEdit = UnifiedUiModelLogic.PrepareNested(typeof(Item), (JsonObject)JsonNode.Parse("{\"BackColor\":\"#FF0000FF\"}")!);
            UnifiedUiModelLogic.ApplyNested(item, colorEdit, new JsonObject());
            check(item.BackColor.ToArgb() == unchecked((int)0xFF0000FF), "color edit applied");
            var stuck = UnifiedUiModelLogic.PrepareNested(typeof(TagDynamization), (JsonObject)JsonNode.Parse("{\"Stuck\":true}")!); var stuckMeta = new JsonObject();
            check(Fails<InvalidOperationException>(() => UnifiedUiModelLogic.ApplyNested(dyn, stuck, stuckMeta)) && stuckMeta["mayHaveChanged"]!.GetValue<bool>(), "ignored setter detected by readback with mayHaveChanged set");
            // Pagination
            var all = new JsonArray(Enumerable.Range(0, 7).Select(i => (JsonNode)new JsonObject { ["i"] = i, ["dataComplete"] = true }).ToArray());
            var page = UnifiedUiModelLogic.Page(all, 5, 5, new JsonObject());
            check(page["actualCount"]!.GetValue<int>() == 2 && page["nextOffset"] == null && !page["dataComplete"]!.GetValue<bool>() && !page["truncated"]!.GetValue<bool>(), "tail page is honest about completeness");
            var first = UnifiedUiModelLogic.Page(all, 0, 500, new JsonObject());
            check(first["dataComplete"]!.GetValue<bool>() && first["expectedCount"]!.GetValue<int>() == 7, "full page complete");
            check(Fails<ArgumentException>(() => UnifiedUiModelLogic.Page(all, 0, 501, new JsonObject())) && Fails<ArgumentException>(() => UnifiedUiModelLogic.Page(all, -1, 1, new JsonObject())), "pagination bounds enforced");
            // Events
            item.EventHandlers.Create(FakeEventType.Click).Script.ScriptCode = "go()";
            item.PropertyEventHandlers.Create("Visible", FakePropertyEventType.Change);
            var rows = UnifiedUiModelLogic.EventRows(item);
            check(rows.Count == 2 && rows[0]!["source"]!.ToString() == "EventHandlers" && rows[0]!["eventType"]!.ToString() == "Click" && rows[0]!["ScriptCode"]!.ToString() == "go()" && rows[0]!["propertyName"] == null
                && rows[1]!["source"]!.ToString() == "PropertyEventHandlers" && rows[1]!["propertyName"]!.ToString() == "Visible", "all screen and property events enumerated with script fields");
            var capabilities = UnifiedUiModelLogic.EventCapabilities(item);
            check(capabilities["EventHandlers"]!["availableEventTypes"]!.AsArray().Count == 3 && capabilities["PropertyEventHandlers"]!["availableEventTypes"]!.AsArray().Select(n => n!.ToString()).SequenceEqual(new[] { "Change", "QualityCodeChange" }), "creatable event types come from the native enum");
            check(UnifiedUiModelLogic.EventCapabilities(new Threshold())["EventHandlers"] == null && UnifiedUiModelLogic.EventRows(new Threshold()).Count == 0, "objects without event compositions report null capability");
            // Parts
            check(Fails<ArgumentException>(() => UnifiedUiModelLogic.Composition(item, "")) && Fails<NotSupportedException>(() => UnifiedUiModelLogic.Composition(item, "Nope")) && Fails<NotSupportedException>(() => UnifiedUiModelLogic.Composition(item, "Name")), "collection property must be an exact public collection");
            var thresholds = UnifiedUiModelLogic.Composition(item, "Thresholds"); item.Thresholds.Create("High"); item.Thresholds.Create("Low");
            check(UnifiedUiModelLogic.Named(thresholds) && !UnifiedUiModelLogic.Named(UnifiedUiModelLogic.Composition(item, "Trends")), "named vs unnamed part detection");
            check(ReferenceEquals(UnifiedUiModelLogic.FindPart(thresholds, "Low", -1), item.Thresholds[1]) && UnifiedUiModelLogic.FindPart(thresholds, "low", -1) == null, "named part lookup is exact and case-sensitive");
            check(Fails<ArgumentException>(() => UnifiedUiModelLogic.FindPart(thresholds, "", -1)) && Fails<ArgumentException>(() => UnifiedUiModelLogic.FindPart(thresholds, "High", 0)), "exactly one part selector required");
            item.Thresholds.Create("High");
            check(Fails<InvalidOperationException>(() => UnifiedUiModelLogic.FindPart(thresholds, "High", -1)), "ambiguous part name refused");
            var trends = UnifiedUiModelLogic.Composition(item, "Trends"); item.Trends.Create();
            check(ReferenceEquals(UnifiedUiModelLogic.FindPart(trends, "", 0), item.Trends[0]) && UnifiedUiModelLogic.FindPart(trends, "", 1) == null, "unnamed part addressed by index");
            check(Fails<ArgumentException>(() => UnifiedUiModelLogic.FindPart(trends, "x", -1)), "name selector refused on unnamed parts");
            var named = UnifiedUiModelLogic.PartCreation(thresholds, "Mid", "");
            check(named.Method.GetParameters().Length == 1 && (string)named.Args[0] == "Mid", "named composition uses Create(string)");
            check(Fails<ArgumentException>(() => UnifiedUiModelLogic.PartCreation(thresholds, "", "")), "named composition requires partName");
            check(UnifiedUiModelLogic.PartCreation(trends, "", "Trend").Method.GetParameters().Length == 0 && Fails<ArgumentException>(() => UnifiedUiModelLogic.PartCreation(trends, "x", "")), "unnamed composition uses Create() and refuses partName");
            check(Fails<ArgumentException>(() => UnifiedUiModelLogic.PartCreation(trends, "", "Threshold")), "partKind mismatch refused");
            check(Fails<NotSupportedException>(() => UnifiedUiModelLogic.PartCreation(UnifiedUiModelLogic.Composition(item, "Columns"), "C1", "")), "composition without native Create is NotSupported");
            var collections = UnifiedUiModelLogic.PartCollections(item);
            var trendRow = collections.First(r => r!["property"]!.ToString() == "Trends")!; var columnRow = collections.First(r => r!["property"]!.ToString() == "Columns")!;
            check(trendRow["create"]!.AsArray().Count == 1 && trendRow["deletable"]!.GetValue<bool>() && !trendRow["named"]!.GetValue<bool>() && columnRow["create"]!.AsArray().Count == 0 && !columnRow["deletable"]!.GetValue<bool>(), "collection inventory reports create/delete/name capabilities");
            // Dynamizations
            var dynamizations = UnifiedUiModelLogic.Composition(item, "Dynamizations");
            check(UnifiedUiModelLogic.FindDynamization(dynamizations, "Visible") == null && Fails<ArgumentException>(() => UnifiedUiModelLogic.FindDynamization(dynamizations, " ")), "missing dynamization is null; blank property refused");
            var created = (TagDynamization)UnifiedUiModelLogic.GenericCreate(dynamizations, typeof(TagDynamization), 1).Invoke(dynamizations, new object[] { "Visible" })!;
            check(ReferenceEquals(UnifiedUiModelLogic.FindDynamization(dynamizations, "Visible"), created), "generic Create<T>(propertyName) resolved and result found by PropertyName");
            check(Fails<NotSupportedException>(() => UnifiedUiModelLogic.GenericCreate(dynamizations, typeof(TagDynamization), 0)), "wrong Create<T> arity is NotSupported");
            item.Dynamizations.Add(new TagDynamization { PropertyName = "Visible" });
            check(Fails<InvalidOperationException>(() => UnifiedUiModelLogic.FindDynamization(dynamizations, "Visible")), "duplicate dynamization is ambiguous");
            check(Fails<ArgumentException>(() => UnifiedUiModelLogic.DynamizationType(dynamizations, "Colour")) && Fails<NotSupportedException>(() => UnifiedUiModelLogic.DynamizationType(dynamizations, "Flashing")), "unknown kind refused; missing native type NotSupported");
            check(UnifiedUiModelLogic.KindOf(created) == "TagDynamization" && UnifiedUiModelLogic.DynamizationKinds.Count == 6, "kind fallback and kind table");
            check(UnifiedUiModelLogic.MappingTablePath(typeof(TagDynamization)).SequenceEqual(new[] { "ValueConverter", "MappingTable", "Entries" }) && UnifiedUiModelLogic.MappingTablePath(typeof(Threshold)).Length == 0, "mapping table path only for converter-bearing kinds");
            Type Resolve(string n) => typeof(Entry);
            var entries = UnifiedUiModelLogic.ParseMappingEntries("[{\"kind\":\"Range\",\"From\":0,\"Value\":\"#FF00FF00\",\"Flashing\":true}]", Resolve);
            check(entries.Count == 1 && entries[0].Kind == "Range" && !entries[0].Properties.ContainsKey("kind"), "mapping entries parsed with kind stripped");
            check(Fails<ArgumentException>(() => UnifiedUiModelLogic.ParseMappingEntries("{}", Resolve)) && Fails<ArgumentException>(() => UnifiedUiModelLogic.ParseMappingEntries("[{\"kind\":\"Other\"}]", Resolve)) && Fails<ArgumentException>(() => UnifiedUiModelLogic.ParseMappingEntries("[{\"From\":1}]", Resolve)), "mapping entry array/kind validation");
            check(Fails<NotSupportedException>(() => UnifiedUiModelLogic.ParseMappingEntries("[{\"kind\":\"Simple\",\"Nope\":1}]", Resolve)), "mapping entry properties validated against the entry type");
            var entry = (Entry)UnifiedUiModelLogic.GenericCreate(created.ValueConverter.MappingTable.Entries, typeof(Entry), 0).Invoke(created.ValueConverter.MappingTable.Entries, Array.Empty<object>())!;
            UnifiedUiModelLogic.ApplyNested(entry, UnifiedUiModelLogic.PrepareNested(typeof(Entry), entries[0].Properties), new JsonObject());
            check(Equals(entry.From, 0) && entry.Flashing && entry.Value is string, "object-typed entry values applied");
            var tree = UnifiedUiModelLogic.Tree(created, 4);
            check(tree["children"]!["ValueConverter"]!["children"]!["MappingTable"]!["children"]!["Entries"]!.AsArray().Count == 1 && tree["values"]!["Address"] != null, "bounded tree reads nested converter, table and entries");
            item.Parent = item;
            check(UnifiedUiModelLogic.Tree(item, 3)["children"]!["Parent"] == null, "tree never follows backlinks");
            // Screens, lists, categories
            check(UnifiedUiModelLogic.ValidateScreenName("Main") == "Main" && Fails<ArgumentException>(() => UnifiedUiModelLogic.ValidateScreenName("a/b")) && Fails<ArgumentException>(() => UnifiedUiModelLogic.ValidateScreenName(" ")), "screen name validation");
            check(!UnifiedUiModelLogic.IsScreen(item) && !UnifiedUiModelLogic.IsScreenComposition(item), "screen type checks are exact");
            var self = UnifiedUiModelLogic.SelfDescription(item);
            check(!self["engineeringObject"]!.GetValue<bool>() && !self["contentsRead"]!.GetValue<bool>(), "self-description never claims contents were read");
            check(UnifiedUiModelLogic.ListCollection("textLists") == "HmiTextLists" && Fails<ArgumentException>(() => UnifiedUiModelLogic.ListCollection("lists")), "list category mapping");
            check(UnifiedUiModelLogic.AlarmCommonCollection("alarmClasses") == "AlarmClasses" && Fails<ArgumentException>(() => UnifiedUiModelLogic.AlarmCommonCollection("alarms")), "alarm category mapping");
            check(UnifiedUiModelLogic.AuditCollection("auditTrails") == "AuditTrails" && UnifiedUiModelLogic.AuditCollection("alarmAuditClasses") == "HmiAlarmAuditClass" && Fails<ArgumentException>(() => UnifiedUiModelLogic.AuditCollection("audit")), "audit category mapping");
        }
    }
}
