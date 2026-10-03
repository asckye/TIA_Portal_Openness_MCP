#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TiaOpenness.Shared
{
    /// <summary>Process I/O shared by the desktop and MCP adapters; no transport or Siemens dependency.</summary>
    public static class LocalProcess
    {
        public static async Task<LocalProcessResult> Run(string executable, IEnumerable<string> arguments,
            string directory, string input, int timeoutSeconds, int maxOutputCharacters = 1024 * 1024,
            IReadOnlyDictionary<string, string> environment = null)
        {
            if (!Path.IsPathRooted(directory) || !Directory.Exists(directory))
                throw new ArgumentException("workingDirectory must be an existing absolute directory.");
            if (timeoutSeconds < 1 || timeoutSeconds > 300) throw new ArgumentException("timeoutSeconds must be 1..300.");
            if (maxOutputCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxOutputCharacters));
            var start = new ProcessStartInfo(executable, string.Join(" ", arguments.Select(ProcessArguments.Quote)))
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            if (environment != null)
                foreach (var item in environment) start.EnvironmentVariables[item.Key] = item.Value;
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using (var process = new Process { StartInfo = start })
            {
                if (!process.Start()) throw new InvalidOperationException("Process did not start.");
                var stdoutTask = Drain(process.StandardOutput, stdout, maxOutputCharacters);
                var stderrTask = Drain(process.StandardError, stderr, maxOutputCharacters);
                using (var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
                {
                    if (input != null) await stdin.WriteAsync(input).ConfigureAwait(false);
                }
                bool finished = await Task.Run(() => process.WaitForExit(timeoutSeconds * 1000)).ConfigureAwait(false);
                if (!finished)
                {
                    if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    {
                        // The command owns this process tree; preserve the MCP runner's timeout cleanup.
                        using (var killer = Process.Start(new ProcessStartInfo(
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"),
                            "/PID " + process.Id + " /T /F")
                            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                            if (killer != null) await Task.Run(() => killer.WaitForExit(5000)).ConfigureAwait(false);
                    }
                    if (!process.HasExited) process.Kill();
                }
                process.WaitForExit();
                bool[] truncated = await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                return new LocalProcessResult(process.ExitCode, !finished, stdout.ToString(), stderr.ToString(), truncated.Any(t => t));
            }
        }

        private static async Task<bool> Drain(StreamReader reader, StringBuilder result, int limit)
        {
            var buffer = new char[8192];
            int read;
            bool truncated = false;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                int take = Math.Min(read, limit - result.Length);
                if (take < read) truncated = true;
                result.Append(buffer, 0, take);
            }
            return truncated;
        }
    }

    public sealed class LocalProcessResult
    {
        public int ExitCode { get; }
        public bool TimedOut { get; }
        public string Stdout { get; }
        public string Stderr { get; }
        public bool OutputTruncated { get; }
        public bool Success => !TimedOut && ExitCode == 0;
        public bool DataComplete => !TimedOut && !OutputTruncated;

        internal LocalProcessResult(int exitCode, bool timedOut, string stdout, string stderr, bool truncated)
        {
            ExitCode = exitCode; TimedOut = timedOut; Stdout = stdout; Stderr = stderr; OutputTruncated = truncated;
        }
    }
}
