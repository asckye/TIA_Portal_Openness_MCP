using TiaMcp.PlcFoundation;

internal static class ExternalSourceDeletePolicyTests
{
    internal static void Run(Action<bool,string> Assert)
    {
        void Reject(Action action,string label) {try{action();}catch(ArgumentException){Assert(true,label);return;}catch(InvalidOperationException){Assert(true,label);return;}throw new Exception("Expected rejection: "+label);}
        PlcExternalSourceDeleteRequest Request()=>new(){Release="14sp1",Project=@"C:\Test\Test.ap14",ProcessId=7,Software="PLC",Name="Pump.scl",RootIdentity="root-session-1"};
        PlcExternalSourceDeleteItem Item(string name="Pump.scl",string id="a",bool parent=true)=>new(){Name=name,Identity=id,ParentVerified=parent};
        foreach(var release in new[]{"14sp1","15.1","16","17","18","19","20","21"}) {
         var r=Request();r.Release=release;var items=new List<PlcExternalSourceDeleteItem>{Item(),Item("Pump.awl","b")};int calls=0;
         PlcExternalSourceDeleteResult Run()=>PlcExternalSourceDeletePolicy.Run(r,()=>items,()=>{},_=>{},id=>{calls++;items.RemoveAll(x=>x.Identity==id);});
         var plan=Run();Assert(plan.Status=="planned"&&!plan.Attempted&&calls==0&&plan.Inventory.Length==2,"preview "+release);
         r.DryRun=false;r.Confirm=true;r.ExpectedProject=r.Project;r.ExpectedHash=plan.PlanHash;
         var deleted=Run();Assert(deleted.Deleted&&deleted.Attempted&&deleted.Executed&&!deleted.RequiresSessionReset&&calls==1&&items.Single().Name=="Pump.awl","single exact delete "+release);
        }
        foreach(var configure in new Action<PlcExternalSourceDeleteRequest>[] {r=>r.Release="14",r=>r.Release="15",r=>r.Group="child",r=>r.Name="../Pump",r=>r.DryRun=false,r=>{r.DryRun=false;r.Confirm=true;r.ExpectedHash=new string('a',64);}}) {var r=Request();configure(r);Reject(()=>PlcExternalSourceDeletePolicy.Run(r,()=>new[]{Item()},()=>{},_=>{},_=>throw new Exception("native reached")),"options");}
        foreach(var items in new[]{new[]{Item(),Item("Pump.scl","b")},new[]{Item(parent:false)},Enumerable.Range(0,4097).Select(i=>Item(i.ToString(),i.ToString())).ToArray(),new[]{Item(),Item("Other","a")}}) Reject(()=>PlcExternalSourceDeletePolicy.Run(Request(),()=>items,()=>{},_=>{},_=>{}),"bad inventory");
        foreach(var name in new[]{"Pump","pump.scl","Pump.SCL"}) {var r=Request();r.Name=name;var result=PlcExternalSourceDeletePolicy.Run(r,()=>new[]{Item()},()=>{},_=>{},_=>throw new Exception());Assert(result.Status=="not-found-not-deleted"&&!result.Deleted&&!result.Attempted,"no fuzzy fallback");}
        foreach(var change in new Action<PlcExternalSourceDeleteRequest,List<PlcExternalSourceDeleteItem>>[]{(r,l)=>r.Project=@"C:\Other\Other.ap14",(r,l)=>r.ProcessId++,(r,l)=>r.RootIdentity="new-root",(r,l)=>r.Software="other",(r,l)=>l[0].Identity="replacement",(r,l)=>l.Add(Item("new","b")),(r,l)=>l.Clear()}) {var r=Request();var items=new List<PlcExternalSourceDeleteItem>{Item()};var p=PlcExternalSourceDeletePolicy.Run(r,()=>items,()=>{},_=>{},_=>{});r.DryRun=false;r.Confirm=true;r.ExpectedProject=r.Project;r.ExpectedHash=p.PlanHash;change(r,items);Reject(()=>PlcExternalSourceDeletePolicy.Run(r,()=>items,()=>{},_=>{},_=>throw new Exception()),"changed identity");}
        foreach(var mode in new[]{"throw","retained","wrong-removal","readback-throw","changed-parent"}) {
         var r=Request();var items=new List<PlcExternalSourceDeleteItem>{Item(),Item("Other","b")};bool entered=false;int calls=0;
         IEnumerable<PlcExternalSourceDeleteItem> Read(){if(entered&&mode=="readback-throw")throw new InvalidOperationException("lost response");return items;}
         var p=PlcExternalSourceDeletePolicy.Run(r,Read,()=>{},_=>{},_=>{});r.DryRun=false;r.Confirm=true;r.ExpectedProject=r.Project;r.ExpectedHash=p.PlanHash;
         var result=PlcExternalSourceDeletePolicy.Run(r,Read,()=>{},_=>{},id=>{calls++;entered=true;if(mode=="throw")throw new InvalidOperationException("native error");if(mode=="wrong-removal")items.Clear();if(mode=="readback-throw")items.RemoveAt(0);if(mode=="changed-parent")items[1].ParentVerified=false;});
         Assert(result.Status=="outcome-unknown"&&result.RequiresSessionReset&&!result.Deleted&&!result.Executed&&result.Attempted&&calls==1,"unknown no retry "+mode);
        }
        {
         var r=Request();var p=PlcExternalSourceDeletePolicy.Run(r,()=>new[]{Item()},()=>{},_=>{},_=>{});r.DryRun=false;r.Confirm=true;r.ExpectedProject=r.Project;r.ExpectedHash=p.PlanHash;
         Reject(()=>PlcExternalSourceDeletePolicy.Run(r,()=>new[]{Item()},()=>{},_=>throw new InvalidOperationException("changed exact Find"),_=>throw new Exception("native reached")),"pre-native identity check");
         int reads=0;Reject(()=>PlcExternalSourceDeletePolicy.Run(r,()=>++reads==1?new[]{Item()}:new[]{Item(id:"replacement")},()=>{},_=>{},_=>throw new Exception("native reached")),"changed final inventory");
        }
        var tokens=new PlcExternalSourceDeleteIdentities();
        Assert(tokens.Get(new Proxy("one"))==tokens.Get(new Proxy("one")),"same logical object across recreated proxies");
        Assert(tokens.Get(new Proxy("one"))!=tokens.Get(new Proxy("replacement")),"same name new native identity differs");
        var throwingTokens=new PlcExternalSourceDeleteIdentities();throwingTokens.Get(new ThrowingProxy());
        Reject(()=>throwingTokens.Get(new Proxy("other")),"equality failure propagates without fallback");
    }

    sealed class Proxy(string identity) { public override bool Equals(object? other)=>other is Proxy p&&p.Identity==Identity;private string Identity=>identity;public override int GetHashCode()=>identity.GetHashCode();}

    sealed class ThrowingProxy { public override bool Equals(object? other)=>throw new InvalidOperationException("native equality unavailable");public override int GetHashCode()=>0;}
}
