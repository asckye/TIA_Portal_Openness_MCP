using TiaMcp.PlcFoundation;
using System.Text;

internal static class ExternalSourcePlanPolicyTests
{
    internal static void Run(Action<bool,string> Check)
    {
        void Reject(Action action,string label) { try {action();} catch(Exception e) when(e is ArgumentException || e is IOException || e is InvalidOperationException || e is NotSupportedException) {Check(true,label);return;} throw new Exception("Accepted: "+label); }
        PlcExternalSourceImportRequest Request()=>new(){Release="21",Project=@"C:\Projects\Demo.ap21",ProcessId=123,Software="Device/PLC",File=@"C:\Sources\Pump.scl",AllowedFile=@"C:\Sources\Pump.scl"};
        PlcExternalSourceImportPlan Run(PlcExternalSourceImportRequest r,byte[]? bytes=null,string[]? names=null,Action? check=null)=>PlcExternalSourceImportPolicy.Plan(r,_=>new MemoryStream(bytes ?? Encoding.ASCII.GetBytes("FUNCTION Test : Void\r\nEND_FUNCTION\r\n")),()=>names ?? Array.Empty<string>(),check ?? (()=>{}));
        var baseline=Run(Request());
        Check(baseline.Status=="planned" && !baseline.Attempted && !baseline.Executed && baseline.CreatedCount==0 && baseline.ApplyBlocked,"honest plan outcome");
        Check(baseline.Generation=="notRun" && baseline.Compilation=="notRun" && baseline.Save=="notRun" && baseline.Download=="notRun","no derived success");
        Check(baseline.SourceName=="Pump.scl" && baseline.GroupPath=="" && baseline.PlanHash.Length==64 && baseline.InputSha256.Length==64,"bound plan identity");
        Check(baseline.PlanHash==Run(Request()).PlanHash,"deterministic plan");
        foreach(var release in new[]{"14sp1","15.1","16","17","18","19","20","21"}) {var r=Request();r.Release=release;Check(Run(r).Release==release,"release "+release);}
        foreach(var release in new[]{"14","15","V21","21.0",""}) {var r=Request();r.Release=release;Reject(()=>Run(r),"excluded release");}
        foreach(var ext in new[]{"scl","SCL","awl","db","udt"}) {var r=Request();r.File=r.AllowedFile=@"C:\Sources\Pump."+ext;Check(Run(r).Extension=="."+ext.ToLowerInvariant(),"extension");}
        foreach(var path in new[]{@"C:Pump.scl",@"\Pump.scl",@"\\server\share\Pump.scl",@"\\?\C:\Pump.scl",@"C:\Sources\..\Pump.scl",@"C:\Sources\\Pump.scl",@"C:\Sources\.\Pump.scl",@"C:\Sources\Pump.scl:stream",@"C:\Sources\CON.scl",@"C:\Sources\LPT¹.scl",@"C:\Sources\Pump.scl.",@"C:\Sources\Pump.scl ",@"C:\Sources\*.scl",@"C:\Sources\Pump.xml",@"C:\Sources\Pump.scl\",@"C:/Sources/Pump.scl",@"C:\Sources\.scl"}) {var r=Request();r.File=r.AllowedFile=path;Reject(()=>Run(r),"path "+path);}
        foreach(var group in new[]{"/","root","Group",".."}) {var r=Request();r.Group=group;Reject(()=>Run(r),"nonroot");}
        foreach(var path in new[]{@"C:\Sources\Other.scl",@"c:\Sources\Pump.scl"}) {var r=Request();r.AllowedFile=path;Reject(()=>Run(r),"allowlist mismatch");}
        foreach(var bytes in new[]{Array.Empty<byte>(),new byte[]{0},new byte[]{127},new byte[]{128},new byte[]{239,187,191,65},new byte[]{11},new byte[]{12},new byte[]{1},new byte[4194305]}) Reject(()=>Run(Request(),bytes),"encoding/size");
        Check(Run(Request(),new byte[]{9,10,13,32,126}).ByteCount==5,"allowed ASCII subset");
        var max=Enumerable.Repeat((byte)' ',4194304).ToArray();Check(Run(Request(),max).ByteCount==4194304,"maximum bytes");
        foreach(var name in new[]{"Pump.scl","PUMP.SCL","pump.awl","Pump","pUmP.db"}) Reject(()=>Run(Request(),names:new[]{name}),"conservative collision");
        foreach(var names in new[]{new[]{"Other.scl","OTHER.SCL"},new[]{""},new[]{"Group/Other.scl"},Enumerable.Range(0,4097).Select(x=>"Other"+x).ToArray()}) Reject(()=>Run(Request(),names:names),"incomplete/ambiguous inventory");
        Check(Run(Request(),names:Enumerable.Range(0,4096).Select(x=>"Other"+x).ToArray()).InventoryCount==4096,"inventory bound exact");
        Check(Run(Request(),names:new[]{"B","A"}).PlanHash==Run(Request(),names:new[]{"A","B"}).PlanHash,"stable sorted inventory");
        Reject(()=>PlcExternalSourceImportPolicy.Plan(Request(),_=>new MemoryStream(new byte[]{65}),()=>Broken(),()=>{}),"inventory read error");
        IEnumerable<string> Broken(){yield return "Other";throw new IOException("incomplete");}
        var n=0;Reject(()=>PlcExternalSourceImportPolicy.Plan(Request(),_=>new MemoryStream(new byte[]{65}),()=>++n==1?new[]{"A"}:new[]{"B"},()=>{}),"inventory change");
        Reject(()=>Run(Request(),check:()=>throw new InvalidOperationException("changed target")),"target recheck");
        var r2=Request();r2.ExpectedProject=@"C:\Projects\Other.ap21";Reject(()=>Run(r2),"changed project");
        foreach(var alter in new Action<PlcExternalSourceImportRequest>[] {r=>r.ProcessId=456,r=>r.Release="20",r=>r.Software="Other/PLC",r=>r.Project=@"C:\Projects\Other.ap21",r=>r.File=r.AllowedFile=@"C:\Sources\Other.scl"}) {var r=Request();alter(r);Check(Run(r).PlanHash!=baseline.PlanHash,"hash identity binding");}
        var reviewed=Request();reviewed.Confirm=true;reviewed.ExpectedProject=reviewed.Project;reviewed.ExpectedHash=baseline.PlanHash;Check(Run(reviewed).PlanHash==baseline.PlanHash,"reviewed unchanged plan");
        reviewed.Confirm=false;Reject(()=>Run(reviewed),"confirmation required");reviewed.Confirm=true;reviewed.ExpectedProject="";Reject(()=>Run(reviewed),"expected project required");reviewed.ExpectedProject=reviewed.Project;Reject(()=>Run(reviewed,new byte[]{65}),"changed input hash");
        var apply=Request();apply.DryRun=false;apply.Confirm=true;apply.ExpectedProject=apply.Project;apply.ExpectedHash=baseline.PlanHash;
        int opened=0,read=0,rechecked=0;Reject(()=>PlcExternalSourceImportPolicy.Plan(apply,_=>{opened++;return new MemoryStream(new byte[]{65});},()=>{read++;return Array.Empty<string>();},()=>rechecked++),"apply hard block");Check(opened==0 && read==0 && rechecked==0,"apply refuses before all I/O");
        var mutable=new MemoryStream(new byte[]{65});Reject(()=>PlcExternalSourceImportPolicy.Plan(Request(),_=>mutable,()=>Array.Empty<string>(),()=>{mutable.Position=0;mutable.WriteByte(66);}),"immutable bytes rehash");Check(!mutable.CanRead,"lock disposed after failure");
    }
}
