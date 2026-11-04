using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class PlcReleasePipelineTests
{
    [Theory]
    [InlineData("14sp1")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void ProductionInstallRemovesTheEnginePayloadAndRequiresTheMatchingAdapter(string key)
    {
        using var tree = new StaticTree();
        var source = $"src/PlcWorker/bin/{key}/Release/net48/";
        tree.Write(source + $"TiaMcp.PlcWorker.{key}.exe", "production-plc");
        tree.Write(source + $"TiaMcp.Adapter.{key}.dll", "adapter");
        tree.Write(source + "TiaMcp.Adapters.Contracts.dll", "contracts");
        tree.Write($"runtime/v{key}/worker/TiaMcp.Engine.V{key}.exe", "engine");
        ReleaseCommands.InstallReleasePlcWorker(tree.Root, key);
        var installed = ReleaseCommands.PlcReleaseInstallDirectory(tree.Root, key);
        Assert.Equal("production-plc", File.ReadAllText(Path.Combine(installed, $"TiaMcp.PlcWorker.{key}.exe")));
        Assert.False(File.Exists(Path.Combine(tree.Root, $"runtime/v{key}/worker/TiaMcp.Engine.V{key}.exe")));
        Assert.Equal("worker", Path.GetFileName(installed));
        tree.Write(source + "TiaMcp.Adapter.wrong.dll", "wrong adapter");
        Assert.Throws<ReleaseException>(() => ReleaseCommands.InstallReleasePlcWorker(tree.Root, key));
    }

}
