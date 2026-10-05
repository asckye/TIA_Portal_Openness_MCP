using System.Text;
using TiaMcp.WorkerChannel;
using Xunit;
using static TiaMcp.WorkerChannel.Tests.ProtocolTests;

namespace TiaMcp.WorkerChannel.Tests;

public sealed class ServerTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EmitFailureTerminal(bool oversized)
    {
        using var output=new FailingOutput(!oversized); int calls=0;
        var server=Server(output,_=>{calls++;return ChannelResponse.Success(oversized ? "\""+new string('x',16*1024*1024)+"\"" : "null");});
        server.WriteHello(); long before=output.Length;
        var failure=Assert.Throws<ChannelFault>(()=>server.Handle(Encoding.UTF8.GetBytes(Request())));
        Assert.True(failure.OutcomeUnknown); Assert.True(server.Poisoned); Assert.Equal(1,calls); Assert.Equal(before,output.Length);
        Assert.Throws<ChannelFault>(()=>server.Handle(Encoding.UTF8.GetBytes(Request(2)))); Assert.Equal(1,calls);
    }

    [Theory]
    [InlineData(0,true)] [InlineData(1,false)]
    public void NonfreshHello(long epoch,bool bound)
    {
        using var output=new MemoryStream(); int calls=0;
        var server=new ChannelServer(Stream.Null,output,Identity,()=>new ChannelBinding(epoch,bound),_=>{calls++;return ChannelResponse.Success("null");});
        Assert.Throws<ChannelFault>(server.WriteHello); Assert.True(server.Poisoned); Assert.Equal(0,calls); Assert.Equal(0,output.Length);
    }

    [Fact]
    public void NoHelloTerminal()
    {
        using var output=new MemoryStream(); int calls=0;
        var server=new ChannelServer(Stream.Null,output,Identity,()=>new ChannelBinding(0,false),_=>{calls++;return ChannelResponse.Success("null");});
        Assert.Throws<ChannelFault>(()=>server.Handle(Encoding.UTF8.GetBytes(Request()))); Assert.Equal(0,calls);
        Assert.Throws<ChannelFault>(server.WriteHello);
    }

    [Fact]
    public void DuplicateHelloTerminal()
    {
        using var output=new MemoryStream(); var server=Server(output); server.WriteHello();
        Assert.Throws<ChannelFault>(server.WriteHello); Assert.True(server.Poisoned);
    }

    [Theory]
    [InlineData("{bad}")] [InlineData("[]")] [InlineData("{}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"Id\":1,\"method\":\"adapter.ReadState\",\"params\":{},\"bindingEpoch\":0}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"adapter.ReadState\",\"params\":{},\"bindingEpoch\":1}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"adapter.ReadState\",\"params\":[],\"bindingEpoch\":0}")]
    public void BadFramingPoisonsBeforeValidation(string frame)
    {
        using var output=new MemoryStream(); int calls=0;
        var server=Server(output,_=>{calls++;return ChannelResponse.Success("null");}); server.WriteHello();
        Assert.Throws<ChannelFault>(()=>server.Handle(Encoding.UTF8.GetBytes(frame)));
        Assert.Throws<ChannelFault>(()=>server.Handle(Encoding.UTF8.GetBytes(Request(2)))); Assert.Equal(0,calls);
    }

    [Fact]
    public void DuplicatePoisonsWorker()
    {
        using var output=new MemoryStream(); int calls=0;
        var server=Server(output,_=>{calls++;return ChannelResponse.Success("null");}); server.WriteHello();
        server.Handle(Encoding.UTF8.GetBytes(Request(5)));
        Assert.Throws<ChannelFault>(()=>server.Handle(Encoding.UTF8.GetBytes(Request(4)))); Assert.Equal(1,calls);
    }

    [Theory]
    [InlineData("oversized")] [InlineData("truncated")] [InlineData("utf8")] [InlineData("bom")]
    public void RequestByteCapAndFramingBeforeDispatch(string kind)
    {
        byte[] bytes=kind=="oversized" ? Encoding.UTF8.GetBytes(new string('x',1024*1024+1)) :
            kind=="truncated" ? Encoding.UTF8.GetBytes(Request()) : kind=="utf8" ? new byte[]{255,10} : Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Request()+"\n")).ToArray();
        using var input=new MemoryStream(bytes); using var output=new MemoryStream(); int calls=0;
        var server=new ChannelServer(input,output,Identity,()=>new ChannelBinding(0,false),_=>{calls++;return ChannelResponse.Success("null");});
        Assert.Throws<ChannelFault>(server.Run); Assert.Equal(0,calls);
    }

    [Fact]
    public void LateProgressTerminal()
    {
        using var output=new MemoryStream(); Action<int>? retained=null;
        var server=Server(output,r=>{retained=r.Progress;return ChannelResponse.Success("null");}); server.WriteHello();
        server.Handle(Encoding.UTF8.GetBytes(Request())); long length=output.Length;
        Assert.Throws<ChannelFault>(()=>retained!(50)); Assert.True(server.Poisoned); Assert.Equal(length,output.Length);
    }

    [Theory]
    [InlineData("reentrant")] [InlineData("wrong-thread")] [InlineData("invalid-progress")] [InlineData("progress-cap")]
    public void SwallowedInvalidProgressAndReentryTerminal(string kind)
    {
        using var output=new MemoryStream(); ChannelServer? server=null;
        server=Server(output,r=>
        {
            if(kind=="reentrant") Assert.Throws<ChannelFault>(()=>server!.Handle(Encoding.UTF8.GetBytes(Request(2))));
            if(kind=="wrong-thread") { var thread=new Thread(()=>Assert.Throws<ChannelFault>(()=>r.Progress(50))); thread.Start(); thread.Join(); }
            if(kind=="invalid-progress") Assert.Throws<ChannelFault>(()=>r.Progress(101));
            if(kind=="progress-cap") { for(int i=0;i<1024;i++) r.Progress(50); Assert.Throws<ChannelFault>(()=>r.Progress(50)); }
            return ChannelResponse.Success("null");
        }); server.WriteHello();
        Assert.Throws<ChannelFault>(()=>server.Handle(Encoding.UTF8.GetBytes(Request()))); Assert.True(server.Poisoned);
    }

    [Fact]
    public void ObserverTerminal()
    {
        using var output=new MemoryStream(); int observed=0;
        var server=new ChannelServer(Stream.Null,output,Identity,()=>++observed==3?throw new IOException("observer failed"):new ChannelBinding(0,false),_=>ChannelResponse.Success("null"));
        server.WriteHello(); Assert.Throws<ChannelFault>(()=>server.Handle(Encoding.UTF8.GetBytes(Request()))); Assert.True(server.Poisoned);
        Assert.Single(Encoding.UTF8.GetString(output.ToArray()).Split('\n',StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void OwningThreadAndRawDtoArePreserved()
    {
        const string payload="{\"Value\":1.00,\"Text\":\"生产线\",\"Null\":null}";
        using var input=new MemoryStream(Encoding.UTF8.GetBytes(Request()+"\n")); using var output=new MemoryStream();
        int owner=Environment.CurrentManagedThreadId, calls=0;
        var server=new ChannelServer(input,output,Identity,()=>{ Assert.Equal(owner,Environment.CurrentManagedThreadId);return new ChannelBinding(0,false); },r=>
        {
            calls++; Assert.Equal(owner,Environment.CurrentManagedThreadId); Assert.Equal("adapter.ReadState",r.Method);
            r.Progress(20); return ChannelResponse.Success(payload);
        }); server.Run(); Assert.Equal(1,calls); Assert.Contains("\"result\":"+payload,Encoding.UTF8.GetString(output.ToArray()));
    }

    private static ChannelServer Server(Stream output,Func<ChannelRequest,ChannelResponse>? dispatch=null)
        =>new(Stream.Null,output,Identity,()=>new ChannelBinding(0,false),dispatch??(_=>ChannelResponse.Success("null")));

    private sealed class FailingOutput(bool fail) : MemoryStream
    {
        public override void Write(byte[] bytes,int offset,int count)
        {
            if(fail && Length>0) throw new IOException("broken reply pipe");
            base.Write(bytes,offset,count);
        }
    }
}
