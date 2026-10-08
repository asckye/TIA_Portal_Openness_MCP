using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
namespace TiaMcp.FoundationHost;
internal static class BatchDocumentExportContract
{
    private static string Hash(params string[] fields)
    {
        var value=new StringBuilder(); foreach(var field in fields) value.Append(field.Length).Append(':').Append(field);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString()))).ToLowerInvariant();
    }
    internal static JsonObject Validate(JsonNode? payload,JsonObject request)
    {
        if(payload is not JsonObject result) throw new InvalidDataException("Missing batch document result.");
        void Shape(JsonObject obj,string[] keys) {if(obj.Count!=keys.Length || obj.Any(p=>!keys.Contains(p.Key))) throw new InvalidDataException("Unexpected batch document shape.");}
        string Text(JsonObject obj,string key)=>obj[key] is JsonValue value && value.TryGetValue<string>(out var text)?text:throw new InvalidDataException("Invalid batch document text.");
        bool Flag(JsonObject obj,string key)=>obj[key] is JsonValue value && value.TryGetValue<bool>(out var flag)?flag:throw new InvalidDataException("Invalid batch document flag.");
        Shape(result,new[]{"Executed","ReleaseKey","ProjectFile","SoftwarePath","GroupPath","Recursive","MaxItems","OutputDirectory","PlanHash","InventoryComplete","Status","RequiresSessionReset","RecoveryDirectory","Options","Evidence","Items"});
        var dry=request["dryRun"]?.GetValue<bool>()??true;
        var release=Text(result,"ReleaseKey");var status=Text(result,"Status");var output=Text(result,"OutputDirectory");var group=Text(result,"GroupPath");var recursive=Flag(result,"Recursive");
        if(release is not ("20" or "21") || Flag(result,"Executed")==dry || !Flag(result,"InventoryComplete") || Flag(result,"RequiresSessionReset")!=(status=="failed") || (dry?status is not ("planned" or "inconsistent"):status is not ("exported" or "failed"))) throw new InvalidDataException("Batch document outcome conflicts.");
        if(result["MaxItems"] is not JsonValue maximum || !maximum.TryGetValue<int>(out var maxItems) || maxItems<1 || maxItems>256 || maxItems!=(request["maxItems"]?.GetValue<int>()??128)) throw new InvalidDataException("Invalid batch document item bound.");
        foreach(var key in new[]{"ProjectFile","SoftwarePath","OutputDirectory"}) if(string.IsNullOrWhiteSpace(Text(result,key)) || Text(result,key).Length>4096) throw new InvalidDataException("Missing batch document identity.");
        if(Text(result,"Options")!="native-default-two-argument-overload" || Text(result,"Evidence")!="official-manual-source-candidate; exact-sdk-build/native-acceptance-pending; no-cross-version-roundtrip-claim") throw new InvalidDataException("Unsupported document policy.");
        if(status=="failed"?string.IsNullOrWhiteSpace(Text(result,"RecoveryDirectory")):Text(result,"RecoveryDirectory")!="") throw new InvalidDataException("Recovery state conflict.");
        foreach(var pair in new[]{("SoftwarePath","softwarePath"),("GroupPath","groupPath"),("OutputDirectory","exportPath")}) if(Text(result,pair.Item1)!=request[pair.Item2]?.GetValue<string>()) throw new InvalidDataException("Batch document result identity mismatch.");
        if(recursive!=(request["recursive"]?.GetValue<bool>()??false) || request["preservePath"]?.GetValue<bool>()==true) throw new InvalidDataException("Batch document scope differs from request.");
        if(result["Items"] is not JsonArray items || items.Count<1 || items.Count>maxItems) throw new InvalidDataException("Missing complete bounded document inventory.");
        var fields=new List<string>{"batch-documents-v1",release,Text(result,"ProjectFile"),Text(result,"SoftwarePath"),group,recursive.ToString(),maxItems.ToString(System.Globalization.CultureInfo.InvariantCulture),output,Text(result,"Options")};
        string? previous=null; bool stopped=false,failed=false,inconsistent=false;var outputs=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var node in items)
        {
            if(node is not JsonObject item) throw new InvalidDataException("Invalid batch document item.");
            Shape(item,new[]{"BlockPath","OutputDirectory","Language","Consistent","Status","Files"});
            var block=Text(item,"BlockPath");var directory=Text(item,"OutputDirectory");var language=Text(item,"Language");var consistent=Flag(item,"Consistent");var state=Text(item,"Status");
            if(string.IsNullOrWhiteSpace(block) || (previous!=null && string.CompareOrdinal(previous,block)>=0) || !(group=="" || block.StartsWith(group+"/",StringComparison.Ordinal)) || (!recursive && block.Substring(group==""?0:group.Length+1).Contains('/'))) throw new InvalidDataException("Unordered or out-of-scope batch document inventory.");
            previous=block; inconsistent|=!consistent;
            if(language is not ("LAD" or "DB") || directory!=Path.Combine(output,"block-"+Hash(block)) || !outputs.Add(directory)) throw new InvalidDataException("Batch document output/language conflict.");
            if(dry) { if(state!=(consistent?"planned":"inconsistent")) throw new InvalidDataException("Preview eligibility conflict."); }
            else
            {
                if(!consistent) throw new InvalidDataException("Execution of inconsistent inventory.");
                if(status=="exported") {if(state!="exported") throw new InvalidDataException("False all-success document result.");}
                else
                {
                    if(state=="staged") {if(stopped) throw new InvalidDataException("Document batch continued after failure.");}
                    else if(state=="failed") {if(stopped || failed) throw new InvalidDataException("Multiple or late document failures.");failed=true;stopped=true;}
                    else if(state=="not-attempted") stopped=true;
                    else throw new InvalidDataException("Failed tree cannot claim published documents.");
                }
            }
            var name=Uri.UnescapeDataString(block.Split('/').Last());
            if(item["Files"] is not JsonArray files || (files.Count!=2 && !(release=="21" && state is ("staged" or "exported") && files.Count==1)) || files[0]?.GetValue<string>()!=name+".s7dcl" || (files.Count==2 && files[1]?.GetValue<string>()!=name+".s7res")) throw new InvalidDataException("Batch document file set conflicts.");
            fields.AddRange(new[]{block,directory,language,consistent.ToString(),name+".s7dcl",name+".s7res"});
        }
        if(dry && status!=(inconsistent?"inconsistent":"planned")) throw new InvalidDataException("Batch eligibility summary conflict.");
        var hash=Text(result,"PlanHash");if(hash!=Hash(fields.ToArray()) || (!dry && hash!=request["expectedPlanHash"]?.GetValue<string>())) throw new InvalidDataException("Batch document hash mismatch.");
        if(!dry && !string.Equals(Path.GetFullPath(Text(result,"ProjectFile")),Path.GetFullPath(request["expectedProjectFile"]?.GetValue<string>()??""),StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Batch document project differs from confirmation.");
        return result;
    }
}
