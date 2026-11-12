using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class HmiTemplateQaSources
{
    [Fact]
    public void Template_apply_design_passes_its_fallbacks_to_the_layout_check()
    {
        string source = System.Text.RegularExpressions.Regex.Replace(Repository.ReadSource(
            Path.Combine(Repository.FindRoot(AppContext.BaseDirectory), "src/Shared/Host/OfflineSuiteTools.UnifiedTemplates.cs")), @"\s+", " ");
        string[] calls = source.Split("HmiTemplateLayoutAnalyzer.AnalyzeFile(templateFile, path =>", StringSplitOptions.None);
        Assert.Equal(3, calls.Length);
        foreach (string call in calls.Skip(1))
        {
            int end = call.IndexOf("var designJson =", StringComparison.Ordinal);
            Assert.True(end >= 0);
            Assert.EndsWith("}, fallbackWidth, fallbackHeight); ", call[..end], StringComparison.Ordinal);
        }
    }
}
