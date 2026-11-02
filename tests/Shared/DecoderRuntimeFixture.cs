#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace TiaMcp.Engine.Tests.Shared
{
    // The same release command validates the configured pin and the receipt. A
    // check-all build publishes it first; standalone fixtures prepare it on demand.
    public static class DecoderRuntimeFixture
    {
        private static readonly Lazy<string> Runtime = new(Prepare);

        public static string Ensure() => Runtime.Value;

        private static string Prepare()
        {
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "build-tools/release/TiaMcp.ReleaseTool.csproj"))) root = root.Parent;
            if (root == null) throw new DirectoryNotFoundException("Decoder fixture must run inside its source checkout.");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_EXE") ?? "dotnet")
            {
                WorkingDirectory = root.FullName, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            string? tool = Environment.GetEnvironmentVariable("TIA_MCP_TEST_DECODER_PREPARE_TOOL");
            if (!string.IsNullOrEmpty(tool)) start.ArgumentList.Add(tool);
            else
            {
                foreach (string argument in new[] { "run", "--project", Path.Combine(root.FullName, "build-tools/release"), "-c", "Release", "--" })
                    start.ArgumentList.Add(argument);
            }
            start.ArgumentList.Add("prepare-decoder-python"); start.ArgumentList.Add("-Reuse");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start decoder runtime preparation.");
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit(); Task.WaitAll(output, error);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Decoder fixture preparation failed: " + output.Result + error.Result);
            return Path.Combine(root.FullName, "runtime/decoder-python");
        }
    }
}
