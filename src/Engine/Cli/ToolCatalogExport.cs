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
            var tools = McpServer.GetAllTools();
            var methods = McpServer.AllToolMethods();
            var signature = typeof(McpServer).GetMethod("RenderSignature", BindingFlags.NonPublic | BindingFlags.Static)!;
            var catalog = new {
                formatVersion = 1, release = McpServer.ReleaseKey,
                workerSha256 = Worker.EngineWorkerHost.Hash(Assembly.GetExecutingAssembly().Location),
                tools = tools.Select(t => t.ProtocolTool).ToArray(),
                liteTools = McpServer.GetLiteTools().Select(t => t.ProtocolTool.Name).ToArray(),
                behaviorCapabilities = BehaviorCapabilities.Table(typeof(McpServer).Assembly, McpServer.ReleaseKey),
                serverInstructions = McpGuides.ServerInstructions,
                descriptors = tools.Select(t => Describe(t.ProtocolTool.Name, methods[t.ProtocolTool.Name], signature)).ToArray()
            };
            File.WriteAllText(Path.GetFullPath(path), JsonSerializer.Serialize(catalog, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions), new UTF8Encoding(false));
        }

        // Standalone builder seam: P7-02 can replace this without changing the wire format.
        private static object Describe(string name, MethodInfo method, MethodInfo signature)
        {
            var classification = method.GetCustomAttribute<ToolClassificationAttribute>()?.Value ?? ToolMetadata.Find(name)!;
            var dryRun = method.GetParameters().FirstOrDefault(p => p.Name == "dryRun" && p.ParameterType == typeof(bool));
            return new {
                name, rawDescription = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "",
                classification = new { level = classification.Layer, domain = classification.Domain, operation = classification.Operation,
                    batchRead = classification.BatchRead, batchWrite = classification.BatchWrite },
                signature = (string)signature.Invoke(null, new object[] { name, method })!,
                parameters = McpServer.SpecsOf(method).Select(s => {
                    var parameter = method.GetParameters().Single(p => p.Name == s.Name);
                    return new { name = s.Name, friendlyType = s.Kind, clrType = parameter.ParameterType.FullName, required = s.Required,
                        defaultJson = s.DefaultText, defaultText = parameter.HasDefaultValue ? parameter.DefaultValue?.ToString() : null,
                        description = s.Description, synthesized = s.Synthesized, allowedValues = s.AllowedValues };
                }).ToArray(),
                dryRun = new { present = dryRun != null, @default = dryRun?.DefaultValue as bool? },
                candidateFamily = method.GetCustomAttribute<BehaviorCandidateAttribute>()?.Family,
                execution = name is "GetExportContent" or "ListExportHandles" || ToolTaxonomy.IsSafeWithoutTia(name) ? "host" : "worker"
            };
        }
    }
}
