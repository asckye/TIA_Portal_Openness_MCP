using System.Text;
using TiaMcp.FoundationHost;
using HostOptions = TiaMcp.FoundationHost.HostOptions;

using var stabilityTelemetry = LocalStabilityTelemetry.Start();
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
var fileLogger = new HostFileLogger(options.ReleaseKey);
fileLogger.Write("TiaMcpServer.log", DateTimeOffset.UtcNow.ToString("O") + " release=" + options.ReleaseKey);

return await SharedReleaseHost.Run(options, fileLogger);
