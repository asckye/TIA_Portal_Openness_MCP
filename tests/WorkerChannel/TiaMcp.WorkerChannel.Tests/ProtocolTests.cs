using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using TiaMcp.WorkerChannel;
using Xunit;

namespace TiaMcp.WorkerChannel.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task Answered_unknown_keeps_only_the_idle_disconnect_channel_available()
    {
        using var wire = new Wire(); await wire.Connect();
        wire.Output.OnWrite = _ => wire.Input.Feed(Reply(payload: "\"error\":{\"code\":-32603,\"message\":\"unknown\",\"data\":{\"outcome\":\"Unknown\",\"evidence\":null}}"));
        await Assert.ThrowsAsync<ChannelFailure>(() => wire.Call(readOnly: false));
        Assert.True(wire.Client.Poisoned); Assert.True(wire.Client.CanDisconnect);
        await Assert.ThrowsAsync<ChannelFault>(() => wire.Call());
        Assert.Single(wire.Output.Writes);
        wire.Output.OnWrite = _ => wire.Input.Feed(Reply(2, payload: "\"result\":{\"detached\":true}"));
        Assert.Equal("{\"detached\":true}", await wire.Client.CallAsync("adapter.Disconnect", "{}", BindingChange.MayAdvance, false, Budget));
        Assert.Equal(2, wire.Output.Writes.Count);
    }
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);
    internal static readonly ChannelIdentity Identity = new("19",new string('a',64),new string('b',64),42,new string('c',64));
    internal static string Hello => "{\"jsonrpc\":\"2.0\",\"method\":\"hello\",\"params\":{\"protocol\":2,\"releaseKey\":\"19\",\"workerSha256\":\""+Identity.WorkerSha256+"\",\"adapterSha256\":\""+Identity.AdapterSha256+"\",\"pid\":42,\"nonce\":\""+Identity.Nonce+"\",\"bindingEpoch\":0,\"bound\":false}}";
    internal static string Reply(long id=1,long before=0,long after=0,string payload="\"result\":null") => "{\"jsonrpc\":\"2.0\",\"id\":"+id+",\"bindingEpochBefore\":"+before+",\"bindingEpochAfter\":"+after+","+payload+"}";
    internal static string Request(long id=1,long epoch=0) => "{\"jsonrpc\":\"2.0\",\"id\":"+id+",\"method\":\"adapter.ReadState\",\"params\":{},\"bindingEpoch\":"+epoch+"}";
    internal static string Progress(long id=1,int sequence=1,int percent=50) => "{\"jsonrpc\":\"2.0\",\"method\":\"progress\",\"params\":{\"requestId\":"+id+",\"sequence\":"+sequence+",\"percent\":"+percent+"}}";
    internal static async Task Eventually(Func<bool> condition)
    {
        var stop=DateTime.UtcNow+Budget;
        while(!condition() && DateTime.UtcNow<stop) await Task.Delay(5);
        Assert.True(condition());
    }

    [Fact]
    public async Task FreshUnboundHelloRequired()
    {
        using var wire=new Wire();
        await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call());
        Assert.True(wire.Client.Poisoned); Assert.False(wire.Client.OutcomeUnknown); Assert.Empty(wire.Output.Writes);
    }

    [Theory]
    [InlineData("protocol",1)] [InlineData("releaseKey","21")] [InlineData("workerSha256","bad")]
    [InlineData("adapterSha256","bad")] [InlineData("pid",43)] [InlineData("nonce","bad")]
    [InlineData("bindingEpoch",1)] [InlineData("bound",true)]
    public async Task BadHelloPoisonsSession(string field,object value)
    {
        using var wire=new Wire(); var hello=JsonNode.Parse(Hello)!;
        hello["params"]![field]=System.Text.Json.JsonSerializer.SerializeToNode(value);
        wire.Input.Feed(hello.ToJsonString());
        await Assert.ThrowsAsync<ChannelFault>(()=>wire.Client.ConnectAsync(Budget));
        await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call());
        Assert.Empty(wire.Output.Writes); Assert.False(wire.Client.OutcomeUnknown);
    }

    [Fact]
    public async Task DuplicateHelloTerminal()
    {
        using var wire=new Wire(); wire.Input.Feed(Hello+"\n"+Hello);
        await Assert.ThrowsAsync<ChannelFault>(()=>wire.Client.ConnectAsync(Budget));
        Assert.Empty(wire.Output.Writes);
    }

    [Fact]
    public async Task MissingHelloHasNoSentOperation()
    {
        using var wire=new Wire();
        await Assert.ThrowsAsync<ChannelFault>(()=>wire.Client.ConnectAsync(TimeSpan.FromMilliseconds(40)));
        Assert.Empty(wire.Output.Writes); Assert.False(wire.Client.OutcomeUnknown);
    }

    [Theory]
    [InlineData("{bad}")] [InlineData("")] [InlineData("[]")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"jsonrpc\":\"2.0\",\"id\":1,\"bindingEpochBefore\":0,\"bindingEpochAfter\":0,\"result\":null}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"bindingEpochBefore\":0,\"bindingEpochAfter\":0,\"result\":null,\"error\":{}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"bindingEpochBefore\":0,\"bindingEpochAfter\":0,\"result\":null}")]
    public async Task BadFramingPoisonsBeforeValidation(string frame)
    {
        using var wire=new Wire(); await wire.Connect();
        int validated=0; wire.Output.OnWrite=_=>wire.Input.Feed(frame);
        await Assert.ThrowsAsync<ChannelFault>(async()=>{ await wire.Call(); validated++; });
        Assert.Equal(0,validated); Assert.True(wire.Client.OutcomeUnknown);
        await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call()); Assert.Single(wire.Output.Writes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OversizedLinePoisonsBeforeParsing(bool request)
    {
        using var wire=new Wire(); await wire.Connect();
        if(request)
        {
            await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call("{\"text\":\""+new string('汉',400000)+"\"}"));
            Assert.Empty(wire.Output.Writes); Assert.False(wire.Client.OutcomeUnknown);
        }
        else
        {
            wire.Output.OnWrite=_=>wire.Input.Feed(new string('x',16*1024*1024+1));
            var fault=await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call());
            Assert.Contains("byte limit",fault.Message); Assert.True(fault.OutcomeUnknown);
        }
        Assert.True(wire.Client.Poisoned);
    }

    [Fact]
    public async Task ExactFrameCapAccepted()
    {
        using var wire=new Wire(); await wire.Connect();
        string empty=Reply(payload:"\"result\":\"\"");
        wire.Output.OnWrite=_=>wire.Input.Feed(Reply(payload:"\"result\":\""+new string('x',16*1024*1024-Encoding.UTF8.GetByteCount(empty))+"\""));
        var result=await wire.Call(); Assert.True(result.Length>16_000_000); Assert.False(wire.Client.Poisoned);
    }

    [Fact]
    public async Task CancelBeforeDispatchHasNoSendOrPoison()
    {
        using var wire=new Wire(); await wire.Connect(); using var cts=new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>wire.Call(token:cts.Token));
        Assert.Empty(wire.Output.Writes); Assert.False(wire.Client.Poisoned);
        wire.Output.OnWrite=_=>wire.Input.Feed(Reply()); await wire.Call();
        Assert.Equal(1,JsonNode.Parse(wire.Output.Writes.Single())!["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task ConcurrentEndpointPoisoned()
    {
        using var wire=new Wire(); await wire.Connect(); var first=wire.Call();
        await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call()); await Assert.ThrowsAsync<ChannelFault>(()=>first);
        Assert.True(wire.Client.OutcomeUnknown); Assert.Single(wire.Output.Writes);
    }

    [Theory]
    [InlineData("timeout")] [InlineData("cancel")] [InlineData("pipe")] [InlineData("session")]
    public async Task UnknownResultPoisonsHostNoReplay(string fault)
    {
        using var wire=new Wire(); await wire.Connect(); using var cts=new CancellationTokenSource();
        wire.Output.OnWrite=_=>
        {
            if(fault=="pipe") throw new IOException("broken pipe");
            if(fault=="cancel") cts.Cancel();
            if(fault=="session") wire.Client.Invalidate(new IOException("session fault"));
        };
        await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call(token:cts.Token,timeout:TimeSpan.FromMilliseconds(40)));
        Assert.True(wire.Client.OutcomeUnknown);
        await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call()); Assert.Single(wire.Output.Writes);
    }

    [Theory]
    [InlineData(9,0,0)] [InlineData(0,0,0)] [InlineData(1,1,1)] [InlineData(1,0,1)]
    public async Task UnknownReplyIdOrBindingEpochPoisons(long id,long before,long after)
    {
        using var wire=new Wire(); await wire.Connect(); wire.Output.OnWrite=_=>wire.Input.Feed(Reply(id,before,after));
        await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call()); Assert.True(wire.Client.OutcomeUnknown);
    }

    [Theory]
    [InlineData("hello")] [InlineData("progress")] [InlineData("reply")]
    public async Task LateProgressAndTrailingFramesPoison(string kind)
    {
        using var wire=new Wire(); await wire.Connect(); wire.Output.OnWrite=_=>wire.Input.Feed(Reply()); await wire.Call();
        wire.Input.Feed(kind=="hello" ? Hello : kind=="progress" ? Progress() : Reply());
        await Eventually(()=>wire.Client.Poisoned);
        await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call()); Assert.Single(wire.Output.Writes);
    }

    [Theory]
    [InlineData(2,1,50)] [InlineData(1,2,50)] [InlineData(1,1,101)]
    public async Task InvalidProgressTerminal(long id,int sequence,int percent)
    {
        using var wire=new Wire(); await wire.Connect(); wire.Output.OnWrite=_=>wire.Input.Feed(Progress(id,sequence,percent));
        await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call());
    }

    [Fact]
    public async Task BudgetNeverResetsOnProgressOrCleanup()
    {
        using var wire=new Wire(); await wire.Connect(); wire.Output.OnWrite=_=>wire.Input.Feed(Progress());
        await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call(timeout:TimeSpan.FromMilliseconds(40)));
        Assert.True(wire.Client.OutcomeUnknown);
    }

    [Theory]
    [InlineData("ReadFailed",true,false)] [InlineData("RejectedBeforeNative",false,false)]
    [InlineData("Unknown",false,true)] [InlineData("ReadFailed",false,true)]
    public async Task ReadFailureOutcomePreserved(string outcome,bool readOnly,bool poisoned)
    {
        using var wire=new Wire(); await wire.Connect();
        wire.Output.OnWrite=_=>wire.Input.Feed(Reply(payload:"\"error\":{\"code\":-32603,\"message\":\"failed\",\"data\":{\"outcome\":\""+outcome+"\",\"evidence\":{\"inputFile\":\"生产线\"}}}"));
        if(outcome=="ReadFailed" && !readOnly) await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call(readOnly:readOnly));
        else
        {
            var error=await Assert.ThrowsAsync<ChannelFailure>(()=>wire.Call(readOnly:readOnly));
            Assert.Equal(outcome,error.Outcome.ToString()); Assert.Equal(-32603,error.Code); Assert.Equal("{\"inputFile\":\"生产线\"}",error.EvidenceJson);
        }
        Assert.Equal(poisoned,wire.Client.Poisoned);
        if(!poisoned) { wire.Output.OnWrite=_=>wire.Input.Feed(Reply(2)); await wire.Call(); }
    }

    [Fact]
    public async Task CloseAdvancesBindingEpoch()
    {
        using var wire=new Wire(); await wire.Connect();
        wire.Output.OnWrite=_=>wire.Input.Feed(Reply(after:1)); await wire.Call(change:BindingChange.Advance);
        wire.Output.OnWrite=_=>wire.Input.Feed(Reply(2,1,2)); await wire.Call(change:BindingChange.Advance);
        Assert.Equal(2,wire.Client.BindingEpoch); Assert.Equal(1,JsonNode.Parse(wire.Output.Writes[1])!["bindingEpoch"]!.GetValue<int>());
    }

    [Fact]
    public async Task ProgressAcceptedAndDtoBytesPreserved()
    {
        using var wire=new Wire(); await wire.Connect();
        const string payload="{\"Text\":\"生产线\",\"Number\":1.00,\"Nullable\":null}";
        wire.Output.OnWrite=_=>wire.Input.Feed(Progress()+"\n"+Progress(sequence:2,percent:100)+"\n"+Reply(payload:"\"result\":"+payload));
        Assert.Equal(payload,await wire.Call()); Assert.False(wire.Client.Poisoned);
    }

    internal sealed class Wire : IDisposable
    {
        internal readonly FeedStream Input=new();
        internal readonly CaptureStream Output=new();
        internal readonly ChannelClient Client;
        internal Wire() { Client=new ChannelClient(Input,Output,Identity); }
        internal async Task Connect() { Input.Feed(Hello); await Client.ConnectAsync(Budget); }
        internal Task<string> Call(string args="{}",CancellationToken token=default,TimeSpan? timeout=null,bool readOnly=true,BindingChange change=BindingChange.None)
            => Client.CallAsync("adapter.ReadState",args,change,readOnly,timeout??Budget,token);
        public void Dispose() => Client.Dispose();
    }

    internal sealed class FeedStream : Stream
    {
        private readonly Channel<byte[]> chunks=Channel.CreateUnbounded<byte[]>();
        private byte[] current=Array.Empty<byte>(); private int offset;
        internal void Feed(string line) => chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(line+"\n"));
        public override async Task<int> ReadAsync(byte[] buffer,int index,int count,CancellationToken token)
        {
            if(offset==current.Length) { current=await chunks.Reader.ReadAsync(token); offset=0; }
            int take=Math.Min(count,current.Length-offset); Array.Copy(current,offset,buffer,index,take); offset+=take; return take;
        }
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override bool CanRead=>true; public override bool CanWrite=>false; public override bool CanSeek=>false;
        public override long Length=>throw new NotSupportedException();
        public override long Position { get=>throw new NotSupportedException(); set=>throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }

    internal sealed class CaptureStream : MemoryStream
    {
        internal readonly List<string> Writes=new();
        internal Action<string>? OnWrite;
        public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken token)
        {
            string line=Encoding.UTF8.GetString(buffer,offset,count); Writes.Add(line); OnWrite?.Invoke(line); return Task.CompletedTask;
        }
    }
}
