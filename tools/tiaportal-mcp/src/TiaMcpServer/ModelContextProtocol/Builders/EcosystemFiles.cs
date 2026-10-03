using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            var provenance = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference", "siemens-openness", "UPSTREAM.json")))!.AsObject();
            var result = new JsonObject { ["source"] = "siemens/tia-portal-ai-extensions", ["commit"] = provenance["commit"]!.DeepClone(), ["referenceDataOnly"] = true };
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

        public static string QuoteArgument(string value) => TiaOpenness.Shared.ProcessArguments.Quote(value);

        public static async Task<JsonObject> Run(string executable, IEnumerable<string> arguments, string directory, string? input, int timeoutSeconds)
        {
            var result = await TiaOpenness.Shared.LocalProcess.Run(executable, arguments, directory, input, timeoutSeconds,
                environment: new Dictionary<string, string> { ["PYTHONIOENCODING"] = "utf-8", ["PYTHONUTF8"] = "1" }).ConfigureAwait(false);
            return new JsonObject { ["success"] = result.Success, ["exitCode"] = result.ExitCode, ["timedOut"] = result.TimedOut,
                ["stdout"] = result.Stdout, ["stderr"] = result.Stderr, ["outputTruncated"] = result.OutputTruncated,
                ["dataComplete"] = result.DataComplete, ["effectsMayHaveOccurred"] = true };
        }
    }
}
