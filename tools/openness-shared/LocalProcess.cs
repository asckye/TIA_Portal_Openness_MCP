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
                if (!ChildProcessInput.Start(process)) throw new InvalidOperationException("Process did not start.");
                var stdoutTask = Drain(process.StandardOutput, stdout, maxOutputCharacters);
                var stderrTask = Drain(process.StandardError, stderr, maxOutputCharacters);
#if NETFRAMEWORK
                // Framework Process.Dispose/Close only nulls the stream fields, so leaving
                // its original writer untouched cannot flush it during process cleanup.
                using (var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
#else
                using (var stdin = process.StandardInput)
#endif
                {
                    stdin.AutoFlush = false;
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

    internal static class ChildProcessInput
    {
#if NETFRAMEWORK
        private static readonly object StartSync = new object();
#endif

        internal static bool Start(Process process)
        {
#if NETFRAMEWORK
            lock (StartSync)
            {
                var previous = Console.InputEncoding;
                if (previous.GetPreamble().Length == 0) return process.Start();
                bool changed = false;
                try
                {
                    // P2-06: Framework AutoFlush emits the preamble inside Process.Start,
                    // before a caller can replace StandardInput with a UTF-8 writer.
                    // Setting Console.InputEncoding also discards Console.In and its buffered input, so a
                    // host that reads Console.In concurrently must install a BOM-free InputEncoding at
                    // startup (the engine's Program.cs does), which makes this switch a no-op there.
                    try { Console.InputEncoding = new UTF8Encoding(false); changed = true; }
                    catch (Exception) /* swallow(env-probe): a host without a console must still attempt child process startup */ { /* A host without a console must still attempt process startup. */ }
                    return process.Start();
                }
                finally
                {
                    if (changed)
                    {
                        try { Console.InputEncoding = previous; }
                        catch (Exception) /* swallow(teardown): the console may disappear before its encoding is restored; preserve the startup outcome */ { /* The console may have disappeared; preserve the process startup outcome. */ }
                    }
                }
            }
#else
            process.StartInfo.StandardInputEncoding = new UTF8Encoding(false);
            return process.Start();
#endif
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
