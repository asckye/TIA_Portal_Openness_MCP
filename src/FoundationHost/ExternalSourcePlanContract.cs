using System.Text.Json.Nodes;
namespace TiaMcp.FoundationHost;
internal static class ExternalSourcePlanContract
{
    // The worker resolves a unique short PLC host name before returning the exact address.
    internal static bool SameSoftware(string actual,string? requested) => actual==requested
        || requested!=null && requested.Length>0 && requested is not ("." or "..") && requested.IndexOf('/')<0
            && string.Equals(Uri.UnescapeDataString(actual.Split('/').Last()),requested,StringComparison.OrdinalIgnoreCase);
    internal static JsonObject Validate(JsonNode? payload)
    {
        if(payload is not JsonObject result) throw new InvalidDataException("Missing external-source plan.");
        var fields=new[]{"Status","MutationStatus","Executed","Attempted","CreatedCount","ApplyBlocked","ApplyBlockedReason","Generation","Compilation","Save","Download","Release","ProjectFile","ProcessId","SoftwarePath","GroupPath","SessionKind","FilePath","SourceName","Extension","ByteCount","InputSha256","PlanHash","InventoryCount","CollisionStatus","ValidationScope","Policy"};
        if(result.Count!=fields.Length || result.Any(p=>!fields.Contains(p.Key))) throw new InvalidDataException("Unexpected external-source plan shape.");
        string Text(string k)=>result[k] is JsonValue v && v.TryGetValue<string>(out var s)?s:throw new InvalidDataException("Invalid plan text: "+k);
        bool Flag(string k)=>result[k] is JsonValue v && v.TryGetValue<bool>(out var b)?b:throw new InvalidDataException("Invalid plan flag: "+k);
        long Number(string k)=>result[k] is JsonValue v && v.TryGetValue<long>(out var n)?n:throw new InvalidDataException("Invalid plan number: "+k);
        if(Text("Status")!="planned" || Text("MutationStatus")!="notAttempted" || Flag("Executed") || Flag("Attempted") || Number("CreatedCount")!=0 || !Flag("ApplyBlocked") || Text("ApplyBlockedReason").Length<40) throw new InvalidDataException("Plan cannot claim a native mutation outcome.");
        foreach(var k in new[]{"Generation","Compilation","Save","Download"}) if(Text(k)!="notRun") throw new InvalidDataException("Plan cannot claim downstream execution.");
        if(Text("Release") is not ("14sp1" or "15.1" or "16" or "17" or "18" or "19" or "20" or "21") || Text("GroupPath")!="" || Text("SessionKind")!="ordinary-project" || Text("Policy")!="root-external-source-plan-v1" || Number("ProcessId")<=0 || Number("ProcessId")>int.MaxValue) throw new InvalidDataException("Invalid plan target scope.");
        if(Number("ByteCount")<1 || Number("ByteCount")>4194304 || Number("InventoryCount")<0 || Number("InventoryCount")>4096) throw new InvalidDataException("Plan bounds conflict.");
        foreach(var k in new[]{"InputSha256","PlanHash"}) if(Text(k).Length!=64 || Text(k).Any(c=>!"0123456789abcdef".Contains(c))) throw new InvalidDataException("Invalid plan hash.");
        foreach(var k in new[]{"ProjectFile","SoftwarePath","FilePath","SourceName","ValidationScope"}) if(string.IsNullOrWhiteSpace(Text(k)) || Text(k).Length>4096) throw new InvalidDataException("Missing plan identity.");
        if(Text("Extension") is not (".scl" or ".awl" or ".db" or ".udt") || Text("FilePath").Split('\\').Last()!=Text("SourceName") || !Text("SourceName").EndsWith(Text("Extension"),StringComparison.OrdinalIgnoreCase) || Text("CollisionStatus")!="none-in-complete-root-snapshot-not-race-proof") throw new InvalidDataException("Plan source/collision identity mismatch.");
        return result;
    }
    internal static JsonObject ValidateRequest(JsonObject request,JsonNode? payload)
    {
        var result=Validate(payload);
        if(request["dryRun"]?.GetValue<bool>()==false) throw new InvalidDataException("External-source apply is blocked.");
        if(!SameSoftware(result["SoftwarePath"]!.GetValue<string>(),request["softwarePath"]?.GetValue<string>())) throw new InvalidDataException("External-source plan differs from requested software.");
        if(result["GroupPath"]!.GetValue<string>()!=request["groupPath"]?.GetValue<string>()) throw new InvalidDataException("External-source plan differs from requested group.");
        foreach(var key in new[]{"filePath","allowedFilePath"}) if(result["FilePath"]!.GetValue<string>()!=request[key]?.GetValue<string>().Replace('/', '\\')) throw new InvalidDataException("External-source plan differs from requested file.");
        var hash=request["expectedPlanHash"]?.GetValue<string>()??"";
        var project=request["expectedProjectFile"]?.GetValue<string>()??"";
        if(hash!="" && (hash!=result["PlanHash"]!.GetValue<string>() || request["confirm"]?.GetValue<bool>()!=true || project=="")) throw new InvalidDataException("External-source reviewed plan differs from request.");
        if(project!="" && !string.Equals(project,result["ProjectFile"]!.GetValue<string>(),StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("External-source project identity mismatch.");
        return result;
    }
}
