using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace TiaMcp.WorkerChannel
{
    public static class WorkerFaultInjection
    {
        // Environment switches are inert without metadata emitted by a test build.
        public static string? Mode(Assembly assembly)
            => assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(a =>
                (a.Key == "TiaMcpEngineWorkerFaultFixture" || a.Key == "TiaMcpEngineWorkerSdkFixture") && a.Value == "true")
                ? Environment.GetEnvironmentVariable("TIA_MCP_ENGINE_WORKER_FAULT") : null;
        public static string? PlcMode(Assembly assembly)
            => Metadata(assembly, "TiaMcpPlcWorkerSdkFixture") == "true"
                ? Environment.GetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT") : null;

        public static bool PlcSdkReady(Func<string, string?> metadata)
            => Environment.GetEnvironmentVariable("TIA_MCP_PLC_WORKER_SDK_READY") == "1"
                && metadata("TiaMcpPlcWorkerSdkFixture") == "true";

        private static string? Metadata(Assembly assembly, string key)
            => assembly.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a => a.Key == key)?.Value;

        public static ChannelResponse? PlcBeforeDispatch(Assembly assembly, ChannelRequest request, Action<Exception> processLost)
        {
            string? mode = PlcMode(assembly);
            BeforeDispatch(mode, request);
            if (mode != "tia-lost" && mode != "background-tia-exit") return null;
            var lost = new FixtureNonRecoverableException();
            processLost(lost);
            if (mode == "background-tia-exit") BackgroundExceptionAfterTiaExit();
            return ChannelResponse.Error(new ChannelFailureException(lost.Message, -32603, ChannelOutcome.Unknown,
                "{\"exceptionType\":\"PortalProcessLost\"}"));
        }

        private sealed class FixtureNonRecoverableException : Exception
        {
            internal FixtureNonRecoverableException() : base("Fixture TIA process lost.") { }
        }

        public static void BackgroundExceptionAfterTiaExit()
        {
            // Deliberately unhandled on a CLR background thread: the executable's
            // generated config, rather than a catch in the fixture, must keep it alive.
            Console.Error.WriteLine("Fixture TIA process exited.");
            var background = new Thread(() => throw new InvalidOperationException("Fixture Openness background exception after TIA exit."))
                { IsBackground = true };
            background.Start();
            background.Join();
        }
        public static void BeforeDispatch(string? mode, ChannelRequest request)
        {
            if (mode == "hang") Thread.Sleep(Timeout.Infinite);
            if (mode == "crash") Environment.Exit(72);
            if (mode == "broken-pipe") { Console.OpenStandardOutput().Dispose(); Environment.Exit(73); }
            string? corrupt = mode == "malformed" ? "{invalid}\n" : mode == "truncated" ? "{\"jsonrpc\":\"2.0\"" :
                mode == "epoch" ? "{\"jsonrpc\":\"2.0\",\"id\":" + request.Id + ",\"bindingEpochBefore\":999,\"bindingEpochAfter\":999,\"result\":{}}\n" : null;
            if (corrupt != null)
            {
                var bytes = Encoding.UTF8.GetBytes(corrupt);
                var stream = Console.OpenStandardOutput(); stream.Write(bytes, 0, bytes.Length); stream.Flush();
                if (mode == "truncated") Environment.Exit(74);
                Thread.Sleep(Timeout.Infinite);
            }
        }
    }
}
