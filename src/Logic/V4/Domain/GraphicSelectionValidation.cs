using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Domain
{
    internal static partial class DomainValidation
    {
        public static void GraphicSelections(GraphicSelectionPage[] before, GraphicSelectionPage[] after)
        {
            var a = ValidatePages(before); var b = ValidatePages(after);
            Require(Equal(before[0].Scope, after[0].Scope));
            foreach (var item in a)
            {
                var next = b[item.Key];
                Require(item.Value.Type != null && item.Value.Type == next.Type && item.Value.Path == next.Path);
                foreach (var field in Geometry(item.Value).Zip(Geometry(next), (x, y) => new[] { x, y }))
                    _ = checked(Coordinate(field[1]) - Coordinate(field[0]));
            }
        }
        private static Dictionary<string, GraphicObjectRecord> ValidatePages(GraphicSelectionPage[] pages)
        {
            Require(pages != null && pages.Length >= 1);
            InputGuard.Limit(pages!.Length, 1024);
            Budget(typeof(GraphicSelectionPage[])).Check(V4Json.ParseInput(V4Json.Serialize(pages)));
            var scope = pages![0].Scope; string id = pages[0].CollectionId;
            new InputBudget(65536).Check(V4Json.ParseInput(V4Json.Serialize(scope.ItemNames)));
            foreach (var name in scope.ItemNames) Text(name);
            var records = new List<GraphicSelectionRecord>();
            for (int i = 0; i < pages.Length; i++)
            {
                var page = pages[i];
                Require(page.PageIndex == i && page.CollectionId == id && page.ApiCallSuccess && Equal(page.Scope, scope));
                bool final = i == pages.Length - 1;
                Require(final ? page.TraversalComplete && page.NextCursor == null && !page.Truncated
                    : page.NextCursor != null && !page.TraversalComplete);
                records.AddRange(page.Records);
            }
            var items = records.OfType<GraphicObjectRecord>().ToArray();
            Require(records.Count == items.Length + 1 && records.Last() is GraphicSummaryRecord
                && items.Length == scope.ItemNames.Count);
            var result = new Dictionary<string, GraphicObjectRecord>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                Require(item.GeometryComplete && item.Fields != null);
                var geometry = Geometry(item).Select(Coordinate).ToArray();
                Require(geometry[2] >= 0 && geometry[3] >= 0);
                Require(!result.ContainsKey(item.Name)); result.Add(item.Name, item);
            }
            Require(scope.ItemNames.All(result.ContainsKey));
            return result;
        }
        private static IEnumerable<GraphicEvidence> Geometry(GraphicObjectRecord item)
        { yield return item.Fields!.Left; yield return item.Fields.Top; yield return item.Fields.Width; yield return item.Fields.Height; }
        private static decimal Coordinate(GraphicEvidence field)
        {
            Require(field.Status == "ok");
            Require(decimal.TryParse(field.Value.Json.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number));
            return number;
        }
        private static bool Equal(DomainDto a, DomainDto b) => JsonNode.DeepEquals(JsonNode.Parse(a.Json.GetRawText()), JsonNode.Parse(b.Json.GetRawText()));
    }
}
