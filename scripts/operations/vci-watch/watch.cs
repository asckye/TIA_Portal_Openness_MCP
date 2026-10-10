#!/usr/bin/env dotnet
// Optional VCI watch cycle; publish before registering the current-user task.
// Usage: dotnet run scripts/operations/vci-watch/watch.cs -- --config <config.json> [--register-task] [--interval-minutes 10] [--remove]
#:property OutputType=WinExe
#:property PublishAot=false
#:property NuGetAudit=false
#:project ../../diagnostics/TiaMcp.DiagnosticClients/TiaMcp.DiagnosticClients.csproj

using TiaMcp.DiagnosticClients;

var options = new Arguments(args);
if (options.Flag("--help")) { Console.WriteLine("--config <config.json> [--register-task [--interval-minutes 10] [--remove]]; publish with release-tool publish-vci-watch"); return 0; }
var config = Path.GetFullPath(options.Take("--config") ?? Path.Combine(AppContext.BaseDirectory, "config.json"));
try
{
    if (options.Flag("--register-task"))
    {
        var interval = int.Parse(options.Take("--interval-minutes") ?? "10"); var remove = options.Flag("--remove");
        if (options.Rest.Count != 0) throw new ArgumentException("Unexpected argument");
        return WatchOperations.RegisterTask(Environment.ProcessPath!, config, interval, remove);
    }
    if (options.Rest.Count != 0) throw new ArgumentException("Unexpected argument");
    return WatchOperations.Run(config);
}
catch (Exception ex)
{
    var directory = Path.Combine(Path.GetDirectoryName(config)!, "log"); Directory.CreateDirectory(directory);
    File.AppendAllText(Path.Combine(directory, "watch-" + DateTime.Now.ToString("yyyyMMdd") + ".log"), ex.Message + "\n"); return 1;
}
