using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.LegacyHost;
internal static class SupplementaryReadContract
{
    internal const string WatchScope = "ordinary PLC root/user-group watch-table paths only; force tables, entries and live values excluded";
    internal const string TechnologyScope = "ordinary PLC root/user-group top-level technology-object engineering metadata only; sub-level TOs, parameters and live values excluded";
    internal const string TechnologyRootScope = "ordinary PLC root top-level technology-object engineering metadata only; no user-group composition in this API; sub-level TOs, parameters and live values excluded";
    internal static JsonObject Wrap(string tool,JsonNode? result,JsonSerializerOptions options)
    {
        if(tool is not ("GetPlcWatchTables" or "GetTechnologyObjects")) throw new InvalidDataException("Unknown supplementary reader.");
        if(result is not JsonObject source || source["Items"] is not JsonArray items || items.Count>10000) throw new InvalidDataException("Invalid supplementary read payload.");
        string Text(JsonObject obj,string key,bool empty=false)
        {
            if(obj[key] is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Length>4096 || (!empty && string.IsNullOrWhiteSpace(text))) throw new InvalidDataException("Missing supplementary field: "+key);
            return text;
        }
        var path=Text(source,"SoftwarePath"); var release=Text(source,"ReleaseKey"); var scope=Text(source,"Scope");
        if(source.Any(p=>p.Key is not ("SoftwarePath" or "ReleaseKey" or "Scope" or "Items"))) throw new InvalidDataException("Unexpected supplementary read field.");
        if(scope!=(tool=="GetPlcWatchTables"?WatchScope:release is "19" or "20" or "21"?TechnologyScope:TechnologyRootScope)) throw new InvalidDataException("Unexpected supplementary read scope.");
        if(release is not ("14sp1" or "15.1" or "16" or "17" or "18" or "19" or "20" or "21") || (tool=="GetPlcWatchTables" && release=="14sp1")) throw new InvalidDataException("Unsupported supplementary release.");
        var seen=new HashSet<string>(StringComparer.Ordinal); var outputItems=new JsonArray(); var unavailable=new JsonArray();
        string Wire(string name)=>options.PropertyNamingPolicy?.ConvertName(name)??name;
        foreach(var item in items)
        {
            if(tool=="GetPlcWatchTables")
            {
                if(item is not JsonValue value || !value.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name) || name.Length>4096 || !seen.Add(name)) throw new InvalidDataException("Invalid watch-table name list.");
                if(name.Split('/').Any(s=>string.IsNullOrWhiteSpace(Uri.UnescapeDataString(s)) || Uri.UnescapeDataString(s) is "." or ".." || Uri.EscapeDataString(Uri.UnescapeDataString(s))!=s)) throw new InvalidDataException("Invalid canonical watch-table path.");
                outputItems.Add(name);
            }
            else
            {
                if(item is not JsonObject row || row.Any(p=>p.Key is not ("Name" or "OfSystemLibElement" or "OfSystemLibVersion" or "Folder" or "UnavailableAttributes"))) throw new InvalidDataException("Invalid technology metadata.");
                var name=Text(row,"Name"); var folder=Text(row,"Folder",true);
                if(name is "." or "..") throw new InvalidDataException("Invalid technology object name.");
                if(folder.Length>0 && folder.Split('/').Any(s=>string.IsNullOrWhiteSpace(Uri.UnescapeDataString(s)) || Uri.UnescapeDataString(s) is "." or ".." || Uri.EscapeDataString(Uri.UnescapeDataString(s))!=s)) throw new InvalidDataException("Invalid technology folder identity.");
                if(!seen.Add(folder+"/"+Uri.EscapeDataString(name))) throw new InvalidDataException("Duplicate technology object identity.");
                if(row["UnavailableAttributes"] is not JsonArray missing || missing.Any(v=>v is not JsonValue val || !val.TryGetValue<string>(out var n) || n is not ("OfSystemLibElement" or "OfSystemLibVersion")) || missing.Select(v=>v!.GetValue<string>()).Distinct().Count()!=missing.Count) throw new InvalidDataException("Invalid unavailable metadata list.");
                var copy=new JsonObject { [Wire("Name")]=name,[Wire("Folder")]=folder };
                foreach(var key in new[]{"OfSystemLibElement","OfSystemLibVersion"})
                {
                    bool absent=missing.Any(v=>v!.GetValue<string>()==key);
                    if(absent && row[key]!=null || !absent && row[key]==null) throw new InvalidDataException("Contradictory optional technology metadata.");
                    if(!absent) copy[Wire(key)]=Text(row,key);
                }
                if(missing.Count!=0) unavailable.Add(new JsonObject { ["objectPath"]=(folder.Length==0?"":folder+"/")+Uri.EscapeDataString(name),["attributes"]=missing.DeepClone() });
                outputItems.Add(copy);
            }
        }
        var output=new JsonObject { [Wire("Items")]=outputItems,[Wire("Message")]=tool+" retrieved",[Wire("Meta")]=new JsonObject { ["success"]=true,["softwarePath"]=path,["releaseKey"]=release,["scope"]=scope,["nativeAcceptance"]="NOT RUN",["typedApiCompilation"]="pending-windows-rebuild",["manualReconciliation"]="partial",["unavailableAttributes"]=unavailable,["timestamp"]=DateTimeOffset.UtcNow.ToString("O") } };
        if(tool=="GetTechnologyObjects") { output[Wire("Ok")]=true; output[Wire("SoftwarePath")]=path; output[Wire("Count")]=items.Count; }
        return output;
    }
}
