using System.Text.Json.Nodes;

namespace TiaMcp.LegacyHost;

internal static class DisconnectContract
{
    internal static JsonObject Recovery(JsonObject acknowledgement, bool requiresTiaRestart, string? priorUnknownRequestId)
    {
        acknowledgement["RequiresTiaRestart"] = requiresTiaRestart;
        acknowledgement["RecoveryMessage"] = requiresTiaRestart ? TiaOpenness.Shared.SessionBehavior.TiaRestartRequired : TiaOpenness.Shared.SessionBehavior.DetachedAfterUnknown;
        acknowledgement["PriorUnknownRequestId"] = priorUnknownRequestId;
        TiaMcpServer.ModelContextProtocol.InvocationJournal.Write(
            TiaOpenness.Shared.AuditInvocation.CurrentRequestId ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CorrelationId,
            "DisconnectPortal", "RECOVERY_DISCONNECT", details: new JsonObject {
                ["priorUnknownRequestId"] = priorUnknownRequestId, ["requiresTiaRestart"] = requiresTiaRestart,
                ["workerAcknowledged"] = acknowledgement["WorkerAcknowledged"]?.DeepClone() });
        return acknowledgement;
    }
    internal static JsonObject Idle() => new() {
        ["Stage"]="disconnected",["SessionState"]="terminal",["Strategy"]="non-owning-attachment-only",
        ["WorkerAcknowledged"]=false,["Detached"]=false,["ProcessId"]=null,
        ["SavedProject"]=false,["ClosedProject"]=false,["LaunchMode"]="never"
    };
    internal static JsonObject Validate(JsonNode? result, bool? workerAcknowledged=null, int? expectedProcessId=null, bool checkProcessId=false)
    {
        if(result is not JsonObject data || data.Count != (data.ContainsKey("RequiresTiaRestart") ? 12 : 9) ||
            Text(data,"Stage")!="disconnected" || Text(data,"SessionState")!="terminal" ||
            Text(data,"Strategy")!="non-owning-attachment-only" || Text(data,"LaunchMode")!="never" ||
            Flag(data,"SavedProject")!=false || Flag(data,"ClosedProject")!=false ||
            Flag(data,"WorkerAcknowledged") is not bool acknowledged || Flag(data,"Detached") is not bool detached ||
            (workerAcknowledged.HasValue && acknowledged!=workerAcknowledged.Value) || !data.ContainsKey("ProcessId"))
            throw new IOException("Missing or malformed Disconnect acknowledgement; outcome unknown, never retry.");
        if (data.ContainsKey("RequiresTiaRestart"))
        {
            bool? restart = Flag(data, "RequiresTiaRestart");
            if (restart == null || Text(data, "RecoveryMessage") != (restart.Value ? TiaOpenness.Shared.SessionBehavior.TiaRestartRequired : TiaOpenness.Shared.SessionBehavior.DetachedAfterUnknown)
                || !data.ContainsKey("PriorUnknownRequestId") || data["PriorUnknownRequestId"] != null && (Text(data, "PriorUnknownRequestId") is not string prior || prior.Length > 128)
                || restart == true && (acknowledged || detached))
                throw new IOException("Malformed recovery Disconnect acknowledgement.");
        }
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
