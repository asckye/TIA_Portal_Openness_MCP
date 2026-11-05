using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReviewPortRoundThreeTests
{
    private static CheckChange Change(string before, string after, string path = "tests/Example.cs") => new(path, path, before, after);

    [Theory]
    [InlineData("check")]
    [InlineData("Check")]
    public void ConsoleChecksAreAssertionsOnlyInTestAndScriptSources(string name)
    {
        string before = "class C { void M(){ " + name + "(true); } }";
        const string after = "class C { void M(){} }";
        Assert.Contains(CheckLoosening.Scan([Change(before, after)]), row => row.Kind == "assertion-removed");
        Assert.Contains(CheckLoosening.Scan([Change(before, after, "scripts/checks/Example.cs")]), row => row.Kind == "assertion-removed");
        Assert.Empty(CheckLoosening.Scan([Change(before, after, "src/Example.cs")]));
    }

    [Fact]
    public void PythonGlobalStatementsAreChecked()
    {
        const string before = "def helper():\n    pass\n\nif __name__ == '__main__':\n    assert count >= 8\n";
        Assert.Contains(CheckLoosening.Scan([Change(before, before.Replace("assert count >= 8", "pass"),
            "scripts/checks/Example.py")]), row => row.Kind == "assertion-removed");
    }

    [Fact]
    public void RemovedTrailingLimitIsNotDiscarded()
    {
        const string before = "class C { bool CheckResult() => passed >= 36 && rejected >= 5; }";
        const string after = "class C { bool CheckResult() => passed >= 36; }";
        Assert.Contains(CheckLoosening.Scan([Change(before, after, "build-tools/release/MergeCheck.cs")]),
            row => row.Kind == "gate-removed");
    }

    [Theory]
    [InlineData("5000")]
    [InlineData("50")]
    public void AChangedNestedNumberIsNotTreatedAsAStricterTopLevelBound(string number)
    {
        const string before = "class C { void M(){ Assert.True(rows.Count(r => r.Ms > 500) == 0); } }";
        Assert.Contains(CheckLoosening.Scan([Change(before, before.Replace("500", number))]), row => row.Kind == "assertion-removed");
    }

    [Fact]
    public void UnregisteredTestProjectDoesNotPreserveMovedTests()
    {
        using var tree = new StaticTree();
        tree.Write("tests/Registered/Tests.csproj", "<Project />");
        tree.Write("tests/Fixtures/TiaMcp.Offline.Fixtures/Fixtures.csproj", "<Project />");
        tree.Write("tests/test-suites.json", """{"one":{"project":"tests/Registered/Tests.csproj"}}""");
        const string source = "class C { [Fact] void T(){} }";
        Assert.Contains(CheckLoosening.Scan([new("tests/Fixtures/TiaMcp.Offline.Fixtures/T.cs", "tests/Registered/T.cs", source, source)],
            ReviewTestSources.Create(tree.Root)), row => row.Kind == "check-file-deleted");
    }

    [Fact]
    public void AddedConjunctIsStricterAndRemovedConjunctNeedsReview()
    {
        const string before = "class C { void M(){ Assert.True(a); } }";
        const string after = "class C { void M(){ Assert.True(a && b); } }";
        Assert.Empty(CheckLoosening.Scan([Change(before, after)]));
        Assert.Contains(CheckLoosening.Scan([Change(after, before)]), row => row.Kind == "assertion-removed");
    }

    [Theory]
    [InlineData("csproj")]
    [InlineData("props")]
    public void MovedProjectFilesAreNotDeletedTests(string extension)
    {
        const string project = "<Project />";
        Assert.Empty(CheckLoosening.Scan([new("tests/New/Test." + extension, "tests/Old/Test." + extension, project, project)]));
        Assert.Contains(CheckLoosening.Scan([Change("""{"one":{"project":"tests/Old/Test.csproj"}}""", "{}",
            "tests/test-suites.json")]), row => row.Kind == "suite-removed");
    }
}
