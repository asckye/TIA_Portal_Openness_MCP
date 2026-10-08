using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.LegacyHost;
using TiaMcp.PlcFoundation;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

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
        var engine=File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!,"../../../src/Adapters/Native/Session/PlcFoundationEngine.cs")));
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
            check(pending.IsCompleted,"Disconnect does not wait behind an in-flight serialization gate");
            serial.Release();
            var terminal = await pending;
            DisconnectContract.Validate(terminal,false,null,true);
            check(terminal?["RequiresTiaRestart"]?.GetValue<bool>() != true,"A held host gate without a native call does not require TIA restart");
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

public sealed class FoundationRecoveryTests
{
    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Answered_unknown_can_detach_and_a_new_foundation_worker_can_attach(string release)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcpServer.LegacyHostTests.exe");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
        using var worker = new WorkerClient(release, exe, AppContext.BaseDirectory, true) { Bundled = false };
        await worker.Call("Attach", new JsonObject { ["processId"] = 123 }, CancellationToken.None);
        using (TiaMcpServer.ModelContextProtocol.InvocationJournal.UseCorrelation("prior-foundation-unknown"))
            await Assert.ThrowsAsync<WorkerOperationException>(() => worker.Call("FixtureUnknown", new JsonObject(), CancellationToken.None));
        Assert.True(worker.Poisoned);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.Call("ReadState", new JsonObject(), CancellationToken.None));
        var ack = await worker.Call("Disconnect", new JsonObject(), CancellationToken.None);
        Assert.True((bool?)ack?["WorkerAcknowledged"]); Assert.True((bool?)ack?["Detached"]);
        Assert.False((bool?)ack?["RequiresTiaRestart"]);
        Assert.Equal("prior-foundation-unknown", (string?)ack?["PriorUnknownRequestId"]);
        Assert.True(JsonNode.DeepEquals(ack, await worker.Call("Disconnect", new JsonObject(), CancellationToken.None)));
        using var next = new WorkerClient(release, exe, AppContext.BaseDirectory, true) { Bundled = false };
        await next.Call("Attach", new JsonObject { ["processId"] = 123 }, CancellationToken.None);
        await next.Call("Disconnect", new JsonObject(), CancellationToken.None);
    }

    [Theory, InlineData("14sp1"), InlineData("19")]
    public async Task Foundation_in_flight_disconnect_does_not_wait_for_a_hung_native_call(string release)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcpServer.LegacyHostTests.exe");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
        using var worker = new WorkerClient(release, exe, AppContext.BaseDirectory, true) { Bundled = false };
        await worker.Call("Attach", new JsonObject { ["processId"] = 123 }, CancellationToken.None);
        var running = worker.Call("FixtureHang", new JsonObject(), CancellationToken.None);
        var ack = await worker.Call("Disconnect", new JsonObject(), CancellationToken.None);
        Assert.True((bool?)ack?["RequiresTiaRestart"]); Assert.False((bool?)ack?["WorkerAcknowledged"]);
        Assert.Contains("restart that TIA instance", (string?)ack?["RecoveryMessage"]);
        await Assert.ThrowsAnyAsync<Exception>(() => running);
    }
}
