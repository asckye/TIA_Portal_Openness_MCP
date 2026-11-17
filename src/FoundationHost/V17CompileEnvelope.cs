using ModelContextProtocol;
using System.Text.Json.Nodes;

namespace TiaMcp.FoundationHost;

internal static class V17CompileEnvelope
{
    internal static JsonObject Validate(JsonNode? payload,bool dryRun,JsonObject? arguments=null)
    {
        if(payload is not JsonObject result || result["Executed"]?.GetValue<bool>()!=!dryRun)
            throw new IOException("Compiler execution outcome is missing or conflicts with preview.");
        foreach(var name in new[]{"Messages","Errors","Warnings","Info"})
            if(result[name] is not JsonArray lines || lines.Any(n=>n is not JsonValue value || !value.TryGetValue<string>(out _)))
                throw new IOException("Compiler diagnostic arrays are incomplete.");
        if(dryRun)
        {
            if(result["State"]!=null || result["ErrorCount"]!=null || result["WarningCount"]!=null)
                throw new IOException("Preview must not fabricate compiler state/counts.");
        }
        else if(result["State"] is not JsonValue state || !state.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) ||
            result["ErrorCount"] is not JsonValue errors || !errors.TryGetValue<int>(out var ec) || ec<0 ||
            result["WarningCount"] is not JsonValue warnings || !warnings.TryGetValue<int>(out var wc) || wc<0)
            throw new IOException("Actual compiler state/counts are missing or invalid.");
        string scope=arguments?["scope"]?.GetValue<string>() ?? "software";
        if(scope!="software")
        {
            string path=TiaMcp.Adapters.Contracts.ObjectPathEncoding.NormalizePath(arguments!["scopePath"]!.GetValue<string>());
            string unit=arguments["unitName"]?.GetValue<string>() ?? "";
            string suffix=scope=="unit" ? "/units/"+path
                : (unit=="" ? "" : "/units/"+TiaMcp.Adapters.Contracts.ObjectPathEncoding.EncodeSegment(unit))+"/blocks/"+path;
            if(result["Scope"]?.GetValue<string>()!=scope || result["ScopePath"]?.GetValue<string>()!=path ||
                result["UnitName"]?.GetValue<string>()!=unit || result["ResolvedObject"] is not JsonValue identity ||
                !identity.TryGetValue<string>(out var resolved) || string.IsNullOrWhiteSpace(resolved) ||
                !resolved.EndsWith(suffix,StringComparison.Ordinal) || resolved.Length<=suffix.Length ||
                result["Compilable"] is not JsonValue service || !service.TryGetValue<bool>(out var compilable) ||
                result["ApplyBlocked"]?.GetValue<bool>()!=!compilable || (!dryRun && !compilable))
                throw new IOException("Compiler scope, exact target or service availability conflicts with the request.");
        }
        else if((result.ContainsKey("Scope") && result["Scope"]?.GetValue<string>()!="software") ||
            (result.ContainsKey("ScopePath") && result["ScopePath"]?.GetValue<string>()!="") ||
            (result.ContainsKey("UnitName") && result["UnitName"]?.GetValue<string>()!=""))
            throw new IOException("Software compiler response unexpectedly contains a scoped target.");
        return result;
    }
    internal static JsonObject Wrap(string tool,JsonNode? payload,bool dryRun,JsonObject? arguments=null)
    {
        var result=Validate(payload,dryRun,arguments);
        var envelope=V17MutationEnvelope.Wrap(tool,"Mutation",result,dryRun);
        string Wire(string name)=>McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(name)??name;
        var meta=(JsonObject)envelope[Wire("Meta")]!;
        meta["contractVersion"]="v17-compile-safe-v1";
        meta["success"]=dryRun
            || (result["ErrorCount"]!.GetValue<int>()==0 && !string.Equals(result["State"]!.GetValue<string>(),"Error",StringComparison.OrdinalIgnoreCase));
        meta["errorDetailCount"]=((JsonArray)result["Errors"]!).Count;
        meta["warningDetailCount"]=((JsonArray)result["Warnings"]!).Count;
        meta["offlineCheckScope"]=
            "All project OnlineProvider/RHOnlineProvider states must be Offline; the selected PLC requires a provider. Devices without an exposed state are " +
            "listed, not asserted Offline. Siemens requires all devices offline before compilation.";
        meta["offlineStateNotExposedByDevices"]=result["OfflineStateNotExposedByDevices"]?.DeepClone() ?? new JsonArray();
        foreach(var name in new[]{"State","ErrorCount","WarningCount"}) envelope[Wire(name)]=result[name]?.DeepClone();
        if(result["Scope"]?.GetValue<string>() is string returnedScope && returnedScope!="software")
            foreach(var name in new[]{"Scope","ScopePath","UnitName","ResolvedObject","Compilable","ApplyBlocked"})
                envelope[Wire(name)]=result[name]!.DeepClone();
        if(tool=="CompilePlcSoftware") envelope[Wire("Messages")]=result["Messages"]!.DeepClone();
        else
        {
            foreach(var name in new[]{"Errors","Warnings","Info"}) envelope[Wire(name)]=result[name]!.DeepClone();
            envelope[Wire("RawMessages")]=result["Messages"]!.DeepClone();
        }
        return envelope;
    }
}
