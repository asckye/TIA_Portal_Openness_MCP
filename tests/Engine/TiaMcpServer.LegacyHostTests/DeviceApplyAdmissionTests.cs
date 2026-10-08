using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.LegacyHost;
using TiaMcp.PlcFoundation;
using TiaOpenness.Shared;
using Xunit;

public sealed class DeviceApplyAdmissionTests
{
    private sealed class Worker : IFoundationWorker
    {
        internal int Creates;
        internal readonly List<PlcDeviceAddItem> Items = new();
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            if (operation == "ListTags") return Task.FromResult<JsonNode?>(new JsonArray());
            try
            {
                var request = new PlcDeviceAddRequest { Release="19", Project="C:/fixture.ap19", ProcessId=42, RootIdentity="root",
                    PreferredMlfb=(string)args["preferredMlfb"]!, PreferredVersion=(string)args["preferredVersion"]!, Name=(string)args["deviceName"]!,
                    Family=(string)args["family"]!, DryRun=(bool)args["dryRun"]!, Confirm=(bool?)args["confirm"]??false,
                    ExpectedHash=(string?)args["expectedPlanHash"]??"", ExpectedProject=(string?)args["expectedProjectFile"]??"" };
                var result = PlcDeviceAddPolicy.Run(request,
                    ()=>new[] { new PlcHardwareCatalogCandidate { ArticleNumber="6ES7 513-1AM03-0AB0",Version="V3.1",TypeIdentifier="OrderNumber:6ES7 513-1AM03-0AB0/V3.1" } },
                    ()=>Items, ()=>{}, (identifier,name)=> { Creates++; var item=new PlcDeviceAddItem { Name=name,Identity=Guid.NewGuid().ToString("N"),ParentIdentity="root",ParentVerified=true }; Items.Add(item); return item; });
                return Task.FromResult(JsonSerializer.SerializeToNode(result));
            }
            catch (AdapterPreconditionException cause)
            {
                var classified=WorkerFailurePolicy.Classify(cause,true,false);
                throw new WorkerOperationException(cause.Message,classified.Code,"rejected-before-operation",
                    JsonSerializer.Serialize(new { exceptionType=cause.GetType().Name,parameter=cause.ParamName,isArgument=cause.IsArgument }));
            }
        }
        public void Dispose() { }
    }
    [Theory]
    [InlineData("duplicate", "PRECONDITION_FAILED", "deviceName")]
    [InlineData("stale", "INVALID_ARGUMENT", "expectedPlanHash")]
    [InlineData("catalog", "INVALID_ARGUMENT", "preferredMlfb/preferredVersion")]
    public async Task Approval_disabled_apply_checks_the_real_policy_and_keeps_the_session_usable(string scenario, string code, string parameter)
    {
        using var worker=new Worker();
        FoundationV4Tool Tool(string source)=>new(new FoundationTool(FoundationTools.Definitions.Single(d=>d.Name==source),worker),"19",null,
            ()=>new(false,1),(pending,current,_)=> { Assert.False(current.Enabled);return Task.FromResult(new ApprovalOutcome(pending,true,null)); });
        async Task<JsonNode> Call(string source,JsonObject args)
        {
            var tool=Tool(source);
            var request=new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer,ApprovalPrecheckTests.ServerProxy>())
            { Params=new() { Name=tool.ProtocolTool.Name,Arguments=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(args.ToJsonString()) } };
            return (await tool.InvokeAsync(request)).StructuredContent!;
        }
        var args=new JsonObject { ["preferredMlfb"]="6ES7 513-1AM03-0AB0",["preferredVersion"]="V3.1",["deviceName"]="PLC_2",["family"]="S7-1500",["dryRun"]=true };
        var preview=await Call("AddDeviceWithFallback",args); Assert.True((bool?)preview["ok"]);
        args["expectedPlanHash"]=preview["data"]!["planHash"]!.DeepClone();args["expectedProjectFile"]="C:/fixture.ap19";args["confirm"]=true;args["dryRun"]=false;
        if (scenario=="duplicate") Assert.True((bool?)(await Call("AddDeviceWithFallback",args))["ok"]);
        if (scenario=="stale") worker.Items.Add(new PlcDeviceAddItem { Name="Other",Identity="other",ParentIdentity="root",ParentVerified=true });
        if (scenario=="catalog") args["preferredVersion"]="V1.7";
        var body=await Call("AddDeviceWithFallback",args);
        Assert.Equal(code,(string?)body["error"]?["code"]);Assert.Equal(parameter,(string?)body["error"]?["details"]?["parameter"]);
        Assert.Equal("rejected-before-operation",(string?)body["meta"]?["outcome"]);Assert.Equal("not-started",(string?)body["meta"]?["execution"]);
        Assert.False((bool?)body["meta"]?["requiresSessionReset"]);Assert.Equal(scenario=="duplicate" ? 1 : 0,worker.Creates);
        Assert.Contains(body["meta"]!["warnings"]!.AsArray(),w=>(string?)w?["code"]=="APPROVAL_DISABLED");
        Assert.DoesNotContain(body["meta"]!["warnings"]!.AsArray(),w=>(string?)w?["code"]=="APPROVAL_PRECHECK_REFUSED");
        Assert.True((bool?)(await Call("ReadPlcTags",new JsonObject { ["plc"]="PLC_1",["table"]="T" }))["ok"]);
    }
}
