#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TiaMcp.Logic.ModelContextProtocol;

namespace TiaMcp.Engine.Tests.Shared
{
    // check-all publishes a verified runtime before tests. Standalone fixtures
    // may invoke an existing release-tool assembly, but never build that tool.
    public static class DecoderRuntimeFixture
    {
        internal const string RuntimeVariable = "TIA_MCP_DECODER_RUNTIME";
        private static readonly Lazy<string> Runtime = new(Prepare);

        public static string Ensure() => Runtime.Value;

        private static string Prepare()
        {
            string? prepared = Environment.GetEnvironmentVariable(RuntimeVariable);
            if (prepared != null) return RequirePrepared(prepared);
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "build-tools/release/TiaMcp.ReleaseTool.csproj"))) root = root.Parent;
            if (root == null) throw new DirectoryNotFoundException("Decoder fixture must run inside its source checkout.");
            return Prepare(root.FullName, null, Environment.GetEnvironmentVariable("TIA_MCP_TEST_DECODER_PREPARE_TOOL"));
        }

        internal static string Prepare(string root, string? prepared, string? tool)
        {
            if (prepared != null) return RequirePrepared(prepared);
            tool ??= Path.Combine(root, "build-tools/release/bin/Release/net10.0/TiaMcp.ReleaseTool.dll");
            if (!Path.IsPathFullyQualified(tool) || !File.Exists(tool))
                throw new InvalidOperationException("Decoder runtime is not prepared. Run 'dotnet run --project build-tools/release -- prepare-decoder-python -Python <absolute-pinned-python.exe>' first, or set " + RuntimeVariable + " to its absolute output directory.");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_EXE") ?? "dotnet")
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(tool);
            start.ArgumentList.Add("prepare-decoder-python"); start.ArgumentList.Add("-Reuse");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start decoder runtime preparation.");
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit(); Task.WaitAll(output, error);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Decoder fixture preparation failed: " + output.Result + error.Result);
            return RequirePrepared(Path.Combine(root, DecoderRuntime.RelativeDirectory));
        }

        private static string RequirePrepared(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
                throw new InvalidOperationException(RuntimeVariable + " must name the absolute prepared decoder runtime directory.");
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            string root = Path.GetFullPath(Path.Combine(directory, "../.."));
            try
            {
                string verified = Path.GetDirectoryName(DecoderRuntime.Require(root))!;
                if (!string.Equals(directory, verified, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new DecoderRuntimeException();
                return verified;
            }
            catch (DecoderRuntimeException error)
            { throw new InvalidOperationException("Invalid prepared decoder runtime in " + RuntimeVariable + ": " + directory, error); }
        }
    }
}
