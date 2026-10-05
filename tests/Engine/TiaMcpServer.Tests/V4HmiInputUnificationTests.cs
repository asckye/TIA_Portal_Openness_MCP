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

namespace TiaMcpServer.Tests
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
                Invalid(text.Error);
                Invalid(element.Error);
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
                Invalid(error);
                Assert.DoesNotContain("secret-canary", V4Json.Serialize(error));
            }
        }

        [Fact]
        public void ToolSchemaAndConditionalAliasesUseTheSharedEvaluator()
        {
            var outer = new InputSchema(Json(HmiSchemas.InputSchema<UnifiedScreenSpec>("design").ToJsonString()));
            Assert.Null(outer.Validate(Json("""{"design":{"items":[{"type":"Text","name":"A"}]}}"""), "design"));
            Invalid(outer.Validate(Json("""{"design":{"items":[{"type":"Lamp","name":"A"}]}}"""), "design"));
            var theme = HmiInputs.Contract<UnifiedThemeSpec>();
            Assert.True(theme.Read("""{"palette":{"Text":"0xFF000000","text":"shadowed"}}""", "design").IsValid);
            Invalid(theme.Read("""{"palette":{"text":"shadowed"}}""", "design").Error);
            var layout = HmiInputs.Contract<UnifiedLayoutSpec>();
            Invalid(layout.Read("""{"items":[{"name":"A","text":"unused"}]}""", "design").Error);
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

        private static JsonElement Json(string json) => V4Json.Deserialize<JsonElement>(json);
        private static void Invalid(Error? error)
        {
            Assert.NotNull(error);
            Assert.Equal(ErrorCode.InvalidArgument, error.Code);
            Assert.Equal("design", Assert.IsType<InvalidArgumentDetails>(error.Details).Parameter);
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
