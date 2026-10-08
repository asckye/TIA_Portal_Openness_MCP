using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public class VectorTests
{
    private static JsonArray Vectors(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", file)))!.AsArray();
    public static IEnumerable<object[]> JsonCases() => Enumerable.Range(0, Vectors("python-json.json").Count).Select(i => new object[] { i });
    public static IEnumerable<object[]> LexerCases() => Enumerable.Range(0, Vectors("lexer.json").Count).Select(i => new object[] { i });
    public static IEnumerable<object[]> McpCases() => Enumerable.Range(0, Vectors("mcp-results.json").Count).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(JsonCases))]
    public void PythonJsonMatchesUtf8Bytes(int index)
    {
        var row = Vectors("python-json.json")[index]!;
        var text = PythonJson.Dumps(row["value"], row["ensureAscii"]!.GetValue<bool>(), row["indent"]?.GetValue<int>(), row["compactSeparators"]!.GetValue<bool>(), row["sortKeys"]!.GetValue<bool>());
        Assert.Equal(Encoding.UTF8.GetBytes(row["expected"]!.GetValue<string>()), Encoding.UTF8.GetBytes(text));
    }

    [Theory]
    [MemberData(nameof(LexerCases))]
    public void LexerAndMatchingPairsMatchPython(int index)
    {
        var row = Vectors("lexer.json")[index]!;
        var lexer = new CSharpLexer(row["source"]!.GetValue<string>());
        if (row["error"] is not null)
        {
            Assert.Equal(row["error"]!.GetValue<string>(), Assert.Throws<ArgumentException>(() => lexer.Scan()).Message);
            return;
        }
        var (tokens, comments) = lexer.Scan();
        Assert.True(JsonNode.DeepEquals(row["tokens"], Tokens(tokens)), "token sequence differs from Python");
        Assert.True(JsonNode.DeepEquals(row["comments"], Tokens(comments)), "comment sequence differs from Python");
        if (row["pairsError"] is not null)
            Assert.Equal(row["pairsError"]!.GetValue<string>(), Assert.Throws<ArgumentException>(() => MatchingPairs.Find(tokens)).Message);
        else
        {
            var pairs = new JsonObject();
            foreach (var (start, end) in MatchingPairs.Find(tokens)) pairs[start.ToString()] = end;
            Assert.True(JsonNode.DeepEquals(row["pairs"], pairs));
        }
    }

    private static JsonArray Tokens(IReadOnlyList<Token> tokens) => new(tokens.Select(t => (JsonNode?)new JsonObject
    {
        ["kind"] = t.Kind, ["start"] = t.Start, ["end"] = t.End, ["value"] = t.Value, ["expressions"] = Tokens(t.Expressions)
    }).ToArray());

    [Theory]
    [MemberData(nameof(McpCases))]
    public void McpResultsMatchPython(int index)
    {
        var row = Vectors("mcp-results.json")[index]!;
        foreach (var (name, decode) in new (string, Func<JsonNode?, JsonObject>)[] { ("envelope", McpResults.Envelope), ("successful", McpResults.Successful) })
        {
            if (index == 0)
            {
                foreach (var content in new JsonNode?[] { null, JsonValue.Create(1), JsonNode.Parse("[null]"), JsonNode.Parse("[1]") })
                    Assert.Throws<ArgumentException>(() => decode(new JsonObject { ["structuredContent"] = row["reply"]!.DeepClone(), ["content"] = content }));
                foreach (var content in new JsonNode[] { new JsonObject(), JsonValue.Create("")! })
                    Assert.True(JsonNode.DeepEquals(row[name], decode(new JsonObject { ["structuredContent"] = row["reply"]!.DeepClone(), ["content"] = content })));
            }
            if (row[name + "Error"] is not null)
                Assert.Equal(row[name + "Error"]!.GetValue<string>(), Assert.Throws<ArgumentException>(() => decode(row["reply"])).Message);
            else
            {
                Assert.True(JsonNode.DeepEquals(row[name], decode(row["reply"])));
                // JSON DOMs created by callers must behave like parsed JSON, including numeric truthiness.
                if (row["reply"] is JsonObject reply && reply["schemaVersion"] is not null)
                {
                    var typed = reply.DeepClone().AsObject();
                    typed["schemaVersion"] = JsonValue.Create(4.0f);
                    Assert.True(JsonNode.DeepEquals(row[name], decode(typed)));
                }
                if (row["reply"] is JsonObject result && result["isError"]?.ToJsonString() == "0")
                {
                    var typed = result.DeepClone().AsObject();
                    typed["isError"] = JsonValue.Create(0);
                    Assert.True(JsonNode.DeepEquals(row[name], decode(typed)));
                }
            }
        }
    }

    [Fact]
    public void PythonJsonHandlesNonfiniteNumbersAndSurrogates()
    {
        Assert.Equal("[NaN, Infinity, -Infinity, -0.0, 1.0]", PythonJson.Dumps(new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.0, 1.0 }));
        Assert.Equal("\"\\ud800\"", PythonJson.Dumps("\ud800"));
        Assert.Equal("\"\ud800\"", PythonJson.Dumps("\ud800", ensureAscii: false));
    }

    [Fact]
    public void OfflineFixturesInheritTheWorktreeAndCleanUp()
    {
        var fixture = new OfflineFixtures();
        Assert.Equal(Path.Combine(Repository.FindRoot(), "bin-build"), Path.GetDirectoryName(fixture.DirectoryPath));
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "owned.txt"), "owned");
        fixture.Dispose();
        Assert.False(Directory.Exists(fixture.DirectoryPath));
        Assert.Throws<ArgumentException>(() => new OfflineFixtures("../escape-"));
    }

    [Fact]
    public void RepositoryLookupAndGitFilesUseTheCurrentWorktree()
    {
        var root = Repository.FindRoot();
        Assert.Equal(root, Repository.FindRoot(Path.Combine(root, "tests")));
        Assert.Contains("AGENTS.md", Repository.GitFiles(root, "AGENTS.md"));
    }
}
