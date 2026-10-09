using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

[Trait("Category", "StaticChecks")]
public sealed class StaticCheckVectorTests
{
    internal static string Root => Repository.FindRoot(AppContext.BaseDirectory);
    public static IEnumerable<object[]> Vectors()
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Vectors"), "*.json").Order(StringComparer.Ordinal))
        {
            var kind = Path.GetFileNameWithoutExtension(path);
            var rows = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
            for (var i = 0; i < rows.Count; i++) yield return [kind, i, rows[i]!.ToJsonString()];
        }
    }
    [Theory]
    [MemberData(nameof(Vectors))]
    public void PythonSelfTestVectors(string kind, int index, string json)
    {
        var row = JsonNode.Parse(json)!;
        var args = row["args"]!.AsArray();
        var kw = row["kwargs"]!.AsObject();
        string Str(int i) => args[i]!.GetValue<string>();
        JsonNode actual;
        switch (kind)
        {
            case "swallowed":
                var catches = SwallowedExceptions.ScanSource(Str(0), Str(1), Str(2));
                actual = SourceCheck.Json(new object[] { catches.Rows, catches.Errors }); break;
            case "comments": actual = SourceCheck.Json(CommentHygiene.ScanSource(Str(0), Str(1), Str(2))); break;
            case "mcp-text": actual = SourceCheck.Json(McpText.ScanSource(Str(0), Str(1), Str(2))); break;
            case "envelopes":
                var counts = ResponseEnvelopes.ScanTokens(new CSharpLexer(Str(0)).Scan().Tokens);
                actual = SourceCheck.Json(new object[] { counts.Counts, counts.Handwritten }); break;
            case "envelope-rewrite":
                var rewrite = EnvelopeRewrite.Compare(Str(0), Str(1));
                actual = SourceCheck.Json(new object?[] { rewrite.Counts, rewrite.Offset, rewrite.Nearest }); break;
            case "dead-references":
                var function = row["function"]!.GetValue<string>();
                if (function == "rewrite_guidance")
                {
                    var fix = DeadToolReferences.RewriteGuidance(Str(0), args[1]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()), args[2]!.AsArray().Select(p => p!.GetValue<string>()).ToHashSet());
                    actual = SourceCheck.Json(new object[] { fix.Source, fix.Events.Select(e => new object[] { e.Start, e.Old, e.New, e.Changed }).ToArray() });
                }
                else if (function == "scan")
                {
                    var scan = DeadToolReferences.Scan(args[0]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()), DeadToolReferences.ContractNames(Root, "manifest/history/contracts-v3/baseline"), kw["extra_text"]?.GetValue<string>());
                    actual = SourceCheck.Json(new object[] { scan.Names.Order(StringComparer.Ordinal).ToArray(), scan.Bad });
                }
                else
                {
                    var docs = args[1]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());
                    var historical = DeadToolReferences.ContractNames(Root, "manifest/history/contracts-v3/baseline");
                    var current = DeadToolReferences.ContractNames(Root, "manifest/contracts/v4/baseline");
                    var retired = DeadToolReferences.MigrationNames(Root).Where(p => historical.Contains(p.Key) && !current.Contains(p.Key) && p.Key != p.Value).ToDictionary(p => p.Key, p => p.Value);
                    actual = SourceCheck.Json(DeadToolReferences.ScanMarkdown(docs, retired));
                }
                break;
            default: throw new InvalidOperationException(kind);
        }
        Assert.True(JsonNode.DeepEquals(row["expected"], actual), $"{kind} vector {index}: expected {row["expected"]}; actual {actual}");
    }
    [Fact]
    public void GenericDictionaryUsesPythonMappingSemantics() => Assert.Equal("{\"a\": 1}", PythonJson.Dumps(new Dictionary<string, int> { ["a"] = 1 }));
}
