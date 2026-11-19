using ModelContextProtocol;
using System.Text.Json.Nodes;

namespace TiaMcp.FoundationHost;

internal static class V17ProjectEnvelope
{
    internal static JsonObject PreviousOwner(JsonNode state, JsonArray? requests)
        => new() { ["leaseState"] = state.DeepClone(), ["unsettledRequests"] = new JsonArray((requests ?? new JsonArray())
            .Select(entry => entry is JsonObject ? entry.DeepClone()
                : new JsonObject { ["requestId"] = entry!.DeepClone(), ["origin"] = "previous-owner" }).ToArray()) };

    internal static JsonObject Wrap(string tool,string shape,JsonNode? result,bool dryRun)
    {
        string Wire(string name)=>McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(name)??name;
        JsonObject response;
        if(shape=="Projects") response=V17ReadEnvelope.Wrap(tool,"Items",result);
        else if (shape == "ProjectMutation" && tool == "CloseProject" && SecondaryProjectAbsence.Reported(result))
        {
            string file = SecondaryProjectAbsence.ValidateResult(result);
            response = new JsonObject { [Wire("Message")] = "Secondary project confirmed absent; no operation executed",
                [Wire("Meta")] = TiaMcpServer.ModelContextProtocol.ResponseMeta.Unstamped(true,
                    ("timestamp", JsonValue.Create(DateTimeOffset.UtcNow.ToString("O"))), ("executed", JsonValue.Create(false)),
                    ("dryRun", JsonValue.Create(dryRun)), ("projectFile", JsonValue.Create(file)), (Wire("SecondaryProjectGone"), JsonValue.Create(true))) };
        }
        else if(shape=="ProjectMutation" || shape=="Bind") response=V17MutationEnvelope.Wrap(tool,"Mutation",result,shape=="Bind" ? false : dryRun);
        else if(shape=="ProjectTree")
        {
            if(result is not JsonValue value || !value.TryGetValue<string>(out var tree) || string.IsNullOrWhiteSpace(tree))
                throw new IOException("Project tree result is missing.");
            response=new JsonObject {
                [Wire("Message")]="Project tree retrieved",[Wire("Tree")]="```\n"+tree+"\n```",[Wire("Meta")]=new JsonObject { ["success"]=true } };
        }
        else
        {
            if(result is not JsonObject state || state["Stage"]?.GetValue<string>()!="attached" || state["OwnsPortal"]?.GetValue<bool>()!=false
                    || state["AttemptedPids"] is not JsonArray pids || pids.Count!=1 || pids[0]!.GetValue<int>()<=0)
                throw new IOException("Explicit attachment outcome is missing or invalid.");
            response=new JsonObject {
                [Wire("Message")]="Attached to the selected existing TIA process",[Wire("Meta")]=new JsonObject { ["success"]=true,["ownsPortal"]=false } };
            if (state["PreviousOwnerState"] is JsonValue previous)
                response["previousOwner"] = PreviousOwner(previous, state["PreviousOwnerRequests"] as JsonArray);
            foreach(var name in new[]{"Stage","Strategy","AttemptedPids","LaunchMode"}) response[Wire(name)]=state[name]?.DeepClone();
        }
        var meta=(JsonObject)response[Wire("Meta")]!;
        meta["contractVersion"]="v17-project-safe-v1"; meta["nativeAcceptance"]="unverified"; meta["serverCommit"]="never";
        meta.Remove("pathContract");
        return response;
    }
}

internal static class SecondaryProjectAbsence
{
    internal static bool Reported(JsonNode? result)
    {
        if (result is not JsonObject data || !data.ContainsKey("SecondaryProjectGone")) return false;
        if (data["SecondaryProjectGone"] is not JsonValue marker || !marker.TryGetValue<bool>(out var gone))
            throw new IOException("Secondary absence result has no boolean absence marker.");
        return gone;
    }
    internal static string ValidateResult(JsonNode? result)
    {
        if (!Reported(result) || result!["Executed"] is not JsonValue flag || !flag.TryGetValue<bool>(out var executed) || executed)
            throw new IOException("Secondary absence must report Executed=false.");
        return ExactFile(result["ProjectFile"]);
    }
    internal static void ValidateRequest(JsonObject arguments, JsonNode? result)
    {
        string file = ValidateResult(result);
        string target = ExactFile(arguments["secondaryProjectFile"]);
        if (!string.Equals(file, target, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Secondary absence belongs to a different project.");
        if ((bool?)arguments["dryRun"] == false && !string.Equals(file, ExactFile(arguments["expectedProjectFile"]), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Secondary absence differs from the execution target.");
    }
    private static string ExactFile(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var file) || string.IsNullOrWhiteSpace(file)
            || !Path.IsPathFullyQualified(file))
            throw new IOException("Secondary absence requires an exact absolute project file.");
        return Path.GetFullPath(file);
    }
}
