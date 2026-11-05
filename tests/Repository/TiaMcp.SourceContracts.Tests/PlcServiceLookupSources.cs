using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class PlcServiceLookupSources
{
    [Fact]
    public void Unified_theme_summary_uses_verified_screen_writes()
    {
        string source = Read("src/Adapters/Native/Hmi/UnifiedHmiAdapter.cs");
        Assert.Contains("return HmiExchangePolicy.UnifiedDesignSummary(screenName, writeResults, screenWriteCount, changed.Count, failed.Count);", source);
        int screen = source.IndexOf("ApplyJsonProperties(screen, screenProps", StringComparison.Ordinal);
        int count = source.IndexOf("screenWriteCount = writeResults.Count;", screen, StringComparison.Ordinal);
        Assert.True(screen >= 0 && count > screen && count < source.IndexOf("var itemArray", screen, StringComparison.Ordinal));
    }

    private static string Read(string path) => Regex.Replace(
        File.ReadAllText(Path.Combine(Repository.FindRoot(AppContext.BaseDirectory), path)), @"\s+", " ");

    [Fact]
    public void WorkerResolverAsksPlcSoftwareBeforeCpuItemAndDevice()
    {
        string source = Read("src/Adapters/Native/Plc/PlcOrganisationWorker.cs");
        source = source[source.IndexOf("public T? ResolvePlcService<T>", StringComparison.Ordinal)..];
        int plc = source.IndexOf("plc.GetService<T>()", StringComparison.Ordinal);
        int selection = source.IndexOf("engine.ReadSelection(path)", StringComparison.Ordinal);
        int item = source.IndexOf("((IEngineeringServiceProvider)item).GetService<T>()", StringComparison.Ordinal);
        int device = source.IndexOf("(selected.DeviceContext as IEngineeringServiceProvider)?.GetService<T>()", StringComparison.Ordinal);
        Assert.True(plc >= 0 && plc < selection && selection < item && item < device);
        Assert.Contains("item = item.Parent as Siemens.Engineering.HW.DeviceItem", source);
        Assert.Contains("if (service != null) return service;", source);
    }

    [Fact]
    public void LoadableProviderUsesSharedResolver()
    {
        string source = Read("src/Adapters/Native/Online/OnlineDataAdapter.cs");
        Assert.Contains("_session.ResolvePlcService<LoadableProvider>(softwarePath, plc)", source);
        Assert.Contains("_session.ResolvePlcService<FingerprintDataProvider>(softwarePath, plc)", source);
        foreach (string provider in new[] { "LoadableProvider", "FingerprintDataProvider" })
            Assert.Contains("new PlcSoftwareException(\"NotSupportedOnVersion\", \"" + provider +
                " is not exposed for this CPU on this release.\")", source);
        Assert.DoesNotContain("plc.GetService<LoadableProvider>()", source);
    }

    [Fact]
    public void MonitorProbeUsesSharedResolverForOnlineAndDownloadProviders()
    {
        string source = Read("src/Adapters/Native/Plc/PlcWatchTables.Online.cs");
        Assert.Contains("_session.ResolvePlcService<Siemens.Engineering.Online.OnlineProvider>(softwarePath, plc)", source);
        Assert.Contains("_session.ResolvePlcService<Siemens.Engineering.Download.DownloadProvider>(softwarePath, plc)", source);
        foreach (string row in new[] { "OnlineService", "PlcOnlineProvider", "WatchTableProvider" }) Assert.Contains(row, source);
        Assert.Contains("TryGetService(plc, st)", source);
    }
}
