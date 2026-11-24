using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class VerifiedImportFrameworkTests
{
    [Fact]
    public async Task Same_style_worker_matrix_passes_on_dotnet_framework()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "verified-net48", "TiaMcp.VerifiedImport.Net48.Tests.exe");
        using var process = Process.Start(new ProcessStartInfo(file) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TestDeadlines.Hang);
        string summary = await output;
        Assert.True(process.ExitCode == 0, summary + Environment.NewLine + await errors);
        Assert.Contains(".NET Framework 4.8", summary);
        Assert.Contains("0 failed, 0 skipped", summary);
    }
}
