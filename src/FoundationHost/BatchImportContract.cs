using System.Text.Json.Nodes;

namespace TiaMcp.LegacyHost;

internal static class BatchImportContract
{
    internal static JsonObject Validate(JsonNode? payload,bool dryRun)
    {
        if(payload is not JsonObject result) throw new InvalidDataException("Missing batch import outcome.");
        string Text(JsonObject o,string key)=>o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new InvalidDataException("Missing batch text: "+key);
        bool Flag(JsonObject o,string key)=>o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : throw new InvalidDataException("Missing batch flag: "+key);
        int Count(string key)=>result[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : throw new InvalidDataException("Missing batch count.");
        void Hash(string value) {if(value.Length!=64 || value.Any(x=>!"0123456789abcdef".Contains(x))) throw new InvalidDataException("Invalid batch hash.");}
        void Object(JsonObject o,bool planned=true)
        {
            if(planned && (string.IsNullOrWhiteSpace(Text(o,"Name")) || Text(o,"Kind") is not ("FC" or "FB" or "OB" or "GlobalDB" or "InstanceDB" or "UDT" or "TagTable"))) throw new InvalidDataException("Invalid native identity.");
            Text(o,"Name");Text(o,"Kind");Text(o,"GroupPath"); if(o["Number"]!=null && (o["Number"] is not JsonValue n || !n.TryGetValue<int>(out var number) || number<0)) throw new InvalidDataException("Invalid native number.");
        }
        if(Flag(result,"Executed")==dryRun || string.IsNullOrWhiteSpace(Text(result,"ProjectFile")) || string.IsNullOrWhiteSpace(Text(result,"SoftwarePath")) || Text(result,"DependencyStatus")!="unverified-caller-order-required") throw new InvalidDataException("Conflicting batch identity/state.");
        if(Text(result,"Release") is not ("14sp1" or "15.1" or "16" or "17" or "18" or "19" or "20" or "21")) throw new InvalidDataException("Unknown release.");
        Flag(result,"Recursive");Hash(Text(result,"PlanHash"));
        if(result["Items"] is not JsonArray items || items.Count is <1 or >256) throw new InvalidDataException("Invalid batch item count.");
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);bool failed=false;int imported=0,failures=0;
        foreach(var node in items)
        {
            if(node is not JsonObject item || item["Planned"] is not JsonObject planned || item["ReturnedObjects"] is not JsonArray returned) throw new InvalidDataException("Malformed batch item.");
            var path=Text(item,"RelativePath"); if(!paths.Add(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Split('/').Any(x=>x is "" or "." or "..")) throw new InvalidDataException("Invalid manifest path.");
            Object(planned);Hash(Text(item,"InputSha256"));var status=Text(item,"Status");var attempted=Flag(item,"Attempted");var failure=Text(item,"Failure");
            foreach(var actual in returned) {if(actual==null && status=="failed" && attempted) continue;if(actual is not JsonObject obj) throw new InvalidDataException("Malformed returned identity.");Object(obj,status!="failed");}
            if(dryRun) {if(status!="planned" || attempted || returned.Count!=0 || failure!="") throw new InvalidDataException("Preview mutated.");continue;}
            if(status=="imported")
            {
                if(failed || !attempted || returned.Count!=1 || failure!="") throw new InvalidDataException("Invalid import success sequence.");
                var actual=returned[0]!.AsObject();
                foreach(var key in new[]{"Name","Kind","GroupPath"}) if(Text(actual,key)!=Text(planned,key)) throw new InvalidDataException("Returned identity mismatch.");
                if((Text(planned,"Kind") is "FC" or "FB" or "OB" or "GlobalDB" or "InstanceDB" && actual["Number"]==null) || (planned["Number"]!=null && !JsonNode.DeepEquals(planned["Number"],actual["Number"]))) throw new InvalidDataException("Returned number mismatch.");
                imported++;
            }
            else if(status=="failed") {if(failed || failure!=(attempted ? "native-outcome-uncertain" : "target-recheck-failed")) throw new InvalidDataException("Invalid failure sequence.");failed=true;failures++;}
            else if(status=="not-attempted") {if(!failed || attempted || returned.Count!=0 || failure!="") throw new InvalidDataException("Invalid skipped outcome.");}
            else throw new InvalidDataException("Invalid batch status.");
        }
        if(Count("ImportedCount")!=imported || Count("FailedCount")!=failures || Flag(result,"RequiresSessionReset")!=failed) throw new InvalidDataException("Batch counts/reset disagree with outcomes.");
        if(result["Imported"] is not JsonArray importedNames || !importedNames.Select(x=>x?.GetValue<string>()).SequenceEqual(items.OfType<JsonObject>().Where(x=>Text(x,"Status")=="imported").SelectMany(x=>x["ReturnedObjects"]!.AsArray().Select(o=>o!["Name"]!.GetValue<string>())))) throw new InvalidDataException("Imported names disagree with actual returned objects.");
        if(result["Failed"] is not JsonArray failedItems || failedItems.Count!=failures) throw new InvalidDataException("Failure summary disagrees with outcomes.");
        foreach(var node in failedItems)
        {
            if(node is not JsonObject summary || !items.OfType<JsonObject>().Any(x=>Text(x,"Status")=="failed" && Text(x,"RelativePath")==Text(summary,"Path") && Text(x,"Failure")==Text(summary,"Error"))) throw new InvalidDataException("Invalid failure summary.");
        }
        return result;
    }
}
