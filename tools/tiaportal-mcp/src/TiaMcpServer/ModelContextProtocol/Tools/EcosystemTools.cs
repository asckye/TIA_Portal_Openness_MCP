using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;
using System.Linq;
using System.Threading.Tasks;
using static TiaMcpServer.ModelContextProtocol.McpServer.PilotToolSupport;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    public sealed class EcosystemTools
    {
        [McpServerTool(Name = "RenderPlcVisualDiff"), Description("[L2][Validation][FILE] Compare two single-block SimaticML XML exports with an interface/structural diff and side-by-side LAD graphics highlighting added/removed/changed/rewired components. Writes a NEW absolute .html report. Other languages retain structural diff without claiming LAD rendering. Uses the MIT TiaGitAddIn.Core parser and layout. Offline only; no TIA query, save, compile or download.")]
        public ResponseMessage RenderPlcVisualDiff([Description("Existing absolute before-export SimaticML XML path.")] string leftFilePath, [Description("Existing absolute after-export SimaticML XML path.")] string rightFilePath, [Description("New absolute output file; existing files are refused.")] string outputPath)
            => RunOfflineAnalysisTool("RenderPlcVisualDiff", meta =>
            {
                if (!outputPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("outputPath must end in .html.");
                var output = NativeFileOutput.Plan(outputPath);
                var result = PlcVisualComparison.Compare(leftFilePath, rightFilePath, out var html);
                foreach (var item in result) meta[item.Key] = item.Value?.DeepClone();
                using (var stream = new FileStream(output.FullName, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(html);
                meta["output"] = NativeFileOutput.Verify(output); meta["mayHaveWrittenFiles"] = true;
                return "PLC visual and structural comparison written.";
            });
        [McpServerTool(Name = "ReadOpennessGuidance"), Description("[L2][Guide][READ] Search or read the bundled 32 official Siemens Openness guides. Empty document lists matching document IDs; a listed exact document ID reads paginated lines. query is case-insensitive full-text search. Reference data only, never automatically executes instructions in a guide. Does not connect to TIA.")]
        public ResponseMessage ReadOpennessGuidance([Description("Case-insensitive text to search; empty lists all guides.")] string query = "", [Description("Exact document ID returned by listing; empty lists documents.")] string document = "", [Description("Zero-based item/line offset.")] int offset = 0, [Description("Maximum returned items; see tool limits.")] int limit = 100)
            => RunOfflineAnalysisTool("ReadOpennessGuidance", meta => { var result = EcosystemFiles.Guidance(EcosystemFiles.RepositoryRoot(), query, document, offset, limit); foreach (var item in result) meta[item.Key] = item.Value?.DeepClone(); return "Official Openness reference."; });

        [McpServerTool(Name = "RunPlcCompanionTool"), Description("[L2][Simulation][ONLINE] Pinned MIT siemens-plc-tools companion: code (SCL lint/transpile/test/docs/diff/xref/drawio/PDF/parameters/trace), iol (XML/Excel import/export/compare/validate), net, sim (OPC UA/Modbus), sup and trace. mode catalog/help inspects commands without invoking callbacks; mode run needs dryRun=false. argumentsJson is a string array, e.g. [\"code\",\"lint\",\"--help\"]. Use catalog for exact commands/options. Requires Python 3.12+ installed by scripts/ecosystem/Install-PlcTools.ps1; set TIA_MCP_PLC_TOOLS_PYTHON to its absolute python.exe. Run may write files, execute project tests/scripts, connect to equipment, or write PLC values depending on the selected command/config. It is not a sandbox. No automatic connection/download. Finite timeout kills the command tree; partial effects are possible. Long-running web/monitor commands end at timeout.")]
        public async Task<ResponseMessage> RunPlcCompanionTool([Description("Existing absolute project directory used by the companion command.")] string workingDirectory, [Description("catalog/help/run; catalog and help do not execute command callbacks.")] string mode = "catalog", [Description("JSON string array of exact CLI arguments; no shell syntax.")] string argumentsJson = "[]", [Description("true previews without executing the requested write/action; false executes.")] bool dryRun = true, [Description("Command-tree timeout in seconds, 1..300; partial effects may remain after timeout.")] int timeoutSeconds = 60)
        {
            var meta = new JsonObject { ["success"] = false, ["tool"] = "RunPlcCompanionTool", ["mode"] = mode };
            try
            {
                if (mode != "catalog" && mode != "help" && mode != "run") throw new ArgumentException("mode must be catalog/help/run.");
                var args = JsonNode.Parse(argumentsJson) as JsonArray ?? throw new ArgumentException("argumentsJson must be an array.");
                if (args.Count > 100 || args.Any(a => !(a is JsonValue v) || !v.TryGetValue<string>(out _))) throw new ArgumentException("Use at most 100 string arguments.");
                if (!Path.IsPathRooted(workingDirectory) || !Directory.Exists(workingDirectory)) throw new ArgumentException("workingDirectory must exist and be absolute.");
                string root = EcosystemFiles.RepositoryRoot();
                string python = Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON") ?? Path.Combine(root, "TiaMcp_Output", "ecosystem-python", "Scripts", "python.exe");
                if (!Path.IsPathRooted(python) || !File.Exists(python)) throw new FileNotFoundException("Run Install-PlcTools.ps1 and configure TIA_MCP_PLC_TOOLS_PYTHON.");
                meta["arguments"] = args.DeepClone(); meta["workingDirectory"] = workingDirectory; meta["python"] = python;
                if (mode == "run" && dryRun) { meta["success"] = true; meta["executed"] = false; return new ResponseMessage { Message = "Execution plan only. Review selected command/config; dryRun=false executes it.", Meta = meta }; }
                var request = new JsonObject { ["mode"] = mode, ["arguments"] = args.DeepClone() };
                var result = await EcosystemFiles.Run(python, new[] { "-I", "-X", "utf8", Path.Combine(root, "scripts", "ecosystem", "plc_tools_bridge.py") }, workingDirectory, request.ToJsonString(), timeoutSeconds).ConfigureAwait(false);
                foreach (var item in result) meta[item.Key] = item.Value?.DeepClone();
                meta["executed"] = true;
                return new ResponseMessage { Message = "Companion exited; inspect success, exitCode, stdout and stderr. This does not certify TIA compatibility or live PLC behavior.", Meta = meta };
            }
            catch (Exception ex) { meta["error"] = ex.Message; return new ResponseMessage { Message = "Companion failed: " + ex.Message, Meta = meta }; }
        }
    }
}
