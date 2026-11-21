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
        var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "src/Logic/Siemens/ToolVersionPolicy.cs"))).GetRoot();
        var policy = syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(variable => variable.Identifier.Text == "V21Only");
        var keys = policy.Initializer!.Value.DescendantNodes().OfType<ImplicitElementAccessSyntax>()
            .Select(access => ((LiteralExpressionSyntax)access.ArgumentList.Arguments.Single().Expression).Token.ValueText).ToArray();
        using var tools = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest/tools-list.json")));
        var names = tools.RootElement.GetProperty("tools").EnumerateArray().Select(row => row.GetProperty("name").GetString()).ToHashSet();
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.Contains(key, names));
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
