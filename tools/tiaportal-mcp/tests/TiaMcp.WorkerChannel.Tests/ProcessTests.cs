using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using TiaMcp.WorkerChannel;
using Xunit;
using static TiaMcp.WorkerChannel.Tests.ProtocolTests;

namespace TiaMcp.WorkerChannel.Tests;

public sealed class ProcessTests
{
    [Fact]
    public async Task DuplicateHelloTerminal()
    {
        using var fixture=new Fixture("duplicate-hello");
        try { await fixture.Client.ConnectAsync(Budget); } catch(ChannelFault) { /* The second hello may arrive in the same read. */ }
        await Eventually(()=>fixture.Client.Poisoned);
        await Assert.ThrowsAsync<ChannelFault>(()=>fixture.Call()); Assert.Equal(0,fixture.Calls);
    }

    [Theory]
    [InlineData("release")] [InlineData("worker-hash")] [InlineData("adapter-hash")] [InlineData("pid")]
    [InlineData("nonce")] [InlineData("version")] [InlineData("nonfresh")] [InlineData("bound")]
    [InlineData("missing-hello")] [InlineData("startup-exit")] [InlineData("hello-oversized")]
    public async Task HandshakeFailedBeforeWrites(string mode)
    {
        using var fixture=new Fixture(mode);
        await Assert.ThrowsAsync<ChannelFault>(()=>fixture.Client.ConnectAsync(TimeSpan.FromMilliseconds(700)));
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
        await Assert.ThrowsAsync<ChannelFault>(()=>fixture.Call(timeout:TimeSpan.FromMilliseconds(700)));
        Assert.True(fixture.Client.OutcomeUnknown);
        await Assert.ThrowsAsync<ChannelFault>(()=>fixture.Call()); Assert.Equal(1,fixture.Calls);
    }

    [Theory]
    [InlineData("second-reply")] [InlineData("late-progress")]
    public async Task TrailingFramesPoisonIdlePeer(string mode)
    {
        using var fixture=new Fixture(mode); await fixture.Client.ConnectAsync(Budget);
        try { await fixture.Call(); } catch(ChannelFault) { /* Expected if the receive loop wins the reply race. */ }
        await Eventually(()=>fixture.Client.Poisoned);
        await Assert.ThrowsAsync<ChannelFault>(()=>fixture.Call()); Assert.Equal(1,fixture.Calls);
    }

    [Theory]
    [InlineData("read-failed",ChannelOutcome.ReadFailed,false)]
    [InlineData("rejected",ChannelOutcome.RejectedBeforeNative,false)]
    [InlineData("session-fault",ChannelOutcome.Unknown,true)]
    public async Task ReadFailureOutcomePreserved(string mode,ChannelOutcome outcome,bool poisoned)
    {
        using var fixture=new Fixture(mode); await fixture.Client.ConnectAsync(Budget);
        var error=await Assert.ThrowsAsync<ChannelFailure>(()=>fixture.Call()); Assert.Equal(outcome,error.Outcome); Assert.Equal(poisoned,fixture.Client.Poisoned);
        if(poisoned) await Assert.ThrowsAsync<ChannelFault>(()=>fixture.Call());
        else await Assert.ThrowsAsync<ChannelFailure>(()=>fixture.Call());
        Assert.Equal(poisoned?1:2,fixture.Calls);
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
        await Assert.ThrowsAsync<ChannelFault>(()=>fixture.Call()); await Assert.ThrowsAsync<ChannelFault>(()=>pending); Assert.Equal(1,fixture.Calls);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Process Process;
        internal readonly ChannelClient Client;
        private readonly string log=Path.Combine(Path.GetTempPath(),"tia-channel-"+Guid.NewGuid().ToString("N")+".jsonl");
        private bool disposed;
        private readonly Task<string> diagnostics;
        internal Fixture(string mode,[CallerFilePath] string source="")
        {
            string exe=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!,"../TiaMcpServer.TransportFixture/bin/Release/net8.0/TransportFixture"+(OperatingSystem.IsWindows()?".exe":"")));
            string adapter=Path.Combine(Path.GetDirectoryName(exe)!,"TiaMcp.Adapter.19.dll");
            string Hash(string path) { using var stream=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
            string nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var start=new ProcessStartInfo(exe) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
            foreach(var arg in new[]{"--native-session","19",Path.GetTempPath(),nonce}) start.ArgumentList.Add(arg);
            start.Environment["TIA_FIXTURE_LOG"]=log; start.Environment["TIA_FIXTURE_FAULT"]=mode;
            Process=Process.Start(start)!; diagnostics=Process.StandardError.ReadToEndAsync();
            Client=new ChannelClient(Process.StandardOutput.BaseStream,Process.StandardInput.BaseStream,new ChannelIdentity("19",Hash(exe),Hash(adapter),Process.Id,nonce));
        }
        internal int Calls=>File.Exists(log)?File.ReadAllLines(log).Count(l=>l.Contains("\"stage\":\"call\"")):0;
        internal Task<string> Call(CancellationToken token=default,TimeSpan? timeout=null)=>Client.CallAsync("adapter.ReadState","{}",BindingChange.None,true,timeout??Budget,token);
        public void Dispose()
        {
            if(disposed) return; disposed=true;
            Client.Dispose();
            // Only this synthetic child is terminated. Production ChannelClient never kills a worker.
            if(!Process.WaitForExit(1000)) { Process.Kill(); Process.WaitForExit(); }
            _=diagnostics.GetAwaiter().GetResult(); Process.Dispose(); File.Delete(log);
        }
    }
}
