using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using TiaMcp.Tests.Shared;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class ReflectionGuardSources
{
    [Fact]
    public void Worker_passes_the_argument_count()
    {
        string source = SourceFamily.Read(Path.Combine(Repository.FindRoot(AppContext.BaseDirectory), "src/PlcWorker/WorkerObservations.cs"));
        string branch = source.Split("case \"reflection-deny\":")[1].Split("default:")[0];
        Assert.Contains("(int)arguments[\"argumentCount\"]!", Regex.Replace(branch, @"\s+", " "), StringComparison.Ordinal);
    }
}
