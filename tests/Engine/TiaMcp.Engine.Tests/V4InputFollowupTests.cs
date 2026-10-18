using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class V4InputFollowupTests
    {




        private static InputContract<CompositeAttributeMap> ScreenItem() => CompositeAttributeMapValidator.ScreenItem(
            new Dictionary<string, AttributeRule> { ["Width"] = new AttributeRule(InputSchema.Integer(0, uint.MaxValue)) },
            new Dictionary<string, IReadOnlyDictionary<string, AttributeRule>> {
                ["Font"] = new Dictionary<string, AttributeRule> {
                    ["Size"] = new AttributeRule(InputSchema.Integer(0, uint.MaxValue)),
                    ["Bold"] = new AttributeRule(InputSchema.Boolean()),
                    ["ReadOnly"] = new AttributeRule(InputSchema.String(), false) } });

        // src/Adapters/Native/Hmi/UnifiedScreenItemLogic.cs:36-40,152-157 and
        // src/Adapters/Native/Hmi/UnifiedUiModelLogic.cs:117-138. Reuse the existing
        // screen-item fakes; compare flattened prepared edits, without invoking SDKs.
        [Theory]
        [InlineData("{}")]
        [InlineData("{\"Width\":20,\"Font\":{\"Size\":14}}")]
        [InlineData("{\"Font\":{}}")]
        [InlineData("{\"Font\":{\"Size\":0,\"Bold\":true}}")]
        [InlineData("{\"Font\":{\"Size\":4294967295}}")]
        [InlineData("{\"Font\":{\"Size\":-1}}")]
        [InlineData("{\"Font\":{\"Size\":4294967296}}")]
        [InlineData("{\"Font\":{\"Size\":14.5}}")]
        [InlineData("{\"Font\":{\"Size\":14.0}}")]
        [InlineData("{\"Font\":{\"Size\":1e1}}")]
        [InlineData("{\"Font\":{\"Size\":\"14\"}}")]
        [InlineData("{\"Font\":{\"Size\":null}}")]
        [InlineData("{\"Font\":{\"Size\":{\"Value\":14}}}")]
        [InlineData("{\"Font\":{\"Size\":[14]}}")]
        [InlineData("{\"Font\":{\"size\":14}}")]
        [InlineData("{\"font\":{\"Size\":14}}")]
        [InlineData("{\"Font\":{\"ReadOnly\":\"x\"}}")]
        [InlineData("{\"Font\":{\"Unknown\":14}}")]
        [InlineData("{\"Font\":{\"Name\":\"x\"}}")]
        [InlineData("{\"Font\":null}")]
        [InlineData("{\"Font\":[]}")]
        [InlineData("{\"Font\":\"{\\\"Size\\\":14}\"}")]
        public void CompositeScreenItemPropertiesMatchLegacy(string json)
        {
            Dictionary<string, JsonElement>? legacy = null;
            var error = Record.Exception(() => {
                var type = typeof(global::Siemens.Engineering.HmiUnified.UI.Shapes.HmiCircle);
                var split = UnifiedScreenItemLogic.SplitProperties(type, UnifiedScreenItemLogic.ParseProperties(json));
                Assert.Empty(split.Texts);
                legacy = UnifiedUiModelLogic.PrepareNested(type, split.Plain).ToDictionary(e => e.Name,
                    e => V4Json.Deserialize<JsonElement>(EngineeringScalarProperties.Json(e.Value)!.ToJsonString()));
            });
            var result = ScreenItem().Read(json, "properties");
            Assert.Equal(error == null, result.IsValid);
            if (error != null) return;
            var current = new Dictionary<string, JsonElement>();
            foreach (var entry in result.Value!)
                if (entry.Value.Json.ValueKind == JsonValueKind.Object)
                    foreach (var field in entry.Value.Json.EnumerateObject()) current.Add(entry.Key + "." + field.Name, field.Value);
                else current.Add(entry.Key, entry.Value.Json);
            Assert.Equal(V4Json.Serialize(legacy), V4Json.Serialize(current));
        }






    }
}
