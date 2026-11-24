using System.Diagnostics;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

public sealed class VciExportFrameworkTests
{
    [Fact]
    public async Task Refused_exports_are_checked_on_the_worker_framework()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "vci-net48", "TiaMcp.VciExport.Net48.Tests.exe");
        using var process = Process.Start(new ProcessStartInfo(file) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TestDeadlines.Hang);
        string summary = await output;
        Assert.True(process.ExitCode == 0, summary + Environment.NewLine + await errors);
        Assert.Contains(".NET Framework 4.8", summary);
        var lines = summary.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        foreach (string scenario in new[] { "unchanged", "changed target", "changed unrelated file", "junction", "unreadable subfolder" })
            Assert.Contains("PASS " + scenario, lines);
    }
}
