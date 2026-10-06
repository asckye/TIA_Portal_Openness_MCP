#!/usr/bin/env dotnet
// Compare native adapter and engine call paths between a baseline and a candidate build.
// Usage: dotnet run scripts/checks/Test-SharedNativeMigration.cs -- --config <json> --public-api-root <sdk-root> --baseline-directory <dir>
#:property PublishAot=false
#:property NuGetAudit=false

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

var options = args.ToList();
var root = FindRoot(Directory.GetCurrentDirectory());
var configPath = FullPath(Required(options, "--config"));
var sdkRoot = FullPath(Required(options, "--public-api-root"));
var baseline = FullPath(Required(options, "--baseline-directory"));
var outputOption = Take(options, "--output-directory");
var output = FullPath(outputOption ?? Path.Combine(root, "bin-build", "shared-native", Path.GetFileNameWithoutExtension(configPath)));
var baselineRef = Take(options, "--baseline-ref") ?? "master";
var skipBaselineBuild = options.Remove("--skip-baseline-build");
var skipBuild = options.Remove("--skip-build");
var validateOnly = options.Remove("--validate-only");
var releases = TakeMany(options, "--release");
if (options.Count != 0) throw new ArgumentException("Unexpected argument: " + options[0]);
RequireInside(root, output, "OutputDirectory must be inside this worktree.");
if (Path.GetFullPath(baseline).Equals(output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ArgumentException("BaselineDirectory and OutputDirectory must be different.");

var domain = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
var checker = Path.Combine(root, "scripts", "checks", "Compare-SharedNativePaths.py");
await Run("python", new[] { checker, "--config", configPath, "--validate-config" }, root, null);
if (validateOnly)
{
    Console.WriteLine("Shared-native migration config is valid.");
    return 0;
}
var domainName = domain["domain"]!.GetValue<string>();
var configuredReleases = domain["adapter"]!["releases"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
if (releases.Count == 0) releases.AddRange(configuredReleases);
foreach (var release in releases)
    if (!configuredReleases.Contains(release, StringComparer.Ordinal)) throw new InvalidDataException($"Release not configured for {domainName}: {release}");

Directory.CreateDirectory(output);
var emptyFeed = Path.Combine(output, "empty-feed");
Directory.CreateDirectory(emptyFeed);
var baselineRecord = Path.Combine(baseline, "baseline.json");
string baselineRevision;
string? baselineSource = null;
if (!skipBuild && !skipBaselineBuild)
{
    RequireInside(root, baseline, "BaselineDirectory must be inside this worktree when building baselines.");
    baselineRevision = (await Run("git", new[] { "rev-parse", "--verify", baselineRef + "^{commit}" }, root, null)).Output.Trim();
    Directory.CreateDirectory(baseline);
    if (File.Exists(baselineRecord))
    {
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(baselineRecord))!;
        if (saved["revision"]?.GetValue<string>() != baselineRevision) throw new InvalidDataException("Use a new BaselineDirectory for a different revision.");
    }
    baselineSource = Path.Combine(output, "baseline-source-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(baselineSource);
    var archive = Path.Combine(baselineSource, "source.tar");
    await Run("git", new[] { "archive", "--format=tar", "--output=" + archive, baselineRevision }, root, Path.Combine(output, "baseline-archive.log"));
    await Run("tar", new[] { "-xf", archive, "-C", baselineSource }, root, Path.Combine(output, "baseline-extract.log"));
    await File.WriteAllTextAsync(baselineRecord, new JsonObject { ["revision"] = baselineRevision }.ToJsonString(), new UTF8Encoding(false));
}
else
{
    if (!File.Exists(baselineRecord)) throw new FileNotFoundException("Saved baselines must record their source revision.", baselineRecord);
    baselineRevision = JsonNode.Parse(await File.ReadAllTextAsync(baselineRecord))!["revision"]!.GetValue<string>();
}

var weaverProject = Path.Combine(root, "build-tools", "native-call-weaver", "NativeCallWeaver.csproj");
await Dotnet(new[] { "build", weaverProject, "-c", "Release", "-p:RestoreSources=" + emptyFeed, "-p:NuGetAudit=false", "-m:1", "-nr:false" }, "weaver-build.log");
var weaverSource = new[] { Path.Combine(root, "build-tools", "native-call-weaver", "bin", "Release", "net10.0"), Path.Combine(root, "build-tools", "native-call-weaver", "bin", "Release", "net8.0") }.FirstOrDefault(Directory.Exists)
    ?? throw new DirectoryNotFoundException("Native call weaver output not found.");
await Run("python", new[] { checker, "--self-test" }, root, Path.Combine(output, "checker-self-test.log"));
await Run("python", new[] { Path.Combine(root, "scripts", "checks", "Test-SharedNativeIlReader.py") }, root, Path.Combine(output, "reader-self-test.log"));

var failedProofs = new List<string>();
foreach (var key in releases)
{
    var folder = key switch { "14sp1" => "V14Sp1", "15.1" => "V15_1", _ => "V" + key };
    var api = key switch
    {
        "14sp1" => Path.Combine(sdkRoot, "TIA_V14SP1_PublicAPI", "V14 SP1"),
        "15.1" => Path.Combine(sdkRoot, "TIA_V15.1_PublicAPI", "V15.1"),
        "21" => Path.Combine(sdkRoot, "TIA_V21_PublicAPI", "V21", "net48"),
        _ => Path.Combine(sdkRoot, "TIA_V" + key + "_PublicAPI", "V" + key)
    };
    var hasEngine = domain["engine"]!["releases"]!.AsArray().Any(x => x!.GetValue<string>() == key);
    var variants = hasEngine ? new[] { "default", "shared" } : new[] { "shared" };
    foreach (var variant in variants)
    {
        if (!skipBuild && !skipBaselineBuild)
            await BuildVariant(baselineSource!, Path.Combine(baseline, variant, "v" + key), $"baseline-{variant}-v{key}", key, folder, api, variant, hasEngine);
        var destination = Path.Combine(output, variant, "v" + key);
        if (!skipBuild) await BuildVariant(root, destination, $"{variant}-v{key}", key, folder, api, variant, hasEngine);
        await VerifyVariant(Path.Combine(baseline, variant, "v" + key), $"baseline-{variant}-v{key}", key, hasEngine);
        await VerifyVariant(destination, $"{variant}-v{key}", key, hasEngine);
    }

    var old = Path.Combine(baseline, "shared", "v" + key);
    var current = Path.Combine(output, "shared", "v" + key);
    var oldAdapter = await Dump(Path.Combine(old, $"TiaMcp.Adapter.{key}.dll"), Path.Combine(old, "adapter-inventory.json"), Path.Combine(output, "il", $"baseline-shared-v{key}"), $"baseline-v{key}-adapter");
    var newAdapter = await Dump(Path.Combine(current, $"TiaMcp.Adapter.{key}.dll"), Path.Combine(current, "adapter-inventory.json"), Path.Combine(output, "il", $"shared-v{key}"), $"shared-v{key}-adapter");
    var proofArgs = new List<string> { checker, "--config", configPath, "--release", key, "--baseline-adapter", oldAdapter, "--current-adapter", newAdapter,
        "--baseline-adapter-inventory", Path.Combine(old, "adapter-inventory.json"), "--current-adapter-inventory", Path.Combine(current, "adapter-inventory.json"),
        "--baseline-revision", baselineRevision, "--output", Path.Combine(output, "proof-v" + key + ".json") };
    if (hasEngine)
    {
        var defaultOut = Path.Combine(output, "default", "v" + key);
        var oldDefault = Path.Combine(baseline, "default", "v" + key);
        var oldDefaultEngine = await Dump(EngineExecutable(oldDefault, key), Path.Combine(oldDefault, "engine-inventory.json"), Path.Combine(output, "il", $"baseline-default-v{key}"), $"baseline-default-v{key}-engine");
        var oldDefaultAdapter = await Dump(Path.Combine(oldDefault, $"TiaMcp.Adapter.{key}.dll"), Path.Combine(oldDefault, "adapter-inventory.json"), Path.Combine(output, "il", $"baseline-default-v{key}"), $"baseline-default-v{key}-adapter");
        var defaultAdapter = await Dump(Path.Combine(defaultOut, $"TiaMcp.Adapter.{key}.dll"), Path.Combine(defaultOut, "adapter-inventory.json"), Path.Combine(output, "il", $"default-v{key}"), $"default-v{key}-adapter");
        proofArgs.AddRange(new[] { "--baseline-default-engine", oldDefaultEngine, "--baseline-default-engine-inventory", Path.Combine(oldDefault, "engine-inventory.json"), "--baseline-default-adapter", oldDefaultAdapter, "--baseline-default-adapter-inventory", Path.Combine(oldDefault, "adapter-inventory.json"), "--default-adapter", defaultAdapter });
        var oldEngine = await Dump(EngineExecutable(old, key), Path.Combine(old, "engine-inventory.json"), Path.Combine(output, "il", $"baseline-shared-v{key}"), $"baseline-v{key}-engine");
        var defaultEngine = await Dump(Path.Combine(defaultOut, $"TiaMcp.Engine.V{key}.exe"), Path.Combine(defaultOut, "engine-inventory.json"), Path.Combine(output, "il", $"default-v{key}"), $"default-v{key}-engine");
        var newEngine = await Dump(Path.Combine(current, $"TiaMcp.Engine.V{key}.exe"), Path.Combine(current, "engine-inventory.json"), Path.Combine(output, "il", $"shared-v{key}"), $"shared-v{key}-engine");
        proofArgs.AddRange(new[] { "--baseline-engine", oldEngine, "--default-engine", defaultEngine, "--shared-engine", newEngine,
            "--baseline-engine-inventory", Path.Combine(old, "engine-inventory.json"), "--default-engine-inventory", Path.Combine(defaultOut, "engine-inventory.json"), "--shared-engine-inventory", Path.Combine(current, "engine-inventory.json") });
    }
    var proof = await Run("python", proofArgs, root, Path.Combine(output, "proof-v" + key + ".log"), allowFailure: true);
    if (proof.ExitCode != 0) failedProofs.Add(key);
    Console.WriteLine(string.Join(Environment.NewLine, proof.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).TakeLast(4)));
}
if (failedProofs.Count != 0) throw new InvalidOperationException(domainName + " proof failed for: " + string.Join(", ", failedProofs));
if (configuredReleases.All(release => releases.Contains(release, StringComparer.Ordinal)))
    await Run("python", new[] { checker, "--config", configPath, "--evidence-from", output, "--output", Path.Combine(output, "evidence.json") }, root, Path.Combine(output, "evidence.log"));

Console.WriteLine($"{domainName} shared-native migration checks passed for: {string.Join(", ", releases)}");
return 0;

async Task BuildVariant(string sourceRepo, string destination, string label, string key, string folder, string api, string variant, bool engine)
{
    Directory.CreateDirectory(destination);
    var project = engine ? $"src/Engine/TiaMcpServer.V{key}.csproj" : $"src/Adapters/{folder}/Adapter.{key}.csproj";
    await Run("dotnet", new[] { "build", project, "-c", "Release", "-p:SiemensEngineeringDirectory=" + api,
        "-p:TiaSharedAdapterPaths=" + (variant == "shared" ? "true" : "false"), "-p:RestoreSources=" + emptyFeed,
        "-p:NuGetAudit=false", "-m:1", "-nr:false", "-p:BuildInParallel=false" }, sourceRepo, Path.Combine(output, label + "-build.log"));
    var source = engine
        ? Path.Combine(sourceRepo, "src", "Engine", key == "20" ? "bin-v20" : "bin", "Release", "net48")
        : Path.Combine(sourceRepo, "src", "Adapters", folder, "bin", key, "Release", "net48");
    CopyDirectory(source, destination);
    var sourceWeaver = new[] { "net10.0", "net8.0" }
        .Select(framework => Path.Combine(sourceRepo, "build-tools", "native-call-weaver", "bin", "Release", framework))
        .FirstOrDefault(Directory.Exists) ?? throw new DirectoryNotFoundException("Native call weaver output not found in " + sourceRepo);
    var savedWeaver = Path.Combine(destination, "weaver");
    CopyDirectory(sourceWeaver, savedWeaver);
}

async Task VerifyVariant(string directory, string label, string key, bool engine)
{
    var weaver = Path.Combine(directory, "weaver", "NativeCallWeaver.dll");
    await Run("dotnet", new[] { weaver, "verify", Path.Combine(directory, $"TiaMcp.Adapter.{key}.dll"), Path.Combine(directory, "adapter-inventory.json") }, root, Path.Combine(output, label + "-adapter-verify.log"));
    if (engine) await Run("dotnet", new[] { weaver, "verify", EngineExecutable(directory, key), Path.Combine(directory, "engine-inventory.json") }, root, Path.Combine(output, label + "-engine-verify.log"));
}

async Task<string> Dump(string assembly, string inventory, string directory, string label)
{
    Directory.CreateDirectory(directory);
    await Run("python", new[] { checker, "--config", configPath, "--dump", assembly, "--inventory", inventory, "--output", directory }, root, Path.Combine(output, label + "-dump.log"));
    return Path.Combine(directory, Path.GetFileName(assembly) + ".il.json");
}

string EngineExecutable(string directory, string key)
{
    var current = Path.Combine(directory, $"TiaMcp.Engine.V{key}.exe");
    if (File.Exists(current)) return current;
    var candidates = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.exe") : Array.Empty<string>();
    if (candidates.Length != 1) throw new FileNotFoundException("Expected one historical engine executable in " + directory);
    return candidates[0];
}

async Task Dotnet(string[] command, string log) => await Run("dotnet", command, root, Path.Combine(output, log));

static async Task<(int ExitCode, string Output)> Run(string program, IReadOnlyList<string> arguments, string workingDirectory, string? logPath, bool allowFailure = false)
{
    var start = new ProcessStartInfo(program) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + program);
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = await outputTask + await errorTask;
    if (logPath != null) await File.WriteAllTextAsync(logPath, output, new UTF8Encoding(false));
    if (process.ExitCode != 0 && !allowFailure) throw new InvalidOperationException($"{program} failed ({process.ExitCode}): {logPath ?? output}");
    return (process.ExitCode, output);
}

static string Required(List<string> values, string name) => Take(values, name) ?? throw new ArgumentException("Missing " + name);
static string? Take(List<string> values, string name)
{
    var index = values.IndexOf(name);
    if (index < 0) return null;
    if (index + 1 >= values.Count) throw new ArgumentException("Missing value for " + name);
    var result = values[index + 1];
    values.RemoveAt(index + 1);
    values.RemoveAt(index);
    return result;
}
static List<string> TakeMany(List<string> values, string name)
{
    var results = new List<string>();
    while (true)
    {
        var value = Take(values, name);
        if (value == null) break;
        results.Add(value);
    }
    return results;
}
static string FullPath(string path) => Path.GetFullPath(path);
static void RequireInside(string root, string path, string message)
{
    var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    if (!Path.GetFullPath(path).StartsWith(prefix, comparison)) throw new ArgumentException(message);
}
static void CopyDirectory(string source, string destination)
{
    if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Build output directory not found: " + source);
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, true);
    }
}
static string FindRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "TiaPortalOpenness.Offline.slnx"))) return directory.FullName;
    throw new DirectoryNotFoundException("Repository root not found.");
}
