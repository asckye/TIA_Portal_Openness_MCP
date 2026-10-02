using System.Diagnostics;
using System.Text.Json;
using TiaMcp.WorkerProtocol;
using TiaMcp.WorkerProtocol.AsyncPreview;
using TiaMcp.WorkerProtocol.HostTransport;
using TiaMcp.WorkerProtocol.JsonV2;

int passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; }
async Task Reject(Func<Task> f, string name)
{
    try { await f().WaitAsync(TimeSpan.FromSeconds(8)); }
    catch (Exception e) when (e is IdentityViolation or OperationCanceledException or IOException) { passed++; return; }
    throw new Exception("Accepted: " + name);
}
string dotnet = Environment.ProcessPath!;
if (!Path.GetFileNameWithoutExtension(dotnet).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
    dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? throw new Exception("Run tests with dotnet or set DOTNET_HOST_PATH");
string fixture = args.Single();
var engine = new EngineIdentity(2,"20",new string('a',64),new string('b',64),new string('c',32));
AsyncJsonSession Session() => new(engine,new[] { new OperationPolicy("List",false,readOnly:true) },OuterIdStyle.Numeric);
Task<OwnedPipeTransport> Start(AsyncJsonSession s, string mode, int ms=3000) => OwnedPipeTransport.StartAsync(dotnet,new[] { fixture,mode },s,TimeSpan.FromMilliseconds(ms));
using var doc = JsonDocument.Parse("{}");
Task<ValidatedReply> Call(AsyncJsonSession s, OwnedPipeTransport p, int ms=2000, CancellationToken token=default, JsonElement? a=null, FrameLimits? limits=null)
    => s.CallAsync("List", a ?? doc.RootElement,p.CreateExchange(),_=>{},TimeSpan.FromMilliseconds(ms),token,limits:limits);
foreach (string mode in new[] { "echo", "stderr" })
{
    var s=Session(); var p=await Start(s,mode);
    Check(p.IdentityVerified,"identity verified");
    for(int i=0;i<3;i++) Check((await Call(s,p)).Outcome==ReplyOutcome.Succeeded,"repeated exchange boundary");
    Check(!s.Faulted && !p.HasExited,"session survives success");
    await p.DisposeAsync(); await p.DisposeAsync(); Check(p.HasExited&&p.ShutdownConfirmed,"owned disposal/repeated disposal");
}
foreach(string mode in new[] { "token","pid","release","hash","hello-oversized","startup-exit","startup-hang" })
{
    string marker=Path.Combine(Path.GetTempPath(),"transport-fixture-"+Guid.NewGuid().ToString("N"));
    try
    {
        await Reject(async()=>{ await using var p=await OwnedPipeTransport.StartAsync(dotnet,new[] { fixture,mode,marker },Session(),TimeSpan.FromMilliseconds(750)); },"startup "+mode);
        Check(File.Exists(marker),"startup fixture actually ran: "+mode);
        int pid=int.Parse(await File.ReadAllTextAsync(marker)); bool alive;
        try { using var child=Process.GetProcessById(pid); alive=!child.HasExited; } catch (ArgumentException) { alive=false; }
        Check(!alive,"rejected startup fixture reaped: "+mode);
    }
    finally { File.Delete(marker); }
}
foreach(string mode in new[] { "exit","hang","oversized","negative","truncated","unknown","trailing","no-boundary","partial-header","progress-flood" })
{
    var s=Session(); await using var p=await Start(s,mode); var watch=Stopwatch.StartNew();
    await Reject(()=>Call(s,p,300),mode);
    Check(watch.Elapsed<TimeSpan.FromSeconds(3),mode+" bounded");
    Check(s.Faulted&&s.OutcomeUnknown,mode+" terminal ambiguity");
    await Reject(()=>Call(s,p),mode+" no replay");
    await p.DisposeAsync(); Check(p.HasExited,mode+" owned reaped");
}
{
    var s=Session(); await using var p=await Start(s,"hang"); using var c=new CancellationTokenSource(100);
    await Reject(()=>Call(s,p,3000,c.Token),"read cancellation");
    Check(s.Faulted&&s.OutcomeUnknown,"read cancel no replay"); await p.DisposeAsync(); Check(p.HasExited,"read cancellation cleanup");
}
{
    var s=Session(); await using var p=await Start(s,"no-read");
    using var large=JsonDocument.Parse("{\"data\":\""+new string('x',800_000)+"\"}");
    using var c=new CancellationTokenSource(100);
    await Reject(()=>Call(s,p,3000,c.Token,large.RootElement),"backpressured write cancellation");
    Check(s.Faulted&&s.OutcomeUnknown,"write cancellation no replay"); await p.DisposeAsync(); Check(p.HasExited,"write cancellation cleanup");
}
{
    var s=Session(); await using var p=await Start(s,"echo");
    await Reject(()=>Call(s,p,2000,limits:new FrameLimits(1,1,5)),"payload rejected before read/allocation");
    Check(s.Faulted,"tiny configured frame bound");
}
{
    var s=Session(); await using var p=await Start(s,"ready-exit");
    var wait=Stopwatch.StartNew(); while (!p.HasExited && wait.Elapsed<TimeSpan.FromSeconds(3)) await Task.Delay(10);
    Check(p.HasExited,"peer closed OS read end");
    await Reject(()=>Call(s,p),"actual broken pipe dispatch");
    Check(s.Faulted&&s.OutcomeUnknown,"broken pipe no replay");
}
{
    var s=Session(); await using var p=await Start(s,"echo");
    using var c=new CancellationTokenSource(); c.Cancel();
    await using (var exchange=p.CreateExchange())
        await Reject(()=>s.CallAsync("List",doc.RootElement,exchange,_=>{},TimeSpan.FromSeconds(2),c.Token),"unsent cancellation");
    Check(!s.Faulted&&!s.OutcomeUnknown,"unsent cancellation stays usable");
    await Call(s,p); Check(!s.Faulted,"unsent exchange released");
}
{
    var s=Session(); await using var p=await Start(s,"hang");
    var pending=Call(s,p,3000); await Task.Delay(50); await p.DisposeAsync();
    await Reject(async()=>await pending,"concurrent owner disposal unblocks pipe");
    Check(p.HasExited&&s.Faulted,"concurrent disposal owns terminal exit");
}
// Disposing one owned peer must leave a separately owned peer running and usable.
{
    var s1=Session(); var s2=Session(); await using var a=await Start(s1,"echo"); await using var b=await Start(s2,"echo");
    Check(a.OwnedProcessId!=b.OwnedProcessId,"distinct owned handles");
    await a.DisposeAsync(); Check(a.HasExited&&!b.HasExited,"only owned process killed");
    await Call(s2,b); Check(!s2.Faulted,"other peer usable");
}
Console.WriteLine($"PASS {passed} actual-process/pipe assertions (test fixture only)");
