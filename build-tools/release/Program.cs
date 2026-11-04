using System.Diagnostics;

namespace TiaMcp.ReleaseTool;

internal static class Program
{
    internal static readonly HashSet<string> BooleanOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "NoDraftUpload", "DryRun", "SelfTest", "NoBinaries", "UseReferenceAssemblyPackage", "NoRestore", "NoBuild", "UpdateBaseline", "AllowGrowth", "Fix", "Check", "BuildOnly", "Initialize", "Complete", "ConfirmPublication", "Reuse", "All", "Offline"
    };

    public static int Main(string[] args)
    {
        // Every run uses fresh DOTNET_CLI_HOME folders; the SDK's first run would append each one's
        // tools folder to the user's persistent PATH until cmd.exe can no longer resolve dotnet.
        Environment.SetEnvironmentVariable("DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", "false");
        Environment.SetEnvironmentVariable("DOTNET_NOLOGO", "1");
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
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
            if ((command.Equals("build-solutions", StringComparison.OrdinalIgnoreCase) || command.Equals("prepare", StringComparison.OrdinalIgnoreCase) ||
                 command.Equals("merge-check", StringComparison.OrdinalIgnoreCase) || command.Equals("test-suites", StringComparison.OrdinalIgnoreCase)) &&
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
        "merge-check", "prepare", "publish-prepared", "upload-draft", "build-plc-worker-fixtures", "build-solutions", "build-file-programs", "publish-vci-watch", "prepare-decoder-python", "get-bundled-dotnet", "test-suites", "check-analyzer-warnings", "check-ratchet", "check-envelope-rewrite", "check-adapter-boundary", "check-bundle-layout", "check-repository", "check-dead-tool-references", "check-tia-features", "check-script-tool-calls", "generate-tool-counts", "generate-catalogs", "generate-tool-usage", "generate-release-migration", "audit-version-tools", "audit-tool-usage", "check-tools-list", "generate-prerelease-notes", "wrap-long-lines", "dump-il"
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
    internal static CommandResult Run(string executable, IEnumerable<string> arguments, string workingDirectory, IDictionary<string, string?>? environment = null, string? logPath = null, string? standardInput = null, int? timeoutMilliseconds = null, bool forwardOutput = false)
    {
        var end = timeoutMilliseconds is { } milliseconds ? DateTime.UtcNow.AddMilliseconds(milliseconds) : DateTime.MaxValue;
        var commandArguments = arguments.ToArray();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            CreateNoWindow = true
        };
        foreach (var argument in commandArguments) start.ArgumentList.Add(argument);
        foreach (var key in new[] { "TIA_MCP_TEST_SHARD_COUNT", "TIA_MCP_TEST_SHARD_INDEX", "TIA_MCP_TEST_SHARD_ASSEMBLY", "TIA_MCP_TEST_SHARD_PLAN", "TIA_MCP_TEST_CASE_TIMINGS", "TIA_MCP_TEST_RESERVE_SENSITIVE_SHARD" }) start.Environment.Remove(key);
        if (environment is not null)
            foreach (var (key, value) in environment)
                if (value is null) start.Environment.Remove(key); else start.Environment[key] = value;
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) { throw new ReleaseException($"Could not start {executable}: {ex.Message}"); }
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
        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
            process.StandardInput.Close();
        }

        int Remaining() => end == DateTime.MaxValue ? Timeout.Infinite : (int)Math.Clamp((end - DateTime.UtcNow).TotalMilliseconds, 0, int.MaxValue);
        void TimedOut()
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new ReleaseException($"{executable} timed out after {timeoutMilliseconds} ms.");
        }
        if (!process.WaitForExit(Remaining())) TimedOut();
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
