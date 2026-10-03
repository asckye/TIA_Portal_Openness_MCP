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
            "ImportPlcExternalSource" or "GenerateBlocksFromExternalSource"=>ExternalSourceWorkflowTests.Payload(operation,arguments),
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
