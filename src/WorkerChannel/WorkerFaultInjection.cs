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
