using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class HmiTagPathSchemaTests
    {
        [Fact]
        public void Both_usage_profiles_publish_a_valid_literal_table_path()
        {
            string root = Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
            var calls = JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(root, "reference/tool-examples/calls.json")))!;
            string pattern = ToolMetadata.Parameter("DeleteHmiTag", "tagTablePath")!.Pattern!;
            foreach (string profile in new[] { "plc-foundation", "full-engine" })
            {
                string path = (string)calls["profiles"]![profile]!["DeleteHmiTag"]!["arguments"]!["tagTablePath"]!;
                Assert.Matches(pattern, path);
                TiaMcp.Adapters.Native.Hmi.HmiTagDeletion.Validate(path, "Tag", true, false);
            }
        }

        [Fact]
        public void Path_hint_survives_descriptor_derivation_without_a_prose_enum()
        {
            var method = typeof(HmiTagDeletionTools).GetMethod("DeleteHmiTag")!;
            var spec = DeclaredToolMetadata.SpecsOf(method).Single(s => s.Name == "tagTablePath");
            Assert.Empty(spec.AllowedValues);
            Assert.NotNull(spec.Pattern);
            var schema = new JsonObject { ["properties"] = new JsonObject { ["tagTablePath"] = new JsonObject { ["type"] = "string" } } };
            Assert.Equal(1, SchemaHintsLogic.Augment(schema, new[] { spec }, null).Patterns);
            Assert.False(SchemaHintsLogic.Augment(schema, new[] { spec }, null).Changed);
            Assert.Null(schema["properties"]!["tagTablePath"]!["enum"]);
            Assert.Equal(spec.Pattern, (string?)schema["properties"]!["tagTablePath"]!["pattern"]);
            Assert.Equal("\"/Table\"", ToolMetadata.Parameter("DeleteHmiTag", "tagTablePath")!.ExampleJson);
            var prose = new PreflightLogic.ParameterSpec("path", "string", true, null, "exact /Group/Sub/Table, or empty");
            Assert.Equal(0, SchemaHintsLogic.Augment(new JsonObject { ["properties"] = new JsonObject { ["path"] = new JsonObject() } }, new[] { prose }, null).Enums);
        }

        [Theory]
        [InlineData("Table")]
        [InlineData("/")]
        [InlineData("/Group//Table")]
        [InlineData("/Group/Table/")]
        [InlineData("/./Table")]
        [InlineData("/Group/..")]
        [InlineData("/ ")]
        [InlineData("/\u0085")]
        [InlineData("/Ta*ble")]
        [InlineData("/Ta?ble")]
        [InlineData("/Group\\Table")]
        [InlineData("/Table\n")]
        [InlineData("/\u007f")]
        public void Malformed_paths_keep_the_existing_typed_refusal(string path)
        {
            Assert.DoesNotMatch(ToolMetadata.Parameter("DeleteHmiTag", "tagTablePath")!.Pattern!, path);
            var session = new FakeHmiToolSession { FixtureRoot = new object() };
            var result = HmiExchangeContract.Run("DeleteHmiTag", false, false, () => session.RunHmiStepTool("DeleteHmiTag", meta => {
                TiaMcp.Adapters.Native.Hmi.HmiTagDeletion.Validate(path, "Tag", true, false);
                return "Unexpectedly accepted malformed path.";
            }));
            Assert.Equal("PRECONDITION_FAILED", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
            Assert.Equal(0, session.FixtureResolveCalls);
        }

        [Fact]
        public void Schema_preserves_the_existing_64_segment_limit_and_literal_names()
        {
            string pattern = ToolMetadata.Parameter("DeleteHmiTag", "tagTablePath")!.Pattern!;
            foreach (string path in new[] { "", "/Table", "/Group/Sub/Table", "/分组/变量表", "/ .literal ", string.Concat(Enumerable.Repeat("/A", 64)) })
            {
                Assert.Matches(pattern, path);
                TiaMcp.Adapters.Native.Hmi.HmiTagDeletion.Validate(path, "Tag", true, false);
            }
            string excessive = string.Concat(Enumerable.Repeat("/A", 65));
            Assert.DoesNotMatch(pattern, excessive);
            Assert.Throws<ArgumentException>(() => TiaMcp.Adapters.Native.Hmi.HmiTagDeletion.Validate(excessive, "Tag", true, false));
        }
    }
}
