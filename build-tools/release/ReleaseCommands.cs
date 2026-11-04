using System;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    private static readonly string Root = FindRoot(Environment.CurrentDirectory);

    internal static string Dotnet => Environment.GetEnvironmentVariable("DOTNET_EXE") ?? "dotnet";

    internal static int Run(string command, Options options) => command switch
    {
        "prepare" => Prepare.Run(Root, options),
        "merge-check" => MergeCheck.Run(Root, options),
        "generate-tool-counts" or "generate-catalogs" or "generate-tool-usage" or "generate-release-migration" or "audit-version-tools" or "audit-tool-usage" or "check-tools-list" or "generate-prerelease-notes" => GeneratorCommand(command, options),
        "check-analyzer-warnings" or "check-ratchet" or "check-envelope-rewrite" or "check-adapter-boundary" or "check-bundle-layout" or "check-repository" or "check-dead-tool-references" or "check-tia-features" or "check-script-tool-calls" => StaticCheck(command, options),
        "test-suites" => TestSuites(options),
        "get-bundled-dotnet" => GetBundledDotnet(options),
        "build-plc-worker-fixtures" => BuildPlcWorkerFixtures(options),
        "publish-vci-watch" => PublishVciWatch(options),
        "build-solutions" => BuildSolutions(options),
        "build-file-programs" => BuildFilePrograms(options),
        "prepare-decoder-python" => PrepareDecoderPython(DecoderPythonOptions(options)),
        "publish-prepared" => PublishPrepared.RunAsync(Root, PreparedPublishOptions(options)).GetAwaiter().GetResult(),
        "upload-draft" => UploadPreparedDraft(options),
        _ => throw new ReleaseException($"Unknown release command '{command}'.", 64)
    };

    internal static PublishOptions PreparedPublishOptions(Options options) => new(
        options.Get("Version") ?? throw new ReleaseException("-Version is required"), options.Get("Token", ""),
        options.Get("Repository", "asckye/TIA_Portal_Openness_MCP"), options.Get("ApiBaseUrl", "https://api.github.com"),
        false, false, GitExecutable: options.Get("Git", "git"));

    private static int UploadPreparedDraft(Options options)
    {
        var publish = PreparedPublishOptions(options);
        PublishPrepared.RequirePrerelease(publish.Version);
        ReleasePublisher.UploadDraftAsync(Root, Path.Combine(Root, "bin-build/prepared", publish.Version, "candidate.json"), publish).GetAwaiter().GetResult();
        return 0;
    }

    private static string ResolveApplication(string executable)
    {
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            var path = Path.GetFullPath(executable);
            if (path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Windows app execution aliases are not supported");
            if (!File.Exists(path)) throw new ReleaseException("Required application was not found: " + executable);
            return path;
        }
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';')
            : [""];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), executable + (Path.HasExtension(executable) ? "" : extension));
                if (!File.Exists(candidate)) continue;
                if (candidate.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Windows app execution aliases are not supported");
                return Path.GetFullPath(candidate);
            }
        throw new ReleaseException("Required application was not found: " + executable);
    }

    private static int GetBundledDotnet(Options options)
    {
        var scriptDir = Path.Combine(Root, "build-tools");
        using var pin = JsonDocument.Parse(File.ReadAllText(Path.Combine(scriptDir, "bundled-dotnet.json")));
        var version = pin.RootElement.GetProperty("version").GetString()!;
        var targetRelative = pin.RootElement.GetProperty("target").GetString()!;
        var cache = Path.GetFullPath(options.Get("Cache", Path.Combine(Root, $"bin-build/cache/dotnet-{version}")));
        var target = Path.GetFullPath(Path.Combine(Root, targetRelative));
        Directory.CreateDirectory(cache);
        var archives = new List<string>();
        foreach (var archive in pin.RootElement.GetProperty("archives").EnumerateArray())
        {
            var name = archive.GetProperty("name").GetString()!;
            var url = archive.GetProperty("url").GetString()!;
            var hash = archive.GetProperty("sha512").GetString()!;
            if (!url.StartsWith("https://builds.dotnet.microsoft.com/dotnet/", StringComparison.Ordinal)) throw new ReleaseException($"Only the official Microsoft download location is allowed: {url}");
            var path = Path.Combine(cache, name);
            if (!File.Exists(path))
            {
                if (options.Has("Offline")) throw new ReleaseException($"Missing cached archive (download disabled): {path}");
                using var client = new HttpClient();
                using var response = client.GetAsync(url).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                File.WriteAllBytes(path + ".partial", response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                File.Move(path + ".partial", path, true);
            }
            var actual = Convert.ToHexStringLower(SHA512.HashData(File.ReadAllBytes(path)));
            if (actual != hash.ToLowerInvariant()) { File.Delete(path); throw new ReleaseException($"SHA-512 mismatch for {name}; the cached copy was removed"); }
            archives.Add(path);
        }
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var archive in archives)
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var relative = entry.FullName.Replace('\\', '/');
                if (relative.StartsWith("/", StringComparison.Ordinal) || relative.Split('/').Contains("..", StringComparer.Ordinal)) throw new ReleaseException($"Unsafe archive path: {relative}");
                if (!seen.Add(relative)) throw new ReleaseException($"Archives overlap at {relative}");
                var output = Path.GetFullPath(Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!output.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ReleaseException($"Unsafe archive path: {relative}");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                entry.ExtractToFile(output, false);
            }
        }
        foreach (var framework in pin.RootElement.GetProperty("frameworks").EnumerateArray())
            if (!Directory.Exists(Path.Combine(target, "shared", framework.GetString()!, version))) throw new ReleaseException($"Bundled runtime lacks {framework.GetString()} {version}");
        if (!File.Exists(Path.Combine(target, "host", "fxr", version, "hostfxr.dll"))) throw new ReleaseException("Bundled runtime lacks hostfxr");
        foreach (var name in new[] { "LICENSE.txt", "ThirdPartyNotices.txt" })
            if (!File.Exists(Path.Combine(target, name))) throw new ReleaseException($"Bundled runtime lacks {name}");
        Console.WriteLine($"Microsoft .NET {version} laid out in {target}: {seen.Count} files");
        return 0;
    }

    internal static (string Directory, string Core) PlcWorkerApi(string key) => key switch { "14sp1" => ("TIA_V14SP1_PublicAPI/V14 SP1", "Siemens.Engineering"), "15.1" => ("TIA_V15.1_PublicAPI/V15.1", "Siemens.Engineering"), "16" => ("TIA_V16_PublicAPI/V16", "Siemens.Engineering"), "17" => ("TIA_V17_PublicAPI/V17", "Siemens.Engineering"), "18" => ("TIA_V18_PublicAPI/V18", "Siemens.Engineering"), "19" => ("TIA_V19_PublicAPI/V19", "Siemens.Engineering"), "20" => ("TIA_V20_PublicAPI/V20", "Siemens.Engineering"), "21" => ("TIA_V21_PublicAPI/V21/net48", "Siemens.Engineering.Base"), _ => throw new ReleaseException("Unsupported precise release key: " + key) };

    internal static void AssertBundledRuntime(string executable, string[] arguments, string name, string logs,
        string? bundleRoot = null, IDictionary<string, string?>? environment = null, SuiteProcess? process = null)
    {
        var trace = Path.Combine(logs, "corehost-" + name + ".txt");
        File.Delete(trace);
        var env = environment is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(environment);
        env["COREHOST_TRACE"] = "1";
        env["COREHOST_TRACEFILE"] = trace;
        env["TIA_OPENNESS_NETWORK_NO_DIALOG"] = "1";
        var result = (process ?? DotnetSuites.RunProcess)(executable, arguments, bundleRoot ?? Root, env);
        WriteLog(Path.Combine(logs, "corehost-" + name + ".out.log"), new CommandResult(result.ExitCode, result.StandardOutput, ""));
        WriteLog(Path.Combine(logs, "corehost-" + name + ".err.log"), new CommandResult(result.ExitCode, "", result.StandardError));
        var fxr = Path.GetFullPath(Path.Combine(bundleRoot ?? Root, "runtime/dotnet/host/fxr")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!File.Exists(trace) || !File.ReadAllText(trace).Contains("Resolved fxr [" + fxr, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseException($"{name} does not load the bundled .NET runtime; see {trace}");
    }

    private static string[] AddOfflineNuGetConfig(string executable, string[] arguments)
    {
        var config = Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG");
        if (!Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            arguments.Length == 0 || arguments[0] != "test" || string.IsNullOrWhiteSpace(config)) return arguments;
        return [.. arguments, "-p:RestoreConfigFile=" + Path.GetFullPath(config), "-p:NuGetAudit=false"];
    }

    private static void AddNugetConfig(List<string> args, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) args.AddRange(["--configfile", Path.GetFullPath(path)]);
    }

    private static int GetMaxParallelism(Options options)
    {
        var configured = options.Get("MaxParallelism");
        if (configured is null) return Math.Max(1, Environment.ProcessorCount);
        if (!int.TryParse(configured, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 1)
            throw new ReleaseException("-MaxParallelism must be a positive integer.", 64);
        return value;
    }

    private static string GetJsonString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int GetJsonInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static bool GetJsonBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static void WriteLog(string path, CommandResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, result.StandardOutput + result.StandardError, new System.Text.UTF8Encoding(false));
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(path, json.Replace("\r\n", "\n", StringComparison.Ordinal), new System.Text.UTF8Encoding(false));
    }

    private static void EnsureWindows(string command)
    {
        if (!OperatingSystem.IsWindows()) throw new ReleaseException($"{command} is Windows-only and cannot run on this operating system.");
    }

    private static string FindRoot(string start)
    {
        var current = new DirectoryInfo(Path.GetFullPath(start));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TiaPortalOpenness.slnx")) && Directory.Exists(Path.Combine(current.FullName, "scripts"))) return current.FullName;
            current = current.Parent;
        }
        throw new ReleaseException("Could not find repository root (TiaPortalOpenness.slnx).");
    }
}
