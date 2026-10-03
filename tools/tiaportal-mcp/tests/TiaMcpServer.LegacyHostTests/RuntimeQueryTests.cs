using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;
using TiaMcp.PlcFoundation;
using TiaMcp.PlcWorker;

internal static class RuntimeQueryTests
{
    internal static JsonNode Payload(string op)
    {
        object value=op switch {
            "ReadState"=>new PlcRuntimeState {ReleaseKey="17"},
            "ReadPortalProcessProjects"=>new PlcProcessQuery {ReleaseKey="17",Processes=new[]{Row()}},
            "ReadPortalConnectReadiness"=>PlcRuntimeQueryPolicy.Diagnose(new PlcProcessQuery {ReleaseKey="17",Processes=new[]{Row()}},123),
            _=>throw new Exception("Unknown query")
        };
        return JsonNode.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(value))!;
    }
    private static PlcProcessSnapshot Row()=>new(){ProcessId=123,SnapshotAcquisitionTime="2026-10-02T10:20:30.0000000",ProjectPath=@"C:\Projects\Example.ap17"};
    private sealed class Reader(JsonNode? payload,Exception? failure=null):IFoundationWorker
    {
        internal int Calls;
        internal string? Operation;
        public Task<JsonNode?> Call(string operation,JsonObject arguments,CancellationToken token)
        {Calls++;Operation=operation;if(failure!=null)throw failure;return Task.FromResult(payload?.DeepClone());}
        public void Dispose(){}
    }
    internal static async Task Run(IMcpServer server,Action<bool,string> check)
    {
        var query=new PlcProcessQuery {ReleaseKey="17",Processes=new[]{Row()}};
        check(PlcRuntimeQueryPolicy.Diagnose(query,123).Readiness=="unknown","Process found is not readiness permission");
        check(PlcRuntimeQueryPolicy.Diagnose(query,456).Readiness=="not-found","Missing exact process never auto-selects");
        foreach(var name in new[]{"GetState","ListPortalProcessProjects","DiagnosePortalConnectReadiness"})
        {
            var def=FoundationTools.Definitions.Single(d=>d.Name==name);
            check(WorkerOperations.Names.Contains(def.Operation),"Runtime query allowlist "+name);
            var payload=Payload(def.Operation);
            var reader=new Reader(payload);
            var tool=FoundationTools.Create(reader).Single(t=>t.ProtocolTool.Name==name);
            var args=name=="DiagnosePortalConnectReadiness"?new Dictionary<string,JsonElement>{{"processId",JsonSerializer.SerializeToElement(123)}}:new();
            var request=new RequestContext<CallToolRequestParams>(server){Params=new(){Name=name,Arguments=args}};
            var response=await tool.InvokeAsync(request);
            var actual=JsonNode.Parse(response.Content.OfType<TextContentBlock>().Single().Text);
            check(response.IsError!=true && JsonNode.DeepEquals(payload,actual),"Runtime query preserves exact keys and nullable unknowns "+name);
            check(reader.Calls==1 && reader.Operation==def.Operation,"Single bounded query dispatch "+name);
            var malformed=new[]{(JsonNode?)null,new JsonObject(),payload.DeepClone(),payload.DeepClone()};
            ((JsonObject)malformed[2]!).Remove("ReleaseKey"); ((JsonObject)malformed[3]!)["unexpected"]="secret";
            foreach(var bad in malformed)
            {
                var badTool=FoundationTools.Create(new Reader(bad)).Single(t=>t.ProtocolTool.Name==name);
                try {await badTool.InvokeAsync(request);check(false,"Malformed runtime response accepted");}
                catch(McpException e){check(e.ErrorCode==McpErrorCode.InternalError && !e.Message.Contains("secret"),"Runtime malformed fail-closed "+name);}
            }
            args["unexpected"]=JsonSerializer.SerializeToElement(true);
            try {await tool.InvokeAsync(request);check(false,"Unknown argument accepted");}
            catch(McpException e){check(e.ErrorCode==McpErrorCode.InvalidParams && reader.Calls==1,"Runtime exact closed args "+name);}
            args.Remove("unexpected");
            if(name=="DiagnosePortalConnectReadiness") foreach(var badPid in new[]{0,-1})
            {
                args["processId"]=JsonSerializer.SerializeToElement(badPid);
                try {await tool.InvokeAsync(request);check(false,"Nonpositive PID accepted");}
                catch(McpException e){check(e.ErrorCode==McpErrorCode.InvalidParams && reader.Calls==1,"PID rejected before worker");}
            }
        }
        var wrongPidTool=FoundationTools.Create(new Reader(Payload("ReadPortalConnectReadiness"))).Single(t=>t.ProtocolTool.Name=="DiagnosePortalConnectReadiness");
        var wrongPidRequest=new RequestContext<CallToolRequestParams>(server){Params=new(){Name="DiagnosePortalConnectReadiness",Arguments=new Dictionary<string,JsonElement>{{"processId",JsonSerializer.SerializeToElement(456)}}}};
        try{await wrongPidTool.InvokeAsync(wrongPidRequest);check(false,"Different requested PID accepted");}catch(McpException e){check(e.ErrorCode==McpErrorCode.InternalError,"Readiness response bound to exact requested PID");}
        foreach(var bound in new[]{false,true})
        {
            var cached=new PlcRuntimeState {ReleaseKey="17",IsAttached=true,ProcessId=123,ProjectFile=bound?@"C:\Projects\Example.ap17":null,OwnsProject=bound};
            var node=JsonNode.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(cached));
            check(JsonNode.DeepEquals(node,RuntimeQueryContract.Validate("GetState",node)),"Attached and bound cached state preserves exact fields");
        }
        var absent=PlcRuntimeQueryPolicy.Diagnose(query,456);
        var absentNode=JsonNode.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(absent));
        check(JsonNode.DeepEquals(absentNode,RuntimeQueryContract.Validate("DiagnosePortalConnectReadiness",absentNode,456)),"Absent explicit process has null metadata and not-found shape");
        var bounded=Payload("ReadPortalProcessProjects"); var boundedRows=(JsonArray)bounded["Processes"]!;boundedRows.Clear();
        for(var i=1;i<=1024;i++){var item=JsonNode.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(Row()))!;item["ProcessId"]=i;boundedRows.Add(item);}
        check(RuntimeQueryContract.Validate("ListPortalProcessProjects",bounded)!=null,"1024 process bound allowed");
        var excess=boundedRows[0]!.DeepClone(); excess["ProcessId"]=1025;boundedRows.Add(excess);
        try{RuntimeQueryContract.Validate("ListPortalProcessProjects",bounded);check(false,"1025 processes accepted");}catch(InvalidDataException){check(true,"1025 process bound rejected");}
        var processes=Payload("ReadPortalProcessProjects");
        var row=(JsonObject)processes["Processes"]![0]!;
        foreach(var time in new[]{"2026-10-02T10:20:30.0000000","2026-10-02T10:20:30.0000000Z","2026-10-02T10:20:30.0000000+02:00"})
        {row["SnapshotAcquisitionTime"]=time;check(RuntimeQueryContract.Validate("ListPortalProcessProjects",processes)["Processes"]![0]!["SnapshotAcquisitionTime"]!.GetValue<string>()==time,"Snapshot kind preserved without UTC fabrication");}
        row["OsIdentityStatus"]="observed-not-bound"; row["OsStartTimeUtc"]="2026-10-02T09:20:30.0000000Z";
        check(RuntimeQueryContract.Validate("ListPortalProcessProjects",processes)!=null,"OS UTC independently represented");
        foreach(var badTime in new[]{"2026-10-02T09:20:30.0000000","2026-10-02T09:20:30.0000000+02:00","invalid"})
        {row["OsStartTimeUtc"]=badTime;try {RuntimeQueryContract.Validate("ListPortalProcessProjects",processes);check(false,"Bad OS UTC accepted");}catch(InvalidDataException){check(true,"Bad OS UTC rejected");}}
        var duplicated=Payload("ReadPortalProcessProjects");
        ((JsonArray)duplicated["Processes"]!).Add(duplicated["Processes"]![0]!.DeepClone());
        try{RuntimeQueryContract.Validate("ListPortalProcessProjects",duplicated);check(false,"Duplicate PID accepted");}catch(InvalidDataException){check(true,"Duplicate PID rejected");}
        var unknown=Payload("ReadPortalProcessProjects"); unknown["Processes"]![0]!["OsStartTimeUtc"]="2026-10-02T09:20:30.0000000Z";
        try{RuntimeQueryContract.Validate("ListPortalProcessProjects",unknown);check(false,"Unknown identity with fabricated timestamp accepted");}catch(InvalidDataException){check(true,"Unknown identity cannot carry timestamp");}
        var readErrorTool=FoundationTools.Create(new Reader(null,new WorkerOperationException("Read failed",-32603,"read-failed"))).Single(t=>t.ProtocolTool.Name=="ListPortalProcessProjects");
        var readErrorRequest=new RequestContext<CallToolRequestParams>(server){Params=new(){Name="ListPortalProcessProjects",Arguments=new Dictionary<string,JsonElement>()}};
        try{await readErrorTool.InvokeAsync(readErrorRequest);check(false,"Read failure became success");}catch(McpException e){check(e.ErrorCode==McpErrorCode.InternalError,"Runtime worker read failures propagate");}
        var readiness=Payload("ReadPortalConnectReadiness"); readiness["Readiness"]="ready";
        try{RuntimeQueryContract.Validate("DiagnosePortalConnectReadiness",readiness);check(false,"Unproven ready accepted");}catch(InvalidDataException){check(true,"Unproven ready rejected");}
        var state=Payload("ReadState");state["IsAttached"]=true;
        try{RuntimeQueryContract.Validate("GetState",state);check(false,"Missing cached PID accepted");}catch(InvalidDataException){check(true,"Missing cached PID rejected");}
    }
}
