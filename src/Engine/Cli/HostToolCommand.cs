using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer
{
    internal static class HostToolCommand
    {
        // Retired engine CLI entry points forward their offline/report work to net10.
        internal static T Run<T>(string command, JsonObject arguments)
        {
            var root = TiaOpenness.Shared.BundleLayout.RequireRoot(AppContext.BaseDirectory);
            var host = TiaOpenness.Shared.BundleLayout.EngineExecutablePath(root, McpServer.ReleaseKey, AppContext.BaseDirectory);
            var worker = Assembly.GetExecutingAssembly().Location;
            var catalog = Path.Combine(Path.GetDirectoryName(worker)!, "tool-catalog.json");
            if (!File.Exists(catalog)) TiaMcpServer.Cli.ToolCatalogExport.Write(catalog);
            // net48 has no ArgumentList; stdin carries JSON and the selected command.
            var start = new ProcessStartInfo(host, "--bundle-root " + Quote(root) + " --release-key " + McpServer.ReleaseKey
                + " --engine-worker " + Quote(worker) + " --engine-catalog " + Quote(catalog) + " --host-command " + command) {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Foundation host.");
            var errors = child.StandardError.ReadToEndAsync();
            child.StandardInput.WriteLine(arguments.ToJsonString()); child.StandardInput.Close();
            var output = child.StandardOutput.ReadToEnd(); child.WaitForExit();
            if (child.ExitCode != 0) throw new InvalidOperationException(errors.GetAwaiter().GetResult());
            return JsonSerializer.Deserialize<T>(output)!;
        }
        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
