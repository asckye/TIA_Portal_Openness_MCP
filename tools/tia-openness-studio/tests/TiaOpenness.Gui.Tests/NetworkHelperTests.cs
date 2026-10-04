using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace TiaOpenness.Gui.Tests;

// The configuration page relaunches the workbench elevated with --network to reserve the HTTP prefix.
// 3.3.0 candidate 1 crashed on that path (WPF on .NET 10 rejects a null StartupUri) before it could
// report anything, so the page only said "network configuration incomplete".
public sealed class NetworkHelperTests
{
    [Fact]
    public void Network_helper_reports_bad_arguments_and_exits_without_crashing()
    {
        var app = typeof(App).Assembly.Location;
        Assert.True(File.Exists(app), app);
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add(app);
        info.ArgumentList.Add("--network");
        info.ArgumentList.Add("127.0.0.1");
        info.ArgumentList.Add("not-a-port");
        info.ArgumentList.Add("S-1-5-18");
        info.Environment["TIA_OPENNESS_NETWORK_NO_DIALOG"] = "1";
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(60_000)) { process.Kill(true); Assert.Fail("The network helper did not exit."); }
        // 1 = handled failure; an unhandled .NET exception would be 0xE0434352 (-532462766).
        Assert.Equal(1, process.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(stderr.Result), "The helper must report why it failed.");
        Assert.Equal(string.Empty, stdout.Result.Trim());
    }

    [Fact]
    public void Network_helper_rejects_a_wrong_argument_count()
    {
        var previous = Environment.GetEnvironmentVariable("TIA_OPENNESS_NETWORK_NO_DIALOG");
        Environment.SetEnvironmentVariable("TIA_OPENNESS_NETWORK_NO_DIALOG", "1");
        try { Assert.Equal(1, App.RunNetworkConfiguration(new[] { "--network", "127.0.0.1" })); }
        finally { Environment.SetEnvironmentVariable("TIA_OPENNESS_NETWORK_NO_DIALOG", previous); }
    }
}
