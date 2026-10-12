using System.Text.Json;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

[Trait("Category", "StaticChecks")]
public sealed class AnalyzerWarningTests
{
    private const string Source = "internal class C { void M() { throw new System.Exception(\"fixture\"); } }\n";
    private static AnalyzerWarning Warning(StaticTree tree, string source = Source, string rule = "CA2201")
    {
        var start = source.IndexOf("new System.Exception", StringComparison.Ordinal);
        var line = source[..start].Count(c => c == '\n') + 1;
        var column = start - source.LastIndexOf('\n', start) ;
        return AnalyzerWarnings.Describe(tree.Root, "src/C.cs", line, column, line, column + "new System.Exception".Length, rule, "Fixture warning", source);
    }

    private static AnalyzerWarningCollection Collection(params AnalyzerWarning[] entries) => new(entries, new HashSet<string> { "src/C.cs" });

    [Fact]
    public void ReportDirectoriesMayLeaveTheCheckoutButMustBeLocal()
    {
        // check-all -Clean builds in a detached checkout and writes its reports into the caller's output directory.
        using var tree = new StaticTree(); using var outside = new StaticTree();
        var reports = Path.Combine(outside.Root, "check-all", "build", "analyzer-warnings");
        Assert.Equal(Path.GetFullPath(reports), AnalyzerWarnings.LocalDirectory(reports));
        AnalyzerWarnings.RememberDirectory(tree.Root, reports);
        Assert.Equal(Path.GetFullPath(reports), AnalyzerWarnings.DefaultDirectory(tree.Root));
        var inside = Path.Combine(tree.Root, "bin-build", "analyzer-warnings", "observed");
        AnalyzerWarnings.RememberDirectory(tree.Root, inside);
        Assert.Equal("bin-build/analyzer-warnings/observed\n", File.ReadAllText(Path.Combine(tree.Root, "bin-build/analyzer-warnings/last-directory.txt")));
        Assert.Throws<ReleaseException>(() => AnalyzerWarnings.LocalDirectory(@"\\server\share\reports"));
        Assert.Throws<ReleaseException>(() => AnalyzerWarnings.LocalDirectory("https://example.invalid/reports"));
        // Source paths and reviewed data stay inside the repository.
        Assert.Throws<ReleaseException>(() => AnalyzerWarnings.RepositoryPath(tree.Root, reports));
    }

    [Fact]
    public void NewWarningFailsAndDisappearanceRequiresBaselineUpdate()
    {
        using var tree = new StaticTree(); tree.Write("src/C.cs", Source);
        var entry = Warning(tree) with { Reason = "test-fixture" };
        Assert.Empty(AnalyzerWarnings.Compare(tree.Root, Collection(entry), [entry], true));
        Assert.Contains(AnalyzerWarnings.Compare(tree.Root, Collection(entry), [], true), issue => issue.StartsWith("NEW "));
        Assert.Contains(AnalyzerWarnings.Compare(tree.Root, Collection(), [entry], true), issue => issue.StartsWith("STALE "));
    }

    [Fact]
    public void MovingLinesAndStatementsInTheSameMemberPreservesIdentity()
    {
        using var tree = new StaticTree(); tree.Write("src/C.cs", Source);
        var entry = Warning(tree) with { Reason = "test-fixture" };
        var moved = Warning(tree, Source.Replace("throw ", "\n        int x = 0;\n        throw ", StringComparison.Ordinal));
        Assert.Equal(entry.Fingerprint, moved.Fingerprint);
        Assert.NotEqual(entry.Line, moved.Line);
        Assert.Empty(AnalyzerWarnings.Compare(tree.Root, Collection(moved), [entry], true));
        var otherMember = Warning(tree, Source.Replace("M()", "N()", StringComparison.Ordinal));
        Assert.NotEqual(entry.Fingerprint, otherMember.Fingerprint);
        Assert.Contains(AnalyzerWarnings.Compare(tree.Root, Collection(otherMember), [entry], true), issue => issue.StartsWith("NEW "));
    }

    [Fact]
    public void DuplicateWarningsCannotConsumeTheSameReviewedAllowance()
    {
        using var tree = new StaticTree(); tree.Write("src/C.cs", Source);
        var entry = Warning(tree) with { Reason = "test-fixture" };
        Assert.Single(AnalyzerWarnings.Compare(tree.Root, Collection(entry, entry with { Line = 2 }), [entry], true));
    }

    [Fact]
    public void BuildCollectionsKeepDistinctMessagesAndMergeIdenticalLocations()
    {
        using var tree = new StaticTree(); var entry = Warning(tree) with { Contexts = ["release"] };
        var same = entry with { Contexts = ["debug"] };
        var other = Warning(tree, rule: "CA1416") with { Contexts = ["release"] };
        var combined = AnalyzerWarnings.Combine([Collection(entry), Collection(same, other)]);
        Assert.Equal(2, combined.Entries.Length);
        Assert.Equal(new[] { "debug", "release" }, combined.Entries.Single(e => e.Rule == entry.Rule).Contexts);
    }

    [Fact]
    public void CompleteVariantBuildDoesNotClaimAnOrdinaryCompilerMessageDisappeared()
    {
        using var tree = new StaticTree(); tree.Write("src/C.cs", Source);
        var entry = Warning(tree) with { Reason = "test-fixture", Contexts = ["ordinary|C"] };
        var variants = new AnalyzerWarningCollection([], new HashSet<string> { "src/C.cs" },
            new Dictionary<string, HashSet<string>> { ["src/C.cs"] = ["variants|C"] });
        Assert.Empty(AnalyzerWarnings.Compare(tree.Root, variants, [entry], true));
        var ordinary = variants with { Coverage = new() { ["src/C.cs"] = ["ordinary|C"] } };
        Assert.Contains(AnalyzerWarnings.Compare(tree.Root, ordinary, [entry], true), issue => issue.StartsWith("STALE "));
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../../outside.cs")]
    [InlineData("https://example.invalid/source.cs")]
    public void ExternalPathsAreRefused(string path)
    {
        using var tree = new StaticTree();
        Assert.Throws<ReleaseException>(() => AnalyzerWarnings.RepositoryPath(tree.Root, path));
    }

    [Fact]
    public void PartialBuildChecksCoveredFilesAndDeletionIsAlwaysStale()
    {
        using var tree = new StaticTree(); var path = tree.Write("src/C.cs", Source);
        var entry = Warning(tree) with { Reason = "pending-port", Retirement = "B9" };
        var partial = new AnalyzerWarningCollection([], []);
        Assert.Empty(AnalyzerWarnings.Compare(tree.Root, partial, [entry], false));
        Assert.NotEmpty(AnalyzerWarnings.Compare(tree.Root, partial, [entry], true));
        File.Delete(path);
        Assert.Contains(AnalyzerWarnings.Compare(tree.Root, partial, [entry], false), issue => issue.StartsWith("STALE "));
    }

    private static string Reports(StaticTree tree, bool warning = true, string suffix = "a")
    {
        var source = tree.Write("src/C.cs", Source);
        var entry = Warning(tree);
        var result = new { ruleId = entry.Rule, level = "warning", message = new { text = entry.Message },
            locations = new[] { new { physicalLocation = new { artifactLocation = new { uri = new Uri(source).AbsoluteUri },
                region = new { startLine = entry.Line, startColumn = entry.Column, endLine = entry.EndLine, endColumn = entry.EndColumn } } } } };
        tree.Write("reports/" + suffix + ".sarif", JsonSerializer.Serialize(new { version = "2.1.0", runs = new[] { new { results = warning ? new[] { result } : [] } } }));
        tree.Write("reports/" + suffix + ".inputs", "context|" + Path.Combine(tree.Root, "C.csproj") + "|net10.0|NET\n" + source + "|" + ReleaseRecords.HashFile(source) + "\n");
        return Path.Combine(tree.Root, "reports");
    }

    [Fact]
    public void FrameworkAndVariantReportsDeduplicateButChangedInputsFail()
    {
        using var tree = new StaticTree(); var reports = Reports(tree); Reports(tree, suffix: "b");
        Assert.Single(AnalyzerWarnings.Collect(tree.Root, reports).Entries);
        tree.Write("src/C.cs", "\n" + Source);
        Assert.Throws<ReleaseException>(() => AnalyzerWarnings.Collect(tree.Root, reports));
    }

    [Theory]
    [InlineData("accepted", 0)]
    [InlineData("rejected", 1)]
    public void OnlyAcceptedSuppressionsAreAbsentFromCompilerWarnings(string status, int expected)
    {
        using var tree = new StaticTree(); var reports = Reports(tree);
        var path = Path.Combine(reports, "a.sarif");
        var data = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        data["runs"]![0]!["results"]![0]!["suppressions"] = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["kind"] = "inSource", ["status"] = status });
        File.WriteAllText(path, data.ToJsonString());
        Assert.Equal(expected, AnalyzerWarnings.Collect(tree.Root, reports).Entries.Length);
    }

    [Fact]
    public void TestSdkAuxiliaryInputCannotAuthorizeAnExternalWarning()
    {
        using var outside = new StaticTree(); using var tree = new StaticTree();
        var reports = Reports(tree);
        var template = outside.Write("microsoft.net.test.sdk/1/build/net10.0/Microsoft.NET.Test.Sdk.Program.cs",
            "// <auto-generated> SDK template </auto-generated>\n");
        File.AppendAllText(Path.Combine(reports, "a.inputs"), template + "|" + ReleaseRecords.HashFile(template) + "\n");
        Assert.Single(AnalyzerWarnings.Collect(tree.Root, reports).Entries);
        var path = Path.Combine(reports, "a.sarif");
        var data = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        data["runs"]![0]!["results"]![0]!["locations"]![0]!["physicalLocation"]!["artifactLocation"]!["uri"] = new Uri(template).AbsoluteUri;
        File.WriteAllText(path, data.ToJsonString());
        Assert.Throws<ReleaseException>(() => AnalyzerWarnings.Collect(tree.Root, reports));
    }

    [Fact]
    public void UpdateOnlyShrinksAndRefusesGrowthWithoutWriting()
    {
        using var tree = new StaticTree(); var reports = Reports(tree);
        var entry = AnalyzerWarnings.Collect(tree.Root, reports).Entries.Single() with { Reason = "test-fixture" };
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        var review = tree.Write("review.json", JsonSerializer.Serialize(new AnalyzerWarningBaseline(1, [entry]), json));
        var baseline = Path.Combine(tree.Root, "baseline.json");
        string[] arguments = ["-Root", tree.Root, "-DiagnosticsDirectory", reports, "-Baseline", baseline];
        int Run(params string[] args) => ReleaseCommands.RunStaticCaptured("check-analyzer-warnings", [.. arguments, .. args]).ExitCode;
        Assert.Equal(0, Run("-Initialize", "-ReviewData", review));
        Assert.Equal(0, Run("-Check", "-Complete"));
        Assert.Equal(64, Run("-Initialize", "-ReviewData", review));
        Reports(tree, warning: false);
        Assert.Equal(1, Run("-Check", "-Complete"));
        Assert.Equal(0, Run("-UpdateBaseline", "-Complete"));
        var bytes = File.ReadAllBytes(baseline);
        Reports(tree);
        Assert.Equal(1, Run("-UpdateBaseline", "-Complete"));
        Assert.Equal(bytes, File.ReadAllBytes(baseline));
        Assert.Equal(64, Run("-Check", "-UpdateBaseline", "-Complete"));
    }

    [Theory]
    [InlineData("-Check")]
    [InlineData("-UpdateBaseline")]
    [InlineData("-Initialize")]
    public void CaptureCannotBypassAGate(string mode)
    {
        using var tree = new StaticTree(); var reports = Reports(tree);
        var capture = Path.Combine(tree.Root, "capture.json");
        var result = ReleaseCommands.RunStaticCaptured("check-analyzer-warnings",
            ["-Root", tree.Root, "-DiagnosticsDirectory", reports, "-Capture", capture, mode]);
        Assert.Equal(64, result.ExitCode);
        Assert.False(File.Exists(capture));
    }

    [Fact]
    public void CaptureCannotOverwriteReviewedData()
    {
        using var tree = new StaticTree(); var reports = Reports(tree);
        var path = tree.Write("review.json", "reviewed bytes");
        var result = ReleaseCommands.RunStaticCaptured("check-analyzer-warnings",
            ["-Root", tree.Root, "-DiagnosticsDirectory", reports, "-Capture", path]);
        Assert.Equal(64, result.ExitCode);
        Assert.Equal("reviewed bytes", File.ReadAllText(path));
    }
}
