using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Cli
{
    internal static class ToolCatalogExport
    {
        internal static void Write(string path)
        {
            var tools = McpServer.GetAllTools().Where(t => !TiaMcp.Adapters.Contracts.PortedFamilies.Contains(t.ProtocolTool.Name)).ToArray();
            var catalog = new {
                formatVersion = 1, release = McpServer.ReleaseKey,
                workerSha256 = Worker.EngineWorkerHost.Hash(Assembly.GetExecutingAssembly().Location),
                tools = tools.Select(t => t.ProtocolTool).ToArray(),
                liteTools = McpServer.RuntimeProfileEntries().Where(row => row!["profiles"]!.AsArray().Any(p => (string?)p == "lite"))
                    .Select(row => (string)row!["currentName"]!).OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                behaviorCapabilities = BehaviorCapabilities.Table(typeof(McpServer).Assembly, McpServer.ReleaseKey),
                serverInstructions = McpGuides.ServerInstructions,
                descriptors = tools.Select(t => Describe(McpServer.CatalogView.All[t.ProtocolTool.Name])).ToArray(),
                unavailableTools = McpServer.CatalogView.IncludingUnavailable.Values
                    .Where(t => !McpServer.CatalogView.All.ContainsKey(t.Name))
                    .Select(t => new { tool = t.Tool, descriptor = Describe(t) }).ToArray()
            };
            File.WriteAllText(Path.GetFullPath(path), JsonSerializer.Serialize(catalog, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions), new UTF8Encoding(false));
        }

        private static object Describe(ToolDescriptor tool)
        {
            return new {
                name = tool.Name, rawDescription = tool.RawDescription, classification = tool.Classification,
                signature = tool.Signature, parameters = tool.Parameters,
                dryRun = new { present = tool.DryRun.Present, @default = tool.DryRun.Present ? (bool?)tool.DryRun.Default : null },
                candidateFamily = tool.CandidateFamily, execution = tool.Execution
            };
        }
    }
}
