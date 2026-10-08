using TiaMcp.Adapters;
using TiaMcp.FoundationHost;

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
        foreach(var alias in new[]{"CPU","pc","Line/CPU","Line/pc","Line/PC/cpu","device-groups/Line/devices/PC/Slot/CPU"}) check(PlcReadPathPolicy.Resolve(rows,alias)==2,"Group/PC alias "+alias);
        foreach(var alias in new[]{"CPU","Line/CPU","Line/pc","Line/PC/cpu"}) check(TiaOpenness.Shared.NativeExportPolicy.ResolvedIdentityMatches(alias,pc.ExactPath),"Transport accepts the resolved grouped short/qualified PLC identity "+alias);
        check(TiaOpenness.Shared.NativeExportPolicy.ResolvedIdentityMatches("PLC_1",cpu.ExactPath),"Transport accepts the ordinary root PLC short identity");
        foreach(var invalid in new[]{""," ","/PLC_1","PLC_1/","Station//PLC_1","Station/PLC_1/ignored","line/CPU","Line/Missing","../PLC_1","PLC"}) Reject(()=>PlcReadPathPolicy.Resolve(rows,invalid),"Unresolved/full-consumption rule "+invalid);
        var duplicate=new PlcReadCandidate<int> { Value=3,ExactPath="devices/Other/PLC_1",Device="Other",Host="PLC_1" };
        Reject(()=>PlcReadPathPolicy.Resolve(new[]{cpu,duplicate},"PLC_1"),"Duplicate CPU alias refused");
        check(PlcReadPathPolicy.Resolve(new[]{cpu,duplicate},"devices/Other/PLC_1")==3,"Exact address disambiguates CPU names");
        var secondSlot=new PlcReadCandidate<int> { Value=4,ExactPath="devices/Station/Slot2",Device="Station",Host="Slot2" };
        Reject(()=>PlcReadPathPolicy.Resolve(new[]{cpu,secondSlot},"Station"),"Device with multiple CPUs refused");
        check(PlcReadPathPolicy.Resolve(new[]{cpu,secondSlot},"Station/Slot2")==4,"Specific PC CPU succeeds");
        var ungrouped=new PlcReadCandidate<int> { Value=5,ExactPath="ungrouped/Other/PLC_1",Device="Other",Host="PLC_1",AllowLegacyAlias=false };
        Reject(()=>PlcReadPathPolicy.Resolve(new[]{cpu,ungrouped},"PLC_1"),"Short PLC identity must be unique across grouped and ungrouped devices");
        check(PlcReadPathPolicy.Resolve(new[]{ungrouped},"PLC_1")==5,"Unique ungrouped short PLC identity resolves");
        check(PlcReadPathPolicy.Resolve(new[]{cpu,ungrouped},ungrouped.ExactPath)==5,"Ungrouped canonical address works");
        Reject(()=>PlcReadPathPolicy.Resolve(new[]{cpu,cpu},cpu.ExactPath),"Duplicate canonical path refused");

        foreach(var pair in new[]{
            new[]{@"C:\Projects\P\P.ap17",@"c:/projects/P/./P.ap17"},
            new[]{@"C:\Projects\P\P.ap17",@"C:\Projects\Other\..\P\P.ap17"},
            new[]{@"\\server\share\P\P.ap17",@"//SERVER/share/P/./P.ap17"},
            new[]{@"\\server\share\P\P.ap17",@"\\server\share\Other\..\P\P.ap17"}})
        { MutationIdentityPolicy.RequireSameProject(pair[0],pair[1]); check(true,"Deterministic Windows identity normalization "+pair[1]); }
        foreach(var invalid in new[]{@"C:P.ap17",@"\P.ap17","/tmp/P.ap17",@"\\server",@"\\server\",@"\\server\\P.ap17",@"\\?\C:\P.ap17",@"\\.\C:\P.ap17",@"C:\..\P.ap17",@"\\server\share\..\P.ap17",@"C:\P.ap17:stream",@"C:\P.\P.ap17",@"C:\CON\P.ap17",@"C:\P \P.ap17",@"C:\P?\P.ap17", "C:\\P\0.ap17"})
            Reject(()=>MutationIdentityPolicy.RequireSameProject(invalid,invalid),"Malformed/ambiguous Windows identity rejected "+invalid);
        Reject(()=>MutationIdentityPolicy.RequireSameProject(@"\\server\share\P.ap17",@"\\server\other\P.ap17"),"UNC share is part of identity");
        Reject(()=>MutationIdentityPolicy.RequireSameProject(@"C:\P.ap17",@"D:\P.ap17"),"Drive is part of identity");

        Reject(()=>MutationIdentityPolicy.RequireSameProject(@"C:\P.ap17\",@"C:\P.ap17"),"Trailing separator cannot alias a project file");
        Reject(()=>MutationIdentityPolicy.RequireSameProject(@"\\server\share\P.ap17\",@"\\server\share\P.ap17"),"UNC trailing separator cannot alias a project file");
        foreach(var device in new[]{"COM¹","COM²","COM³","LPT¹","LPT²","LPT³"})
            Reject(()=>MutationIdentityPolicy.RequireSameProject(@"C:\"+device+@"\P.ap17",@"C:\"+device+@"\P.ap17"),"Reserved DOS superscript device rejected "+device);
        foreach(var directory in new[]{@"\\server\share",@"\\server\share\"})
            check(PlcLifecyclePolicy.CreationFile("17",directory,"New")==@"\\server\share\New\New.ap17","UNC share root creation parent "+directory);

        foreach(var invalid in new[]{@"C:\P.ap17\",@"\\server\share\P.ap17\"})
            Reject(()=>MutationIdentityPolicy.RequireSameProject(invalid,invalid),"Identical trailing-separator paths are not project files");
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
            var major=key=="14sp1"?"14":key=="15.1"?"15_1":key;
            Gate(false,true,@"C:\Projects\New\New.ap"+major,"CreateProject",key,directory:@"C:\Projects",name:"New");
            check(boundChecks==before,"Create exact version target "+key);
        }
        Reject(()=>Gate(false,true,@"C:\Projects\New\New.ap17","CreateProject",directory:@"C:\Projects",name:"../New"),"Create traversal refused");
        Reject(()=>Gate(false,true,@"C:\Projects\New\New.ap16","CreateProject",directory:@"C:\Projects",name:"New"),"Wrong project version refused");
        foreach(var invalidKey in new[]{"14","15","V17","22"}) Reject(()=>Gate(false,true,@"C:\Projects\New\New.ap17","CreateProject",invalidKey,directory:@"C:\Projects",name:"New"),"Original/unknown release refused");
        foreach(var outcome in new[]{"rejected-before-operation","read-failed","unknown"})
        {
            var error=new WorkerOperationException("test",-32602,outcome);
            check(WorkerProtocol.RequiresSessionReset(true,error)==(outcome=="unknown"),"Only unknown outcome poisons session");
        }
        check(WorkerProtocol.RequiresSessionReset(true,new OperationCanceledException()),"Sent cancellation remains unknown");
        check(!WorkerProtocol.RequiresSessionReset(false,new OperationCanceledException()),"Unsent cancellation is safe");
    }
}
