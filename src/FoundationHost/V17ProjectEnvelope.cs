using ModelContextProtocol;
using System.Text.Json.Nodes;

namespace TiaMcp.FoundationHost;

internal static class V17ProjectEnvelope
{
    internal static JsonObject Wrap(string tool,string shape,JsonNode? result,bool dryRun)
    {
        string Wire(string name)=>McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(name)??name;
        JsonObject response;
        if(shape=="Projects") response=V17ReadEnvelope.Wrap(tool,"Items",result);
        else if(shape=="ProjectMutation" || shape=="Bind") response=V17MutationEnvelope.Wrap(tool,"Mutation",result,shape=="Bind" ? false : dryRun);
        else if(shape=="ProjectTree")
        {
            if(result is not JsonValue value || !value.TryGetValue<string>(out var tree) || string.IsNullOrWhiteSpace(tree)) throw new IOException("Project tree result is missing.");
            response=new JsonObject { [Wire("Message")]="Project tree retrieved",[Wire("Tree")]="```\n"+tree+"\n```",[Wire("Meta")]=new JsonObject { ["success"]=true } };
        }
        else
        {
            if(result is not JsonObject state || state["Stage"]?.GetValue<string>()!="attached" || state["OwnsPortal"]?.GetValue<bool>()!=false || state["AttemptedPids"] is not JsonArray pids || pids.Count!=1 || pids[0]!.GetValue<int>()<=0)
                throw new IOException("Explicit attachment outcome is missing or invalid.");
            response=new JsonObject { [Wire("Message")]="Attached to the selected existing TIA process",[Wire("Meta")]=new JsonObject { ["success"]=true,["ownsPortal"]=false } };
            foreach(var name in new[]{"Stage","Strategy","AttemptedPids","LaunchMode"}) response[Wire(name)]=state[name]?.DeepClone();
        }
        var meta=(JsonObject)response[Wire("Meta")]!;
        meta["contractVersion"]="v17-project-safe-v1"; meta["nativeAcceptance"]="unverified"; meta["serverCommit"]="never";
        meta.Remove("pathContract");
        return response;
    }
}
