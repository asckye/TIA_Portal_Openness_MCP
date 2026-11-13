using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens;
using static TiaMcpServer.ModelContextProtocol.McpServer;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcOfflineTools
    {
        [McpServerTool(Name = "AnalyzePlcReferences"),
                Description("[L2][Validation][OFFLINE] Query supplied exports without native CrossReferenceService. action " +
                "summary/callers/callees/callPaths/unreachable/references. Recursively indexes single-block SimaticML XML (max 2000 files / 64 MiB); " +
                "SCL/S7DCL and invalid files are reported unindexed. target is exact block name or relative file ID; references takes a quoted component " +
                "path e.g. \"Motor\".\"Run\". Uses explicit CallInfo and global Symbol nodes only. Reports missing/ambiguous callees, cycles, truncation and " +
                "failures; coverageComplete and safeToDelete are always false. An empty result is not proof of no references. No TIA calls or writes.")]
        public CallToolResult AnalyzePlcReferencesV4(
            [Description("Absolute directory of exports from one PLC/scope, scanned recursively.")] string directory,
            [Description("summary | callers | callees | callPaths | unreachable | references. summary, callers, callees, callPaths, unreachable or " +
                "references.")] string action =
                "summary",
            [Description("Exact block name or relative file ID; references uses quoted symbol components.")] string target = "",
            [Description("Maximum call-path edge depth, 1..50.")] int maxDepth = 10,
            [Description("Zero-based result offset.")] int offset = 0,
            [Description("Page size, 1..500.")] int limit = 100)
            => PlcToolContract.Run("AnalyzePlcReferences", false, false, () => AnalyzePlcReferences(directory, action, target, maxDepth, offset, limit));

        public ResponseMessage AnalyzePlcReferences(
            string directory,
            string action = "summary",
            string target = "",
            int maxDepth = 10,
            int offset = 0,
            int limit = 100)
            => OfflineToolExecution.RunOfflineAnalysisTool("AnalyzePlcReferences", meta => {
                meta["data"] = PlcOfflineReferences.Analyze(directory, action, target, maxDepth, offset, limit);
                meta["dataComplete"] = false;
                return "Partial offline reference query completed. Inspect failures, unresolvedCalls and scope; never use it alone to justify deletion.";
            });

        [McpServerTool(Name = "PatchPlcBlockDocument"),
                Description("[L2][PLC-Builders][FILE] Patch an exported single-block or single-UDT SimaticML file into a NEW file, preserving all unrelated XML. Requires " +
                "expectedDocumentHash from GetPlcBlockEditCapabilities. changes array: setBlockText(field Title/Comment,culture,expectedValue,value), " +
                "setNetworkText(same plus zero-based networkIndex), setMemberStartValue(section,memberPath slash-separated,expectedValue,value), " +
                "setMemberComment(section,memberPath,culture,expectedValue,value), e.g. Static/Motor/Speed in zh-CN with expectedValue=\"\". " +
                "StartValue and block/network text must exist; member comments may be added or removed. Use a culture contained in the export, or an explicit " +
                "culture when it contains none; no guessed defaults, library-connected blocks or read-only members. UDTs return with ImportPlcType overwrite=true; " +
                "overwrite makes dependent DBs and blocks inconsistent until compiled. " +
                "Default dryRun=true previews; false writes outputPath. No native calls/import/compile; input remains unchanged.")]
        public CallToolResult PatchPlcBlockDocumentV4(
            [Description("Absolute existing single-block or single-UDT SimaticML XML file.")] string filePath,
            [Description("JSON array of objects with action=setBlockText/setNetworkText/setMemberStartValue/setMemberComment, exact target fields, expectedValue and value. " +
                "The selector key is action. Example: [{\"action\":\"setMemberComment\",\"section\":\"Static\",\"memberPath\":\"Motor/Speed\",\"culture\":\"zh-CN\"," +
                "\"expectedValue\":\"\",\"value\":\"Motor speed\"}]. UDT members use section=None; empty value removes that culture's comment.")] BlockEdit[] changes,
            [Description("documentFingerprint returned by GetPlcBlockEditCapabilities for this input.")] string expectedDocumentHash,
            [Description("New absolute output file; existing parent directory required when dryRun=false.")] string outputPath = "",
            [Description("Preview only by default; false writes a new file, never imports into TIA.")] bool dryRun = true)
            => PlcToolContract.Run("PatchPlcBlockDocument", !dryRun, false, () =>
                PatchPlcBlockDocument(filePath, V4Json.Serialize(changes), expectedDocumentHash, outputPath, dryRun));

        public ResponseMessage PatchPlcBlockDocument(
            string filePath,
            string changesJson,
            string expectedFingerprint,
            string outputPath = "",
            bool dryRun = true)
            => OfflineToolExecution.RunOfflineAnalysisTool("PatchPlcBlockDocument", meta => {
                var edited = PlcDocumentEditing.Patch(PlcDocumentEditing.Read(filePath), changesJson, expectedFingerprint);
                meta["dryRun"] = dryRun; meta["result"] = PlcDocumentEditing.Inspect(edited);
                meta["nativeImportValidated"] = false;
                if (!dryRun) {
                    if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("New absolute outputPath required.");
                    var output = Siemens.NativeFileOutput.Plan(outputPath);
                    using (var stream = new FileStream(output.FullName, FileMode.CreateNew, FileAccess.Write))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(true))) writer.Write(edited);
                    meta["output"] = Siemens.NativeFileOutput.Verify(output);
                }
                return dryRun ? "Offline patch preview validated. No file or project changed."
                    : "Patched document written; native import/compilation remain unverified.";
            });

        [McpServerTool(Name = "WritePlcSclSourceFile"),
                Description("[L1][PLC-Software][OFFLINE] Write complete SCL text to a local .scl file without connecting to TIA Portal. " +
                "Plain ASCII is written without a BOM for ImportPlcExternalSource then GenerateBlocksFromExternalSource. Other text uses UTF-8 " +
                "with BOM to avoid Chinese mojibake; the tool importers refuse it. Import it manually in TIA: project tree → 'External source files' → " +
                "'Add new external file', then right-click the source → 'Generate blocks from source'. This is also the fallback when XML block " +
                "import rejects another release's SimaticML tokens. sclContent must contain complete FUNCTION_BLOCK, FUNCTION, DATA_BLOCK or TYPE " +
                "declarations. Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance.")]
        public CallToolResult WritePlcSclSourceFileV4(
            [Description("sclContent: the full SCL source text (complete FUNCTION_BLOCK / FUNCTION / DATA_BLOCK / TYPE declarations). This is written " +
                "verbatim; plain ASCII omits the BOM for tool import, other text uses UTF-8 BOM and requires manual TIA import.")] string sclContent,
            [Description("outputPath: .scl only target file path. If a directory is given (or the path has no extension), the file is named after the first " +
                "block found in the source. Empty means a temp file under %TEMP%\\tia_mcp_scl.")] string outputPath =
                "")
        {
            return PlcExchangeContract.Run("WritePlcSclSourceFile", () => WritePlcSclSourceFile(sclContent, outputPath), write: true, current: true);
        }

        public ResponseMessage WritePlcSclSourceFile(
            [Description("sclContent: the full SCL source text (complete FUNCTION_BLOCK / FUNCTION / DATA_BLOCK / TYPE declarations). This is written " +
                "verbatim; plain ASCII omits the BOM for tool import, other text uses UTF-8 BOM and requires manual TIA import.")] string sclContent,
            [Description("outputPath: .scl only target file path. If a directory is given (or the path has no extension), the file is named after the first " +
                "block found in the source. Empty means a temp file under %TEMP%\\tia_mcp_scl.")] string outputPath =
                "")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sclContent))
                    throw new McpException("sclContent is empty — provide the full SCL source text to write.", McpErrorCode.InvalidParams);

                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    int drivePrefix = outputPath.Length >= 2 && char.IsLetter(outputPath[0]) && outputPath[1] == ':' ? 2 : 0;
                    if (outputPath.IndexOf(':', drivePrefix) >= 0 || outputPath.EndsWith(".", StringComparison.Ordinal)
                        || outputPath.EndsWith(" ", StringComparison.Ordinal))
                        throw new McpException("outputPath must be .scl only; alternate streams and trailing dots or spaces are not allowed.", McpErrorCode
                                .InvalidParams);
                    bool directory = Directory.Exists(outputPath) || outputPath.EndsWith("\\", StringComparison.Ordinal)
                        || outputPath.EndsWith("/", StringComparison.Ordinal);
                    string extension = Path.GetExtension(outputPath);
                    if (!directory && extension.Length != 0 && !extension.Equals(".scl", StringComparison.OrdinalIgnoreCase))
                        throw new McpException("outputPath must be .scl only, a directory, or a path without an extension.", McpErrorCode.InvalidParams);
                }

                // Derive a default file name from the first block declaration in the source.
                var nameMatch = Regex.Match(sclContent,
                    "(?:FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|TYPE)\\s+\"?([A-Za-z_][A-Za-z0-9_]*)\"?",
                    RegexOptions.IgnoreCase);
                var defaultName = EngineeringFileNames.MakeSafeFileName(nameMatch.Success ? nameMatch.Groups[1].Value : "MCP_Source");

                string finalPath;
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    var dir = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "tia_mcp_scl");
                    finalPath = Path.Combine(dir, defaultName + ".scl");
                }
                else if (Directory.Exists(outputPath) ||
                         outputPath.EndsWith("\\", StringComparison.Ordinal) ||
                         outputPath.EndsWith("/", StringComparison.Ordinal))
                {
                    finalPath = Path.Combine(outputPath, defaultName + ".scl");
                }
                else
                {
                    finalPath = string.IsNullOrEmpty(Path.GetExtension(outputPath))
                        ? outputPath + ".scl"
                        : outputPath;
                }

                var parent = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                // Preserve BOM for non-ASCII text; automated import currently accepts only plain ASCII.
                bool importable = sclContent.All(c => c == '\t' || c == '\r' || c == '\n' || c >= 32 && c <= 126);
                PlcExchangeContract.StartWrite("scl-file-write");
                File.WriteAllText(finalPath, sclContent, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: !importable));
                PlcExchangeContract.ConfirmWrite();

                return new ResponseMessage
                {
                    Message =
                        $"SCL source written to '{finalPath}'. To import in TIA Portal: project tree → " +
                        "'External source files' → 'Add new external file' → select this .scl → " +
                        "right-click the source → 'Generate blocks from source'. " +
                        (importable ? "(Or call ImportPlcExternalSource then GenerateBlocksFromExternalSource if connected.)"
                            : "The tool importers refuse this non-ASCII source. Use the manual TIA steps above."),
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["path"] = finalPath,
                        ["encoding"] = importable ? "ascii" : "utf-8-bom",
                        ["importableByTool"] = importable,
                        ["blockName"] = nameMatch.Success ? nameMatch.Groups[1].Value : null,
                        ["bytes"] = new System.IO.FileInfo(finalPath).Length
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error writing SCL source file: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
