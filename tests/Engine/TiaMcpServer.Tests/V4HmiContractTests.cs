using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Hmi;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class V4HmiContractTests
    {
        internal static HmiObject Read(Type type, string json)
        {
            try { return (HmiObject)typeof(V4Json).GetMethod(nameof(V4Json.Deserialize))!.MakeGenericMethod(type).Invoke(null, new object[] { json })!; }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        public static IEnumerable<object[]> Roots()
        {
            yield return new object[] { typeof(UnifiedThemeSpec), """{"palette":{}}""" };
            yield return new object[] { typeof(ClassicScreenSpec), """{"items":[]}""" };
            yield return new object[] { typeof(UnifiedScreenSpec), """{"items":[]}""" };
            yield return new object[] { typeof(ClassicTagTableSpec), """{"name":"T","tags":[{"name":"A","dataType":"Bool"}]}""" };
            yield return new object[] { typeof(ClassicPackageSpec), """{"name":"P","screenDesign":{"items":[]},"tagTable":{"name":"T","tags":[{"name":"A","dataType":"Bool"}]}}""" };
            yield return new object[] { typeof(UnifiedLayoutSpec), """{"items":[]}""" };
            yield return new object[] { typeof(DeviceAmlSpec), """{"projectName":"P","devices":[{"name":"D","typeIdentifier":"x","deviceItems":[]}]}""" };
        }

        public static IEnumerable<object[]> ClosedRoots()
        {
            foreach (var row in Roots())
            {
                var root = JsonNode.Parse((string)row[1])!.AsObject();
                yield return new object[] { row[0], "{}" };
                yield return new object[] { row[0], "null" };
                yield return new object[] { row[0], "[]" };
                yield return new object[] { row[0], JsonSerializer.Serialize((string)row[1]) };
                var first = root.First();
                var unknown = (JsonObject)root.DeepClone(); unknown["unknown"] = true;
                yield return new object[] { row[0], unknown.ToJsonString() };
                var wrongCase = (JsonObject)root.DeepClone(); wrongCase.Remove(first.Key); wrongCase[char.ToUpperInvariant(first.Key[0]) + first.Key.Substring(1)] = first.Value!.DeepClone();
                yield return new object[] { row[0], wrongCase.ToJsonString() };
                var nullField = (JsonObject)root.DeepClone(); nullField[first.Key] = null;
                yield return new object[] { row[0], nullField.ToJsonString() };
                yield return new object[] { row[0], ((string)row[1]).Insert(1, "\"" + first.Key + "\":" + first.Value!.ToJsonString() + ",") };
            }
        }

        [Theory, MemberData(nameof(ClosedRoots))]
        public void ClosedRootRejects(Type type, string json) => Assert.ThrowsAny<Exception>(() => Read(type, json));

        [Theory, MemberData(nameof(Roots))]
        public void MissingFieldsStayMissingAndSchemasResolve(Type type, string json)
        {
            var first = Read(type, json);
            string serialized = V4Json.Serialize(first);
            Assert.Equal(serialized, V4Json.Serialize(Read(type, serialized)));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(serialized)));
            Assert.True(HmiSchemaAccepts(first.GetSchema(), JsonNode.Parse(serialized)));
        }

        public static IEnumerable<object[]> InvalidNested()
        {
            foreach (string field in new[] { "screen", "items" })
                yield return new object[] { typeof(ClassicScreenSpec), "{\"items\":[],\"" + field + "\":null}" };
            foreach (string body in new[]
            {
                """{"type":"Text","name":"A","left":null}""",
                """{"type":"Text","name":"A","width":1.5}""",
                """{"type":"Text","name":"A","width":2147483648}""",
                """{"type":"Text","name":"A","left":2147483647}""",
                """{"type":"Text","name":"A","properties":{"foreColor":null}}""",
                """{"type":"Rectangle","name":"A","properties":{"foreColor":"0xFF000000"}}""",
                """{"type":"IOField","name":"A","text":"unused"}""",
                """{"type":"Rectangle","name":"A","text":"unused"}""",
                """{"type":"Text","name":"A","text":{"en-US":null}}""",
                """{"type":"Text","name":"A","text":{"en-US":"a","en-US":"b"}}""",
                """{"type":"Button","name":"A","actions":[{"event":"Press","actionKind":"SetBit","targetTag":"T","other":1}]}""",
                """{"type":"Button","name":"A","actions":[null]}""",
                """{"type":"text","name":"A"}""",
                """{"type":"HmiText","name":"A"}""",
                """{"type":"TextField","name":"A"}""",
                """{"type":"Gauge","name":"A"}""",
                """{"type":"Text","name":"A","font":{}}""",
                """{"type":"Text","type":"Text","name":"A"}""",
                """{"name":"A"}""", "null", "1"
            }) yield return new object[] { typeof(ClassicScreenSpec), "{\"items\":[" + body + "]}" };
            foreach (string body in new[]
            {
                """{"type":"Lamp","name":"A"}""",
                """{"type":"Text","name":"A","processValueTag":"T"}""",
                """{"type":"Text","name":"A","text":null}""",
                """{"type":"Text","name":"A","text":{"en-US":"A"}}""",
                """{"type":"Text","name":"A","actions":[]}""",
                """{"type":"IOField","name":"A","properties":{"ProcessValue":1}}""",
                """{"type":"IOField","name":"A","properties":{"backColor":"0xFF000000"}}""",
                """{"type":"Rectangle","name":"A","text":"A"}""",
                """{"type":"Rectangle","name":"A","properties":{"ForeColor":"0xFF000000"}}""",
                """{"type":"Text","name":"A","properties":{"Name":{}}}""",
                """{"type":"Text","name":"A","font":{"Size":12,"Size":14}}""",
                """{"type":"Text","name":"A","properties":{" ":1}}""",
                """{"type":"Text","name":"A","textProperty":"text"}""",
                """{"type":"Text","name":"A","width":1e999}""",
                """{"type":"Text","name":"A","width":"120"}""",
                """{"type":"Text","name":"A","Properties":{}}"""
            }) yield return new object[] { typeof(UnifiedScreenSpec), "{\"items\":[" + body + "]}" };
            yield return new object[] { typeof(UnifiedThemeSpec), """{"palette":{"Text":"0xFF000000","Text":"0xFFFFFFFF"}}""" };
            yield return new object[] { typeof(UnifiedThemeSpec), """{"palette":{"Text":null}}""" };
            yield return new object[] { typeof(DeviceAmlSpec), """{"projectName":"P","devices":[{"name":"D","typeIdentifier":"x","deviceItems":[{"name":"I","builtIn":true,"nodes":null}]}]}""" };
            yield return new object[] { typeof(DeviceAmlSpec), """{"projectName":"P","devices":[{"name":"D","typeIdentifier":"x","deviceItems":[{"name":"I","builtIn":true,"attributes":{"A":null}}]}]}""" };
            yield return new object[] { typeof(UnifiedLayoutSpec), """{"items":[{"name":"A","type":"Lamp"}]}""" };
        }

        [Theory, MemberData(nameof(InvalidNested))]
        public void NestedShapeAndUnionAreClosed(Type type, string json) => Assert.ThrowsAny<Exception>(() => Read(type, json));

        [Fact]
        public void TextPreservesOmittedEmptyAndScalarNull()
        {
            var omitted = V4Json.Deserialize<UnifiedScreenSpec>("""{"items":[{"type":"Text","name":"A"}]}""");
            var empty = V4Json.Deserialize<UnifiedScreenSpec>("""{"items":[{"type":"Text","name":"A","text":"","properties":{"Visible":null}}]}""");
            Assert.False(omitted.ToBuilderInput()["items"]![0]!.AsObject().ContainsKey("text"));
            Assert.Equal("", empty.ToBuilderInput()["items"]![0]!["text"]!.GetValue<string>());
            Assert.True(empty.ToBuilderInput()["items"]![0]!["properties"]!.AsObject().ContainsKey("Visible"));
            Assert.Null(empty.ToBuilderInput()["items"]![0]!["properties"]!["Visible"]);
            Assert.IsType<UnifiedTextItem>(empty.Items[0]);
        }

        [Fact]
        public void CollectionsAreDefensiveAndDictionaryOrderIsPreserved()
        {
            var palette = new Dictionary<string, string> { ["z"] = "first", ["Page"] = "0xFF000000", ["a"] = "last" };
            var theme = new UnifiedThemeSpec(palette);
            palette["Page"] = "invalid";
            Assert.Equal(new[] { "z", "Page", "a" }, theme.Palette.Keys);
            Assert.Equal("0xFF000000", theme.Palette["Page"]);
            var items = new List<ClassicScreenItem> { new ClassicLampItem("L") };
            var screen = new ClassicScreenSpec(items);
            items.Clear();
            Assert.Single(screen.Items);
            Assert.Throws<ArgumentException>(() => new ClassicScreenSpec(new[] { new ClassicLampItem("L", left: int.MaxValue) }));
            Assert.Throws<ArgumentException>(() => new UnifiedTextItem("A", width: double.PositiveInfinity));
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("de-DE")]
        [InlineData("tr-TR")]
        public void SerializationAndDiscriminatorsAreCultureIndependent(string culture)
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Assert.Equal("""{"items":[{"type":"IOField","name":"A","width":12.5}]}""",
                    V4Json.Serialize(new UnifiedScreenSpec(new[] { new UnifiedIoFieldItem("A", width: 12.5) })));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Fact]
        public void InputSchemaHoistsDefinitionsAndHasNoUnconstrainedControl()
        {
            var schema = HmiSchemas.InputSchema<UnifiedScreenSpec>("design");
            Assert.True(HmiSchemaAccepts(schema, JsonNode.Parse("""{"design":{"items":[{"type":"Text","name":"A"}]}}""")));
            Assert.False(HmiSchemaAccepts(schema, JsonNode.Parse("""{"design":{"items":[{"type":"Lamp","name":"A"}]}}""")));
            Assert.False(HmiSchemaAccepts(schema, JsonNode.Parse("""{"design":{"items":[{"type":"Text","name":"A","unknown":1}]}}""")));
            Assert.Equal(4, schema["$defs"]!["UnifiedScreenItem"]!["oneOf"]!.AsArray().Count);
            Assert.Equal(5, HmiSchemas.For<ClassicScreenSpec>()["$defs"]!["ClassicScreenItem"]!["oneOf"]!.AsArray().Count);
        }

        [Theory]
        [InlineData(typeof(ClassicScreenSpec), "{\"screen\":{\"name\":\"S\",\"width\":319},\"items\":[]}")]
        [InlineData(typeof(ClassicScreenSpec), "{\"items\":[{\"type\":\"Text\",\"name\":\"A\",\"width\":0}]}")]
        [InlineData(typeof(ClassicScreenSpec), "{\"items\":[{\"type\":\"Rectangle\",\"name\":\"A\",\"text\":\"A\"}]}")]
        [InlineData(typeof(UnifiedScreenSpec), "{\"items\":[{\"type\":\"IOField\",\"name\":\"A\",\"properties\":{\"ProcessValue\":1}}]}")]
        [InlineData(typeof(UnifiedScreenSpec), "{\"items\":[{\"type\":\"Rectangle\",\"name\":\"A\",\"properties\":{\"ForeColor\":1}}]}")]
        [InlineData(typeof(UnifiedLayoutSpec), "{\"items\":[{\"name\":\"A\",\"text\":\"A\"}]}")]
        [InlineData(typeof(UnifiedLayoutSpec), "{\"items\":[{\"name\":\"A\",\"type\":\"IOField\",\"properties\":{\"ProcessValue\":1}}]}")]
        [InlineData(typeof(UnifiedThemeSpec), "{\"palette\":{\"Text\":\"#FF123456\"}}")]
        [InlineData(typeof(DeviceItemSpec), "{\"name\":\"A\"}")]
        [InlineData(typeof(DeviceItemSpec), "{\"name\":\"A\",\"role\":\"Module\",\"typeIdentifier\":\"x\"}")]
        public void SchemasAndRuntimeBothRejectExpressibleConstraints(Type type, string json)
        {
            Assert.False(HmiSchemaAccepts(HmiSchemas.For(type), JsonNode.Parse(json)));
            Assert.ThrowsAny<Exception>(() => Read(type, json));
        }

        [Fact]
        public void UnifiedStablePropertySchemaTracksTheCurrentServiceAllowlist()
        {
            var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
            Assert.NotNull(directory);
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(directory!.FullName, "src/Engine/Siemens/Services/UnifiedHmiService.cs"));
            int start = source.IndexOf("private static string ValidateUnifiedHmiDesignProperty", StringComparison.Ordinal);
            int end = source.IndexOf("private bool TrySetEngineeringAttribute", start, StringComparison.Ordinal);
            string method = source.Substring(start, end - start);
            var stable = System.Text.RegularExpressions.Regex.Match(method, @"var stable = new HashSet<string>.*?\{(?<values>.*?)\};", System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.True(stable.Success);
            var names = System.Text.RegularExpressions.Regex.Matches(stable.Groups["values"].Value, "\"([^\"]+)\"")
                .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToArray();
            var advertised = HmiSchemas.For<UnifiedIoFieldItem>()["$defs"]!["UnifiedIoFieldItem"]!["properties"]!["properties"]!["propertyNames"]!["enum"]!.AsArray()
                .Select(n => n!.GetValue<string>()).ToArray();
            Assert.Equal(names, advertised);
            foreach (string excluded in new[] { "ForeColor", "Text", "Font", "Content", "Padding" })
                Assert.Contains("prop.Equals(\"" + excluded + "\", StringComparison.OrdinalIgnoreCase)", method);
        }

        // A small evaluator for the emitted vocabulary, independent of DTO reflection/converters.
        // The full draft-2020-12 schema is also usable by the later host's schema validator.
        internal static bool HmiSchemaAccepts(JsonObject schema, JsonNode? value)
        {
            var document = JsonNode.Parse(schema.ToJsonString())!.AsObject();
            return Check(document, value, document);
        }
        private static bool Check(JsonNode rule, JsonNode? value, JsonObject root)
        {
            if (rule is JsonValue flag && flag.TryGetValue<bool>(out var allowed)) return allowed;
            var schema = rule.AsObject();
            if (schema["$ref"] is JsonNode reference && !Check(root["$defs"]![reference.GetValue<string>().Split('/').Last()]!, value, root)) return false;
            if (schema["type"] is JsonNode types)
            {
                var list = types is JsonArray array ? array.Select(t => t!.GetValue<string>()) : new[] { types.GetValue<string>() };
                if (!list.Any(t => MatchesType(t, value))) return false;
            }
            if (schema.ContainsKey("const") && !JsonNode.DeepEquals(schema["const"], value)) return false;
            if (schema["enum"] is JsonArray choices && !choices.Any(c => JsonNode.DeepEquals(c, value))) return false;
            if (schema["oneOf"] is JsonArray one && one.Count(r => Check(r!, value, root)) != 1) return false;
            if (schema["anyOf"] is JsonArray any && !any.Any(r => Check(r!, value, root))) return false;
            if (schema["allOf"] is JsonArray all && !all.All(r => Check(r!, value, root))) return false;
            if (schema["not"] is JsonNode not && Check(not, value, root)) return false;
            if (schema["if"] is JsonNode condition && Check(condition, value, root) && schema["then"] is JsonNode then && !Check(then, value, root)) return false;
            if (value is JsonObject obj)
            {
                if (schema["required"] is JsonArray required && required.Any(r => !obj.ContainsKey(r!.GetValue<string>()))) return false;
                foreach (var pair in obj)
                {
                    if (schema["propertyNames"] is JsonNode names && !Check(names, JsonValue.Create(pair.Key)!, root)) return false;
                    var propertyRule = schema["properties"]?[pair.Key] ?? schema["additionalProperties"];
                    if (propertyRule != null && !Check(propertyRule, pair.Value, root)) return false;
                }
            }
            if (value is JsonArray values)
            {
                if (schema["minItems"] is JsonNode min && values.Count < min.GetValue<int>()) return false;
                if (schema["maxItems"] is JsonNode max && values.Count > max.GetValue<int>()) return false;
                if (schema["items"] is JsonNode items && values.Any(v => !Check(items, v, root))) return false;
            }
            if (value is JsonValue scalar)
            {
                if (scalar.TryGetValue<string>(out var str) && schema["pattern"] is JsonNode pattern && !System.Text.RegularExpressions.Regex.IsMatch(str, pattern.GetValue<string>())) return false;
                if (scalar.TryGetValue<double>(out var number))
                {
                    if (schema["minimum"] is JsonNode min && number < min.GetValue<double>()) return false;
                    if (schema["maximum"] is JsonNode max && number > max.GetValue<double>()) return false;
                }
            }
            return true;
        }
        private static bool MatchesType(string type, JsonNode? value)
        {
            var kind = value?.GetValueKind() ?? JsonValueKind.Null;
            return type switch
            {
                "object" => kind == JsonValueKind.Object, "array" => kind == JsonValueKind.Array,
                "string" => kind == JsonValueKind.String, "boolean" => kind == JsonValueKind.True || kind == JsonValueKind.False,
                "null" => kind == JsonValueKind.Null, "number" => kind == JsonValueKind.Number,
                "integer" => kind == JsonValueKind.Number && value!.GetValue<double>() % 1 == 0,
                _ => throw new InvalidOperationException(type)
            };
        }
    }
}
