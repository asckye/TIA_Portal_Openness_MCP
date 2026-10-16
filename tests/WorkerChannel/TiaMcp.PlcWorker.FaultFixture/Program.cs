using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TiaMcp.PlcWorker;
using TiaMcp.WorkerChannel;

internal static class Program
{
    private static int Main(string[] args)
    {
        var options = WorkerLaunchOptions.Parse(args);
        if (options.TiaPortalLocation != null) Environment.SetEnvironmentVariable("TiaPortalLocation", options.TiaPortalLocation);
        AppDomain.CurrentDomain.UnhandledException += (_, error) => Console.Error.WriteLine(
            "PlcWorker background exception; terminating=" + error.IsTerminating + ": " + (error.ExceptionObject as Exception)?.Message);
        string exe = typeof(Program).Assembly.Location;
        string Hash(string path)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }
        var identity = new ChannelIdentity(options.ReleaseKey, Hash(exe),
            Hash(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + options.ReleaseKey + ".dll")),
            System.Diagnostics.Process.GetCurrentProcess().Id, options.Nonce);
        bool lost = false;
        new ChannelServer(Console.OpenStandardInput(), Console.OpenStandardOutput(), identity, () => new ChannelBinding(0, false), request => {
            if (lost) return ChannelResponse.Error(new ChannelFailureException("Explicit restart required.", -32603, ChannelOutcome.RejectedBeforeNative));
            var injected = WorkerFaultInjection.PlcBeforeDispatch(typeof(Program).Assembly, request, _ => lost = true);
            if (injected != null) return injected;
            return ChannelResponse.Success(JsonSerializer.Serialize(new { ReleaseKey = options.ReleaseKey, IsAttached = false,
                ProjectFile = (string?)null, WithUi = options.WithUi, TiaPortalLocation = Environment.GetEnvironmentVariable("TiaPortalLocation") }));
        }).Run();
        return 0;
    }
}
