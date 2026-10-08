#!/usr/bin/env dotnet
// Test download-route ranking in a built engine or compile the exact production members with -SourceOnly.
// Usage: dotnet run scripts/checks/Test-DownloadRouteSelection.cs -- -SourceOnly -PublicApiDirectory <V21 SDK>
#:property PublishAot=false
#:property NuGetAudit=false
#:project ../../build-tools/common/TiaMcp.BuildCommon/TiaMcp.BuildCommon.csproj

using System.Text;
using TiaMcp.BuildCommon;

var options = args.ToList();
var sourceOnly = RemoveFlag(options, "-SourceOnly", "--source-only");
var exe = Take(options, "-Exe", "--exe");
var apiDirectory = Take(options, "-PublicApiDirectory", "--public-api-directory");
var dotnet = Take(options, "-Dotnet", "--dotnet") ?? "dotnet";
if (options.Count != 0) throw new ArgumentException("Unexpected argument: " + options[0]);
var root = FindRoot(Directory.GetCurrentDirectory());
if (!sourceOnly)
{
    if (string.IsNullOrWhiteSpace(exe)) exe = Path.Combine(root, "runtime", "v21", "worker", "TiaMcp.Engine.V21.exe");
    var harness = Path.Combine(root, "tests", "Engine", "TiaMcp.Engine.Harness", "TiaMcp.Engine.Harness.csproj");
    var runArgs = new List<string> { "run", "--project", harness, "-c", "Release", "--no-restore", "--", Path.GetFullPath(exe), "test-download-route" };
    if (!string.IsNullOrWhiteSpace(apiDirectory)) runArgs.Add(Path.GetFullPath(apiDirectory));
    return await Run(dotnet, runArgs, root);
}
if (string.IsNullOrWhiteSpace(apiDirectory)) throw new ArgumentException("-SourceOnly requires -PublicApiDirectory for the route fixture.");

const string FakeTypes = @"
public class FakeAddress { public string Address { get; set; } }
public class FakeTarget { public FakeTarget() { Addresses = new System.Collections.Generic.List<FakeAddress>(); } public string Name { get; set; } public System.Collections.Generic.List<FakeAddress> Addresses { get; set; } }
public class FakePcInterface { public FakePcInterface() { Addresses = new System.Collections.Generic.List<FakeAddress>(); TargetInterfaces = new System.Collections.Generic.List<FakeTarget>(); } public string Name { get; set; } public int Number { get; set; } public System.Collections.Generic.List<FakeAddress> Addresses { get; set; } public System.Collections.Generic.List<FakeTarget> TargetInterfaces { get; set; } }
public class FakeMode { public FakeMode() { PcInterfaces = new System.Collections.Generic.List<FakePcInterface>(); } public string Name { get; set; } public System.Collections.Generic.List<FakePcInterface> PcInterfaces { get; set; } }
public class FakeConnectionConfiguration { public FakeConnectionConfiguration() { Modes = new System.Collections.Generic.List<FakeMode>(); } public System.Collections.Generic.List<FakeMode> Modes { get; set; } }
public static class FakeBuilder {
 public static FakeTarget Target(string name,string ip) { var t=new FakeTarget(); t.Name=name; t.Addresses.Add(new FakeAddress { Address=ip }); return t; }
 public static FakePcInterface Pc(string name,int number,string ip,FakeTarget target) { var p=new FakePcInterface(); p.Name=name; p.Number=number; p.Addresses.Add(new FakeAddress { Address=ip }); p.TargetInterfaces.Add(target); return p; }
 public static FakeConnectionConfiguration Config(params FakePcInterface[] pcs) { var mode=new FakeMode(); mode.Name=""PN/IE""; foreach(var pc in pcs) mode.PcInterfaces.Add(pc); var c=new FakeConnectionConfiguration(); c.Modes.Add(mode); return c; }
}
";

var temporaryFixture = new OfflineFixtures("tia-route-selection-", root);
var temporary = temporaryFixture.DirectoryPath;
try
{
    var fixtureText = ExtractFixture(root);
    var assemblyPath = Path.Combine(temporary, "TiaMcp.Engine.V21.dll");
    await CompileFrameworkFixture(fixtureText, assemblyPath, Directory.GetFiles(Path.GetFullPath(apiDirectory), "Siemens.Engineering*.dll"), dotnet);
    var harness = Path.Combine(root, "tests", "Engine", "TiaMcp.Engine.Harness", "bin", "Release", "net48", "TiaMcp.Engine.Harness.exe");
    return await Run(harness, new[] { assemblyPath, "test-download-route", Path.GetFullPath(apiDirectory) }, root);
}
finally { temporaryFixture.Dispose(); }

static string ExtractFixture(string root)
{
    var sources = new EngineSources(root);
    const string head = "using System; using System.Collections; using System.Collections.Generic; using System.Linq; using System.Reflection; using Siemens.Engineering.Connection; ";
    string[] names = ["FindSubnetOrGatewayAddress", "TryCreateTargetAddress", "EnumerateDownloadRoutes", "ReadReflectedParent", "ReadReflectedInt", "ReadConfigurationAddresses", "SameIpv4Subnet24", "ScoreDownloadRoutes", "DescribeRoutes", "SelectDownloadRoute"];
    var members = string.Join('\n', names.Select(name => sources.Member(name, owner: "OnlineDownloadService")));
    var types = string.Join('\n', new[] { "DownloadRoute", "DownloadRouteSelection" }.Select(name => "private sealed " + sources.TypeText(name)));
    var helpers = string.Join('\n', new[] { "ReadReflectedString", "EnumerateReflectedProperty" }.Select(name => sources.Member(name, owner: "Portal").Replace("private static", "public", StringComparison.Ordinal)));
    return head + "namespace TiaMcpServer.Siemens { public class Portal {" + helpers + "} internal static class EngineeringGroupOperations {" + sources.Member("Items", owner: "EngineeringGroupOperations")
        + "} } namespace TiaMcpServer.Siemens.Services { public class OnlineDownloadService { private readonly Portal _session; public OnlineDownloadService(Portal session) { _session=session; }" + types + members + "} }\n" + FakeTypes;
}

static async Task CompileFrameworkFixture(string source, string output, IReadOnlyList<string> references, string dotnet)
{
    var sdkText = await Capture(dotnet, new[] { "--list-sdks" });
    var line = sdkText.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(value => value.StartsWith("10.", StringComparison.Ordinal)).LastOrDefault() ?? throw new InvalidOperationException(".NET 10 SDK is required.");
    var version = line[..line.IndexOf(' ')];
    var sdkRoot = line[(line.IndexOf('[') + 1)..^1];
    var csc = Path.Combine(sdkRoot, version, "Roslyn", "bincore", "csc.dll");
    var referenceDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v4.8");
    if (!Directory.Exists(referenceDirectory)) throw new DirectoryNotFoundException(".NET Framework 4.8 targeting pack not found: " + referenceDirectory);
    var sourcePath = Path.ChangeExtension(output, ".cs");
    var response = Path.Combine(Path.GetDirectoryName(output)!, "compile.rsp");
    await File.WriteAllTextAsync(sourcePath, source, new UTF8Encoding(false));
    var arguments = new List<string> { "/nologo", "/target:library", "/langversion:latest", "/nullable:enable", "/nostdlib+", "/out:" + Quote(output), Quote(sourcePath) };
    arguments.AddRange(Directory.GetFiles(referenceDirectory, "*.dll").Where(file => !Path.GetFileName(file).StartsWith("System.EnterpriseServices.", StringComparison.Ordinal)).Select(file => "/reference:" + Quote(file)));
    arguments.AddRange(references.Select(file => "/reference:" + Quote(file)));
    await File.WriteAllLinesAsync(response, arguments, new UTF8Encoding(false));
    var result = await Capture(dotnet, new[] { csc, "@" + response }, Path.GetDirectoryName(output));
    if (result.ExitCode != 0) throw new InvalidOperationException("Production source fixture compilation failed: " + result.Output);
}

static async Task<int> Run(string program, IReadOnlyList<string> arguments, string workingDirectory)
{
    var start = new System.Diagnostics.ProcessStartInfo(program) { WorkingDirectory = workingDirectory, UseShellExecute = false };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = System.Diagnostics.Process.Start(start)!;
    await process.WaitForExitAsync();
    return process.ExitCode;
}

static async Task<(int ExitCode, string Output)> Capture(string program, IReadOnlyList<string> arguments, string? workingDirectory = null)
{
    var start = new System.Diagnostics.ProcessStartInfo(program) { WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = System.Diagnostics.Process.Start(start)!;
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode, await outputTask + await errorTask);
}

static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
static bool RemoveFlag(List<string> values, params string[] names)
{
    var found = false;
    foreach (var name in names) found |= values.RemoveAll(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase)) > 0;
    return found;
}
static string? Take(List<string> values, params string[] names)
{
    foreach (var name in names)
    {
        var index = values.FindIndex(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) continue;
        if (index + 1 >= values.Count) throw new ArgumentException("Missing value for " + name);
        var value = values[index + 1];
        values.RemoveAt(index + 1);
        values.RemoveAt(index);
        return value;
    }
    return null;
}
static string FindRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "TiaPortalOpenness.Offline.slnx"))) return directory.FullName;
    throw new DirectoryNotFoundException("Repository root not found.");
}
