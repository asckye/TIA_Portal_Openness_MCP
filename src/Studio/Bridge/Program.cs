using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using TiaMcp.WorkerChannel;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Environment;
using TiaOpenness.Core.Rpc;

namespace TiaOpenness.Bridge
{
    /// <summary>
    /// The native session and every dispatch stay on one owner thread: the STA main thread for V14 SP1-V19, and a dedicated
    /// MTA thread for V20/V21, matching the PlcWorker (API audit 2026-10-19, F3).
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (!MtaRelease(args)) return Run(args);
            int result = 1;
            Exception failure = null;
            var owner = new System.Threading.Thread(() =>
            {
                try { result = Run(args); }
                catch (Exception ex) { failure = ex; }
            });
            owner.SetApartmentState(System.Threading.ApartmentState.MTA);
            owner.Start();
            owner.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }

        // Only an explicitly selected V20/V21 Openness version moves to MTA; anything else keeps the STA main thread.
        internal static bool MtaRelease(string[] args)
        {
            try
            {
                var key = TiaMcp.Versioning.TiaVersionCatalog.FromApiVersion(GetValue(args, "--openness-version")).Key;
                return key == "20" || key == "21";
            }
            catch (Exception) /* swallow(parse-fallback): an unknown or missing version keeps the STA thread; Run reports it */ { return false; }
        }

        private static int Run(string[] args)
        {
            // Redirected diagnostics must match BridgeClient's UTF-8 readers even
            // when the Windows console code page is GBK. Never emit a BOM on stdout.
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.SetError(new System.IO.StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
            if (System.Environment.GetEnvironmentVariable("TIA_MCP_LOG_SWALLOWED") == "1")
                TiaMcp.Shared.SwallowedExceptions.Sink = message => Console.Error.WriteLine(message);
            try { TiaOpenness.Shared.BundleLayout.InitializeWorkbench(AppDomain.CurrentDomain.BaseDirectory, args, out args); }
            catch (Exception ex) { Console.Error.WriteLine("[bridge] " + ex.Message); return ex is ArgumentException ? 64 : 70; }
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
