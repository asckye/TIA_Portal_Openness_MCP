using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    // Offline documentation family: Markdown/Mermaid rendering of exported blocks, a program handbook
    // over an export directory and a heuristic SCL pre-check. Same contract as OfflineAnalysis: no
    // project is changed, nothing is compiled or downloaded; the only writes are the optional output files.
    public static partial class McpServer
    {
        [McpServerTool(Name = "RenderPlcBlockDocument"), Description("[L2][Validation][OFFLINE] Render ONE exported PLC block document as Markdown: header (type/number/language/title/comment), interface table, then one section per network — SCL networks as a ```scl listing reconstructed from the StructuredText tokens, LAD/FBD networks as a part listing plus a ```mermaid flowchart (power rail, parts with instance/template, operands, pin-labelled wires). Source is EITHER filePath (absolute; SimaticML .xml, .s7dcl with sibling .s7res, or .scl) OR softwarePath + blockPath (block exported to a temp directory that is deleted afterwards). mermaidDirection LR (default) or TD. Returns the Markdown in Meta.markdown (truncated to maxChars, default 60000, full length in Meta.markdownLength); outputPath (optional, NEW absolute .md file) writes the complete text and returns bytes+sha256. The Mermaid graph follows the wires in the export (branches/feedback are edges, not a ladder drawing); nothing is saved, compiled or downloaded.")]
        public static ResponseMessage RenderPlcBlockDocument(string filePath = "", string softwarePath = "", string blockPath = "", string mermaidDirection = "LR", string outputPath = "", int maxChars = 60000)
            => RunOfflineAnalysisTool("RenderPlcBlockDocument", meta =>
            {
                if (maxChars < 1000 || maxChars > 2000000) throw new ArgumentException("maxChars must be between 1000 and 2000000.");
                var output = string.IsNullOrWhiteSpace(outputPath) ? null : NativeFileOutput.Plan(outputPath);
                string? temp = null;
                try
                {
                    var hasFile = !string.IsNullOrWhiteSpace(filePath);
                    var hasBlock = !string.IsNullOrWhiteSpace(blockPath);
                    if (hasFile == hasBlock) throw new ArgumentException("Exactly one of filePath or blockPath (with softwarePath) must be given.");
                    string path;
                    if (hasFile)
                    {
                        if (!Path.IsPathRooted(filePath)) throw new ArgumentException("filePath must be absolute.");
                        if (!File.Exists(filePath)) throw new FileNotFoundException("filePath not found: " + filePath, filePath);
                        path = filePath;
                        meta["source"] = new JsonObject { ["mode"] = "file", ["path"] = filePath };
                    }
                    else
                    {
                        var export = Portal.ExportBlockDocumentForAnalysis(softwarePath, blockPath);
                        temp = export.TempDir; path = export.XmlPath;
                        meta["source"] = new JsonObject { ["mode"] = "block", ["softwarePath"] = softwarePath, ["blockPath"] = blockPath, ["tempExportDeleted"] = true };
                    }
                    var rendered = PlcDocumentationLogic.RenderFile(path, mermaidDirection ?? "LR");
                    var md = rendered.Markdown;
                    meta["blockName"] = rendered.Document.BlockName; meta["blockType"] = rendered.Document.BlockType; meta["language"] = rendered.Document.Language;
                    meta["networkCount"] = rendered.Networks.Count; meta["graphicalNetworks"] = rendered.GraphicalNetworks; meta["textNetworks"] = rendered.TextNetworks;
                    meta["interfaceMembers"] = rendered.Document.Members.Count;
                    meta["markdownLength"] = md.Length;
                    meta["markdown"] = md.Length > maxChars ? md.Substring(0, maxChars) : md;
                    meta["truncated"] = md.Length > maxChars;
                    if (output != null)
                    {
                        File.WriteAllText(output.FullName, md, new UTF8Encoding(false));
                        meta["output"] = NativeFileOutput.Verify(output);
                        meta["mayHaveWrittenFiles"] = true;
                    }
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = md.Length <= maxChars || output != null;
                    return "Rendered " + rendered.Document.BlockType + " '" + rendered.Document.BlockName + "': " + rendered.Networks.Count + " network(s) (" + rendered.GraphicalNetworks + " graphical, " + rendered.TextNetworks + " text), " + md.Length + " Markdown characters" + (output != null ? ", written to " + output.FullName : "") + ".";
                }
                finally { DeleteAnalysisTempDir(temp); }
            });

        [McpServerTool(Name = "GeneratePlcDocumentation"), Description("[L2][Validation][FILE] Generate ONE Markdown program handbook from a directory of exported PLC documents (absolute path; recursive by default; extensionsJson default [\".xml\",\".s7dcl\",\".scl\"]): an index table (block, type, number, language, networks, interface members, calls), a call cross-reference (called block → callers, flagged when the callee is not in the export), then every block rendered as by RenderPlcBlockDocument (interface table, SCL listings, Mermaid LAD/FBD graphs). outputPath must be a NEW absolute .md file (existing files are refused); returns bytes+sha256, block counts and per-file parse failures. Typical input: the folder written by ExportBlocksAsDocuments. No TIA Portal connection; nothing is saved, compiled or downloaded.")]
        public static ResponseMessage GeneratePlcDocumentation(string directory, string outputPath, string title = "", bool recursive = true, string extensionsJson = "", string mermaidDirection = "LR")
            => RunOfflineAnalysisTool("GeneratePlcDocumentation", meta =>
            {
                var output = NativeFileOutput.Plan(outputPath);
                var extensions = OfflineAnalysisLogic.ParseExtensions(extensionsJson);
                var hb = PlcDocumentationLogic.RenderHandbook(directory, recursive, extensions, title ?? "", mermaidDirection ?? "LR");
                File.WriteAllText(output.FullName, hb.Markdown, new UTF8Encoding(false));
                meta["directory"] = directory; meta["recursive"] = recursive;
                meta["documentCount"] = hb.Entries.Count; meta["renderedBlocks"] = hb.Entries.Count - hb.Failed; meta["failedDocuments"] = hb.Failed;
                meta["failures"] = new JsonArray(hb.Entries.Where(e => e.Block == null).Select(e => (JsonNode)new JsonObject { ["file"] = e.File, ["error"] = e.Error }).ToArray());
                meta["blocks"] = new JsonArray(hb.Entries.Where(e => e.Block != null).Select(e => (JsonNode)new JsonObject
                {
                    ["name"] = e.Block!.Document.BlockName, ["type"] = e.Block.Document.BlockType, ["language"] = e.Block.Document.Language,
                    ["networks"] = e.Block.Networks.Count, ["calls"] = e.Block.Document.BlockCalls.Distinct().Count()
                }).ToArray());
                meta["output"] = NativeFileOutput.Verify(output);
                meta["markdownLength"] = hb.Markdown.Length;
                meta["mayHaveWrittenFiles"] = true; meta["apiCallSuccess"] = true; meta["dataComplete"] = hb.Failed == 0;
                return "Documented " + (hb.Entries.Count - hb.Failed) + " block(s) from " + hb.Entries.Count + " document(s)" + (hb.Failed > 0 ? " (" + hb.Failed + " failed to parse)" : "") + " into " + output.FullName + ".";
            });

        [McpServerTool(Name = "LintPlcSclSource"), Description("[L2][Validation][OFFLINE] Heuristic pre-check of SCL source BEFORE ImportBlocksFromDocuments / WritePlcSclSourceFile: block keyword pairing (IF/END_IF, CASE, FOR, WHILE, REPEAT, REGION, FUNCTION[_BLOCK], ORGANIZATION_BLOCK, DATA_BLOCK, TYPE, STRUCT, VAR*/END_VAR), unbalanced ( ) [ ], '=' at statement level instead of ':=', statement before ELSE/ELSIF/UNTIL/END_* without ';', GOTO, WHILE TRUE, nesting depth, line length, tabs/trailing whitespace, ';;', unterminated (* comment, TODO/FIXME markers. Input is EITHER sourceText OR filePath (absolute .scl/.txt; UTF-8). rulesJson optional, e.g. {\"disable\":[\"SCL007\",\"SCL008\"],\"maxLineLength\":120,\"maxNesting\":5,\"markers\":[\"TODO\"]}. Returns findings {rule, severity error|warning|info, line, message, text} sorted by line, counts per severity and the rule catalog. Heuristics only: 'ok' means no finding, NOT that TIA will compile the source; the TIA compiler (CompileSoftware) remains the verdict. Nothing is saved, compiled or downloaded.")]
        public static ResponseMessage LintPlcSclSource(string sourceText = "", string filePath = "", string rulesJson = "", int limit = 500)
            => RunOfflineAnalysisTool("LintPlcSclSource", meta =>
            {
                if (limit < 1 || limit > 5000) throw new ArgumentException("limit must be between 1 and 5000.");
                var hasText = !string.IsNullOrWhiteSpace(sourceText);
                var hasFile = !string.IsNullOrWhiteSpace(filePath);
                if (hasText == hasFile) throw new ArgumentException("Exactly one of sourceText or filePath must be given.");
                string source;
                if (hasFile)
                {
                    if (!Path.IsPathRooted(filePath)) throw new ArgumentException("filePath must be absolute.");
                    if (!File.Exists(filePath)) throw new FileNotFoundException("filePath not found: " + filePath, filePath);
                    source = File.ReadAllText(filePath, Encoding.UTF8);
                    meta["filePath"] = filePath;
                }
                else source = sourceText;
                if (source.Length > 4_000_000) throw new ArgumentException("Source exceeds 4 MB.");
                var options = PlcDocumentationLogic.ParseLintOptions(rulesJson);
                var findings = PlcDocumentationLogic.LintScl(source, options);
                meta["lineCount"] = source.Replace("\r\n", "\n").Split('\n').Length;
                meta["findingCount"] = findings.Count;
                meta["errors"] = findings.Count(f => f.Severity == "error");
                meta["warnings"] = findings.Count(f => f.Severity == "warning");
                meta["infos"] = findings.Count(f => f.Severity == "info");
                meta["findings"] = new JsonArray(findings.Take(limit).Select(f => (JsonNode)PlcDocumentationLogic.FindingJson(f)).ToArray());
                meta["truncated"] = findings.Count > limit;
                meta["disabledRules"] = new JsonArray(options.Disabled.Select(d => (JsonNode)d).ToArray());
                meta["rules"] = PlcDocumentationLogic.RuleCatalog();
                meta["ok"] = findings.All(f => f.Severity != "error");
                meta["verdictNote"] = "Heuristic pre-check only; CompileSoftware is the compiler verdict.";
                meta["apiCallSuccess"] = true; meta["dataComplete"] = findings.Count <= limit;
                return findings.Count == 0 ? "No findings in " + meta["lineCount"] + " line(s)."
                    : findings.Count + " finding(s): " + meta["errors"] + " error(s), " + meta["warnings"] + " warning(s), " + meta["infos"] + " info.";
            });
    }
}
