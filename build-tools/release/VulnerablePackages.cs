using System.Text.Json;

namespace TiaMcp.ReleaseTool;

internal static class VulnerablePackages
{
    internal const string Source = "https://api.nuget.org/v3/index.json";

    internal static List<ReviewFinding> Parse(string json, string root)
    {
        // .NET 10 can print restore progress before its machine-readable report.
        if (!json.TrimStart().StartsWith('{') && json.IndexOf("\n{", StringComparison.Ordinal) is var start && start >= 0)
            json = json[(start + 1)..];
        using var document = JsonDocument.Parse(json);
        var report = document.RootElement;
        if (report.TryGetProperty("problems", out var problems) && problems.EnumerateArray().Any(problem =>
                problem.GetProperty("level").GetString() == "error") ||
            !report.TryGetProperty("sources", out var sources) || !sources.EnumerateArray().Any(source => source.GetString() == Source) ||
            !report.TryGetProperty("version", out var version) || version.GetInt32() != 1 ||
            !report.TryGetProperty("projects", out var projects) || projects.GetArrayLength() == 0)
            throw new ReleaseException("Incomplete NuGet vulnerability report");
        var rows = new List<ReviewFinding>();
        foreach (var project in projects.EnumerateArray())
        {
            if (project.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
                throw new ReleaseException("NuGet project scan failed");
            string path = project.GetProperty("path").GetString()!;
            if (string.IsNullOrWhiteSpace(path)) throw new ReleaseException("NuGet project path missing");
            // A clean project has only its path, with no frameworks property.
            if (!project.TryGetProperty("frameworks", out var frameworks)) continue;
            path = Path.IsPathRooted(path) ? Path.GetRelativePath(root, path) : path;
            path = path.Replace('\\', '/');
            foreach (var framework in frameworks.EnumerateArray())
            foreach (var kind in new[] { "topLevelPackages", "transitivePackages" })
            {
                if (!framework.TryGetProperty(kind, out var packages)) continue;
                foreach (var package in packages.EnumerateArray())
                {
                    if (!package.TryGetProperty("vulnerabilities", out var vulnerabilities)) continue;
                    foreach (var vulnerability in vulnerabilities.EnumerateArray())
                    {
                        string severity = vulnerability.GetProperty("severity").GetString()!;
                        if (!new[] { "Low", "Moderate", "High", "Critical" }.Contains(severity, StringComparer.OrdinalIgnoreCase))
                            throw new ReleaseException("Unknown NuGet advisory severity: " + severity);
                        string advisory = vulnerability.GetProperty("advisoryurl").GetString()!;
                        string identity = package.GetProperty("id").GetString() + "@" + package.GetProperty("resolvedVersion").GetString();
                        rows.Add(new(path, "vulnerability-" + severity.ToLowerInvariant(),
                            framework.GetProperty("framework").GetString() + ":" + identity + ":" + advisory,
                            severity + " " + identity + " " + advisory));
                    }
                }
            }
        }
        return rows.Distinct().ToList();
    }

    internal static List<ReviewFinding> Blocking(IEnumerable<ReviewFinding> rows) =>
        rows.Where(row => row.Kind is "vulnerability-high" or "vulnerability-critical").ToList();

    internal static CommandResult Check(string root,
        Func<string[], string, IDictionary<string, string?>, CommandResult> run, string? output = null)
    {
        var environment = new Dictionary<string, string?>
        {
            ["RestoreConfigFile"] = null, ["TIA_MCP_OFFLINE_NUGET_CONFIG"] = null,
            ["NuGetAudit"] = null, ["DOTNET_CLI_UI_LANGUAGE"] = "en-US"
        };
        CommandResult result;
        try
        {
            result = run(["list", "TiaPortalOpenness.slnx", "package", "--vulnerable", "--include-transitive",
                "--format", "json", "--output-version", "1", "--source", Source], root, environment);
        }
        catch (Exception ex) when (ex is ReleaseException or IOException or System.ComponentModel.Win32Exception)
        { return NotRun(ex.Message, output); }
        if (result.ExitCode != 0) return NotRun(FirstError(result.StandardOutput + "\n" + result.StandardError), output);
        List<ReviewFinding> findings;
        try { findings = Parse(result.StandardOutput, root); }
        catch (Exception ex) when (ex is JsonException or ReleaseException or InvalidOperationException or KeyNotFoundException)
        { return NotRun(ex.Message, output); }
        var unapproved = CheckChangeApprovals.Unapproved(Blocking(findings), CheckChangeApprovals.Load(root));
        return Result(unapproved.Length == 0 ? "passed" : "failed", findings,
            unapproved.Select(row => row.Path + ": " + row.Message).ToArray(), unapproved.Length == 0 ? 0 : 1, output);
    }

    private static string FirstError(string text)
    {
        try
        {
            int start = text.IndexOf('{');
            if (start >= 0)
            {
                using var document = JsonDocument.Parse(text[start..]);
                if (document.RootElement.TryGetProperty("problems", out var problems))
                    foreach (var problem in problems.EnumerateArray())
                        if (problem.GetProperty("level").GetString() == "error") return problem.GetProperty("text").GetString()!;
            }
        }
        catch (JsonException) { /* swallow(parse-fallback): Non-JSON failures still report their first error line. */ }
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return lines.FirstOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? lines.FirstOrDefault() ?? "NuGet scan failed";
    }

    private static CommandResult NotRun(string error, string? output) =>
        Result("not run: " + FirstError(error), [], [error], 2, output);

    private static CommandResult Result(string status, IEnumerable<ReviewFinding> findings, string[] errors, int exitCode, string? output)
    {
        var rows = findings.ToArray();
        if (output != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { status, findings = rows, errors }, CheckChangeApprovals.Json) + "\n");
        }
        return new(exitCode, $"check-vulnerable-packages: {status}; {rows.Length} advisories, {Blocking(rows).Count} High/Critical\n",
            string.Join('\n', errors));
    }

    internal static int Run(string root, Options options, string dotnet)
    {
        var result = Check(root, (arguments, cwd, environment) =>
            ProcessRunner.Run(dotnet, arguments, cwd, environment, timeoutMilliseconds: 300_000), options.Get("Output"));
        Console.Write(result.StandardOutput); Console.Error.Write(result.StandardError);
        return result.ExitCode;
    }
}
