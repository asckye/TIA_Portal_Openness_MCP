using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcpServer.Runtime;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class V4DomainOpenPipeTests
    {
        // Expert message acceptance, cookie normalization and read-only classification:
        // src/Logic/Runtime/RuntimeChannelsLogic.cs:335-358. No channel is opened.
        [Theory]
        [InlineData("ReadConfig", true)]
        [InlineData("BrowseConfiguredAlarms", true)]
        [InlineData("BrowseAlarmClasses", true)]
        [InlineData("WriteVendorExtension", false)]
        [InlineData("readconfig", true)]
        [InlineData("BROWSECONFIGUREDALARMS", true)]
        [InlineData("browsealarmclasses", true)]
        [InlineData("readtag", true)]
        [InlineData("READALARM", true)]
        [InlineData("browsetags", true)]
        [InlineData("ReadUnknown", false)]
        [InlineData("BrowseUnknown", false)]
        [InlineData(" SubscribeTag", false)]
        public void Expert_requests_preserve_normalized_values_and_read_only_policy(string message, bool readOnly)
        {
            const string parameters = """{"Config":{"Enabled":true,"Offset":-1.25,"Text":"中文","Missing":null,"Values":[1,"2",false,null,[],{}]},"tags":["lower"],"Tags":["upper"],"":0}""";
            var legacy = RuntimeChannelsLogic.PrepareRawRequest(Envelope(message, parameters, "fixed", true));
            var request = DomainValidation.Read<OpenPipeRequest>(Envelope(message, parameters, "fixed", false));
            Assert.Equal(legacy.line, request.ToWire("unused"));
            Assert.Equal(legacy.message, request.Message); Assert.Equal(legacy.cookie, request.ClientCookie);
            Assert.Equal(legacy.isReadOnly, request.IsReadOnly); Assert.Equal(readOnly, request.IsReadOnly);
            var values = Assert.IsType<OpenPipeExpertParams>(request.Params).Values;
            Assert.Equal("中文", values["Config"].Properties["Text"].Scalar.String);
            Assert.Equal(JsonValueKind.Null, values["Config"].Properties["Missing"].Kind);
            Assert.Equal(6, values["Config"].Properties["Values"].Items.Count);
            Assert.Equal("lower", values["tags"].Items[0].Scalar.String);
            Assert.Equal("upper", values["Tags"].Items[0].Scalar.String);
            DomainValidation.OpenPipe(request, "21", true, false);
            if (readOnly) DomainValidation.OpenPipe(request, "20", true, true);
            else Assert.Throws<NotSupportedException>(() => DomainValidation.OpenPipe(request, "20", true, true));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" \t")]
        [InlineData("caller-中文")]
        public void Expert_cookie_generation_and_omitted_params_match_legacy(string? cookie)
        {
            var legacy = RuntimeChannelsLogic.PrepareRawRequest(Envelope("ReadConfig", null, cookie, true));
            var request = DomainValidation.Read<OpenPipeRequest>(Envelope("ReadConfig", null, cookie, false));
            Assert.False(request.HasParams);
            Assert.Empty(Assert.IsType<OpenPipeExpertParams>(request.Params).Values);
            Assert.Equal(legacy.line, request.ToWire(legacy.cookie));
            Assert.False(JsonNode.Parse(request.ToWire(legacy.cookie))!.AsObject().ContainsKey("Params"));
        }

        [Theory]
        [InlineData("Subscribe")]
        [InlineData("SubscribeTag")]
        [InlineData("subscribeAlarm")]
        [InlineData("SUBSCRIBEVendor")]
        [InlineData("Unsubscribe")]
        [InlineData("UnsubscribeTag")]
        [InlineData("unsubscribeAlarm")]
        [InlineData("UNSUBSCRIBEVendor")]
        public void Streaming_names_keep_the_legacy_NotSupported_refusal(string message)
        {
            var legacy = Assert.Throws<NotSupportedException>(() => RuntimeChannelsLogic.PrepareRawRequest(Envelope(message, "{}", null, true)));
            var current = Assert.Throws<NotSupportedException>(() => DomainValidation.Read<OpenPipeRequest>(Envelope(message, "{}", null, false)));
            Assert.Equal(legacy.Message, current.Message);
        }

        [Theory]
        [InlineData("SubscribeTag", "\"{}\"")]
        [InlineData("SubscribeTag", "null")]
        [InlineData("SubscribeTag", "[]")]
        [InlineData("UnsubscribeTag", "\"{}\"")]
        [InlineData("UnsubscribeTag", "null")]
        [InlineData("UnsubscribeTag", "[]")]
        public void Streaming_refusal_precedes_params_shape_validation(string message, string parameters)
        {
            var legacy = Assert.Throws<NotSupportedException>(() => RuntimeChannelsLogic.PrepareRawRequest(Envelope(message, parameters, null, true)));
            string json = Envelope(message, parameters, null, false);
            var current = Assert.Throws<NotSupportedException>(() => DomainValidation.Read<OpenPipeRequest>(json));
            Assert.Equal(legacy.Message, current.Message);
            // The serializer also rejects before Params validation, adding its JSON path.
            Assert.Throws<NotSupportedException>(() => V4Json.Deserialize<OpenPipeRequest>(json));
        }

        [Theory]
        [InlineData("{", "{")]
        [InlineData("[]", "[]")]
        [InlineData("null", "null")]
        [InlineData("{}", "{}")]
        [InlineData("\"{}\"", "\"{}\"")]
        [InlineData("{\"Message\":null}", "{\"message\":null}")]
        [InlineData("{\"Message\":42}", "{\"message\":42}")]
        [InlineData("{\"Message\":\"\"}", "{\"message\":\"\"}")]
        [InlineData("{\"Message\":\"   \"}", "{\"message\":\"   \"}")]
        public void Malformed_requests_are_rejected_by_both_boundaries(string legacyJson, string json)
        {
            Assert.NotNull(Record.Exception(() => RuntimeChannelsLogic.PrepareRawRequest(legacyJson)));
            Assert.NotNull(Record.Exception(() => DomainValidation.Read<OpenPipeRequest>(json)));
        }

        [Theory]
        [InlineData("ReadTag", "{\"Tags\":[]}")]
        [InlineData("WriteTag", "{\"tags\":[{\"name\":\"A\",\"value\":{}}]}")]
        [InlineData("ReadAlarm", "{\"languageId\":1033}")]
        [InlineData("BrowseTags", "{\"extension\":true}")]
        public void Known_message_schemas_cannot_fall_back_to_expert_params(string message, string parameters)
        {
            Assert.Throws<JsonException>(() => DomainValidation.Read<OpenPipeRequest>(Envelope(message, parameters, null, false)));
        }

        [Theory]
        [InlineData("\"{}\"")]
        [InlineData("[]")]
        [InlineData("null")]
        [InlineData("42")]
        [InlineData("true")]
        public void Expert_params_require_an_object_without_decoding_strings(string parameters)
        {
            Assert.Throws<JsonException>(() => DomainValidation.Read<OpenPipeRequest>(Envelope("WriteExtension", parameters, null, false)));
        }

        [Theory]
        [InlineData("{\"nested\":{\"key\":1,\"key\":2}}")]
        [InlineData("{\"nested\":[1e999]}")]
        public void Expert_values_keep_duplicate_and_finite_number_guards(string parameters)
        {
            Assert.Throws<ArgumentException>(() => DomainValidation.Read<OpenPipeRequest>(Envelope("WriteExtension", parameters, null, false)));
        }

        [Theory]
        [InlineData(62, true)]
        [InlineData(63, false)]
        public void Expert_depth_matches_the_legacy_parser_limit(int arrays, bool accepted)
        {
            string parameters = "{\"Nested\":" + new string('[', arrays) + "null" + new string(']', arrays) + "}";
            // Build the envelope as text so an intermediate JsonNode parser cannot
            // reject the sample before either boundary sees it.
            string json = "{\"message\":\"WriteExtension\",\"params\":" + parameters + "}";
            string legacyJson = "{\"Message\":\"WriteExtension\",\"Params\":" + parameters + "}";
            Assert.Equal(accepted, Record.Exception(() => RuntimeChannelsLogic.PrepareRawRequest(legacyJson)) == null);
            Assert.Equal(accepted, Record.Exception(() => DomainValidation.Read<OpenPipeRequest>(json).ToWire("fixed")) == null);
        }

        [Theory]
        [InlineData("BrowseTags", "filter")]
        [InlineData("WriteExtension", "Payload")]
        public void Typed_and_expert_requests_share_the_utf8_size_budget(string message, string field)
        {
            string prefix = "{\"message\":\"" + message + "\",\"params\":{\"" + field + "\":\"";
            const string suffix = "\"},\"clientCookie\":\"fixed\"}";
            string json = prefix + new string('x', OpenPipeLimits.MaxBytes - prefix.Length - suffix.Length) + suffix;
            Assert.Equal(OpenPipeLimits.MaxBytes, Encoding.UTF8.GetByteCount(json));
            var request = DomainValidation.Read<OpenPipeRequest>(json);
            Assert.Equal(OpenPipeLimits.MaxBytes, Encoding.UTF8.GetByteCount(request.ToWire("fixed")));
            // One two-byte UTF-8 character replaces one ASCII byte; char count is unchanged.
            string oversized = json.Substring(0, prefix.Length) + "é" + json.Substring(prefix.Length + 1);
            Assert.Throws<JsonException>(() => DomainValidation.Read<OpenPipeRequest>(oversized));
            var generated = DomainValidation.Read<OpenPipeRequest>(Envelope(message, "{}", null, false));
            Assert.Throws<JsonException>(() => generated.ToWire(new string('c', OpenPipeLimits.MaxBytes)));
        }

        [Fact]
        public void Expert_schema_publishes_a_recursive_value_dictionary_and_disjoint_message_branch()
        {
            var schema = DomainSchemas.Get<OpenPipeRequest>();
            var expert = schema["oneOf"]!.AsArray().Last()!;
            Assert.Equal(4, expert["properties"]!["message"]!["not"]!["enum"]!.AsArray().Count);
            Assert.IsType<JsonObject>(expert["properties"]!["params"]!["additionalProperties"]);
            var valueSchema = schema["$defs"]![OpenPipeLimits.ValueDefinition]!;
            Assert.Equal("#/$defs/" + OpenPipeLimits.ValueDefinition, valueSchema["oneOf"]![1]!["items"]!["$ref"]!.GetValue<string>());
            Assert.Equal(OpenPipeLimits.MaxBytes, schema["x-maxUtf8Bytes"]!.GetValue<int>());
            Assert.Equal(OpenPipeLimits.MaxDepth, schema["x-maxDepth"]!.GetValue<int>());
        }

        private static string Envelope(string message, string? parameters, string? cookie, bool wire)
        {
            var request = new JsonObject { [wire ? "Message" : "message"] = message };
            if (parameters != null) request[wire ? "Params" : "params"] = JsonNode.Parse(parameters);
            if (cookie != null) request[wire ? "ClientCookie" : "clientCookie"] = cookie;
            return request.ToJsonString();
        }
    }
}
