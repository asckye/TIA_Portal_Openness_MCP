using System.Diagnostics;
using System.Security.Cryptography;
using TiaMcp.WorkerChannel;
using Xunit;
using TiaMcp.Engine.Tests.Shared;
using static TiaMcp.WorkerChannel.Tests.ProtocolTests;

namespace TiaMcp.WorkerChannel.Tests;

public sealed class ProcessTests
{
    [Fact]
    public async Task UnhandledFixtureFaultHasDocumentedExitAndOneDiagnosticLine()
    {
        using var fixture = new Fixture("unhandled");
        await fixture.Process.WaitForExitAsync().WaitAsync(Budget);
        string stderr = await fixture.diagnostics;
        Assert.True(fixture.Process.ExitCode == 86, FixtureExit.Describe(fixture.Process.ExitCode, stderr));
        Assert.Equal("FIXTURE_UNHANDLED_EXCEPTION System.InvalidOperationException: Forced transport fixture fault", stderr.TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task DuplicateHelloTerminal()
    {
        using var fixture=new Fixture("duplicate-hello");
        try { await fixture.Client.ConnectAsync(Budget); } catch(ChannelFaultException) { /* The second hello may arrive in the same read. */ }
        await Eventually(()=>fixture.Client.Poisoned);
        await Assert.ThrowsAsync<ChannelFaultException>(()=>fixture.Call()); Assert.Equal(0,fixture.Calls);
    }

    [Theory]
    [InlineData("release")] [InlineData("worker-hash")] [InlineData("adapter-hash")] [InlineData("pid")]
    [InlineData("nonce")] [InlineData("version")] [InlineData("nonfresh")] [InlineData("bound")]
    [InlineData("missing-hello")] [InlineData("startup-exit")] [InlineData("hello-oversized")]
    public async Task HandshakeFailedBeforeWrites(string mode)
    {
        using var fixture=new Fixture(mode);
        await Assert.ThrowsAsync<ChannelFaultException>(()=>fixture.Client.ConnectAsync(TimeSpan.FromMilliseconds(700)));
        Assert.True(fixture.Client.Poisoned); Assert.False(fixture.Client.OutcomeUnknown); Assert.Equal(0,fixture.Calls);
    }

    [Theory]
    [InlineData("late-hello")] [InlineData("unknown-id")] [InlineData("old-id")]
    [InlineData("epoch-before")] [InlineData("epoch-after")] [InlineData("bad-framing")]
    [InlineData("invalid-utf8")] [InlineData("truncated")] [InlineData("oversized")]
    [InlineData("timeout")] [InlineData("broken-pipe")]
    public async Task TerminalAmbiguityNoReplay(string mode)
    {
        using var fixture=new Fixture(mode); await fixture.Client.ConnectAsync(Budget);
        await Assert.ThrowsAsync<ChannelFaultException>(()=>fixture.Call(timeout:TimeSpan.FromMilliseconds(700)));
        Assert.True(fixture.Client.OutcomeUnknown);
        await Assert.ThrowsAsync<ChannelFaultException>(()=>fixture.Call()); Assert.Equal(1,fixture.Calls);
    }

    [Fact]
    public async Task First_attach_timeout_explains_the_TIA_access_confirmation()
    {
        using var fixture=new Fixture("timeout"); await fixture.Client.ConnectAsync(Budget);
        var fault=await Assert.ThrowsAsync<ChannelFaultException>(()=>fixture.Client.CallAsync("adapter.Attach","{}",BindingChange.Advance,false,
            TimeSpan.FromMilliseconds(700),firstAttach:true));
        Assert.True(fixture.Client.OutcomeUnknown);
        Assert.Contains("native outcome is unknown",fault.Message,StringComparison.Ordinal);
        Assert.Contains("Yes to all",fault.Message,StringComparison.Ordinal);
        Assert.Contains("Openness access confirmation",fault.Message,StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_attach_timeout_is_identified_after_an_earlier_read()
    {
        using var fixture=new Fixture("timeout-attach"); await fixture.Client.ConnectAsync(Budget);
        await fixture.Call();
        var fault=await Assert.ThrowsAsync<ChannelFaultException>(()=>fixture.Client.CallAsync("adapter.Attach",
            "{\"processId\":42}",BindingChange.Advance,false,TimeSpan.FromMilliseconds(700),firstAttach:true));
        Assert.True(fixture.Client.OutcomeUnknown);
        Assert.Contains("Yes to all",fault.Message,StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("second-reply")] [InlineData("late-progress")]
    public async Task TrailingFramesPoisonIdlePeer(string mode)
    {
        using var fixture=new Fixture(mode); await fixture.Client.ConnectAsync(Budget);
        try { await fixture.Call(); } catch(ChannelFaultException) { /* Expected if the receive loop wins the reply race. */ }
        await Eventually(()=>fixture.Client.Poisoned);
        await Assert.ThrowsAsync<ChannelFaultException>(()=>fixture.Call()); Assert.Equal(1,fixture.Calls);
    }

    [Theory]
    [InlineData("read-failed",ChannelOutcome.ReadFailed,false)]
    [InlineData("rejected",ChannelOutcome.RejectedBeforeNative,false)]
    [InlineData("session-fault",ChannelOutcome.Unknown,false)]
    public async Task ReadFailureOutcomePreserved(string mode,ChannelOutcome outcome,bool poisoned)
    {
        using var fixture=new Fixture(mode); await fixture.Client.ConnectAsync(Budget);
        var error=await Assert.ThrowsAsync<ChannelFailureException>(()=>fixture.Call()); Assert.Equal(outcome,error.Outcome); Assert.Equal(poisoned,fixture.Client.Poisoned);
        Assert.Equal(1,fixture.Calls);
        var next=await Assert.ThrowsAsync<ChannelFailureException>(()=>fixture.Call());
        Assert.Equal(outcome,next.Outcome); Assert.Equal(error.Code,next.Code);
        Assert.Equal(2,fixture.Calls);
    }

    [Fact]
    public async Task UnsentCancellationStaysUsable()
    {
        using var fixture=new Fixture(""); await fixture.Client.ConnectAsync(Budget);
        using var cts=new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fixture.Call(token:cts.Token)); Assert.Equal(0,fixture.Calls);
        await fixture.Call(); Assert.False(fixture.Client.Poisoned); Assert.Equal(1,fixture.Calls);
    }

    [Fact]
    public async Task ProgressAcceptedAndDistinctOwnedHandles()
    {
        using var a=new Fixture("progress"); using var b=new Fixture("");
        await a.Client.ConnectAsync(Budget); await b.Client.ConnectAsync(Budget); Assert.NotEqual(a.Process.Id,b.Process.Id);
        await a.Call(); a.Dispose(); await b.Call(); Assert.False(b.Client.Poisoned);
    }

    [Fact]
    public async Task ConcurrentEndpointPoisoned()
    {
        using var fixture=new Fixture("timeout"); await fixture.Client.ConnectAsync(Budget);
        var pending=fixture.Call(); await Eventually(()=>fixture.Calls==1);
        await Assert.ThrowsAsync<ChannelFaultException>(()=>fixture.Call()); await Assert.ThrowsAsync<ChannelFaultException>(()=>pending); Assert.Equal(1,fixture.Calls);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Process Process;
        internal readonly ChannelClient Client;
        private readonly string log=Path.Combine(Path.GetTempPath(),"tia-channel-"+Guid.NewGuid().ToString("N")+".jsonl");
        private bool disposed;
        internal readonly Task<string> diagnostics;
        private readonly string image;
        private readonly bool expectedCrash;
        internal Fixture(string mode)
        {
            string exe=TestPaths
                .WorkerFixture("TiaMcp.WorkerChannel.TransportFixture", "TiaMcp.WorkerChannel.TransportFixture"+(OperatingSystem.IsWindows()?".exe":""));
            image=exe; expectedCrash=mode=="unhandled";
            string adapter=Path.Combine(Path.GetDirectoryName(exe)!,"TiaMcp.Adapter.19.dll");
            string Hash(string path) { using var stream=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
            string nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var start=new ProcessStartInfo(exe) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
            foreach(var arg in new[]{"--native-session","19",Path.GetTempPath(),nonce}) start.ArgumentList.Add(arg);
            start.Environment["TIA_FIXTURE_LOG"]=log; start.Environment["TIA_FIXTURE_FAULT"]=mode;
            Process=Process.Start(start)!; diagnostics=Process.StandardError.ReadToEndAsync();
            Client=new ChannelClient(Process.StandardOutput.BaseStream,Process.StandardInput.BaseStream,new ChannelIdentity("19",Hash(exe),Hash(adapter),Process.Id,nonce));
        }
        internal int Calls=>File.Exists(log)?ReadLog().Split('\n').Count(l=>l.Contains("\"stage\":\"call\"")):0;
        // The fixture appends while the test polls. File.ReadAllLines denies other writers, so either side could fail
        // with a sharing violation; share read/write access instead.
        private string ReadLog()
        {
            using var stream=new FileStream(log,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var reader=new StreamReader(stream);
            return reader.ReadToEnd();
        }
        internal Task<string> Call(CancellationToken token=default,TimeSpan? timeout=null)=>Client.CallAsync("adapter.ReadState","{}",BindingChange.None,true,timeout??Budget,token);
        public void Dispose()
        {
            if(disposed) return; disposed=true;
            Client.Dispose();
            // Only this synthetic child is terminated. Production ChannelClient never kills a worker.
            if(!Process.WaitForExit(1000))
            {
                Assert.Equal(Path.GetFullPath(image), Path.GetFullPath(Process.MainModule!.FileName),
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                Process.Kill(); Process.WaitForExit();
            }
            string stderr=diagnostics.GetAwaiter().GetResult();
            try { if(!expectedCrash) Assert.True(Process.ExitCode!=86,FixtureExit.Describe(Process.ExitCode,stderr)); }
            finally { Process.Dispose(); File.Delete(log); }
        }
    }
}
