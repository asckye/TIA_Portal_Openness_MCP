using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class V4DomainInputContractTests
    {
        [Theory]
        [InlineData("{\"id\":\"secret\",\"extra\":0}")]
        [InlineData("{\"id\":\"secret\",\"id\":\"other\"}")]
        [InlineData("{\"Id\":\"secret\"}")]
        [InlineData("\"secret\"")]
        [InlineData("{\"id\":\"secret\",\"priority\":1e999}")]
        [InlineData("{\"id\":\"secret\",\"priority\":1.0}")]
        [InlineData("{\"id\":\"secret\",\"dependencies\":null}")]
        [InlineData("{\"id\":\"secret")]
        public void Domain_errors_use_shared_invalid_argument_details_without_input_values(string json)
        {
            var actual = DomainValidation.Contract<Artifact>().Read(json, "artifacts");
            var shared = new InputContract<Artifact>(DomainSchemas.For(typeof(Artifact)), new InputBudget()).Read(json, "artifacts");
            Assert.False(actual.IsValid);
            Assert.Equal(V4Json.Serialize(shared.Error), V4Json.Serialize(actual.Error));
            Assert.Equal(ErrorCode.InvalidArgument, actual.Error!.Code);
            Assert.Equal("artifacts", Assert.IsType<InvalidArgumentDetails>(actual.Error.Details).Parameter);
            Assert.DoesNotContain("secret", V4Json.Serialize(actual.Error));
        }

        [Fact]
        public void Presence_keeps_omitted_null_and_empty_rules_distinct()
        {
            var contract = DomainValidation.Contract<LintRules>();
            var missing = contract.Read((string?)null, "rules", optional: true);
            Assert.True(missing.IsValid); Assert.Equal(InputPresence.Missing, missing.Presence); Assert.Null(missing.Value);
            Assert.Equal(ErrorCode.InvalidArgument, contract.Read((string?)null, "rules").Error!.Code);
            var explicitNull = contract.Read("null", "rules", optional: true);
            Assert.Equal(InputPresence.Null, explicitNull.Presence); Assert.Equal(ErrorCode.InvalidArgument, explicitNull.Error!.Code);
            var empty = contract.Read("{}", "rules");
            Assert.True(empty.IsValid); Assert.Equal(InputPresence.Value, empty.Presence); Assert.Equal(120, empty.Value!.MaxLineLength);
            var references = DomainValidation.Contract<Dictionary<string, SivarcReference?>>().Read("{\"LibraryScreen\":null}", "references");
            Assert.True(references.IsValid); Assert.Null(references.Value!["LibraryScreen"]);
        }

        [Theory]
        [InlineData("[{\"id\":\"A\",\"dependencies\":[\"missing\"]}]")]
        [InlineData("[{\"id\":\"A\",\"dependencies\":[\"B\"]},{\"id\":\"B\",\"dependencies\":[\"A\"]}]")]
        [InlineData("[{\"id\":\"A\"},{\"id\":\"a\"}]")]
        public void Family_business_failures_return_the_shared_invalid_argument(string json)
        {
            var contract = DomainValidation.Contract<Artifact[]>();
            var read = contract.Read(json, "artifacts");
            var typed = V4Json.Deserialize<Artifact[]>(json);
            var validated = contract.Validate(typed, "artifacts");
            Assert.Equal(ErrorCode.InvalidArgument, read.Error!.Code);
            Assert.Equal(V4Json.Serialize(read.Error), V4Json.Serialize(validated.Error));
        }

        [Theory]
        [InlineData(64)]
        [InlineData(65)]
        [InlineData(20000)]
        public void Deep_text_reports_the_same_shared_depth_limit(int depth)
        {
            string json = "{\"message\":\"WriteExtension\",\"params\":{\"Nested\":" + new string('[', depth) + "0" + new string(']', depth) + "}}";
            var contract = DomainValidation.Contract<OpenPipeRequest>();
            var result = contract.Read(json, "request");
            var shared = new InputContract<NativeValue>(InputSchema.Scalar(), new InputBudget()).Read(json, "request");
            Assert.Equal(V4Json.Serialize(shared.Error), V4Json.Serialize(result.Error));
            var error = Assert.IsType<LimitExceededDetails>(result.Error!.Details);
            Assert.Equal(64L, error.Limit); Assert.Equal(65L, error.Actual);
        }

        [Fact]
        public void External_elements_are_bounded_before_recursive_domain_validation()
        {
            using var document = JsonDocument.Parse(new string('[', 1000) + "0" + new string(']', 1000), new JsonDocumentOptions { MaxDepth = 1001 });
            var result = DomainValidation.Contract<Artifact[]>().Read(document.RootElement, "artifacts");
            Assert.Equal(ErrorCode.LimitExceeded, result.Error!.Code);
            var details = Assert.IsType<LimitExceededDetails>(result.Error.Details);
            Assert.Equal(64L, details.Limit); Assert.Equal(65L, details.Actual);
        }

        [Fact]
        public void Raw_budget_precedes_parsing_and_normalized_budget_precedes_business_rules()
        {
            var contract = DomainValidation.Contract<MotionTarget>();
            var oversized = contract.Read(new string(' ', 16385) + "{secret", "target");
            var raw = Assert.IsType<LimitExceededDetails>(oversized.Error!.Details);
            Assert.Equal(16384L, raw.Limit); Assert.Equal(16392L, raw.Actual);
            string json = "{\"plcTagPath\":\"" + new string('é', 3000) + "\"}";
            Assert.True(json.Length < 16384);
            var normalized = contract.Read(json, "target");
            var details = Assert.IsType<LimitExceededDetails>(normalized.Error!.Details);
            Assert.Equal(16384L, details.Limit); Assert.True(details.Actual > details.Limit);
        }

        [Fact]
        public void Collection_limits_retain_exact_actual_and_limit_details()
        {
            string json = V4Json.Serialize(Enumerable.Range(0, 257).Select(i => new { id = i.ToString() }).ToArray());
            var result = DomainValidation.Contract<Artifact[]>().Read(json, "artifacts");
            var details = Assert.IsType<LimitExceededDetails>(result.Error!.Details);
            Assert.Equal("artifacts", details.Parameter); Assert.Equal(256L, details.Limit); Assert.Equal(257L, details.Actual);
        }

        [Fact]
        public void Action_context_uses_the_same_contract_for_parsed_and_typed_callers()
        {
            var contract = DomainValidation.Contract<MotionTarget>(target => DomainValidation.Motion(target, "sensor", "connect", "21", true, false));
            const string json = "{\"inputBitAddress\":0,\"outputBitAddress\":8}";
            var read = contract.Read(json, "target");
            var validate = contract.Validate(V4Json.Deserialize<MotionTarget>(json), "target");
            Assert.Equal(ErrorCode.InvalidArgument, read.Error!.Code);
            Assert.Equal(V4Json.Serialize(read.Error), V4Json.Serialize(validate.Error));
        }

        [Theory]
        [InlineData("SubscribeTag")]
        [InlineData("UnsubscribeAlarm")]
        public void Shared_pre_schema_hook_retains_streaming_refusal_priority(string message)
        {
            string json = "{\"message\":\"" + message + "\",\"params\":null}";
            Assert.Throws<NotSupportedException>(() => DomainValidation.Contract<OpenPipeRequest>().Read(json, "request"));
        }

        [Fact]
        public void Shared_byte_budget_checks_raw_and_normalized_values()
        {
            var contract = new InputContract<Scalar>(InputSchema.Scalar(), new InputBudget(utf8Bytes: 8));
            Assert.True(contract.Read("\"é\"", "value").IsValid);
            var normalized = Assert.IsType<LimitExceededDetails>(contract.Read("\"éé\"", "value").Error!.Details);
            Assert.Equal(8L, normalized.Limit); Assert.Equal(14L, normalized.Actual);
            var raw = Assert.IsType<LimitExceededDetails>(contract.Read("\"éééé\"", "value").Error!.Details);
            Assert.Equal(10L, raw.Actual);
        }

        [Theory]
        [InlineData("{\"$ref\":\"https://example.invalid/schema\"}")]
        [InlineData("{\"$ref\":\"#\"}")]
        [InlineData("{\"$ref\":\"#/$defs/missing\",\"$defs\":{}}")]
        [InlineData("{\"$ref\":\"#/$defs/a/b\",\"$defs\":{\"a/b\":true}}")]
        [InlineData("{\"x-maxDepth\":65}")]
        [InlineData("{\"x-maxUtf8Bytes\":-1}")]
        public void Unsupported_references_and_invalid_budget_annotations_are_rejected(string json) =>
            Assert.Throws<ArgumentException>(() => new InputSchema(V4Json.ParseInput(json)));

        [Fact]
        public void Local_reference_cycles_that_consume_no_input_are_rejected_at_construction()
            => Assert.Throws<ArgumentException>(() =>
                new InputSchema(V4Json.ParseInput("{\"$ref\":\"#/$defs/loop\",\"$defs\":{\"loop\":{\"$ref\":\"#/$defs/loop\"}}}")));

        [Theory]
        [InlineData("{\"x-maxDepth\":1}", "[[0]]", 1, 2)]
        [InlineData("{\"x-maxUtf8Bytes\":3}", "\"ab\"", 3, 4)]
        public void Schema_budget_annotations_are_enforced(string schemaJson, string json, long limit, long actual)
        {
            var schema = new InputSchema(V4Json.ParseInput(schemaJson));
            var result = new InputContract<NativeValue>(schema, new InputBudget()).Read(json, "value");
            var details = Assert.IsType<LimitExceededDetails>(result.Error!.Details);
            Assert.Equal(limit, details.Limit); Assert.Equal(actual, details.Actual);
        }

        [Theory]
        [InlineData("{\"scalar\":null,\"items\":[{},true,1,\"text\"]}", true)]
        [InlineData("{\"duplicate\":1,\"duplicate\":2}", false)]
        [InlineData("{\"number\":1e999}", false)]
        public void Native_policy_and_published_schema_share_the_same_decision(string parameters, bool accepted)
        {
            string json = "{\"message\":\"WriteExtension\",\"params\":" + parameters + "}";
            var contract = DomainValidation.Contract<OpenPipeRequest>();
            var schema = new InputSchema(V4Json.ParseInput(DomainSchemas.Get<OpenPipeRequest>().ToJsonString()));
            Assert.Equal(accepted, contract.Read(json, "request").IsValid);
            Assert.Equal(accepted, schema.Validate(V4Json.ParseInput(json), "request") == null);
        }
    }
}
