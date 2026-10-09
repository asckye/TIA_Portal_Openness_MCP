using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.PlcWorker;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class WorkerOperationModuleTests
{
    private sealed class Facade
    {
        public string Read() => "read";
        public void Write() { }
        public void Unregistered() { }
        public static void Static() { }
    }

    [Fact]
    public void LegacyAndFamilyModulesKeepExactWireNames()
    {
        var methods = WorkerOperationModule.Register(new[] {
            new WorkerOperationModule("", typeof(Facade), new[] { "Read" }),
            new WorkerOperationModule("hardware-addresses", typeof(Facade), new[] { "Write" })
        });
        Assert.Equal(new[] { "Read", "hardware-addresses.Write" }, methods.Keys);
        Assert.Equal("read", methods["Read"].Invoke(new Facade(), Array.Empty<object>()));
        Assert.False(methods.ContainsKey("Unregistered"));
        Assert.False(methods.ContainsKey("read"));
        Assert.False(methods.ContainsKey("Write"));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("read")]
    [InlineData("Static")]
    [InlineData("ToString")]
    public void MissingOrUnownedMethodsFailRegistration(string name)
        => Assert.Throws<InvalidOperationException>(() => new WorkerOperationModule("hardware", typeof(Facade), new[] { name }));

    [Fact]
    public void DuplicateWireNamesFailBeforeDispatch()
        => Assert.Throws<ArgumentException>(() => WorkerOperationModule.Register(new[] {
            new WorkerOperationModule("hardware", typeof(Facade), new[] { "Read" }),
            new WorkerOperationModule("hardware", typeof(Facade), new[] { "Read" })
        }));

    [Fact]
    public void CandidateResetAndMutationEvidenceAreIndependent()
    {
        var reply = new DeviceCandidateReply { Attempt = new DeviceCreateAttempt { Issued = true } };
        var outcome = (IWorkerOperationReply)reply;
        Assert.True(outcome.MayHaveChanged);
        Assert.False(outcome.RequiresSessionReset);
        Assert.True(outcome.BlockReadsAfterUncertain);
        reply.RequiresSessionReset = true;
        Assert.True(outcome.RequiresSessionReset);
        Assert.DoesNotContain("MayHaveChanged", WorkerJson.Serialize(reply));
        Assert.DoesNotContain("BlockReadsAfterUncertain", WorkerJson.Serialize(reply));
    }

    [Fact]
    public void ExistingReplyReadLockPoliciesStayDistinct()
    {
        var device = new TiaMcp.Adapters.PlcDeviceAddResult();
        var documentBatch = new TiaMcp.Adapters.PlcBatchDocumentImportResult();
        Assert.False(((IWorkerOperationReply)device).MayHaveChanged);
        Assert.False(((IWorkerOperationReply)device).BlockReadsAfterUncertain);
        Assert.True(((IWorkerOperationReply)documentBatch).BlockReadsAfterUncertain);
    }
}
