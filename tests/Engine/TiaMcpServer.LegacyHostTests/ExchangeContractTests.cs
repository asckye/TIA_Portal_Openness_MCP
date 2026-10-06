using System.Text.Json.Nodes;
using ModelContextProtocol;
using TiaMcp.Adapters.Contracts;
using TiaMcp.LegacyHost;
using TiaMcp.PlcFoundation;

internal static class ExchangeContractTests
{
    internal static void Run(Action<bool,string> check)
    {
        void Reject(Action action,string message) { try { action(); throw new Exception(message); } catch(ArgumentException) { check(true,message); } }
        var root=Path.Combine(Path.GetTempPath(),"tia-exchange-contract-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root,"Group A"));
            check(PlcExchangePolicy.ObjectPath("Group A/Motor")=="Group%20A/Motor","Raw group names normalize to exact address");
            check(PlcExchangePolicy.ObjectPath("Group%20A/Motor")=="Group%20A/Motor","Canonical encoding stable");
            check(PlcExchangePolicy.ObjectPath("",true)=="","Explicit import root accepted");
            foreach(var bad in new[]{"","/Motor","Group//Motor","Group/..","%2e%2e/Motor",@"Group\Motor"}) Reject(()=>PlcExchangePolicy.ObjectPath(bad),"Invalid object path refused "+bad);
            var flat=PlcExchangePolicy.ExportDestination(root,"Group A/Motor",false);
            var tree=PlcExchangePolicy.ExportDestination(root,"Group A/Motor",true);
            check(flat.FullName==Path.Combine(root,"Motor.xml"),"V17 directory export filename");
            check(tree.FullName==Path.Combine(root,"Group A","Motor.xml"),"Preserve group directories");
            check(!File.Exists(flat.FullName) && !File.Exists(tree.FullName),"Preview path planning creates no output");
            Reject(()=>PlcExchangePolicy.ExportDestination(root,"Missing/Motor",true),"Missing subdirectory explicitly refused");
            Reject(()=>PlcExchangePolicy.ExportDestination(root,"Group%2fEscape/Motor",true),"Encoded path separator cannot escape directory");
            File.WriteAllText(flat.FullName,"existing export");
            Reject(()=>PlcExchangePolicy.ExportDestination(root,"Motor",false),"Existing file not deleted or overwritten");
            check(File.ReadAllText(flat.FullName)=="existing export","Collision retains original bytes");
            Reject(()=>PlcExchangePolicy.Exact(new[]{"Root","Group"},x=>x,"Missing"),"Wrong group never falls back to root");
            Reject(()=>PlcExchangePolicy.Exact(new[]{"A","A"},x=>x,"A"),"Duplicate object refused");
            var tagTables=new[]{(Path:"devices/PLC/Tags/%E9%BB%98%E8%AE%A4%E5%8F%98%E9%87%8F%E8%A1%A8",Name:"默认变量表"),
                (Path:"devices/PLC/Tags/Archive",Name:"Archive")};
            check(PlcExchangePolicy.SelectTable(tagTables,"devices/PLC/Tags/%E9%BB%98%E8%AE%A4%E5%8F%98%E9%87%8F%E8%A1%A8",x=>x.Path,x=>x.Name).Name=="默认变量表","Exact encoded non-ASCII table path is selected");
            check(PlcExchangePolicy.SelectTable(tagTables,"默认变量表",x=>x.Path,x=>x.Name).Name=="默认变量表","Unique raw non-ASCII table name is selected");
            Reject(()=>PlcExchangePolicy.SelectTable(tagTables,"Missing",x=>x.Path,x=>x.Name),"Missing table selector is refused");
            Reject(()=>PlcExchangePolicy.SelectTable(tagTables.Concat(new[]{(Path:"devices/PLC/Tags/Other/%E9%BB%98%E8%AE%A4%E5%8F%98%E9%87%8F%E8%A1%A8",Name:"默认变量表")}),"默认变量表",x=>x.Path,x=>x.Name),"Ambiguous raw table name is refused");
            Reject(()=>PlcExchangePolicy.SelectTable(tagTables,"devices/PLC/Tags/默认变量表",x=>x.Path,x=>x.Name),"Unencoded hierarchical table path is not treated as a raw name");
            var xml=Path.Combine(root,"Input.xml");
            File.WriteAllText(xml,"<Document><Engineering version=\"V21\" /></Document>");
            var bytes=File.ReadAllBytes(xml);
            foreach(var release in new[]{"14sp1","15.1","16","17","18","19","20","21"})
            {
                PlcFoundationPolicy.RequireRelease(release,release);
                PlcFoundationPolicy.XmlInput(xml);
                check(File.ReadAllBytes(xml).SequenceEqual(bytes),"Input XML not rewritten for "+release);
                var mismatch=release=="21"?"20":"21";
                try { PlcFoundationPolicy.RequireRelease(release,mismatch); throw new Exception("Version mismatch allowed"); } catch(InvalidOperationException) { check(true,"Cross-version import facade refused"); }
            }
            File.WriteAllText(xml,"<!DOCTYPE Document [<!ENTITY e SYSTEM 'file:///unavailable'>]><Document>&e;</Document>");
            try { PlcFoundationPolicy.XmlInput(xml); throw new Exception("External entity accepted"); }
            catch(AdapterPreconditionException ex) { check(ex.ParamName=="importPath","DTD rejected as an importPath precondition before native import"); }
            File.WriteAllText(xml,"<WrongRoot />");
            Reject(()=>PlcFoundationPolicy.XmlInput(xml),"Wrong XML root refused");
            foreach(var tool in new[]{"ImportBlock","ImportType","ImportPlcTagTable","ExportBlock","ExportType","ExportPlcTagTable"})
            {
                var def=FoundationTools.Definitions.Single(d=>d.Name==tool);
                check(def.Arguments[0].Name=="softwarePath","V17 softwarePath preserved "+tool);
                check(def.Arguments.Single(a=>a.Name=="dryRun").Default is true,"Preview default "+tool);
                if(tool.StartsWith("Import")) check(def.Arguments.Single(a=>a.Name=="overwrite").Default is false,"No overwrite default "+tool);
            }
            string Wire(string name)=>McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(name)??name;
            var payload=new JsonObject { ["Executed"]=false,["ProjectFile"]=@"C:\Projects\P.ap17",["OutputFile"]=@"C:\Exports\P.xml" };
            var preview=V17MutationEnvelope.Wrap("ExportPlcTagTable","ExportFile",payload,true);
            check(!preview[Wire("Meta")]!["executed"]!.GetValue<bool>() && preview[Wire("Message")]!.GetValue<string>().Contains("no operation executed"),"Preview never claims execution");
            check(preview[Wire("ExportPath")]!.GetValue<string>()==@"C:\Exports\P.xml","V17 ExportPath returned");
            try { V17MutationEnvelope.Wrap("ImportBlock","Mutation",payload,false); throw new Exception("Wrong execution outcome accepted"); } catch(InvalidDataException) { check(true,"Execution outcome mismatch rejected"); }
            payload["Executed"]=true;
            check(V17MutationEnvelope.Wrap("ImportBlock","Mutation",payload,false)[Wire("Meta")]!["executed"]!.GetValue<bool>(),"Verified execution reported");
            var arguments=new JsonObject { ["dryRun"]=false,["expectedProjectFile"]=@"C:\Projects\P.ap17" };
            WorkerProtocol.ValidateExchangeResult("ImportBlocks",arguments,payload);
            check(true,"Result identity and requested execution agree");
            arguments["expectedProjectFile"]=@"C:\Projects\Other.ap17";
            try { WorkerProtocol.ValidateExchangeResult("ImportBlocks",arguments,payload); throw new Exception("Mismatched output identity accepted"); }
            catch(IOException ex) { check(WorkerProtocol.RequiresSessionReset(true,ex),"Mismatched result poisons session before releasing worker lock"); }
            payload.Remove("ProjectFile");
            try { V17MutationEnvelope.Wrap("ImportBlock","Mutation",payload,false); throw new Exception("Missing identity accepted"); } catch(InvalidDataException) { check(true,"Missing result project identity rejected"); }
        }
        finally
        {
            // Delete only the fresh test directory with its verified dedicated prefix.
            if(Path.GetFileName(root).StartsWith("tia-exchange-contract-",StringComparison.Ordinal) && Path.GetDirectoryName(root)==Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)) Directory.Delete(root,true);
        }
    }
}
