using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

[Trait("Category", "StaticChecks")]
public sealed class CheckLooseningTests
{
    private static List<ReviewFinding> Scan(string before, string after)
    {
        const string path = "tests/CounterTests.cs";
        return CheckLoosening.Scan([new(path, path, "class CounterTests {" + before + "}", "class CounterTests {" + after + "}")]);
    }

    [Theory]
    [InlineData("CountFits", "long")]
    [InlineData("CountIsInRange", "int")]
    public void TestEditsKeepTheMemberAndCompareEveryAssertion(string name, string type)
    {
        const string checks = "Assert.True(count > 0); Assert.NotEqual(9, count); Assert.InRange(count, 1, 8);";
        const string original = "[Theory][InlineData(4)] public void CountFits(int count) {" + checks + "}";
        string edited = $"[Theory][InlineData(4)] public void {name}({type} count) {{ count++; {checks} }}";
        Assert.Empty(Scan(original, edited));
        var removal = Assert.Single(Scan(original, edited.Replace("Assert.NotEqual(9, count);", "")));
        Assert.Equal("assertion-removed", removal.Kind);
        Assert.Contains("NotEqual", removal.Message);
        Assert.Equal("test-removed", Assert.Single(Scan(original, "")).Kind);
    }

    [Fact]
    public void DeletedTestCannotClaimAnotherTestsMatchingAssertions()
    {
        const string deleted = "[Fact] public void Removed() { Assert.True(IsReady()); }";
        const string kept = "[Fact] public void Kept() { Prepare(); Assert.True(IsReady()); }";
        var finding = Assert.Single(Scan(deleted + kept, kept));
        Assert.Equal("test-removed", finding.Kind);
        Assert.Contains("/Removed", finding.Identifier);
    }

    [Fact]
    public void AmbiguousEditedRenamesStillReportTheDeletion()
    {
        const string old = "[Fact] public void Old() { Assert.True(IsReady()); }";
        const string added = "[Fact] public void A() { Prepare(); Assert.True(IsReady()); }";
        var finding = Assert.Single(Scan(old, added + added.Replace("void A()", "void B()")));
        Assert.Equal("test-removed", finding.Kind);
    }

    [Fact]
    public void AnUnrelatedNewTestWithOneCommonCheckCannotMaskADeletion()
    {
        const string removed = "[Fact] public void Old() { Assert.True(IsReady()); }";
        const string added = "[Fact] public void Other() { Prepare(); Assert.True(IsReady()); Assert.NotNull(Current()); Assert.Empty(Errors()); }";
        var finding = Assert.Single(Scan(removed, added));
        Assert.Equal("test-removed", finding.Kind);
        Assert.Contains("/Old", finding.Identifier);
    }

    [Fact]
    public void MultilineInlineDataShowsTheEntireRemovedRowOnOneLogLine()
    {
        string argument = new string('x', 240) + " tail";
        string row = $"[InlineData(1,\n    \"{argument}\")]";
        const string test = "public void HasText(int id, string text) { Assert.NotEmpty(text); }";
        string before = "[Theory]" + row + test;
        Assert.Empty(Scan(before, before.Replace("\n    ", " ")));
        var finding = Assert.Single(Scan(before, "[Theory][InlineData(2, \"new\")]" + test));
        Assert.Equal("assertion-removed", finding.Kind);
        Assert.DoesNotContain("\n", finding.Message);
        Assert.Contains("[InlineData(1,", finding.Message);
        Assert.Contains($"\"{argument}\")]", finding.Message);
    }
}
