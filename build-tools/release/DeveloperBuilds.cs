namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    private static int BuildSolutions(Options options)
    {
        EnsureWindows("build-solutions");
        var properties = new List<string> { "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false" };
        if (options.Get("PublicApiRoot") is { } api) properties.Add("-p:TiaPublicApiRoot=" + Path.GetFullPath(api));
        var nuget = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG");
        if (nuget is not null) properties.Add("-p:RestoreConfigFile=" + Path.GetFullPath(nuget));
        var directory = Path.GetFullPath(options.Get("OutputDirectory", Path.Combine(Root, "bin-build/analyzer-warnings/solutions-" + Guid.NewGuid().ToString("N"))));
        AnalyzerWarnings.LocalDirectory(directory);
        if (Directory.Exists(directory)) throw new ReleaseException("Analyzer report directory must be fresh: " + directory, 64);
        Directory.CreateDirectory(directory);
        var environment = new Dictionary<string, string?> { ["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = directory };
        foreach (var solution in new[] { "TiaPortalOpenness.Offline.slnx", "TiaPortalOpenness.slnx" })
        {
            var result = ProcessRunner.Run(Dotnet, ["build", Path.Combine(Root, solution), "-c", "Release", "--nologo", .. properties], Root, environment,
                logPath: Path.Combine(directory, Path.GetFileNameWithoutExtension(solution) + ".log"));
            Console.Write(result.StandardOutput); Console.Error.Write(result.StandardError); ProcessRunner.RequireSuccess(result, solution);
        }
        AnalyzerWarnings.RememberDirectory(Root, directory);
        return AnalyzerWarnings.Run(Root, Options.Parse(["-Check", "-DiagnosticsDirectory", directory], new HashSet<string> { "Check" }));
    }
}
