using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class ArgumentPointerTests
    {
        private static readonly InputSchema Schema = InputSchema.Object(new Dictionary<string, InputSchema> {
            ["devicePath"] = InputSchema.Array(InputSchema.String()),
            ["kind"] = InputSchema.String(allowed: new[] { "software", "hardware" }),
            ["properties"] = InputSchema.Map(InputSchema.String()),
            ["a~/b"] = InputSchema.Boolean() });

        [Theory]
        [InlineData("token=sk-live")]
        [InlineData("123456")]
        public void Private_pointer_segments_hide_unquoted_data(string key)
        {
            var data = new System.Text.Json.Nodes.JsonObject {
                ["evidence"] = new System.Text.Json.Nodes.JsonObject { ["message"] = "Segment " + key + " is unknown." },
                ["items"] = new System.Text.Json.Nodes.JsonArray("Segment " + key + " is unknown.") };
            var error = new Error("Segment " + key + " is unknown.",
                new InvalidArgumentDetails("/devicePath/" + key, Array.Empty<string>()));
            var restricted = Schema.Restrict(error, JsonSerializer.Deserialize<JsonElement>("{\"devicePath\":[\"Main\"]}"), data);
            Assert.Equal("/devicePath", ((InvalidArgumentDetails)restricted.Details).Parameter);
            Assert.Equal("Input does not satisfy the declared contract.", restricted.Message);
            Assert.Equal("Input does not satisfy the declared contract.", (string?)data["evidence"]!["message"]);
            Assert.Equal("Input does not satisfy the declared contract.", (string?)data["items"]![0]);
            Assert.DoesNotContain(key, data.ToJsonString(), StringComparison.Ordinal);
        }

        [Fact]
        public void Data_words_are_preserved_and_disclosed_keys_use_whole_safe_values()
        {
            var data = new System.Text.Json.Nodes.JsonObject {
                ["evidence"] = new System.Text.Json.Nodes.JsonObject { ["message"] = "The Visible property is read-only." },
                ["items"] = new System.Text.Json.Nodes.JsonArray("The path has a name.", "Rejected 'Visible'."), ["Visible"] = false };
            var error = new Error("Invalid properties.", new InvalidArgumentDetails("properties", Array.Empty<string>()));
            Schema.Restrict(error, JsonSerializer.Deserialize<JsonElement>("{\"properties\":{\"Visible\":false},\"path\":1,\"name\":1}"), data);
            Assert.Equal("The Visible property is read-only.", (string?)data["evidence"]!["message"]);
            Assert.Equal("The path has a name.", (string?)data["items"]![0]);
            Assert.Equal("Input does not satisfy the declared contract.", (string?)data["items"]![1]);
            Assert.False(data.ContainsKey("Visible"));
        }

        [Fact]
        public void Worker_allowed_values_are_limited_to_the_declared_enum()
        {
            var error = new Error("Invalid kind.", new InvalidArgumentDetails("kind", new[] { "software", "token=sk-live", "123456", "hardware" }));
            var restricted = Schema.Restrict(error, JsonSerializer.Deserialize<JsonElement>("{\"kind\":\"bad\"}"));
            Assert.Equal(new[] { "software", "hardware" }, ((InvalidArgumentDetails)restricted.Details).AllowedValues);
            restricted = Schema.Restrict(new Error("Invalid properties.", new InvalidArgumentDetails("properties", new[] { "token=sk-live" })),
                JsonSerializer.Deserialize<JsonElement>("{}"));
            Assert.Empty(((InvalidArgumentDetails)restricted.Details).AllowedValues);
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"properties\":{}}")]
        [InlineData("{\"properties\":{\"kind\":null,\"category\":null}}")]
        public void Enum_declarations_skip_missing_parameters(string json)
        {
            var schema = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            string original = schema.ToJsonString();
            TiaMcpServer.ModelContextProtocol.SchemaHintsLogic.DeclareArgumentEnums("CompareProjects", schema);
            TiaMcpServer.ModelContextProtocol.SchemaHintsLogic.DeclareArgumentEnums("ExportUnifiedEngineeringList", schema);
            Assert.Equal(original, schema.ToJsonString());
        }

        [Theory]
        [InlineData("{\"devicePath\":[\"one\",\"two\",3]}", "/devicePath/2", "string")]
        [InlineData("{\"devicePath\":{\"123456\":1}}", "/devicePath", "array of strings")]
        [InlineData("{\"kind\":\"bad\"}", "/kind", "one of the allowed values")]
        [InlineData("{\"a~/b\":1}", "/a~0~1b", "boolean")]
        [InlineData("[]", "", "object")]
        public void Schema_errors_preserve_exact_declared_pointer_and_expected_form(string json, string pointer, string expected)
        {
            var error = Schema.Validate(V4Json.Deserialize<JsonElement>(json), "arguments")!;
            var details = V4Json.Deserialize<JsonElement>(V4Json.Serialize(error)).GetProperty("details");
            Assert.Equal(pointer, details.GetProperty("parameter").GetString());
            Assert.Equal(new[] { pointer }, details.GetProperty("parameters").EnumerateArray().Select(p => p.GetString()));
            Assert.Equal(expected, details.GetProperty("expected").GetString());
            Assert.Equal("Input does not satisfy the declared contract.", error.Message);
            if (pointer == "/kind")
                Assert.Equal(new[] { "software", "hardware" }, details.GetProperty("allowedValues").EnumerateArray().Select(p => p.GetString()));
        }

        [Theory]
        [InlineData("token=sk-live")]
        [InlineData("123456")]
        public void Undeclared_duplicate_and_map_keys_are_private(string key)
        {
            foreach (string json in new[] {
                "{\"" + key + "\":1}",
                "{\"" + key + "\":1,\"" + key + "\":2}",
                "{\"properties\":{\"" + key + "\":1}}",
                "{\"devicePath\":{\"" + key + "\":1}}" })
            {
                var error = Schema.Validate(JsonSerializer.Deserialize<JsonElement>(json), "arguments")!;
                Assert.DoesNotContain(key, V4Json.Serialize(error), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void All_invalid_array_entries_are_reported()
        {
            var error = Schema.Validate(V4Json.Deserialize<JsonElement>("{\"devicePath\":[1,2]}"), "arguments")!;
            Assert.Equal(new[] { "/devicePath/0", "/devicePath/1" }, ((InvalidArgumentDetails)error.Details).Parameters);
        }

        [Theory]
        [InlineData("{\"first\":false,\"second\":\"long\"}", ErrorCode.InvalidArgument)]
        [InlineData("{\"second\":\"long\",\"first\":false}", ErrorCode.LimitExceeded)]
        public void Collecting_locations_retains_the_first_failure_classification(string json, ErrorCode code)
        {
            var schema = InputSchema.Object(new Dictionary<string, InputSchema> {
                ["first"] = InputSchema.String(), ["second"] = InputSchema.String(maxLength: 1) });
            Assert.Equal(code, schema.Validate(JsonSerializer.Deserialize<JsonElement>(json), "arguments")!.Code);
        }

        [Fact]
        public void Batch_business_validation_preserves_its_argument_path()
        {
            var target = new ToolTarget("Fixture", InputSchema.Object(new Dictionary<string, InputSchema> {
                ["softwarePath"] = InputSchema.String() }), new InputBudget(), read: true,
                businessValidation: _ => new Error("Invalid software path.",
                    new InvalidArgumentDetails("softwarePath", Array.Empty<string>(), expected: "exact software path")));
            var result = ToolCallValidator.Create(ToolCallMode.ReadBatch, new[] { target })
                .Read("[{\"name\":\"Fixture\",\"arguments\":{\"softwarePath\":\"invalid\"}}]", "operations");
            Assert.Equal("/operations/0/arguments/softwarePath", ((InvalidArgumentDetails)result.Error!.Details).Parameter);
        }

        [Theory]
        [InlineData("token=sk-live")]
        [InlineData("123456")]
        public void Worker_pointers_are_limited_to_the_tool_schema(string key)
        {
            var error = new Error("Invalid field " + key + ".", new InvalidArgumentDetails("/devicePath/" + key,
                Array.Empty<string>(), expected: key));
            var data = new System.Text.Json.Nodes.JsonObject { ["field"] = key };
            var restricted = Schema.Restrict(error, JsonSerializer.Deserialize<JsonElement>("{\"devicePath\":{\"" + key + "\":1}}"), data);
            Assert.Equal("/devicePath", ((InvalidArgumentDetails)restricted.Details).Parameter);
            Assert.DoesNotContain(key, V4Json.Serialize(restricted), StringComparison.Ordinal);
            Assert.DoesNotContain(key, data.ToJsonString(), StringComparison.Ordinal);
            data["evidence"] = new System.Text.Json.Nodes.JsonObject { ["parameter"] = key };
            error = new Error("Invalid field " + key + ".", new InvalidArgumentDetails("", Array.Empty<string>(), expected: key));
            restricted = Schema.Restrict(error, JsonSerializer.Deserialize<JsonElement>("{}"), data);
            Assert.DoesNotContain(key, V4Json.Serialize(restricted), StringComparison.Ordinal);
            Assert.DoesNotContain(key, data.ToJsonString(), StringComparison.Ordinal);
        }

        [Fact]
        public void Unknown_empty_segments_stop_at_the_declared_parent()
        {
            var error = new Error("Invalid field.", new InvalidArgumentDetails("/devicePath/", Array.Empty<string>()));
            var data = new System.Text.Json.Nodes.JsonObject { ["field"] = "value" };
            var restricted = Schema.Restrict(error, JsonSerializer.Deserialize<JsonElement>("{\"devicePath\":{\"\":1}}"), data);
            Assert.Equal("/devicePath", ((InvalidArgumentDetails)restricted.Details).Parameter);
        }

        [Fact]
        public void Ordinary_diagnostic_words_are_preserved_and_quoted_private_fields_are_hidden()
        {
            var schema = InputSchema.Object(new Dictionary<string, InputSchema> { ["expectedProject"] = InputSchema.String() });
            var arguments = JsonSerializer.Deserialize<JsonElement>("{\"expectedProject\":\"C:/Fixture.ap21\",\"NAME\":true}");
            var error = HostBehavior.ExpectedProjectArgument("C:/Fixture.ap21")!;
            Assert.Equal(error.Message, schema.Restrict(error, arguments).Message);
            var ordinary = JsonSerializer.Deserialize<JsonElement>("{\"was\":true,\"must\":true}");
            foreach (string message in new[] { "The request argument was refused before operation.", "The parameter must be a string." })
                Assert.Equal(message, schema.Restrict(new Error(message,
                    new InvalidArgumentDetails("expectedProject", Array.Empty<string>())), ordinary).Message);
            foreach (string message in new[] { "Invalid field 'NAME'.", "Unknown property NAME.",
                "Invalid field token=sk-live.", "Cannot resolve token=sk-live here." })
            {
                string key = message.Contains("token=sk-live", StringComparison.Ordinal) ? "token=sk-live" : "NAME";
                var refusal = new Error(message, new InvalidArgumentDetails(key, Array.Empty<string>(), expected: key));
                var result = schema.Restrict(refusal, arguments);
                Assert.Equal("Input does not satisfy the declared contract.", result.Message);
                Assert.DoesNotContain(key, V4Json.Serialize(result), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void A_private_key_matching_a_fixed_diagnostic_word_keeps_the_message()
        {
            var arguments = JsonSerializer.Deserialize<JsonElement>("{\"contract\":1}");
            var error = Schema.Validate(arguments, "arguments")!;
            Assert.Equal(error.Message, Schema.Restrict(error, arguments).Message);
        }

        [Theory]
        [InlineData("token=sk-live")]
        [InlineData("123456")]
        public void Worker_map_keys_are_private_when_the_error_names_only_the_parent(string key)
        {
            foreach (string message in new[] { "Unknown property " + key + ".", "Missing or invalid target field(s): " + key + "." })
            {
                var error = new Error(message,
                    new InvalidArgumentDetails("properties", Array.Empty<string>(), expected: key));
                var data = new System.Text.Json.Nodes.JsonObject { ["message"] = "Unknown property " + key + "." };
                var restricted = Schema.Restrict(error,
                    JsonSerializer.Deserialize<JsonElement>("{\"properties\":{\"" + key + "\":\"value\"}}"), data);
                var meta = new Meta(DateTimeOffset.UtcNow, "21", "Fixture", "fixture", Outcome.RejectedBeforeOperation,
                    Execution.NotStarted, false, BehaviorPolicy.NotApplicable, Completeness.None, null, Array.Empty<Warning>());
                var result = McpResult.From(Envelope.Create(data, restricted, meta));
                Assert.Equal("/properties", ((InvalidArgumentDetails)restricted.Details).Parameter);
                Assert.DoesNotContain(key, result.StructuredContent.GetRawText(), StringComparison.Ordinal);
                Assert.DoesNotContain(key, result.Content[0].Text, StringComparison.Ordinal);
            }
        }

        [Theory]
        [InlineData("token=sk-live")]
        [InlineData("123456")]
        public void Multilingual_culture_keys_are_data_in_both_result_forms(string key)
        {
            var raw = TiaMcp.Shared.UnifiedHmiPropertyRules.Validate(typeof(ScreenItem),
                "{\"Text\":{\"" + key + "\":false}}")!;
            var error = V4Json.Deserialize<Error>(JsonSerializer.Serialize(raw));
            var meta = new Meta(DateTimeOffset.UtcNow, "21", "SetUnifiedScreenItemProperties", "fixture",
                Outcome.RejectedBeforeOperation, Execution.NotStarted, false, BehaviorPolicy.NotApplicable,
                Completeness.None, null, Array.Empty<Warning>());
            var result = McpResult.From(Envelope.Create<object?>(null, error, meta));
            Assert.Equal("/properties", ((InvalidArgumentDetails)error.Details).Parameter);
            Assert.DoesNotContain(key, result.StructuredContent.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(key, result.Content[0].Text, StringComparison.Ordinal);
        }

        public sealed class MultilingualText { }
        public sealed class ScreenItem
        {
            public MultilingualText Text { get; } = new MultilingualText();
        }
    }
}
