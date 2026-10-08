using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Adapters;
internal static class DeviceAddDispatchTests
{
    internal static JsonNode Payload(JsonObject args)
    {
        var r=new PlcDeviceAddRequest {Release="19",Project=@"C:\Test\Test.ap19",ProcessId=42,RootIdentity="root",PreferredMlfb=args["preferredMlfb"]!.GetValue<string>(),PreferredVersion=args["preferredVersion"]!.GetValue<string>(),Name=args["deviceName"]!.GetValue<string>(),Family=args["family"]?.GetValue<string>()??"S7-1500",DryRun=args["dryRun"]?.GetValue<bool>()??true,Confirm=args["confirm"]?.GetValue<bool>()??false,ExpectedProject=args["expectedProjectFile"]?.GetValue<string>()??"",ExpectedHash=args["expectedPlanHash"]?.GetValue<string>()??""};
        var items=new List<PlcDeviceAddItem>();
        return JsonSerializer.SerializeToNode(PlcDeviceAddPolicy.Run(r,()=>new[]{new PlcHardwareCatalogCandidate {ArticleNumber="6ES7513-1AM03-0AB0",Version="V3.0",TypeIdentifier="OrderNumber:6ES7513-1AM03-0AB0/V3.0"}},()=>items,()=>{},(id,name)=>{var d=new PlcDeviceAddItem {Name=name,Identity="new",ParentIdentity="root",ParentVerified=true};items.Add(d);return d;}))!;
    }
    internal static async Task Run(IMcpServer server,Action<bool,string> check)
    {
        var worker=new FakeWorker();var tool=FoundationTools.Create(worker).Single(t=>t.ProtocolTool.Name=="AddDeviceWithFallback");
        var args=new Dictionary<string,JsonElement> { ["preferredMlfb"]=JsonSerializer.SerializeToElement("6ES7513-1AM03-0AB0"),["preferredVersion"]=JsonSerializer.SerializeToElement("V3.0"),["deviceName"]=JsonSerializer.SerializeToElement("PLC_2") };
        RequestContext<CallToolRequestParams> Request()=>new(server){Params=new(){Name="AddDeviceWithFallback",Arguments=args}};
        var response=await tool.InvokeAsync(Request());check(response.IsError!=true && worker.Calls==1,"Device addition actual MCP preview dispatch");
        var planned=Payload(worker.Arguments!);check(worker.Arguments!["dryRun"]!.GetValue<bool>(),"Device add defaults preview");
        args["dryRun"]=JsonSerializer.SerializeToElement(false);int calls=worker.Calls;bool rejected=false;try{await tool.InvokeAsync(Request());}catch(McpException){rejected=true;}check(rejected&&worker.Calls==calls,"Device add confirmation required before worker");
        args["confirm"]=JsonSerializer.SerializeToElement(true);args["expectedProjectFile"]=JsonSerializer.SerializeToElement(@"C:\Test\Test.ap19");rejected=false;try{await tool.InvokeAsync(Request());}catch(McpException){rejected=true;}check(rejected&&worker.Calls==calls,"Device add hash required before worker");
        args["expectedPlanHash"]=JsonSerializer.SerializeToElement(planned["PlanHash"]!.GetValue<string>());response=await tool.InvokeAsync(Request());check(response.IsError!=true&&worker.Calls==calls+1,"Reviewed device addition actual MCP dispatch");
        var unknown=Payload(worker.Arguments!);unknown["Executed"]=false;unknown["Status"]="outcome-unknown";unknown["RequiresSessionReset"]=true;unknown["Error"]="native outcome unknown";
        var state=new WorkerOutcomeState();state.AcceptResult("AddDeviceWithFallback",worker.Arguments!,unknown);check(state.Poisoned,"Unknown device create poisons shared host session");bool blocked=false;try{state.RequireUsable();}catch(InvalidOperationException){blocked=true;}check(blocked,"Shared host refuses subsequent requests");
    }
}
