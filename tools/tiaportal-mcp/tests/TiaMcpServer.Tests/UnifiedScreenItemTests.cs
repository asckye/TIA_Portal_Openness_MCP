using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

// Fakes live in the official namespaces so UnifiedScreenItemLogic resolves them exactly as it resolves the real API.
namespace Siemens.Engineering.HmiUnified.UI.Features { public interface IHmiBoxFeature { } public interface IHmiAreaFeature { } }
namespace Siemens.Engineering.HmiUnified.UI.Parts
{
    public sealed class HmiFontPart { public string Name { get; set; } = "Siemens Sans"; public uint Size { get; set; } = 12; public bool Bold { get; set; } }
    public sealed class HmiTrendPart { public string Name { get; set; } = ""; public byte LineWidth { get; set; } = 1; }
    public sealed class HmiTrendPartComposition : List<HmiTrendPart> { public HmiTrendPart Create(string name) { var t = new HmiTrendPart { Name = name }; Add(t); return t; } }
}
namespace Siemens.Engineering.HmiUnified.UI.Base
{
    public enum HmiFillPattern { Solid, Transparent }
    public class HmiScreenItemBase { public string Name { get; set; } = ""; public bool Visible { get; set; } = true; public HmiScreenItemBase? Parent { get; set; } public void Delete() { } }
    public class HmiShapeBase : HmiScreenItemBase, Features.IHmiBoxFeature { public int Left { get; set; } public int Top { get; set; } public uint Width { get; set; } = 10; public uint Height { get; set; } = 10; }
}
namespace Siemens.Engineering.HmiUnified.UI.Shapes
{
    public sealed class HmiCircle : Base.HmiShapeBase, Features.IHmiAreaFeature
    {
        public Color BackColor { get; set; } = Color.FromArgb(unchecked((int)0xFF112233)); public Base.HmiFillPattern BackFillPattern { get; set; }
        public Parts.HmiFontPart Font { get; } = new Parts.HmiFontPart(); public TiaMcpServer.Tests.UnifiedScreenItemFakes.MultilingualText Text { get; } = new TiaMcpServer.Tests.UnifiedScreenItemFakes.MultilingualText();
        public Parts.HmiTrendPartComposition Trends { get; } = new Parts.HmiTrendPartComposition();
    }
    public sealed class HmiText : Base.HmiShapeBase { public TiaMcpServer.Tests.UnifiedScreenItemFakes.MultilingualText Text { get; } = new TiaMcpServer.Tests.UnifiedScreenItemFakes.MultilingualText(); }
    public sealed class HmiCircleSegment : Base.HmiShapeBase { public ushort StartAngle { get; set; } }
}
namespace Siemens.Engineering.HmiUnified.UI.Widgets
{
    public sealed class HmiButton : Base.HmiShapeBase { public bool ReadOnly { get; set; } }
    public sealed class HmiWidgetBase : Base.HmiShapeBase { }
    // The real HmiSlider hides HmiBar.EventHandlers with a narrower composition; Type.GetProperty("EventHandlers") is ambiguous there.
    public class HmiBar : Base.HmiShapeBase { public List<int> EventHandlers { get; } = new List<int>(); }
    public sealed class HmiSlider : HmiBar { public new List<string> EventHandlers { get; } = new List<string>(); }
}
namespace Siemens.Engineering.HmiUnified.UI.Controls { public abstract class HmiAbstractControl : Base.HmiShapeBase { } }
namespace Siemens.Engineering.HmiUnified.UI.Screens { public sealed class HmiScreenWindow : Base.HmiShapeBase { public string Screen { get; set; } = ""; } public sealed class HmiScreen { public string Name { get; set; } = ""; } }
namespace Siemens.Engineering.HmiUnified.Other { public sealed class HmiCircle : UI.Base.HmiShapeBase { } }

namespace TiaMcpServer.Tests
{
    public static class UnifiedScreenItemFakes
    {
        // Minimal MultilingualText look-alike: only its type name matters to the splitter.
        public sealed class MultilingualText { public string Text { get; set; } = ""; }
    }

    internal static class UnifiedScreenItemTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }
        internal static void Run(Action<bool, string> check)
        {
            var assembly = typeof(UnifiedScreenItemTests).Assembly;
            var empty = new JsonObject();

            // Request gating.
            check(!UnifiedScreenItemLogic.ValidateRequest("read", "Item_1", "", empty, false, false), "screen item: read is never a write");
            check(!UnifiedScreenItemLogic.ValidateRequest("list", "", "", empty, false, false), "screen item: list needs no itemName");
            check(Fails<ArgumentException>(() => UnifiedScreenItemLogic.ValidateRequest("read", "", "", empty, false, true)), "screen item: read without itemName refused");
            check(Fails<ArgumentException>(() => UnifiedScreenItemLogic.ValidateRequest("create", "Item_1", "", empty, false, true)), "screen item: create without itemType refused");
            check(Fails<ArgumentException>(() => UnifiedScreenItemLogic.ValidateRequest("update", "Item_1", "HmiCircle", new JsonObject { ["Width"] = 1 }, false, true)), "screen item: itemType on update refused");
            check(Fails<ArgumentException>(() => UnifiedScreenItemLogic.ValidateRequest("update", "Item_1", "", empty, false, true)), "screen item: update without properties refused");
            check(Fails<ArgumentException>(() => UnifiedScreenItemLogic.ValidateRequest("delete", "Item_1", "", new JsonObject { ["Width"] = 1 }, true, true)), "screen item: properties on delete refused");
            check(Fails<ArgumentException>(() => UnifiedScreenItemLogic.ValidateRequest("read", "a/b", "", empty, false, true)), "screen item: path separator in itemName refused");
            check(Fails<ArgumentException>(() => UnifiedScreenItemLogic.ValidateRequest("update", "Item_1", "", new JsonObject { ["Width"] = 1 }, false, true, "Faceplate_1")), "screen item: containedType outside create refused");
            check(!UnifiedScreenItemLogic.ValidateRequest("delete", "Item_1", "", empty, false, true), "screen item: delete preview needs no confirmation");
            check(Fails<ArgumentException>(() => UnifiedScreenItemLogic.ValidateRequest("delete", "Item_1", "", empty, false, false)), "screen item: real delete without confirmDelete refused");
            check(UnifiedScreenItemLogic.ValidateRequest("delete", "Item_1", "", empty, true, false), "screen item: confirmed real delete is a write (sentinel)");
            check(UnifiedScreenItemLogic.ValidateRequest("create", "Item_1", "HmiCircle", empty, false, false, "Faceplate_1"), "screen item: create with containedType is a write (sentinel)");
            check(Fails<ArgumentException>(() => UnifiedScreenItemLogic.ParseProperties("[1]")), "screen item: non-object propertiesJson refused");

            // Catalog: concrete item types under the four UI namespaces, base classes and abstract/non-item classes excluded.
            var names = UnifiedScreenItemLogic.ItemTypes(assembly).Select(t => t.Name).ToArray();
            check(names.SequenceEqual(new[] { "HmiScreenWindow", "HmiCircle", "HmiCircleSegment", "HmiText", "HmiBar", "HmiButton", "HmiSlider" }), "catalog: HmiScreenWindow, shapes and widgets listed in namespace order; abstract control, *Base classes, HmiScreen and foreign-namespace HmiCircle excluded: " + string.Join(",", names));
            var catalog = UnifiedScreenItemLogic.Catalog(assembly);
            check(catalog.Count == names.Length && catalog[0]!["group"]!.GetValue<string>() == "Screens", "catalog rows carry the group (namespace suffix)");

            // Resolution: short name, without Hmi prefix, namespace-qualified, full name; unknown/ambiguous refused with hints.
            check(UnifiedScreenItemLogic.ResolveItemType(assembly, "HmiCircle").FullName == "Siemens.Engineering.HmiUnified.UI.Shapes.HmiCircle", "resolve: short name (foreign-namespace HmiCircle never competes)");
            check(UnifiedScreenItemLogic.ResolveItemType(assembly, "circle").Name == "HmiCircle", "resolve: name without Hmi prefix, case-insensitive");
            check(UnifiedScreenItemLogic.ResolveItemType(assembly, "Shapes.HmiCircle").Name == "HmiCircle", "resolve: namespace-qualified");
            check(UnifiedScreenItemLogic.ResolveItemType(assembly, "Siemens.Engineering.HmiUnified.UI.Widgets.HmiButton").Name == "HmiButton", "resolve: full CLR name");
            check(Fails<ArgumentException>(() => UnifiedScreenItemLogic.ResolveItemType(assembly, "HmiWidgetBase")), "resolve: base class is not a placeable item");
            check(Fails<ArgumentException>(() => UnifiedScreenItemLogic.ResolveItemType(assembly, "Circ")), "resolve: partial name refused (no fuzzy pick)");
            try { UnifiedScreenItemLogic.ResolveItemType(assembly, "Circ"); check(false, "unreachable"); }
            catch (ArgumentException ex) { check(ex.Message.Contains("HmiCircle") && ex.Message.Contains("HmiCircleSegment"), "resolve: unknown name lists similar types"); }
            check(Fails<ArgumentException>(() => UnifiedScreenItemLogic.ResolveItemType(assembly, "")), "resolve: empty refused");

            // Schema and features.
            var circle = typeof(global::Siemens.Engineering.HmiUnified.UI.Shapes.HmiCircle);
            var description = UnifiedScreenItemLogic.TypeDescription(circle);
            var features = description["features"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            check(features.SequenceEqual(new[] { "IHmiAreaFeature", "IHmiBoxFeature" }), "schema: feature interfaces incl. inherited ones, sorted");
            check(description["baseTypes"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(new[] { "HmiShapeBase", "HmiScreenItemBase" }), "schema: base chain stops at the Siemens root");
            check(description["deletable"]!.GetValue<bool>() && description["group"]!.GetValue<string>() == "Shapes", "schema: Delete availability and group");
            var props = description["properties"]!.AsArray().ToDictionary(p => p!["name"]!.GetValue<string>(), p => (JsonObject)p!);
            check(!props.ContainsKey("Parent"), "schema: backlink Parent excluded");
            check(props["BackColor"]["kind"]!.GetValue<string>() == "color" && props["Width"]["kind"]!.GetValue<string>() == "scalar" && props["Text"]["kind"]!.GetValue<string>() == "multilingual", "schema: color/scalar/multilingual kinds");
            check(props["BackFillPattern"]["enumValues"]!.AsArray().Count == 2, "schema: enum values listed");
            check(props["Font"]["kind"]!.GetValue<string>() == "part" && props["Font"]["properties"]!.AsArray().Any(p => p!["name"]!.GetValue<string>() == "Size"), "schema: part expanded with its own properties");
            check(props["Trends"]["kind"]!.GetValue<string>() == "collection" && props["Trends"]["elementType"]!.GetValue<string>() == "HmiTrendPart" && props["Trends"]["create"]!.AsArray()[0]!.GetValue<string>() == "Create(String)", "schema: collection element type and Create signature");
            check(props["Font"]["writable"]!.GetValue<bool>() == false && props["Width"]["writable"]!.GetValue<bool>(), "schema: writability from the setter");
            check(UnifiedScreenItemLogic.TypeDescription(circle, 0)["properties"]!.AsArray().First(p => p!["name"]!.GetValue<string>() == "Font")!["properties"] == null, "schema: depth 0 does not expand parts");

            // Property splitting: multilingual texts per culture, everything else stays a nested edit.
            var (plain, texts) = UnifiedScreenItemLogic.SplitProperties(circle, new JsonObject { ["Width"] = 20, ["Font"] = new JsonObject { ["Size"] = 14 }, ["Text"] = new JsonObject { ["en-US"] = "Start", ["de-DE"] = "" } });
            check(plain.Count == 2 && plain.ContainsKey("Font") && texts.Count == 2 && texts[0] == ("Text", "en-US", "Start") && texts[1].Text == "", "split: nested part kept, two cultures split out (empty clears)");
            check(Fails<ArgumentException>(() => UnifiedScreenItemLogic.SplitProperties(circle, new JsonObject { ["Text"] = "Start" })), "split: plain string on a MultilingualText refused");
            check(Fails<ArgumentException>(() => UnifiedScreenItemLogic.SplitProperties(circle, new JsonObject { ["Text"] = new JsonObject() })), "split: empty culture map refused");
            check(Fails<ArgumentException>(() => UnifiedScreenItemLogic.SplitProperties(circle, new JsonObject { ["Text"] = new JsonObject { ["en-US"] = 5 } })), "split: non-string text refused");
            // The plain part goes through the shared nested-edit validator: unknown property and collection edits are refused there.
            check(Fails<NotSupportedException>(() => UnifiedUiModelLogic.PrepareNested(circle, new JsonObject { ["Bogus"] = 1 })), "split+prepare: unknown property refused");
            check(Fails<NotSupportedException>(() => UnifiedUiModelLogic.PrepareNested(circle, new JsonObject { ["Trends"] = new JsonObject { ["x"] = 1 } })), "split+prepare: collection edit refused (use ManageUnifiedObjectParts)");
            var prepared = UnifiedUiModelLogic.PrepareNested(circle, plain);
            check(prepared.Count == 2 && prepared.Any(e => e.Name == "Font.Size") && prepared.Any(e => e.Name == "Width"), "split+prepare: nested leaf edits prepared (sentinel)");
            var item = new global::Siemens.Engineering.HmiUnified.UI.Shapes.HmiCircle { Name = "Circle_1" };
            var meta = new JsonObject(); UnifiedUiModelLogic.ApplyNested(item, prepared, meta);
            check(item.Width == 20 && item.Font.Size == 14 && meta["appliedProperties"]!.AsArray().Count == 2, "apply: values land on the fake item and are read back");

            // Hidden properties: the most derived declaration wins, once.
            var slider = typeof(global::Siemens.Engineering.HmiUnified.UI.Widgets.HmiSlider);
            check(Fails<System.Reflection.AmbiguousMatchException>(() => slider.GetProperty("EventHandlers")), "hiding: the fake reproduces the ambiguous lookup of the real API (sentinel)");
            check(UnifiedUiModelLogic.FindProperty(slider, "EventHandlers")!.PropertyType == typeof(List<string>), "hiding: FindProperty returns the most derived declaration");
            check(UnifiedUiModelLogic.FindProperty(slider, "Width")!.DeclaringType!.Name == "HmiShapeBase" && UnifiedUiModelLogic.FindProperty(slider, "Nope") == null, "hiding: inherited property found, unknown is null");
            var sliderProps = UnifiedScreenItemLogic.Schema(slider).Select(p => p!["name"]!.GetValue<string>()).ToArray();
            check(sliderProps.Count(n => n == "EventHandlers") == 1 && sliderProps.SequenceEqual(sliderProps.OrderBy(n => n, StringComparer.Ordinal)), "hiding: schema lists EventHandlers once, sorted");
            check(UnifiedScreenItemLogic.ListRow(new global::Siemens.Engineering.HmiUnified.UI.Widgets.HmiSlider { Name = "S1" }, 0)["name"]!.GetValue<string>() == "S1", "hiding: list row on a hiding type does not throw");

            // List rows.
            var row = UnifiedScreenItemLogic.ListRow(item, 3);
            check(row["name"]!.GetValue<string>() == "Circle_1" && row["type"]!.GetValue<string>() == "HmiCircle" && row["group"]!.GetValue<string>() == "Shapes" && row["width"]!.GetValue<uint>() == 20 && row["visible"]!.GetValue<bool>() && row["index"]!.GetValue<int>() == 3, "list row: identity, group and geometry");
            check(UnifiedScreenItemLogic.ListRow(new global::Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen { Name = "S" }, 0)["left"] == null, "list row: absent geometry simply omitted");
        }
    }
}
