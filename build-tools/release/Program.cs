using System.Diagnostics;

namespace TiaMcp.ReleaseTool;

internal static class Program
{
    private static readonly HashSet<string> BooleanOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "DryRun", "SkipBuild", "NoReuse", "EarlyGatesOnly", "DocumentationOnly", "SelfTest", "NoPush", "NoTag", "NoWait",
        "KillStrayEngine", "Resume", "NoRestore", "PrepareOnly", "CompleteOnly", "Offline", "Test", "Strict", "NoBinaries",
        "SkipSourceHashes", "PackageMode", "UseReferenceAssemblyPackage", "Rebuild", "FunctionsOnly", "PassThru", "SkipFullEngines",
        "DraftOnly", "DeleteDraft", "NoBuildCache", "NoResultCache", "ResultCacheInventory", "CompileOnly", "NoBuild", "UpdateBaseline", "AllowGrowth", "Fix", "Check", "Clean", "Cold", "ProveEquivalence", "BuildOnly", "Initialize", "Complete", "GateOnly"
    };

    public static int Main(string[] args)
    {
        CheckAffinity.EnsureRunnerJob();
        // Every run uses fresh DOTNET_CLI_HOME folders; the SDK's first run would append each one's
        // tools folder to the user's persistent PATH until cmd.exe can no longer resolve dotnet.
        Environment.SetEnvironmentVariable("DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", "false");
        Environment.SetEnvironmentVariable("DOTNET_NOLOGO", "1");
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
        if (args is ["--check-process"] && Environment.GetEnvironmentVariable("TIA_MCP_CHECK_CHILD") == "1") return CheckAffinity.Child();
        if (args is ["--check-probe", var probePath] && Environment.GetEnvironmentVariable("TIA_MCP_CHECK_PROBE") == "1") return CheckAffinity.Probe(probePath);
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 64 : 0;
        }

        var command = args[0];
        if (!CommandLine.Commands.Contains(command, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown release command '{command}'.");
            PrintUsage();
            return 64;
        }

        try
        {
            var options = Options.Parse(args[1..], BooleanOptions);
            if ((command.Equals("check-all", StringComparison.OrdinalIgnoreCase) ||
                 command.Equals("run-release-build", StringComparison.OrdinalIgnoreCase) ||
                 command.Equals("build-solutions", StringComparison.OrdinalIgnoreCase) ||
                 command.Equals("preflight", StringComparison.OrdinalIgnoreCase) ||
                 command.Equals("test-suites", StringComparison.OrdinalIgnoreCase) || options.Has("SelfTest")) &&
                Environment.GetEnvironmentVariable("TIA_MCP_CHECK_RUNNER") != "1")
            {
                // The traversal also compiles this tool. Execute a private copy so
                // Windows never locks the build outputs being refreshed.
                var directory = Path.Combine(Environment.CurrentDirectory, "bin-build/check-runners", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(typeof(Program).Assembly.Location)!))
                    File.Copy(path, Path.Combine(directory, Path.GetFileName(path)));
                // ProcessRunner's JIT references BuildCommon; keep the bootstrapper free
                // of project dependencies that the child traversal can replace.
                var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_EXE") ?? "dotnet")
                {
                    WorkingDirectory = Environment.CurrentDirectory, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.ArgumentList.Add(Path.Combine(directory, "TiaMcp.ReleaseTool.dll"));
                foreach (var argument in args) start.ArgumentList.Add(argument);
                start.Environment["TIA_MCP_CHECK_RUNNER"] = "1";
                using var process = Process.Start(start) ?? throw new ReleaseException("Could not start private release runner");
                var output = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
                var error = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
                process.WaitForExit();
                Task.WaitAll(output, error);
                return process.ExitCode;
            }
            return ReleaseCommands.Run(command.ToLowerInvariant(), options);
        }
        catch (ReleaseException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run --project build-tools/release -- <command> [options]");
        Console.WriteLine("Commands: " + string.Join(", ", CommandLine.Commands));
    }
}

internal static class CommandLine
{
    internal static readonly string[] Commands =
    [
        "release", "build-release", "build-multi-version", "run-release-build", "branch-gate", "check-all", "build-tool", "cache-info", "cache-clear", "preflight", "prerequisites", "publish",
        "build-studio", "build-configurator", "build-plc-workers", "build-plc-worker-fixtures", "check-plc-workers", "build-solutions", "build-file-programs", "publish-vci-watch", "prepare-delivery", "get-bundled-dotnet", "validate-bundle", "test-suites", "host-parity",
        "check-analyzer-warnings", "check-ratchet", "check-envelope-rewrite", "check-adapter-boundary", "check-bundle-layout", "check-repository", "check-dead-tool-references", "check-tia-features", "check-script-tool-calls",
        "generate-tool-counts", "generate-catalogs", "generate-tool-usage", "generate-release-migration", "audit-version-tools", "audit-tool-usage", "check-tools-list"
    ];
}

internal sealed class Options
{
    private readonly Dictionary<string, List<string>> values = new(StringComparer.OrdinalIgnoreCase);

    internal static Options Parse(string[] args, ISet<string> booleanOptions)
    {
        var result = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token.Length == 0 || token[0] != '-')
                throw new ReleaseException($"Unexpected argument: {token}", 64);
            var option = token.TrimStart('-');
            var equals = option.IndexOf('=');
            var name = (equals >= 0 ? option[..equals] : option).Replace("-", "", StringComparison.Ordinal);
            if (name.Length == 0)
                throw new ReleaseException("Empty option name.", 64);
            string value;
            if (equals >= 0) value = option[(equals + 1)..];
            else if (booleanOptions.Contains(name))
            {
                value = "true";
                if (i + 1 < args.Length && bool.TryParse(args[i + 1], out _)) value = args[++i];
            }
            else
            {
                if (++i >= args.Length || args[i].Length > 0 && args[i][0] == '-')
                    throw new ReleaseException($"Option -{name} requires a value.", 64);
                value = args[i];
            }
            if (!result.values.TryGetValue(name, out var list)) result.values[name] = list = [];
            list.Add(value);
        }
        return result;
    }

    internal string? Get(string name) => values.TryGetValue(name, out var list) ? list[^1] : null;
    internal string Get(string name, string fallback) => Get(name) ?? fallback;
    internal bool Has(string name) => values.ContainsKey(name) && !string.Equals(Get(name), "false", StringComparison.OrdinalIgnoreCase);
    internal IReadOnlyList<string> All(string name) => values.TryGetValue(name, out var list) ? list : [];
    internal IEnumerable<string> Arguments() => values.SelectMany(pair => pair.Value.SelectMany(value => new[] { "-" + pair.Key + "=" + value }));
}

internal sealed class ReleaseException(string message, int exitCode = 1) : Exception(message)
{
    internal int ExitCode { get; } = exitCode;
}

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

internal static class ProcessRunner
{
    internal static CommandResult Run(string executable, IEnumerable<string> arguments, string workingDirectory, IDictionary<string, string?>? environment = null, string? logPath = null, string? standardInput = null, int? timeoutMilliseconds = null, bool forwardOutput = false, bool waitForDescendants = false, Action? afterExit = null)
    {
        CheckAffinity.EnsureRunnerJob();
        var requestedExecutable = executable;
        var deadline = CheckScheduler.Deadline;
        var started = DateTime.UtcNow;
        var end = timeoutMilliseconds is { } milliseconds ? started.AddMilliseconds(milliseconds) : DateTime.MaxValue;
        if (deadline is { } budget && budget.Deadline < end) end = budget.Deadline;
        if (end <= started) throw new ReleaseException("Check job timed out: " + deadline?.Name);
        var commandArguments = arguments.ToArray();
        var affinity = CheckAffinity.Current;
        if (OperatingSystem.IsWindows() && affinity == null && (deadline != null || waitForDescendants || timeoutMilliseconds != null))
        {
            using var current = Process.GetCurrentProcess(); affinity = unchecked((ulong)current.ProcessorAffinity.ToInt64());
        }
        waitForDescendants |= OperatingSystem.IsWindows() && (deadline != null || timeoutMilliseconds != null);
        string? jobName = waitForDescendants ? "Local\\TIA-Check-" + Guid.NewGuid().ToString("N") : null;
        string? childInput = null;
        if (affinity != null)
        {
            var childEnvironment = environment == null ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase);
            childEnvironment[CheckAffinity.CommandJobVariable] = jobName;
            childEnvironment["MSBUILDDISABLENODEREUSE"] = "1";
            CheckAffinity.ConfigureRuntime(childEnvironment, affinity.Value);
            childInput = System.Text.Json.JsonSerializer.Serialize(new CheckChild(executable, commandArguments, workingDirectory,
                childEnvironment, standardInput, jobName));
            executable = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.GetEnvironmentVariable("DOTNET_EXE") ?? "dotnet";
            commandArguments = [typeof(Program).Assembly.Location, "--check-process"];
        }
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null || childInput != null,
            CreateNoWindow = true
        };
        foreach (var argument in commandArguments) start.ArgumentList.Add(argument);
        if (affinity is { } cpuMask)
        {
            start.Environment["TIA_MCP_CHECK_CHILD"] = "1";
            // Convey the actual CPU budget before CLR startup as well as imposing
            // the job limit. Otherwise runtime thread/GC sizing can use all cores.
            CheckAffinity.ConfigureRuntime(start.Environment, cpuMask);
        }
        foreach (var key in new[] { "TIA_MCP_TEST_SHARD_COUNT", "TIA_MCP_TEST_SHARD_INDEX", "TIA_MCP_TEST_SHARD_ASSEMBLY", "TIA_MCP_TEST_SHARD_PLAN", "TIA_MCP_TEST_CASE_TIMINGS", "TIA_MCP_TEST_RESERVE_SENSITIVE_SHARD" }) start.Environment.Remove(key);
        if (environment is not null)
            foreach (var (key, value) in environment)
                if (value is null) start.Environment.Remove(key); else start.Environment[key] = value;
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) { throw new ReleaseException($"Could not start {executable}: {ex.Message}"); }
        CheckLoad.Current?.Register(process.Id);
        using var affinityJob = affinity is { } mask ? CheckAffinity.Assign(process, mask, jobName) : null;
        using var ownedChildren = waitForDescendants ? CheckAffinity.CompleteOnDispose(
            affinityJob ?? throw new ReleaseException("Descendant cleanup requires an owned affinity job"), CheckAffinity.OwnershipRoot) : null;
        StreamWriter? logWriter = null;
        if (logPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
            logWriter = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        using var ownedLog = logWriter;
        var logLock = new object();
        async Task<string> Read(StreamReader reader, TextWriter target)
        {
            var content = new System.Text.StringBuilder();
            while (await reader.ReadLineAsync() is { } line)
            {
                content.AppendLine(line);
                if (logWriter != null) lock (logLock) logWriter.WriteLine(line);
                if (forwardOutput) target.WriteLine(line);
            }
            return content.ToString();
        }
        var stdout = logPath != null || forwardOutput ? Read(process.StandardOutput, Console.Out) : process.StandardOutput.ReadToEndAsync();
        var stderr = logPath != null || forwardOutput ? Read(process.StandardError, Console.Error) : process.StandardError.ReadToEndAsync();
        if (standardInput is not null || childInput != null)
        {
            try
            {
                if (childInput != null) process.StandardInput.WriteLine(childInput); else process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }
            catch (IOException) when (childInput != null && process.WaitForExit(1000))
            {
                Task.WhenAll(stdout, stderr).Wait(1000);
                throw new ReleaseException("Check launcher exited before accepting " + requestedExecutable + ": " + process.ExitCode + "; " +
                    (stderr.IsCompletedSuccessfully ? stderr.Result : "stderr incomplete"));
            }
        }
        int Remaining() => end == DateTime.MaxValue ? Timeout.Infinite : (int)Math.Clamp((end - DateTime.UtcNow).TotalMilliseconds, 0, int.MaxValue);
        void TimedOut()
        {
            var evidence = logPath == null ? Path.Combine(CheckScheduler.EvidenceDirectory ?? Path.Combine(CheckAffinity.OwnershipRoot, "bin-build/process-jobs"),
                Guid.NewGuid().ToString("N") + ".timeout.json") : logPath + ".timeout.json";
            if (affinityJob != null) CheckAffinity.StopAndWait(affinityJob, CheckAffinity.OwnershipRoot, grace: false, evidence: evidence);
            else CheckAffinity.StopOwnedProcess(process, CheckAffinity.OwnershipRoot);
            process.WaitForExit();
            throw new ReleaseException(deadline != null ? "Check job timed out: " + deadline.Value.Name + " (" + requestedExecutable + ")"
                : $"{requestedExecutable} timed out after {timeoutMilliseconds} ms.");
        }
        if (!process.WaitForExit(Remaining())) TimedOut();
        afterExit?.Invoke();
        if (waitForDescendants) CheckAffinity.StopAndWait(affinityJob!, CheckAffinity.OwnershipRoot);
        if (!Task.WhenAll(stdout, stderr).Wait(Remaining())) TimedOut();
        var output = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        return new CommandResult(process.ExitCode, output, error);
    }

    internal static void RequireSuccess(CommandResult result, string description)
    {
        if (result.ExitCode != 0)
            throw new ReleaseException($"{description} exited {result.ExitCode}.\n{Tail(result.StandardOutput + result.StandardError)}");
    }

    private static string Tail(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.TakeLast(12));
    }
}
