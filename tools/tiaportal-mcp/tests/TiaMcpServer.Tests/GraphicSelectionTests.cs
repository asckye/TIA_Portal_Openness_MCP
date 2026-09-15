using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using TiaMcpServer.ModelContextProtocol;

namespace Siemens.Engineering
{
    // Self-description boundary only; no Siemens runtime is loaded offline.
    internal interface IEngineeringObject { List<TiaMcpServer.Tests.GraphicSelectionTests.Info> GetAttributeInfos(); List<TiaMcpServer.Tests.GraphicSelectionTests.Info> GetCompositionInfos(); object GetAttribute(string name); }
}
namespace TiaMcpServer.Tests
{
    internal static class GraphicSelectionTests
    {
        internal const string NamesJson = "[\"fgxTop\",\"txtTop\",\"gfxBotom\",\"txtBotom\"]";
        public sealed class Info { public string Name { get; set; } = ""; public string AccessMode { get; set; } = "Read"; }
        public sealed class Screen
        {
            public string Name => "Main";
            public object Parent => throw new Exception("Parent traversal exceeded one hop");
            public List<object> ScreenItems { get; } = new List<object>();
        }
        public class Item
        {
            public string Name { get; set; } = "";
            public Screen Parent { get; set; } = new Screen();
            public virtual int Left { get; set; }
            public int Top { get; set; }
            public uint Width { get; set; } = 38;
            public uint Height { get; set; } = 42;
            public string ScriptDiagnosisOverviewText => throw new Exception("Unrelated getter must not be traversed");
        }
        public sealed class Broken : Item { public override int Left { get => throw new ObjectDisposedException("software"); set { } } }
        public sealed class Counted : Item { public int Reads; public override int Left { get { Reads++; return base.Left; } set { base.Left = value; } } }
        public sealed class PrivateGeometry
        {
            public string Name => "Private";
            public int Left { private get; set; }
            public int Top => 0; public uint Width => 1; public uint Height => 1;
            public Screen Parent { get; } = new Screen();
        }
        public sealed class AttributeGeometry : global::Siemens.Engineering.IEngineeringObject
        {
            public string Name => "Attributes";
            public Screen Parent { get; } = new Screen();
            public int UnsafeReads;
            public object ContainedItems => throw new Exception("Unverified group composition getter");
            public List<Info> GetCompositionInfos() => new List<Info> { new Info { Name = "ContainedItems" } };
            public List<Info> GetAttributeInfos() => new List<Info> {
                new Info { Name = "Left" }, new Info { Name = "Top" }, new Info { Name = "Width" }, new Info { Name = "Height" },
                new Info { Name = "GroupId" }, new Info { Name = "LayoutSecret", AccessMode = "Write" } };
            public object GetAttribute(string name)
            {
                if (name == "LayoutSecret") { UnsafeReads++; throw new Exception("write-only"); }
                return name == "GroupId" ? "native-raw-id" : (object)1;
            }
        }
        private static JsonArray Pages(Portal portal, string names = NamesJson, int size = 2)
        {
            var pages = new JsonArray(); string cursor = "";
            do
            {
                var page = portal.ReadUnifiedGraphicSelection("HMI", "Project_A", "/Main", names, cursor, size).Meta!;
                pages.Add(page.DeepClone());
                cursor = page["nextCursor"]?.ToString() ?? "";
                if (pages.Count > 30) throw new Exception("Pagination did not terminate");
            } while (cursor != "");
            return pages;
        }
        private static JsonObject[] Rows(JsonArray pages) => pages.SelectMany(p => (JsonArray)p!["records"]!).OfType<JsonObject>().ToArray();
        private static bool Refused(JsonArray before, JsonArray after) => new Portal().CompareUnifiedGraphicSelections(before.ToJsonString(), after.ToJsonString()).Meta!["success"]!.GetValue<bool>() == false;
        internal static void Run(Action<bool, string> check)
        {
            Console.WriteLine("== Exact graphical selections and offline coordinate comparison ==");
            var hmi = new global::Siemens.Engineering.HmiUnified.HmiSoftware(); var screen = new Screen(); hmi.Screens.Add(screen);
            foreach (var name in UnifiedGraphicSelection.Names(NamesJson)) screen.ScreenItems.Add(new Item { Name = name, Parent = screen, Left = name.StartsWith("txt") ? -22 : 4 });
            var portal = new Portal { FixtureRoot = hmi };
            var before = Pages(portal); var rows = Rows(before);
            check(rows.Length == 5 && rows.Take(4).All(r => r["geometryComplete"]!.GetValue<bool>()), "four exact names and complete geometry, no deep property traversal");
            check(rows[0]["owner"]!["name"]!["value"]!.ToString() == "Main", "one-hop owner identity stops before owner's Parent");
            var summary = rows.Last();
            check(summary["rawCoordinateEnvelope"]!["left"]!.GetValue<decimal>() == -22 && summary["rawCoordinateEnvelope"]!["width"]!.GetValue<decimal>() == 64, "negative coordinates included in raw envelope");
            check(before.Last()!["traversalComplete"]!.GetValue<bool>() && !before.Last()!["dataComplete"]!.GetValue<bool>() && summary["nativeGroupBounds"] == null, "successful traversal does not claim native graphical group coverage");
            check(summary["status"]!.ToString() == "unsupported" && summary["code"]!.ToString() == "NativeGraphicGroupUnverified", "native group gap explicit, not interpreted as ungrouped");
            var original = before.ToJsonString();
            ((Item)screen.ScreenItems[0]).Left += 7; ((Item)screen.ScreenItems[1]).Top += 3;
            var after = Pages(portal); var comparison = portal.CompareUnifiedGraphicSelections(original, after.ToJsonString()).Meta!;
            check(comparison["success"]!.GetValue<bool>() && comparison["changedObjectCount"]!.GetValue<int>() == 2, "before/after identifies both changed objects including sibling");
            check(comparison["objects"]![0]!["fields"]!["Left"]!["delta"]!.GetValue<decimal>() == 7 && !comparison["causalityEstablished"]!.GetValue<bool>(), "delta retained without claiming causality");
            check(portal.CompareUnifiedGraphicSelections(original, original).Meta!["changedObjectCount"]!.GetValue<int>() == 0, "identical complete snapshots compare unchanged");
            var missingPage = (JsonArray)before.DeepClone(); missingPage.RemoveAt(0);
            check(Refused(missingPage, after), "missing first page rejected");
            var unfinished = (JsonArray)before.DeepClone(); unfinished.RemoveAt(unfinished.Count - 1);
            check(Refused(unfinished, after), "missing final page rejected");
            var mixed = (JsonArray)before.DeepClone(); mixed[1]!["collectionId"] = "other";
            check(Refused(mixed, after), "mixed collection pages rejected");
            var changedScope = (JsonArray)after.DeepClone(); foreach (var p in changedScope) p!["scope"]!["softwarePath"] = "Other";
            check(Refused(before, changedScope), "different HMI scope rejected");
            var missingValue = (JsonArray)after.DeepClone(); missingValue[0]!["records"]![0]!["fields"]!["Left"]!["value"] = null;
            check(Refused(before, missingValue), "missing coordinate cannot masquerade as unchanged");
            var wrongType = (JsonArray)after.DeepClone(); wrongType[0]!["records"]![0]!["type"] = "Replacement";
            check(Refused(before, wrongType), "object type replacement rejected");
            var near = Rows(Pages(portal, "[\"gfxTop\",\"fgxTop\"]"));
            check(near[0]["type"] == null && near[1]["name"]!.ToString() == "fgxTop", "fgx spelling is not guessed or corrected to gfx");
            check(portal.ReadUnifiedGraphicSelection("HMI", "Project_A", "/Main", "[\"x\",\"x\"]").Meta!["success"]!.GetValue<bool>() == false, "duplicate selection rejected");
            int resolves = portal.FixtureResolveCalls;
            check(!portal.ReadUnifiedGraphicSelection("HMI", "Wrong", "/Main", NamesJson).Meta!["success"]!.GetValue<bool>() && portal.FixtureResolveCalls == resolves, "project mismatch refuses HMI access");
            screen.ScreenItems.Add(new PrivateGeometry());
            check(!Rows(Pages(portal, "[\"Private\"]"))[0]["geometryComplete"]!.GetValue<bool>(), "private getter not invoked");
            var attr = new AttributeGeometry(); screen.ScreenItems.Add(attr);
            var attrRow = Rows(Pages(portal, "[\"Attributes\"]"))[0];
            check(attrRow["geometryComplete"]!.GetValue<bool>() && attr.UnsafeReads == 0, "advertised readable attributes supported; write-only attributes not read");
            check(attrRow["relationEvidence"]![0]!["value"]!.ToString() == "native-raw-id" && !attrRow["nativeGroupVerified"]!.GetValue<bool>(), "raw GroupId retained without fabricating membership semantics");
            check(attrRow["relationCapabilities"]!.AsArray().Any(c => c?["source"]?.ToString() == "IEngineeringObject.GetCompositionInfos" && c?["contentsRead"]?.GetValue<bool>() == false), "composition metadata retained without invoking unverified group collection");
            screen.ScreenItems.Add(new Item { Name = "Large", Left = int.MaxValue, Width = uint.MaxValue });
            check(Rows(Pages(portal, "[\"Large\"]")).Last()["rawCoordinateEnvelope"]!["width"]!.GetValue<decimal>() == uint.MaxValue, "geometry math does not overflow Int32/UInt32");
            screen.ScreenItems.Add(new Item { Name = "Duplicate" }); screen.ScreenItems.Add(new Item { Name = "Duplicate" });
            check(Rows(Pages(portal, "[\"Duplicate\"]"))[0]["type"] == null, "ambiguous exact-name lookup is not silently first-match");
            var counted = new Counted { Name = "Counted" }; screen.ScreenItems.Add(counted);
            var first = portal.ReadUnifiedGraphicSelection("HMI", "Project_A", "/Main", "[\"Counted\"]", pageSize: 1).Meta!;
            int readCount = counted.Reads;
            var replay = portal.ReadUnifiedGraphicSelection("HMI", "Project_A", "/Main", "[\"Counted\"]", first["pageCursor"]!.ToString(), 1).Meta!;
            check(counted.Reads == readCount && JsonNode.DeepEquals(first["records"], replay["records"]), "page replay does not reread remote geometry");
            portal.ReleaseUnifiedReadCursor(first["releaseCursor"]!.ToString());
            screen.ScreenItems.Add(new Broken { Name = "Broken" });
            var failure = Pages(portal, "[\"fgxTop\",\"Broken\",\"Counted\"]", 1);
            check(!failure.Last()!["apiCallSuccess"]!.GetValue<bool>() && failure.Last()!["remoteInspectionStopped"]!.GetValue<bool>() && counted.Reads == readCount, "IPC/disposal in continuation stops before sibling and marks failure");
            int stopped = portal.FixtureResolveCalls;
            var blocked = portal.ReadUnifiedGraphicSelection("HMI", "Project_A", "/Main", NamesJson).Meta!;
            check(blocked["status"]!.ToString() == "HmiReadSessionBlocked" && portal.FixtureResolveCalls == stopped, "future selection blocked until explicit rebind");
            check(portal.CompareUnifiedGraphicSelections(original, original).Meta!["success"]!.GetValue<bool>(), "offline comparison still works with unavailable TIA");
            McpServer.Portal = new Portal { FixtureRoot = hmi };
            var bridge = McpServer.CallTool("ReadUnifiedGraphicSelection", "{\"softwarePath\":\"HMI\",\"expectedProject\":\"Project_A\",\"screenPath\":\"/Main\",\"itemNamesJson\":\"[\\\"fgxTop\\\"]\"}");
            check(bridge.Meta!["success"]!.GetValue<bool>(), "selection tool discoverable through CallTool bridge");
            var badCompare = McpServer.CallTool("CompareUnifiedGraphicSelections", "{\"beforePagesJson\":\"[]\",\"afterPagesJson\":\"[]\"}");
            check(!badCompare.Meta!["success"]!.GetValue<bool>(), "comparison validation failure propagated through CallTool bridge");
        }
    }
}
