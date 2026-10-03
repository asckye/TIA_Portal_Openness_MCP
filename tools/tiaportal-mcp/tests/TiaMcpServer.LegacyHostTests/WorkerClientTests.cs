using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;

internal static class WorkerClientTests
{
    internal static async Task Run(Action<bool,string> Check)
    {
        using(var disabled=new WorkerClient("17","intentionally-nonexistent.exe","intentionally-nonexistent-api",false))
        {
            try { await disabled.Call("Attach",new JsonObject { ["processId"]=123 },CancellationToken.None); throw new Exception("Native default gate failed"); }
            catch(InvalidOperationException ex) { Check(ex.Message.StartsWith("Native calls are disabled"),"Discovery denies before any process/file access"); }
        }
        Check(WorkerProtocol.Decode("{\"id\":1,\"result\":null}",1)==null,"Explicit void result accepted");
        foreach(var malformed in new[]{"{\"id\":1}","{\"id\":1,\"error\":\"bad\"}","{\"id\":1,\"result\":null,\"error\":{\"message\":\"bad\"}}","{\"id\":2,\"result\":[]}","{\"id\":1,\"result\":[],\"result\":null}"})
        { try { WorkerProtocol.Decode(malformed,1); throw new Exception("Malformed response accepted"); } catch(IOException) { Check(true,"Malformed response rejected: "+malformed); } }
    }
}
