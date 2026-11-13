using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Hmi;
using TiaMcp.Logic.V4.Inputs;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class V4HmiInputUnificationTests
    {
        [Theory, MemberData(nameof(V4HmiContractTests.Roots), MemberType = typeof(V4HmiContractTests))]
        public void SharedEntryPointsPreserveRootsAndPresence(Type type, string json) => Invoke(nameof(CheckRoot), type, json);

        private static void CheckRoot<T>(string json) where T : HmiObject
        {
            CheckDecision<T>(json, true);
            var contract = HmiInputs.Contract<T>();
            var missing = contract.Read((string?)null, "design", optional: true);
            Assert.True(missing.IsValid);
            Assert.Equal(InputPresence.Missing, missing.Presence);
            Assert.Null(missing.Value);
            Assert.Equal(InputPresence.Missing, contract.Read(default(JsonElement), "design").Presence);
            Invalid(contract.Read((string?)null, "design").Error);
            var explicitNull = contract.Read("null", "design", optional: true);
            Assert.Equal(InputPresence.Null, explicitNull.Presence);
            Invalid(explicitNull.Error);
        }

        public static IEnumerable<object[]> RejectedInputs() =>
            V4HmiContractTests.ClosedRoots().Concat(V4HmiContractTests.InvalidNested());

        [Theory, MemberData(nameof(RejectedInputs))]
        public void SharedEntryPointsRetainEveryClosedShapeRejection(Type type, string json) =>
            Invoke(nameof(CheckDecision), type, json, false);

        [Theory, MemberData(nameof(V4HmiEquivalenceTests.Samples), MemberType = typeof(V4HmiEquivalenceTests))]
        public void SharedEntryPointsRetainLegacyDecisionsAndBuilderInputs(string family, string json, bool accepted)
        {
            Type type = family switch
            {
                "theme" => typeof(UnifiedThemeSpec), "classic" => typeof(ClassicScreenSpec),
                "tags" => typeof(ClassicTagTableSpec), "package" => typeof(ClassicPackageSpec),
                "layout" => typeof(UnifiedLayoutSpec), "aml" => typeof(DeviceAmlSpec),
                _ => throw new ArgumentException(family)
            };
            Invoke(nameof(CheckDecision), type, V4HmiEquivalenceTests.CanonicalInput(family, json), accepted);
        }

        private static void CheckDecision<T>(string json, bool accepted) where T : HmiObject
        {
            var contract = HmiInputs.Contract<T>();
            var text = contract.Read(json, "design");
            using var document = JsonDocument.Parse(json);
            var element = contract.Read(document.RootElement, "design");
            Assert.Equal(accepted, text.IsValid);
            Assert.Equal(accepted, element.IsValid);
            if (!accepted)
            {
                Invalid(text.Error, ExpectedPointer<T>(json));
                Invalid(element.Error, ExpectedPointer<T>(json));
                return;
            }
            var old = V4Json.Deserialize<T>(json);
            var typed = contract.Validate(old, "design");
            Assert.True(typed.IsValid);
            foreach (var value in new[] { text.Value!, element.Value!, typed.Value! })
            {
                Assert.Equal(V4Json.SerializeUtf8(old), V4Json.SerializeUtf8(value));
                Assert.Equal(old.ToBuilderInput().ToJsonString(), value.ToBuilderInput().ToJsonString());
            }
            var schema = new InputSchema(contract.Schema);
            Assert.Null(schema.Validate(document.RootElement, "design"));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(contract.Schema.GetRawText()), HmiSchemas.For<T>()));
        }

        [Fact]
        public void MapsUseSharedScalarsWithoutChangingKeysOrderOrNulls()
        {
            const string json = """{"items":[{"type":"Text","name":"A","properties":{"z":null,"Z":"{\"nested\":1}","Visible":true}}]}""";
            var value = HmiInputs.Contract<UnifiedScreenSpec>().Read(json, "design").Value!;
            var properties = Assert.IsType<AttributeMap<Scalar>>(value.Items[0].Properties);
            Assert.Equal(new[] { "z", "Z", "Visible" }, properties.Keys);
            Assert.Equal(JsonValueKind.Null, properties["z"].Json.ValueKind);
            Assert.Equal("{\"nested\":1}", properties["Z"].Json.GetString());
            Assert.Equal(V4Json.Serialize(Json(json)), V4Json.Serialize(value));
            var source = new Dictionary<string, string> { ["zh-CN"] = "start", ["en-US"] = "Start" };
            var text = new ClassicText(source);
            source.Clear();
            Assert.IsType<AttributeMap<string>>(text.Languages);
            Assert.Equal(2, text.Languages!.Count);
            Assert.ThrowsAny<Exception>(() => new ClassicText(new Dictionary<string, string> { ["en-US"] = null! }));
            Assert.ThrowsAny<Exception>(() => new UnifiedThemeSpec(new Dictionary<string, string> { ["extension"] = null! }));
            Assert.ThrowsAny<Exception>(() => new DeviceItemSpec("I", builtIn: true,
                attributes: new Dictionary<string, string> { ["A"] = null! }));
        }

        [Theory]
        [InlineData(5000, true)]
        [InlineData(5001, false)]
        public void AggregateAmlCountUsesSharedLimitDetails(int count, bool accepted)
        {
            var first = Enumerable.Range(0, 2500).Select(i => new DeviceItemSpec("I" + i, builtIn: true)).ToArray();
            var second = Enumerable.Range(2500, count - 2500).Select(i => new DeviceItemSpec("I" + i, builtIn: true)).ToArray();
            string json = V4Json.Serialize(new { projectName = "P", devices = new[] {
                new AmlDeviceSpec("D1", "x", first), new AmlDeviceSpec("D2", "x", second) } });
            CheckAmlLimit(json, accepted, 5000, 5001);
        }

        [Theory]
        [InlineData(8, true)]
        [InlineData(9, false)]
        public void RecursiveAmlDepthUsesSharedLimitDetails(int depth, bool accepted)
        {
            var item = new DeviceItemSpec("I", builtIn: true);
            for (int i = 0; i < depth; i++) item = new DeviceItemSpec("I", builtIn: true, deviceItems: new[] { item });
            string json = V4Json.Serialize(new { projectName = "P", devices = new[] { new AmlDeviceSpec("D", "x", new[] { item }) } });
            CheckAmlLimit(json, accepted, 8, 9);
        }

        private static void CheckAmlLimit(string json, bool accepted, int limit, int actual)
        {
            var contract = HmiInputs.Contract<DeviceAmlSpec>();
            foreach (var result in new[] { contract.Read(json, "design"), contract.Read(Json(json), "design") })
            {
                Assert.Equal(accepted, result.IsValid);
                if (!accepted) Limit(result.Error, limit, actual);
                else Assert.True(contract.Validate(result.Value!, "design").IsValid);
            }
        }

        [Theory]
        [InlineData(64)]
        [InlineData(65)]
        [InlineData(66)]
        [InlineData(20000)]
        public void TextAndExternalElementDepthUseSharedBoundary(int depth)
        {
            string json = new string('[', depth) + "0" + new string(']', depth);
            var contract = HmiInputs.Contract<UnifiedScreenSpec>();
            var text = contract.Read(json, "design");
            if (depth == 64) Invalid(text.Error);
            else Limit(text.Error, 64, 65);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = depth });
            var element = contract.Read(document.RootElement, "design");
            if (depth == 64) Invalid(element.Error);
            else Limit(element.Error, 64, 65);
        }

        [Fact]
        public void FamilyHasNoInventedCharacterCapAndErrorsDoNotEchoInput()
        {
            var contract = HmiInputs.Contract<UnifiedThemeSpec>();
            string json = V4Json.Serialize(new { palette = new { extension = new string('x', 262145) } });
            Assert.True(contract.Read(json, "design").IsValid);
            Assert.Null(contract.Budget.Characters);
            Assert.Null(contract.Budget.StringLength);
            foreach (string invalid in new[] { "{\"secret-canary\":true}", "{\"palette\":{\"Text\":\"secret-canary\"}}", "{secret-canary" })
            {
                var error = contract.Read(invalid, "design").Error;
                Invalid(error, invalid.Contains("\"Text\"", StringComparison.Ordinal) ? "/design/palette/Text" : "/design");
                Assert.DoesNotContain("secret-canary", V4Json.Serialize(error));
            }
        }

        [Fact]
        public void ToolSchemaAndConditionalAliasesUseTheSharedEvaluator()
        {
            var outer = new InputSchema(Json(HmiSchemas.InputSchema<UnifiedScreenSpec>("design").ToJsonString()));
            Assert.Null(outer.Validate(Json("""{"design":{"items":[{"type":"Text","name":"A"}]}}"""), "design"));
            Invalid(outer.Validate(Json("""{"design":{"items":[{"type":"Lamp","name":"A"}]}}"""), "design"), "/design/design/items/0");
            var theme = HmiInputs.Contract<UnifiedThemeSpec>();
            Assert.True(theme.Read("""{"palette":{"Text":"0xFF000000","text":"shadowed"}}""", "design").IsValid);
            Invalid(theme.Read("""{"palette":{"text":"shadowed"}}""", "design").Error, "/design/palette/text");
            var layout = HmiInputs.Contract<UnifiedLayoutSpec>();
            Invalid(layout.Read("""{"items":[{"name":"A","text":"unused"}]}""", "design").Error, "/design/items/0/text");
            Assert.True(layout.Read("""{"items":[{"name":"A","type":"Text","text":""}]}""", "design").IsValid);
            var conditional = new InputSchema(Json("""{"if":{"const":true},"then":{"type":"boolean"},"else":{"type":"string"}}"""));
            Assert.Null(conditional.Validate(Json("true"), "design"));
            Assert.Null(conditional.Validate(Json("\"text\""), "design"));
            Invalid(conditional.Validate(Json("false"), "design"));
        }

        [Theory]
        [InlineData("""{"$ref":"https://example.invalid/schema"}""")]
        [InlineData("""{"$ref":"#/$defs/Missing","$defs":{}}""")]
        [InlineData("""{"$ref":"#/$defs/A/x","$defs":{"A":true}}""")]
        [InlineData("""{"$ref":"#/$defs/A~2","$defs":{"A~2":true}}""")]
        [InlineData("""{"$ref":"#/$defs/A%2FB","$defs":{"A%2FB":true}}""")]
        [InlineData("""{"$defs":{"A":{"$ref":"#/$defs/A"}}}""")]
        [InlineData("""{"$defs":{"A":{"allOf":[{"$ref":"#/$defs/B"}]},"B":{"not":{"$ref":"#/$defs/A"}}}}""")]
        [InlineData("""{"$defs":{"A":{"format":"unsupported"}}}""")]
        public void SharedSchemasRejectUnresolvedRemoteAndNonConsumingReferences(string json) =>
            Assert.Throws<ArgumentException>(() => new InputSchema(Json(json)));

        [Fact]
        public void LocalReferencesRetainSiblingsAndRejectExcessiveAliasChains()
        {
            var schema = new InputSchema(Json("""{"$ref":"#/$defs/A~1B~0","minimum":2,"$defs":{"A/B~":{"type":"integer","maximum":3}}}"""));
            Assert.Null(schema.Validate(Json("2"), "design"));
            Assert.NotNull(schema.Validate(Json("1"), "design"));
            Assert.NotNull(schema.Validate(Json("4"), "design"));
            var definitions = new JsonObject { ["D64"] = true };
            for (int i = 63; i >= 0; i--) definitions["D" + i] = new JsonObject { ["$ref"] = "#/$defs/D" + (i + 1) };
            Assert.Throws<ArgumentException>(() => new InputSchema(Json(new JsonObject { ["$defs"] = definitions }.ToJsonString())));
        }

        private static string ExpectedPointer<T>(string json) =>
            ExactPointers.TryGetValue((typeof(T), json), out var pointer) ? pointer : "/design";

        // DES-4R2 pins only the approved field-identity changes for the existing cases.
        private static readonly IReadOnlyDictionary<(Type Type, string Json), string> ExactPointers =
            new Dictionary<(Type Type, string Json), string> {
                [(typeof(ClassicPackageSpec),
                    "{\"name\":\"P\",\"screenDesign\":{\"items\":[]},\"tagTable\":{\"name\":\"T\",\"t"
                    + "ags\":[]}}")] = "/design/tagTable/tags",
                [(typeof(ClassicPackageSpec), "{\"name\":\"P\",\"screenDesign\":{\"items\":[]}}")] = "/design/tagTable",
                [(typeof(ClassicPackageSpec),
                    "{\"name\":\"P\",\"tagTable\":{\"name\":\"T\",\"tags\":[{\"name\":\"A\",\"dataType\""
                    + ":\"Bool\"}]}}")] = "/design/screenDesign",
                [(typeof(ClassicPackageSpec),
                    "{\"name\":null,\"screenDesign\":{\"items\":[]},\"tagTable\":{\"name\":\"T\",\""
                    + "tags\":[{\"name\":\"A\",\"dataType\":\"Bool\"}]}}")] = "/design/name",
                [(typeof(ClassicPackageSpec), "{}")] = "/design/name",
                [(typeof(ClassicScreenSpec), "{\"items\":[1]}")] = "/design/items/0",
                [(typeof(ClassicScreenSpec), "{\"items\":[],\"screen\":null}")] = "/design/screen",
                [(typeof(ClassicScreenSpec), "{\"items\":[null]}")] = "/design/items/0",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"name\":\" \",\"type\":\"Text\"}]}")] = "/design/items/0/name",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"name\":\"A\",\"type\":\"Text\",\"height\":-1}]}")] = "/design/items/0/height",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"name\":\"A\",\"type\":\"Text\",\"left\":-1}]}")] = "/design/items/0/left",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"name\":\"A\",\"type\":\"Text\",\"width\":0}]}")] = "/design/items/0/width",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"name\":\"A\"}]}")] = "/design/items/0",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"type\":\"Button\",\"name\":\"A\",\"actions\":[null]}]}")] = "/design/items/0/actions/0",
                [(typeof(ClassicScreenSpec),
                    "{\"items\":[{\"type\":\"Button\",\"name\":\"A\",\"actions\":[{\"event\":\"Press\""
                    + ",\"actionKind\":\"SetBit\",\"targetTag\":\"T\",\"other\":1}]}]}")] = "/design/items/0/actions/0",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"type\":\"Gauge\",\"name\":\"A\"}]}")] = "/design/items/0",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"type\":\"HmiText\",\"name\":\"A\"}]}")] = "/design/items/0",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"type\":\"IOField\",\"name\":\"A\",\"text\":\"unused\"}]}")] = "/design/items/0",
                [(typeof(ClassicScreenSpec),
                    "{\"items\":[{\"type\":\"Rectangle\",\"name\":\"A\",\"properties\":{\"foreColor"
                    + "\":\"0xFF000000\"}}]}")] = "/design/items/0/properties",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"type\":\"Rectangle\",\"name\":\"A\",\"text\":\"unused\"}]}")] = "/design/items/0",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"font\":{}}]}")] = "/design/items/0",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"left\":null}]}")] = "/design/items/0/left",
                [(typeof(ClassicScreenSpec),
                    "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"properties\":{\"foreColor\":nul"
                    + "l}}]}")] = "/design/items/0/properties/foreColor",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"text\":{\"en-US\":null}}]}")] = "/design/items/0/text",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"width\":1.5}]}")] = "/design/items/0/width",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"width\":2147483648}]}")] = "/design/items/0/width",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"type\":\"TextField\",\"name\":\"A\"}]}")] = "/design/items/0",
                [(typeof(ClassicScreenSpec), "{\"items\":[{\"type\":\"text\",\"name\":\"A\"}]}")] = "/design/items/0",
                [(typeof(ClassicScreenSpec), "{\"items\":null}")] = "/design/items",
                [(typeof(ClassicScreenSpec), "{\"screen\":{\"name\":\"S\",\"height\":239},\"items\":[]}")] = "/design/screen/height",
                [(typeof(ClassicScreenSpec), "{\"screen\":{\"name\":\"S\",\"width\":319},\"items\":[]}")] = "/design/screen/width",
                [(typeof(ClassicScreenSpec), "{}")] = "/design/items",
                [(typeof(ClassicTagTableSpec), "{\"name\":\"\",\"tags\":[{\"name\":\"A\",\"dataType\":\"Bool\"}]}")] = "/design/name",
                [(typeof(ClassicTagTableSpec), "{\"name\":\"T\",\"tags\":[]}")] = "/design/tags",
                [(typeof(ClassicTagTableSpec), "{\"name\":\"T\",\"tags\":[{\"name\":\"\",\"dataType\":\"Bool\"}]}")] = "/design/tags/0/name",
                [(typeof(ClassicTagTableSpec), "{\"name\":null,\"tags\":[{\"name\":\"A\",\"dataType\":\"Bool\"}]}")] = "/design/name",
                [(typeof(ClassicTagTableSpec), "{}")] = "/design/name",
                [(typeof(DeviceAmlSpec), "{\"devices\":[],\"projectName\":\"Project\"}")] = "/design/devices",
                [(typeof(DeviceAmlSpec),
                    "{\"devices\":[{\"name\":\" \",\"typeIdentifier\":\"x\",\"deviceItems\":[]}],\""
                    + "projectName\":\"Project\"}")] = "/design/devices/0/name",
                [(typeof(DeviceAmlSpec),
                    "{\"devices\":[{\"name\":\"D\",\"typeIdentifier\":\" \",\"deviceItems\":[]}],\""
                    + "projectName\":\"Project\"}")] = "/design/devices/0/typeIdentifier",
                [(typeof(DeviceAmlSpec),
                    "{\"devices\":[{\"name\":\"D\",\"typeIdentifier\":\"x\",\"deviceItems\":[]}],\""
                    + "subnets\":[{\"name\":\" \"}],\"projectName\":\"Project\"}")] = "/design/subnets/0/name",
                [(typeof(DeviceAmlSpec),
                    "{\"devices\":[{\"name\":\"D\",\"typeIdentifier\":\"x\",\"deviceItems\":[{\"nam"
                    + "e\":\"M\",\"role\":\"Module\",\"typeIdentifier\":\"y\"}]}],\"projectName\":\"Pr"
                    + "oject\"}")] = "/design/devices/0/deviceItems/0/role",
                [(typeof(DeviceAmlSpec),
                    "{\"devices\":[{\"name\":\"D\",\"typeIdentifier\":\"x\",\"deviceItems\":[{\"nam"
                    + "e\":\"M\"}]}],\"projectName\":\"Project\"}")] = "/design/devices/0/deviceItems/0",
                [(typeof(DeviceAmlSpec),
                    "{\"projectName\":\"P\",\"devices\":[{\"name\":\"D\",\"typeIdentifier\":\"x\",\"d"
                    + "eviceItems\":[{\"name\":\"I\",\"builtIn\":true,\"attributes\":{\"A\":null}}]"
                    + "}]}")] = "/design/devices/0/deviceItems/0/attributes",
                [(typeof(DeviceAmlSpec),
                    "{\"projectName\":\"P\",\"devices\":[{\"name\":\"D\",\"typeIdentifier\":\"x\",\"d"
                    + "eviceItems\":[{\"name\":\"I\",\"builtIn\":true,\"nodes\":null}]}]}")] = "/design/devices/0/deviceItems/0/nodes",
                [(typeof(DeviceAmlSpec),
                    "{\"projectName\":null,\"devices\":[{\"name\":\"D\",\"typeIdentifier\":\"x\",\""
                    + "deviceItems\":[]}]}")] = "/design/projectName",
                [(typeof(DeviceAmlSpec), "{}")] = "/design/projectName",
                [(typeof(UnifiedLayoutSpec), "{\"items\":[1]}")] = "/design/items/0",
                [(typeof(UnifiedLayoutSpec), "{\"items\":[{\"name\":\" \"}]}")] = "/design/items/0/name",
                [(typeof(UnifiedLayoutSpec), "{\"items\":[{\"name\":\"A\",\"type\":\"Lamp\"}]}")] = "/design/items/0/type",
                [(typeof(UnifiedLayoutSpec), "{\"items\":null}")] = "/design/items",
                [(typeof(UnifiedLayoutSpec), "{}")] = "/design/items",
                [(typeof(UnifiedScreenSpec),
                    "{\"items\":[{\"type\":\"IOField\",\"name\":\"A\",\"properties\":{\"ProcessValue\":1}}]}")] = "/design/items/0/properties",
                [(typeof(UnifiedScreenSpec),
                    "{\"items\":[{\"type\":\"IOField\",\"name\":\"A\",\"properties\":{\"backColor\":\"0xFF000000\"}}]}")] = "/design/items/0/properties",
                [(typeof(UnifiedScreenSpec),
                    "{\"items\":[{\"type\":\"Rectangle\",\"name\":\"A\",\"properties\":{\"ForeColor\":\"0xFF000000\"}}]}")] = "/design/items/0/properties",
                [(typeof(UnifiedScreenSpec), "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"properties\":{\" \":1}}]}")] = "/design/items/0/properties",
                [(typeof(UnifiedScreenSpec), "{\"items\":[{\"type\":\"Lamp\",\"name\":\"A\"}]}")] = "/design/items/0",
                [(typeof(UnifiedScreenSpec), "{\"items\":[{\"type\":\"Rectangle\",\"name\":\"A\",\"text\":\"A\"}]}")] = "/design/items/0",
                [(typeof(UnifiedScreenSpec), "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"Properties\":{}}]}")] = "/design/items/0",
                [(typeof(UnifiedScreenSpec), "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"actions\":[]}]}")] = "/design/items/0",
                [(typeof(UnifiedScreenSpec), "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"processValueTag\":\"T\"}]}")] = "/design/items/0",
                [(typeof(UnifiedScreenSpec), "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"properties\":{\"Name\":{}}}]}")] = "/design/items/0/properties",
                [(typeof(UnifiedScreenSpec), "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"text\":null}]}")] = "/design/items/0/text",
                [(typeof(UnifiedScreenSpec), "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"text\":{\"en-US\":\"A\"}}]}")] = "/design/items/0/text",
                [(typeof(UnifiedScreenSpec), "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"textProperty\":\"text\"}]}")] = "/design/items/0",
                [(typeof(UnifiedScreenSpec), "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"width\":\"120\"}]}")] = "/design/items/0/width",
                [(typeof(UnifiedScreenSpec), "{\"items\":null}")] = "/design/items",
                [(typeof(UnifiedScreenSpec), "{}")] = "/design/items",
                [(typeof(UnifiedThemeSpec), "{\"palette\":null}")] = "/design/palette",
                [(typeof(UnifiedThemeSpec), "{\"palette\":{\"Background\":\"#FFAabb00\"}}")] = "/design/palette/Background",
                [(typeof(UnifiedThemeSpec), "{\"palette\":{\"Border\":\"#FFAabb00\"}}")] = "/design/palette/Border",
                [(typeof(UnifiedThemeSpec), "{\"palette\":{\"Page\":\"#FFAabb00\"}}")] = "/design/palette/Page",
                [(typeof(UnifiedThemeSpec), "{\"palette\":{\"Surface\":\"#FFAabb00\"}}")] = "/design/palette/Surface",
                [(typeof(UnifiedThemeSpec), "{\"palette\":{\"Text\":\"#FFAabb00\"}}")] = "/design/palette/Text",
                [(typeof(UnifiedThemeSpec), "{\"palette\":{\"Text\":\"0xFFFFFFFFF\"}}")] = "/design/palette/Text",
                [(typeof(UnifiedThemeSpec), "{\"palette\":{\"Text\":null}}")] = "/design/palette/Text",
                [(typeof(UnifiedThemeSpec), "{}")] = "/design/palette",
            };

        private static JsonElement Json(string json) => V4Json.Deserialize<JsonElement>(json);
        private static void Invalid(Error? error)
        {
            Assert.NotNull(error);
            Assert.Equal(ErrorCode.InvalidArgument, error.Code);
            Assert.Equal("/design", Assert.IsType<InvalidArgumentDetails>(error.Details).Parameter);
        }
        private static void Invalid(Error? error, string pointer)
        {
            Assert.NotNull(error);
            Assert.Equal(ErrorCode.InvalidArgument, error.Code);
            Assert.Equal(pointer, Assert.IsType<InvalidArgumentDetails>(error.Details).Parameter);
        }
        private static void Limit(Error? error, int limit, int actual)
        {
            Assert.NotNull(error);
            Assert.Equal(ErrorCode.LimitExceeded, error.Code);
            var details = Assert.IsType<LimitExceededDetails>(error.Details);
            Assert.Equal("design", details.Parameter);
            Assert.Equal(limit, details.Limit);
            Assert.Equal(actual, details.Actual);
        }
        private static void Invoke(string method, Type type, params object[] arguments)
        {
            try { typeof(V4HmiInputUnificationTests).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(type).Invoke(null, arguments); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }
    }
}
