using System.Diagnostics;

namespace TiaMcp.ReleaseTool;

internal static class Program
{
    private static readonly HashSet<string> BooleanOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "DryRun", "SkipBuild", "NoReuse", "EarlyGatesOnly", "DocumentationOnly", "SelfTest", "NoPush", "NoTag", "NoWait",
        "KillStrayEngine", "Resume", "NoRestore", "PrepareOnly", "CompleteOnly", "Offline", "Test", "Strict", "NoBinaries",
        "SkipSourceHashes", "PackageMode", "UseReferenceAssemblyPackage", "Rebuild", "FunctionsOnly", "PassThru", "SkipFullEngines",
        "DraftOnly", "DeleteDraft", "NoBuildCache", "CompileOnly", "NoBuild"
    };

    public static int Main(string[] args)
    {
        // Every run uses fresh DOTNET_CLI_HOME folders; the SDK's first run would append each one's
        // tools folder to the user's persistent PATH until cmd.exe can no longer resolve dotnet.
        Environment.SetEnvironmentVariable("DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", "false");
        Environment.SetEnvironmentVariable("DOTNET_NOLOGO", "1");
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
        "release", "build-release", "build-multi-version", "run-release-build", "branch-gate", "build-tool", "cache-info", "cache-clear", "preflight", "prerequisites", "publish",
        "build-studio", "build-configurator", "build-plc-workers", "prepare-delivery", "get-bundled-dotnet", "validate-bundle", "test-suites", "host-parity"
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
}

internal sealed class ReleaseException(string message, int exitCode = 1) : Exception(message)
{
    internal int ExitCode { get; } = exitCode;
}

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

internal static class ProcessRunner
{
    internal static CommandResult Run(string executable, IEnumerable<string> arguments, string workingDirectory, IDictionary<string, string?>? environment = null, string? logPath = null, string? standardInput = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment)
                if (value is null) start.Environment.Remove(key); else start.Environment[key] = value;

        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) { throw new ReleaseException($"Could not start {executable}: {ex.Message}"); }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
            process.StandardInput.Close();
        }
        process.WaitForExit();
        var output = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        if (logPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
            File.WriteAllText(logPath, output + error);
        }
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
