using TiaMcp.WorkerProtocol;
using System.Reflection;
using System.Text.Json;

try {
int count=0;
void Check(bool value,string label){if(!value)throw new Exception(label);count++;}
void Reject(Action action,string code){try{action();}catch(IdentityViolation ex){Check(ex.Code==code,$"Expected {code}, got {ex.Code}");return;}throw new Exception("Expected rejection: "+code);}
var id=new EngineIdentity(2,"20",new string('a',64),new string('b',64),new string('c',32));
var policies=new[]{new OperationPolicy("BindProject",false,BindingEffect.Bind),new OperationPolicy("CloseProject",true,BindingEffect.Unbind),new OperationPolicy("ReadBlocks",true,readOnly:true),new OperationPolicy("ImportBlocks",true),new OperationPolicy("ListProcesses",false,readOnly:true)};
var project=new ProjectIdentity(new string('d',64),123,DateTime.UtcNow.Ticks);
RequestGuard Host(){var host=new RequestGuard(id,policies);host.AcceptHello(id,BindingSnapshot.Unbound(0));return host;}
Check(!typeof(RequestGuard).Assembly.GetReferencedAssemblies().Any(a=>a.Name!.StartsWith("Siemens",StringComparison.Ordinal)),"pure protocol has no Siemens dependency");
foreach(string key in new[]{"14sp1","15.1","16","17","18","19","20","21"})Check(new EngineIdentity(2,key,id.WorkerSha256,id.EngineSha256,id.SessionId).ReleaseKey==key,"exact release preserved");
foreach(string key in new[]{"14","15","15.10","V20","22",""," 20"})Reject(()=>new EngineIdentity(2,key,id.WorkerSha256,id.EngineSha256,id.SessionId),"ExactReleaseRequired");
Reject(()=>new EngineIdentity(1,"20",id.WorkerSha256,id.EngineSha256,id.SessionId),"ProtocolVersionMismatch");
Reject(()=>new EngineIdentity(2,"20","",id.EngineSha256,id.SessionId),"BinaryIdentityRequired");
Reject(()=>new EngineIdentity(2,"20",id.WorkerSha256,"",id.SessionId),"BinaryIdentityRequired");
Reject(()=>new EngineIdentity(2,"20",id.WorkerSha256,id.EngineSha256,""),"SessionIdentityRequired");
Reject(()=>new ProjectIdentity("",123,1),"ProjectIdentityRequired");
Reject(()=>new ProjectIdentity(project.ProjectSha256,0,1),"TiaProcessIdentityRequired");
Reject(()=>BindingSnapshot.Bound(0,project),"BoundIdentityRequired");
Reject(()=>new RequestIdentity(0,id.SessionId,id,"ReadBlocks",BindingSnapshot.Unbound(0),BindingSnapshot.Unbound(0)),"RequestIdRequired");
Reject(()=>new RequestIdentity(1,"",id,"ReadBlocks",BindingSnapshot.Unbound(0),BindingSnapshot.Unbound(0)),"CorrelationRequired");
var beforeHello=new RequestGuard(id,policies);Reject(()=>beforeHello.Begin("ListProcesses"),"HelloRequired");
foreach(var wrong in new EngineIdentity?[]{null,new EngineIdentity(2,"21",id.WorkerSha256,id.EngineSha256,id.SessionId),new EngineIdentity(2,"20",new string('e',64),id.EngineSha256,id.SessionId),new EngineIdentity(2,"20",id.WorkerSha256,new string('e',64),id.SessionId),new EngineIdentity(2,"20",id.WorkerSha256,id.EngineSha256,new string('e',32))}){
    var host=new RequestGuard(id,policies);Reject(()=>host.AcceptHello(wrong,BindingSnapshot.Unbound(0)),"EngineIdentityMismatch");Check(host.Faulted,"bad hello poisons session");
}
var lostHello=new RequestGuard(id,policies);Reject(()=>lostHello.AcceptHello(id,null),"FreshUnboundHelloRequired");
var inheritedHello=new RequestGuard(id,policies);Reject(()=>inheritedHello.AcceptHello(id,BindingSnapshot.Bound(1,project)),"FreshUnboundHelloRequired");
var h=Host();var wire=new FakeTransport(id,policies);
Reject(()=>h.Begin("ReadBlocks"),"ProjectBindingRequired");
Reject(()=>h.Begin("PasswordFromUnregisteredCaller"),"UnknownOperation");
var cancelled=h.Begin("ListProcesses");h.CancelBeforeDispatch(cancelled);
Check(!h.Faulted && wire.Sends==0,"cancel before dispatch has no send or poison");
await wire.Call(h,"BindProject",project);
await wire.Call(h,"ReadBlocks");
Check(wire.Sends==2 && wire.NativeCalls==2,"single dispatch per operation");
Check(wire.Binding.Epoch==1 && wire.Binding.Project!.Matches(project),"binding transition observed");
Check(wire.Logs.Select(x=>x["correlationId"]).Distinct().Count()==2,"fresh request correlation");
Check(wire.Logs.GroupBy(x=>x["correlationId"]).All(g=>g.Count()==2),"request correlation persists through completion");
Check(!JsonSerializer.Serialize(wire.Logs).Contains(FakeTransport.Credential),"payload credentials absent from logs");
Check(wire.Logs.All(x=>!x.ContainsKey("arguments")&&!x.ContainsKey("exceptionMessage")&&!x.ContainsKey("projectPath")),"log fields allowlisted");
Check(RequestLogContext.Current==null,"context cleared after request");
var duplicate=new WorkerRequestGuard(id,policies);var request=Host().Begin("ListProcesses");duplicate.Accept(request,BindingSnapshot.Unbound(0));duplicate.Finish(BindingSnapshot.Unbound(0),ReplyOutcome.Succeeded);
Reject(()=>duplicate.Accept(request,BindingSnapshot.Unbound(0)),"DuplicateOrStaleRequest");Check(duplicate.Faulted,"duplicate poisons worker");
foreach(string fault in new[]{"wrong-id","missing-identity","wrong-release","wrong-worker-hash","wrong-engine-hash","wrong-session","lost-binding","wrong-epoch","wrong-project","wrong-process-start","write-as-read-failure","timeout-after-dispatch","cancel-after-dispatch","pipe-failure-after-dispatch"}){
    var host=Host();var transport=new FakeTransport(id,policies);await transport.Call(host,"BindProject",project);transport.Fault=fault;
    try{await transport.Call(host,"ImportBlocks");throw new Exception("Fault accepted: "+fault);}catch(IdentityViolation){}catch(IOException){}catch(OperationCanceledException){}catch(TimeoutException){}
    Check(host.Faulted && host.OutcomeUnknown,"unknown result poisons host: "+fault);
    int sent=transport.Sends;Reject(()=>host.Begin("ImportBlocks"),"SessionFaultedNoReplay");Check(transport.Sends==sent,"no retry send: "+fault);
}
foreach(string fault in new[]{"worker-lost-binding","worker-wrong-epoch","worker-wrong-project"}){
    var host=Host();var transport=new FakeTransport(id,policies);await transport.Call(host,"BindProject",project);transport.Fault=fault;int calls=transport.NativeCalls;
    try{await transport.Call(host,"ImportBlocks");throw new Exception("Fault accepted");}catch(IdentityViolation){}
    Check(transport.NativeCalls==calls,"binding mismatch refused before operation: "+fault);Check(host.Faulted,"channel closed after identity loss");
}
var known=Host();var safe=new FakeTransport(id,policies);await safe.Call(known,"BindProject",project);safe.Outcome=ReplyOutcome.ReadFailed;await safe.Call(known,"ReadBlocks");Check(!known.Faulted,"verified read failure keeps session usable");safe.Outcome=ReplyOutcome.RejectedBeforeOperation;await safe.Call(known,"ImportBlocks");Check(!known.Faulted,"verified rejection keeps session usable");
safe.Outcome=ReplyOutcome.Succeeded;await safe.Call(known,"CloseProject");Check(!safe.Binding.IsBound&&safe.Binding.Epoch==2,"close advances binding epoch");Reject(()=>known.Begin("ReadBlocks"),"ProjectBindingRequired");
var scopeRequest=Host().Begin("ListProcesses");var scope=RequestLogContext.Open(scopeRequest);Reject(()=>RequestLogContext.Open(scopeRequest),"NestedRequestContextRefused");
var delayed=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var continuation=Task.Run(async()=>{await delayed.Task;return RequestLogContext.Current;});scope.Dispose();delayed.SetResult(true);Check(await continuation==null,"captured async context invalidated after scope disposal");
Reject(()=>scope.SnapshotFields(),"RequestContextExpired");
Console.WriteLine($"PASS {count}: pure identity/context and fake-transport checks; no process or native execution.");return 0;
}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}

sealed class FakeTransport
{
    public const string Credential="NEVER_LOG_PASSWORD_7x!";
    readonly EngineIdentity engine;readonly WorkerRequestGuard worker;
    public BindingSnapshot Binding=BindingSnapshot.Unbound(0);
    public string Fault="";public ReplyOutcome Outcome=ReplyOutcome.Succeeded;
    public int Sends,NativeCalls;public List<IReadOnlyDictionary<string,string>> Logs=new();
    public FakeTransport(EngineIdentity engine,OperationPolicy[] policies){this.engine=engine;worker=new WorkerRequestGuard(engine,policies);}
    public async Task Call(RequestGuard host,string name,ProjectIdentity? target=null){
        var req=host.Begin(name,target);host.MarkDispatchAttempt(req);Sends++;
        try {
            await Task.Yield(); // In-memory transport only; opaque credential payload never reaches the logger.
            BindingSnapshot? before=Binding;
            if(Fault=="worker-lost-binding")before=null;
            if(Fault=="worker-wrong-epoch")before=BindingSnapshot.Bound(Binding.Epoch+1,Binding.Project!);
            if(Fault=="worker-wrong-project")before=BindingSnapshot.Bound(Binding.Epoch,new ProjectIdentity(new string('f',64),123,Binding.Project!.TiaProcessStartUtcTicks));
            worker.Accept(req,before);
            using(var scope=RequestLogContext.Open(req)){
                Logs.Add(scope.SnapshotFields());if(Outcome!=ReplyOutcome.RejectedBeforeOperation)NativeCalls++;
                if(Fault=="timeout-after-dispatch")throw new TimeoutException(Credential);
                if(Fault=="cancel-after-dispatch")throw new OperationCanceledException(Credential);
                if(Fault=="pipe-failure-after-dispatch")throw new IOException(Credential);
                Binding=Outcome==ReplyOutcome.Succeeded?req.ExpectedAfter:req.Before;
                scope.ObserveCompletion(Binding,Outcome);Logs.Add(scope.SnapshotFields());
                var reply=worker.Finish(Binding,Outcome);
                var changed=req;var after=Binding;var outcome=Outcome;
                if(Fault=="wrong-id")changed=new RequestIdentity(req.RequestId+1,req.CorrelationId,req.Engine,req.Operation,req.Before,req.ExpectedAfter);
                if(Fault.StartsWith("wrong-",StringComparison.Ordinal) && Fault is not ("wrong-id" or "wrong-epoch" or "wrong-project" or "wrong-process-start")){
                    var identity=new EngineIdentity(2,Fault=="wrong-release"?"21":engine.ReleaseKey,Fault=="wrong-worker-hash"?new string('f',64):engine.WorkerSha256,Fault=="wrong-engine-hash"?new string('f',64):engine.EngineSha256,Fault=="wrong-session"?new string('f',32):engine.SessionId);
                    changed=new RequestIdentity(req.RequestId,req.CorrelationId,identity,req.Operation,req.Before,req.ExpectedAfter);
                }
                if(Fault=="lost-binding")after=BindingSnapshot.Unbound(Binding.Epoch);
                if(Fault=="wrong-epoch")after=BindingSnapshot.Bound(Binding.Epoch+1,Binding.Project!);
                if(Fault=="wrong-project")after=BindingSnapshot.Bound(Binding.Epoch,new ProjectIdentity(new string('f',64),123,Binding.Project!.TiaProcessStartUtcTicks));
                if(Fault=="wrong-process-start")after=BindingSnapshot.Bound(Binding.Epoch,new ProjectIdentity(Binding.Project!.ProjectSha256,123,Binding.Project.TiaProcessStartUtcTicks+1));
                if(Fault=="write-as-read-failure")outcome=ReplyOutcome.ReadFailed;
                host.Complete(Fault=="missing-identity"?null:new ReplyIdentity(changed,after,outcome));
            }
        } catch { host.TransportFailed();worker.Fail();throw; }
    }
}
