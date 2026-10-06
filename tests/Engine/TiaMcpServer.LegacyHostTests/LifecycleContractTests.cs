using System.Text.Json.Nodes;
using ModelContextProtocol;
using TiaMcp.LegacyHost;
using TiaMcp.PlcFoundation;

internal static class LifecycleContractTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        PlcLifecyclePolicy.RequireLocalSessionExecution(false,false); check(true,"Ordinary project lifecycle is outside local-session lock gate");
        PlcLifecyclePolicy.RequireLocalSessionExecution(true,true); check(true,"Local-session preview allowed without granting mutation");
        try { PlcLifecyclePolicy.RequireLocalSessionExecution(true,false); throw new Exception("Unreviewed server lock effects permitted"); }
        catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException ex) { check(ex.Message.Contains("lock") && !ex.IsArgument,"Unverified local-session kind/lock behavior blocks execution before native mutation"); }
        check(TiaMcp.PlcWorker.WorkerOperations.Names.SetEquals(FoundationTools.Definitions.Select(d=>d.Operation)),"Worker allowlist matches actual public tool operations");
        foreach(var internalMethod in new[]{"Dispose","UnbindProject","RequireProjectIdentity","ListPlcs","ListBlocks"}) check(!TiaMcp.PlcWorker.WorkerOperations.Names.Contains(internalMethod),"Internal facade method not exposed on worker wire: "+internalMethod);
        void Reject(Action action,string message) { try { action(); throw new Exception(message); } catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or NotSupportedException) { check(true,message); } }
        // Spelled out rather than derived: the file names TIA itself writes for each release.
        var suffixes=new Dictionary<string,string>{["14sp1"]="14",["15.1"]="15_1",["16"]="16",["17"]="17",["18"]="18",["19"]="19",["20"]="20",["21"]="21"};
        foreach(var key in new[]{"14sp1","15.1","16","17","18","19","20","21"})
        {
            var major=suffixes[key];
            check(PlcLifecyclePolicy.FileSuffix(key)==major,"Project file suffix "+key);
            check(!PlcLifecyclePolicy.IsSessionFile(key,"Project.ap"+major),"Exact project format "+key);
            Reject(()=>PlcLifecyclePolicy.IsSessionFile(key,"Project.ap99"),"No cross-version upgrade "+key);
            Reject(()=>PlcLifecyclePolicy.IsSessionFile(key,"Project.zap"+major),"No archive/server-open fallback "+key);
            if(key is "14sp1" or "15.1" or "16") Reject(()=>PlcLifecyclePolicy.IsSessionFile(key,"Project.als"+major),"Missing local-session API is gated "+key);
            else check(PlcLifecyclePolicy.IsSessionFile(key,"Project.als"+major),"Local-session format admitted "+key);
            check(PlcLifecyclePolicy.CreationFile(key,@"C:\Projects","New")==@"C:\Projects\New\New.ap"+major,"Creation preview identity "+key);
        }
        Reject(()=>PlcLifecyclePolicy.IsSessionFile("15.1","Project.ap15"),"A V15 project is not opened as V15.1");
        var state=new PlcLifecycleState();
        Reject(state.RequireUnbound,"Cannot open while detached"); Reject(()=>state.RequireAttach(0),"No automatic process selection");
        state.RequireAttach(123); check(!state.ProcessId.HasValue,"Validation does not attach");
        state.Attached(123); Reject(()=>state.RequireAttach(123),"Duplicate attach cannot replay native call");
        state.RequireUnbound(); state.RequireUnbound(); check(state.ProjectFile==null,"Repeated preview leaves project state unchanged");
        state.Bound(@"C:\Projects\P.ap17",false,false);
        Reject(state.RequireUnbound,"Cannot replace a bound project"); Reject(()=>state.RequireClose(false),"Cannot close borrowed unmodified project");
        state.RequireBound(); check(!state.OwnsProject,"Saving a borrowed project never grants ownership");
        state.Unbound(); Reject(state.Unbound,"Repeated unbind refused");
        state.Bound(@"C:\Projects\P.als17",true,true);
        Reject(()=>state.RequireClose(true),"Unsaved local session cannot be discarded");
        state.RequireClose(false); check(state.IsLocalSession && state.ProjectFile!=null,"Close preview retains binding");
        state.Unbound(); check(state.ProcessId==123 && state.ProjectFile==null,"Close retains borrowed TIA connection");
        state.Bound(@"C:\Projects\P.als17",false,true); Reject(()=>state.RequireClose(false),"Borrowed local session cannot be closed");
        state.Detached(); check(state.ProjectFile==null && !state.ProcessId.HasValue,"Detach clears ownership without project save/close semantics");
        var outcome=new WorkerOutcomeState();
        outcome.Failed(false,new OperationCanceledException()); outcome.RequireUsable(); check(!outcome.Poisoned,"Cancelled before dispatch permits a later explicit call");
        outcome.Failed(true,new WorkerOperationException("rejected",-32602,"rejected-before-operation")); outcome.RequireUsable(); check(!outcome.Poisoned,"Known validation rejection does not poison");
        outcome.Failed(true,new TimeoutException()); Reject(outcome.RequireUsable,"Sent timeout prevents later calls");
        outcome.Failed(false,new OperationCanceledException()); Reject(outcome.RequireUsable,"Unsent cancellation cannot reset an unknown native result");
        var cancelled=new WorkerOutcomeState(); cancelled.Failed(true,new OperationCanceledException()); Reject(cancelled.RequireUsable,"Cancellation after send remains unknown");
        using(var client=new WorkerClient("17","must-not-start.exe","must-not-read-api",true))
        using(var cts=new CancellationTokenSource())
        {
            cts.Cancel();
            try { await client.Call("Attach",new JsonObject { ["processId"]=123 },cts.Token); throw new Exception("Pre-cancelled transport call accepted"); }
            catch(OperationCanceledException) { check(true,"Actual client cancellation happens before file checks/process startup"); }
        }
        var result=new JsonObject { ["Executed"]=true,["ProjectFile"]=@"C:\Projects\P.ap17" };
        var args=new JsonObject { ["dryRun"]=false,["expectedProjectFile"]=@"C:\Projects\P.ap17" };
        foreach(var operation in new[]{"OpenProject","CreateProject","SaveProject","CloseProject","BindProject"})
        {
            WorkerProtocol.ValidateExchangeResult(operation,args,result); check(true,"Lifecycle result identity validated "+operation);
            args["expectedProjectFile"]=@"C:\Projects\Other.ap17";
            try { WorkerProtocol.ValidateExchangeResult(operation,args,result); throw new Exception("Wrong project accepted"); } catch(IOException ex) { check(WorkerProtocol.RequiresSessionReset(true,ex),"Wrong lifecycle result stops session "+operation); }
            args["expectedProjectFile"]=@"C:\Projects\P.ap17";
        }
        string Wire(string name)=>McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(name)??name;
        var response=V17ProjectEnvelope.Wrap("CloseProject","ProjectMutation",result,false);
        check(response[Wire("Meta")]!["contractVersion"]!.GetValue<string>()=="v17-project-safe-v1" && response[Wire("Meta")]!["serverCommit"]!.GetValue<string>()=="never","Lifecycle contract discloses no server commit");
        var tree=V17ProjectEnvelope.Wrap("GetProjectTree","ProjectTree",JsonValue.Create("P\n  Device: PLC"),false);
        check(tree[Wire("Tree")]!.GetValue<string>().StartsWith("```\nP"),"V17 Tree response envelope retained");
    }
}
