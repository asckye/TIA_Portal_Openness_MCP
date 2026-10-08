using TiaMcp.Adapters;

internal static class HardwareCatalogTests
{
    internal static void Run(Action<bool,string> Check)
    {
        void Reject(Action action,string name) { try { action(); } catch(ArgumentException) { Check(true,name); return; } catch(InvalidOperationException) { Check(true,name); return; } catch(NotSupportedException) { Check(true,name); return; } throw new Exception("Expected rejection: "+name); }
        PlcHardwareCatalogCandidate Row(string id)=>new(){ TypeIdentifier=id,ArticleNumber="6ES7",Description="CPU",TypeIdentifierNormalized=id.Replace(" ",""),Version="V1.0" };
        var calls=0;
        PlcHardwareCatalogSearchResult Run(IEnumerable<PlcHardwareCatalogCandidate> rows,int limit=50)=>PlcHardwareCatalogPolicy.Search("19","  CPU  ",limit,q=> { Check(q=="CPU","trim literal query"); calls++; return rows; });
        foreach(var q in new[]{"", " ","* ?", "CPU\n",new string('x',257)}) Reject(()=>PlcHardwareCatalogPolicy.Query(q,50),"query bound");
        foreach(var n in new[]{-1,0,101,int.MaxValue}) Reject(()=>PlcHardwareCatalogPolicy.Query("CPU",n),"limit bound");
        foreach(var release in new[]{"14sp1","15.1","16","17","18","unknown"}) Reject(()=>PlcHardwareCatalogPolicy.Search(release,"CPU",50,_=>throw new Exception("Must not query unsupported release")),"release gate");
        var result=Run(new[]{Row("OrderNumber:6ES7 123/V1.0"),Row("OrderNumber:6ES7 123/V1.0"),Row("OrderNumber:6es7 123/V1.0")});
        Check(calls==1 && result.Count==2,"one query and exact ordinal dedup");
        Check(result.Items[0].TypeIdentifier=="OrderNumber:6ES7 123/V1.0","exact identifier preserved");
        Check(result.Items.All(r=>r.Insertable==null && r.Score==null),"no invented insertion or score");
        Check(!(bool)result.Meta["truncated"] && (bool)result.Meta["complete"],"complete enumeration");
        result=Run(new[]{Row("a"),Row("b")},1); Check(result.Count==1 && (string)result.Meta["truncationReason"]=="result-limit","explicit limit truncation");
        result=Run(new[]{Row("a")},1); Check(!(bool)result.Meta["truncated"],"exact limit with exhausted source complete");
        var consumed=0;
        IEnumerable<PlcHardwareCatalogCandidate> Infinite() { while(true) { consumed++; yield return Row("same"); } }
        result=Run(Infinite()); Check(consumed==1000 && (string)result.Meta["truncationReason"]=="entry-budget","infinite duplicates bounded");
        result=Run(new[]{new PlcHardwareCatalogCandidate { TypeIdentifier="a",Description=new string('x',16385) }}); Check(result.Count==0 && (string)result.Meta["truncationReason"]=="field-character-budget","field bound explicit");
        result=Run(Enumerable.Range(0,100).Select(i=>new PlcHardwareCatalogCandidate { TypeIdentifier=i.ToString(),Description=new string('x',16000) })); Check(result.Count<100 && (string)result.Meta["truncationReason"]=="response-character-budget","response bound explicit");
        Reject(()=>Run(new[]{new PlcHardwareCatalogCandidate()}),"no fabricated identifier");
        var disposed=false;
        IEnumerable<PlcHardwareCatalogCandidate> Disposal() { try { yield return Row("a"); yield return Row("b"); } finally { disposed=true; } }
        Run(Disposal(),1); Check(disposed,"early exit disposes enumerator");
        IEnumerable<PlcHardwareCatalogCandidate> Failure() { yield return Row("a"); throw new InvalidOperationException("read failed"); }
        Reject(()=>Run(Failure()),"native exception not swallowed as success");


        foreach(var release in new[]{"19","20","21"}) { var supported=PlcHardwareCatalogPolicy.Search(release,"CPU",1,_=>Array.Empty<PlcHardwareCatalogCandidate>()); Check(supported.Count==0,"supported documented release"); }
        var argsJson=System.Text.Json.Nodes.JsonNode.Parse("{\"keyword\":\"CPU\",\"limit\":50}")!.AsObject();
        var good=System.Text.Json.JsonSerializer.SerializeToNode(Run(new[]{Row("a")}))!;
        Check(TiaMcp.FoundationHost.HardwareCatalogContract.Validate(good,argsJson)["Count"]!.GetValue<int>()==1,"host preserves count");
        void Bad(string field,object value) { var clone=(System.Text.Json.Nodes.JsonObject)good.DeepClone(); clone[field]=System.Text.Json.JsonSerializer.SerializeToNode(value); try { TiaMcp.FoundationHost.HardwareCatalogContract.Validate(clone,argsJson); } catch(InvalidDataException) { Check(true,"Host rejected "+field); return; } throw new Exception("Expected host rejection: "+field); }
        Bad("Count",2); Bad("Keyword","other");
        var wrong=good.DeepClone(); wrong["Meta"]!["complete"]=false;
        try { TiaMcp.FoundationHost.HardwareCatalogContract.Validate(wrong,argsJson); throw new Exception("Incomplete accepted"); } catch(InvalidDataException) { Check(true,"Incomplete response rejected"); }
        wrong=good.DeepClone(); wrong["Items"]![0]!["Insertable"]=true;
        try { TiaMcp.FoundationHost.HardwareCatalogContract.Validate(wrong,argsJson); throw new Exception("Insertion claim accepted"); } catch(InvalidDataException) { Check(true,"Insertion claim rejected"); }


        var engine=new PlcFoundationEngine();
        Reject(()=>engine.SearchHardwareCatalog("CPU"),"existing project required");
        Check(engine.PortalReads==0 && engine.Attached.HardwareCatalog.Calls==0,"missing project does not query");
        engine.Bound=true;
        Reject(()=>engine.SearchHardwareCatalog("*"),"invalid query before portal");
        Check(engine.PortalReads==0,"invalid request no native access");
        engine.Attached.HardwareCatalog.Rows.Add(Row("OrderNumber: A/V1"));
        var nativeShape=engine.SearchHardwareCatalog("CPU");
        Check(nativeShape.Items[0].TypeIdentifier=="OrderNumber: A/V1" && engine.Attached.HardwareCatalog.Calls==1,"typed adapter one query exact field mapping");
        engine.ReleaseKey="18";
        Reject(()=>engine.SearchHardwareCatalog("CPU"),"signature only release gated before portal");
        Check(engine.PortalReads==1,"unsupported release no native access");


        foreach(var target in new[]{"root","meta","row"}) {
            var injected=good.DeepClone();
            var obj=target=="root"?injected.AsObject():target=="meta"?injected["Meta"]!.AsObject():injected["Items"]![0]!.AsObject();
            obj["extra"]=new string('x',300000);
            try { TiaMcp.FoundationHost.HardwareCatalogContract.Validate(injected,argsJson); throw new Exception("Extra data accepted"); } catch(InvalidDataException) { Check(true,"Extra data rejected: "+target); }
        }
        wrong=good.DeepClone(); wrong["Meta"]!["boundsScope"]=new string('x',300000);
        try { TiaMcp.FoundationHost.HardwareCatalogContract.Validate(wrong,argsJson); throw new Exception("Oversized metadata accepted"); } catch(InvalidDataException) { Check(true,"Oversized metadata rejected"); }
    }
}
