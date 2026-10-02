using System.Buffers.Binary;
using System.Text.Json;
using TiaMcp.WorkerProtocol;
using TiaMcp.WorkerProtocol.JsonV2;
using TiaMcp.WorkerProtocol.AsyncPreview;

int passed = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); passed++; }
async Task Reject(Func<Task> action, string label)
{
    try { await action().WaitAsync(TimeSpan.FromSeconds(3)); }
    catch (IdentityViolation) { passed++; return; }
    catch (OperationCanceledException) { passed++; return; }
    throw new Exception("Accepted: " + label);
}
var engine = new EngineIdentity(2, "20", new string('a', 64), new string('b', 64), new string('c', 32));
var policies = new[] { new OperationPolicy("List", false, readOnly: true), new OperationPolicy("Write", false),
    new OperationPolicy("Open", false, BindingEffect.Bind), new OperationPolicy("ReadBound", true, readOnly: true),
    new OperationPolicy("Close", true, BindingEffect.Unbind) };
var project = new ProjectIdentity(new string('d', 64), 1, 100);
var hello = StrictCodec.Encode(new HelloFrame(engine, BindingSnapshot.Unbound(0)));
using var doc = JsonDocument.Parse("{}");
var arguments = doc.RootElement;
var timeout = TimeSpan.FromSeconds(10);
AsyncJsonSession Session(TimeProvider? clock = null, OuterIdStyle style = OuterIdStyle.Numeric)
{
    var s = new AsyncJsonSession(engine, policies, style, clock); s.AcceptHello(hello); return s;
}
Task<ValidatedReply> Call(AsyncJsonSession s, ScriptedExchange x, Action<JsonElement>? validate = null,
    CancellationToken token = default, string op = "List", ProjectIdentity? target = null, FrameLimits? limits = null)
    => s.CallAsync(op, arguments, x, validate ?? (_ => { }), timeout, token, target, limits);
byte[] Reply(RequestFrame r, ReplyOutcome outcome = ReplyOutcome.Succeeded, BindingSnapshot? after = null,
    RequestIdentity? identity = null, WireId? id = null, string payload = "{\"ok\":true}")
{
    using var d = JsonDocument.Parse(payload);
    return StrictCodec.Encode(new ReplyFrame(id ?? r.Id,
        new ReplyIdentity(identity ?? r.Identity, after ?? (outcome == ReplyOutcome.Succeeded ? r.Identity.ExpectedAfter : r.Identity.Before), outcome), d.RootElement));
}
ScriptedExchange Echo() => new(r => new[] { Reply(r) });

foreach (var style in Enum.GetValues<OuterIdStyle>())
{
    var s = Session(style: style); var x = Echo(); int callbacks = 0;
    var result = await Call(s, x, r => { Check(r.GetProperty("ok").GetBoolean(), "result valid"); callbacks++; });
    Check(result.Outcome == ReplyOutcome.Succeeded && !s.Faulted && !s.OutcomeUnknown && callbacks == 1, "roundtrip");
    Check(x.Dispatches == 1 && x.Disposals == 1 && x.EnumeratorDisposals == 1 && x.Aborts == 0, "success owned cleanup");
    Check((style == OuterIdStyle.Numeric) == (x.Request!.Id.Number != null), "outer style preserved");
}
{
    var s = Session();
    await Call(s, Echo(), op: "Open", target: project);
    var bound = Echo(); await Call(s, bound, op: "ReadBound");
    Check(bound.Request!.Identity.Before.Project!.Matches(project) && bound.Request.Identity.Before.Epoch == 1, "binding committed");
    await Call(s, Echo(), op: "Close");
    await Reject(() => Call(s, Echo(), op: "ReadBound"), "unbound after close");
    Check(!s.Faulted, "local policy rejection recoverable");
}
foreach (var outcome in new[] { ReplyOutcome.ReadFailed, ReplyOutcome.RejectedBeforeOperation })
{
    var s = Session(); var x = new ScriptedExchange(r => new[] { Reply(r, outcome) });
    Check((await Call(s, x)).Outcome == outcome && !s.Faulted, "known negative outcome usable");
}
{
    var s = Session();
    var x = new ScriptedExchange(r => new[] { StrictCodec.Encode(new ProgressFrame(r.Id, r.Identity, 1, 25)), Reply(r) });
    await Call(s, x); Check(!s.Faulted, "progress accepted");
}
// Every invalid identity/outcome must be rejected before any callback, with no retry.
var badReplies = new Dictionary<string, Func<RequestFrame, byte[][]>>
{
    ["unknown"] = r => new[] { Reply(r, ReplyOutcome.Unknown) },
    ["outer"] = r => new[] { Reply(r, id: new WireId("wrong")) },
    ["binding"] = r => new[] { Reply(r, after: BindingSnapshot.Unbound(9)) },
    ["request"] = r => new[] { Reply(r, identity: new RequestIdentity(r.Identity.RequestId + 1, r.Identity.CorrelationId, engine, "List", r.Identity.Before, r.Identity.ExpectedAfter)) },
    ["correlation"] = r => new[] { Reply(r, identity: new RequestIdentity(r.Identity.RequestId, new string('f',32), engine, "List", r.Identity.Before, r.Identity.ExpectedAfter)) },
    ["operation"] = r => new[] { Reply(r, identity: new RequestIdentity(r.Identity.RequestId, r.Identity.CorrelationId, engine, "Write", r.Identity.Before, r.Identity.ExpectedAfter)) },
    ["engine"] = r => new[] { Reply(r, identity: new RequestIdentity(r.Identity.RequestId, r.Identity.CorrelationId, new EngineIdentity(2,"21",new string('a',64),new string('b',64),new string('c',32)), "List", r.Identity.Before, r.Identity.ExpectedAfter)) },
    ["before"] = r => new[] { Reply(r, identity: new RequestIdentity(r.Identity.RequestId, r.Identity.CorrelationId, engine, "List", BindingSnapshot.Unbound(1), r.Identity.ExpectedAfter)) },
    ["expectedAfter"] = r => new[] { Reply(r, identity: new RequestIdentity(r.Identity.RequestId, r.Identity.CorrelationId, engine, "List", r.Identity.Before, BindingSnapshot.Unbound(1))) },
    ["trailing"] = r => new[] { Reply(r), Reply(r) },
    ["missing"] = r => Array.Empty<byte[]>(),
    ["hello"] = r => new[] { hello },
    ["malformed"] = r => new[] { new byte[] { (byte)'{' } },
    ["v1"] = r => new[] { System.Text.Encoding.UTF8.GetBytes("{\"version\":1,\"result\":{}}") },
    ["progress id"] = r => new[] { StrictCodec.Encode(new ProgressFrame(new WireId("wrong"),r.Identity,1,25)), Reply(r) },
    ["progress duplicate"] = r => new[] { StrictCodec.Encode(new ProgressFrame(r.Id,r.Identity,1,25)), StrictCodec.Encode(new ProgressFrame(r.Id,r.Identity,1,30)), Reply(r) },
    ["progress identity"] = r => new[] { StrictCodec.Encode(new ProgressFrame(r.Id,new RequestIdentity(r.Identity.RequestId+1,r.Identity.CorrelationId,engine,"List",r.Identity.Before,r.Identity.ExpectedAfter),1,25)), Reply(r) },
    ["empty"] = r => new[] { Array.Empty<byte>() },
    ["oversized"] = r => new[] { new byte[StrictCodec.MaxFrameBytes + 1] }
};
foreach (var (label, factory) in badReplies)
{
    var s = Session(); var x = new ScriptedExchange(factory); int callbacks = 0;
    await Reject(() => Call(s, x, _ => callbacks++), label);
    Check(s.Faulted && s.OutcomeUnknown && callbacks == 0 && x.Dispatches == 1 && x.Aborts == 1, label + " terminal before callback");
    var retry = Echo(); await Reject(() => Call(s, retry), label + " replay rejected");
    Check(retry.Dispatches == 0 && x.Disposals == 1 && x.EnumeratorDisposals == 1, label + " no replay and cleanup");
}
{
    var s = Session(); int calls = 0;
    await Reject(() => Call(s, new ScriptedExchange(r => new[] { Reply(r, ReplyOutcome.ReadFailed) }), _ => calls++, op:"Write"), "write read failure");
    Check(calls == 0 && s.OutcomeUnknown, "write read failure not exposed");
}
{
    var s = Session(); var x = Echo();
    await Reject(() => Call(s, x, _ => throw new Exception("secret result body")), "validator fault");
    Check(s.Faulted && s.OutcomeUnknown && x.Disposals == 1, "validation failure terminal");
}
{
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    var s = Session(); var x = Echo();
    await Reject(() => Call(s, x, token:canceled.Token), "pre-cancel");
    Check(!s.Faulted && !s.OutcomeUnknown && x.Dispatches == 0 && x.Disposals == 0, "unsent ownership retained");
    await Call(s, Echo()); Check(!s.Faulted, "unsent cancellation recoverable");
}
{
    var s = Session(); var x = Echo(); using var nonobject = JsonDocument.Parse("[]");
    await Reject(() => s.CallAsync("List", nonobject.RootElement, x, _=>{}, timeout), "encoding fail");
    Check(!s.Faulted && x.Dispatches == 0, "encoding unsent recoverable"); await Call(s,Echo());
}
foreach (var duration in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromHours(2) })
{
    var s=Session(); var x=Echo(); await Reject(()=>s.CallAsync("List", arguments,x,_=>{},duration),"invalid deadline");
    Check(!s.Faulted && x.Dispatches==0,"invalid deadline unsent");
}
foreach (string stage in new[] { "dispatch", "move", "end", "enumeratorDispose", "exchangeDispose" })
{
    var clock = new ManualClock(); var s=Session(clock); var x=Echo(); int calls=0;
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var stalledMove = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    if(stage=="dispatch") x.OnDispatch = () => { started.SetResult(); return new ValueTask(stalled.Task); };
    if(stage=="move" || stage=="end") x.OnMove = n => {
        if(n == (stage=="move" ? 0 : 1)) { started.SetResult(); return new ValueTask<bool>(stalledMove.Task); }
        return new ValueTask<bool>(n==0);
    };
    if(stage=="enumeratorDispose") x.OnEnumeratorDispose = () => { started.SetResult(); return new ValueTask(stalled.Task); };
    if(stage=="exchangeDispose") x.OnDispose = () => { started.SetResult(); return new ValueTask(stalled.Task); };
    // Deliberately ignores cancellation; outer awaits must still be bounded. Abort wakes
    // abandoned operations, demonstrating adapter-owned safe cleanup after cancellation.
    x.OnAbort = () => { stalled.TrySetException(new Exception("late secret")); stalledMove.TrySetException(new Exception("late secret")); };
    var call=Call(s,x,_=>calls++);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
    clock.Advance(timeout);
    await Reject(()=>call,"deadline awaiting "+stage);
    Check(s.Faulted && s.OutcomeUnknown && calls==0 && x.Aborts==1 && x.Disposals==1,"deadline terminal "+stage);
}
foreach (string stage in new[] { "dispatch", "move", "enumeratorDispose", "exchangeDispose", "current", "getEnumerator" })
{
    var s=Session(); var x=Echo(); int calls=0;
    if(stage=="dispatch") x.OnDispatch=()=>throw new Exception("secret dispatch");
    if(stage=="move") x.OnMove=_=>ValueTask.FromException<bool>(new Exception("secret move"));
    if(stage=="enumeratorDispose") x.OnEnumeratorDispose=()=>ValueTask.FromException(new Exception("secret dispose"));
    if(stage=="exchangeDispose") x.OnDispose=()=>ValueTask.FromException(new Exception("secret dispose"));
    if(stage=="current") x.ThrowCurrent=true;
    if(stage=="getEnumerator") x.ThrowGetEnumerator=true;
    try { await Call(s,x,_=>calls++); throw new Exception("accepted fault"); }
    catch(IdentityViolation error) { Check(error.Code=="V2AsyncExchangeFailedNoReplay" && !error.ToString().Contains("secret"),"redacted "+stage); }
    Check(s.Faulted && s.OutcomeUnknown && calls==0 && x.Disposals==1,"fault cleanup "+stage);
}
foreach (string stage in new[] { "dispatch", "reply", "dispose", "callback" })
{
    using var cts=new CancellationTokenSource(); var s=Session(); var x=Echo(); int calls=0;
    if(stage=="dispatch") x.OnDispatch=()=>{ cts.Cancel(); return ValueTask.CompletedTask; };
    if(stage=="reply") x.OnMove=n=>{if(n==1)cts.Cancel(); return new ValueTask<bool>(n==0);};
    if(stage=="dispose") x.OnDispose=()=>{cts.Cancel();return ValueTask.CompletedTask;};
    await Reject(()=>Call(s,x,_=>{calls++;if(stage=="callback")cts.Cancel();},cts.Token),"cancel "+stage);
    Check(s.Faulted && s.OutcomeUnknown && calls==(stage=="callback"?1:0),"cancel poisoned "+stage);
}
foreach (string stage in new[] { "reply", "dispose", "callback" })
{
    var clock=new ManualClock(); var s=Session(clock); var x=Echo(); int calls=0;
    if(stage=="reply")x.OnMove=n=>{if(n==1)clock.Advance(timeout);return new ValueTask<bool>(n==0);};
    if(stage=="dispose")x.OnDispose=()=>{clock.Advance(timeout);return ValueTask.CompletedTask;};
    await Reject(()=>Call(s,x,_=>{calls++;if(stage=="callback")clock.Advance(timeout);}),"late "+stage);
    Check(s.Faulted && s.OutcomeUnknown && calls==(stage=="callback"?1:0),"late no commit "+stage);
}
{
    var clock=new ManualClock();var s=Session(clock);var x=Echo();
    x.OnDispatch=()=>{clock.Rewind(TimeSpan.FromTicks(1));return ValueTask.CompletedTask;};
    await Reject(()=>Call(s,x),"negative clock");Check(s.Faulted&&s.OutcomeUnknown,"negative clock terminal");
}
{
    var clock=new ManualClock();var s=Session(clock);var x=Echo();
    x.OnDispatch=()=>{clock.Advance(TimeSpan.FromSeconds(1));return ValueTask.CompletedTask;};
    x.OnMove=n=>{clock.Rewind(TimeSpan.FromTicks(1));return new ValueTask<bool>(n==0);};
    await Reject(()=>Call(s,x),"backward positive clock");Check(s.Faulted,"clock regression terminal");
}
{
    var s=Session();var x=Echo();var waiting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    x.OnDispatch=()=>new ValueTask(waiting.Task);
    var call=Call(s,x);var concurrent=Echo();
    await Reject(()=>Call(s,concurrent),"single flight");Check(concurrent.Dispatches==0&&!s.Faulted,"concurrent attempt cannot mutate active request");
    waiting.SetResult();await call;Check(!s.Faulted,"original completes");
}
foreach (bool countCap in new[] {true,false})
{
    var s=Session();var x=new ScriptedExchange(r=>Enumerable.Range(1,4).Select(i=>StrictCodec.Encode(new ProgressFrame(r.Id,r.Identity,i,20))).ToArray());
    int sample=StrictCodec.Encode(new ProgressFrame(new WireId(1),new RequestIdentity(1,new string('a',32),engine,"List",BindingSnapshot.Unbound(0),BindingSnapshot.Unbound(0)),1,20)).Length;
    var limits=countCap?new FrameLimits(maxFrames:2):new FrameLimits(maxExchangeBytes:sample+4);
    await Reject(()=>Call(s,x,limits:limits),"incremental cap");
    Check(s.Faulted && x.Moves==(countCap?3:2),"stops at first over-budget live frame");
}
// The reader applies announced lengths before payload allocation and supports fragmented
// async sources. These are byte-source fakes, never OS pipes or worker processes.
byte[] Pack(params byte[][] frames)
{
    using var bytes=new MemoryStream();foreach(var f in frames){byte[] h=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(h,f.Length);bytes.Write(h);bytes.Write(f);}return bytes.ToArray();
}
byte[] Header(int size){byte[] h=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(h,size);return h;}
async Task<int> Read(IAsyncFrameSource source,FrameLimits? limits=null,CancellationToken token=default)
{int frames=0;await foreach(var frame in BoundedAsyncFrames.ReadAsync(source,limits??new FrameLimits(),token)){Check(frame.Length>0,"reader emits owned frame");frames++;}return frames;}
{
    var source=new ByteSource(Pack(new byte[]{1,2,3},new byte[]{4}),1);
    Check(await Read(source)==2 && source.BytesRead==12,"fragmented source");
}
foreach(int size in new[]{-1,0,int.MaxValue,StrictCodec.MaxFrameBytes+1})
{
    var source=new ByteSource(Header(size),4);await Reject(()=>Read(source),"header cap");
    Check(source.BytesRead==4,"bad size rejected before payload read");
}
foreach(var malformed in new[]{new byte[]{1},new byte[]{1,0,0},Pack(new byte[]{1,2})[..5]})
    await Reject(()=>Read(new ByteSource(malformed,1)),"truncated input");
{
    var source=new ByteSource(Pack(new byte[]{1},new byte[]{2}),4);
    await Reject(()=>Read(source,new FrameLimits(maxFrames:1)),"reader frame count");
    Check(source.BytesRead==6,"count cap reads one boundary byte only");
}
{
    var source=new ByteSource(Header(100),4);
    await Reject(()=>Read(source,new FrameLimits(maxExchangeBytes:20)),"reader aggregate cap");
    Check(source.BytesRead==4,"aggregate cap before payload");
}
foreach(int invalid in new[]{-1,2})await Reject(()=>Read(new InvalidSource(invalid)),"source invalid read count");
{
    using var cts=new CancellationTokenSource();cts.Cancel();var source=new ByteSource(new byte[]{1},1);
    await Reject(()=>Read(source,token:cts.Token),"reader cancellation");Check(source.BytesRead==0,"reader pre-cancel no IO");
}
// Exercise the exact bounded byte-reader through the async session, not only separately.
{
    var s=Session();var x=new ByteExchange(r=>Pack(Reply(r)));
    await s.CallAsync("List",arguments,x,_=>{},timeout);
    Check(!s.Faulted && x.Disposals==1 && x.BytesRead>4,"framed byte-source session roundtrip");
}
{
    var s=Session();var x=new ByteExchange(_=>Header(int.MaxValue));int calls=0;
    await Reject(()=>s.CallAsync("List",arguments,x,_=>calls++,timeout),"framed adapter oversized header");
    Check(s.OutcomeUnknown && calls==0 && x.BytesRead==4 && x.Disposals==1,"adapter rejects before allocation");
}
{
    using var cts=new CancellationTokenSource();var s=Session();var x=Echo();
    var stalled=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    x.OnMove=_=>new ValueTask<bool>(stalled.Task);x.OnAbort=()=>stalled.TrySetCanceled();
    var call=Call(s,x,token:cts.Token);cts.Cancel();
    await Reject(()=>call,"caller cancellation while awaiting uncooperative move");
    Check(s.Faulted && x.Aborts==1 && x.Disposals==1,"pending canceled IO aborts and disposes");
}
{
    var s=Session();var x=Echo();var stalled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    x.OnDispatch=()=>new ValueTask(stalled.Task);x.OnAbort=()=>stalled.TrySetCanceled();
    await Reject(()=>s.CallAsync("List",arguments,x,_=>{},TimeSpan.FromMilliseconds(100)),"system timer deadline");
    Check(s.OutcomeUnknown && x.Dispatches==1 && x.Aborts==1,"real timer bounds uncooperative dispatch");
}
{
    var clock=new SequenceClock(0,0,timeout.Ticks);var s=Session(clock);var x=Echo();
    await Reject(()=>Call(s,x),"deadline during encoding before dispatch");
    Check(!s.Faulted && !s.OutcomeUnknown && x.Dispatches==0 && x.Disposals==0,"encoding deadline unsent");
    var next=Echo();await Call(s,next);Check(next.Request!.Identity.RequestId==2,"canceled pending cleared without replay");
}
{
    var clock=new SequenceClock(0,-1);var s=Session(clock);var x=Echo();
    await Reject(()=>Call(s,x),"negative clock before dispatch");
    Check(!s.Faulted && x.Dispatches==0,"negative initial clock unsent");
}
{
    var s=Session();var x=Echo();x.OnMove=_=>throw new Exception("secret read");x.OnAbort=()=>throw new Exception("secret abort");
    await Reject(()=>Call(s,x),"abort implementation fault");
    Check(s.OutcomeUnknown && x.Disposals==1 && x.EnumeratorDisposals==1,"abort fault still attempts cleanup");
}
foreach(var invalid in new Func<FrameLimits>[] {()=>new(maxFrames:0),()=>new(maxFrames:33),()=>new(maxFrameBytes:0),()=>new(maxFrameBytes:StrictCodec.MaxFrameBytes+1),()=>new(maxExchangeBytes:4),()=>new(maxExchangeBytes:FrameLimits.HardMaxExchangeBytes+1)})
    await Reject(()=>{invalid();return Task.CompletedTask;},"invalid limits");
{
    var s=new AsyncJsonSession(engine,policies,OuterIdStyle.Numeric);
    await Reject(()=>{s.AcceptHello(System.Text.Encoding.UTF8.GetBytes("{\"version\":1}"));return Task.CompletedTask;},"v1 hello cannot downgrade");
    var x=Echo();await Reject(()=>Call(s,x),"bad hello terminal");Check(s.Faulted && !s.OutcomeUnknown && x.Dispatches==0,"handshake failed before writes");
}
{
    var clock=new ManualClock();var s=Session(clock);var x=Echo();int callbacks=0;
    x.OnDispatch=()=>{clock.Advance(TimeSpan.FromSeconds(4));return ValueTask.CompletedTask;};
    x.OnMove=n=>{clock.Advance(TimeSpan.FromSeconds(n==0?3:2));return new ValueTask<bool>(n==0);};
    x.OnEnumeratorDispose=()=>{clock.Advance(TimeSpan.FromSeconds(2));return ValueTask.CompletedTask;};
    await Reject(()=>Call(s,x,_=>callbacks++),"one cumulative budget across stages");
    Check(s.OutcomeUnknown && callbacks==0 && x.Disposals==1,"budget never resets on progress or cleanup");
}
{
    var clock=new ManualClock();var s=Session(clock);var x=Echo();int callbacks=0;
    var stalled=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    x.OnMove=_=>new ValueTask<bool>(stalled.Task); // Broken Abort deliberately leaves IO pending.
    var call=Call(s,x,_=>callbacks++);clock.Advance(timeout);
    await Reject(()=>call,"noncooperative abort cannot delay caller forever");
    Check(!stalled.Task.IsCompleted && s.OutcomeUnknown && callbacks==0,"bounded return does not claim adapter resource cleanup");
    stalled.SetException(new Exception("late failure after caller returned"));
    await Task.Yield();Check(callbacks==0 && s.Faulted,"late IO cannot commit or deliver callback");
}
Console.WriteLine($"{passed} passed, 0 failed, 0 skipped.");

sealed class ScriptedExchange(Func<RequestFrame,byte[][]> frames) : IAsyncV2Exchange, IAsyncEnumerable<ReadOnlyMemory<byte>>
{
    public int Dispatches,Disposals,EnumeratorDisposals,Aborts,Moves;
    public bool ThrowCurrent,ThrowGetEnumerator;
    public Func<ValueTask>? OnDispatch,OnDispose,OnEnumeratorDispose;
    public Func<int,ValueTask<bool>>? OnMove;
    public Action? OnAbort;
    public RequestFrame? Request;
    byte[][] response=Array.Empty<byte[]>();int index=-1;
    public ValueTask DispatchAsync(ReadOnlyMemory<byte> request,CancellationToken token)
    {Dispatches++;Request=(RequestFrame)StrictCodec.Decode(request);response=frames(Request);return OnDispatch?.Invoke()??ValueTask.CompletedTask;}
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadFramesAsync(FrameLimits limits,CancellationToken token)=>this;
    public IAsyncEnumerator<ReadOnlyMemory<byte>> GetAsyncEnumerator(CancellationToken token=default)
    {if(ThrowGetEnumerator)throw new Exception("secret enumerator");return new Enumerator(this);}
    public ReadOnlyMemory<byte> Current=>ThrowCurrent?throw new Exception("secret current"):response[index];
    public ValueTask<bool> MoveNextAsync(){Moves++;index++;return OnMove?.Invoke(index)??new ValueTask<bool>(index<response.Length);}
    ValueTask IAsyncDisposable.DisposeAsync(){Disposals++;return OnDispose?.Invoke()??ValueTask.CompletedTask;}
    sealed class Enumerator(ScriptedExchange owner):IAsyncEnumerator<ReadOnlyMemory<byte>>
    {
        public ReadOnlyMemory<byte> Current=>owner.Current;
        public ValueTask<bool> MoveNextAsync()=>owner.MoveNextAsync();
        public ValueTask DisposeAsync(){owner.EnumeratorDisposals++;return owner.OnEnumeratorDispose?.Invoke()??ValueTask.CompletedTask;}
    }
    public void Abort(){Aborts++;OnAbort?.Invoke();}
}
sealed class ByteSource(byte[] bytes,int fragment) : IAsyncFrameSource
{
    public int BytesRead;
    public async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token)
    {await Task.Yield();token.ThrowIfCancellationRequested();int n=Math.Min(Math.Min(buffer.Length,fragment),bytes.Length-BytesRead);bytes.AsMemory(BytesRead,n).CopyTo(buffer);BytesRead+=n;return n;}
}
sealed class InvalidSource(int count):IAsyncFrameSource
{public ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token)=>new(count);}
sealed class ManualClock : TimeProvider
{
    long timestamp;readonly List<ManualTimer> timers=new();
    public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    public override long GetTimestamp()=>timestamp;
    public void Rewind(TimeSpan amount)=>timestamp-=amount.Ticks;
    public void Advance(TimeSpan amount){timestamp+=amount.Ticks;foreach(var timer in timers.ToArray())timer.Fire(timestamp);}
    public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
    {var timer=new ManualTimer(this,callback,state,dueTime);timers.Add(timer);return timer;}
    sealed class ManualTimer(ManualClock clock,TimerCallback callback,object? state,TimeSpan due):ITimer
    {
        long at=clock.timestamp+due.Ticks;bool disposed;
        public bool Change(TimeSpan dueTime,TimeSpan period){at=clock.timestamp+dueTime.Ticks;return !disposed;}
        public void Dispose()=>disposed=true;
        public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
        public void Fire(long now){if(!disposed && now>=at){disposed=true;callback(state);}}
    }
}

sealed class ByteExchange(Func<RequestFrame,byte[]> response):IAsyncV2Exchange
{
    ByteSource? source;public int Disposals;public int BytesRead=>source?.BytesRead??0;
    public ValueTask DispatchAsync(ReadOnlyMemory<byte> request,CancellationToken token)
    {if(source!=null)throw new IdentityViolation("ExchangeAlreadyAttempted");source=new ByteSource(response((RequestFrame)StrictCodec.Decode(request)),1);return ValueTask.CompletedTask;}
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadFramesAsync(FrameLimits limits,CancellationToken token)=>BoundedAsyncFrames.ReadAsync(source!,limits,token);
    public ValueTask DisposeAsync(){Disposals++;return ValueTask.CompletedTask;}
    public void Abort(){}
}
sealed class SequenceClock(params long[] values):TimeProvider
{
    int index;
    public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    public override long GetTimestamp()=>values[Math.Min(index++,values.Length-1)];
}
