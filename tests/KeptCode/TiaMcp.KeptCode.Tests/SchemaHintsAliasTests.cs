using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class SchemaHintsAliasTests
    {
        public static IEnumerable<object[]> AliasExamples() =>
            from tool in new[] { "AnalyzePlcReferences", "ImportPlcTagTablesFromDirectory", "WritePlcSclSourceFile",
                "GenerateAcceptanceReport", "SaveExportContent", "ManageMachineDescription" }
            from alias in CanonicalToolParameters.Aliases(tool)
            from canonicalArgument in new[] { false, true }
            from canonicalSchemaFirst in new[] { false, true }
            select new object[] { tool, alias.Legacy, alias.Canonical, canonicalArgument, canonicalSchemaFirst };

        [Theory, MemberData(nameof(AliasExamples))]
        public void Either_argument_name_keeps_both_schema_examples(string tool, string legacy, string canonical,
            bool canonicalArgument, bool canonicalSchemaFirst)
        {
            const string path = @"C:\Examples\exports";
            var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
                [legacy] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray() };
            JsonObject CanonicalSchema(JsonObject input) => JsonNode.Parse(CanonicalToolParameters.Schema(tool,
                JsonSerializer.SerializeToElement(input)).GetRawText())!.AsObject();
            if (canonicalSchemaFirst) schema = CanonicalSchema(schema);
            var example = new JsonObject { [canonicalArgument ? canonical : legacy] = path };
            var spec = new PreflightLogic.ParameterSpec(legacy, "string", false, null, "");
            SchemaHintsLogic.Augment(schema, new[] { spec }, example, tool);
            if (!canonicalSchemaFirst) schema = CanonicalSchema(schema);
            Assert.Equal(path, (string?)schema["properties"]![legacy]!["examples"]?[0]);
            Assert.Equal(path, (string?)schema["properties"]![canonical]!["examples"]?[0]);
            Assert.Single(example);
            Assert.Equal(path, (string?)example[canonicalArgument ? canonical : legacy]);
        }

        [Fact]
        public void Existing_examples_are_preserved()
        {
            var schema = new JsonObject { ["properties"] = new JsonObject {
                ["directory"] = new JsonObject { ["examples"] = new JsonArray("legacy sample") },
                ["sourceDirectory"] = new JsonObject { ["examples"] = new JsonArray("canonical sample") } } };
            var spec = new PreflightLogic.ParameterSpec("directory", "string", false, null, "");
            var result = SchemaHintsLogic.Augment(schema, new[] { spec },
                new JsonObject { ["sourceDirectory"] = @"C:\Examples\exports" }, "AnalyzePlcReferences");
            Assert.Equal(0, result.Examples);
            Assert.Equal("legacy sample", (string?)schema["properties"]!["directory"]!["examples"]![0]);
            Assert.Equal("canonical sample", (string?)schema["properties"]!["sourceDirectory"]!["examples"]![0]);
        }

        [Fact]
        public void Unknown_tool_names_do_not_map_aliases()
        {
            var schema = new JsonObject { ["properties"] = new JsonObject { ["directory"] = new JsonObject() } };
            var spec = new PreflightLogic.ParameterSpec("directory", "string", false, null, "");
            var result = SchemaHintsLogic.Augment(schema, new[] { spec },
                new JsonObject { ["sourceDirectory"] = @"C:\Examples\exports" }, "UnrelatedTool");
            Assert.Equal(0, result.Examples);
            Assert.Null(schema["properties"]!["directory"]!["examples"]);
        }

        [Fact]
        public void Schema_properties_are_not_created()
        {
            var schema = new JsonObject { ["properties"] = new JsonObject { ["other"] = new JsonObject() } };
            var spec = new PreflightLogic.ParameterSpec("directory", "string", false, null, "");
            var result = SchemaHintsLogic.Augment(schema, new[] { spec },
                new JsonObject { ["sourceDirectory"] = @"C:\Examples\exports" }, "AnalyzePlcReferences");
            Assert.Equal(0, result.Examples);
            Assert.Single(schema["properties"]!.AsObject());
            Assert.True(schema["properties"]!.AsObject().ContainsKey("other"));
        }
    }
}
