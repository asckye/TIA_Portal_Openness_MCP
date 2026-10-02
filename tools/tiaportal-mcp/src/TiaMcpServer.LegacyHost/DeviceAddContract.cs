using System.Text.Json.Nodes;
namespace TiaMcp.LegacyHost;
internal static class DeviceAddContract
{
    internal static JsonObject Validate(JsonNode? payload,JsonObject request)
    {
        if(payload is not JsonObject r) throw new InvalidDataException("Missing device creation result.");
        var fields=new[]{"Status","Attempted","Executed","RequiresSessionReset","Error","Release","ProjectFile","ProcessId","DeviceName","TypeIdentifier","ArticleNumber","Version","Family","PlanHash","Inventory","Policy","Compatibility","Save","Download","Recovery"};
        if(r.Count!=fields.Length || r.Any(x=>!fields.Contains(x.Key,StringComparer.Ordinal)))throw new InvalidDataException("Unknown/missing device creation fields.");
        string Text(string k)=>r[k] is JsonValue v&&v.TryGetValue<string>(out var s)?s:throw new InvalidDataException("Missing creation text: "+k);
        bool Flag(string k)=>r[k] is JsonValue v&&v.TryGetValue<bool>(out var b)?b:throw new InvalidDataException("Missing creation flag: "+k);
        foreach(var field in r) if(field.Value is JsonValue value && value.TryGetValue<string>(out var text) && text.Length>4096) throw new InvalidDataException("Oversized creation field.");
        if(Text("DeviceName").Length>128 || Text("TypeIdentifier").Length>512 || Text("ArticleNumber").Length>256 || Text("Version").Length>64 || Text("Error").Length>2048)throw new InvalidDataException("Creation scalar bound exceeded.");
        TiaMcp.PlcFoundation.MutationIdentityPolicy.AbsoluteFile(Text("ProjectFile"));
        bool dry=request["dryRun"]?.GetValue<bool>()??true;
        if(Text("Release") is not ("19" or "20" or "21") || r["ProcessId"] is not JsonValue p || !p.TryGetValue<int>(out var pid)||pid<=0 || string.IsNullOrWhiteSpace(Text("ProjectFile")))throw new InvalidDataException("Invalid creation session.");
        if(Text("DeviceName")!=request["deviceName"]?.GetValue<string>() || Text("Family")!=(request["family"]?.GetValue<string>()??"S7-1500"))throw new InvalidDataException("Creation target mismatch.");
        var mlfb=request["preferredMlfb"]?.GetValue<string>()??"";var version=request["preferredVersion"]?.GetValue<string>()??"";
        if(!Text("TypeIdentifier").StartsWith("OrderNumber:",StringComparison.Ordinal) || string.IsNullOrWhiteSpace(Text("ArticleNumber")) || string.IsNullOrWhiteSpace(Text("Version")) || (mlfb.StartsWith("OrderNumber:",StringComparison.Ordinal)?Text("TypeIdentifier")!=mlfb || version!="":Text("ArticleNumber")!=mlfb || Text("Version")!=version))throw new InvalidDataException("Unreviewed catalog substitution.");
        var admitted=Text("Family")=="S7-1200" ? new[]{"6ES7211-1BE40-0XB0","6ES7 211-1BE40-0XB0"} : Text("Family")=="S7-1500" ? new[]{"6ES7513-1AM03-0AB0","6ES7 513-1AM03-0AB0"} : Array.Empty<string>();
        if(!admitted.Contains(Text("ArticleNumber"),StringComparer.Ordinal) || Text("TypeIdentifier")!="OrderNumber:"+Text("ArticleNumber")+"/"+Text("Version") || Text("Version").Any(c=>c=='*'||c=='?'||c=='/'||char.IsControl(c)))throw new InvalidDataException("Catalog identity is outside the bounded PLC admission.");
        var hash=Text("PlanHash");if(hash.Length!=64 || hash.Any(c=>!"0123456789abcdef".Contains(c)))throw new InvalidDataException("Missing creation plan hash.");
        if(!dry && (request["confirm"]?.GetValue<bool>()!=true || hash!=request["expectedPlanHash"]?.GetValue<string>()))throw new InvalidDataException("Creation review mismatch.");
        if(!dry) TiaMcp.PlcFoundation.MutationIdentityPolicy.RequireSameProject(request["expectedProjectFile"]?.GetValue<string>()??"",Text("ProjectFile"));
        if(r["Inventory"] is not JsonArray a || a.Count>4096 || a.Any(x=>x is not JsonValue v||!v.TryGetValue<string>(out var n)||string.IsNullOrWhiteSpace(n)||n.Length>128))throw new InvalidDataException("Incomplete creation inventory.");
        var names=a.Select(x=>x!.GetValue<string>()).ToArray();if(names.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=names.Length || names.Contains(Text("DeviceName"),StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("Creation name collision.");
        switch(Text("Status"))
        {
            case "planned":if(!dry||Flag("Attempted")||Flag("Executed")||Flag("RequiresSessionReset")||Text("Error")!="")throw new InvalidDataException("Conflicting creation preview.");break;
            case "created-verified":if(dry||!Flag("Attempted")||!Flag("Executed")||Flag("RequiresSessionReset")||Text("Error")!="")throw new InvalidDataException("Unverified creation success.");break;
            case "outcome-unknown":if(dry||!Flag("Attempted")||Flag("Executed")||!Flag("RequiresSessionReset")||string.IsNullOrWhiteSpace(Text("Error")))throw new InvalidDataException("Unknown creation must poison session.");break;
            default:throw new InvalidDataException("Unknown creation status.");
        }
        if(Text("Policy")!="exact-catalog-root-device-add-v1" || Text("Compatibility")!="partial-exact-selection-only-no-fallback-probes" || Text("Save")!="notRun" || Text("Download")!="notRun" || Text("Recovery")!="notEstablished-no-automatic-backup-or-rollback")throw new InvalidDataException("Creation scope/recovery mismatch.");
        return r;
    }
}
