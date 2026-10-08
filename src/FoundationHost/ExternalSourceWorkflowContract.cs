using System.Text.Json.Nodes;

namespace TiaMcp.FoundationHost;

internal static class ExternalSourceWorkflowContract
{
    internal static JsonObject Validate(string operation,JsonObject request,JsonNode? payload)
    {
        if(payload is not JsonObject result) throw new InvalidDataException("Missing external-source result.");
        string Text(string key)=>result[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new InvalidDataException("Missing external-source field: "+key);
        bool Flag(string key)=>result[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : throw new InvalidDataException("Missing external-source flag: "+key);
        var dryRun=request["dryRun"]?.GetValue<bool>() ?? true;
        if(Text("Operation")!=operation || !ExternalSourcePlanContract.SameSoftware(Text("SoftwarePath"),request["softwarePath"]?.GetValue<string>()) || Text("GroupPath")!="") throw new InvalidDataException("External-source result target mismatch.");
        if(Text("Release") is not ("14sp1" or "15.1" or "16" or "17" or "18" or "19" or "20" or "21") || Text("PlanHash").Length!=64 || string.IsNullOrWhiteSpace(Text("ProjectFile")) || string.IsNullOrWhiteSpace(Text("SourceName")) || result["ProcessId"] is not JsonValue pid || !pid.TryGetValue<int>(out var processId) || processId<=0) throw new InvalidDataException("External-source result lacks an exact target and plan hash.");
        if(!dryRun && (request["confirm"]?.GetValue<bool>()!=true || Text("PlanHash")!=request["expectedPlanHash"]?.GetValue<string>() || !SameFile(Text("ProjectFile"),request["expectedProjectFile"]?.GetValue<string>()))) throw new InvalidDataException("External-source execution review mismatch.");
        var status=Text("Status");
        bool validOutcome=status switch
        {
            "planned" => dryRun && !Flag("Attempted") && !Flag("Executed") && !Flag("RequiresSessionReset") && Text("Error")=="",
            "completed" => !dryRun && Flag("Attempted") && Flag("Executed") && !Flag("RequiresSessionReset") && Text("Error")=="",
            "failed" => !dryRun && operation=="GenerateBlocksFromExternalSource" && Flag("Attempted") && !Flag("Executed") && !Flag("RequiresSessionReset") && !string.IsNullOrWhiteSpace(Text("Error")),
            "outcome-unknown" => !dryRun && Flag("Attempted") && !Flag("Executed") && Flag("RequiresSessionReset") && !string.IsNullOrWhiteSpace(Text("Error")),
            _ => false
        };
        if(!validOutcome) throw new InvalidDataException("Conflicting external-source execution outcome.");
        foreach(var field in new[]{"Compilation","Save","Download"}) if(Text(field)!="notRun") throw new InvalidDataException("External-source result exceeds its operation scope.");
        if(operation=="ImportPlcExternalSource")
        {
            if(request["groupPath"]?.GetValue<string>()!="" || !SameFile(Text("FilePath"),request["filePath"]?.GetValue<string>()) || Text("InputSha256").Length!=64 || string.IsNullOrWhiteSpace(Text("RequestedSourceName")) || result["ByteCount"] is not JsonValue size || !size.TryGetValue<long>(out var byteCount) || byteCount<=0 || Text("Generation")!="notRun") throw new InvalidDataException("Invalid external-source import identity.");
            if(result["SourceNamesAfter"] is not JsonArray names || names.Any(x=>x is not JsonValue v || !v.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))) throw new InvalidDataException("Missing external-source readback.");
            if(status=="completed" && !names.Any(x=>x!.GetValue<string>()==Text("SourceName"))) throw new InvalidDataException("Created source is absent from readback.");
        }
        else
        {
            if(Text("SourceName")!=request["externalSourceName"]?.GetValue<string>() || string.IsNullOrWhiteSpace(Text("SourceIdentity")) || !Flag("MayOverwriteExistingBlocks") || Flag("SourceContentReviewed")) throw new InvalidDataException("Invalid generation source contract.");
            var old=Text("Release")=="14sp1";
            if(Text("GenerationOption")!=(old?"parameterless":"None") || Text("ResultBasis")!=(old?"inventory-observation-only; native API returns void":"native-returned-objects-with-path-readback")) throw new InvalidDataException("Generation return semantics mismatch this release.");
            foreach(var field in new[]{"ObjectsBefore","ObjectsAfter","GeneratedObjects","ObservedChanges"}) ValidateObjects(result[field],field);
            if(old && result["GeneratedObjects"]!.AsArray().Count!=0) throw new InvalidDataException("V14 SP1 void API cannot claim a native generated object list.");
            if((dryRun || status=="failed") && new[]{"ObjectsAfter","GeneratedObjects","ObservedChanges"}.Any(x=>result[x]!.AsArray().Count!=0)) throw new InvalidDataException("Preview or rolled-back generation cannot report generated objects.");
            if(status=="completed") foreach(var item in result["GeneratedObjects"]!.AsArray())
                if(!result["ObjectsAfter"]!.AsArray().Any(x=>x!["Kind"]!.GetValue<string>()==item!["Kind"]!.GetValue<string>() && x["Path"]!.GetValue<string>()==item["Path"]!.GetValue<string>())) throw new InvalidDataException("Generated object is absent from PLC readback.");
        }
        return result;
    }
    private static bool SameFile(string actual,string? requested)=>requested!=null && string.Equals(TiaMcp.Adapters.MutationIdentityPolicy.AbsoluteFile(actual),TiaMcp.Adapters.MutationIdentityPolicy.AbsoluteFile(requested),StringComparison.OrdinalIgnoreCase);
    private static void ValidateObjects(JsonNode? payload,string field)
    {
        if(payload is not JsonArray items) throw new InvalidDataException("Missing generation object list: "+field);
        foreach(var item in items)
            if(item is not JsonObject data || data["Kind"]?.GetValue<string>() is not ("block" or "type") || string.IsNullOrWhiteSpace(data["Path"]?.GetValue<string>()) || string.IsNullOrWhiteSpace(data["Name"]?.GetValue<string>()) || data["IsConsistent"] is not JsonValue consistent || !consistent.TryGetValue<bool>(out _)) throw new InvalidDataException("Incomplete generation object readback: "+field);
    }
}
