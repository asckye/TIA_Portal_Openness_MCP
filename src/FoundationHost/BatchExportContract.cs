using System.Text.Json.Nodes;

namespace TiaMcp.FoundationHost;

internal static class BatchExportContract
{
    internal static JsonObject Validate(JsonNode? payload,bool dryRun)
    {
        if(payload is not JsonObject result || result["Executed"] is not JsonValue flag || !flag.TryGetValue<bool>(out var executed) || executed==dryRun ||
            result["InventoryComplete"] is not JsonValue complete || !complete.TryGetValue<bool>(out var isComplete) || !isComplete ||
            result["RequiresSessionReset"] is not JsonValue reset || !reset.TryGetValue<bool>(out var requiresReset) ||
            result["Recursive"] is not JsonValue recurse || !recurse.TryGetValue<bool>(out _) ||
            result["Items"] is not JsonArray items || items.Count>256)
            throw new InvalidDataException("Malformed batch export outcome.");
        string Text(JsonObject obj,string name) => obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new InvalidDataException("Missing batch export field: "+name);
        foreach(var name in new[]{"ProjectFile","SoftwarePath","InventoryHash"}) if(string.IsNullOrWhiteSpace(Text(result,name))) throw new InvalidDataException("Empty batch identity.");
        Text(result,"GroupPath");
        var hash=Text(result,"InventoryHash");
        if(hash.Length!=64 || hash.Any(c=>!"0123456789abcdef".Contains(c))) throw new InvalidDataException("Malformed inventory hash.");
        var paths=new HashSet<string>(StringComparer.Ordinal); var outputs=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previous=null; bool failed=false;
        foreach(var node in items)
        {
            if(node is not JsonObject item) throw new InvalidDataException("Malformed batch item.");
            var path=Text(item,"ObjectPath"); var output=Text(item,"OutputFile"); var status=Text(item,"Status");
            if(string.IsNullOrWhiteSpace(path) || !paths.Add(path) || !outputs.Add(output) || string.IsNullOrWhiteSpace(output) || (previous!=null && string.CompareOrdinal(previous,path)>=0)) throw new InvalidDataException("Ambiguous or unordered batch inventory.");
            previous=path;
            if(dryRun ? status is not ("planned" or "inconsistent") : status is not ("exported" or "inconsistent" or "failed" or "not-attempted")) throw new InvalidDataException("Invalid batch status.");
            if(status=="failed") { if(failed) throw new InvalidDataException("Multiple batch failures imply continued execution."); failed=true; }
            else if((status=="not-attempted" && !failed) || (status=="exported" && failed)) throw new InvalidDataException("Batch continued after failure.");
            if(item["Warnings"] is not JsonArray warnings || warnings.Any(w=>w is not JsonValue v || !v.TryGetValue<string>(out _)) || item["Evidence"] is not JsonObject evidence) throw new InvalidDataException("Invalid batch evidence.");
            Text(item,"XmlContent");
            foreach(var pair in evidence) if(pair.Key is not ("outputFile" or "stagedFile" or "recoveryDirectory" or "exportPhase" or "stagedSha256") || pair.Value is not JsonValue v || !v.TryGetValue<string>(out _)) throw new InvalidDataException("Unexpected batch evidence.");
        }
        if(requiresReset!=failed) throw new InvalidDataException("Batch reset state conflicts with item outcomes.");
        return result;
    }
}
