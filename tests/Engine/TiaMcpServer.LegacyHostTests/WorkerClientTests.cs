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
        using(var bundled=new WorkerClient("17","intentionally-nonexistent.exe","intentionally-nonexistent-api",false))
        using(var fixture=new WorkerClient("17","intentionally-nonexistent.exe","intentionally-nonexistent-api",false) { Bundled=false })
        {
            var gated=FoundationTools.Create(bundled,"17").OfType<FoundationTool>().Where(t=>t.RequiresTia).ToArray();
            Check(gated.Length>0 && gated.All(t=>t.UsesProductionWorker),"Bundled worker tools keep the pre-dispatch readiness gate");
            Check(!FoundationTools.Create(fixture,"17").OfType<FoundationTool>().Any(t=>t.UsesProductionWorker),"An explicit --worker-exe fixture skips the bundled-worker readiness gate");
        }
    }
}
