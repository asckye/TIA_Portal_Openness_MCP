using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Runtime;
using TiaMcpServer.Siemens;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class V4InputEquivalenceTests
    {
        private static readonly Dictionary<string, AttributeRule> Attributes = new Dictionary<string, AttributeRule> {
            ["Name"] = new AttributeRule(InputSchema.Scalar()), ["Enabled"] = new AttributeRule(InputSchema.Scalar()),
            ["Count"] = new AttributeRule(InputSchema.Scalar()) };
        private static PropertyPathPolicy PathPolicy() => new PropertyPathPolicy(new Dictionary<string, PropertyAdmission> {
            ["TagTables"] = new PropertyAdmission(true, next: new PropertyPathPolicy(new Dictionary<string, PropertyAdmission> {
                ["Tags"] = new PropertyAdmission(true) })), ["Value"] = new PropertyAdmission(true) });
        private static ToolTarget Target() => new ToolTarget("GetSessionState", InputSchema.Object(new Dictionary<string, InputSchema>()),
            new InputBudget(), read: true, transaction: true);

        // Each row executes the cited old parser, not a second copy of its algorithm.
        // JSON aliases/casing/map writes are converted to section 2's V4 wire shape.
        public static IEnumerable<object[]> Samples()
        {
            foreach (string value in new[] { "[]", "[\"PLC_1\"]", "[\"Group\",\"Station/A,B\"]", "[\" A \"]", "[\"\"]", "[null]", "[3]", "{}", "null",
                V4Json.Serialize(Enumerable.Repeat("G", 64).ToArray()), V4Json.Serialize(Enumerable.Repeat("G", 65).ToArray()) })
                yield return new object[] { "P", value, "src/Engine/Siemens/ProjectSecurityLogic.cs:55-64" };
            foreach (string value in new[] { "[]", "[\"A\",\"B\"]", "[\"A\",\"a\"]", "[\" A \"]", "[\"A\",\"A\"]", "[\"\"]", "[null]", "[3]", "{}",
                V4Json.Serialize(Enumerable.Range(0, 64).Select(i => i.ToString()).ToArray()), V4Json.Serialize(Enumerable.Range(0, 65).Select(i => i.ToString()).ToArray()) })
                yield return new object[] { "S", value, "src/Logic/Siemens/HardwareNetworkLogic.cs:51-60" };
            foreach (string value in new[] { "[]", "[0]", "[65535]", "[1000,1000]", "[-1]", "[65536]", "[2147483648]", "[1.5]", "[1.0]", "[1e2]", "[\"1\"]", "[null]",
                V4Json.Serialize(new int[200]), V4Json.Serialize(new int[201]) })
                yield return new object[] { "N", value, "src/Engine/Siemens/StartdriveLogic.cs:107-120 (arrayIndex=-1)" };
            foreach (string value in new[] { "[]", "[{\"property\":\"Value\"}]", "[{\"property\":\"TagTables\",\"name\":\"Table\"},{\"property\":\"Tags\",\"name\":\"Tag\"}]",
                "[{\"property\":\"Parent\"}]", "[{\"property\":\"parent\"}]", "[{\"property\":\"GetType()\"}]", "[{\"property\":\"Value\",\"name\":\"\"}]",
                "[{\"Property\":\"Value\"}]", "[{\"property\":\"Value\",\"index\":0}]", "[null]", "{}" })
                yield return new object[] { "R", value, "src/Engine/Siemens/EngineeringObjectAddress.cs:11-27" };
            foreach (string value in new[] { "{}", "{\"Name\":\"PLC\"}", "{\"Enabled\":true,\"Count\":2}", "{\"Name\":null}", "{\"Name\":[]}", "{\"Name\":{}}", "{\"Other\":1}", "{\"name\":1}", "[]", "null" })
                yield return new object[] { "M", value, "src/Logic/Siemens/ArgumentRules.cs:38-42,53-87" };
            foreach (string value in new[] { "{\"en-US\":\"Motor\"}", "{\"zh-CN\":\"\"}", "{\"en-US\":\"A\",\"zh-CN\":\"B\"}", "{}", "{\" \":\"x\"}", "{\"12345678901234567\":\"x\"}", "{\"en-US\":1}", "{\"en-US\":null}", "{\"en-US\":[]}", "[]" })
                yield return new object[] { "L", value, "src/Logic/Siemens/PlcTagEditingLogic.cs:48-66" };
            foreach (string value in new[] { "true", "42.5", "\"text\"", "{\"bicoSource\":\"r19\"}", "{\"bicoSource\":\"P2050[0].6\"}", "null", "[]", "{}", "{\"bicoSource\":\"bad\"}", "{\"bicoSource\":\"r19\",\"other\":0}", "{\"BicoSource\":\"r19\"}", "{\"bicoSource\":null}" })
                yield return new object[] { "V", value, "src/Engine/Siemens/StartdriveLogic.cs:131-158" };
            foreach (string value in new[] { "[{\"name\":\"GetSessionState\",\"arguments\":{}}]", "[]", "{}", "[null]", "[{\"name\":\"CallTool\",\"arguments\":{}}]", "[{\"name\":\"RunToolsInTransaction\",\"arguments\":{}}]", "[{\"name\":\"GetSessionState\",\"arguments\":[]}]", "[{\"name\":\"bad name\",\"arguments\":{}}]",
                "[" + string.Join(",", Enumerable.Repeat("{\"name\":\"GetSessionState\",\"arguments\":{}}", 20)) + "]",
                "[" + string.Join(",", Enumerable.Repeat("{\"name\":\"GetSessionState\",\"arguments\":{}}", 21)) + "]" })
                yield return new object[] { "C", value, "src/Engine/Siemens/ToolTransactionRules.cs:12-28" };
            foreach (string value in new[] { "[{\"name\":\"Ready\",\"value\":true}]", "[{\"name\":\" Speed \",\"value\":42.5}]", "[{\"name\":\"Text\",\"value\":\"ok\"}]", "[]", "[null]", "[{\"name\":\"\",\"value\":1}]", "[{\"name\":\"A\"}]", "[{\"name\":\"A\",\"value\":null}]", "[{\"name\":\"A\",\"value\":[]}]", "[{\"name\":\"A\",\"value\":{}}]", "[{\"name\":\"A\",\"value\":1},{\"name\":\" A \",\"value\":2}]" })
                yield return new object[] { "W", value, "src/Logic/Runtime/RuntimeChannelsLogic.cs:111-160" };
        }

        [Theory]
        [MemberData(nameof(Samples))]
        public void MatchesLegacyDecisionAndNormalizedValue(string family, string input, string source)
        {
            Assert.Contains(".cs:", source);
            string? oldValue = null;
            var error = Record.Exception(() => oldValue = Legacy(family, input));
            var current = Current(family, input);
            Assert.Equal(error == null, current.Accepted);
            if (error == null) Assert.Equal(oldValue, current.Json);
        }

        private static string Legacy(string family, string input)
        {
            switch (family)
            {
                case "P":
                    ProjectSecurityLogic.ValidateDevicePath(input);
                    return V4Json.Serialize(V4Json.Deserialize<string[]>(input));
                case "S": return V4Json.Serialize(HardwareNetworkLogic.ParseNames(input, "names"));
                case "N":
                    var numbers = JsonNode.Parse(input)!.AsArray();
                    var old = new JsonArray(numbers.Select(n => (JsonNode)new JsonObject { ["number"] = n?.DeepClone() }).ToArray());
                    var selector = StartdriveLogic.ParseParameterSelector("[]", old.ToJsonString());
                    return V4Json.Serialize(selector.Numbers.Select(n => n.Number).ToArray());
                case "R": return V4Json.Serialize(V4Json.Deserialize<JsonElement>(EngineeringObjectAddress.Parse(input).ToJsonString()));
                case "M":
                    var attributes = HardwareNetworkLogic.ParseObject(input, "properties");
                    ArgumentRules.RequireKeys(attributes, Attributes.Keys.ToArray(), "properties");
                    return V4Json.Serialize(V4Json.Deserialize<JsonElement>(attributes.ToJsonString()));
                case "L":
                    var comments = PlcTagEditingLogic.ParseComment(JsonNode.Parse(input));
                    return V4Json.Serialize(comments.ToDictionary(p => p.Culture!, p => p.Text));
                case "V":
                    var value = StartdriveLogic.ParseParameterValue(input);
                    return V4Json.Serialize(V4Json.Deserialize<JsonElement>(value.IsBico ? new JsonObject { ["bicoSource"] = value.BicoSource }.ToJsonString() : value.Scalar!.ToJsonString()));
                case "C": return V4Json.Serialize(ToolTransactionRules.ParseToolCalls(input).Select(c => new {
                    name = c.Name, arguments = V4Json.Deserialize<JsonElement>(c.ArgumentsJson) }).ToArray());
                case "W": return V4Json.Serialize(RuntimeChannelsLogic.ParseWriteList(input, "writes").Select(w => new {
                    name = w.Name, value = V4Json.Deserialize<JsonElement>(w.Value!.ToJsonString()) }).ToArray());
                default: throw new ArgumentException(family);
            }
        }

        private static (bool Accepted, string? Json) Result<T>(InputResult<T> result) =>
            (result.IsValid, result.IsValid ? V4Json.Serialize(result.Value) : null);
        private static (bool Accepted, string? Json) Current(string family, string input) => family switch
        {
            "P" => Result(PathValidator.Device().Read(input, "path")),
            "S" => Result(NameListValidator.Hardware().Read(input, "names")),
            "N" => Result(NumberListValidator.Drive().Read(input, "numbers")),
            "R" => Result(PropertyPathValidator.Create(PathPolicy()).Read(input, "path")),
            "M" => Result(AttributeMapValidator.Hardware(Attributes).Read(input, "properties")),
            "L" => Result(TextMapValidator.Comments().Read(input, "comments")),
            "V" => Result(NativeValueValidator.Create(NativeValueValidator.DriveParameter()).Read(input, "value")),
            "C" => Result(ToolCallValidator.Create(ToolCallMode.Transaction, new[] { Target() }).Read(input, "calls")),
            "W" => Result(WriteValueValidator.Runtime().Read(input, "writes")),
            _ => throw new ArgumentException(family)
        };

        [Theory]
        [InlineData("P", "[\"PLC_1\"]")]
        [InlineData("S", "[\".scl\"]")]
        [InlineData("N", "[1000]")]
        [InlineData("R", "[{\"property\":\"TagTables\",\"name\":\"Table\"}]")]
        [InlineData("M", "{\"Enabled\":true}")]
        [InlineData("L", "{\"en-US\":\"Motor\"}")]
        [InlineData("V", "42.5")]
        [InlineData("C", "[{\"name\":\"GetSessionState\",\"arguments\":{}}]")]
        [InlineData("W", "[{\"name\":\"Ready\",\"value\":true}]")]
        public void FamilyGoldenBytes(string family, string golden)
        {
            var result = Current(family, golden);
            Assert.True(result.Accepted);
            Assert.Equal(Encoding.UTF8.GetBytes(golden), Encoding.UTF8.GetBytes(result.Json!));
        }

        [Fact]
        public void LegacyAliasesAreConvertedBeforeComparison()
        {
            // RuntimeChannelsLogic.cs:126-138 and PlcSimAdvancedLogic.cs:126-142.
            Assert.Equal(Legacy("W", "{\"Ready\":true}"), Current("W", "[{\"name\":\"Ready\",\"value\":true}]").Json);
            Assert.Equal(Legacy("W", "[{\"Name\":\"Ready\",\"Value\":true}]"), Current("W", "[{\"name\":\"Ready\",\"value\":true}]").Json);
            Assert.Equal("{\"OverwriteHmiData\":\"true\"}", V4Json.Serialize(DownloadPromptPolicy.ParseExplicitAnswers("{\"OverwriteHmiData\":true}")));
            Assert.True(TextMapValidator.PromptAnswers().Read("{\"OverwriteHmiData\":\"true\"}", "answers").IsValid);
        }
    }
}
