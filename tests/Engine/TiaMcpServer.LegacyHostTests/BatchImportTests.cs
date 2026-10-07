using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.LegacyHost;
using TiaMcp.PlcFoundation;

internal static class BatchImportTests
{
    internal static JsonObject Payload(bool program)=>JsonSerializer.SerializeToNode(new PlcBatchImportResult {ProjectFile="C:/Projects/project.ap17",SoftwarePath="exact/path",Release="17",PlanHash=new string('a',64),Recursive=program,Items=new[]{new PlcBatchImportItem {RelativePath="a.xml",InputSha256=new string('b',64),Planned=new PlcBatchImportObject {Name="A",Kind="FC",GroupPath=program ? "" : "exact/path"}}}})!.AsObject();
    internal static string Xml(string name,string kind="FC",int? number=null,string extra="",string producer="V17",string language="LAD")=>$"<Document><Engineering version=\"{producer}\"/><{(kind=="UDT" ? "SW.Types.PlcStruct" : kind=="TagTable" ? "SW.Tags.PlcTagTable" : "SW.Blocks."+kind)}><AttributeList><Name>{name}</Name><ProgrammingLanguage>{language}</ProgrammingLanguage>{(number.HasValue ? $"<Number>{number}</Number>" : "")}</AttributeList>{extra}</{(kind=="UDT" ? "SW.Types.PlcStruct" : kind=="TagTable" ? "SW.Tags.PlcTagTable" : "SW.Blocks."+kind)}></Document>";
    internal static void Run(Action<bool,string> check,Action<string> skip)
    {
        var root=Path.Combine(Path.GetTempPath(),"tia-batch-import-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        int imports=0,gates=0;
        PlcBatchImportRequest Request()=>new(){Release="17",Project="C:/Projects/project.ap17",ExpectedProject="C:/Projects/project.ap17",ProcessId=123,Software="devices/PLC",Directory=root};
        PlcBatchImportObject[] Import(FileInfo file,PlcBatchImportObject planned) {imports++;return new[]{new PlcBatchImportObject {Name=planned.Name,Kind=planned.Kind,GroupPath=planned.GroupPath,Number=planned.Number ?? 900+imports}};}
        PlcBatchImportResult Run(PlcBatchImportRequest r,IEnumerable<PlcBatchImportObject>? existing=null,Func<FileInfo,PlcBatchImportObject,PlcBatchImportObject[]>? import=null,Action? gate=null)=>PlcBatchImportPolicy.Run(r,existing??Array.Empty<PlcBatchImportObject>(),gate??(()=>gates++),import??Import);
        void Reject(Action action,string name) {try{action();throw new Exception("Accepted: "+name);}catch(Exception ex) when(ex is ArgumentException or NotSupportedException or System.Xml.XmlException or IOException or InvalidDataException){check(true,name);}}
        void Reset(){foreach(var path in Directory.GetFileSystemEntries(root)) {if(Directory.Exists(path))Directory.Delete(path,true);else File.Delete(path);}imports=0;gates=0;}
        void Files(){File.WriteAllText(Path.Combine(root,"A.xml"),Xml("LogicalZ",number:1));File.WriteAllText(Path.Combine(root,"B.xml"),Xml("LogicalA",number:2));File.WriteAllText(Path.Combine(root,"C.xml"),Xml("Last",number:3));}
        PlcBatchImportRequest Apply(PlcBatchImportResult preview){var r=Request();r.DryRun=false;r.Confirm=true;r.ExpectedHash=preview.PlanHash;r.Order=preview.Items.Select(x=>x.RelativePath).ToArray();return r;}
        JsonObject Wire(PlcBatchImportResult r)=>JsonSerializer.SerializeToNode(r)!.AsObject();
        try
        {
            var missing=Request();missing.Program=true;missing.Directory=Path.Combine(root,"absent");
            try {Run(missing);throw new Exception("Missing input accepted");}
            catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException error)
            {check(error.IsArgument && error.ParamName=="sourceDir" && imports==0,"Program directory input failure retains the actual public file argument before import");}
            Files();var preview=Run(Request());var original=Directory.GetFiles(root).ToDictionary(x=>x,File.ReadAllBytes);
            check(imports==0 && gates==0 && preview.Items.Select(x=>x.Planned.Name).SequenceEqual(new[]{"LogicalZ","LogicalA","Last"}),"Preview reads actual XML names, invokes no writes/gates and does not infer filename identity");
            check(preview.DependencyStatus=="unverified-caller-order-required" && !preview.Executed,"Suggested ordinal ordering explicitly has no dependency proof");
            BatchImportContract.Validate(Wire(preview),true);check(true,"Host accepts bounded import preview");
            var reverse=Request();reverse.Order=new[]{"C.xml","B.xml","A.xml"};var reversed=Run(reverse);check(reversed.PlanHash!=preview.PlanHash && reversed.Items[0].RelativePath=="C.xml","Caller ordering retained and hashed");
            var changes=new Action<PlcBatchImportRequest>[] {r=>r.Software="other",r=>r.BlockGroup="other",r=>r.ProcessId=456,r=>r.Project="other",r=>r.Regex=".*"};
            foreach(var change in changes){var r=Request();change(r);check(Run(r).PlanHash!=preview.PlanHash,"Target/scope identity participates in plan hash");}
            var bad=Apply(preview);bad.Order=Array.Empty<string>();Reject(()=>Run(bad),"Apply without explicit order refused");
            bad=Apply(preview);bad.Order=new[]{"A.xml","A.xml","C.xml"};Reject(()=>Run(bad),"Repeated/omitted paths rejected");
            bad=Apply(preview);bad.Order=new[]{"../A.xml","B.xml","C.xml"};Reject(()=>Run(bad),"Traversal order rejected");
            bad=Apply(preview);bad.ExpectedProject="C:/Projects/other.ap17";Reject(()=>Run(bad),"Exact project mismatch refused");
            bad=Apply(preview);bad.Confirm=false;Reject(()=>Run(bad),"Confirmation required");
            foreach(var change in new Action<PlcBatchImportRequest>[] {r=>r.CompileAfter=true,r=>r.StopOnImportFailure=false,r=>r.TechnologyGroup="technology"}) {bad=Apply(preview);change(bad);bad.Directory="missing";Reject(()=>Run(bad),"Unsupported option rejected before directory/native access");}
            check(imports==0 && gates==0,"All invalid apply options stop before native callback");
            File.AppendAllText(Path.Combine(root,"A.xml")," ");Reject(()=>Run(Apply(preview)),"Changed file bytes invalidate consent");File.WriteAllBytes(Path.Combine(root,"A.xml"),original[Path.Combine(root,"A.xml")]);
            File.WriteAllText(Path.Combine(root,"D.xml"),Xml("Added"));Reject(()=>Run(Apply(preview)),"Added file invalidates complete order");File.Delete(Path.Combine(root,"D.xml"));
            Reject(()=>Run(Request(),new[]{new PlcBatchImportObject {Name="LogicalZ",Kind="FB",GroupPath="other"}}),"Cross-group/name collision blocks filename mismatch bypass");
            Reject(()=>Run(Request(),new[]{new PlcBatchImportObject {Name="Other",Kind="FC",Number=1}}),"Fixed number collision is blocked");
            check(Run(Request(),new[]{new PlcBatchImportObject {Name="Other",Kind="FB",Number=1}}).Items.Length==3,"FC1 and FB1 are separate number namespaces");
            try {Run(Apply(preview),gate:()=>PlcOfflinePolicy.RequireStates(new[]{"Online"},true,"selected PLC"));throw new Exception("Policy refusal accepted");}
            catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException error)
            {check(!error.IsArgument && imports==0 && gates==0,"Known offline refusal before first import stays a precondition and issues no import");}
            var result=Run(Apply(preview));check(result.ImportedCount==3 && !result.RequiresSessionReset && imports==3 && gates==3,"Fake native callbacks verify all identities and gate every item");
            check(original.All(x=>File.ReadAllBytes(x.Key).SequenceEqual(x.Value)),"Input bytes stay unchanged throughout preview/apply");
            imports=0;var partial=Run(Apply(preview),import:(f,p)=>{if(++imports==2)throw new IOException("uncertain write");return new[]{p};});
            check(partial.ImportedCount==1 && partial.FailedCount==1 && partial.RequiresSessionReset && partial.Items.Select(x=>x.Status).SequenceEqual(new[]{"imported","failed","not-attempted"}) && imports==2,"Second failure preserves first result, stops before third, never retries");
            BatchImportContract.Validate(Wire(partial),false);check(true,"Host retains valid partial outcome");
            var args=new JsonObject {["dryRun"]=false,["expectedProjectFile"]=preview.ProjectFile,["softwarePath"]=preview.SoftwarePath,["groupPath"]="",["expectedPlanHash"]=preview.PlanHash,["importOrder"]=JsonSerializer.SerializeToNode(Apply(preview).Order)};
            var state=new WorkerOutcomeState();state.AcceptResult("ImportBlocksFromDirectory",args,Wire(partial));check(state.Poisoned,"Host becomes poisoned after retained partial import");
            Reject(()=>{var wire=Wire(partial);wire["RequiresSessionReset"]=false;BatchImportContract.Validate(wire,false);},"Reset/count conflicts rejected");
            foreach(var returned in new PlcBatchImportObject[]?[] {null,Array.Empty<PlcBatchImportObject>(),new[]{new PlcBatchImportObject {Name="Wrong",Kind="FC"}},new[]{preview.Items[0].Planned,preview.Items[1].Planned},new[]{new PlcBatchImportObject {Name="LogicalZ",Kind="unknown-native-kind",GroupPath="",Number=1}},new PlcBatchImportObject[]{null!}})
            {
                imports=0;var mismatch=Run(Apply(preview),import:(_,_)=>{imports++;return returned!;});
                check(mismatch.RequiresSessionReset && imports==1 && mismatch.ImportedCount==0 && mismatch.Items[0].ReturnedObjects.Length==(returned?.Length??0),"Null/empty/multiple/mismatched native identities stop and preserve returned evidence");
                BatchImportContract.Validate(Wire(mismatch),false);check(true,"Unexpected native identities remain visible in the host partial result");
            }
            imports=0;var unexpectedSecond=Run(Apply(preview),import:(_,p)=>++imports==2 ? new[]{new PlcBatchImportObject {Name=p.Name,Kind="UnexpectedNativeBlock",GroupPath="<unverified-native-owner>"}} : new[]{p});
            BatchImportContract.Validate(Wire(unexpectedSecond),false);
            check(unexpectedSecond.ImportedCount==1 && unexpectedSecond.FailedCount==1 && imports==2 && unexpectedSecond.Items[1].ReturnedObjects[0].Kind=="UnexpectedNativeBlock" && unexpectedSecond.Items[2].Status=="not-attempted","Unexpected second native kind/owner preserves first success and raw evidence through host contract");
            gates=0;imports=0;var online=Run(Apply(preview),gate:()=>{if(++gates==2)throw new InvalidOperationException("online/project swap");});check(online.ImportedCount==1 && imports==1 && !online.Items[1].Attempted && online.RequiresSessionReset,"Target changes before second import preserve first mutation and stop without another callback");
            gates=0;imports=0;var refused=Run(Apply(preview),gate:()=>{if(++gates==2)PlcOfflinePolicy.RequireStates(new[]{"Online"},true,"selected PLC");});check(refused.ImportedCount==1 && imports==1 && !refused.Items[1].Attempted && refused.RequiresSessionReset,"Typed policy refusal after first import retains prior mutation and requires reset");
            Reset();File.WriteAllText(Path.Combine(root,"A.xml"),Xml("Duplicate"));File.WriteAllText(Path.Combine(root,"B.xml"),Xml("Duplicate"));Reject(()=>Run(Request()),"Duplicate logical identity is rejected, never silently deduplicated");
            Reset();File.WriteAllText(Path.Combine(root,"A.xml"),Xml("A"));File.WriteAllText(Path.Combine(root,"a.xml"),Xml("B"));if(Directory.GetFiles(root).Length==2)Reject(()=>Run(Request()),"Windows case aliases refused on Linux fake filesystem");
            Reset();File.WriteAllText(Path.Combine(root,"A.xml"),Xml("A"));var limits=Request();limits.MaxItems=0;Reject(()=>Run(limits),"Invalid maxItems refused");
            File.WriteAllText(Path.Combine(root,"B.xml"),Xml("B"));limits=Request();limits.MaxItems=1;Reject(()=>Run(limits),"File count bounded without truncation");
            Reset();var malformed=new[]{"<!DOCTYPE Document [<!ENTITY x 'bad'>]><Document>&x;</Document>","<Document><Engineering version=\"V17\"/><SW.Blocks.FC/><SW.Types.PlcStruct/></Document>",Xml("A").Replace("SW.Blocks.FC","Unknown"),Xml("A",producer:"V21"),Xml("A",language:"F_LAD"),Xml("A",language:"SCL"),Xml("A",extra:"<KnowHowProtection>true</KnowHowProtection>"),"<Document>"+string.Concat(Enumerable.Repeat("<x>",65))+string.Concat(Enumerable.Repeat("</x>",65))+"</Document>"};
            foreach(var xml in malformed){File.WriteAllText(Path.Combine(root,"A.xml"),xml);Reject(()=>Run(Request()),"DTD/multiple/unknown/cross-release/Safety/deep XML refused");}
            File.WriteAllText(Path.Combine(root,"A.xml"),Xml("A",producer:"V14 SP1",language:"SCL"));var v14=Request();v14.Release="14sp1";Reject(()=>Run(v14),"V14 SP1 SCL interface-only import refused");
            Reset();File.WriteAllText(Path.Combine(root,"A.xml"),new string('x',(int)PlcBatchImportPolicy.MaximumFileBytes+1));Reject(()=>Run(Request()),"Oversized file refused before XML parsing");
            Reset();Directory.CreateDirectory(Path.Combine(root,"nested"));File.WriteAllText(Path.Combine(root,"A.xml"),Xml("Block"));File.WriteAllText(Path.Combine(root,"nested","B.xml"),Xml("Type","UDT"));check(Run(Request()).Items.Length==1,"Blocks wrapper scans top directory only");var program=Request();program.Program=true;check(Run(program).Items.Length==2,"Program wrapper scans bounded recursive ordinary XML");
            Reset();File.WriteAllText(Path.Combine(root,"A.xml"),Xml("Dependent","UDT",extra:"<Member Datatype=\"&quot;Base&quot;\"/>"));File.WriteAllText(Path.Combine(root,"Z.xml"),Xml("Base","UDT"));program=Request();program.Program=true;Reject(()=>Run(program),"Nested UDT does not become dependency-safe through alphabetical sorting");Reject(()=>Run(program,new[]{new PlcBatchImportObject {Name="Base",Kind="FB"}}),"FB cannot satisfy a recognized UDT dependency");program.Order=new[]{"Z.xml","A.xml"};check(Run(program).Items[1].RecognizedDependencies.SequenceEqual(new[]{"UDT:Base"}),"Explicit dependency order accounts for recognized UDT references");
            File.WriteAllText(Path.Combine(root,"Z.xml"),Xml("Base","UDT",extra:"<Member Datatype=\"&quot;Dependent&quot;\"/>"));Reject(()=>Run(program),"Recognized cycle rejected rather than reordered/retried");
            Reset();File.WriteAllText(Path.Combine(root,"A.xml"),Xml("DB","InstanceDB",extra:"<InstanceOfName>MissingFB</InstanceOfName>"));Reject(()=>Run(Request()),"Missing instance FB is not synthesized");Reject(()=>Run(Request(),new[]{new PlcBatchImportObject {Name="MissingFB",Kind="UDT"}}),"UDT cannot satisfy an instance FB dependency");
            Reset();File.WriteAllText(Path.Combine(root,"A.xml"),Xml("NoNumber"));check(Run(Request()).Items[0].Planned.Number==null,"Absent number explicitly records native automatic assignment");
            if(!OperatingSystem.IsWindows()){File.CreateSymbolicLink(Path.Combine(root,"link.xml"),Path.Combine(root,"A.xml"));Reject(()=>Run(Request()),"Symlink input refused before native callback");}
            skip("Batch imports use fake native callbacks only; Windows read-lock behavior, version-specific SDK builds, XSD/native acceptance and actual Siemens execution were not tested.");
        }
        finally {Directory.Delete(root,true);}
    }
}
