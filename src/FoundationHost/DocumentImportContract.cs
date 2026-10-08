using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
namespace TiaMcp.FoundationHost;
internal static class DocumentImportContract
{
    internal static string PlanHash(JsonObject result)
    {
        string Field(string text)=>text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+text;
        var fields=new[]{"Policy","Release","ProjectFile","ProcessId","TargetIdentity","SoftwarePath","GroupPath","InputDirectory","DeclaredName","Kind","Language","NativeOptions","CodeSha256","ResourceSha256","Validation","InstalledUpdate"};
        var canonical=string.Concat(fields.Select(key=>Field(key=="ProcessId"?result[key]!.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture):result[key]!.GetValue<string>())))+string.Concat(result["Inventory"]!.AsArray().Select(x=>Field(x!.GetValue<string>())));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
    internal static JsonObject Validate(JsonNode? payload,JsonObject request)
    {
        if(payload is not JsonObject result)throw new InvalidDataException("Missing single-document import result.");
        string Text(string key)=>result[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length<=8192?s:throw new InvalidDataException("Invalid document import text: "+key);
        bool Flag(string key)=>result[key] is JsonValue v && v.TryGetValue<bool>(out var b)?b:throw new InvalidDataException("Invalid document import flag: "+key);
        var fields=new[]{"Status","Executed","Attempted","MayHaveChanged","RequiresSessionReset","ExistsVerified","ContentVerified","Error","Release","ProjectFile","ProcessId","TargetIdentity","SoftwarePath","GroupPath","InputDirectory","DeclaredName","Kind","Language","CodeSha256","ResourceSha256","PlanHash","Inventory","ImportedIdentities","Messages","NativeState","NativeOptions","Compilation","Save","Download","InstalledUpdate","Validation","Recovery","Policy"};
        if(result.Count!=fields.Length || result.Any(p=>!fields.Contains(p.Key)))throw new InvalidDataException("Unexpected document import result shape.");
        bool Hash(string value)=>value.Length==64 && value.All(c=>"0123456789abcdef".Contains(c));
        if(!Hash(Text("PlanHash")) || !Hash(Text("CodeSha256")) || (Text("ResourceSha256")!="" && !Hash(Text("ResourceSha256"))))throw new InvalidDataException("Invalid document hashes.");
        if(Text("Release") is not ("20" or "21") || (Text("Release")=="20" && Text("ResourceSha256")=="") || Text("Kind")!="GlobalDB" || Text("Language")!="DB" || Text("NativeOptions")!="None" || Text("Policy")!="single-document-global-db-v1" || Text("Validation")!="bounded-global-db-lexical-admission-not-Siemens-syntax-or-content-validity" || Text("InstalledUpdate")!="unknown-not-inferred-from-major-version" || Text("Recovery")!="unknown-no-automatic-retry-or-rollback")throw new InvalidDataException("Unverified document import scope.");
        if(result["ProcessId"] is not JsonValue pid || !pid.TryGetValue<int>(out var process) || process<=0 || string.IsNullOrWhiteSpace(Text("TargetIdentity")) || string.IsNullOrWhiteSpace(Text("ProjectFile")) || string.IsNullOrWhiteSpace(Text("SoftwarePath")) || string.IsNullOrWhiteSpace(Text("DeclaredName")))throw new InvalidDataException("Missing document target identity.");
        foreach(var key in new[]{"Compilation","Save","Download"})if(Text(key)!="notRun")throw new InvalidDataException("Unexpected document follow-on work.");
        if(Flag("ContentVerified"))throw new InvalidDataException("Content validity was not established.");
        JsonArray Array(string key,int maximum)
        {
            if(result[key] is not JsonArray a || a.Count>maximum || a.Any(x=>x is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Length>4096))throw new InvalidDataException("Invalid document evidence array: "+key);
            return a;
        }
        var inventory=Array("Inventory",4096);var imported=Array("ImportedIdentities",2);Array("Messages",256);
        var inventoryText=inventory.Select(x=>x!.GetValue<string>()).ToArray();
        if(inventoryText.Any(string.IsNullOrEmpty) || !inventoryText.SequenceEqual(inventoryText.OrderBy(x=>x,StringComparer.Ordinal),StringComparer.Ordinal) || inventoryText.Distinct(StringComparer.Ordinal).Count()!=inventoryText.Length)throw new InvalidDataException("Invalid complete inventory snapshot.");
        if(PlanHash(result)!=Text("PlanHash"))throw new InvalidDataException("Document evidence differs from its deterministic plan hash.");
        var dry=request["dryRun"]?.GetValue<bool>()??true;var status=Text("Status");
        if(dry)
        {
            if(status!="planned" || Flag("Executed") || Flag("Attempted") || Flag("MayHaveChanged") || Flag("RequiresSessionReset") || Flag("ExistsVerified") || Text("Error")!="" || imported.Count!=0 || Text("NativeState")!="notRun" || result["Messages"]!.AsArray().Count!=0)throw new InvalidDataException("Conflicting document preview outcome.");
        }
        else
        {
            if(!Flag("Executed") || !Flag("Attempted") || !Flag("MayHaveChanged") || status is not ("imported" or "unknown") || Flag("RequiresSessionReset")!=(status=="unknown") || Flag("ExistsVerified")!=(status=="imported"))throw new InvalidDataException("Conflicting attempted document outcome.");
            string Field(string text)=>text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+text;
            var identity=Field(Text("GroupPath"))+Field(Text("DeclaredName"))+Field("GlobalDB")+Field("returned");
            if(status=="imported" && (Text("NativeState")!="Success" || Text("Error")!="" || imported.Count!=1 || imported[0]!.GetValue<string>()!=identity))throw new InvalidDataException("Import success lacks exact identity evidence.");
            if(status=="unknown" && Text("Error")!="native-outcome-uncertain-inspect-before-new-session")throw new InvalidDataException("Unknown outcome missing uncertainty disclosure.");
            if(request["confirm"]?.GetValue<bool>()!=true || Text("ProjectFile")!=request["expectedProjectFile"]?.GetValue<string>() || Text("PlanHash")!=request["expectedPlanHash"]?.GetValue<string>())throw new InvalidDataException("Document result differs from reviewed project/hash.");
        }
        if(!TiaOpenness.Shared.NativeExportPolicy.ResolvedIdentityMatches(request["softwarePath"]!.GetValue<string>(),Text("SoftwarePath")) || TiaOpenness.Shared.NativeInputPolicy.FullPath(request["importPath"]!.GetValue<string>())!=TiaOpenness.Shared.NativeInputPolicy.FullPath(Text("InputDirectory")))throw new InvalidDataException("Document input/software identity mismatch.");
        foreach(var pair in new[]{("GroupPath","groupPath"),("DeclaredName","fileNameWithoutExtension")})if(Text(pair.Item1)!=request[pair.Item2]?.GetValue<string>())throw new InvalidDataException("Document request/response identity mismatch.");
        if(request["overwrite"]?.GetValue<bool>()==true)throw new InvalidDataException("Document overwrite is not admitted.");
        return result;
    }
}
