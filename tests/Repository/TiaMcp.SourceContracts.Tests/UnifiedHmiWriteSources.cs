using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class UnifiedHmiWriteSources
{
    private static string Read(string path) => Regex.Replace(Repository.ReadSource(
        Path.Combine(Repository.FindRoot(AppContext.BaseDirectory), path)), @"\s+", " ");

    [Fact]
    public void Bind_dynamization_writes_only_Tag()
    {
        string source = Read("src/Adapters/Native/Hmi/UnifiedHmiAdapter.cs");
        int start = source.IndexOf("public HmiMessage BindUnifiedHmiTagDynamization", StringComparison.Ordinal);
        int end = source.IndexOf("private object ResolveHmiScreenItemOrThrow", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string method = source[start..end];
        Assert.Contains("Write(\"Tag\", tagName, () => _session.TrySetProperty(dyn, \"Tag\", tagName))", method, StringComparison.Ordinal);
        foreach (string property in new[] { "DataType", "PlcTag", "Address" })
        {
            Assert.DoesNotContain("TrySetProperty(dyn, \"" + property + "\"", method, StringComparison.Ordinal);
            Assert.DoesNotContain("Write(\"" + property + "\"", method, StringComparison.Ordinal);
        }
        Assert.Contains("UnifiedUiModelLogic.VerifiedDerived(dyn, property, value)", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TrySetTagDynamizationAddress", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_dynamization_checks_the_snapshot_before_the_preview_returns()
    {
        string source = Read("src/Adapters/Native/Hmi/UnifiedHmiInspectionAdapter.cs");
        int start = source.IndexOf("public HmiMessage DeleteUnifiedHmiDynamization", StringComparison.Ordinal);
        int check = source.IndexOf("HmiExchangePolicy.UnifiedDynamizationDeleteRefusal(data)", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && check > start);
        int refusal = source.IndexOf("if (refusal != null) throw", check, StringComparison.Ordinal);
        int preview = source.IndexOf("if(dryRun)return", start, StringComparison.Ordinal);
        int token = source.IndexOf("HmiExactAccess.RequireToken", preview, StringComparison.Ordinal);
        int delete = source.IndexOf("HmiExactAccess.Delete(dyn)", token, StringComparison.Ordinal);
        Assert.True(start >= 0 && check > start && refusal > check && preview > refusal && token > preview && delete > token);
    }
}
