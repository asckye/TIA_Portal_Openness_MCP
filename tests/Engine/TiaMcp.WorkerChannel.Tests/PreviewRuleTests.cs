using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using static TiaMcp.WorkerChannel.Tests.ProtocolTests;

namespace TiaMcp.WorkerChannel.Tests;

public sealed class PreviewRuleTests
{
    private const string Error = "\"error\":{\"code\":-32602,\"message\":\"failed\",\"data\":{\"outcome\":\"ReadFailed\",\"evidence\":null}}";

    public static IEnumerable<object[]> InvalidEnvelopes()
    {
        foreach(var (kind,json,paths) in new[] {
            ("hello",Hello,new[]{"","params"}), ("request",Request(),new[]{""}),
            ("reply",Reply(),new[]{""}), ("error",Reply(payload:Error),new[]{"","error","error.data"}),
            ("progress",Progress(),new[]{"","params"}) })
        foreach(string path in paths)
        {
            JsonObject Target(JsonNode root) { foreach(string key in path.Split('.',StringSplitOptions.RemoveEmptyEntries)) root=root[key]!; return root.AsObject(); }
            var original=JsonNode.Parse(json)!;
            foreach(string field in Target(original).Select(p=>p.Key).ToArray())
            {
                var missing=original.DeepClone(); Target(missing).Remove(field);
                yield return new object[]{kind,path+" missing "+field,missing.ToJsonString()};
                var ambiguous=original.DeepClone(); Target(ambiguous)[field.ToUpperInvariant()]=Target(original)[field]?.DeepClone();
                yield return new object[]{kind,path+" ambiguous "+field,ambiguous.ToJsonString()};
            }
            var extra=original.DeepClone(); Target(extra)["unexpected"]=true;
            yield return new object[]{kind,path+" unknown",extra.ToJsonString()};
            string compact=original.ToJsonString(), nested=Target(original).ToJsonString();
            string first=Target(original).First().Key;
            yield return new object[]{kind,path+" duplicate",compact.Replace(nested,nested.Insert(1,"\""+first+"\":null,"))};
        }
        foreach(string number in new[]{"1.0","1e0","9223372036854775808","-1","0","\"1\"","null","true"})
        {
            yield return new object[]{"request","id "+number,Request().Replace("\"id\":1","\"id\":"+number)};
            yield return new object[]{"reply","id "+number,Reply().Replace("\"id\":1","\"id\":"+number)};
            yield return new object[]{"progress","sequence "+number,Progress().Replace("\"sequence\":1","\"sequence\":"+number)};
        }
        foreach(string kind in new[]{"request","reply","progress"})
            yield return new object[]{kind,"downgrade",(kind=="request"?Request():kind=="reply"?Reply():Progress()).Replace("\"2.0\"","\"1.0\"")};
        foreach(string number in new[]{"-1","101","9223372036854775807","1.0","1e0","\"1\"","null","true"})
            yield return new object[]{"progress","percent "+number,Progress().Replace("\"percent\":50","\"percent\":"+number)};
        foreach(string payload in new[]{"\"error\":\"bad\"", "\"result\":null,\"error\":{}",
            Error.Replace("-32602","-32000"), Error.Replace("ReadFailed","read-failed"), Error.Replace("\"evidence\":null","\"evidence\":[]")})
            yield return new object[]{"error",payload,Reply(payload:payload)};
    }

    [Theory]
    [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")] [InlineData("17")]
    [InlineData("18")] [InlineData("19")] [InlineData("20")] [InlineData("21")]
    public async Task ExactReleaseHelloRoundTrip(string release)
    {
        Assert.DoesNotContain(typeof(ChannelClient).Assembly.GetReferencedAssemblies(),a=>a.Name!.StartsWith("Siemens",StringComparison.Ordinal));
        var identity=new ChannelIdentity(release,Identity.WorkerSha256,Identity.AdapterSha256,Identity.ProcessId,Identity.Nonce);
        using var encoded=new MemoryStream();
        new ChannelServer(Stream.Null,encoded,identity,()=>new ChannelBinding(0,false),_=>ChannelResponse.Success("null")).WriteHello();
        using var input=new FeedStream(); using var output=new CaptureStream(); using var client=new ChannelClient(input,output,identity);
        input.Feed(Encoding.UTF8.GetString(encoded.ToArray()).TrimEnd('\n'));
        await client.ConnectAsync(Budget); Assert.False(client.Poisoned); Assert.Empty(output.Writes);
    }

    [Theory]
    [InlineData("release")] [InlineData("workerHash")] [InlineData("adapterHash")] [InlineData("pid")] [InlineData("nonce")]
    public void LaunchIdentityRequiresCompleteFields(string field)
    {
        Assert.Throws<ArgumentException>(()=>new ChannelIdentity(field=="release"?"":Identity.ReleaseKey,
            field=="workerHash"?"":Identity.WorkerSha256,field=="adapterHash"?"":Identity.AdapterSha256,
            field=="pid"?0:Identity.ProcessId,field=="nonce"?"":Identity.Nonce));
    }

    [Theory]
    [MemberData(nameof(InvalidEnvelopes))]
    public async Task StrictEnvelopeRejectedBeforeDispatchOrResult(string kind,string rule,string frame)
    {
        Assert.NotEmpty(rule);
        if(kind=="request")
        {
            using var output=new MemoryStream(); int calls=0;
            var server=new ChannelServer(Stream.Null,output,Identity,()=>new ChannelBinding(0,false),_=>{calls++;return ChannelResponse.Success("null");});
            server.WriteHello();
            Assert.Throws<ChannelFault>(()=>server.Handle(Encoding.UTF8.GetBytes(frame)));
            Assert.Throws<ChannelFault>(()=>server.Handle(Encoding.UTF8.GetBytes(Request(2)))); Assert.Equal(0,calls);
        }
        else
        {
            using var wire=new Wire();
            if(kind=="hello")
            {
                wire.Input.Feed(frame);
                await Assert.ThrowsAsync<ChannelFault>(()=>wire.Client.ConnectAsync(Budget)); Assert.Empty(wire.Output.Writes);
                Assert.False(wire.Client.OutcomeUnknown);
            }
            else
            {
                await wire.Connect(); wire.Output.OnWrite=_=>wire.Input.Feed(frame);
                await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call()); Assert.True(wire.Client.OutcomeUnknown);
                Assert.Single(wire.Output.Writes);
            }
            await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call());
        }
    }

    [Fact]
    public async Task ExplicitNullAndReadErrorCodePreserved()
    {
        using var wire=new Wire(); await wire.Connect();
        wire.Output.OnWrite=_=>wire.Input.Feed(Reply()); Assert.Equal("null",await wire.Call());
        wire.Output.OnWrite=_=>wire.Input.Feed(Reply(2,payload:Error));
        var error=await Assert.ThrowsAsync<ChannelFailure>(()=>wire.Call());
        Assert.Equal(-32602,error.Code); Assert.Equal("failed",error.Message); Assert.Equal("null",error.EvidenceJson);
        Assert.False(wire.Client.Poisoned);
    }

    [Theory]
    [InlineData("ReadFailed")] [InlineData("RejectedBeforeNative")]
    public async Task KnownFailureCannotChangeBindingEpoch(string outcome)
    {
        using var wire=new Wire(); await wire.Connect();
        wire.Output.OnWrite=_=>wire.Input.Feed(Reply(after:1,payload:Error.Replace("ReadFailed",outcome)));
        await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call(change:BindingChange.Advance));
        Assert.True(wire.Client.OutcomeUnknown); Assert.Equal(0,wire.Client.BindingEpoch);
    }

    [Fact]
    public async Task ProgressCapAndPerRequestSequence()
    {
        using var wire=new Wire(); await wire.Connect();
        wire.Output.OnWrite=_=>wire.Input.Feed(string.Join("\n",Enumerable.Range(1,1024).Select(i=>Progress(sequence:i)))+"\n"+Reply());
        await wire.Call();
        wire.Output.OnWrite=_=>wire.Input.Feed(Progress(2)+"\n"+Reply(2)); await wire.Call();
        wire.Output.OnWrite=_=>wire.Input.Feed(string.Join("\n",Enumerable.Range(1,1025).Select(i=>Progress(3,i))));
        await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call());
        await Assert.ThrowsAsync<ChannelFault>(()=>wire.Call()); Assert.Equal(3,wire.Output.Writes.Count);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UncooperativeWriteIsBoundedAndNeverReplayed(bool cancel)
    {
        using var input=new FeedStream(); using var output=new StalledOutput();
        using var client=new ChannelClient(input,output,Identity); using var cts=new CancellationTokenSource();
        input.Feed(Hello); await client.ConnectAsync(Budget);
        var pending=client.CallAsync("adapter.ReadState","{}",BindingChange.None,true,cancel?Budget:TimeSpan.FromMilliseconds(40),cts.Token);
        if(cancel) cts.Cancel();
        await Assert.ThrowsAsync<ChannelFault>(()=>pending.WaitAsync(Budget));
        Assert.False(output.Completion.Task.IsCompleted); Assert.True(client.OutcomeUnknown);
        output.Completion.SetException(new IOException("late pipe failure"));
        await Assert.ThrowsAsync<ChannelFault>(()=>client.CallAsync("adapter.ReadState","{}",BindingChange.None,true,Budget));
        Assert.Equal(1,output.Writes);
    }

    [Theory]
    [InlineData("observe")] [InlineData("emit")] [InlineData("progress-emit")]
    public void SwallowedReentryAndProgressEmissionFailureAreTerminal(string stage)
    {
        using var output=new CallbackOutput(); ChannelServer? server=null; int observations=0,calls=0;
        server=new ChannelServer(Stream.Null,output,Identity,()=>
        {
            if(++observations==2 && stage=="observe") Assert.Throws<ChannelFault>(()=>server!.Handle(Encoding.UTF8.GetBytes(Request(2))));
            return new ChannelBinding(0,false);
        },r=>{calls++;if(stage=="progress-emit") Assert.Throws<ChannelFault>(()=>r.Progress(50));return ChannelResponse.Success("null");});
        server.WriteHello();
        output.Callback=()=>
        {
            if(stage=="emit") Assert.Throws<ChannelFault>(()=>server.Handle(Encoding.UTF8.GetBytes(Request(2))));
            if(stage=="progress-emit") throw new IOException("progress pipe failed");
        };
        Assert.Throws<ChannelFault>(()=>server.Handle(Encoding.UTF8.GetBytes(Request())));
        Assert.True(server.Poisoned); Assert.Equal(stage=="observe"?0:1,calls);
        Assert.Throws<ChannelFault>(()=>server.Handle(Encoding.UTF8.GetBytes(Request(3))));
    }

    [Fact]
    public void FragmentedUtf8RequestAndExactByteCap()
    {
        string empty=Request().Replace("\"params\":{}","\"params\":{\"text\":\"生产线😀\"}");
        string request=empty.Replace("生产线😀","生产线😀"+new string('x',1024*1024-Encoding.UTF8.GetByteCount(empty)));
        using var input=new FragmentedInput(Encoding.UTF8.GetBytes(request+"\n")); using var output=new MemoryStream(); int calls=0;
        var server=new ChannelServer(input,output,Identity,()=>new ChannelBinding(0,false),r=>
        { calls++;Assert.StartsWith("生产线😀",JsonNode.Parse(r.ArgumentsJson)!["text"]!.GetValue<string>());return ChannelResponse.Success("null"); });
        server.Run(); Assert.Equal(1,calls); Assert.False(server.Poisoned);
    }

    private sealed class StalledOutput : MemoryStream
    {
        internal readonly TaskCompletionSource<bool> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Writes;
        public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken token) { Writes++;return Completion.Task; }
    }
    private sealed class CallbackOutput : MemoryStream
    {
        internal Action? Callback;
        public override void Write(byte[] buffer,int offset,int count) { Callback?.Invoke();base.Write(buffer,offset,count); }
    }
    private sealed class FragmentedInput(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer,int offset,int count)=>base.Read(buffer,offset,Math.Min(count,7));
    }
}
