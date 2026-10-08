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
        // src/Engine/Siemens/StartdriveLogic.cs:83-105. The selector is shared by
        // the parameter request parsers; omitted index is the legacy -1 sentinel.
        [Theory]
        [InlineData("[]")]
        [InlineData("[{\"number\":0}]")]
        [InlineData("[{\"number\":65535,\"arrayIndex\":32767}]")]
        [InlineData("[{\"number\":1000,\"arrayIndex\":0}]")]
        [InlineData("[{\"number\":1000,\"arrayIndex\":7},{\"number\":1000,\"arrayIndex\":7}]")]
        [InlineData("[{\"number\":1000,\"arrayIndex\":-1}]")]
        [InlineData("[{\"number\":-1}]")]
        [InlineData("[{\"number\":65536}]")]
        [InlineData("[{\"number\":2147483648}]")]
        [InlineData("[{\"number\":1,\"arrayIndex\":-2}]")]
        [InlineData("[{\"number\":1,\"arrayIndex\":32768}]")]
        [InlineData("[{\"number\":1,\"arrayIndex\":2147483648}]")]
        [InlineData("[{\"number\":1,\"arrayIndex\":1.0}]")]
        [InlineData("[{\"number\":1,\"arrayIndex\":1e1}]")]
        [InlineData("[{\"number\":1,\"arrayIndex\":null}]")]
        [InlineData("[{\"number\":1,\"arrayIndex\":\"1\"}]")]
        [InlineData("[{\"number\":1,\"ArrayIndex\":1}]")]
        [InlineData("[{\"Number\":1}]")]
        [InlineData("[{\"arrayIndex\":1}]")]
        [InlineData("[{\"number\":1,\"extra\":1}]")]
        [InlineData("[1]")]
        [InlineData("[null]")]
        public void IndexedSelectorsMatchLegacy(string json)
        {
            (int Number, int ArrayIndex)[]? legacy = null;
            var error = Record.Exception(() => legacy = StartdriveLogic.ParseParameterSelector("[]", json).Numbers);
            var result = NumberListValidator.ParameterReferences().Read(json, "numbers");
            Assert.Equal(error == null, result.IsValid);
            if (error == null) Assert.Equal(legacy, result.Value!.Select(p => (p.Number, p.ArrayIndex ?? -1)).ToArray());
        }

        [Theory]
        [InlineData(0, 200)]
        [InlineData(0, 201)]
        [InlineData(199, 1)]
        [InlineData(199, 2)]
        [InlineData(200, 0)]
        [InlineData(200, 1)]
        public void SelectorsShareTheCombinedNameAndNumberBudget(int names, int numbers)
        {
            string namesJson = V4Json.Serialize(Enumerable.Range(0, names).Select(i => "p" + i).ToArray());
            var values = Enumerable.Repeat(new ParameterRef(1000, 3), numbers).ToArray();
            string json = V4Json.Serialize(values);
            var error = Record.Exception(() => StartdriveLogic.ParseParameterSelector(namesJson, json));
            var contract = NumberListValidator.ParameterReferences(names);
            Assert.Equal(error == null, contract.Read(json, "numbers").IsValid);
            Assert.Equal(error == null, contract.Validate(values, "numbers").IsValid);
        }

        [Theory]
        [InlineData("{\"number\":1,\"number\":2}")]
        [InlineData("{\"number\":1,\"arrayIndex\":0,\"arrayIndex\":1}")]
        [InlineData("{\"number\":1,\"arrayIndex\":null}")]
        [InlineData("{\"number\":1,\"ArrayIndex\":0}")]
        [InlineData("{\"number\":1,\"other\":0}")]
        public void ParameterRefIsClosedWithoutTheAdapter(string json) =>
            Assert.ThrowsAny<Exception>(() => V4Json.Deserialize<ParameterRef>(json));

        private static InputContract<CompositeAttributeMap> ScreenItem() => CompositeAttributeMapValidator.ScreenItem(
            new Dictionary<string, AttributeRule> { ["Width"] = new AttributeRule(InputSchema.Integer(0, uint.MaxValue)) },
            new Dictionary<string, IReadOnlyDictionary<string, AttributeRule>> {
                ["Font"] = new Dictionary<string, AttributeRule> {
                    ["Size"] = new AttributeRule(InputSchema.Integer(0, uint.MaxValue)),
                    ["Bold"] = new AttributeRule(InputSchema.Boolean()),
                    ["ReadOnly"] = new AttributeRule(InputSchema.String(), false) } });

        // src/Engine/Siemens/Hmi/UnifiedScreenItemLogic.cs:36-40,152-157 and
        // src/Engine/Siemens/Hmi/UnifiedUiModelLogic.cs:117-138. Reuse the existing
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

        [Fact]
        public void NestedRulesAreClosedAndScalarMapsStayScalarOnly()
        {
            var scalar = AttributeMapValidator.Create(new Dictionary<string, AttributeRule> {
                ["Font"] = new AttributeRule(new InputSchema(V4Json.Deserialize<JsonElement>("true"))) }, new InputBudget());
            Assert.False(scalar.Read("{\"Font\":{\"Size\":14}}", "properties").IsValid);
            foreach (string json in new[] { "{\"Font\":{\"Size\":1,\"Size\":2}}", "{\"Font\":{\"Bold\":1}}",
                "{\"Font\":{\"Size\":{\"Value\":14}}}" })
                Assert.False(ScreenItem().Read(json, "properties").IsValid);
            Assert.ThrowsAny<Exception>(() => V4Json.Deserialize<CompositeAttributeMap>("{\"Font\":{\"Size\":{\"Value\":14}}}"));
            var schema = new InputSchema(ScreenItem().Schema);
            Assert.NotNull(schema.Validate(V4Json.Deserialize<JsonElement>("{\"Font\":{\"ReadOnly\":\"x\"}}"), "properties"));
            Assert.NotNull(schema.Validate(V4Json.Deserialize<JsonElement>("{\"Font\":{\"Size\":-1}}"), "properties"));
            Assert.True(ScreenItem().Validate(V4Json.Deserialize<CompositeAttributeMap>("{\"Font\":{\"Size\":14}}"), "properties").IsValid);
        }

        [Fact]
        public void CompositeWritesKeepLeafAndCharacterBudgets()
        {
            var rules = Enumerable.Range(0, 26).ToDictionary(i => "Value" + i, _ => new AttributeRule(InputSchema.String()));
            var contract = CompositeAttributeMapValidator.ScreenItem(new Dictionary<string, AttributeRule>(),
                new Dictionary<string, IReadOnlyDictionary<string, AttributeRule>> { ["First"] = rules, ["Second"] = rules });
            var leaves = Enumerable.Range(0, 25).ToDictionary(i => "Value" + i, _ => "x");
            string json = V4Json.Serialize(new Dictionary<string, object> { ["First"] = leaves, ["Second"] = leaves });
            Assert.True(contract.Read(json, "properties").IsValid);
            var more = new Dictionary<string, string>(leaves) { ["Value25"] = "x" };
            json = V4Json.Serialize(new Dictionary<string, object> { ["First"] = more, ["Second"] = leaves });
            Assert.Equal(ErrorCode.LimitExceeded, contract.Read(json, "properties").Error!.Code);
            string large = "{\"First\":{\"Value0\":\"" + new string('x', 65536) + "\"}}";
            Assert.Equal(ErrorCode.LimitExceeded, contract.Read(large, "properties").Error!.Code);
        }

        [Fact]
        public void AddedShapesHaveStableGoldenBytes()
        {
            const string numbers = "[{\"number\":1},{\"number\":2,\"arrayIndex\":-1},{\"number\":1000,\"arrayIndex\":7}]";
            Assert.Equal(Encoding.UTF8.GetBytes(numbers), V4Json.SerializeUtf8(NumberListValidator.ParameterReferences().Read(numbers, "numbers").Value));
            const string properties = "{\"Width\":20,\"Font\":{\"Size\":14}}";
            Assert.Equal(Encoding.UTF8.GetBytes(properties), V4Json.SerializeUtf8(ScreenItem().Read(properties, "properties").Value));
            Assert.True(NumberListValidator.Drive().Read("[1,2]", "numbers").IsValid);
            Assert.False(NumberListValidator.Drive().Read(numbers, "numbers").IsValid);
        }

        [Theory]
        [InlineData(64, true)]
        [InlineData(65, false)]
        [InlineData(66, false)]
        [InlineData(20000, false)]
        public void TextDepthIsBoundedAndReportedAsALimit(int depth, bool accepted)
        {
            var schema = new InputSchema(V4Json.Deserialize<JsonElement>("true"));
            var contract = NativeValueValidator.Create(new NativeValuePolicy(schema, new InputBudget()));
            var result = contract.Read(new string('[', depth) + "0" + new string(']', depth), "value");
            Assert.Equal(accepted, result.IsValid);
            if (!accepted)
            {
                Assert.Equal(ErrorCode.LimitExceeded, result.Error!.Code);
                var details = Assert.IsType<LimitExceededDetails>(result.Error.Details);
                Assert.Equal(64, details.Limit); Assert.Equal(65, details.Actual);
            }
        }

        [Fact]
        public void RawBudgetPrecedesParsingAndCompactBudgetStillApplies()
        {
            var contract = new InputContract<Scalar>(InputSchema.Scalar(), new InputBudget(characters: 5));
            Assert.Equal(ErrorCode.LimitExceeded, contract.Read(new string('[', 20000), "secret").Error!.Code);
            Assert.Equal(ErrorCode.InvalidArgument, contract.Read("[", "secret").Error!.Code);
            Assert.Equal(ErrorCode.LimitExceeded, contract.Read("\"é\"", "secret").Error!.Code);
            Assert.Equal(ErrorCode.LimitExceeded, contract.Read(new string(' ', 6), "secret").Error!.Code);
        }

        [Fact]
        public void ExternalElementsAreBoundedBeforeRecursiveValidation()
        {
            const int depth = 20000;
            using var document = JsonDocument.Parse(string.Concat(Enumerable.Repeat("{\"not\":", depth)) + "true" + new string('}', depth),
                new JsonDocumentOptions { MaxDepth = depth });
            var value = document.RootElement;
            var schema = new InputSchema(V4Json.Deserialize<JsonElement>("true"));
            Assert.Equal(ErrorCode.LimitExceeded, schema.Validate(value, "value")!.Code);
            var contract = NativeValueValidator.Create(new NativeValuePolicy(schema, new InputBudget(depth: 16)));
            Assert.Equal(ErrorCode.LimitExceeded, contract.Read(value, "value").Error!.Code);
            Assert.ThrowsAny<Exception>(() => new InputSchema(value));
            Assert.ThrowsAny<Exception>(() => new NativeValue(value));
            Assert.ThrowsAny<Exception>(() => new ToolArguments(value));
            Assert.ThrowsAny<Exception>(() => new Scalar(value));
            Assert.ThrowsAny<Exception>(() => new CompositeAttributeValue(value));
        }
    }
}
