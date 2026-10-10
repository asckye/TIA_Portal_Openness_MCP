namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    private static int PublishVciWatch(Options options)
    {
        EnsureWindows("publish-vci-watch");
        var output = Path.Combine(Root, "bin-build/vci-watch");
        var arguments = new List<string> { "publish", Path.Combine(Root, "scripts/operations/vci-watch/watch.cs"), "-c", "Release", "-o", output, "-p:PublishAot=false", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false" };
        var nuget = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG");
        if (nuget is not null) arguments.Add("-p:RestoreConfigFile=" + Path.GetFullPath(nuget));
        ProcessRunner.RequireSuccess(ProcessRunner.Run(Dotnet, arguments, Root), "Publish VCI watch");
        File.Copy(Path.Combine(Root, "scripts/operations/vci-watch/config.example.json"), Path.Combine(output, "config.example.json"), true);
        Console.WriteLine("Published " + Path.Combine(output, "watch.exe") + "; configure config.json before registering the task.");
        return 0;
    }
}
