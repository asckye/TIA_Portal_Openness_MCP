using System.Globalization;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class TestDeadlineContracts
{
    private static readonly string[] Files =
    [
        "tests/FoundationHost/TiaMcp.EngineHost.Tests/ConcurrencyTests.cs",
        "tests/FoundationHost/TiaMcp.EngineHost.Tests/PipelineTests.cs",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/ConcurrencyTests.cs",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/EngineHttpSessionTests.cs",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/EngineWorkerClientTests.cs",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/ExportPublicationTests.cs",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/FixR1RegressionTests.cs",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/FoundationProcessLeaseTests.cs",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/OfflineSymbolManifestTests.cs",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/OperatorProtocolTests.cs",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/OperatorSessionTests.cs",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/PlcWorkerTimeoutTests.cs",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/PortedPortalAdmissionTests.cs",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/Program.cs",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/SessionStateTests.cs",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/SharedWorkerRegressionTests.cs",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/WorkbenchControlHostTests.cs",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/WorkerClientTests.cs",
        "tests/Adapters/TiaMcp.Adapters.Contracts.Tests/StudioThreadGuardTests.cs",
        "tests/KeptCode/TiaMcp.KeptCode.Tests/ApprovalTests.cs",
        "tests/KeptCode/TiaMcp.KeptCode.Tests/ImportStagingTests.cs",
        "tests/KeptCode/TiaMcp.KeptCode.Tests/V4ProtocolRulesTests.cs",
        "tests/Release/TiaMcp.ReleaseTool.Tests/PlainReleaseTests.cs",
        "tests/Release/TiaMcp.ReleaseTool.Tests/ReleaseSingleStageTests.cs",
        "tests/Release/TiaMcp.ReleaseTool.Tests/ReleaseValidationTests.cs",
        "tests/Repository/TiaMcp.SourceContracts.Tests/ScriptClientTests.cs",
        "tests/Shared/TiaMcp.WorkbenchControl.Tests/PipeSecurityTests.cs",
        "tests/Studio/TiaOpenness.Core.Tests/BridgeChannelTests.cs",
        "tests/Studio/TiaOpenness.Core.Tests/CallJournalTests.cs",
        "tests/Studio/TiaOpenness.Gui.Tests/ApprovalServiceTests.cs",
        "tests/Studio/TiaOpenness.Gui.Tests/HostLifecycleTests.cs",
        "tests/Studio/TiaOpenness.Gui.Tests/McpPanelTests.cs",
        "tests/Studio/TiaOpenness.Gui.Tests/ResponsivenessTests.cs",
        "tests/Studio/TiaOpenness.Gui.Tests/UnifiedDesktopTests.Menu.cs",
        "tests/Studio/TiaOpenness.Gui.Tests/VciPanelTests.cs",
        "tests/Studio/TiaOpenness.Gui.Tests/WorkbenchApprovalConfirmationTests.cs",
        "tests/Studio/TiaOpenness.Gui.Tests/WorkbenchControlPipeTests.cs",
        "tests/Studio/TiaOpenness.Gui.Tests/WorkbenchControlTests.cs",
        "tests/Studio/TiaOpenness.Gui.Tests/WorkbenchMcpPrefillTests.cs",
        "tests/Tools/TiaMcp.ShippedTools.Tests/PluginLauncherTests.cs",
        "tests/Updater/TiaMcp.Updater.Tests/UpdaterTests.cs",
        "tests/WorkerChannel/TiaMcp.WorkerChannel.Tests/ProtocolTests.cs",
    ];

    // Exact statements retain the approved sampling window, expired budgets and fixture behaviour.
    private static readonly HashSet<string> Remains = new(StringComparer.Ordinal)
    {
        "tests/FoundationHost/TiaMcp.EngineHost.Tests/PipelineTests.cs|while (duration.Elapsed < TimeSpan.FromSeconds(5))",
        "tests/WorkerChannel/TiaMcp.WorkerChannel.Tests/ProtocolTests.cs|await Assert.ThrowsAsync<ChannelFaultException>(()=>wire.Client.ConnectAsync(TimeSpan.FromMilliseconds(40)));",
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/PlcWorkerTimeoutTests.cs|Assert.True(await channel.WaitForDrain(TimeSpan.FromSeconds(5)));",
        "tests/Studio/TiaOpenness.Gui.Tests/WorkbenchControlTests.cs|entered.Set(); release.Wait(TimeSpan.FromSeconds(2));",
        "tests/Studio/TiaOpenness.Gui.Tests/WorkbenchControlTests.cs|entered.Set(); release.Wait(TimeSpan.FromSeconds(4));",
        "tests/Tools/TiaMcp.ShippedTools.Tests/PluginLauncherTests.cs|process.Kill(); process.WaitForExit(15000);",
    };

    [Fact]
    public void ApprovedWaitsUseHangGuardsAndRetainOnlyTheListedShortLimits()
    {
        string root = Repository.FindRoot(AppContext.BaseDirectory);
        var failures = new List<string>();
        foreach (string path in Files)
        {
            int number = 0;
            foreach (string line in File.ReadLines(Path.Combine(root, path)))
            {
                number++;
                bool wait = Regex.IsMatch(line,
                    @"\b(?:WaitAsync|WaitForExit|WaitForDrain|Wait|Join|SpinUntil|SignalAndWait|ConnectAsync|TryEnter|CancellationTokenSource)\(");
                bool elapsed = line.Contains(".Elapsed", StringComparison.Ordinal) && line.Contains('<');
                if (!wait && !elapsed) continue;
                var limits = Regex.Matches(line,
                    @"TimeSpan\.From(?<unit>Seconds|Milliseconds)\((?<value>[\d.]+)\)|(?:WaitForExit|Wait|Join|ConnectAsync)\((?<milliseconds>[\d_]+)");
                bool shortLimit = limits.Cast<Match>().Any(match => match.Groups["milliseconds"].Success
                    ? double.Parse(match.Groups["milliseconds"].Value.Replace("_", ""), CultureInfo.InvariantCulture) < 30_000
                    : double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture)
                        < (match.Groups["unit"].Value == "Seconds" ? 30 : 30_000));
                if (shortLimit && !Remains.Contains(path + "|" + line.Trim())) failures.Add(path + "(" + number + "): " + line.Trim());
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
