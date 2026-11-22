using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

[Trait("Category", "StaticChecks")]
public sealed class CheckLimitChangesTests
{
    [Theory]
    [InlineData("count >= 157 && skipped <= 5", null)]
    [InlineData("count >= 156 && skipped <= 5", "limit-lowered")]
    [InlineData("count >= 157", "gate-removed")]
    public void SignatureChangesPreserveTheComparisonForEachOverload(string condition, string? expected)
    {
        const string path = "build-tools/release/MergeCheck.cs";
        const string stable = "bool CheckResult(long count) => count >= 36;";
        const string changed = "bool CheckResult(int count, int skipped) => count >= 157 && skipped <= 5;";
        string before = $"class MergeCheck {{ {changed} {stable} }}";
        string after = $"class MergeCheck {{ {stable} bool CheckResult(long count, int skipped) => {condition}; }}";
        var findings = CheckLoosening.Scan([new(path, path, before, after)]);
        if (expected == null) Assert.Empty(findings);
        else Assert.Equal(expected, Assert.Single(findings).Kind);
    }

    [Fact]
    public void ADeletedOverloadCannotReuseTheSurvivingOverloadsLimits()
    {
        const string path = "build-tools/release/MergeCheck.cs";
        const string stable = "bool CheckResult(long count) => count >= 157;";
        const string removed = "bool CheckResult(int count) => count >= 157;";
        var findings = CheckLoosening.Scan([new(path, path, $"class C {{ {removed} {stable} }}", $"class C {{ {stable} }}")]);
        Assert.Equal("gate-removed", Assert.Single(findings).Kind);
    }

    [Fact]
    public void OptionalParameterLiteralsAreComparedSeparatelyFromBodyLimits()
    {
        const string path = "build-tools/release/MergeCheck.cs";
        const string before = "class C { bool CheckResult(int count, int minimum = 36) => count >= 157 && count >= minimum; }";
        string added = before.Replace("int minimum", "int skipped = 0, int minimum");
        Assert.Empty(CheckLoosening.Scan([new(path, path, before, added)]));
        string removed = added.Replace("count >= 157 && ", "");
        Assert.Equal("gate-removed", Assert.Single(CheckLoosening.Scan([new(path, path, before, removed)])).Kind);
        string lowered = added.Replace("minimum = 36", "minimum = 35");
        Assert.Equal("limit-lowered", Assert.Single(CheckLoosening.Scan([new(path, path, before, lowered)])).Kind);
    }
}
