using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;

public class ServerProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
}
internal sealed class FakeWorker : IFoundationWorker
{
    internal int Calls;
    internal string? Operation;
    internal JsonObject? Arguments;
    internal CancellationToken Token;
    public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
    {
        Calls++; Operation=operation; Arguments=arguments; Token=token;
        return Task.FromResult<JsonNode?>(operation switch {
            "Attach"=>new JsonObject { ["Stage"]="attached",["Strategy"]="explicit-existing-pid",["AttemptedPids"]=new JsonArray(123),["LaunchMode"]="never",["OwnsPortal"]=false },
            "ReadBlockInfo" or "ReadTypeInfo"=>new JsonObject { ["Name"]="Object",["TypeName"]="PlcObject",["Attributes"]=new JsonArray() },
            "ReadProjectTree"=>JsonValue.Create("Project\n  Device: PLC_1"),
            "ListProjects" or "ListTags" or "ListUserConstants" or "ListSystemConstants"=>new JsonArray(),
            "ReadBlockHierarchy"=>new JsonObject { ["Name"]="Program blocks",["Groups"]=new JsonArray(),["Blocks"]=new JsonArray() },
            _ when operation.StartsWith("Read")=>new JsonArray(),
            _=>new JsonObject { ["Executed"]=operation=="BindProject",["ProjectFile"]=@"C:\Projects\Example.ap17",["OutputFile"]=@"C:\Exports\Example.xml",["Messages"]=new JsonArray(),["Errors"]=new JsonArray(),["Warnings"]=new JsonArray(),["Info"]=new JsonArray() }
        });
    }
    public void Dispose() { }
}
internal static class Program
{
    private static int passed;
    private static void Check(bool condition,string message) { if(!condition) throw new Exception(message); passed++; }
    private static async Task<bool> Rejected(McpServerTool tool,RequestContext<CallToolRequestParams> request)
    { try { return (await tool.InvokeAsync(request)).IsError==true; } catch(McpException ex) { return ex.ErrorCode==McpErrorCode.InvalidParams; } }
    private static async Task Main(string[] cliArgs)
    {
        var server=DispatchProxy.Create<IMcpServer,ServerProxy>();
        var fake=new FakeWorker();
        var tools=FoundationTools.Create(fake);
        Check(tools.Select(t=>t.ProtocolTool.Name).Distinct(StringComparer.Ordinal).Count()==tools.Count,"Unique tool names");
        foreach(var tool in tools)
        {
            var def=FoundationTools.Definitions.Single(d=>d.Name==tool.ProtocolTool.Name);
            Check(!tool.ProtocolTool.InputSchema.GetProperty("additionalProperties").GetBoolean(),"Closed schema");
            Check(tool.ProtocolTool.Description!.Contains("native unverified"),"No unsupported acceptance claim");
            var args=def.Arguments.Where(a=>a.Required).ToDictionary(a=>a.Name,a=>a.Type=="integer" ? JsonSerializer.SerializeToElement(123) : JsonSerializer.SerializeToElement("exact/path"));
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
        using(var disabled=new WorkerClient("17","intentionally-nonexistent.exe","intentionally-nonexistent-api",false))
        {
            try { await disabled.Call("Attach",new JsonObject { ["processId"]=123 },CancellationToken.None); throw new Exception("Native default gate failed"); }
            catch(InvalidOperationException ex) { Check(ex.Message.StartsWith("Native calls are disabled."),"Discovery denies before any process/file access"); }
        }
        Check(WorkerProtocol.Decode("{\"id\":1,\"result\":null}",1)==null,"Explicit void result accepted");
        foreach(var malformed in new[]{"{\"id\":1}","{\"id\":1,\"error\":\"bad\"}","{\"id\":1,\"result\":null,\"error\":{\"message\":\"bad\"}}","{\"id\":2,\"result\":[]}","{\"id\":1,\"result\":[],\"result\":null}"})
        { try { WorkerProtocol.Decode(malformed,1); throw new Exception("Malformed response accepted"); } catch(IOException) { passed++; } }
        await V17ContractTests.Run(server,Check);
        await DeclarationReadTests.Run(server,Check);
        PathAndIdentityTests.Run(Check);
        ExchangeContractTests.Run(Check);
        ExportPublicationTests.Run(Check);
        AdditionalMutationResultTests.Run(Check);
        OfflineCoverageBoundaryTests.Run(Check);
        CompileContractTests.Run(Check);
        await LifecycleContractTests.Run(Check);
        if(cliArgs.Length!=0) { if(cliArgs.Length!=2) throw new ArgumentException("Optional metadata check requires PublicAPI root and adapter project root."); ApiMetadataTests.Run(cliArgs[0],cliArgs[1],Check); }
        Console.WriteLine($"{passed} passed, 0 failed, 0 skipped. No worker process or Siemens assembly loaded.");
    }
}
