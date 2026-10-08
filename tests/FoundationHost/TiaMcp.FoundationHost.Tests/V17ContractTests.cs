using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;

internal static class V17ContractTests
{
    // Pinned to 70758f2: PlcSoftwareV17McpTools.cs, Responses.cs and Types.cs.
    private sealed class BlockExample
    {
        public string? Path { get; set; }
        public string? Message { get; set; }
        public object? Meta { get; set; }
        public object[] Attributes { get; set; }=Array.Empty<object>();
        public string TypeName { get; set; }="FB";
        public string Name { get; set; }="Motor";
        public string? Namespace { get; set; }
        public string ProgrammingLanguage { get; set; }="SCL";
        public string MemoryLayout { get; set; }="Optimized";
        public bool IsConsistent { get; set; }=true;
        public string HeaderName { get; set; }="";
        public DateTime ModifiedDate { get; set; }=new(2026,1,1);
        public bool IsKnowHowProtected { get; set; }
        public string Description { get; set; }="Motor";
    }
    private sealed class PayloadWorker(JsonNode payload) : IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation,JsonObject arguments,CancellationToken token)=>Task.FromResult<JsonNode?>(payload.DeepClone());
        public void Dispose() { }
    }
    private sealed class ErrorWorker(int code) : IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation,JsonObject arguments,CancellationToken token)=>throw new WorkerOperationException("native read failure",code,"read-failed");
        public void Dispose() { }
    }
    internal static async Task Run(IMcpServer server,Action<bool,string> check)
    {
        foreach(var pair in new[]{("GetBlockInfo","BlockInfo","blockPath"),("GetTypeInfo","TypeInfo","typePath")})
        {
            var definition=FoundationTools.Definitions.Single(d=>d.Name==pair.Item1);
            check(definition.Arguments.Select(a=>a.Name).SequenceEqual(new[]{"softwarePath",pair.Item3}),"Exact single-object argument contract");
            var detail=JsonSerializer.SerializeToNode(new BlockExample())!;
            var result=V17ReadEnvelope.Wrap(pair.Item1,pair.Item2,detail);
            string Key(string n)=>McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(n)??n;
            var serialized=JsonSerializer.SerializeToNode(new BlockExample(),McpJsonUtilities.DefaultOptions)!.AsObject();
            foreach(var field in serialized.Where(f=>f.Key!=Key("Message") && f.Key!=Key("Meta")))
                check(JsonNode.DeepEquals(field.Value,result[field.Key]),"Flat object detail preserves SDK field "+field.Key);
            check(!result.ContainsKey(Key("Items")) && !result.ContainsKey(pair.Item2),"Detail is flat V17 DTO, not nested collection");
            check(result[Key("Meta")]!["manualReconciliation"]!.GetValue<string>()=="closed-ordinary-plc","Manual closure is limited to ordinary PLC scope");
            var candidates=new[]{"Folder/Motor","Other/Motor"};
            check(TiaMcp.Adapters.PlcExchangePolicy.Exact(candidates,s=>s,"Folder/Motor")=="Folder/Motor","Single object selects the exact qualified group");
            foreach(var selected in new[]{"Motor","folder/Motor","Folder/Motor/extra"})
            {
                try { TiaMcp.Adapters.PlcExchangePolicy.Exact(candidates,s=>s,selected); throw new Exception("Fallback accepted"); }
                catch(ArgumentException) { check(true,"No basename, case-fold or extra-segment fallback"); }
            }
            try { TiaMcp.Adapters.PlcExchangePolicy.Exact(new[]{"A","A"},s=>s,"A"); throw new Exception("Duplicate accepted"); }
            catch(ArgumentException) { check(true,"Duplicate exact object identities rejected"); }
            foreach(var path in new[]{"","../A","A//B","A/%2e%2e/B"})
            {
                try { TiaMcp.Adapters.PlcExchangePolicy.ObjectPath(path); throw new Exception("Invalid path accepted"); }
                catch(ArgumentException) { check(true,"Invalid single-object path rejected"); }
            }
            foreach(var bad in new JsonNode?[]{null,new JsonArray(),new JsonObject(),new JsonObject{["Name"]=7,["TypeName"]="FB",["Attributes"]=new JsonArray()},new JsonObject{["Name"]="A",["TypeName"]="FB",["Attributes"]=new JsonObject()}})
            {
                try { V17ReadEnvelope.Wrap(pair.Item1,pair.Item2,bad); throw new Exception("Malformed detail accepted"); }
                catch(InvalidDataException) { check(true,"Malformed detail is not fabricated success"); }
            }
        }
        foreach(var name in new[]{"GetBlocks","GetTypes","GetBlocksWithHierarchy","GetPlcTagTables","GetPlcExternalSources"})
        {
            var def=FoundationTools.Definitions.Single(d=>d.Name==name);
            check(def.Arguments[0].Name=="softwarePath" && def.Arguments[0].Required,"V17 required softwarePath");
            bool filtered=name is "GetBlocks" or "GetTypes";
            check(def.Arguments.Length==(filtered?2:1),"V17 exact argument count");
            if(filtered) check(def.Arguments[1] is { Name:"regexName",Type:"string",Required:false,Default:"" },"V17 regex default");
        }
        var example=new BlockExample();
        var payload=new JsonArray(JsonSerializer.SerializeToNode(example));
        var tool=new FoundationTool(FoundationTools.Definitions.Single(d=>d.Name=="GetBlocks"),new PayloadWorker(payload));
        var request=new RequestContext<CallToolRequestParams>(server) { Params=new CallToolRequestParams { Name="GetBlocks",Arguments=new Dictionary<string,JsonElement> { ["softwarePath"]=JsonSerializer.SerializeToElement("devices/PLC/CPU") } } };
        var response=await tool.InvokeAsync(request);
        var json=JsonNode.Parse(response.Content.OfType<TextContentBlock>().Single().Text)!;
        string Wire(string s)=>McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(s)??s;
        var expected=JsonSerializer.SerializeToNode(example,McpJsonUtilities.DefaultOptions);
        check(JsonNode.DeepEquals(json[Wire("Items")]![0],expected),"Block fields and null/casing match actual MCP SDK serializer");
        check(json[Wire("Meta")]!["success"]!.GetValue<bool>(),"Only successful payload has success metadata");
        foreach(var kind in new[]{DateTimeKind.Unspecified,DateTimeKind.Utc,DateTimeKind.Local})
        {
            var rawDate=DateTime.SpecifyKind(new DateTime(2026,1,2,3,4,5),kind);
            var nativeDto=new BlockExample { ModifiedDate=rawDate };
            var workerJson=Newtonsoft.Json.JsonConvert.SerializeObject(nativeDto);
            var roundtrip=Newtonsoft.Json.JsonConvert.DeserializeObject<BlockExample>(workerJson)!;
            check(roundtrip.ModifiedDate.Kind==kind && roundtrip.ModifiedDate.Ticks==rawDate.Ticks,"Actual worker Newtonsoft serializer preserves timestamp ticks and Kind "+kind);
            var dated=JsonNode.Parse(workerJson)!;
            var datedResult=V17ReadEnvelope.Wrap("GetBlockInfo","BlockInfo",dated);
            check(datedResult[Wire("ModifiedDate")]!.ToJsonString()==dated["ModifiedDate"]!.ToJsonString(),"Read envelope preserves native timestamp representation without timezone coercion "+kind);
        }
        var tags=V17ReadEnvelope.Wrap("GetPlcTagTables","Items",new JsonArray("Table_A","Table_B"));
        check(tags[Wire("Items")]![1]!.GetValue<string>()=="Table_B","Tag list contains strings, not foundation DTOs");
        foreach(var names in new[]{new JsonArray(),new JsonArray("Source.scl","source.scl","UDT_1.udt")})
        {
            var sourceResult=V17ReadEnvelope.Wrap("GetPlcExternalSources","Items",names);
            check(sourceResult[Wire("Meta")]?["manualReconciliation"]?.GetValue<string>()=="closed-ordinary-plc" && sourceResult[Wire("Meta")]?["manualScope"]?.GetValue<string>()?.Contains("root external-source names only")==true,"External source manual closure has exact root-read scope");
            check(JsonNode.DeepEquals(sourceResult[Wire("Items")],names),"External source names preserve native order, spelling, case and genuine empty collection");
        }
        foreach(var bad in new JsonNode?[]{null,new JsonObject(),new JsonArray(1),new JsonArray((JsonNode?)null),new JsonArray(""),new JsonArray("valid.scl",new JsonObject())})
        {
            try { V17ReadEnvelope.Wrap("GetPlcExternalSources","Items",bad); throw new Exception("Malformed name list accepted"); }
            catch(InvalidDataException) { check(true,"External source malformed/mixed list cannot claim empty success"); }
        }
        var sourceRequest=new RequestContext<CallToolRequestParams>(server) { Params=new CallToolRequestParams { Name="GetPlcExternalSources",Arguments=new Dictionary<string,JsonElement> { ["softwarePath"]=JsonSerializer.SerializeToElement("devices/PLC/CPU") } } };
        var sourceTool=new FoundationTool(FoundationTools.Definitions.Single(d=>d.Name=="GetPlcExternalSources"),new PayloadWorker(new JsonArray("Source.scl")));
        var sourceResponse=await sourceTool.InvokeAsync(sourceRequest);
        check(JsonNode.Parse(sourceResponse.Content.OfType<TextContentBlock>().Single().Text)![Wire("Items")]![0]!.GetValue<string>()=="Source.scl","External source actual MCP SDK envelope preserves V17 Items strings");
        sourceTool=new FoundationTool(FoundationTools.Definitions.Single(d=>d.Name=="GetPlcExternalSources"),new ErrorWorker(-32603));
        try { await sourceTool.InvokeAsync(sourceRequest); throw new Exception("Read failure swallowed"); }
        catch(McpException ex) { check(ex.ErrorCode==McpErrorCode.InternalError,"External source failure is explicit MCP error"); }
        check(!WorkerProtocol.RequiresSessionReset(true,new WorkerOperationException("read failed",-32603,"read-failed")),"Known external-source read failure need not poison session");
        var hierarchy=V17ReadEnvelope.Wrap("GetBlocksWithHierarchy","Root",new JsonObject { ["Name"]="Root",["Blocks"]=new JsonArray(),["Groups"]=new JsonArray(new JsonObject { ["Name"]="Empty",["Blocks"]=new JsonArray(),["Groups"]=new JsonArray() }) });
        check(hierarchy[Wire("Root")]![Wire("Groups")]![0]![Wire("Name")]!.GetValue<string>()=="Empty","Empty hierarchy groups retained");
        tool=new FoundationTool(FoundationTools.Definitions.Single(d=>d.Name=="GetBlocks"),new PayloadWorker(new JsonObject()));
        try { await tool.InvokeAsync(request); throw new Exception("Bad DTO accepted"); }
        catch(McpException ex) { check(ex.ErrorCode==McpErrorCode.InternalError,"Bad backend shape maps to InternalError"); }
        foreach(var code in new[]{-32602,-32603})
        {
            tool=new FoundationTool(FoundationTools.Definitions.Single(d=>d.Name=="GetBlocks"),new ErrorWorker(code));
            try { await tool.InvokeAsync(request); throw new Exception("Worker failure accepted"); }
            catch(McpException ex) { check((int)ex.ErrorCode==code && ex.Message=="native read failure","Classified worker failure preserved through MCP boundary"); }
        }
    }
}
