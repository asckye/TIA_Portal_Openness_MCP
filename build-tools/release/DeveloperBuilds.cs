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
        foreach (var solution in new[] { "TiaPortalOpenness.Offline.slnx", "TiaPortalOpenness.slnx" })
        {
            var result = ProcessRunner.Run(Dotnet, ["build", Path.Combine(Root, solution), "-c", "Release", "--nologo", .. properties], Root);
            Console.Write(result.StandardOutput); Console.Error.Write(result.StandardError); ProcessRunner.RequireSuccess(result, solution);
        }
        return 0;
    }
}
