using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcpServer.Siemens;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class V4DomainSnapshotTests
    {
        private static JsonArray Pages()
        {
            const string scope = """{"tool":"ReadUnifiedGraphicSelection","softwarePath":"HMI_1","expectedProject":"P","project":"P","screenPath":"/Main","itemNames":["A"]}""";
            var fields = new JsonObject();
            foreach (string name in new[] { "Left", "Top", "Width", "Height" })
            {
                fields[name] = MigrationRead.Scalar("/Screens/Main/ScreenItems/A/" + name, name == "Left" ? -5 : 10);
                fields[name]!["access"] = "public property";
            }
            var records = new[]
            {
                new JsonObject { ["kind"] = "graphicObject", ["name"] = "A", ["path"] = "/Screens/Main/ScreenItems/A", ["type"] = "HmiButton",
                    ["status"] = "ok", ["sampleStartedUtc"] = "2026-10-03T00:00:00Z", ["sampleFinishedUtc"] = "2026-10-03T00:00:01Z",
                    ["geometryComplete"] = true, ["fields"] = fields,
                    ["owner"] = new JsonObject { ["status"] = "ok", ["evidence"] = "/Parent", ["type"] = "HmiScreen", ["meaning"] = "owner only",
                        ["name"] = MigrationRead.Scalar("/Parent/Name", "Main") },
                    ["relationEvidence"] = new JsonArray(MigrationRead.Scalar("/ParentId", "1")),
                    ["relationCapabilities"] = new JsonArray(new JsonObject { ["name"] = "Group", ["source"] = "public CLR property metadata", ["type"] = "string", ["readable"] = true }),
                    ["gaps"] = new JsonArray(), ["nativeGroupVerified"] = false, ["coordinateFrame"] = "unverified raw Openness geometry",
                    ["attributeMetadataApiAvailable"] = true, ["compositionMetadataApiAvailable"] = true },
                new JsonObject { ["kind"] = "graphicSelectionSummary", ["path"] = "/Screens/Main", ["status"] = "unsupported",
                    ["code"] = "NativeGraphicGroupUnverified", ["reason"] = "No native group evidence.", ["expectedObjectCount"] = 1,
                    ["actualObjectCount"] = 1, ["geometryComplete"] = true, ["nativeGroupVerified"] = false, ["nativeGroup"] = null,
                    ["nativeGroupBounds"] = null, ["rawCoordinateEnvelope"] = new JsonObject { ["left"] = -5, ["top"] = 10, ["width"] = 10, ["height"] = 10, ["nativeGroupBounds"] = false },
                    ["coordinateCaveat"] = "Raw geometry only." }
            };
            using var cache = new MigrationPages(); var project = new object(); string cursor = "";
            var pages = new JsonArray();
            do
            {
                var page = cache.Read(project, scope, cursor, 1, 1000, () => records, 2);
                page["success"] = true; page["operationSuccess"] = true; page["softwarePath"] = "HMI_1"; page["screenPath"] = "/Main";
                page["expectedObjectCount"] = 1; page["selectionKind"] = "caller-defined exact-name selection"; page["nativeGroupVerified"] = false;
                pages.Add(page); cursor = page["nextCursor"]?.ToString() ?? "";
            } while (cursor != "");
            return pages;
        }
        [Theory]
        [InlineData("same", true)]
        [InlineData("moved", true)]
        [InlineData("missingFirst", false)]
        [InlineData("missingLast", false)]
        [InlineData("collection", false)]
        [InlineData("pageIndex", false)]
        [InlineData("scope", false)]
        [InlineData("unfinished", false)]
        [InlineData("apiFailure", false)]
        [InlineData("missingCoordinate", false)]
        [InlineData("negativeWidth", false)]
        [InlineData("type", false)]
        [InlineData("path", false)]
        [InlineData("summaryOnly", false)]
        [InlineData("wrongObject", false)]
        public void Both_appendix_B_page_rows_preserve_identity_completeness_and_values(string mutation, bool accepted)
        {
            // UnifiedGraphicSelection.cs:199-241; actual paging metadata from MigrationPages.cs:93-112.
            var before = Pages(); var after = (JsonArray)before.DeepClone();
            var row = after[0]!["records"]![0]!;
            switch (mutation)
            {
                case "moved": row["fields"]!["Left"]!["value"] = 15; break;
                case "missingFirst": after.RemoveAt(0); break;
                case "missingLast": after.RemoveAt(after.Count - 1); break;
                case "collection": after[1]!["collectionId"] = "other"; break;
                case "pageIndex": after[1]!["pageIndex"] = 7; break;
                case "scope": foreach (var page in after) page!["scope"]!["softwarePath"] = "Other"; break;
                case "unfinished": after.Last()!["truncated"] = true; break;
                case "apiFailure": after[0]!["apiCallSuccess"] = false; break;
                case "missingCoordinate": row["fields"]!["Left"]!["value"] = null; break;
                case "negativeWidth": row["fields"]!["Width"]!["value"] = -1; break;
                case "type": row["type"] = "Other"; break;
                case "path": row["path"] = "Other"; break;
                case "summaryOnly": after[0]!["records"]!.AsArray().Clear(); break;
                case "wrongObject": row["name"] = "Other"; break;
            }
            var oldError = Record.Exception(() => UnifiedGraphicSelection.Compare(before.ToJsonString(), after.ToJsonString()));
            var error = Record.Exception(() => DomainValidation.GraphicSelections(DomainValidation.Read<GraphicSelectionPage[]>(before.ToJsonString()), DomainValidation.Read<GraphicSelectionPage[]>(after.ToJsonString())));
            Assert.Equal(accepted, oldError == null); Assert.True(accepted == (error == null), error?.ToString());
            if (accepted)
            {
                var typed = DomainValidation.Read<GraphicSelectionPage[]>(after.ToJsonString());
                Assert.True(JsonNode.DeepEquals(after, JsonNode.Parse(V4Json.Serialize(typed))));
                var item = Assert.IsType<GraphicObjectRecord>(typed[0].Records[0]);
                Assert.Equal(mutation == "moved" ? "15" : "-5", item.Fields!.Left.Value.ToString());
                Assert.False(typed.Last().DataComplete); // The unsupported native-group summary remains incomplete evidence.
                foreach (var page in typed) foreach (var property in page.GetType().GetProperties()) _ = property.GetValue(page);
            }
        }
    }
}
