using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using TiaMcp.LegacyHost;
using TiaMcp.PlcFoundation;

internal static class CompileContractTests
{
    private sealed class Device
    {
        internal readonly string Name;
        internal string? State="Offline";
        internal bool Covered=true;
        internal Device(string name) { Name=name; }
    }
    private sealed class Group
    {
        internal Device[] Devices=Array.Empty<Device>();
        internal Group[] Children=Array.Empty<Group>();
    }
    internal static void Run(Action<bool,string> check)
    {
        var root=new Device("root"); var ungrouped=new Device("ungrouped"); var nested=new Device("nested");
        var group=new Group { Children=new[]{new Group { Devices=new[]{nested} }} };
        var observed=new List<string>(); int compilations=0;
        void Compile()
        {
            PlcOfflinePolicy.RequireProjectDevicesOffline(new[]{root},new[]{ungrouped},new[]{group},
                g=>g.Devices,g=>g.Children,d=>
                { observed.Add(d.Name); PlcOfflinePolicy.RequireStates(new[]{d.State},d.Covered,d.Name); });
            compilations++;
        }
        Compile();
        check(compilations==1 && observed.SequenceEqual(new[]{"root","ungrouped","nested"}),"Positive project-wide offline evidence admits compilation and includes nested/ungrouped devices");
        foreach(var device in new[]{root,ungrouped,nested})
        {
            device.State="Online";
            try { Compile(); throw new Exception("Online project device admitted"); }
            catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException) { check(compilations==1,"Online device prevents compilation: "+device.Name); }
            device.State="Offline";
        }
        nested.Covered=false;
        try { Compile(); throw new Exception("Unknown device coverage admitted"); }
        catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException) { check(compilations==1,"Unknown PLC device coverage prevents compilation"); }
        nested.Covered=true;
        try { PlcOfflinePolicy.RequireProjectDevicesOffline(Array.Empty<Device>(),Array.Empty<Device>(),Array.Empty<Group>(),g=>g.Devices,g=>g.Children,d=>{}); throw new Exception("Empty project admitted"); }
        catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException) { check(true,"Empty project cannot prove all devices offline"); }
        try { PlcOfflinePolicy.RequireProjectDevicesOffline(Array.Empty<Device>(),Array.Empty<Device>(),new[]{group},g=>throw new IOException("inventory read failed"),g=>g.Children,d=>{}); throw new Exception("Unreadable inventory admitted"); }
        catch(IOException) { check(true,"Device inventory failure propagates before compilation"); }
        check(!PlcOfflinePolicy.CheckCompileStates(Array.Empty<string>(),true,"HMI"),"A device with no PLC online service is reported unobserved, not falsely treated as Online");
        check(PlcOfflinePolicy.CheckCompileStates(new[]{"Offline"},true,"PLC"),"Observed offline PLC state is accepted for compilation");
        try { PlcOfflinePolicy.CheckCompileStates(Array.Empty<string>(),false,"PLC"); throw new Exception("Missing PLC provider admitted"); }
        catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException) { check(true,"Missing required PLC provider is distinct from non-PLC devices without that service"); }
        try { PlcOfflinePolicy.CheckCompileStates(new[]{"Online"},true,"PLC"); throw new Exception("Online PLC admitted"); }
        catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException) { check(true,"Observable online project device still blocks compilation"); }
        foreach(var release in new[]{"14sp1","15.1","16","17","18","19","20","21"}) { PlcOfflinePolicy.RequireDocumentedRelease(release); check(true,"Exact-release offline workflow reviewed "+release); }
        foreach(var release in new[]{"14","15","bogus"})
        { try { PlcOfflinePolicy.RequireDocumentedRelease(release); throw new Exception("Missing manual evidence admitted"); } catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException) { check(true,"Unreviewed release denied"); } }
        PlcOfflinePolicy.RequireStates(new[]{"Offline","Offline"},true,"R/H"); check(true,"Both R/H states explicitly Offline");
        foreach(var state in new string?[]{null,"","Online","Connecting","Disconnecting","Incompatible","NotReachable","Protected","offline","999"})
        {
            foreach(var states in new[]{new[]{state,"Offline"},new[]{"Offline",state}})
            { try { PlcOfflinePolicy.RequireStates(states,true,"R/H"); throw new Exception("Non-offline state admitted"); } catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException) { check(true,"Neither R/H side may be unknown/non-Offline"); } }
        }
        foreach(var states in new[]{Array.Empty<string>(),new[]{"Offline"}})
        { try { PlcOfflinePolicy.RequireStates(states,false,"coverage"); throw new Exception("Missing service admitted"); } catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException) { check(true,"Missing coverage is not offline"); } }
        try { PlcOfflinePolicy.RequireStates(Array.Empty<string>(),true,"empty"); throw new Exception("Empty proof admitted"); } catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException) { check(true,"Empty inventory rejected"); }
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
        var result=new PlcCompileResult { Executed=true,ProjectFile=@"C:\Projects\P.ap17",State="Error",ErrorCount=7,WarningCount=3,OfflineStateNotExposedByDevices=new[]{"HMI_1"} };
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
        check((string?)output[Wire("Meta")]!["offlineStateNotExposedByDevices"]![0]=="HMI_1","Compile output reports devices whose online state was not exposed");
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
