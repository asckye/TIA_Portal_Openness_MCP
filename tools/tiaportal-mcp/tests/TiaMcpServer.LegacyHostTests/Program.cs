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
            "ExportBlocksAsDocuments"=>BatchDocumentExportTests.Payload(arguments),
            "ImportBlocksFromDocuments"=>BatchDocumentImportTests.Payload(arguments),
            "ImportFromDocuments"=>DocumentImportTests.Payload(arguments),
            "ExportAsDocuments"=>DocumentExportTests.Payload(arguments),
            "AddDeviceWithFallback"=>DeviceAddDispatchTests.Payload(arguments),
            "SearchHardwareCatalog"=>JsonSerializer.SerializeToNode(TiaMcp.PlcFoundation.PlcHardwareCatalogPolicy.Search("19",arguments["keyword"]!.GetValue<string>(),arguments["limit"]!.GetValue<int>(),_=>Array.Empty<TiaMcp.PlcFoundation.PlcHardwareCatalogCandidate>())),
            "DeletePlcExternalSource"=>ExternalSourceDeleteTests.Payload(arguments),
            "PlanPlcExternalSourceImport"=>ExternalSourcePlanTests.Payload(arguments),
            "ReadState" or "ReadPortalProcessProjects" or "ReadPortalConnectReadiness"=>RuntimeQueryTests.Payload(operation),
            "ReadWatchTableNames" or "ReadTechnologyObjects"=>new JsonObject { ["SoftwarePath"]="devices/D/CPU",["ReleaseKey"]="20",["Scope"]=operation=="ReadWatchTableNames"?SupplementaryReadContract.WatchScope:SupplementaryReadContract.TechnologyScope,["Items"]=new JsonArray() },
            "ReadSoftwareInfo"=>new JsonObject { ["Name"]="PLC",["Attributes"]=new JsonArray(),["Meta"]=new JsonObject { ["softwarePath"]="devices/D/CPU",["scope"]="ordinary PLC" } },
            "ReadSoftwareTree"=>new JsonObject { ["Tree"]="PLC [PLC Software]",["Meta"]=new JsonObject { ["softwarePath"]="devices/D/CPU",["scope"]="ordinary PLC",["paths"]=new JsonArray(new JsonObject { ["name"]="Program blocks",["path"]="devices/D/CPU/blocks",["kind"]="block-group",["objectPath"]="",["selectorParameter"]="groupPath" }) } },
            "ImportBlocksFromDirectory" or "ImportPlcProgramFromDirectory"=>BatchImportTests.Payload(operation=="ImportPlcProgramFromDirectory"),
            "ExportPlcWatchTable" or "ExportTechnologyObject"=>JsonSerializer.SerializeToNode(new TiaMcp.PlcFoundation.PlcSpecialExportResult { ProjectFile="C:/Projects/Example.ap17", SoftwarePath="exact/path",ObjectPath="exact/path",OutputFile="C:/Exports/Example.xml",ReleaseKey="17",Kind=operation=="ExportPlcWatchTable"?"watch-table":"technology-object",Scope=operation=="ExportPlcWatchTable"?SupplementaryReadContract.WatchScope:SupplementaryReadContract.TechnologyRootScope,PlanHash=new string('a',64),MethodEvidence="static-sdk-signature-verified",SemanticsEvidence="official-manual-source-candidate" }),
            "ExportBlocks" or "ExportTypes"=>new JsonObject { ["Executed"]=false,["ProjectFile"]=@"C:\Projects\Example.ap17",["SoftwarePath"]="exact/path",["GroupPath"]="exact/path",["Recursive"]=false,["InventoryHash"]=new string('a',64),["InventoryComplete"]=true,["RequiresSessionReset"]=false,["Items"]=new JsonArray() },
            "Disconnect"=>DisconnectContract.Idle(),
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
    private static int skipped;
    private static void Check(bool condition,string message) { if(!condition) throw new Exception(message); passed++; }
    private static async Task<bool> Rejected(McpServerTool tool,RequestContext<CallToolRequestParams> request)
    { try { return (await tool.InvokeAsync(request)).IsError==true; } catch(McpException ex) { return ex.ErrorCode==McpErrorCode.InvalidParams; } }
    private static async Task Main(string[] cliArgs)
    {
        var server=DispatchProxy.Create<IMcpServer,ServerProxy>();
        await DisconnectTests.Run(server,Check);
        await PassiveHostDiagnosticsTests.Run(server,Check);
        DocumentImportTests.Run(Check);
        BatchDocumentImportTests.Run(Check);
        DocumentExportTests.Run(Check);
        BatchDocumentExportTests.Run(Check);
        ExternalSourcePlanTests.Run(Check);
        ExternalSourceDeleteTests.Run(Check);
        AdapterSourceClosureTests.Run(Check);
        await HardwareCatalogDispatchTests.Run(server,Check);
        await DeviceAddDispatchTests.Run(server,Check);
        await RuntimeQueryTests.Run(server,Check);
        await OfflineSymbolManifestTests.Run(server,Check);
        await BatchExportDispatchTests.Run(server,Check);
        await SpecialExportDispatchTests.Run(server,Check);
        await BatchImportDispatchTests.Run(server,Check);
        await OfflineCompositionTests.Run(server,Check);
        await OfflineBlockCompositionTests.Run(server,Check);
        await OfflineLadderTests.Run(server,Check);
        var fake=new FakeWorker();
        var tools=FoundationTools.Create(fake);
        Check(tools.Select(t=>t.ProtocolTool.Name).Distinct(StringComparer.Ordinal).Count()==tools.Count,"Unique tool names");
        foreach(var tool in tools)
        {
            var def=FoundationTools.Definitions.Single(d=>d.Name==tool.ProtocolTool.Name);
            Check(!tool.ProtocolTool.InputSchema.GetProperty("additionalProperties").GetBoolean(),"Closed schema");
            Check(tool.ProtocolTool.Description!.Contains("native unverified"),"No unsupported acceptance claim");
            var args=def.Arguments.Where(a=>a.Required).ToDictionary(a=>a.Name,a=>a.Type=="integer" ? JsonSerializer.SerializeToElement(123) : JsonSerializer.SerializeToElement("exact/path"));
            if(def.Name=="ImportBlocksFromDocuments") args["fileNamesWithoutExtension"]=JsonSerializer.SerializeToElement(new[]{"Demo"});
            if(def.Name=="AddDeviceWithFallback") { args["preferredMlfb"]=JsonSerializer.SerializeToElement("6ES7513-1AM03-0AB0");args["preferredVersion"]=JsonSerializer.SerializeToElement("V3.0");args["deviceName"]=JsonSerializer.SerializeToElement("PLC_2"); }
            if(def.Name=="DeletePlcExternalSource") { args["groupPath"]=JsonSerializer.SerializeToElement("");args["externalSourceName"]=JsonSerializer.SerializeToElement("Pump.scl"); }
            if(def.Name=="PlanPlcExternalSourceImport") { args["groupPath"]=JsonSerializer.SerializeToElement(""); args["filePath"]=args["allowedFilePath"]=JsonSerializer.SerializeToElement(@"C:\Sources\Pump.scl"); }
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
        await SoftwareReadDispatchTests.Run(server,Check);
        await SupplementaryReadTests.Run(server,Check);
        await OfflineXmlBuilderTests.Run(server,Check);
        PathAndIdentityTests.Run(Check);
        ExchangeContractTests.Run(Check);
        ExportPublicationTests.Run(Check,message=>{skipped++; Console.WriteLine("SKIP: "+message);});
        BatchImportTests.Run(Check,message=>{skipped++; Console.WriteLine("SKIP: "+message);});
        SpecialExportTests.Run(Check,message=>{skipped++; Console.WriteLine("SKIP: "+message);});
        BatchExportTests.Run(Check,message=>{skipped++; Console.WriteLine("SKIP: "+message);});
        AdditionalMutationResultTests.Run(Check);
        OfflineCoverageBoundaryTests.Run(Check);
        CompileContractTests.Run(Check);
        await LifecycleContractTests.Run(Check);
        if(cliArgs.Length!=0) { if(cliArgs.Length!=2) throw new ArgumentException("Optional metadata check requires PublicAPI root and adapter project root."); ApiMetadataTests.Run(cliArgs[0],cliArgs[1],Check); }
        Console.WriteLine($"{passed} passed, 0 failed, {skipped} skipped. No worker process or Siemens assembly loaded.");
    }
}
