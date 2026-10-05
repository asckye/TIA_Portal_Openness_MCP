using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;

internal static class FoundationToolDispatchTests
{
    private static async Task<bool> Rejected(McpServerTool tool,RequestContext<CallToolRequestParams> request)
    { try { return (await tool.InvokeAsync(request)).IsError==true; } catch(McpException ex) { return ex.ErrorCode==McpErrorCode.InvalidParams; } }
    internal static async Task Run(IMcpServer server,Action<bool,string> Check)
    {
        var fake=new FakeWorker();
        var tools=FoundationTools.Create(fake);
        Check(tools.Select(t=>t.ProtocolTool.Name).Distinct(StringComparer.Ordinal).Count()==tools.Count,"Unique tool names");
        foreach(var tool in tools.OfType<FoundationTool>().Where(t => t.IsNative))
        {
            var def=FoundationTools.Definitions.Single(d=>d.Name==tool.ProtocolTool.Name);
            Check(!tool.ProtocolTool.InputSchema.GetProperty("additionalProperties").GetBoolean(),"Closed schema");
            Check(tool.ProtocolTool.Description!.Contains("native unverified"),"No unsupported acceptance claim");
            var args=def.Arguments.Where(a=>a.Required).ToDictionary(a=>a.Name,a=>a.Type=="integer" ? JsonSerializer.SerializeToElement(123) : JsonSerializer.SerializeToElement("exact/path"));
            if(def.Name=="ImportBlocksFromDocuments") args["fileNamesWithoutExtension"]=JsonSerializer.SerializeToElement(new[]{"Demo"});
            if(def.Name=="AddDeviceWithFallback") { args["preferredMlfb"]=JsonSerializer.SerializeToElement("6ES7513-1AM03-0AB0");args["preferredVersion"]=JsonSerializer.SerializeToElement("V3.0");args["deviceName"]=JsonSerializer.SerializeToElement("PLC_2"); }
            if(def.Name=="DeletePlcExternalSource") { args["groupPath"]=JsonSerializer.SerializeToElement("");args["externalSourceName"]=JsonSerializer.SerializeToElement("Pump.scl"); }
            if(def.Name=="PlanPlcExternalSourceImport") { args["groupPath"]=JsonSerializer.SerializeToElement(""); args["filePath"]=args["allowedFilePath"]=JsonSerializer.SerializeToElement(@"C:\Sources\Pump.scl"); }
            if(def.Name=="ImportPlcExternalSource") { args["groupPath"]=JsonSerializer.SerializeToElement(""); args["filePath"]=JsonSerializer.SerializeToElement(@"C:\Sources\Pump.scl"); }
            if(def.Name=="GenerateBlocksFromExternalSource") args["externalSourceName"]=JsonSerializer.SerializeToElement("Pump.scl");
            var request=new RequestContext<CallToolRequestParams>(server) { Params=new CallToolRequestParams { Name=def.Name,Arguments=args } };
            using var cts=new CancellationTokenSource();
            var before=fake.Calls;
            var result=await tool.InvokeAsync(request,cts.Token);
            Check(result.IsError!=true && fake.Calls==before+1 && fake.Operation==def.Operation,"Single real facade dispatch");
            Check(fake.Token==cts.Token,"Cancellation forwarded");
            foreach(var arg in def.Arguments.Where(a=>!a.Required)) Check(fake.Arguments![arg.Name]!.ToJsonString()==JsonSerializer.Serialize(arg.Default),"Safe defaults inserted");
            if(def.Arguments.Any(a=>a.Name=="dryRun"))
            {
                Check(fake.Arguments!["confirm"]!.GetValue<bool>()==false,"Execution confirmation defaults false");
                var savedCalls=fake.Calls;
                args["dryRun"]=JsonSerializer.SerializeToElement(false);
                Check(await Rejected(tool,request) && fake.Calls==savedCalls,"Write without confirmation blocked before worker");
                args["confirm"]=JsonSerializer.SerializeToElement(true);
                Check(await Rejected(tool,request) && fake.Calls==savedCalls,"Write without project identity blocked before worker");
                args.Remove("confirm"); args.Remove("dryRun");
            }
            args["unrecognized"]=JsonSerializer.SerializeToElement(true);
            before=fake.Calls;
            Check(await Rejected(tool,request) && fake.Calls==before,"Unknown argument rejected before worker");
            args.Remove("unrecognized");
            if(def.Arguments.Any(a=>a.Required))
            {
                var key=def.Arguments.First(a=>a.Required).Name;
                var value=args[key]; args.Remove(key);
                Check(await Rejected(tool,request) && fake.Calls==before,"Missing argument rejected");
                args[key.ToUpperInvariant()]=value;
                Check(await Rejected(tool,request) && fake.Calls==before,"Case mismatch rejected");
                args.Remove(key.ToUpperInvariant()); args[key]=JsonSerializer.SerializeToElement(false);
                Check(await Rejected(tool,request) && fake.Calls==before,"Wrong type rejected");
                args[key]=value;
            }
            cts.Cancel();
            try { await tool.InvokeAsync(request,cts.Token); throw new Exception("Cancellation not enforced"); }
            catch(OperationCanceledException) { Check(fake.Calls==before,"Cancelled call never dispatched"); }
        }
    }
}
