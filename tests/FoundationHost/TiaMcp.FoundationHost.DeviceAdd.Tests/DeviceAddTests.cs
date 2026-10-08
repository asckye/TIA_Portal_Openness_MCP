using TiaMcp.Adapters;

internal static class DeviceAddTests
{
    internal static void Run(Action<bool,string> Check)
    {
        void Reject(Action a,string n) { try {a();}catch(ArgumentException){Check(true,n);return;}catch(InvalidOperationException){Check(true,n);return;}catch(NotSupportedException){Check(true,n);return;}throw new Exception("Expected rejection: "+n); }
        PlcDeviceAddRequest Request()=>new(){Release="19",Project=@"C:\Test\Test.ap19",RootIdentity="root",ProcessId=42,PreferredMlfb="6ES7 513-1AM03-0AB0",PreferredVersion="V1",Name="PLC_2"};
        PlcHardwareCatalogCandidate Candidate()=>new(){TypeIdentifier="OrderNumber:6ES7 513-1AM03-0AB0/V1",ArticleNumber="6ES7 513-1AM03-0AB0",Version="V1"};
        PlcDeviceAddItem Item(string name="PLC_1",string id="one")=>new(){Name=name,Identity=id,ParentIdentity="root",ParentVerified=true};
        var items=new List<PlcDeviceAddItem>();var rows=new List<PlcHardwareCatalogCandidate>();int calls=0,checks=0;
        void Reset(){items=new(){Item()};rows=new(){Candidate()};calls=0;checks=0;}
        PlcDeviceAddResult Run(PlcDeviceAddRequest r,Func<string,string,PlcDeviceAddItem>? create=null,Action? check=null)=>PlcDeviceAddPolicy.Run(r,()=>rows,()=>items,check??(()=>checks++),create??((id,name)=>{calls++;Check(id==Candidate().TypeIdentifier,"exact ID retained");var added=Item(name,"two");items.Add(added);return added;}));
        PlcDeviceAddRequest Apply(){var r=Request();r.ExpectedHash=Run(r).PlanHash;r.DryRun=false;r.Confirm=true;r.ExpectedProject=r.Project;return r;}
        Reset();var result=Run(Request());Check(!result.Attempted&&!result.Executed&&calls==0&&result.PlanHash.Length==64,"preview no writes");
        foreach(var release in new[]{"19","20","21"}) {Reset();var r=Request();r.Release=release;Check(Run(r).Release==release,"exact enabled release");}
        foreach(var release in new[]{"14sp1","15.1","16","17","18","unknown"}) {Reset();var r=Request();r.Release=release;Reject(()=>Run(r),"gate");Check(checks==0&&calls==0,"gate before access");}
        foreach(var name in new[]{""," ","../x","a/b","a\\b","a\n","a*","a?","a:","a ",".","..",new string('x',129)}) {Reset();var r=Request();r.Name=name;Reject(()=>Run(r),"name");}
        foreach(var family in new[]{"","S7-300","WinCCUnifiedPC","s7-1500"}) {Reset();var r=Request();r.Family=family;Reject(()=>Run(r),"unsupported family");}
        foreach(var value in new[]{""," ","a\n",new string('x',257)}) {Reset();var r=Request();r.PreferredMlfb=value;Reject(()=>Run(r),"identifier bounds");}
        foreach(var value in new[]{""," ","a\n",new string('x',65)}) {Reset();var r=Request();r.PreferredVersion=value;Reject(()=>Run(r),"version bounds");}
        Reset();var exact=Request();exact.PreferredMlfb=Candidate().TypeIdentifier!;exact.PreferredVersion="";Check(Run(exact).TypeIdentifier==exact.PreferredMlfb,"full identifier selection");exact.PreferredVersion="V1";Reject(()=>Run(exact),"conflicting version");
        Reset();rows.Clear();Reject(()=>Run(Request()),"missing exact candidate");
        Reset();rows.Add(Candidate());Reject(()=>Run(Request()),"duplicate candidate ambiguity");
        Reset();rows[0].ArticleNumber="6ES7513-1AM03-0AB0";Reject(()=>Run(Request()),"no article normalization");
        Reset();rows[0].Version="v1";Reject(()=>Run(Request()),"no version normalization");
        Reset();rows[0].TypeIdentifier="GSD:x";Reject(()=>Run(Request()),"no GSD");
        Reset();rows=Enumerable.Range(0,1001).Select(_=>Candidate()).ToList();Reject(()=>Run(Request()),"catalog budget");
        Reset();items.Add(Item("plc_2","two"));Reject(()=>Run(Request()),"case insensitive collision");
        Reset();items.Add(Item("plc_1","two"));Reject(()=>Run(Request()),"ambiguous existing names");
        Reset();items.Add(Item("other","one"));Reject(()=>Run(Request()),"duplicate identities");
        Reset();items[0].ParentVerified=false;Reject(()=>Run(Request()),"wrong parent");
        Reset();items=Enumerable.Range(0,4097).Select(i=>Item("N"+i,"I"+i)).ToList();Reject(()=>Run(Request()),"inventory budget");
        Reset();var apply=Apply();result=Run(apply);Check(calls==1&&result.Attempted&&result.Executed&&!result.RequiresSessionReset&&items.Count==2,"exactly one creation");
        foreach(var mutate in new Action<PlcDeviceAddRequest>[] {r=>r.Confirm=false,r=>r.ExpectedHash="",r=>r.ExpectedProject=@"C:\Other.ap19",r=>r.ProcessId=43,r=>r.RootIdentity="changed",r=>r.Name="changed",r=>r.Family="S7-1200"}) {Reset();apply=Apply();mutate(apply);Reject(()=>Run(apply),"changed bound field");Check(calls==0,"changed bound field no creation");}
        Reset();apply=Apply();items[0].Identity="replaced";Reject(()=>Run(apply),"identity replacement");Check(calls==0,"replacement no create");
        Reset();apply=Apply();rows[0].TypeIdentifier="OrderNumber:different/V1";Reject(()=>Run(apply),"catalog change");Check(calls==0,"catalog changed no create");
        Reset();apply=Apply();int checkNo=0;Reject(()=>Run(apply,check:()=>{if(++checkNo==2)items[0].Name="changed";}),"late inventory change");Check(calls==0,"late inventory no create");
        Reset();apply=Apply();result=Run(apply,(id,n)=>{calls++;throw new InvalidOperationException("native failed");});Check(calls==1&&result.Attempted&&!result.Executed&&result.RequiresSessionReset&&result.Status=="outcome-unknown","native exception once and unknown");
        Reset();apply=Apply();result=Run(apply,(id,n)=>{calls++;items.Add(Item(n,"two"));throw new InvalidOperationException("partial create");});Check(calls==1&&items.Count==2&&result.RequiresSessionReset,"partial create never retry or rollback");
        Reset();apply=Apply();result=Run(apply,(id,n)=>{calls++;return Item(n,"two");});Check(calls==1&&!result.Executed&&result.RequiresSessionReset,"missing created inventory unknown");
        Reset();apply=Apply();result=Run(apply,(id,n)=>{calls++;items.Clear();var created=Item(n,"two");items.Add(created);return created;});Check(calls==1&&result.RequiresSessionReset,"unexpected removal unknown");
        Reset();apply=Apply();result=Run(apply,(id,n)=>{calls++;var created=Item("renamed","two");items.Add(created);return created;});Check(calls==1&&result.RequiresSessionReset,"auto rename unknown");
        Reset();apply=Apply();result=Run(apply,(id,n)=>{calls++;var created=Item(n,"two");created.ParentVerified=false;items.Add(created);return created;});Check(calls==1&&result.RequiresSessionReset,"created parent unknown");
        Reset();apply=Apply();checkNo=0;result=Run(apply,check:()=>{if(++checkNo==4)throw new InvalidOperationException("project lost");});Check(calls==1&&result.RequiresSessionReset,"post-entry identity failure unknown");

        PlcFoundationEngine Engine(){var e=new PlcFoundationEngine();e.AttachedPortal.HardwareCatalog.Rows.Add(Candidate());return e;}
        PlcDeviceAddResult Preview(PlcFoundationEngine e)=>e.AddDeviceWithFallback("6ES7 513-1AM03-0AB0","V1","PLC_2");
        PlcDeviceAddResult Execute(PlcFoundationEngine e,string hash)=>e.AddDeviceWithFallback("6ES7 513-1AM03-0AB0","V1","PLC_2",dryRun:false,expectedPlanHash:hash,confirm:true,expectedProjectFile:e.BoundProject.Path.FullName);
        var engine=Engine();result=Preview(engine);Check(engine.BoundProject.Devices.Calls==0&&!result.Attempted,"adapter preview read-only");result=Execute(engine,result.PlanHash);Check(engine.BoundProject.Devices.Calls==1&&result.Executed,"adapter one exact create");
        engine=Engine();engine.Bound=false;Reject(()=>Preview(engine),"adapter unbound");Check(engine.AttachedPortal.HardwareCatalog.Calls==0,"unbound no query");
        var bindingRefusal=Xunit.Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(()=>Preview(engine));
        Check(!bindingRefusal.IsArgument && bindingRefusal.Message.Contains("ConnectPortal") && bindingRefusal.Message.Contains("AttachOpenProject"),"unbound typed connect instruction");
        engine=Engine();engine.AttachedPortal.HardwareCatalog.Rows.Clear();
        var rowRefusal=Xunit.Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(()=>Preview(engine));
        Check(rowRefusal.IsArgument && rowRefusal.ParamName=="preferredMlfb/preferredVersion" && rowRefusal.Message.Contains("SearchHardwareCatalog") && engine.BoundProject.Devices.Calls==0,"missing row typed argument before write");
        engine=Engine();result=Preview(engine);var reviewed=result.PlanHash;
        Check(Execute(engine,reviewed).Executed,"approval-disabled initial device creation");
        var duplicate=Xunit.Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(()=>Execute(engine,reviewed));
        Check(!duplicate.IsArgument && duplicate.ParamName=="deviceName" && engine.BoundProject.Devices.Calls==1,"approval-disabled duplicate refuses before second create");
        engine=Engine();result=Preview(engine);engine.BoundProject.Devices.Add(new(){Name="Other",Parent=engine.BoundProject});
        var stale=Xunit.Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(()=>Execute(engine,result.PlanHash));
        Check(stale.IsArgument && stale.ParamName=="expectedPlanHash" && engine.BoundProject.Devices.Calls==0,"approval-disabled stale reviewed plan refuses before create");
        engine=Engine();engine.ReleaseKey="18";Reject(()=>Preview(engine),"adapter release gate");Check(engine.AttachedPortal.HardwareCatalog.Calls==0,"release before query");
        engine=Engine();engine.lifecycle.IsLocalSession=false;Reject(()=>Preview(engine),"adapter local gate");
        engine=Engine();var group=new Siemens.Engineering.HW.DeviceUserGroup(){Parent=engine.BoundProject};group.Devices.Add(new(){Name="PLC_2",Parent=group});engine.BoundProject.DeviceGroups.Add(group);Reject(()=>Preview(engine),"grouped name collision");
        engine=Engine();engine.BoundProject.UngroupedDevicesGroup.Devices.Add(new(){Name="plc_2",Parent=engine.BoundProject.UngroupedDevicesGroup});Reject(()=>Preview(engine),"ungrouped name collision");
        engine=Engine();group=new(){Parent=engine.BoundProject};group.Groups.Add(group);engine.BoundProject.DeviceGroups.Add(group);Reject(()=>Preview(engine),"cyclic groups");
        engine=Engine();group=new(){Parent=engine.BoundProject};group.Devices.Add(new(){Name="existing",Parent=group});engine.BoundProject.DeviceGroups.Add(group);result=Preview(engine);Check(result.Inventory.SequenceEqual(new[]{"existing"}),"group inventory complete");Check(Execute(engine,result.PlanHash).Executed,"ordinary grouped project supported");
        engine=Engine();engine.BoundProject.Devices.ThrowAfterCreate=true;result=Execute(engine,Preview(engine).PlanHash);Check(result.RequiresSessionReset&&engine.BoundProject.Devices.Calls==1,"adapter unknown latch");Reject(()=>Preview(engine),"adapter replay quarantined");Check(engine.BoundProject.Devices.Calls==1,"quarantine prevents retry");
        Reset();var requestJson=System.Text.Json.Nodes.JsonNode.Parse("{\"preferredMlfb\":\"6ES7 513-1AM03-0AB0\",\"preferredVersion\":\"V1\",\"deviceName\":\"PLC_2\",\"family\":\"S7-1500\",\"dryRun\":true}")!.AsObject();
        var response=System.Text.Json.JsonSerializer.SerializeToNode(Run(Request()))!;
        Check(TiaMcp.FoundationHost.DeviceAddContract.Validate(response,requestJson)["Status"]!.GetValue<string>()=="planned","host preview validated");
        void BadResponse(string key,object value){var bad=response.DeepClone();bad[key]=System.Text.Json.JsonSerializer.SerializeToNode(value);try{TiaMcp.FoundationHost.DeviceAddContract.Validate(bad,requestJson);}catch(InvalidDataException){Check(true,"Host rejected "+key);return;}throw new Exception("Host accepted "+key);}
        foreach(var key in new[]{"Attempted","Executed","RequiresSessionReset"})BadResponse(key,true);
        BadResponse("Status","created-verified");BadResponse("DeviceName","different");BadResponse("TypeIdentifier","OrderNumber:different/V1");
        BadResponse("Version","V2");BadResponse("ArticleNumber","different");BadResponse("Policy","other");BadResponse("Save","saved");BadResponse("Inventory",new[]{"PLC_2"});BadResponse("Unexpected",true);BadResponse("Release","18");BadResponse("PlanHash","bad");
        requestJson["preferredMlfb"]=Candidate().TypeIdentifier;requestJson["preferredVersion"]="";BadResponse("TypeIdentifier","OrderNumber:different/V1");
        Reset();rows[0].ArticleNumber="6AV2123-3GB32-0AW0";rows[0].TypeIdentifier="OrderNumber:6AV2123-3GB32-0AW0/V1";var hmi=Request();hmi.PreferredMlfb=rows[0].TypeIdentifier!;hmi.PreferredVersion="";Reject(()=>Run(hmi),"HMI identifier cannot bypass PLC family gate");
        Reset();rows[0].TypeIdentifier+="*";Reject(()=>Run(Request()),"wildcard selected identifier fails");
        Reset();apply=Apply();items[0].ParentIdentity="moved-group";Reject(()=>Run(apply),"device move invalidates preview");Check(calls==0,"move no create");
        engine=Engine();result=Preview(engine);engine.BoundProject.DeviceGroups.Add(new(){Parent=engine.BoundProject});Reject(()=>Execute(engine,result.PlanHash),"new empty group invalidates preview");Check(engine.BoundProject.Devices.Calls==0,"group graph change no create");
        engine=Engine();group=new(){Parent=new object()};engine.BoundProject.DeviceGroups.Add(group);Reject(()=>Preview(engine),"group parent fails closed");
        engine=Engine();group=new(){Parent=engine.BoundProject};var subgroup=new Siemens.Engineering.HW.DeviceUserGroup(){Parent=group,Name="Sub"};group.Groups.Add(subgroup);engine.BoundProject.DeviceGroups.Add(group);result=Preview(engine);subgroup.Parent=engine.BoundProject;group.Groups.Clear();engine.BoundProject.DeviceGroups.Add(subgroup);Reject(()=>Execute(engine,result.PlanHash),"group movement invalidates preview");
        Reset();apply=Apply();result=Run(apply,(id,n)=>{calls++;throw new InvalidOperationException(new string('x',5000));});Check(result.Error.Length==2048&&result.RequiresSessionReset,"native diagnostic bounded without hiding unknown");
    }
}
