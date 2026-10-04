using System;
using System.Diagnostics;
using System.Reflection;
using TiaMcp.WorkerChannel;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Environment;
using TiaOpenness.Core.Rpc;

namespace TiaOpenness.Bridge
{
    /// <summary>The native session and every dispatch stay on this process's STA main thread.</summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (System.Environment.GetEnvironmentVariable("TIA_MCP_LOG_SWALLOWED") == "1")
                TiaMcp.Shared.SwallowedExceptions.Sink = message => Console.Error.WriteLine(message);
            var forceMock = HasFlag(args, "--mock");
            OpennessLocator.PublicApiDirectory = GetValue(args, "--public-api");

            if (HasFlag(args, "--doctor"))
            {
                var report = OpennessDoctor.Run();
                Console.Out.Write(OpennessDoctor.Format(report));
                return report.CanRunOpenness ? 0 : 1;
            }

            try
            {
                var release = TiaMcp.Versioning.TiaVersionCatalog.FromApiVersion(GetValue(args, "--openness-version"));
                var adapterPath = BridgeChannel.AdapterPath(AppDomain.CurrentDomain.BaseDirectory, release.Key, forceMock);
                // Load the adapter before hello without constructing a session. Factory
                // configuration remains at its original first RPC dispatch on this thread.
                var adapter = Assembly.LoadFrom(adapterPath);
                var identity = new ChannelIdentity(release.Key, BridgeChannel.Hash(Assembly.GetExecutingAssembly().Location),
                    BridgeChannel.Hash(adapter.Location), Process.GetCurrentProcess().Id, GetValue(args, "--nonce"));
                Console.Error.WriteLine("[bridge] local native protocol ready; no TIA connection yet");
                using (var dispatcher = new BridgeChannelDispatcher(() => SessionFactoryLoader.Resolve(forceMock, release.ApiVersion)))
                {
                    var server = new ChannelServer(Console.OpenStandardInput(), Console.OpenStandardOutput(), identity,
                        dispatcher.Observe, request =>
                        {
                            var response = dispatcher.Handle(request);
                            if (response.Failure != null)
                            {
                                var error = BridgeJson.Deserialize<RpcError>(
                                    response.Failure.RpcErrorJson);
                                Console.Error.WriteLine("[bridge] " + request.Method + " -> " + error.Code + " " + error.Message);
                                Console.Error.Flush();
                            }
                            return response;
                        }, ChannelProfile.Studio);
                    server.Run();
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[bridge] " + ex.Message);
                return 1;
            }
        }

        private static bool HasFlag(string[] args, string flag)
        {
            foreach (var a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string GetValue(string[] args, string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }
}
