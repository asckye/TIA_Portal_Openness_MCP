#!/usr/bin/env dotnet
// Check release-specific worker adapter selection and woven native-call coverage.
// Usage: dotnet run src/Adapters/build/Test-WorkerIsolation.cs -- --evidence-directory <path> --native-call-weaver <dll>
#:property PublishAot=false
#:property NuGetAudit=false

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var options = args.ToList();
var evidenceDirectory = Path.GetFullPath(Take(options, "--evidence-directory") ?? throw new ArgumentException("Missing --evidence-directory"));
var weaver = Path.GetFullPath(Take(options, "--native-call-weaver") ?? throw new ArgumentException("Missing --native-call-weaver"));
var dotnet = Take(options, "--dotnet") ?? "dotnet";
if (options.Count != 0) throw new ArgumentException("Unexpected argument: " + options[0]);
Directory.CreateDirectory(evidenceDirectory);
Environment.SetEnvironmentVariable("DOTNET_GENERATE_ASPNET_CERTIFICATE", "false");
Environment.SetEnvironmentVariable("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1");
Environment.SetEnvironmentVariable("DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", "false");

var repo = FindRoot(Directory.GetCurrentDirectory());
var adapterRoot = Path.Combine(repo, "src", "Adapters");
var worker = Path.Combine(repo, "src", "PlcWorker", "TiaMcp.PlcWorker.csproj");
var results = new JsonArray();
var passed = 0;
var failed = 0;

foreach (var key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
{
    var selectionLog = Path.Combine(evidenceDirectory, "selection-" + key + ".json");
    var selection = await Run(dotnet, new[] { "msbuild", worker, "-nologo", "-v:quiet", "-t:ValidateWorkerAdapter", "-p:TiaReleaseKey=" + key, "-getItem:ProjectReference", "-getProperty:TargetFramework" }, repo);
    await File.WriteAllTextAsync(selectionLog, selection.Output, new UTF8Encoding(false));
    var correct = selection.ExitCode == 0 && HasSelectedReferences(selection.Output, "/Adapter." + key + ".csproj") && TargetFramework(selection.Output) == "net48";
    Record("selection-" + key, correct, selection.ExitCode);

    var binary = Path.Combine(repo, "src", "PlcWorker", "bin", key, "Release", "net48", "TiaMcp.Adapter." + key + ".dll");
    var verifyLog = Path.Combine(evidenceDirectory, "verify-" + key + ".log");
    var verify = await Run(dotnet, new[] { weaver, "verify", binary, Path.Combine(evidenceDirectory, "coverage-" + key + ".json") }, repo);
    await File.WriteAllTextAsync(verifyLog, verify.Output, new UTF8Encoding(false));
    Record("coverage-" + key, verify.ExitCode == 0, verify.ExitCode);

    var rejectionLog = Path.Combine(evidenceDirectory, "coverage-rejection-" + key + ".log");
    var rejection = await Run(dotnet, new[] { weaver, "self-test", binary }, repo);
    await File.WriteAllTextAsync(rejectionLog, rejection.Output, new UTF8Encoding(false));
    Record("coverage-rejection-" + key, rejection.ExitCode == 0, rejection.ExitCode);
}

var cases = new (string Name, string[] Args, string Expected)[]
{
    ("missing-key", new[] { "-p:TargetFramework=net48" }, "exact release adapters"),
    ("excluded-v14", new[] { "-p:TiaReleaseKey=14", "-p:TargetFramework=net48" }, "exact release adapters"),
    ("excluded-v15", new[] { "-p:TiaReleaseKey=15", "-p:TargetFramework=net48" }, "exact release adapters"),
    ("future-version", new[] { "-p:TiaReleaseKey=22", "-p:TargetFramework=net48" }, "exact release adapters"),
    ("wrong-framework", new[] { "-p:TiaReleaseKey=20", "-p:TargetFramework=net461" }, "framework mismatch"),
    ("wrong-platform", new[] { "-p:TiaReleaseKey=20", "-p:PlatformTarget=x86" }, "x64 executable"),
    ("wrong-output", new[] { "-p:TiaReleaseKey=20", "-p:OutputType=Library" }, "x64 executable"),
    ("glob-sources", new[] { "-p:TiaReleaseKey=20", "-p:EnableDefaultCompileItems=true" }, "explicit allowlist")
};
foreach (var test in cases)
{
    var log = Path.Combine(evidenceDirectory, test.Name + ".log");
    var result = await Run(dotnet, new[] { "msbuild", worker, "-nologo", "-v:minimal", "-t:ValidateWorkerAdapter" }.Concat(test.Args).ToArray(), repo);
    await File.WriteAllTextAsync(log, result.Output, new UTF8Encoding(false));
    Record(test.Name, result.ExitCode != 0 && result.Output.Contains(test.Expected, StringComparison.Ordinal), result.ExitCode);
}

var identityLog = Path.Combine(evidenceDirectory, "identity-override.json");
var identity = await Run(dotnet, new[] { "msbuild", worker, "-nologo", "-v:quiet", "-t:ValidateWorkerAdapter", "-p:TiaReleaseKey=20", "-p:SelectedAdapterDirectory=V21", "-p:SelectedAdapterProject=invalid.csproj", "-getItem:ProjectReference" }, repo);
await File.WriteAllTextAsync(identityLog, identity.Output, new UTF8Encoding(false));
Record("identity-override-ignored", identity.ExitCode == 0 && HasSelectedReferences(identity.Output, "/V20/Adapter.20.csproj"), identity.ExitCode);

var adapter = Path.Combine(adapterRoot, "V20", "Adapter.20.csproj");
var missingWeaverLog = Path.Combine(evidenceDirectory, "missing-weaver.log");
var missingWeaver = await Run(dotnet, new[] { "msbuild", adapter, "-nologo", "-v:minimal", "-t:InstrumentAdapterNativeBoundaries", "-p:NativeCallWeaverPath=" + Path.Combine(evidenceDirectory, "absent-weaver.dll") }, repo);
await File.WriteAllTextAsync(missingWeaverLog, missingWeaver.Output, new UTF8Encoding(false));
Record("missing-weaver", missingWeaver.ExitCode != 0 && missingWeaver.Output.Contains("Uninstrumented adapter output is refused", StringComparison.Ordinal), missingWeaver.ExitCode);

var output = new JsonObject { ["results"] = results };
await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "worker-isolation-results.json"), results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
Console.WriteLine($"Worker isolation checks: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

void Record(string name, bool ok, int exitCode)
{
    if (ok) passed++; else failed++;
    results.Add((JsonNode)new JsonObject { ["name"] = name, ["passed"] = ok, ["exitCode"] = exitCode });
    Console.WriteLine(name + ": " + ok.ToString().ToLowerInvariant());
}

static bool HasSelectedReferences(string output, string adapterSuffix)
{
    try
    {
        var projectReferences = JsonNode.Parse(output)!["Items"]!["ProjectReference"]!.AsArray();
        return projectReferences.Any(item => (item!["Identity"]?.GetValue<string>() ?? "").Replace('\\', '/').EndsWith(adapterSuffix, StringComparison.Ordinal))
            && projectReferences.Any(item => (item!["Identity"]?.GetValue<string>() ?? "").Replace('\\', '/').EndsWith("/Adapters.Contracts/TiaMcp.Adapters.Contracts.csproj", StringComparison.Ordinal))
            && projectReferences.All(item =>
            {
                var identity = (item!["Identity"]?.GetValue<string>() ?? "").Replace('\\', '/');
                return identity.EndsWith(adapterSuffix, StringComparison.Ordinal)
                    || identity.EndsWith("/Adapters.Contracts/TiaMcp.Adapters.Contracts.csproj", StringComparison.Ordinal)
                    || identity.EndsWith("/WorkerChannel/TiaMcp.WorkerChannel.csproj", StringComparison.Ordinal);
            });
    }
    catch /* swallow(parse-fallback): invalid project-reference JSON means the worker is not selected. */ { return false; }
}

static string TargetFramework(string output)
{
    try { return JsonNode.Parse(output)!["Properties"]!["TargetFramework"]!.GetValue<string>(); }
    catch /* swallow(parse-fallback): missing or invalid target-framework JSON cannot provide a framework value. */ { return ""; }
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

static string? Take(List<string> values, string key)
{
    var index = values.IndexOf(key);
    if (index < 0) return null;
    if (index + 1 >= values.Count) throw new ArgumentException("Missing value for " + key);
    var result = values[index + 1];
    values.RemoveAt(index + 1);
    values.RemoveAt(index);
    return result;
}
static string FindRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "TiaPortalOpenness.Offline.slnx"))) return directory.FullName;
    throw new DirectoryNotFoundException("Repository root not found.");
}
