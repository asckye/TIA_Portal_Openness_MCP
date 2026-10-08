using System.Text;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.WorkerChannel;
using Xunit;

public sealed class CandidateAtomicDispatchTests
{
    private static DeviceCreateCheck Check(CandidateWorkerFixture.DeviceNative adapter)
    {
        var check = new DeviceCreateCheck { Identity = adapter.ReadIdentity(), Catalog = adapter.ReadCatalog(CandidateWorkerFixture.Identifier).Single(),
            Inventory = adapter.ReadInventory().ToArray(), TypeIdentifier = CandidateWorkerFixture.Identifier, DeviceName = "PLC" };
        check.Digest = CandidateDigest.DeviceObservation(check.Identity, check.Catalog, check.Inventory, check.TypeIdentifier, check.DeviceName);
        return check;
    }

    [Fact]
    public void QueuedWorkerOperationRunsAfterFinalCheckCreateAndReadback()
    {
        var adapter = new CandidateWorkerFixture.DeviceNative(); var check = Check(adapter);
        int owner = Environment.CurrentManagedThreadId; bool next = false; int calls = 0;
        adapter.Before = () => { Assert.Equal(owner, Environment.CurrentManagedThreadId); Assert.False(next); Assert.Equal(0, adapter.Creates); };
        string Frame(int id, string operation) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"adapter." + operation + "\",\"params\":{},\"bindingEpoch\":0}\n";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(1, "CreateHardwareDeviceCandidate") + Frame(2, "ReadState")));
        using var output = new MemoryStream();
        var identity = new ChannelIdentity("19", new string('a', 64), new string('b', 64), 42, new string('c', 64));
        var server = new ChannelServer(input, output, identity, () => new ChannelBinding(0, false), request =>
        {
            Assert.Equal(owner, Environment.CurrentManagedThreadId); calls++;
            if (calls == 1)
            {
                var result = CandidateExecution.Create(adapter, check);
                Assert.Null(result.Fault); Assert.Single(result.After); Assert.False(next);
            }
            else { Assert.Equal(1, adapter.Creates); next = true; }
            return ChannelResponse.Success("null");
        });
        server.Run(); Assert.True(next); Assert.Equal(2, calls); Assert.Equal(1, adapter.Creates);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ChangedObservationsOrArgumentsAreRejectedWithoutIssuing(bool atBoundary)
    {
        var adapter = new CandidateWorkerFixture.DeviceNative(); var check = Check(adapter);
        if (atBoundary) adapter.Before = () => check.DeviceName = "changed";
        else adapter.Rows.Add(new DeviceInventoryItem { Id = "new", Name = "Other", ParentId = "root" });
        var result = CandidateExecution.Create(adapter, check);
        Assert.Equal("stale", result.Fault!.Kind); Assert.False(result.Issued); Assert.False(result.RequiresSessionReset); Assert.Equal(0, adapter.Creates);
    }
}
