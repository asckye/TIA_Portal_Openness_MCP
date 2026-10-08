using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Adapters;
using TiaMcp.PlcWorker;

internal static class SupplementaryReadTests
{
    // Explicit fake delegates/worker. This exercises production policy and MCP
    // dispatch only; it is not Siemens API compilation or native acceptance.
    private sealed class FakeGroup
    {
        internal string Name="G";
        internal PlcTechnologyReadRow[] Rows=Array.Empty<PlcTechnologyReadRow>();
        internal FakeGroup[] Children=Array.Empty<FakeGroup>();
    }
    private sealed class FakeReader(JsonNode? payload,Exception? error=null) : IFoundationWorker
    {
        internal int Calls;
        public Task<JsonNode?> Call(string operation,JsonObject args,CancellationToken token) { Calls++; if(error!=null) throw error; return Task.FromResult(payload?.DeepClone()); }
        public void Dispose() { }
    }
    private static PlcTechnologyReadRow Row(string name="TO")=>new() { Name=name,OfSystemLibElement="TO_PositioningAxis",OfSystemLibVersion="1.0" };
    internal static async Task Run(IMcpServer server,Action<bool,string> check)
    {
        void Reject(Action action,string message) { try { action(); } catch(Exception ex) when(ex is InvalidOperationException or NotSupportedException or InvalidDataException) {check(true,message);return;} throw new Exception(message); }
        var elementReads=0; var versionReads=0;
        var metadata=PlcSupplementaryReadPolicy.TechnologyMetadata("TO",()=>{elementReads++;return "TO_PositioningAxis";},()=>{versionReads++;return new Version(3,2,1,0);});
        check(metadata.OfSystemLibElement=="TO_PositioningAxis" && metadata.OfSystemLibVersion=="3.2.1.0" && metadata.UnavailableAttributes.Length==0 && elementReads==1 && versionReads==1,"Typed metadata getter delegates called once and version formatted exactly");
        metadata=PlcSupplementaryReadPolicy.TechnologyMetadata("TO",()=>null,()=>null);
        check(metadata.OfSystemLibElement==null && metadata.OfSystemLibVersion==null && metadata.UnavailableAttributes.SequenceEqual(new[]{"OfSystemLibElement","OfSystemLibVersion"}),"Null typed metadata reports exact unavailable fields");
        metadata=PlcSupplementaryReadPolicy.TechnologyMetadata("TO",()=>throw new InvalidOperationException("private getter detail"),()=>new Version(1,0));
        check(metadata.OfSystemLibElement==null && metadata.OfSystemLibVersion=="1.0" && metadata.UnavailableAttributes.SequenceEqual(new[]{"OfSystemLibElement"}),"Element getter failure does not suppress independent version");
        metadata=PlcSupplementaryReadPolicy.TechnologyMetadata("TO",()=>"TO_Cam",()=>throw new InvalidOperationException("private getter detail"));
        check(metadata.OfSystemLibElement=="TO_Cam" && metadata.OfSystemLibVersion==null && metadata.UnavailableAttributes.SequenceEqual(new[]{"OfSystemLibVersion"}) && !JsonSerializer.Serialize(metadata).Contains("private getter detail"),"Version getter failure stays unavailable without native exception text");
        Reject(()=>PlcSupplementaryReadPolicy.TechnologyMetadata("TO",()=>new string('x',4097),()=>new Version(1,0)),"Oversized getter metadata fails closed");
        Reject(()=>PlcSupplementaryReadPolicy.TechnologyMetadata("TO",()=>" ",()=>new Version(1,0)),"Invalid getter metadata fails closed");
        check(PlcSupplementaryReadPolicy.WatchScope==SupplementaryReadContract.WatchScope && PlcSupplementaryReadPolicy.TechnologyScope==SupplementaryReadContract.TechnologyScope,"Native and MCP scope declarations agree");
        foreach(var key in new[]{"14","15","22","V20",""}) Reject(()=>PlcSupplementaryReadPolicy.RequireRelease(key),"Unreconciled release denied: "+key);
        foreach(var key in new[]{"14sp1","15.1","16","17","18","19","20","21"}) {PlcSupplementaryReadPolicy.RequireRelease(key);check(true,"Recorded typed shape "+key);}
        Reject(()=>PlcSupplementaryReadPolicy.RequireRelease("14sp1",true),"V14 SP1 watch property absent");
        check(PlcSupplementaryReadPolicy.WatchNames(new[]{"B","A"}).SequenceEqual(new[]{"A","B"}),"Watch names stable order");
        Reject(()=>PlcSupplementaryReadPolicy.WatchNames(new[]{"A","A"}),"Duplicate names fail");
        Reject(()=>PlcSupplementaryReadPolicy.WatchNames(new[]{""}),"Empty name fails");
        Reject(()=>PlcSupplementaryReadPolicy.WatchNames(Enumerable.Range(0,10001).Select(i=>i.ToString())),"Watch budget fails");
        IEnumerable<string> BrokenNames() { yield return "A"; throw new InvalidOperationException("fake enumeration failure"); }
        Reject(()=>PlcSupplementaryReadPolicy.WatchNames(BrokenNames()),"No partial watch result");
        PlcTechnologyReadRow[] Read(FakeGroup g)=>PlcSupplementaryReadPolicy.Technologies(g,x=>x.Rows,x=>x.Children,x=>x.Name);
        var root=new FakeGroup { Rows=new[]{Row()},Children=new[]{new FakeGroup {Name="G/1",Rows=new[]{Row()}}} };
        var rows=Read(root);check(rows.Length==2 && rows[1].Folder=="G%2F1","Nested escaped technology identity");
        root.Children=new[]{root};Reject(()=>Read(root),"Cycle fails");
        root.Children=Array.Empty<FakeGroup>();root.Rows=new[]{Row(),Row()};Reject(()=>Read(root),"Duplicate TO identity fails");
        root.Rows=new[]{new PlcTechnologyReadRow {Name="TO",UnavailableAttributes=new[]{"OfSystemLibElement","OfSystemLibVersion"}}};check(Read(root)[0].OfSystemLibElement==null,"Unknown metadata remains unavailable");
        root.Rows=new[]{Row(new string('x',4097))};Reject(()=>Read(root),"Oversized object name fails before path allocation");
        root.Rows=Array.Empty<PlcTechnologyReadRow>();root.Children=Enumerable.Range(0,400).Select(i=>new FakeGroup {Name=i+new string('x',3000)}).ToArray();Reject(()=>Read(root),"Empty groups consume snapshot budget");
        root.Children=new[]{new FakeGroup(),new FakeGroup()};Reject(()=>Read(root),"Duplicate sibling group fails");
        root.Children=Array.Empty<FakeGroup>(); var cursor=root;for(int i=0;i<129;i++) {var child=new FakeGroup();cursor.Children=new[]{child};cursor=child;}Reject(()=>Read(root),"Depth 129 fails");
        root=new FakeGroup {Rows=new[]{new PlcTechnologyReadRow {Name="TO",OfSystemLibElement=new string('x',4097),OfSystemLibVersion="1.0"}}};Reject(()=>Read(root),"Oversized metadata fails");
        IEnumerable<PlcTechnologyReadRow> BrokenRows(FakeGroup g) {yield return Row();throw new InvalidOperationException("fake enumeration failure");}
        Reject(()=>PlcSupplementaryReadPolicy.Technologies(root,BrokenRows,g=>g.Children,g=>g.Name),"No partial technology result");
        var watchRoot=new FakeGroup {Name="root",Children=new[]{new FakeGroup{Name="G/1"}}};
        check(PlcSupplementaryReadPolicy.WatchPaths(watchRoot,g=>new[]{"A"},g=>g.Children,g=>g.Name).Contains("G%2F1/A"),"Watch recursive canonical path");
        foreach(var pair in new[]{("GetPlcWatchTables","ReadWatchTableNames"),("GetTechnologyObjects","ReadTechnologyObjects")})
        {
            var def=FoundationTools.Definitions.Single(d=>d.Name==pair.Item1);
            check(WorkerOperations.Names.Contains(pair.Item2) && def.Arguments.Length==1 && def.Arguments[0].Name=="softwarePath","Exact tool schema and operation");
            var request=new RequestContext<CallToolRequestParams>(server) {Params=new() {Name=pair.Item1,Arguments=new Dictionary<string,JsonElement> { ["softwarePath"]=JsonSerializer.SerializeToElement("devices/D/CPU") } } };
            JsonObject Payload(JsonArray items)=>new() { ["SoftwarePath"]="devices/D/CPU",["ReleaseKey"]="20",["Scope"]=pair.Item1=="GetPlcWatchTables"?SupplementaryReadContract.WatchScope:SupplementaryReadContract.TechnologyScope,["Items"]=items };
            var items=pair.Item1=="GetPlcWatchTables"?new JsonArray("A"):new JsonArray(JsonSerializer.SerializeToNode(Row()));
            var reader=new FakeReader(Payload(items));
            var result=await new FoundationTool(def,reader).InvokeAsync(request);
            string Wire(string name)=>McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(name)??name;
            var actual=JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!;
            check(reader.Calls==1 && actual[Wire("Items")] is JsonArray a && a.Count==1,"Actual MCP invoke and nonempty response");
            check(actual[Wire("Meta")]!["nativeAcceptance"]!.GetValue<string>()=="NOT RUN","No native acceptance inflation");
            if(pair.Item1=="GetTechnologyObjects") check(actual[Wire("Ok")]!.GetValue<bool>() && actual[Wire("Count")]!.GetValue<int>()==1,"Technology response fields");
            foreach(var malformed in new JsonNode?[]{null,new JsonArray(),new JsonObject(),Payload(new JsonArray(123)),Payload(new JsonArray(items[0]!.DeepClone(),items[0]!.DeepClone()))})
            {
                try { await new FoundationTool(def,new FakeReader(malformed)).InvokeAsync(request); throw new Exception("Malformed supplementary payload accepted"); }
                catch(McpException ex) {check(ex.ErrorCode==McpErrorCode.InternalError,"Malformed worker response fails MCP");}
            }
            if(pair.Item1=="GetPlcWatchTables")
            {
                foreach(var invalid in new[]{"G//A","../A","G%2f1/A","A%","G/ ",new string('A',4097)}) Reject(()=>SupplementaryReadContract.Wrap(pair.Item1,Payload(new JsonArray(invalid)),McpJsonUtilities.DefaultOptions),"Noncanonical/oversized watch path rejected");
            }
            else
            {
                var unavailableRow=Row();unavailableRow.OfSystemLibVersion=null;unavailableRow.UnavailableAttributes=new[]{"OfSystemLibVersion"};
                var unavailableResponse=SupplementaryReadContract.Wrap(pair.Item1,Payload(new JsonArray(JsonSerializer.SerializeToNode(unavailableRow))),McpJsonUtilities.DefaultOptions);
                check(unavailableResponse[Wire("Meta")]!["unavailableAttributes"]!.AsArray().Count==1 && unavailableResponse[Wire("Items")]![0]![Wire("OfSystemLibVersion")]==null,"Unavailable typed metadata reported without invented value");
            }
            var badScope=Payload(new JsonArray()); badScope["Scope"]="all native objects accepted";
            Reject(()=>SupplementaryReadContract.Wrap(pair.Item1,badScope,McpJsonUtilities.DefaultOptions),"Worker cannot inflate declared scope");
            const string secret="sentinel-private-native-project-secret";
            Exception safeError;
            try { PlcSupplementaryReadPolicy.ReadSnapshot<int>(()=>throw new InvalidOperationException(secret)); throw new Exception("Sanitization not invoked"); }
            catch(InvalidOperationException ex) {check(!ex.ToString().Contains(secret),"Native error and inner exception suppressed");safeError=ex;}
            var decodedError=new WorkerOperationException(safeError.Message,-32603,"read-failed");
            try {await new FoundationTool(def,new FakeReader(null,decodedError)).InvokeAsync(request);throw new Exception("MCP read failure not propagated");}
            catch(McpException ex) {check(!ex.ToString().Contains(secret) && ex.ErrorCode==McpErrorCode.InternalError,"Sanitized failure through worker exception and MCP boundary");}
            var empty=SupplementaryReadContract.Wrap(pair.Item1,Payload(new JsonArray()),McpJsonUtilities.DefaultOptions);
            check(empty[Wire("Items")]!.AsArray().Count==0,"True empty composition supported");
        }
    }
}
