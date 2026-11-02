using System.Text;
using TiaMcp.FoundationHost;
using HostOptions = TiaMcp.FoundationHost.HostOptions;

using var stabilityTelemetry = LocalStabilityTelemetry.Start();
LocalStabilityTelemetry.StartupStage("program.begin");
HostOptions options;
Console.OutputEncoding = new UTF8Encoding(false);
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
try
{
    HostOptions.RejectEngineOptions(args, AppContext.BaseDirectory);
    var root = TiaOpenness.Shared.BundleLayout.ExtractRootOption(args, out args);
    TiaOpenness.Shared.BundleLayout.Initialize(AppContext.BaseDirectory, root);
}
catch (TiaOpenness.Shared.BundleResourceUnavailableException ex) { Console.Error.WriteLine(ex.Message); return 70; }
catch (ArgumentException ex) { Console.Error.WriteLine("RESOURCE_UNAVAILABLE: " + ex.Message); return 64; }
if (args.Length > 0 && args[0] == "--relay")
{
    using var stopping = new CancellationTokenSource();
    try
    {
        if (args.Length != 2) throw new ArgumentException("Use --relay <HTTP(S) endpoint> without other host options.");
        var endpoint = TiaOpenness.Shared.HttpAccessKeyStore.Endpoint(args[1]);
        var key = TiaOpenness.Shared.HttpAccessKeyStore.Read(TiaOpenness.Shared.DataLocations.Current.ConfigDirectory, endpoint);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        return await StdioHttpRelay.Run(endpoint, key, input, Console.Out, token: stopping.Token);
    }
    catch (OperationCanceledException) when (stopping.IsCancellationRequested) /* swallow(teardown): explicit relay shutdown closes the transport without replaying requests */ { return 0; }
    catch (Exception)
    {
        // Never print an HTTP exception: it may contain a credential or an untrusted response body.
        Console.Error
            .WriteLine("RELAY_CONNECTION_FAILED: Check the endpoint, protected Workbench access key and proxy settings. The relay did not replay any request.");
        return 2;
    }
}
if (HostCliOptions.IsCommand(args))
{
    HostCliOptions command;
    try { command = HostCliOptions.Parse(args, AppContext.BaseDirectory); }
    catch (TiaOpenness.Shared.BundleResourceUnavailableException ex) { Console.Error.WriteLine(ex.Message); return 70; }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    { Console.Error.WriteLine("ERROR: " + ex.Message); return 64; }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    { Console.Error.WriteLine("ERROR: " + ex.Message); return 70; }
    TiaOpenness.Shared.DataLocations.InitializeHost(command.Host.ReleaseKey);
    TiaOpenness.Shared.DataLocations.PruneLogsAtStartup();
    return HostCliCommands.Run(command);
}
try { options = HostOptions.Parse(args, AppContext.BaseDirectory); }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
TiaOpenness.Shared.DataLocations.InitializeHost(options.ReleaseKey);
TiaOpenness.Shared.DataLocations.PruneLogsAtStartup();
LocalStabilityTelemetry.StartupStage("program.logging.begin");
var fileLogger = new HostFileLogger(options.ReleaseKey);
fileLogger.Write("TiaMcpServer.log", DateTimeOffset.UtcNow.ToString("O") + " release=" + options.ReleaseKey);
LocalStabilityTelemetry.StartupStage("program.logging.end");

return await SharedReleaseHost.Run(options, fileLogger);
