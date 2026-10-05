using ModelContextProtocol;
using System.Text.Json.Nodes;

namespace TiaMcp.LegacyHost;

internal static class V17MutationEnvelope
{
    internal static JsonObject Wrap(string tool,string shape,JsonNode? payload,bool dryRun)
    {
        if(payload is not JsonObject result || result["Executed"] is not JsonValue flag || !flag.TryGetValue<bool>(out var executed) || executed==dryRun)
            throw new InvalidDataException("Mutation result does not match the requested preview/execution state.");
        if(result["ProjectFile"] is not JsonValue project || !project.TryGetValue<string>(out var projectFile) || string.IsNullOrWhiteSpace(projectFile))
            throw new InvalidDataException("Mutation result lacks exact project identity.");
        string Wire(string name)=>McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(name)??name;
        var meta=new JsonObject { ["timestamp"]=DateTimeOffset.UtcNow.ToString("O"),["success"]=true,["executed"]=executed,["dryRun"]=dryRun,["projectFile"]=projectFile,["contractVersion"]="v17-exchange-safe-v1",["nativeAcceptance"]="unverified" };
        foreach(var name in new[]{"OutputFile","InputFile","InputSha256","AffectedNames","RecoveryDirectory","XmlContent","FullProgramRestoreSupported","Warnings"}) if(result[name]!=null) meta[Wire(name)]=result[name]!.DeepClone();
        var response=new JsonObject { [Wire("Message")]=executed ? tool+" completed" : tool+" preview validated; no operation executed",[Wire("Meta")]=meta };
        if(shape=="ExportFile") response[Wire("ExportPath")]=result["OutputFile"]?.DeepClone() ?? throw new InvalidDataException("Export result lacks an output path.");
        return response;
    }
}
