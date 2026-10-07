using System.Text.Json.Nodes;

namespace TiaMcp.LegacyHost;

internal static class SpecialExportContract
{
    internal static JsonObject Validate(JsonNode? payload,bool dryRun)
    {
        if(payload is not JsonObject result) throw new InvalidDataException("Missing special export result.");
        if(result.Any(p=>p.Key is not ("Executed" or "ProjectFile" or "SoftwarePath" or "ObjectPath" or "OutputFile" or "ReleaseKey" or "Kind" or "Scope" or "PlanHash" or "ExportOptions" or "MethodEvidence" or "SemanticsEvidence" or "Status" or "RequiresSessionReset" or "Evidence"))) throw new InvalidDataException("Unexpected special export field.");
        string Text(string key)=>result[key] is JsonValue v && v.TryGetValue<string>(out var text)?text:throw new InvalidDataException("Missing special export field: "+key);
        bool Flag(string key)=>result[key] is JsonValue v && v.TryGetValue<bool>(out var flag)?flag:throw new InvalidDataException("Missing special export flag: "+key);
        if(Flag("Executed")==dryRun) throw new InvalidDataException("Special export execution conflict.");
        foreach(var key in new[]{"ProjectFile","SoftwarePath","ObjectPath","OutputFile","ReleaseKey","Scope"}) if(string.IsNullOrWhiteSpace(Text(key))) throw new InvalidDataException("Empty special export identity.");
        var kind=Text("Kind"); var release=Text("ReleaseKey"); var status=Text("Status"); var evidence=Text("MethodEvidence");
        if(kind is not ("watch-table" or "technology-object") || Text("ExportOptions")!="None" ||
            release is not ("14sp1" or "15.1" or "16" or "17" or "18" or "19" or "20" or "21") || (kind=="watch-table" && release=="14sp1")) throw new InvalidDataException("Invalid special export scope.");
        if(Text("Scope")!=(kind=="watch-table"?SupplementaryReadContract.WatchScope:release is "19" or "20" or "21"?SupplementaryReadContract.TechnologyScope:SupplementaryReadContract.TechnologyRootScope)) throw new InvalidDataException("Unexpected special export read scope.");
        bool documented=release is "16" or "17" or "18" or "19" or "20" || (release=="21" && kind=="watch-table");
        if(evidence!="static-sdk-signature-verified" || Text("SemanticsEvidence")!=(documented?"official-manual-source-candidate":"unverified") ||
            (dryRun ? documented ? status is not ("planned" or "inconsistent") : status!="semantics-unverified" : !documented || status is not ("exported" or "failed")) ||
            Flag("RequiresSessionReset")!=(status=="failed")) throw new InvalidDataException("Special export status/evidence conflict.");
        var hash=Text("PlanHash");
        if(hash.Length!=64 || hash.Any(c=>!"0123456789abcdef".Contains(c))) throw new InvalidDataException("Invalid special export plan hash.");
        if(result["Evidence"] is not JsonObject recovery || (dryRun && recovery.Count!=0)) throw new InvalidDataException("Invalid special export recovery evidence.");
        foreach(var pair in recovery) if(pair.Key is not ("outputFile" or "stagedFile" or "recoveryDirectory" or "exportPhase" or "stagedSha256") || pair.Value is not JsonValue v || !v.TryGetValue<string>(out _)) throw new InvalidDataException("Unexpected special export evidence.");
        return result;
    }
    internal static void ValidateRequest(string operation,JsonObject request,JsonNode? payload)
    {
        bool dryRun=request["dryRun"]?.GetValue<bool>()??true;
        var result=Validate(payload,dryRun);
        string Requested(string key)=>request[key]?.GetValue<string>()??throw new InvalidDataException("Missing special export request identity.");
        string Actual(string key)=>result[key]!.GetValue<string>();
        var kind=operation=="ExportPlcWatchTable"?"watch-table":operation=="ExportTechnologyObject"?"technology-object":throw new InvalidDataException("Invalid special export operation.");
        if(Actual("Kind")!=kind || !TiaOpenness.Shared.NativeExportPolicy.ResolvedIdentityMatches(Requested("softwarePath"),Actual("SoftwarePath")) || Actual("ObjectPath")!=Requested(kind=="watch-table"?"watchTableName":"toName") ||
            !string.Equals(Actual("OutputFile"),Path.GetFullPath(Requested("exportPath")),StringComparison.OrdinalIgnoreCase) ||
            (!dryRun && (Actual("PlanHash")!=Requested("expectedPlanHash") || !string.Equals(Path.GetFullPath(Actual("ProjectFile")),Path.GetFullPath(Requested("expectedProjectFile")),StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Special export outcome conflicts with the exact request; session stopped.");
    }
}
