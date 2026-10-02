using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.LegacyHost;

internal sealed class WorkerOperationException(string message,int code,string outcome) : Exception(message)
{
    internal int Code { get; }=code;
    internal bool KnownNoMutation { get; }=outcome is "rejected-before-operation" or "read-failed";
}
internal sealed class WorkerOutcomeState
{
    internal bool Poisoned { get; private set; }
    internal void RequireUsable()
    { if(Poisoned) throw new InvalidOperationException("Previous native request has an unknown outcome. Inspect TIA before a new explicit session; requests are never replayed."); }
    internal void Failed(bool sent,Exception error) { if(WorkerProtocol.RequiresSessionReset(sent,error)) Poisoned=true; }
}
internal static class WorkerProtocol
{
    internal static bool RequiresSessionReset(bool sent,Exception error) => sent && !(error is WorkerOperationException known && known.KnownNoMutation);
    internal static void ValidateExchangeResult(string operation,JsonObject arguments,JsonNode? result)
    {
        if(operation=="Attach")
        {
            V17ProjectEnvelope.Wrap("Connect","Connection",result,false);
            if(result!["AttemptedPids"]![0]!.GetValue<int>()!=arguments["processId"]!.GetValue<int>()) throw new IOException("Attached process differs from the explicit request; session stopped.");
            return;
        }
        if(operation is not ("BindProject" or "OpenProject" or "CreateProject" or "SaveProject" or "CloseProject" or "CompileSoftware" or "ExportBlock" or "ExportType" or "ExportTagTable" or "ImportBlocks" or "ImportTypes" or "ImportTagTables" or "CreateTagTable" or "CreateTag" or "CreateUserConstant")) return;
        var dryRun=arguments["dryRun"]?.GetValue<bool>() ?? operation!="BindProject";
        if(result is not JsonObject data || data["Executed"] is not JsonValue flag || !flag.TryGetValue<bool>(out var executed) || executed==dryRun)
            throw new IOException("Exchange worker returned a conflicting or missing execution outcome; session stopped.");
        if(data["ProjectFile"] is not JsonValue project || !project.TryGetValue<string>(out var projectFile) || string.IsNullOrWhiteSpace(projectFile))
            throw new IOException("Exchange worker returned no project identity; session stopped.");
        if(!dryRun && !string.Equals(Path.GetFullPath(arguments["expectedProjectFile"]!.GetValue<string>()),Path.GetFullPath(projectFile),StringComparison.OrdinalIgnoreCase))
            throw new IOException("Exchange worker result belongs to a different project; session stopped.");
        if(operation=="CompileSoftware") V17CompileEnvelope.Validate(data,dryRun);
    }
    internal static JsonNode? Decode(string response,long expectedId)
    {
        using var document=JsonDocument.Parse(response);
        var root=document.RootElement;
        if(root.ValueKind!=JsonValueKind.Object) throw new IOException("Malformed worker response.");
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var field in root.EnumerateObject())
            if(!names.Add(field.Name) || field.Name is not ("id" or "result" or "error")) throw new IOException("Duplicate or unknown worker response field.");
        if(!root.TryGetProperty("id",out var id) || !id.TryGetInt64(out var actualId) || actualId!=expectedId) throw new IOException("Worker response ID mismatch.");
        bool hasResult=root.TryGetProperty("result",out var result);
        bool hasError=root.TryGetProperty("error",out var error);
        if(hasResult==hasError) throw new IOException("Worker response must contain exactly one result or error.");
        if(hasError)
        {
            if(error.ValueKind!=JsonValueKind.Object || !error.TryGetProperty("message",out var message) || message.ValueKind!=JsonValueKind.String)
                throw new IOException("Malformed worker error.");
            if(!error.TryGetProperty("code",out var code) || !code.TryGetInt32(out var number) || number is not (-32602 or -32603) ||
                !error.TryGetProperty("outcome",out var outcome) || outcome.ValueKind!=JsonValueKind.String || outcome.GetString() is not ("rejected-before-operation" or "read-failed" or "unknown"))
                throw new IOException("Malformed worker failure classification.");
            var detail=message.GetString()!;
            if(error.TryGetProperty("evidence",out var evidence) && evidence.ValueKind==JsonValueKind.Object) detail+="; failure evidence: "+evidence.GetRawText();
            throw new WorkerOperationException(detail,number,outcome.GetString()!);
        }
        return JsonNode.Parse(result.GetRawText()); // Explicit null is a valid void result.
    }
}
