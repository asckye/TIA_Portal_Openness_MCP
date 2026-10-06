#!/usr/bin/env dotnet
// Test download-route ranking in a built engine or compile the exact production members with -SourceOnly.
// Usage: dotnet run scripts/checks/Test-DownloadRouteSelection.cs -- -SourceOnly -PublicApiDirectory <V21 SDK>
#:property PublishAot=false
#:property NuGetAudit=false

using System.Text;

var options = args.ToList();
var sourceOnly = RemoveFlag(options, "-SourceOnly", "--source-only");
var exe = Take(options, "-Exe", "--exe");
var apiDirectory = Take(options, "-PublicApiDirectory", "--public-api-directory");
var dotnet = Take(options, "-Dotnet", "--dotnet") ?? "dotnet";
var python = Take(options, "-Python", "--python") ?? "python";
if (options.Count != 0) throw new ArgumentException("Unexpected argument: " + options[0]);
var root = FindRoot(Directory.GetCurrentDirectory());
if (!sourceOnly)
{
    if (string.IsNullOrWhiteSpace(exe)) exe = Path.Combine(root, "runtime", "v21", "TiaMcp.Engine.V21.exe");
    var harness = Path.Combine(root, "tests", "Engine", "TiaMcpServer.HttpTests", "TiaMcpServer.HttpTests.csproj");
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

var temporary = NewTempDirectory("tia-route-selection-");
try
{
    var fixtureText = await ExtractFixture(root, Path.Combine(temporary, "Fixture.cs"), python);
    var assemblyPath = Path.Combine(temporary, "TiaMcp.Engine.V21.dll");
    await CompileFrameworkFixture(fixtureText, assemblyPath, Directory.GetFiles(Path.GetFullPath(apiDirectory), "Siemens.Engineering*.dll"), dotnet);
    var harness = Path.Combine(root, "tests", "Engine", "TiaMcpServer.HttpTests", "bin", "Release", "net48", "HttpTests.exe");
    return await Run(harness, new[] { assemblyPath, "test-download-route", Path.GetFullPath(apiDirectory) }, root);
}
finally { Directory.Delete(temporary, true); }

static async Task<string> ExtractFixture(string root, string output, string python)
{
    const string extractor = "import sys\nfrom pathlib import Path\nroot,out=Path(sys.argv[1]),Path(sys.argv[2])\nsys.path.insert(0,str(root/'scripts/checks'))\nfrom engine_sources import EngineSources\ne=EngineSources(root)\nhead='using System; using System.Collections; using System.Collections.Generic; using System.Linq; using System.Reflection; using Siemens.Engineering.Connection; '\nnames=['FindSubnetOrGatewayAddress','TryCreateTargetAddress','EnumerateDownloadRoutes','ReadReflectedParent','ReadReflectedInt','ReadConfigurationAddresses','SameIpv4Subnet24','ScoreDownloadRoutes','DescribeRoutes','SelectDownloadRoute']\nmembers='\\n'.join(e.member(n,owner='OnlineDownloadService') for n in names)\ntypes='\\n'.join('private sealed '+e.type_text(n) for n in ['DownloadRoute','DownloadRouteSelection'])\nhelpers='\\n'.join(e.member(n,owner='Portal').replace('private static','public') for n in ['ReadReflectedString','EnumerateReflectedProperty'])\ntext=head+'namespace TiaMcpServer.Siemens { public class Portal {'+helpers+'} internal static class EngineeringGroupOperations {'+e.member('Items',owner='EngineeringGroupOperations')+'} } namespace TiaMcpServer.Siemens.Services { public class OnlineDownloadService { private readonly Portal _session; public OnlineDownloadService(Portal session) { _session=session; }'+types+members+'} }'\nout.write_text(text+'\\n'+sys.argv[3],encoding='utf-8')";
    var result = await Capture(python, new[] { "-c", extractor, root, output, FakeTypes });
    if (result.ExitCode != 0) throw new InvalidOperationException("Production source fixture extraction failed: " + result.Output);
    return await File.ReadAllTextAsync(output);
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
static string NewTempDirectory(string prefix)
{
    var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var path = Path.GetFullPath(Path.Combine(root, prefix + Guid.NewGuid().ToString("N")));
    if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Test directory escaped TEMP.");
    Directory.CreateDirectory(path);
    return path;
}
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
