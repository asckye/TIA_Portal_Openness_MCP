using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.LegacyHost;
using TiaMcp.PlcFoundation;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

internal static class DisconnectTests
{
    internal static async Task Run(IMcpServer server,Action<bool,string> check,[System.Runtime.CompilerServices.CallerFilePath] string sourceFile="")
    {
        void Reject(Action action,string title) { try { action(); throw new Exception("Accepted: "+title); } catch(Exception ex) when(ex is InvalidOperationException or IOException or NotSupportedException) { check(true,title); } }
        foreach(var bound in new[]{false,true})
        {
            var lifecycle=new PlcLifecycleState(); lifecycle.Attached(123);
            if(bound) lifecycle.Bound("C:/Projects/Test.ap17",false,false);
            var state=new PlcDisconnectState(); int detaches=0;
            var result=state.Execute(lifecycle.ProcessId,false,()=>detaches++);
            lifecycle.Detached();
            check(result.Detached && result.ProcessId==123 && result.WorkerAcknowledged && !result.SavedProject && !result.ClosedProject,$"Acknowledged non-owning detach bound={bound}");
            check(detaches==1 && lifecycle.ProjectFile==null,"No project prerequisite or save/close callback");
            check(ReferenceEquals(state.Execute(null,null,()=>detaches++),result) && detaches==1,"Repeated disconnect never calls native again");
            Reject(state.RequireActive,"Subsequent explicit attach requires new session");
            DisconnectContract.Validate(JsonSerializer.SerializeToNode(result),true,123,true);
        }
        foreach(bool? ownership in new bool?[]{true,null})
        {
            var state=new PlcDisconnectState(); int calls=0;
            Reject(()=>state.Execute(123,ownership,()=>calls++),"Owned/unknown portal semantics gate");
            check(!state.Attempted && calls==0,"Ownership rejection precedes native disposal");
        }
        var unbound=new PlcLifecycleState();unbound.Attached(123);unbound.Bound("C:/Projects/Test.ap17",true,false);unbound.Unbound();
        check(new PlcDisconnectState().Execute(unbound.ProcessId,false,()=>{}).Detached,"Explicitly unbound attachment can disconnect");
        var engine=File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!,"../../src/TiaMcpServer.PlcFoundation/PlcFoundationEngine.cs")));
        var disconnectSource=engine.Split("public PlcDisconnectResult Disconnect()")[1].Split("public void Dispose()")[0];
        check(!disconnectSource.Contains("Project()") && !disconnectSource.Contains("IsModified") && !disconnectSource.Contains(".Save(") && !disconnectSource.Contains(".Close("),"Dirty/bound project is not inspected, saved or closed by Disconnect source");
        check(engine.Split("public void Dispose()")[1].Contains("if (!disconnect.Attempted) Disconnect();"),"Final Dispose source does not retry a failed explicit detach");
        var idle=new PlcDisconnectState();
        check(!idle.Execute(null,null,()=>throw new Exception("Must not detach")).Detached,"Worker idle acknowledged without native action");
        var failed=new PlcDisconnectState();int attempts=0;
        Reject(()=>failed.Execute(123,false,()=>{attempts++;throw new IOException("uncertain");}),"Native disposal failure retained");
        Reject(()=>failed.Execute(123,false,()=>attempts++),"Failed disconnect not retried");
        check(failed.Attempted && failed.Result==null && attempts==1,"Uncertain state retained without success acknowledgement");
        var valid=JsonSerializer.SerializeToNode(new PlcDisconnectState().Execute(123,false,()=>{}))!;
        foreach(var key in valid.AsObject().Select(x=>x.Key).ToArray())
        { var bad=valid.DeepClone().AsObject();bad.Remove(key);Reject(()=>DisconnectContract.Validate(bad,true,123,true),"Missing ack field rejected: "+key); }
        Reject(()=>DisconnectContract.Validate(valid,true,456,true),"Wrong selected PID acknowledgement rejected");
        Reject(()=>DisconnectContract.Validate(DisconnectContract.Idle(),true),"Host idle response cannot masquerade as worker acknowledgement");
        foreach(var error in new Exception[]{new IOException("EOF"),new OperationCanceledException(),new JsonException("malformed ack"),new WorkerOperationException("detach threw",-32603,"unknown")})
        {
            var outcome=new WorkerOutcomeState(); outcome.Failed(true,error);
            check(outcome.Poisoned,"Failed/lost/cancelled/malformed acknowledgement poisons session");
            Reject(outcome.RequireUsable,"Disconnect cannot clear prior poisoning");
        }
        foreach(var enabled in new[]{false,true})
        {
            using var worker=new WorkerClient("17","/missing/never-launch-worker","/missing/no-api",enabled);
            var ack=await worker.Call("Disconnect",new JsonObject(),CancellationToken.None);
            DisconnectContract.Validate(ack,false,null,true);
            check(JsonNode.DeepEquals(ack,await worker.Call("Disconnect",new JsonObject(),CancellationToken.None)),"Host idle disconnect repeats without launch");
            try { await worker.Call("Attach",new JsonObject{["processId"]=123},CancellationToken.None);throw new Exception("Attach accepted"); }
            catch(InvalidOperationException){check(true,"Idle terminal session cannot subsequently launch");}
        }
        using(var worker=new WorkerClient("17","/missing/never-launch-worker","/missing/no-api",false))
        {
            var serial=(SemaphoreSlim)typeof(WorkerClient).GetField("serial",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(worker)!;
            await serial.WaitAsync();
            var pending=worker.Call("Disconnect",new JsonObject(),CancellationToken.None);
            check(!pending.IsCompleted,"Disconnect waits behind in-flight serialization gate");
            serial.Release();
            DisconnectContract.Validate(await pending,false,null,true);
        }
        using(var worker=new WorkerClient("17","/missing/never-launch-worker","/missing/no-api",false))
        {
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try { await worker.Call("Disconnect",new JsonObject(),cancelled.Token);throw new Exception("Cancellation ignored"); }
            catch(OperationCanceledException) { check(true,"Pre-send cancellation has no disconnect outcome"); }
            DisconnectContract.Validate(await worker.Call("Disconnect",new JsonObject(),CancellationToken.None),false,null,true);
        }
        var fake=new FakeWorker();var tool=FoundationTools.Create(fake).Single(t=>t.ProtocolTool.Name=="Disconnect");
        var request=new RequestContext<CallToolRequestParams>(server) { Params=new CallToolRequestParams{Name="Disconnect",Arguments=new Dictionary<string,JsonElement>()} };
        await tool.InvokeAsync(request);
        check(fake.Operation=="Disconnect" && fake.Arguments!.Count==0,"Public Disconnect requires no project or dryRun");
    }
}
