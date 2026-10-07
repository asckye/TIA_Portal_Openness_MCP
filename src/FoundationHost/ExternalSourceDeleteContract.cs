using System.Text.Json.Nodes;
namespace TiaMcp.LegacyHost;
internal static class ExternalSourceDeleteContract
{
    internal static JsonObject Validate(JsonNode? payload,JsonObject request)
    {
        if(payload is not JsonObject r) throw new InvalidDataException("Missing delete result.");
        string Text(string k)=>r[k] is JsonValue v&&v.TryGetValue<string>(out var s)?s:throw new InvalidDataException("Missing delete text: "+k);
        bool Flag(string k)=>r[k] is JsonValue v&&v.TryGetValue<bool>(out var b)?b:throw new InvalidDataException("Missing delete flag: "+k);
        bool dry=request["dryRun"]?.GetValue<bool>()??true;
        if(Text("Release") is not ("14sp1" or "15.1" or "16" or "17" or "18" or "19" or "20" or "21") || Text("GroupPath")!="" || Text("Policy")!="root-external-source-delete-v1" || r["ProcessId"] is not JsonValue pid || !pid.TryGetValue<int>(out var process)||process<=0) throw new InvalidDataException("Invalid delete target.");
        if(!ExternalSourcePlanContract.SameSoftware(Text("SoftwarePath"),request["softwarePath"]?.GetValue<string>())) throw new InvalidDataException("Delete software scope mismatch.");
        foreach(var pair in new[]{("GroupPath","groupPath"),("SourceName","externalSourceName")}) if(Text(pair.Item1)!=request[pair.Item2]?.GetValue<string>()) throw new InvalidDataException("Delete scope mismatch.");
        if(string.IsNullOrWhiteSpace(Text("ProjectFile"))||string.IsNullOrWhiteSpace(Text("RootIdentity"))||Text("PlanHash").Length!=64||Text("PlanHash").Any(c=>!"0123456789abcdef".Contains(c))) throw new InvalidDataException("Missing delete identity/hash.");
        if(!dry && (request["confirm"]?.GetValue<bool>()!=true || Text("PlanHash")!=request["expectedPlanHash"]?.GetValue<string>() || !string.Equals(Text("ProjectFile"),request["expectedProjectFile"]?.GetValue<string>(),StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Delete review mismatch.");
        if(r["Inventory"] is not JsonArray array || array.Count>4096 || array.Any(x=>x is not JsonValue v||!v.TryGetValue<string>(out var name)||string.IsNullOrWhiteSpace(name))) throw new InvalidDataException("Incomplete delete inventory.");
        var names=array.Select(x=>x!.GetValue<string>()).ToArray();
        if(names.Distinct(StringComparer.Ordinal).Count()!=names.Length || !names.SequenceEqual(names.OrderBy(x=>x,StringComparer.Ordinal))) throw new InvalidDataException("Ambiguous delete inventory.");
        var status=Text("Status");bool found=names.Contains(Text("SourceName"),StringComparer.Ordinal);
        if(found!=!string.IsNullOrWhiteSpace(Text("TargetIdentity"))) throw new InvalidDataException("Target identity mismatch.");
        if(status=="planned") {if(!dry||!found||Flag("Attempted")||Flag("Executed")||Flag("Deleted")||Flag("RequiresSessionReset")||Text("Error")!="")throw new InvalidDataException("Invalid delete preview.");}
        else if(status=="not-found-not-deleted") {if(found||Flag("Attempted")||Flag("Executed")||Flag("Deleted")||Flag("RequiresSessionReset")||Text("Error")!="")throw new InvalidDataException("Missing target cannot be deleted.");}
        else if(status=="deleted-verified") {if(dry||!found||!Flag("Attempted")||!Flag("Executed")||!Flag("Deleted")||Flag("RequiresSessionReset")||Text("Error")!="")throw new InvalidDataException("Unverified delete success.");}
        else if(status=="outcome-unknown") {if(dry||!found||!Flag("Attempted")||Flag("Executed")||Flag("Deleted")||!Flag("RequiresSessionReset")||string.IsNullOrWhiteSpace(Text("Error")))throw new InvalidDataException("Unknown delete must poison session.");}
        else throw new InvalidDataException("Unknown delete status.");
        foreach(var k in new[]{"Generation","Compilation","Save","Download"})if(Text(k)!="notRun")throw new InvalidDataException("Delete scope exceeded.");
        if(Text("Recovery")!="notEstablished-no-automatic-backup-or-rollback")throw new InvalidDataException("Unverified recovery claim.");
        return r;
    }
}
