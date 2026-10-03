using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Environment;
using TiaOpenness.Core.Rpc;

namespace TiaOpenness.Bridge
{
    /// <summary>
    /// The only process that ever touches Siemens.Engineering.
    ///
    /// It speaks newline-delimited JSON-RPC 2.0: requests on stdin, responses and
    /// <c>progress</c> notifications on stdout, diagnostics on stderr. Keeping it in its own
    /// .NET Framework 4.8 x64 process is what lets the desktop UI targets
    /// modern .NET, and lets a hung TIA Portal be killed without taking the UI with it.
    /// </summary>
    public static class Program
    {
        private static readonly object StdoutGate = new object();

        [STAThread]
        public static int Main(string[] args)
        {
            if (System.Environment.GetEnvironmentVariable("TIA_MCP_LOG_SWALLOWED") == "1")
                TiaMcp.Shared.SwallowedExceptions.Sink = message => Console.Error.WriteLine(message);
            var forceMock = HasFlag(args, "--mock");

            if (HasFlag(args, "--doctor"))
            {
                var report = OpennessDoctor.Run();
                Console.Out.Write(OpennessDoctor.Format(report));
                return report.CanRunOpenness ? 0 : 1;
            }

            // stdout carries protocol only; anything else would corrupt the stream.
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

            var opennessVersion = GetValue(args, "--openness-version");
            Func<ITiaSessionFactory> resolveFactory = () => SessionFactoryLoader.Resolve(forceMock, opennessVersion);

            Console.Error.WriteLine("[bridge] local native protocol ready; no TIA connection yet");

            using (var dispatcher = new RpcDispatcher(resolveFactory, n => WriteLine(stdout, n)))
            {
                string line;
                while ((line = stdin.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;

                    RpcRequest request;
                    try
                    {
                        request = JsonConvert.DeserializeObject<RpcRequest>(line, BridgeJson.Settings);
                    }
                    catch (Exception ex)
                    {
                        WriteLine(stdout, RpcResponse.Fail(null, RpcErrorCodes.ParseError, ex.Message));
                        continue;
                    }

                    if (request == null || string.IsNullOrWhiteSpace(request.Method))
                    {
                        WriteLine(stdout, RpcResponse.Fail(request?.Id, RpcErrorCodes.InvalidRequest,
                            "Request must carry a 'method'."));
                        continue;
                    }

                    var response = dispatcher.Handle(request);
                    WriteLine(stdout, response);

                    if (response.Error != null)
                    {
                        Console.Error.WriteLine("[bridge] " + request.Method + " -> " +
                                                response.Error.Code + " " + response.Error.Message);
                        Console.Error.Flush();
                    }
                }
            }

            return 0;
        }

        /// <summary>
        /// Writes one protocol frame. Serialised on a lock because progress notifications are
        /// raised from inside a call, interleaved with the response for that same call.
        /// </summary>
        private static void WriteLine(TextWriter stdout, object payload)
        {
            var json = JsonConvert.SerializeObject(payload, BridgeJson.Settings);
            lock (StdoutGate)
            {
                stdout.Write(json);
                stdout.Write('\n');
                stdout.Flush();
            }
        }

        private static bool HasFlag(string[] args, string flag)
        {
            foreach (var a in args)
            {
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Reads <c>--name value</c> from the command line.</summary>
        private static string GetValue(string[] args, string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }
    }
}
