#!/usr/bin/env dotnet
// Real-project wrong-path sweep, using the selected bundle's packaged MCP entry.
// Usage: dotnet run scripts/diagnostics/Sweep-WrongPathHonesty.cs -- <project.apXX> --bundle-root <bundle> [--release-key 21]
#:property PublishAot=false
#:property NuGetAudit=false
#:project TiaMcp.DiagnosticClients/TiaMcp.DiagnosticClients.csproj

using System.Text;
using TiaMcp.BuildCommon;
using TiaMcp.DiagnosticClients;

Console.OutputEncoding = new UTF8Encoding(false);
try
{
    var options = new Arguments(args);
    var bundle = options.Take("--bundle-root") ?? Repository.FindRoot(Environment.CurrentDirectory);
    var release = options.Take("--release-key") ?? "21";
    var help = options.Flag("--help");
    if (options.Rest.Count == 0 || help) { Console.WriteLine("<project.apXX> --bundle-root <bundle> --release-key <key>; live manual gate, requires a real test project"); return help ? 0 : 2; }
    if (options.Rest.Count != 1) throw new ArgumentException("Specify exactly one project and select its bundle with --bundle-root");
    using var client = new StdioClient(FoundationLaunch.Resolve(bundle, release), "wrong-path-sweep");
    return WrongPathSweep.Run(client, Path.GetFullPath(options.Rest[0]), Console.Out);
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
