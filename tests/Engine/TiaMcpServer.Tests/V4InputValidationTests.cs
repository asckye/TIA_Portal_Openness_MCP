using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Runtime;
using TiaMcpServer.Siemens;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class V4InputValidationTests
    {
        private static JsonElement Json(string json) => V4Json.Deserialize<JsonElement>(json);
        private static void Limit<T>(InputResult<T> result, int maximum, int actual)
        {
            Assert.False(result.IsValid);
            Assert.Equal(ErrorCode.LimitExceeded, result.Error!.Code);
            var details = Assert.IsType<LimitExceededDetails>(result.Error.Details);
            Assert.Equal(maximum, details.Limit); Assert.Equal(actual, details.Actual);
        }

        [Fact]
        public void PresenceIsNotDefaultedOrCoerced()
        {
            var contract = PathValidator.Item();
            Assert.Equal(InputPresence.Missing, contract.Read((string?)null, "path", true).Presence);
            Assert.True(contract.Read((string?)null, "path", true).IsValid);
            Assert.False(contract.Read((string?)null, "path").IsValid);
            Assert.Equal(InputPresence.Null, contract.Read("null", "path", true).Presence);
            Assert.False(contract.Read("null", "path", true).IsValid);
            Assert.Empty(contract.Read("[]", "path").Value!);
            Assert.False(contract.Read("\"[]\"", "path").IsValid);
            var scalar = NativeValueValidator.Create(NativeValueValidator.Scalar(true));
            Assert.True(scalar.Read("null", "value").IsValid);
            Assert.Equal("null", V4Json.Serialize(scalar.Read("null", "value").Value));
            Assert.Equal("null", V4Json.Serialize(V4Json.Deserialize<Scalar>("null")));
            Assert.ThrowsAny<Exception>(() => V4Json.Serialize(default(Scalar)));
        }

        [Theory]
        [InlineData("[{\"name\":\"A\",\"value\":1,\"unknown\":0}]")]
        [InlineData("[{\"Name\":\"A\",\"value\":1}]")]
        [InlineData("[{\"name\":\"A\",\"value\":1,\"value\":2}]")]
        [InlineData("[{\"name\":\"A\",\"value\":1,\"Value\":2}]")]
        [InlineData("[{\"name\":\"A\"}]")]
        [InlineData("\"[{\\\"name\\\":\\\"A\\\",\\\"value\\\":1}]\"")]
        public void ClosedWritesRejectMisspellingsDuplicatesAndDoubleEncoding(string input)
        {
            var result = WriteValueValidator.Runtime().Read(input, "writes");
            Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
        }

        [Theory]
        [InlineData("{\"property\":\"Tags\",\"name\":null}")]
        [InlineData("{\"property\":\"Tags\",\"index\":null}")]
        [InlineData("{\"property\":\"Tags\",\"index\":1.5}")]
        [InlineData("{\"property\":\"Tags\",\"index\":2147483648}")]
        [InlineData("{\"property\":\"Tags\",\"name\":\"A\",\"index\":0}")]
        [InlineData("{\"property\":\"Tags\",\"property\":\"Blocks\"}")]
        [InlineData("{\"Property\":\"Tags\"}")]
        [InlineData("{\"property\":\"Tags\",\"other\":0}")]
        public void PropertyStepsAreClosedEvenWithoutFamilyAdapter(string input) =>
            Assert.ThrowsAny<Exception>(() => V4Json.Deserialize<PropertyStep>(input));

        [Fact]
        public void PathsPreserveSegmentEscapesAndRootMeaning()
        {
            const string input = "[\"A/B,C\\\\D\\\"E\",\"a\",\"A\"]";
            var result = PathValidator.Device().Read(input, "devicePath");
            Assert.Equal(new[] { "A/B,C\\D\"E", "a", "A" }, result.Value);
            Assert.True(PathValidator.Item().Read("[]", "itemPath").IsValid);
            Assert.False(PathValidator.Device().Read("[]", "devicePath").IsValid);
            Assert.False(PathValidator.Create(true, rejectDotSegments: true).Read("[\"..\"]", "path").IsValid);
            Limit(PathValidator.Device().Validate(Enumerable.Repeat("a", 65).ToArray(), "path"), 64, 65);
        }

        [Fact]
        public void BudgetsBoundRawAndCompactJsonAndRemainOnTypedInputs()
        {
            var contract = new InputContract<string[]>(InputSchema.Array(InputSchema.String()), new InputBudget(characters: 5));
            Assert.True(contract.Read("[\"a\"]", "items").IsValid);
            const string spaced = "  [  \"a\"  ]  ";
            Limit(contract.Read(spaced, "items"), 5, spaced.Length);
            Limit(contract.Read("[\"ab\"]", "items"), 5, 6);
            Limit(contract.Validate(new[] { "ab" }, "items"), 5, 6);
            var strings = new InputContract<string[]>(InputSchema.Array(InputSchema.String()), new InputBudget(stringLength: 3, items: 2));
            Limit(strings.Read("[\"abcd\"]", "items"), 3, 4);
            Limit(strings.Read("[\"a\",\"b\",\"c\"]", "items"), 2, 3);
            var nested = NativeValueValidator.Create(new NativeValuePolicy(new InputSchema(Json("true")), new InputBudget(depth: 16)));
            Assert.True(nested.Read(new string('[', 16) + "0" + new string(']', 16), "value").IsValid);
            Limit(nested.Read(new string('[', 17) + "0" + new string(']', 17), "value"), 16, 17);
            Limit(nested.Read(new string('[', 65) + "0" + new string(']', 65), "value"), 16, 17);
        }

        [Fact]
        public void ReflectionRequiresDeclaredReadablePropertiesAndTracksTraversalBudget()
        {
            var policy = new PropertyPathPolicy(new Dictionary<string, PropertyAdmission> {
                ["Tags"] = new PropertyAdmission(true), ["Parent"] = new PropertyAdmission(true),
                ["Private"] = new PropertyAdmission(false), ["Indexed"] = new PropertyAdmission(true, true) });
            var contract = PropertyPathValidator.Create(policy);
            foreach (var name in new[] { "Parent", "parent", "Private", "Indexed", "GetType", "tags" })
                Assert.False(contract.Validate(new[] { new PropertyStep(name) }, "path").IsValid);
            Assert.False(contract.Validate(new[] { new PropertyStep("Tags", index: 0) }, "path").IsValid);
            Assert.False(contract.Validate(new[] { new PropertyStep("Tags"), new PropertyStep("Tags") }, "path").IsValid);
            Assert.Null(PropertyPathValidator.ValidateTraversal(10000, "path"));
            Assert.Equal(ErrorCode.LimitExceeded, PropertyPathValidator.ValidateTraversal(10001, "path")!.Code);
            var indexed = PropertyPathValidator.Create(new PropertyPathPolicy(policy.Properties, true, 10));
            Assert.True(indexed.Validate(new[] { new PropertyStep("Tags", index: 10) }, "path").IsValid);
            Assert.False(indexed.Validate(new[] { new PropertyStep("Tags", index: 11) }, "path").IsValid);
            Limit(contract.Validate(Enumerable.Repeat(new PropertyStep("Tags"), 25).ToArray(), "path"), 24, 25);
            Limit(contract.Read("[{\"property\":\"Tags\",\"name\":\"" + new string('a', 32768) + "\"}]", "path"), 32768, 32799);
        }

        [Fact]
        public void AttributeAdmissionKeepsSdkSpellingWritabilityTypesAndBudgets()
        {
            var fields = Enumerable.Range(0, 51).ToDictionary(i => "Key" + i, i => new AttributeRule(InputSchema.Scalar()));
            fields["ReadOnly"] = new AttributeRule(InputSchema.Scalar(), false);
            fields["Count"] = new AttributeRule(InputSchema.Integer(0, 10));
            var contract = AttributeMapValidator.Hardware(fields);
            Assert.False(contract.Read("{\"ReadOnly\":0}", "properties").IsValid);
            Assert.False(contract.Read("{\"Count\":11}", "properties").IsValid);
            Assert.False(contract.Read("{\"count\":1}", "properties").IsValid);
            Assert.False(contract.Read("{\"Key0\":0,\"Key0\":1}", "properties").IsValid);
            Assert.False(contract.Read("{\"Key0\":{}}", "properties").IsValid);
            string many = V4Json.Serialize(Enumerable.Range(0, 51).ToDictionary(i => "Key" + i, i => i));
            Limit(contract.Read(many, "properties"), 50, 51);
            string large = "{\"Key0\":\"" + new string('a', 16384) + "\"}";
            Limit(AttributeMapValidator.Safety(fields).Read(large, "properties"), 16384, large.Length);
            var dynamicKeys = V4Json.Deserialize<AttributeMap<Scalar>>("{\"SDKName\":true,\"sdkName\":false}");
            Assert.Equal(2, dynamicKeys.Count);
            Assert.Equal("{\"SDKName\":true,\"sdkName\":false}", V4Json.Serialize(dynamicKeys));
            var cpu = AttributeMapValidator.Cpu(new Dictionary<string, AttributeRule> { ["Name"] = new AttributeRule(InputSchema.String()) });
            Assert.True(cpu.Read("{\"exactAttributes\":{\"Name\":\"PLC_1\"}}", "settings").IsValid);
            Assert.False(cpu.Read("{\"Name\":\"PLC_1\"}", "settings").IsValid);
        }

        [Theory]
        [InlineData("[\"en-US\",\"zh-CN\"]")]
        [InlineData("[\" en-US \"]")]
        [InlineData("[\"en-US\",\"EN-us\"]")]
        [InlineData("[]")]
        [InlineData("[null]")]
        [InlineData("[\" \"]")]
        public void CultureListEquivalence(string input)
        {
            // PlcBlockServicesLogic.cs:76-90.
            string[]? expected = null;
            var error = Record.Exception(() => expected = PlcBlockServicesLogic.ParseCultureNames(input).Select(c => c.Name).ToArray());
            var result = NameListValidator.CultureNames().Read(input, "cultures");
            Assert.Equal(error == null, result.IsValid);
            if (result.IsValid) Assert.Equal(expected, result.Value);
        }

        [Fact]
        public void TextMapsEnforceActiveCulturesPromptOptionsAndAccessLevelsWithoutEcho()
        {
            Assert.False(TextMapValidator.Comments(new[] { "en-US" }).Read("{\"zh-CN\":\"text\"}", "comments").IsValid);
            var prompts = new Dictionary<string, PromptAdmission> {
                ["StopModules"] = new PromptAdmission(new[] { "StopAll", "NoAction" }),
                ["OverwriteHmiData"] = new PromptAdmission(checkbox: true), ["ModuleReadAccessPassword"] = new PromptAdmission(password: true) };
            var contract = TextMapValidator.PromptAnswers(prompts);
            Assert.Equal("{\"StopModules\":\"StopAll\",\"OverwriteHmiData\":\"true\"}",
                V4Json.Serialize(contract.Read("{\"StopModules\":\"stopall\",\"OverwriteHmiData\":\"TRUE\"}", "answers").Value));
            foreach (var input in new[] { "{\"ModuleReadAccessPassword\":\"private-secret\"}", "{\"StopModules\":\"private-secret\"}",
                "{\"private-secret\":\"answer\"}", "{\"StopModules\":\"StopAll\",\"stopmodules\":\"NoAction\"}" })
            {
                var result = contract.Read(input, "answers");
                Assert.False(result.IsValid);
                Assert.DoesNotContain("private-secret", V4Json.Serialize(result.Error));
            }
            Limit(contract.Read("{\"StopModules\":\"" + new string('x', 65) + "\"}", "answers"), 64, 65);
            var access = TextMapValidator.AccessLevels();
            Assert.True(access.Read("{\"Inputs\":4,\"SafetyGlobalDBs\":1}", "accessLevels").IsValid);
            foreach (var input in new[] { "{\"Inputs\":5}", "{\"Inputs\":1.5}", "{\"Inputs\":2147483648}", "{\"inputs\":1}", "{\"SafetyGlobalDBs\":2}" })
                Assert.False(access.Read(input, "accessLevels").IsValid);
            Assert.Equal("{\"Inputs\":4,\"SafetyGlobalDBs\":1}", V4Json.Serialize(access.Read("{\"Inputs\":4,\"SafetyGlobalDBs\":1}", "accessLevels").Value));
        }

        [Theory]
        [InlineData("Int8", "127")]
        [InlineData("Int8", "128")]
        [InlineData("UInt8", "-1")]
        [InlineData("UInt64", "\"18446744073709551615\"")]
        [InlineData("UInt64", "\"18446744073709551616\"")]
        [InlineData("Int32", "1.5")]
        [InlineData("Int32", "\"16#7F\"")]
        [InlineData("Bool", "true")]
        [InlineData("Bool", "\"1\"")]
        [InlineData("Bool", "\"yes\"")]
        [InlineData("WChar", "\"A\"")]
        [InlineData("WChar", "\"AB\"")]
        [InlineData("Double", "42.5")]
        [InlineData("Double", "null")]
        [InlineData("Double", "[]")]
        public void SimulationConversionEquivalenceInBothCultures(string type, string input)
        {
            // PlcSimAdvancedLogic.cs:166-213.
            var previous = CultureInfo.CurrentCulture;
            try
            {
                foreach (var culture in new[] { "en-US", "de-DE" })
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    var error = Record.Exception(() => PlcSimAdvancedLogic.ConvertValue(JsonNode.Parse(input), type));
                    var result = NativeValueValidator.Create(NativeValueValidator.Simulation(type)).Read(input, "value");
                    Assert.Equal(error == null, result.IsValid);
                    if (result.IsValid) Assert.Equal(input, V4Json.Serialize(result.Value));
                }
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Theory]
        [InlineData("setMotorCode", "{\"motorCode\":1,\"motorDataSet\":65535}")]
        [InlineData("setMotorCode", "{\"motorCode\":1,\"motorDataSet\":65536}")]
        [InlineData("setMotorCode", "{\"motorCode\":1.5}")]
        [InlineData("setMotorCode", "{\"motorCode\":-1}")]
        [InlineData("setMotorCode", "{}")]
        [InlineData("changeActivationState", "{\"activationState\":\"Activate\"}")]
        [InlineData("changeActivationState", "{\"activationState\":\"activate\"}")]
        [InlineData("setSimoGearMlfb", "{\"mlfb\":\" \"}")]
        [InlineData("readMotorConfiguration", "{\"entries\":{\"p305\":20}}")]
        [InlineData("projectMotorConfiguration", "{\"entries\":{\"p305\":20}}")]
        [InlineData("projectMotorConfiguration", "{\"entries\":{\"p305\":{}}}")]
        [InlineData("projectMotorConfiguration", "{\"entries\":{}}")]
        [InlineData("read", "{}")]
        [InlineData("read", "{\"dataSet\":0}")]
        [InlineData("setEncoderType", "{\"rotaryLinear\":\"Rotary\"}")]
        public void DriveFunctionActionEquivalence(string action, string input)
        {
            // StartdriveLogic.cs:208-280.
            var error = Record.Exception(() => StartdriveLogic.ValidateFunctionRequest(action, input, true));
            var result = NativeValueValidator.Create(DriveFunctionPolicy.Create(action)).Read(input, "value");
            Assert.Equal(error == null, result.IsValid);
            if (result.IsValid) Assert.Equal(input, V4Json.Serialize(result.Value));
        }

        [Fact]
        public void WritesRetainCountUniquenessAndOnlineGuard()
        {
            const string write = "[{\"name\":\"Ready\",\"value\":true}]";
            Assert.False(WriteValueValidator.Runtime(false, false).Read(write, "writes").IsValid);
            Assert.True(WriteValueValidator.Runtime(false, true).Read(write, "writes").IsValid);
            var writes = Enumerable.Range(0, 501).Select(i => new WriteValue("T" + i, new NativeValue(Json("true")))).ToArray();
            Assert.True(WriteValueValidator.Runtime().Validate(writes.Take(500).ToArray(), "writes").IsValid);
            Limit(WriteValueValidator.Runtime().Validate(writes, "writes"), 500, 501);
            var simulation = WriteValueValidator.Create(NativeValueValidator.Scalar(), true, false, false, _ => NativeValueValidator.Simulation("Int8"));
            Assert.False(simulation.Read("[{\"name\":\"T\",\"value\":128}]", "writes").IsValid);
            var bounded = WriteValueValidator.Create(new NativeValuePolicy(InputSchema.Scalar(), new InputBudget(characters: 2)), true, false,
                targetValuePolicy: _ => NativeValueValidator.Simulation("Int16"));
            Limit(bounded.Read("[{\"name\":\"T\",\"value\":100}]", "writes"), 2, 3);
        }

        [Fact]
        public void CallsValidateTargetSchemaAllowlistsNestingAndPreview()
        {
            var schema = InputSchema.Object(new Dictionary<string, InputSchema> {
                ["dryRun"] = InputSchema.Boolean(), ["count"] = InputSchema.Integer(0, 4),
                ["path"] = new InputSchema(PathValidator.Device().Schema) }, new[] { "count", "path" });
            var target = new ToolTarget("SetValue", schema, new InputBudget(characters: 128), preview: true);
            var preview = ToolCallValidator.Create(ToolCallMode.PreviewBatch, new[] { target });
            const string valid = "[{\"name\":\"SetValue\",\"arguments\":{\"count\":4,\"path\":[\"PLC\"],\"dryRun\":false}}]";
            var result = preview.Read(valid, "calls");
            Assert.True(result.IsValid);
            Assert.True(result.Value![0].Arguments.Json.GetProperty("dryRun").GetBoolean());
            foreach (var bad in new[] { "{\"count\":5,\"path\":[\"PLC\"]}", "{\"Count\":1,\"path\":[\"PLC\"]}", "{\"count\":1}",
                "{\"count\":1,\"path\":[]}", "{\"count\":1,\"path\":\"[\\\"PLC\\\"]\"}", "{\"count\":1,\"count\":2,\"path\":[\"PLC\"]}" })
                Assert.False(ToolCallValidator.Arguments(target).Read(bad, "arguments").IsValid);
            Assert.False(ToolCallValidator.Create(ToolCallMode.ReadBatch, new[] { target }).Read(valid, "calls").IsValid);
            Assert.False(ToolCallValidator.Create(ToolCallMode.PreviewBatch, new[] { target }, 1).Read(valid, "calls").IsValid);
            var empty = InputSchema.Object(new Dictionary<string, InputSchema>());
            var read = new ToolTarget("GetValue", empty, new InputBudget(), read: true);
            var readCalls = Enumerable.Repeat(new ToolCall("GetValue", new ToolArguments(Json("{}"))), 51).ToArray();
            Limit(ToolCallValidator.Create(ToolCallMode.ReadBatch, new[] { read }).Validate(readCalls, "calls"), 50, 51);
            var nested = new ToolTarget("CallTool", empty, new InputBudget(), read: true, transaction: true);
            Assert.False(ToolCallValidator.Create(ToolCallMode.ReadBatch, new[] { nested }).Read("[{\"name\":\"CallTool\",\"arguments\":{}}]", "calls").IsValid);
            Assert.Throws<ArgumentException>(() => new InputSchema(Json("{\"$ref\":\"https://example.invalid/schema\"}")));
            Assert.Throws<ArgumentException>(() => new InputSchema(Json("{\"format\":\"unvalidated-format\"}")));
        }

        [Fact]
        public void SchemasRemainEnforcedAfterTypedConstruction()
        {
            var contract = NativeValueValidator.Create(new NativeValuePolicy(InputSchema.Array(InputSchema.Integer(0, 10), maximum: 2), new InputBudget()));
            Assert.True(contract.Read("[0,10]", "value").IsValid);
            Assert.False(contract.Read("{\"value\":1}", "value").IsValid);
            Assert.False(contract.Validate(new NativeValue(Json("[11]")), "value").IsValid);
            Limit(contract.Read("[0,1,2]", "value"), 2, 3);
            Assert.Equal("array", contract.Schema.GetProperty("type").GetString());
            Assert.Equal(10, contract.Schema.GetProperty("items").GetProperty("maximum").GetInt32());
            Assert.False(new InputSchema(contract.Schema).Validate(Json("[11]"), "value") == null);
        }
    }
}
