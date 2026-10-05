using System.Text.Json.Nodes;

namespace TiaMcp.LegacyHost;

internal static class DisconnectContract
{
    internal static JsonObject Idle() => new() {
        ["Stage"]="disconnected",["SessionState"]="terminal",["Strategy"]="non-owning-attachment-only",
        ["WorkerAcknowledged"]=false,["Detached"]=false,["ProcessId"]=null,
        ["SavedProject"]=false,["ClosedProject"]=false,["LaunchMode"]="never"
    };
    internal static JsonObject Validate(JsonNode? result, bool? workerAcknowledged=null, int? expectedProcessId=null, bool checkProcessId=false)
    {
        if(result is not JsonObject data || data.Count!=9 ||
            Text(data,"Stage")!="disconnected" || Text(data,"SessionState")!="terminal" ||
            Text(data,"Strategy")!="non-owning-attachment-only" || Text(data,"LaunchMode")!="never" ||
            Flag(data,"SavedProject")!=false || Flag(data,"ClosedProject")!=false ||
            Flag(data,"WorkerAcknowledged") is not bool acknowledged || Flag(data,"Detached") is not bool detached ||
            (workerAcknowledged.HasValue && acknowledged!=workerAcknowledged.Value) || !data.ContainsKey("ProcessId"))
            throw new IOException("Missing or malformed Disconnect acknowledgement; outcome unknown, never retry.");
        int? pid=null;
        if(data["ProcessId"]!=null)
        {
            if(data["ProcessId"] is not JsonValue value || !value.TryGetValue<int>(out var id) || id<=0)
                throw new IOException("Invalid Disconnect process identity.");
            pid=id;
        }
        if(detached!=pid.HasValue || (!acknowledged && detached) || (checkProcessId && pid!=expectedProcessId))
            throw new IOException("Disconnect acknowledgement conflicts with the selected session.");
        return data;
    }
    private static string? Text(JsonObject data,string key) => data[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool? Flag(JsonObject data,string key) => data[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
