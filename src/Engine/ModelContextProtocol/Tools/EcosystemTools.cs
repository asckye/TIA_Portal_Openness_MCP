using ModelContextProtocol.Protocol;
using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;
using System.Linq;
using System.Threading.Tasks;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    public sealed class EcosystemTools
    {
        [McpServerTool(Name = "RenderPlcBlock"), Description("[L2][PLC-Software][FILE] Render one existing SimaticML block export as a self-contained static HTML page with ladder SVG, interface and code. Offline local files only. Writes a new absolute .html path; never overwrites. No Siemens calls or online power-flow display.")]
        public CallToolResult RenderPlcBlock(
            [Description("Existing absolute local SimaticML XML file containing exactly one block.")] string inputPath,
            [Description("New absolute local .html output path in an existing directory; existing files are refused.")] string outputPath)
            => Render(inputPath, outputPath, false);

        [McpServerTool(Name = "RenderPlcProgramAtlas"), Description("[L2][PLC-Software][FILE] Render a SimaticML block XML file or recursive block export directory into one self-contained static HTML program atlas. Contents, call links and called-by counts cover only included blocks. Offline local file output, no Siemens calls. Requires a new absolute .html path; never overwrites.")]
        public CallToolResult RenderPlcProgramAtlas(
            [Description("Existing absolute local SimaticML XML file or directory containing block exports; at most 256 files and 512 networks.")] string inputPath,
            [Description("New absolute local .html output path in an existing directory; existing files are refused.")] string outputPath)
            => Render(inputPath, outputPath, true);

        private static CallToolResult Render(string inputPath, string outputPath, bool atlas)
        {
            var mapped = TiaMcp.Logic.V4.McpResult.From(PlcProgramRenderer.Write(inputPath, outputPath, atlas,
                McpServer.ReleaseKey, InvocationJournal.CorrelationId));
            return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
        }

        [McpServerTool(Name = "RenderPlcVisualDiff"), Description("[L2][Validation][FILE] Compare two single-block SimaticML XML exports with an interface/structural diff and side-by-side LAD graphics highlighting added/removed/changed/rewired components. Writes a NEW absolute .html report. Other languages retain structural diff without claiming LAD rendering. Uses the MIT TiaGitAddIn.Core parser and layout. Offline only; no TIA query, save, compile or download.")]
        public CallToolResult RenderPlcVisualDiffV4(
            [Description("Existing absolute before-export SimaticML XML path.")] string leftFilePath,
            [Description("Existing absolute after-export SimaticML XML path.")] string rightFilePath,
            [Description("New absolute output file; existing files are refused.")] string outputPath)
            => OfflineContracts.Run("RenderPlcVisualDiff", () => RenderPlcVisualDiff(leftFilePath, rightFilePath, outputPath), writes: true, current: false);

        public ResponseMessage RenderPlcVisualDiff([Description("Existing absolute before-export SimaticML XML path.")] string leftFilePath, [Description("Existing absolute after-export SimaticML XML path.")] string rightFilePath, [Description("New absolute output file; existing files are refused.")] string outputPath)
            => OfflineToolExecution.RunOfflineAnalysisTool("RenderPlcVisualDiff", meta =>
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
        [McpServerTool(Name = "GetOpennessGuidance"), Description("[L2][Guide][READ] Search or read the bundled 32 official Siemens Openness guides. Empty document lists matching document IDs; a listed exact document ID reads paginated lines. query is case-insensitive full-text search. Reference data only, never automatically executes instructions in a guide. Does not connect to TIA.")]
        public CallToolResult ReadOpennessGuidanceV4(
            [Description("Case-insensitive text to search; empty lists all guides.")] string query = "",
            [Description("Exact document ID returned by listing; empty lists documents.")] string document = "",
            [Description("Zero-based item/line offset.")] int offset = 0,
            [Description("Maximum returned items; see tool limits.")] int limit = 100)
            => OfflineContracts.Run("GetOpennessGuidance", () => ReadOpennessGuidance(query, document, offset, limit), writes: false, current: false, offset: offset, limit: limit);

        public ResponseMessage ReadOpennessGuidance([Description("Case-insensitive text to search; empty lists all guides.")] string query = "", [Description("Exact document ID returned by listing; empty lists documents.")] string document = "", [Description("Zero-based item/line offset.")] int offset = 0, [Description("Maximum returned items; see tool limits.")] int limit = 100)
            => OfflineToolExecution.RunOfflineAnalysisTool("GetOpennessGuidance", meta => { var result = EcosystemFiles.Guidance(EcosystemFiles.RepositoryRoot(), query, document, offset, limit); foreach (var item in result) meta[item.Key] = item.Value?.DeepClone(); return "Official Openness reference."; });

        [McpServerTool(Name = "RunPlcCompanionTool"), Description("[L2][Simulation][ONLINE] Pinned MIT siemens-plc-tools companion: code (SCL lint/transpile/test/docs/diff/xref/drawio/PDF/parameters/trace), iol (XML/Excel import/export/compare/validate), net, sim (OPC UA/Modbus), sup and trace. mode catalog/help inspects commands without invoking callbacks; mode run needs dryRun=false. arguments is a string array, e.g. [\"code\",\"lint\",\"--help\"]. Use catalog for exact commands/options. Requires Python 3.12+ and dependencies installed with `tia install-plc-tools`; set TIA_MCP_PLC_TOOLS_PYTHON to its absolute python.exe. Run may write files, execute project tests/scripts, connect to equipment, or write PLC values depending on the selected command/config. It is not a sandbox. No automatic connection/download. Finite timeout kills the command tree; partial effects are possible. Long-running web/monitor commands end at timeout.")]
        public Task<CallToolResult> RunPlcCompanionToolV4(
            [Description("Existing absolute project directory used by the companion command.")] string workingDirectory,
            [Description("catalog/help/run; catalog and help do not execute command callbacks.")] string mode = "catalog",
            [Description("arguments: string[]. Supply the structured value directly; exact camelCase fields, no null or JSON string encoding.")] string[]? arguments = null,
            [Description("true previews without executing the requested write/action; false executes.")] bool dryRun = true,
            [Description("Command-tree timeout in seconds, 1..300; partial effects may remain after timeout.")] int timeoutSeconds = 60)
            => OfflineContracts.RunAsync("RunPlcCompanionTool", () => RunPlcCompanionTool(workingDirectory, mode, OfflineContracts.Names(arguments, "[]"), dryRun, timeoutSeconds), writes: mode == "run" && !dryRun, current: false);

        public async Task<ResponseMessage> RunPlcCompanionTool([Description("Existing absolute project directory used by the companion command.")] string workingDirectory, [Description("catalog/help/run; catalog and help do not execute command callbacks.")] string mode = "catalog", [Description("JSON string array of exact CLI arguments; no shell syntax.")] string argumentsJson = "[]", [Description("true previews without executing the requested write/action; false executes.")] bool dryRun = true, [Description("Command-tree timeout in seconds, 1..300; partial effects may remain after timeout.")] int timeoutSeconds = 60)
        {
            var meta = ResponseMeta.Unstamped(false, ("tool", "RunPlcCompanionTool"), ("mode", mode));
            try
            {
                if (mode != "catalog" && mode != "help" && mode != "run") throw new ArgumentException("mode must be catalog/help/run.");
                var args = JsonNode.Parse(argumentsJson) as JsonArray ?? throw new ArgumentException("argumentsJson must be an array.");
                if (args.Count > 100 || args.Any(a => !(a is JsonValue v) || !v.TryGetValue<string>(out _))) throw new ArgumentException("Use at most 100 string arguments.");
                if (!Path.IsPathRooted(workingDirectory) || !Directory.Exists(workingDirectory)) throw new ArgumentException("workingDirectory must exist and be absolute.");
                string root = EcosystemFiles.RepositoryRoot();
                string bridge = TiaOpenness.Shared.BundleLayout.RequirePath(root, "scripts/ecosystem/plc_tools_bridge.py");
                string python = TiaOpenness.Shared.DataLocations.Current.EcosystemPythonExecutable;
                if (!Path.IsPathRooted(python) || !File.Exists(python)) throw new FileNotFoundException("Run `tia install-plc-tools` and configure TIA_MCP_PLC_TOOLS_PYTHON.");
                meta["arguments"] = args.DeepClone(); meta["workingDirectory"] = workingDirectory; meta["python"] = python;
                // envelope: legacy-single-verdict
                if (mode == "run" && dryRun) { meta["success"] = true; meta["executed"] = false; return new ResponseMessage { Message = "Execution plan only. Review selected command/config; dryRun=false executes it.", Meta = meta }; }
                var request = new JsonObject { ["mode"] = mode, ["arguments"] = args.DeepClone() };
                meta["dispatchStarted"] = true;
                var result = await EcosystemFiles.Run(python, new[] { "-I", "-X", "utf8", bridge }, workingDirectory, request.ToJsonString(), timeoutSeconds).ConfigureAwait(false);
                foreach (var item in result) meta[item.Key] = item.Value?.DeepClone();
                meta["executed"] = true;
                return new ResponseMessage { Message = "Companion exited; inspect success, exitCode, stdout and stderr. This does not certify TIA compatibility or live PLC behavior.", Meta = meta };
            }
            catch (TiaOpenness.Shared.BundleResourceUnavailableException) { throw; }
            catch (TiaOpenness.Shared.DataLocationIOException) { throw; }
            catch (Exception ex) { meta["error"] = ex.Message; return new ResponseMessage { Message = "Companion failed: " + ex.Message, Meta = meta }; }
        }
    }
}
