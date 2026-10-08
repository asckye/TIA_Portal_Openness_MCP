using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TiaMcp.FoundationHost;

// SDK options are supplied by the caller, so this contract can be tested without
// the SDK or Siemens assemblies. Never wrap malformed/missing native results as success.
internal static class SoftwareReadContract
{
    internal static JsonObject Wrap(string tool,JsonNode? result,JsonSerializerOptions options)
    {
        if(tool is not ("GetSoftwareInfo" or "GetSoftwareTree")) throw new InvalidDataException("Unknown software reader.");
        if(result is not JsonObject source || source["Meta"] is not JsonObject meta)
            throw new InvalidDataException("Worker returned invalid software details.");
        RequireString(meta,"softwarePath"); RequireString(meta,"scope");
        var allowed=tool=="GetSoftwareInfo" ? new[]{"Name","Attributes","Description","Meta"} : new[]{"Tree","Meta"};
        if(source.Any(p=>!allowed.Contains(p.Key,StringComparer.Ordinal))) throw new InvalidDataException("Worker returned unexpected software fields.");
        if(tool=="GetSoftwareInfo")
        {
            RequireString(source,"Name");
            if(source["Attributes"] is not JsonArray attributes) throw new InvalidDataException("Worker returned invalid software attributes.");
            foreach(var item in attributes)
            {
                if(item is not JsonObject attribute) throw new InvalidDataException("Worker returned invalid software attribute.");
                RequireString(attribute,"Name");
                if(attribute.Any(p=>p.Key is not ("Name" or "Value" or "AccessMode"))) throw new InvalidDataException("Worker returned unexpected attribute fields.");
                if(attribute["AccessMode"]!=null) RequireString(attribute,"AccessMode");
            }
            if(source["Description"]!=null && (source["Description"] is not JsonValue description || !description.TryGetValue<string>(out _)))
                throw new InvalidDataException("Worker returned invalid software description.");
        }
        else
        {
            RequireString(source,"Tree");
            if(meta["paths"] is not JsonArray paths || paths.Count==0) throw new InvalidDataException("Worker returned no software tree identities.");
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var item in paths)
            {
                if(item is not JsonObject path) throw new InvalidDataException("Worker returned an invalid tree identity.");
                RequireString(path,"name"); var kind=RequireString(path,"kind"); var address=RequireString(path,"path");
                var plc=meta["softwarePath"]!.GetValue<string>();
                var root=kind is "block" or "block-group" ? plc+"/blocks" : kind is "type" or "type-group" ? plc+"/types" : throw new InvalidDataException("Worker returned unknown tree kind.");
                var selector=kind=="block" ? "blockPath" : kind=="type" ? "typePath" : "groupPath";
                if(!Under(address,root) || !seen.Add(kind+"\0"+address))
                    throw new InvalidDataException("Worker returned conflicting tree identities.");
                if(path["objectPath"] is not JsonValue selected || !selected.TryGetValue<string>(out var relative) ||
                    path["selectorParameter"]?.GetValue<string>()!=selector ||
                    relative!=(address==root ? "" : address.Substring(root.Length+1)) ||
                    (relative.Length==0 && kind is "block" or "type"))
                    throw new InvalidDataException("Worker returned an invalid tree selector.");
                if(relative.Length!=0 && relative.Split('/').Any(segment=>
                    string.IsNullOrWhiteSpace(Uri.UnescapeDataString(segment)) || Uri.UnescapeDataString(segment) is "." or ".." ||
                    Uri.EscapeDataString(Uri.UnescapeDataString(segment))!=segment))
                    throw new InvalidDataException("Worker returned a noncanonical tree selector.");
            }
        }
        string Wire(string name)=>options.PropertyNamingPolicy?.ConvertName(name)??name;
        var output=new JsonObject();
        foreach(var pair in source)
        {
            if(pair.Key=="Meta") continue;
            if(pair.Value==null && options.DefaultIgnoreCondition!=JsonIgnoreCondition.Never) continue;
            if(pair.Key=="Attributes" && pair.Value is JsonArray attrs)
            {
                var rows=new JsonArray();
                foreach(var row in attrs.OfType<JsonObject>())
                {
                    var copy=new JsonObject();
                    foreach(var field in row)
                        if(field.Value!=null || options.DefaultIgnoreCondition==JsonIgnoreCondition.Never)
                            copy[Wire(field.Key)]=field.Value?.DeepClone(); // Value is opaque user data.
                    rows.Add(copy);
                }
                output[Wire(pair.Key)]=rows;
            }
            else output[Wire(pair.Key)]=pair.Value?.DeepClone();
        }
        var details=(JsonObject)meta.DeepClone();
        details["success"]=true; details["timestamp"]=DateTimeOffset.UtcNow.ToString("O");
        details["nativeAcceptance"]="NOT RUN"; details["typedApiCompilation"]="pending-windows-rebuild";
        details["manualReconciliation"]="candidate-two-tool-scope";
        details["pathContract"]="Meta softwarePath selects PLC; paths.objectPath feeds paths.selectorParameter; path+kind is full identity; unique legacy software aliases accepted";
        output[Wire("Message")]=tool+" retrieved"; output[Wire("Meta")]=details;
        return output;
    }
    private static bool Under(string path,string root) => path==root || path.StartsWith(root+"/",StringComparison.Ordinal);
    private static string RequireString(JsonObject value,string field)
    {
        if(value[field] is not JsonValue node || !node.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("Worker returned invalid "+field+".");
        return text;
    }
}
