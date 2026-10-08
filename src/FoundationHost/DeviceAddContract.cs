using System.Text.Json.Nodes;
namespace TiaMcp.FoundationHost;
internal static class DeviceAddContract
{
    internal static TiaMcp.Logic.V4.Envelope ValidateCandidate(JsonNode? payload, JsonObject request)
    {
        if (payload == null) throw new InvalidDataException("Missing device candidate result.");
        var result = TiaMcp.Logic.V4.V4Json.Deserialize<TiaMcp.Logic.V4.Envelope>(payload.ToJsonString());
        if (result.Meta.ReleaseKey != "19" || result.Meta.Tool != "CreateHardwareDevice" || result.Meta.RequestId != (string?)request["requestId"]
            || result.Meta.BehaviorPolicy != TiaMcp.Logic.V4.BehaviorPolicy.SafeV4) throw new InvalidDataException("Candidate response identity mismatch.");
        bool apply = (string?)request["mode"] == "apply";
        if (!apply && result.Meta.Execution is not (TiaMcp.Logic.V4.Execution.ReadOnly or TiaMcp.Logic.V4.Execution.NotStarted)) throw new InvalidDataException("Candidate preview wrote to the project.");
        if (result.Ok)
        {
            var data = JsonNode.Parse(result.Data!.Value.GetRawText())!.AsObject();
            string article = (string?)data["catalogEntry"]?["articleNumber"] ?? "";
            string version = (string?)data["catalogEntry"]?["version"] ?? "";
            string family = (string?)request["family"] ?? "";
            bool allowed = family == "S7-1200" ? article is "6ES7211-1BE40-0XB0" or "6ES7 211-1BE40-0XB0"
                : family == "S7-1500" && article is "6ES7513-1AM03-0AB0" or "6ES7 513-1AM03-0AB0";
            if (!allowed || version.Length == 0 || (string?)request["typeIdentifier"] != "OrderNumber:" + article + "/" + version
                || (string?)data["capabilityScope"]?["host"] != "foundation" || (string?)data["capabilityScope"]?["family"] != family)
                throw new InvalidDataException("Candidate model is outside Foundation capability.");
            if (data["createIssued"]?.GetValue<bool>() != apply
                || result.Meta.Execution != (apply ? TiaMcp.Logic.V4.Execution.Completed : TiaMcp.Logic.V4.Execution.ReadOnly)
                || apply && ((string?)data["created"]?["name"] != (string?)request["deviceName"] || (string?)data["residueCheck"]?["status"] != "checked"))
                throw new InvalidDataException("Candidate success did not verify the planned operation.");
            var plan = TiaMcp.Logic.V4.V4Json.Deserialize<TiaMcp.Logic.V4.Plan>(data["plan"]!.ToJsonString());
            var arguments = plan.Operations.Single().Arguments;
            if (plan.ReleaseKey != "19" || plan.Tool != "CreateHardwareDevice" || plan.Operations[0].Tool != plan.Tool
                || arguments.GetProperty("typeIdentifier").GetString() != (string?)request["typeIdentifier"]
                || arguments.GetProperty("deviceName").GetString() != (string?)request["deviceName"]
                || arguments.GetProperty("family").GetString() != (string?)request["family"]
                || (string?)data["catalogEntry"]?["typeIdentifier"] != (string?)request["typeIdentifier"])
                throw new InvalidDataException("Candidate plan target mismatch.");
            if (apply && (request["confirm"]?.GetValue<bool>() != true || plan.Hash != (string?)request["expectedPlanHash"]
                || TiaMcp.Logic.V4.DeviceCreationSession.CanonicalProject(plan.Identity.ProjectFile!) != TiaMcp.Logic.V4.DeviceCreationSession.CanonicalProject((string)request["expectedProjectFile"]!)))
                throw new InvalidDataException("Candidate apply confirmation mismatch.");
        }
        return result;
    }
    internal static JsonObject Validate(JsonNode? payload,JsonObject request)
    {
        if(payload is not JsonObject r) throw new InvalidDataException("Missing device creation result.");
        var fields=new[]{"Status","Attempted","Executed","RequiresSessionReset","Error","Release","ProjectFile","ProcessId","DeviceName","TypeIdentifier","ArticleNumber","Version","Family","PlanHash","Inventory","Policy","Compatibility","Save","Download","Recovery"};
        if(r.Count!=fields.Length || r.Any(x=>!fields.Contains(x.Key,StringComparer.Ordinal)))throw new InvalidDataException("Unknown/missing device creation fields.");
        string Text(string k)=>r[k] is JsonValue v&&v.TryGetValue<string>(out var s)?s:throw new InvalidDataException("Missing creation text: "+k);
        bool Flag(string k)=>r[k] is JsonValue v&&v.TryGetValue<bool>(out var b)?b:throw new InvalidDataException("Missing creation flag: "+k);
        foreach(var field in r) if(field.Value is JsonValue value && value.TryGetValue<string>(out var text) && text.Length>4096) throw new InvalidDataException("Oversized creation field.");
        if(Text("DeviceName").Length>128 || Text("TypeIdentifier").Length>512 || Text("ArticleNumber").Length>256 || Text("Version").Length>64 || Text("Error").Length>2048)throw new InvalidDataException("Creation scalar bound exceeded.");
        TiaMcp.Adapters.MutationIdentityPolicy.AbsoluteFile(Text("ProjectFile"));
        bool dry=request["dryRun"]?.GetValue<bool>()??true;
        if(Text("Release") is not ("19" or "20" or "21") || r["ProcessId"] is not JsonValue p || !p.TryGetValue<int>(out var pid)||pid<=0 || string.IsNullOrWhiteSpace(Text("ProjectFile")))throw new InvalidDataException("Invalid creation session.");
        if(Text("DeviceName")!=request["deviceName"]?.GetValue<string>() || Text("Family")!=(request["family"]?.GetValue<string>()??"S7-1500"))throw new InvalidDataException("Creation target mismatch.");
        var mlfb=request["preferredMlfb"]?.GetValue<string>()??"";var version=request["preferredVersion"]?.GetValue<string>()??"";
        if(!Text("TypeIdentifier").StartsWith("OrderNumber:",StringComparison.Ordinal) || string.IsNullOrWhiteSpace(Text("ArticleNumber")) || string.IsNullOrWhiteSpace(Text("Version")) || (mlfb.StartsWith("OrderNumber:",StringComparison.Ordinal)?Text("TypeIdentifier")!=mlfb || version!="":Text("ArticleNumber")!=mlfb || Text("Version")!=version))throw new InvalidDataException("Unreviewed catalog substitution.");
        var admitted=Text("Family")=="S7-1200" ? new[]{"6ES7211-1BE40-0XB0","6ES7 211-1BE40-0XB0"} : Text("Family")=="S7-1500" ? new[]{"6ES7513-1AM03-0AB0","6ES7 513-1AM03-0AB0"} : Array.Empty<string>();
        if(!admitted.Contains(Text("ArticleNumber"),StringComparer.Ordinal) || Text("TypeIdentifier")!="OrderNumber:"+Text("ArticleNumber")+"/"+Text("Version") || Text("Version").Any(c=>c=='*'||c=='?'||c=='/'||char.IsControl(c)))throw new InvalidDataException("Catalog identity is outside the bounded PLC admission.");
        var hash=Text("PlanHash");if(hash.Length!=64 || hash.Any(c=>!"0123456789abcdef".Contains(c)))throw new InvalidDataException("Missing creation plan hash.");
        if(!dry && (request["confirm"]?.GetValue<bool>()!=true || hash!=request["expectedPlanHash"]?.GetValue<string>()))throw new InvalidDataException("Creation review mismatch.");
        if(!dry) TiaMcp.Adapters.MutationIdentityPolicy.RequireSameProject(request["expectedProjectFile"]?.GetValue<string>()??"",Text("ProjectFile"));
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
