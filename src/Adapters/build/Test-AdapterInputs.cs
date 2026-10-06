#!/usr/bin/env dotnet
// Validate adapter source allowlists and PublicAPI identities without compiling or loading Siemens assemblies.
// Usage: dotnet run src/Adapters/build/Test-AdapterInputs.cs -- --source-root src --public-api-root <sdk> --evidence-directory <path>
#:property PublishAot=false
#:property NuGetAudit=false

using System.Text;
using System.Text.Json.Nodes;

var options = args.ToList();
var sourceRoot = Full(Take(options, "--source-root") ?? throw new ArgumentException("Missing --source-root"));
var apiRoot = Full(Take(options, "--public-api-root") ?? throw new ArgumentException("Missing --public-api-root"));
var evidenceDirectory = Full(Take(options, "--evidence-directory") ?? throw new ArgumentException("Missing --evidence-directory"));
var dotnet = Take(options, "--dotnet") ?? "dotnet";
if (options.Count != 0) throw new ArgumentException("Unexpected argument: " + options[0]);
Directory.CreateDirectory(evidenceDirectory);
Environment.SetEnvironmentVariable("DOTNET_GENERATE_ASPNET_CERTIFICATE", "false");
Environment.SetEnvironmentVariable("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1");
Environment.SetEnvironmentVariable("DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", "false");

var adapterRoot = FindAdapterRoot(Directory.GetCurrentDirectory());
var cases = new (string Folder, string Key, string ApiRelative)[]
{
    ("V14Sp1", "14sp1", "TIA_V14SP1_PublicAPI/V14 SP1"), ("V15_1", "15.1", "TIA_V15.1_PublicAPI/V15.1"),
    ("V16", "16", "TIA_V16_PublicAPI/V16"), ("V17", "17", "TIA_V17_PublicAPI/V17"),
    ("V18", "18", "TIA_V18_PublicAPI/V18"), ("V19", "19", "TIA_V19_PublicAPI/V19"),
    ("V20", "20", "TIA_V20_PublicAPI/V20"), ("V21", "21", "TIA_V21_PublicAPI/V21/net48")
};
var results = new JsonArray();
var passed = 0;
var failed = 0;

async Task Check(string name, (string Folder, string Key, string ApiRelative) item, string api, string[] extra, string expectedError = "")
{
    var project = Path.Combine(adapterRoot, item.Folder, "Adapter." + item.Key + ".csproj");
    var log = Path.Combine(evidenceDirectory, name + ".log");
    var buildOutput = Path.Combine(evidenceDirectory, name + "-" + Guid.NewGuid().ToString("N"));
    var args = new List<string>
    {
        "msbuild", project, "-nologo", "-v:minimal", "-t:ValidateAdapterInputs",
        "-p:AdapterSourceRoot=" + sourceRoot,
        "-p:SiemensEngineeringDirectory=" + api,
        "-p:BaseIntermediateOutputPath=" + Path.Combine(buildOutput, "obj") + Path.DirectorySeparatorChar,
        "-p:BaseOutputPath=" + Path.Combine(buildOutput, "bin") + Path.DirectorySeparatorChar
    };
    args.AddRange(extra);
    var result = await Run(dotnet, args, adapterRoot);
    await File.WriteAllTextAsync(log, result.Output, new UTF8Encoding(false));
    var noCompileOutput = !Directory.Exists(buildOutput) || !Directory.EnumerateFiles(buildOutput, "*", SearchOption.AllDirectories).Any();
    var matches = expectedError.Length == 0 ? result.ExitCode == 0 : result.ExitCode != 0 && result.Output.Contains(expectedError, StringComparison.Ordinal);
    var ok = matches && noCompileOutput;
    Record(name, ok, result.ExitCode, expectedError, noCompileOutput, log);
}

foreach (var item in cases) await Check("valid-" + item.Key, item, Path.Combine(apiRoot, item.ApiRelative.Replace('/', Path.DirectorySeparatorChar)), Array.Empty<string>());
var v20 = cases[6];
var api20 = Path.Combine(apiRoot, v20.ApiRelative.Replace('/', Path.DirectorySeparatorChar));
await Check("wrong-api-version", v20, Path.Combine(apiRoot, cases[5].ApiRelative.Replace('/', Path.DirectorySeparatorChar)), Array.Empty<string>(), "Wrong adapter API identity");
await Check("missing-api-directory", v20, Path.Combine(evidenceDirectory, "absent-sdk"), Array.Empty<string>(), "PublicAPI 20 requires Siemens.Engineering.dll");
await Check("missing-api-module", cases[7], api20, Array.Empty<string>(), "PublicAPI 21 requires Siemens.Engineering.Base.dll");
await Check("wrong-release", v20, api20, new[] { "-p:TiaReleaseKey=21" }, "Adapter release mismatch");
await Check("override-project-identity", v20, api20, new[] { "-p:AdapterReleaseKey=19", "-p:TiaReleaseKey=19" }, "Adapter release mismatch");
await Check("excluded-v14", cases[0], Path.Combine(apiRoot, cases[0].ApiRelative.Replace('/', Path.DirectorySeparatorChar)), new[] { "-p:TiaReleaseKey=14" }, "Adapter release mismatch");
await Check("excluded-v15", cases[1], Path.Combine(apiRoot, cases[1].ApiRelative.Replace('/', Path.DirectorySeparatorChar)), new[] { "-p:TiaReleaseKey=15" }, "Adapter release mismatch");
await Check("wrong-framework", v20, api20, new[] { "-p:TargetFramework=net461" }, "Adapter framework mismatch");
await Check("wrong-platform", v20, api20, new[] { "-p:PlatformTarget=x86" }, "x64 library");
await Check("executable-output", v20, api20, new[] { "-p:OutputType=Exe" }, "x64 library");
await Check("wildcard-sources", v20, api20, new[] { "-p:EnableDefaultCompileItems=true" }, "explicit allowlist");
await Check("missing-source", v20, api20, new[] { "-p:AdapterSourceRoot=" + Path.Combine(evidenceDirectory, "absent-source") }, "Missing allowlisted adapter source");

var json = new JsonObject { ["results"] = results };
await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "input-results.json"), json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
Console.WriteLine($"Adapter input checks: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

void Record(string name, bool ok, int exitCode, string expectedError, bool noCompileOutput, string log)
{
    if (ok) passed++; else failed++;
    results.Add((JsonNode)new JsonObject
    {
        ["name"] = name, ["passed"] = ok, ["exitCode"] = exitCode, ["expectedError"] = expectedError,
        ["noCompileOutput"] = noCompileOutput, ["log"] = log
    });
    Console.WriteLine(name + ": " + ok.ToString().ToLowerInvariant());
}

static async Task<(int ExitCode, string Output)> Run(string program, IReadOnlyList<string> arguments, string workingDirectory)
{
    var start = new System.Diagnostics.ProcessStartInfo(program) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start " + program);
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode, await outputTask + await errorTask);
}

static string Full(string path) => Path.GetFullPath(path);
static string? Take(List<string> values, string key)
{
    var index = values.IndexOf(key);
    if (index < 0) return null;
    if (index + 1 == values.Count) throw new ArgumentException("Missing value for " + key);
    var result = values[index + 1];
    values.RemoveAt(index + 1);
    values.RemoveAt(index);
    return result;
}
static string FindAdapterRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "TiaPortalOpenness.Offline.slnx"))) return Path.Combine(directory.FullName, "src", "Adapters");
    throw new DirectoryNotFoundException("Repository root not found.");
}
