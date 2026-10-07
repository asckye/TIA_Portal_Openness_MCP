using System.Text.Json.Nodes;
namespace TiaMcp.LegacyHost;
internal static class DocumentExportContract
{
    internal static JsonObject Validate(JsonNode? payload,bool dryRun)
    {
        if(payload is not JsonObject result) throw new InvalidDataException("Missing document export result.");
        var fields=new[]{"Executed","ReleaseKey","ProjectFile","SoftwarePath","BlockPath","OutputDirectory","Language","PlanHash","Status","RequiresSessionReset","Options","Files","Evidence","RecoveryDirectory"};
        if(result.Count!=fields.Length || result.Any(p=>!fields.Contains(p.Key))) throw new InvalidDataException("Unexpected document export shape.");
        string Text(string key)=>result[key] is JsonValue v && v.TryGetValue<string>(out var s)?s:throw new InvalidDataException("Invalid document field: "+key);
        bool Flag(string key)=>result[key] is JsonValue v && v.TryGetValue<bool>(out var b)?b:throw new InvalidDataException("Invalid document flag: "+key);
        foreach(var key in new[]{"ProjectFile","SoftwarePath","BlockPath","OutputDirectory"}) if(string.IsNullOrWhiteSpace(Text(key)) || Text(key).Length>4096) throw new InvalidDataException("Missing document identity.");
        if(Text("ReleaseKey") is not ("20" or "21") || Text("Language") is not ("LAD" or "DB") || Text("Options")!="native-default-two-argument-overload" || Text("Evidence")!="official-manual-source-candidate; exact-sdk-build/native-acceptance-pending; no-cross-version-roundtrip-claim") throw new InvalidDataException("Unverified document scope.");
        var hash=Text("PlanHash"); if(hash.Length!=64 || hash.Any(c=>!"0123456789abcdef".Contains(c))) throw new InvalidDataException("Invalid document hash.");
        var status=Text("Status");
        if(Flag("Executed")==dryRun || (dryRun?status is not ("planned" or "inconsistent"):status is not ("exported" or "failed")) || Flag("RequiresSessionReset")!=(status=="failed") || (status=="failed"?string.IsNullOrWhiteSpace(Text("RecoveryDirectory")):Text("RecoveryDirectory")!="")) throw new InvalidDataException("Document status conflict.");
        var name=Uri.UnescapeDataString(Text("BlockPath").Split('/').Last());
        if(result["Files"] is not JsonArray files || (files.Count!=2 && !(Text("ReleaseKey")=="21" && status=="exported" && files.Count==1)) || files[0]?.GetValue<string>()!=name+".s7dcl" || (files.Count==2 && files[1]?.GetValue<string>()!=name+".s7res")) throw new InvalidDataException("Document pair conflict.");
        return result;
    }
    internal static void ValidateRequest(JsonObject request,JsonNode? payload)
    {
        bool dry=request["dryRun"]?.GetValue<bool>()??true; var result=Validate(payload,dry);
        if(!TiaOpenness.Shared.NativeExportPolicy.ResolvedIdentityMatches(request["softwarePath"]!.GetValue<string>(),result["SoftwarePath"]!.GetValue<string>())) throw new InvalidDataException("Document software identity mismatch.");
        if(TiaOpenness.Shared.NativeInputPolicy.FullPath(request["exportPath"]!.GetValue<string>())!=TiaOpenness.Shared.NativeInputPolicy.FullPath(result["OutputDirectory"]!.GetValue<string>())) throw new InvalidDataException("Document output identity mismatch.");
        foreach(var pair in new[]{("BlockPath","blockPath")})
            if(result[pair.Item1]!.GetValue<string>()!=request[pair.Item2]?.GetValue<string>()) throw new InvalidDataException("Document result identity mismatch.");
        if(request["preservePath"]?.GetValue<bool>()==true || (!dry && (result["PlanHash"]!.GetValue<string>()!=request["expectedPlanHash"]?.GetValue<string>() || !string.Equals(Path.GetFullPath(result["ProjectFile"]!.GetValue<string>()),Path.GetFullPath(request["expectedProjectFile"]!.GetValue<string>()),StringComparison.OrdinalIgnoreCase)))) throw new InvalidDataException("Document result differs from reviewed request.");
    }
}
