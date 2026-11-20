using TiaMcp.BuildCommon;
using TiaMcp.Tests.Shared;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class CrashProbeSources
{
    private static string Read(string path) => SourceFamily.Read(Path.Combine(Repository.FindRoot(AppContext.BaseDirectory), path));

    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, start);
        int last = source.IndexOf(end, first, StringComparison.Ordinal);
        Assert.True(last > first, end);
        return source.Substring(first, last - first);
    }

    [Fact]
    public void Sivarc_probe_bindings_never_enumerate_a_definition_composition()
    {
        string adapter = Between(Read("src/Adapters/Native/Session/NativeCrashProbeAdapter.cs"), "ReadProbeSivarc(", "ExecuteProbeMasterCopy(");
        string logic = Read("src/Adapters/Native/Session/NativeCrashProbes.cs");
        foreach (var source in new[] { adapter, logic })
            foreach (var member in new[] { "GetEnumerator", "IEnumerable", "Items(", ".Cast<", "PlcGroupOperations.Find" })
                Assert.DoesNotContain(member, source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach", adapter, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_probes_return_before_confirmation_and_project_identity_reads()
    {
        string dispatch = Between(Read("src/PlcWorker/FoundationWorkerDispatcher.cs"), "internal ChannelResponse Dispatch(", "catch (Exception ex)");
        int refusal = dispatch.IndexOf("WorkerOperations.DisabledProbeReply(", StringComparison.Ordinal);
        Assert.True(refusal > 0);
        foreach (var later in new[] { "WorkerJson.ParseArguments(", "var confirm=", "MutationIdentityPolicy.ValidateTarget(", "RequireProjectIdentity",
            "method.Invoke(" })
            Assert.True(refusal < dispatch.IndexOf(later, StringComparison.Ordinal), later);
    }

    [Fact]
    public void Master_copy_probe_acquires_no_exclusive_access()
    {
        string adapter = Between(Read("src/Adapters/Native/Session/NativeCrashProbeAdapter.cs"), "ExecuteProbeMasterCopy(", "ExportProbeAlarmTextLists(");
        foreach (var member in new[] { "ExclusiveAccess", "EditAccess", "Transaction" })
            Assert.DoesNotContain(member, adapter, StringComparison.Ordinal);
        Assert.Contains("MasterCopies", adapter, StringComparison.Ordinal);
        Assert.Contains(".Create((IMasterCopySource)source)", adapter, StringComparison.Ordinal);
    }
}
