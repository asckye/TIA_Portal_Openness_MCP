using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TiaMcpServer.ModelContextProtocol
{
    public static class EcosystemFiles
    {
        public static string RepositoryRoot()
        {
            var configured = Environment.GetEnvironmentVariable("TIA_MCP_REPOSITORY_ROOT");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!Path.IsPathRooted(configured) || !File.Exists(Path.Combine(configured, "scripts", "ecosystem", "plc_tools_bridge.py")))
                    throw new DirectoryNotFoundException("TIA_MCP_REPOSITORY_ROOT must point to this source/distribution root.");
                return Path.GetFullPath(configured);
            }
            for (var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "scripts", "ecosystem", "plc_tools_bridge.py"))) return dir.FullName;
            throw new DirectoryNotFoundException("Companion files missing. Set TIA_MCP_REPOSITORY_ROOT to the source/distribution root.");
        }

        public static JsonObject Guidance(string root, string query, string document, int offset, int limit)
        {
            if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0; limit 1..500 lines/items.");
            var folder = Path.Combine(root, "reference", "siemens-openness", "skills");
            // Use an enumerated identifier, not an arbitrary caller-controlled filesystem path.
            var files = Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories)
                .Select(p => new { Path = p, Id = p.Substring(folder.Length + 1).Replace('\\', '/') }).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
            var result = new JsonObject { ["source"] = "siemens/tia-portal-ai-extensions", ["commit"] = "2119df978ffe26cf1436384b6d94549b28aa2264", ["referenceDataOnly"] = true };
            if (!string.IsNullOrEmpty(document))
            {
                var file = files.SingleOrDefault(p => p.Id == document) ?? throw new ArgumentException("Unknown document id. List first.");
                var lines = File.ReadAllLines(file.Path);
                result["document"] = document; result["content"] = string.Join("\n", lines.Skip(offset).Take(limit));
                result["total"] = lines.Length; result["offset"] = offset; result["dataComplete"] = offset + limit >= lines.Length;
                result["nextOffset"] = offset + limit < lines.Length ? (JsonNode)(offset + limit) : null;
                return result;
            }
            var matches = files.Where(f => string.IsNullOrEmpty(query) || f.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || File.ReadAllText(f.Path).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            result["documents"] = new JsonArray(matches.Skip(offset).Take(limit).Select(f => (JsonNode)new JsonObject { ["id"] = f.Id, ["lines"] = File.ReadLines(f.Path).Count() }).ToArray());
            result["total"] = matches.Count; result["dataComplete"] = offset + limit >= matches.Count;
            result["nextOffset"] = offset + limit < matches.Count ? (JsonNode)(offset + limit) : null;
            return result;
        }

        // Windows CommandLineToArgvW escaping. No cmd.exe/PowerShell or argument-string concatenation from callers.
        public static string QuoteArgument(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0) throw new ArgumentException("Invalid process argument.");
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
                else result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        public static async Task<JsonObject> Run(string executable, IEnumerable<string> arguments, string directory, string? input, int timeoutSeconds)
        {
            if (!Path.IsPathRooted(directory) || !Directory.Exists(directory)) throw new ArgumentException("workingDirectory must be an existing absolute directory.");
            if (timeoutSeconds < 1 || timeoutSeconds > 300) throw new ArgumentException("timeoutSeconds must be 1..300.");
            var start = new ProcessStartInfo(executable, string.Join(" ", arguments.Select(QuoteArgument)))
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            start.EnvironmentVariables["PYTHONUTF8"] = "1";
            var stdout = new StringBuilder(); var stderr = new StringBuilder();
            const int maxChars = 1024 * 1024;
            using (var process = new Process { StartInfo = start })
            {
                if (!process.Start()) throw new InvalidOperationException("Process did not start.");
                var stdoutTask = Drain(process.StandardOutput, stdout, maxChars);
                var stderrTask = Drain(process.StandardError, stderr, maxChars);
                if (input != null) await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
                process.StandardInput.Close();
                bool finished = await Task.Run(() => process.WaitForExit(timeoutSeconds * 1000)).ConfigureAwait(false);
                if (!finished)
                {
                    // Kill the timed-out command and its children, including pytest/web child processes.
                    using (var killer = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"), "/PID " + process.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                        if (killer != null) await Task.Run(() => killer.WaitForExit(5000)).ConfigureAwait(false);
                    if (!process.HasExited) process.Kill();
                }
                process.WaitForExit();
                bool[] truncated = await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                return new JsonObject { ["success"] = finished && process.ExitCode == 0, ["exitCode"] = process.ExitCode, ["timedOut"] = !finished,
                    ["stdout"] = stdout.ToString(), ["stderr"] = stderr.ToString(), ["outputTruncated"] = truncated.Any(t => t), ["dataComplete"] = finished && !truncated.Any(t => t),
                    ["effectsMayHaveOccurred"] = true };
            }
        }
        private static async Task<bool> Drain(StreamReader reader, StringBuilder result, int limit)
        {
            var buffer = new char[8192]; int read; bool truncated = false;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                int take = Math.Min(read, limit - result.Length);
                if (take < read) truncated = true;
                result.Append(buffer, 0, take);
            }
            return truncated;
        }
    }
}
