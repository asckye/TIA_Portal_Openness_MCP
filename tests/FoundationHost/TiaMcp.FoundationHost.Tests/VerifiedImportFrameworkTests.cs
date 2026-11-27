using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class VerifiedImportFrameworkTests
{
    [Fact]
    public async Task Same_style_worker_matrix_passes_on_dotnet_framework()
    {
        string source = Path.Combine(AppContext.BaseDirectory, "verified-net48");
        string scratch = Path.Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build");
        string directory = Path.Combine(scratch, "matrix-" + Guid.NewGuid().ToString("N")).PadRight(160, 'x');
        string file = Path.Combine(directory, "TiaMcp.VerifiedImport.Net48.Tests.exe");
        Process? process = null;
        Exception? failure = null;
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string sourceFile in Directory.EnumerateFiles(source))
                File.Copy(sourceFile, Path.Combine(directory, Path.GetFileName(sourceFile)));
            process = Process.Start(new ProcessStartInfo(file) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TestDeadlines.Hang);
            string summary = await output;
            Assert.True(process.ExitCode == 0, summary + Environment.NewLine + await errors);
            Assert.Contains(".NET Framework 4.8", summary);
            Assert.Contains("0 failed, 0 skipped", summary);
            var count = System.Text.RegularExpressions.Regex.Match(summary, @"(?m)^(\d+) cases,");
            Assert.True(count.Success && int.Parse(count.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 121, summary);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try
            {
                using (process)
                {
                    if (process is { HasExited: false }) await StopFixture(process, file);
                }
                await DeleteFixtureDirectory(directory);
            }
            catch (Exception cleanup) when (failure != null) { failure.Data["matrixCleanup"] = cleanup.ToString(); }
        }
    }

    private static async Task StopFixture(Process process, string file)
    {
        try
        {
            Assert.Equal(file, process.MainModule!.FileName, ignoreCase: true);
            process.Kill();
        }
        catch (Exception error) when ((error is InvalidOperationException or Win32Exception) && process.HasExited)
        { /* swallow(cleanup): the owned fixture exited while checking its image or terminating it. */ }
        await process.WaitForExitAsync();
    }

    private static async Task DeleteFixtureDirectory(string directory)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Delete(directory, true); return; }
            // Windows can signal process exit before releasing the executable's image mapping.
            catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && attempt < 20)
            { await Task.Delay(50); }
        }
    }
}
