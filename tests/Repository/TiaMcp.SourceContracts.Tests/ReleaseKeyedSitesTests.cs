using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using Xunit;
using Xunit.Abstractions;

namespace TiaMcp.SourceContracts.Tests;

public sealed class ReleaseKeyedSitesTests(ITestOutputHelper output)
{
    private static readonly Regex Literal = new(""" "(?:20|21)" | \b(?:major|version|MajorVersion)\s*(?:<=?|>=?|==|!=)\s*(?:1[4-9]|2[01])\b """,
        RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);
    private static readonly Regex ToolRosterRow = new(
        """^\["\w+"\] = (ReleaseRange\.(Since|Between)\("[\d.a-z]+"(, "[\d.a-z]+")?\)|new\[\] \{ "[\d.a-z]+"(, "[\d.a-z]+")* \}),?$""");

    [Fact]
    public void EveryV21OnlyPolicyKeyHasAToolListRow()
    {
        var root = Repository.FindRoot(AppContext.BaseDirectory);
        var keys = DictionaryKeys(root, "src/Logic/Siemens/ToolVersionPolicy.cs", "V21Only");
        using var tools = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest/tools-list.json")));
        var names = tools.RootElement.GetProperty("tools").EnumerateArray().Select(row => row.GetProperty("name").GetString()).ToHashSet();
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.Contains(key, names));
    }

    [Fact]
    public void EveryV21OnlyPolicyKeyHasAPortedToolRelease()
    {
        var root = Repository.FindRoot(AppContext.BaseDirectory);
        var policy = DictionaryKeys(root, "src/Logic/Siemens/ToolVersionPolicy.cs", "V21Only");
        var releases = DictionaryKeys(root, "src/Adapters.Contracts/PortedFamilies.cs", "ToolReleases").ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(policy);
        Assert.All(policy, key => Assert.Contains(key, releases));
    }

    private static string[] DictionaryKeys(string root, string path, string field)
    {
        var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, path))).GetRoot();
        var declaration = Assert.Single(syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>(), variable => variable.Identifier.Text == field);
        var initializer = (declaration.Initializer?.Value as ObjectCreationExpressionSyntax)?.Initializer;
        Assert.True(initializer != null, $"{path}: {field} must have a dictionary initializer. Found '{declaration.Initializer?.Value}'.");
        return initializer.Expressions.Select(row =>
        {
            if (row is AssignmentExpressionSyntax { Left: ImplicitElementAccessSyntax { ArgumentList.Arguments: [var argument] } }
                && argument.Expression is LiteralExpressionSyntax key && key.Kind() == SyntaxKind.StringLiteralExpression)
                return key.Token.ValueText;
            throw new Xunit.Sdk.XunitException($"{path}:{row.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: " +
                $"expected a string-literal dictionary key in {field}. Found '{row}'.");
        }).ToArray();
    }

    [Theory]
    [InlineData("GetKeys()", "Keys must have a dictionary initializer", "GetKeys()")]
    [InlineData("new Dictionary<string, int> { [computed] = 1 }", "string-literal dictionary key in Keys", "[computed] = 1")]
    public void Unexpected_policy_syntax_identifies_the_field_and_offending_expression(string initializer, string expectation, string offending)
    {
        using var fixture = new OfflineFixtures();
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "Policy.cs"), $"class Policy {{ object Keys = {initializer}; }}");
        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => DictionaryKeys(fixture.DirectoryPath, "Policy.cs", "Keys"));
        Assert.Contains("Policy.cs", error.Message);
        Assert.Contains(expectation, error.Message);
        Assert.Contains(offending, error.Message);
    }

    [Fact]
    public void Release_literals_are_confined_to_reviewed_sites()
    {
        var root = Repository.FindRoot(AppContext.BaseDirectory);
        var src = Path.Combine(root, "src");
        var exceptions = File.ReadLines(Path.Combine(root, "tests/Repository/TiaMcp.SourceContracts.Tests/ReleaseKeyedSites.txt"))
            .Where(row => row.Length > 0 && !row.StartsWith('#')).Select(row => row.Split('|', 2))
            .ToLookup(row => row[0], row => row[1], StringComparer.Ordinal);
        var findings = new List<string>();
        foreach (var path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, path).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is "bin" or "obj")) continue;
            var allowed = exceptions[relative].ToList();
            int lineNumber = 0;
            foreach (var sourceLine in File.ReadLines(path))
            {
                lineNumber++;
                if (!Literal.IsMatch(sourceLine)) continue;
                var line = sourceLine.Trim();
                if (relative == "Adapters.Contracts/PortedFamilies.cs" && ToolRosterRow.IsMatch(line)) continue;
                for (int i = 0; i < allowed.Count;)
                {
                    int start = line.IndexOf(allowed[i], StringComparison.Ordinal);
                    if (start < 0) { i++; continue; }
                    line = line.Remove(start, allowed[i].Length);
                    allowed.RemoveAt(i);
                }
                if (Literal.IsMatch(line)) findings.Add($"{relative}:{lineNumber}  {line.Trim()}");
            }
            findings.AddRange(allowed.Select(fragment => $"{relative}  unused exception: {fragment}"));
        }
        foreach (var group in exceptions.Where(group => !File.Exists(Path.Combine(src, group.Key))))
            findings.AddRange(group.Select(fragment => $"{group.Key}  unused exception: {fragment}"));
        foreach (var finding in findings) output.WriteLine(finding);
        Assert.Empty(findings);
    }
}
