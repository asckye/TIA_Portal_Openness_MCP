using TiaMcp.PlcFoundation;
using TiaMcp.LegacyHost;

internal static class PathAndIdentityTests
{
    internal static void Run(Action<bool,string> check)
    {
        void Reject(Action action,string message) { try { action(); throw new Exception(message); } catch(ArgumentException) { check(true,message); } }
        var cpu=new PlcReadCandidate<int> { Value=1,ExactPath="devices/Station/PLC_1",Device="Station",Host="PLC_1",Context="exact-host",DeviceContext="exact-device" };
        check((string?)PlcReadPathPolicy.Select(new[]{cpu},"PLC_1").Context=="exact-host","Safety host stays bound to selected PLC candidate");
        check((string?)PlcReadPathPolicy.Select(new[]{cpu},"PLC_1").DeviceContext=="exact-device","Offline device stays bound to selected PLC candidate");
        var pc=new PlcReadCandidate<int> { Value=2,ExactPath="device-groups/Line/devices/PC/Slot/CPU",Groups=new[]{"Line"},Device="PC",Host="CPU" };
        var rows=new[]{cpu,pc};
        foreach(var alias in new[]{"PLC_1","plc_1","Station","station/PLC_1","devices/Station/PLC_1"}) check(PlcReadPathPolicy.Resolve(rows,alias)==1,"Hardware and exact alias "+alias);
        foreach(var alias in new[]{"Line/CPU","Line/pc","Line/PC/cpu","device-groups/Line/devices/PC/Slot/CPU"}) check(PlcReadPathPolicy.Resolve(rows,alias)==2,"Group/PC alias "+alias);
        foreach(var invalid in new[]{""," ","/PLC_1","PLC_1/","Station//PLC_1","Station/PLC_1/ignored","line/CPU","Line/Missing","../PLC_1","PLC"}) Reject(()=>PlcReadPathPolicy.Resolve(rows,invalid),"Unresolved/full-consumption rule "+invalid);
        var duplicate=new PlcReadCandidate<int> { Value=3,ExactPath="devices/Other/PLC_1",Device="Other",Host="PLC_1" };
        Reject(()=>PlcReadPathPolicy.Resolve(new[]{cpu,duplicate},"PLC_1"),"Duplicate CPU alias refused");
        check(PlcReadPathPolicy.Resolve(new[]{cpu,duplicate},"devices/Other/PLC_1")==3,"Exact address disambiguates CPU names");
        var secondSlot=new PlcReadCandidate<int> { Value=4,ExactPath="devices/Station/Slot2",Device="Station",Host="Slot2" };
        Reject(()=>PlcReadPathPolicy.Resolve(new[]{cpu,secondSlot},"Station"),"Device with multiple CPUs refused");
        check(PlcReadPathPolicy.Resolve(new[]{cpu,secondSlot},"Station/Slot2")==4,"Specific PC CPU succeeds");
        var ungrouped=new PlcReadCandidate<int> { Value=5,ExactPath="ungrouped/Other/PLC_1",Device="Other",Host="PLC_1",AllowLegacyAlias=false };
        check(PlcReadPathPolicy.Resolve(new[]{cpu,ungrouped},"PLC_1")==1,"Canonical-only ungrouped entry cannot shadow V17 alias");
        check(PlcReadPathPolicy.Resolve(new[]{cpu,ungrouped},ungrouped.ExactPath)==5,"Ungrouped canonical address works");
        Reject(()=>PlcReadPathPolicy.Resolve(new[]{cpu,cpu},cpu.ExactPath),"Duplicate canonical path refused");

        int boundChecks=0;
        Action<string> bound=expected=>{boundChecks++; MutationIdentityPolicy.RequireSameProject(expected,@"C:\Projects\P\P.ap17");};
        void Gate(bool dry,bool confirm,string expected,string op="SaveProject",string key="17",string file="",string directory="",string name="")=>MutationIdentityPolicy.ValidateTarget(dry,confirm,expected,op,key,file,directory,name,bound);
        Gate(true,false,""); check(boundChecks==0,"Preview does not query project identity");
        Reject(()=>Gate(false,false,@"C:\Projects\P\P.ap17"),"No confirmation refused");
        foreach(var path in new[]{"P.ap17",@"C:P.ap17",@"\P.ap17",@"C:\Projects\Other\P.ap17"}) Reject(()=>Gate(false,true,path),"Identity ambiguity/mismatch refused "+path);
        Gate(false,true,@"c:\projects\P\P.ap17"); check(boundChecks==2,"Exact Windows identity checked; different project rejected");
        int before=boundChecks;
        Gate(false,true,@"C:\Projects\New.ap17","OpenProject",file:@"C:\Projects\New.ap17"); check(boundChecks==before,"Open target independent from current project");
        Reject(()=>Gate(false,true,@"C:\Projects\New.ap17","OpenProject",file:@"C:\Projects\Old.ap17"),"Open identity mismatch refused");
        foreach(var key in new[]{"14sp1","15.1","16","17","18","19","20","21"})
        {
            var major=key=="14sp1"?"14":key=="15.1"?"15":key;
            Gate(false,true,@"C:\Projects\New\New.ap"+major,"CreateProject",key,directory:@"C:\Projects",name:"New");
            check(boundChecks==before,"Create exact version target "+key);
        }
        Reject(()=>Gate(false,true,@"C:\Projects\New\New.ap17","CreateProject",directory:@"C:\Projects",name:"../New"),"Create traversal refused");
        Reject(()=>Gate(false,true,@"C:\Projects\New\New.ap16","CreateProject",directory:@"C:\Projects",name:"New"),"Wrong project version refused");
        foreach(var invalidKey in new[]{"14","15","V17","22"}) Reject(()=>Gate(false,true,@"C:\Projects\New\New.ap17","CreateProject",invalidKey,directory:@"C:\Projects",name:"New"),"Original/unknown release refused");
        foreach(var outcome in new[]{"rejected-before-operation","read-failed","unknown"})
        {
            var wire="{\"id\":1,\"error\":{\"message\":\"test\",\"code\":-32602,\"outcome\":\""+outcome+"\"}}";
            try { WorkerProtocol.Decode(wire,1); throw new Exception("Error returned as success"); }
            catch(WorkerOperationException error) { check(error.Code==-32602,"Worker error code preserved"); check(WorkerProtocol.RequiresSessionReset(true,error)==(outcome=="unknown"),"Only unknown outcome poisons session"); }
        }
        check(WorkerProtocol.RequiresSessionReset(true,new OperationCanceledException()),"Sent cancellation remains unknown");
        check(!WorkerProtocol.RequiresSessionReset(false,new OperationCanceledException()),"Unsent cancellation is safe");
    }
}
