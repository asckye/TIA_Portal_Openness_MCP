using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using TiaMcp.LegacyHost;
using TiaMcp.PlcFoundation;

internal static class CompileContractTests
{
    internal static void Run(Action<bool,string> check)
    {
        { try { PlcOfflinePolicy.RequireReviewedExecution("hardware coverage"); throw new Exception("Incomplete execution review admitted"); } catch(NotSupportedException) { check(true,"Unproven hardware coverage remains blocked"); } }
        foreach(var release in new[]{"14sp1","15.1","16","17","18","19","20","21"}) { PlcOfflinePolicy.RequireDocumentedRelease(release); check(true,"Exact-release offline workflow reviewed "+release); }
        foreach(var release in new[]{"14","15","bogus"})
        { try { PlcOfflinePolicy.RequireDocumentedRelease(release); throw new Exception("Missing manual evidence admitted"); } catch(NotSupportedException) { check(true,"Unreviewed release denied"); } }
        PlcOfflinePolicy.RequireStates(new[]{"Offline","Offline"},true,"R/H"); check(true,"Both R/H states explicitly Offline");
        foreach(var state in new string?[]{null,"","Online","Connecting","Disconnecting","Incompatible","NotReachable","Protected","offline","999"})
        {
            foreach(var states in new[]{new[]{state,"Offline"},new[]{"Offline",state}})
            { try { PlcOfflinePolicy.RequireStates(states,true,"R/H"); throw new Exception("Non-offline state admitted"); } catch(InvalidOperationException) { check(true,"Neither R/H side may be unknown/non-Offline"); } }
        }
        foreach(var states in new[]{Array.Empty<string>(),new[]{"Offline"}})
        { try { PlcOfflinePolicy.RequireStates(states,false,"coverage"); throw new Exception("Missing service admitted"); } catch(NotSupportedException) { check(true,"Missing coverage is not offline"); } }
        try { PlcOfflinePolicy.RequireStates(Array.Empty<string>(),true,"empty"); throw new Exception("Empty proof admitted"); } catch(NotSupportedException) { check(true,"Empty inventory rejected"); }
        foreach(var key in new[]{"14sp1","15.1","16","17","18","19","20","21"})
        {
            PlcCompilePolicy.RequirePasswordCapability(key,""); check(true,"Password-free compile available "+key);
            if(key is "14sp1" or "15.1" or "16")
            {
                try { PlcCompilePolicy.RequirePasswordCapability(key,"do-not-echo"); throw new Exception("Unavailable safety password silently accepted"); }
                catch(NotSupportedException ex) { check(!ex.Message.Contains("do-not-echo"),"Explicit capability gate without secret echo "+key); }
            }
            else { PlcCompilePolicy.RequirePasswordCapability(key,"do-not-echo"); check(true,"Safety login API version admitted "+key); }
        }
        var result=new PlcCompileResult { Executed=true,ProjectFile=@"C:\Projects\P.ap17",State="Error",ErrorCount=7,WarningCount=3 };
        PlcCompilePolicy.Classify(result,new[]{
            new PlcDiagnostic { State="Error",Description="",Formatted="parent",HasChildren=true },
            new PlcDiagnostic { State="Error",Description="Bad symbol",Formatted="error" },
            new PlcDiagnostic { State="Error",Description="Bad symbol",Formatted="error" },
            new PlcDiagnostic { State="Warning",Description="Unused",Formatted="warning" },
            new PlcDiagnostic { State="Information",Description="Details",Formatted="info" },
            new PlcDiagnostic { State="Error",Description="Compiling finished with errors",Formatted="summary" }});
        check(result.Messages.Length==6 && result.Errors.SequenceEqual(new[]{"error"}) && result.Warnings.Length==1 && result.Info.Length==1,"Nested raw lines retained; summary/empty parent excluded and details deduplicated");
        check(result.ErrorCount==7 && result.WarningCount==3,"Native aggregate counts not replaced by detail-list counts");
        string Wire(string name)=>McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(name)??name;
        var payload=JsonSerializer.SerializeToNode(result)!;
        var output=V17CompileEnvelope.Wrap("CompileAndDiagnosePlc",payload,false);
        check(output[Wire("ErrorCount")]!.GetValue<int>()==7 && !output[Wire("Meta")]!["success"]!.GetValue<bool>(),"Compiler errors cannot be reported as successful compile");
        check(((JsonArray)output[Wire("RawMessages")]!).Count==6 && ((JsonArray)output[Wire("Errors")]!).Count==1,"V17 diagnostic fields preserved");
        output=V17CompileEnvelope.Wrap("CompileSoftware",payload,false);
        check(output[Wire("Messages")] is JsonArray && !output.ContainsKey(Wire("RawMessages")),"Basic compile response uses Messages");
        var preview=JsonSerializer.SerializeToNode(new PlcCompileResult { ProjectFile=@"C:\Projects\P.ap17" })!;
        output=V17CompileEnvelope.Wrap("CompileSoftware",preview,true);
        check(output[Wire("ErrorCount")]==null && output[Wire("State")]==null && !output[Wire("Meta")]!["executed"]!.GetValue<bool>(),"Preview never fabricates zero errors or successful compilation state");
        preview["ErrorCount"]=0;
        try { V17CompileEnvelope.Validate(preview,true); throw new Exception("Fabricated preview count accepted"); } catch(IOException) { check(true,"Preview fake counts rejected"); }
        payload["WarningCount"]=-1;
        try { V17CompileEnvelope.Validate(payload,false); throw new Exception("Negative count accepted"); } catch(IOException ex) { check(WorkerProtocol.RequiresSessionReset(true,ex),"Invalid actual compiler outcome stops session"); }
        foreach(var name in new[]{"CompileSoftware","CompileAndDiagnosePlc"})
        {
            var def=FoundationTools.Definitions.Single(d=>d.Name==name);
            check(def.Arguments.Select(a=>a.Name).SequenceEqual(new[]{"softwarePath","password","dryRun"}),"V17 compile parameters plus preview "+name);
            check(def.Arguments[1].Default is "" && def.Arguments[2].Default is true,"Password/preview defaults "+name);
        }
    }
}
