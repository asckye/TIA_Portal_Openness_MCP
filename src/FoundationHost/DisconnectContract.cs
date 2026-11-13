using System.Text.Json.Nodes;

namespace TiaMcp.FoundationHost;

internal static class DisconnectContract
{
    // The worker refuses before any native call when a portal it started still has a project open (P8-81c F1).
    internal static bool Refused(TiaMcp.WorkerChannel.ChannelFailureException failure)
        => failure.Outcome == TiaMcp.WorkerChannel.ChannelOutcome.RejectedBeforeNative
            && new WorkerOperationException(failure.Message, failure.Code, "rejected-before-operation", failure.EvidenceJson).ExceptionType
                == nameof(TiaMcp.Adapters.Contracts.AdapterPreconditionException);
    internal static TiaMcp.Adapters.Contracts.AdapterPreconditionException Refusal(TiaMcp.WorkerChannel.ChannelFailureException failure)
        => new(failure.Message, "session", false, failure);
    internal static JsonObject Idle() => new() {
        ["Stage"]="disconnected",["SessionState"]="disconnected",["Strategy"]="non-owning-attachment-only",
        ["WorkerAcknowledged"]=false,["Detached"]=false,["ProcessId"]=null,
        ["SavedProject"]=false,["ClosedProject"]=false,["LaunchMode"]="never"
    };
    internal static JsonObject Validate(JsonNode? result, bool? workerAcknowledged=null, int? expectedProcessId=null, bool checkProcessId=false)
    {
        if(result is not JsonObject data || data.Count != 9 ||
            Text(data,"Stage")!="disconnected" || Text(data,"SessionState")!="disconnected" ||
            !(Text(data,"Strategy")=="non-owning-attachment-only" && Text(data,"LaunchMode")=="never"
                || Text(data,"Strategy")=="worker-started-portal-dispose" && Text(data,"LaunchMode")=="worker-started") ||
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
