using System.Text.Json.Nodes;

namespace TiaMcp.LegacyHost;

internal sealed class WorkerOperationException(string message,int code,string outcome,string? evidenceJson = null) : Exception(message)
{
    internal int Code { get; }=code;
    internal string Outcome { get; }=outcome;
    internal string? EvidenceJson { get; }=evidenceJson;
    internal bool KnownNoMutation { get; }=outcome is "rejected-before-operation" or "read-failed";
}
internal sealed class WorkerOutcomeState
{
    internal bool Poisoned { get; private set; }
    internal void RequireUsable()
    { if(Poisoned) throw new InvalidOperationException("Previous native request has an unknown outcome. Inspect TIA before a new explicit session; requests are never replayed."); }
    internal void Failed(bool sent,Exception error) { if(WorkerProtocol.RequiresSessionReset(sent,error)) Poisoned=true; }
    internal void AcceptResult(string operation,JsonObject arguments,JsonNode? result)
    {
        WorkerProtocol.ValidateExchangeResult(operation,arguments,result);
        if ((operation == TiaMcp.PlcWorker.WorkerOperations.DeviceCreationCandidate || operation == TiaMcp.PlcWorker.WorkerOperations.PlcImportCandidate || operation == TiaMcp.PlcWorker.WorkerOperations.PlcExportCandidate || operation == TiaMcp.PlcWorker.WorkerOperations.SessionCandidate || operation == TiaMcp.PlcWorker.WorkerOperations.SaveCloseCandidate || operation == TiaMcp.PlcWorker.WorkerOperations.SourceCandidate) && result?["RequiresSessionReset"]?.GetValue<bool>() == true)
            Failed(true, new IOException("Device candidate outcome is unknown; inspect before a new explicit session."));
        if(operation is "ImportPlcExternalSource" or "GenerateBlocksFromExternalSource" && result?["RequiresSessionReset"]?.GetValue<bool>()==true)
            Failed(true,new IOException("External-source outcome is unknown; inspect before a new explicit session."));
        if(operation is "AddDeviceWithFallback" or "ImportFromDocuments" or "ImportBlocksFromDocuments" or "DeletePlcExternalSource" or "ExportAsDocuments" or "ExportBlocksAsDocuments" or "ExportPlcWatchTable" or "ExportTechnologyObject" or "ExportBlocks" or "ExportTypes" or "ImportBlocksFromDirectory" or "ImportPlcProgramFromDirectory" && result?["RequiresSessionReset"]?.GetValue<bool>()==true)
            Failed(true,new IOException("Batch stopped with retained partial outcomes; explicit inspection required."));
    }

}
internal static class WorkerProtocol
{
    internal static bool RequiresSessionReset(bool sent,Exception error) => sent && !(error is WorkerOperationException known && known.KnownNoMutation);
    internal static void ValidateExchangeResult(string operation,JsonObject arguments,JsonNode? result)
    {
        if (operation == TiaMcp.PlcWorker.WorkerOperations.SourceCandidate) { CandidateWire.Source(result, arguments); return; }
        if (operation == TiaMcp.PlcWorker.WorkerOperations.SaveCloseCandidate) { CandidateWire.SaveClose(result, arguments); return; }
        if (operation == TiaMcp.PlcWorker.WorkerOperations.SessionCandidate) { CandidateWire.Session(result, arguments); return; }
        if (operation == TiaMcp.PlcWorker.WorkerOperations.DeviceCreationCandidate) { CandidateWire.Device(result, arguments); return; }
        if (operation == TiaMcp.PlcWorker.WorkerOperations.PlcImportCandidate || operation == TiaMcp.PlcWorker.WorkerOperations.PlcExportCandidate) { CandidateWire.Import(result, arguments); return; }
        if(operation=="AddDeviceWithFallback") { DeviceAddContract.Validate(result,arguments); return; }
        if(operation=="ImportBlocksFromDocuments") { BatchDocumentImportContract.Validate(result,arguments); return; }
        if(operation=="ImportFromDocuments") { DocumentImportContract.Validate(result,arguments); return; }
        if(operation=="DeletePlcExternalSource") { ExternalSourceDeleteContract.Validate(result,arguments); return; }
        if(operation=="SearchHardwareCatalog") { HardwareCatalogContract.Validate(result,arguments); return; }
        if(operation=="PlanPlcExternalSourceImport") { ExternalSourcePlanContract.ValidateRequest(arguments,result); return; }
        if(operation is "ImportPlcExternalSource" or "GenerateBlocksFromExternalSource") { ExternalSourceWorkflowContract.Validate(operation,arguments,result); return; }
        if(operation=="ExportBlocksAsDocuments") { BatchDocumentExportContract.Validate(result,arguments); return; }
        if(operation=="ExportAsDocuments") { DocumentExportContract.ValidateRequest(arguments,result); return; }
        if(operation is "ExportPlcWatchTable" or "ExportTechnologyObject") { SpecialExportContract.ValidateRequest(operation,arguments,result); return; }
        if(operation=="Disconnect") { DisconnectContract.Validate(result,true); return; }
        if(operation=="Attach")
        {
            V17ProjectEnvelope.Wrap("Connect","Connection",result,false);
            if(result!["AttemptedPids"]![0]!.GetValue<int>()!=arguments["processId"]!.GetValue<int>()) throw new IOException("Attached process differs from the explicit request; session stopped.");
            return;
        }
        if(operation is not ("BindProject" or "OpenProject" or "CreateProject" or "SaveProject" or "CloseProject" or "CompileSoftware" or "ImportBlocksFromDirectory" or "ImportPlcProgramFromDirectory" or "ExportBlocks" or "ExportTypes" or "ExportBlock" or "ExportType" or "ExportTagTable" or "ImportBlocks" or "ImportTypes" or "ImportTagTables" or "CreateTagTable" or "CreateTag" or "CreateUserConstant")) return;
        var dryRun=arguments["dryRun"]?.GetValue<bool>() ?? operation!="BindProject";
        if(result is not JsonObject data || data["Executed"] is not JsonValue flag || !flag.TryGetValue<bool>(out var executed) || executed==dryRun)
            throw new IOException("Exchange worker returned a conflicting or missing execution outcome; session stopped.");
        if(data["ProjectFile"] is not JsonValue project || !project.TryGetValue<string>(out var projectFile) || string.IsNullOrWhiteSpace(projectFile))
            throw new IOException("Exchange worker returned no project identity; session stopped.");
        if(!dryRun && !string.Equals(Path.GetFullPath(arguments["expectedProjectFile"]!.GetValue<string>()),Path.GetFullPath(projectFile),StringComparison.OrdinalIgnoreCase))
            throw new IOException("Exchange worker result belongs to a different project; session stopped.");
        if(operation is "ExportBlocks" or "ExportTypes")
        {
            BatchExportContract.Validate(data,dryRun);
            if(data["SoftwarePath"]!.GetValue<string>()!=arguments["softwarePath"]!.GetValue<string>() ||
                data["GroupPath"]!.GetValue<string>()!=arguments["groupPath"]!.GetValue<string>() ||
                data["Recursive"]!.GetValue<bool>()!=(arguments["recursive"]?.GetValue<bool>() ?? false) ||
                data["Items"]!.AsArray().Count>(arguments["maxItems"]?.GetValue<int>() ?? 128) ||
                (!dryRun && data["InventoryHash"]!.GetValue<string>()!=arguments["expectedInventoryHash"]!.GetValue<string>()))
                throw new IOException("Batch export scope conflicts with the explicit request; session stopped.");
        }
        if(operation is "ImportBlocksFromDirectory" or "ImportPlcProgramFromDirectory")
        {
            BatchImportContract.Validate(data,dryRun);
            var items=data["Items"]!.AsArray();
            if(data["SoftwarePath"]!.GetValue<string>()!=arguments["softwarePath"]!.GetValue<string>() || data["Recursive"]!.GetValue<bool>()!=(operation=="ImportPlcProgramFromDirectory") || items.Count>(arguments["maxItems"]?.GetValue<int>() ?? 128) || (!dryRun && data["PlanHash"]!.GetValue<string>()!=arguments["expectedPlanHash"]!.GetValue<string>())) throw new IOException("Batch import scope/hash conflicts with request.");
            if(arguments["importOrder"] is JsonArray order && order.Count>0 && !items.Select(x=>x!["RelativePath"]!.GetValue<string>()).SequenceEqual(order.Select(x=>x!.GetValue<string>()),StringComparer.Ordinal)) throw new IOException("Batch import order conflicts with request.");
            foreach(var item in items)
            {
                var planned=item!["Planned"]!;var kind=planned["Kind"]!.GetValue<string>();
                var key=operation=="ImportBlocksFromDirectory" ? "groupPath" : kind=="UDT" ? "typeGroupPath" : kind=="TagTable" ? "tagFolderPath" : "blockGroupPath";
                if((operation=="ImportBlocksFromDirectory" && kind is "UDT" or "TagTable") || planned["GroupPath"]!.GetValue<string>()!=(arguments[key]?.GetValue<string>() ?? "")) throw new IOException("Batch import destination conflicts with request.");
            }
        }
        if(operation=="CompileSoftware") V17CompileEnvelope.Validate(data,dryRun);
    }
}
