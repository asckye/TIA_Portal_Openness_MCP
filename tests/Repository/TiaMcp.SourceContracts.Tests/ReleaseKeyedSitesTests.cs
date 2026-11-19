using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class ReleaseKeyedSitesTests
{
    private static readonly Regex Literal = new("\"(?:20|21)\"");
    private static readonly Regex ToolRosterRow = new(
        """^\["\w+"\] = (ReleaseRange\.(Since|Between)\("[\d.a-z]+"(, "[\d.a-z]+")?\)|new\[\] \{ "[\d.a-z]+"(, "[\d.a-z]+")* \}),?$""");

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
        Assert.Empty(findings);
    }
}
