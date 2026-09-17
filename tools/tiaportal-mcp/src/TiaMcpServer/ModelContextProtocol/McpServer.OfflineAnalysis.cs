using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "ComparePlcBlockDocuments"), Description("[L2][Validation][READ] Semantic diff of two exported PLC block documents (SimaticML .xml, SIMATIC SD .s7dcl with sibling .s7res, or external .scl) with volatile noise removed (ID/UId/IId/RefId, DocumentInfo timestamps and product versions, GUIDs, ISO timestamps, MLC_* ids). Each side is EITHER an existing absolute file path (leftFilePath/rightFilePath; no TIA Portal needed) OR an exact block path in the open project (leftBlockPath/rightBlockPath + softwarePath; the block is exported to a temp directory that is deleted afterwards). Returns identicalAfterNormalization, a structural report (block attributes, interface members added/removed/type-changed, network count/titles/languages) and paginated Myers line hunks over the canonical form. Both sides must be given; mixing a file and a block is allowed. Diff refused above 60000 normalized lines per side. Nothing is saved, compiled or downloaded.")]
        public static ResponseMessage ComparePlcBlockDocuments(string leftFilePath = "", string rightFilePath = "", string softwarePath = "", string leftBlockPath = "", string rightBlockPath = "", int offset = 0, int limit = 100, int contextLines = 2)
            => RunOfflineAnalysisTool("ComparePlcBlockDocuments", meta =>
            {
                OfflineAnalysisLogic.ValidatePage(offset, limit);
                if (contextLines < 0 || contextLines > 20) throw new ArgumentException("contextLines must be between 0 and 20.");
                string? leftTemp = null, rightTemp = null;
                try
                {
                    var left = ResolveCompareSide("left", leftFilePath, leftBlockPath, softwarePath, meta, out leftTemp);
                    var right = ResolveCompareSide("right", rightFilePath, rightBlockPath, softwarePath, meta, out rightTemp);
                    var result = OfflineAnalysisLogic.CompareFiles(left, right, offset, limit, contextLines);
                    foreach (var kv in result.ToList()) meta[kv.Key] = kv.Value?.DeepClone();
                    meta["apiCallSuccess"] = true;
                    var identical = result["identicalAfterNormalization"]?.GetValue<bool>() == true;
                    var structural = result["structural"]?["differenceCount"]?.GetValue<int>() ?? 0;
                    return identical
                        ? "Documents are identical after normalization."
                        : "Documents differ: " + result["hunkCount"] + " hunk(s), " + structural + " structural difference(s). Page offset=" + offset + " limit=" + limit + ".";
                }
                finally
                {
                    DeleteAnalysisTempDir(leftTemp);
                    DeleteAnalysisTempDir(rightTemp);
                }
            });

        [McpServerTool(Name = "ScanPlcSourceAnnotations"), Description("[L2][Validation][FILE] Scan exported PLC documents in a directory (absolute path; recursive by default; extensionsJson default [\".xml\",\".s7dcl\",\".scl\"]) for annotation markers in comments, block/network titles and network comments. markersJson default [\"TODO\",\"FIXME\",\"HACK\",\"XXX\",\"NOTE\",\"BUG\"]; matching is case-sensitive on whole words. SimaticML text nodes (titles, comments, SCL LineComment, member comments) and text-source comments (// and (* *)) are scanned; .s7dcl MLC_* titles are resolved via the sibling .s7res. Returns paginated rows {file, blockName, network, line, marker, text, kind}. csvPath (optional) must be a NEW absolute file: all rows are written as CSV and hashed; an existing file is refused. No TIA Portal connection, nothing is saved, compiled or downloaded.")]
        public static ResponseMessage ScanPlcSourceAnnotations(string directory, bool recursive = true, string extensionsJson = "", string markersJson = "", int offset = 0, int limit = 200, string csvPath = "")
            => RunOfflineAnalysisTool("ScanPlcSourceAnnotations", meta =>
            {
                OfflineAnalysisLogic.ValidatePage(offset, limit);
                var markers = OfflineAnalysisLogic.ParseMarkers(markersJson);
                var extensions = OfflineAnalysisLogic.ParseExtensions(extensionsJson);
                var csv = string.IsNullOrWhiteSpace(csvPath) ? null : NativeFileOutput.Plan(csvPath);
                var files = OfflineAnalysisLogic.EnumerateDocuments(directory, recursive, extensions);
                var rows = new System.Collections.Generic.List<OfflineAnalysisLogic.AnnotationRow>();
                var failures = new JsonArray();
                foreach (var file in files)
                {
                    try { rows.AddRange(OfflineAnalysisLogic.ScanFile(file, markers)); }
                    catch (Exception ex) { failures.Add(new JsonObject { ["file"] = file, ["error"] = ex.Message }); }
                }
                meta["directory"] = directory; meta["recursive"] = recursive;
                meta["markers"] = new JsonArray(markers.Select(m => (JsonNode)m).ToArray());
                meta["extensions"] = new JsonArray(extensions.Select(e => (JsonNode)e).ToArray());
                meta["filesScanned"] = files.Count; meta["filesFailed"] = failures.Count; meta["failures"] = failures;
                meta["rowCount"] = rows.Count;
                var byMarker = new JsonObject();
                foreach (var g in rows.GroupBy(r => r.Marker).OrderBy(g => g.Key, StringComparer.Ordinal)) byMarker[g.Key] = g.Count();
                meta["countsByMarker"] = byMarker;
                var page = new JsonArray();
                foreach (var row in rows.Skip(offset).Take(limit)) page.Add(OfflineAnalysisLogic.AnnotationJson(row));
                meta["offset"] = offset; meta["limit"] = limit; meta["rows"] = page;
                meta["dataComplete"] = failures.Count == 0 && offset + page.Count >= rows.Count;
                meta["apiCallSuccess"] = true;
                if (csv != null)
                {
                    meta["mayHaveWrittenFiles"] = true;
                    File.WriteAllText(csv.FullName, OfflineAnalysisLogic.ToCsv(rows), new UTF8Encoding(true));
                    meta["csv"] = NativeFileOutput.Verify(csv);
                    meta["csv"]!["rowsWritten"] = rows.Count;
                }
                return rows.Count + " annotation(s) in " + files.Count + " file(s)" + (failures.Count > 0 ? " (" + failures.Count + " file(s) failed to parse)" : "") + (csv != null ? "; CSV written to " + csv.FullName : "") + ".";
            });

        [McpServerTool(Name = "ExtractPlcBlockMetrics"), Description("[L2][Validation][READ] Derive per-block metrics from exported PLC documents (path = absolute file or directory; recursive; extensionsJson default [\".xml\",\".s7dcl\",\".scl\"]): network count and titles/comments, interface members per section, block calls (CallInfo / quoted calls / #instance calls), instructions (LAD/FBD Parts, SCL functions), SCL source lines, comment lines and ratio, and max IF/CASE/FOR/WHILE/REPEAT nesting where SCL text is available (null otherwise). Graphical networks contribute no source lines. The numbers describe the export only; they are NOT a Siemens quality verdict and do not replace TIA compile diagnostics or the programming guidelines. Paginated rows. No TIA Portal connection, nothing is saved, compiled or downloaded.")]
        public static ResponseMessage ExtractPlcBlockMetrics(string path, bool recursive = true, string extensionsJson = "", int offset = 0, int limit = 100)
            => RunOfflineAnalysisTool("ExtractPlcBlockMetrics", meta =>
            {
                OfflineAnalysisLogic.ValidatePage(offset, limit);
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("Absolute file or directory path required.");
                var extensions = OfflineAnalysisLogic.ParseExtensions(extensionsJson);
                var files = File.Exists(path) ? new System.Collections.Generic.List<string> { path } : OfflineAnalysisLogic.EnumerateDocuments(path, recursive, extensions);
                var failures = new JsonArray();
                var rows = new JsonArray();
                var index = 0;
                foreach (var file in files)
                {
                    if (index >= offset && rows.Count < limit)
                    {
                        try { rows.Add(OfflineAnalysisLogic.Metrics(OfflineAnalysisLogic.ParseFile(file))); }
                        catch (Exception ex) { failures.Add(new JsonObject { ["file"] = file, ["error"] = ex.Message }); }
                    }
                    index++;
                }
                meta["path"] = path; meta["recursive"] = recursive;
                meta["extensions"] = new JsonArray(extensions.Select(e => (JsonNode)e).ToArray());
                meta["fileCount"] = files.Count; meta["offset"] = offset; meta["limit"] = limit;
                meta["rows"] = rows; meta["failures"] = failures;
                meta["dataComplete"] = failures.Count == 0 && offset + rows.Count >= files.Count;
                meta["apiCallSuccess"] = true;
                meta["metricNotes"] = new JsonArray(
                    "Metrics are derived from the exported document only and are not a Siemens quality verdict.",
                    "sourceLines/commentLines/commentRatio cover SCL text (and all body lines of .s7dcl/.scl); SimaticML graphical networks contribute none.",
                    "maxNesting counts IF/CASE/FOR/WHILE/REPEAT depth in SCL text; null when not derivable.");
                return rows.Count + " block metric row(s) from " + files.Count + " file(s)" + (failures.Count > 0 ? " (" + failures.Count + " failed to parse)" : "") + ".";
            });

        private static string ResolveCompareSide(string side, string filePath, string blockPath, string softwarePath, JsonObject meta, out string? tempDir)
        {
            tempDir = null;
            var hasFile = !string.IsNullOrWhiteSpace(filePath);
            var hasBlock = !string.IsNullOrWhiteSpace(blockPath);
            if (hasFile == hasBlock) throw new ArgumentException("Exactly one of " + side + "FilePath or " + side + "BlockPath must be given.");
            if (hasFile)
            {
                if (!Path.IsPathRooted(filePath)) throw new ArgumentException(side + "FilePath must be absolute.");
                if (!File.Exists(filePath)) throw new FileNotFoundException(side + "FilePath not found: " + filePath, filePath);
                meta[side + "Source"] = new JsonObject { ["mode"] = "file", ["path"] = filePath };
                return filePath;
            }
            var export = Portal.ExportBlockDocumentForAnalysis(softwarePath, blockPath);
            tempDir = export.TempDir;
            meta[side + "Source"] = new JsonObject { ["mode"] = "block", ["softwarePath"] = softwarePath, ["blockPath"] = blockPath, ["tempExportDeleted"] = true };
            return export.XmlPath;
        }

        private static void DeleteAnalysisTempDir(string? tempDir)
        {
            if (string.IsNullOrWhiteSpace(tempDir)) return;
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* best-effort cleanup of our own temp export */ }
        }

        // File-only tools never need a project; mirrors RunHmiStepTool's meta/status contract without touching Portal.
        private static ResponseMessage RunOfflineAnalysisTool(string toolName, Func<JsonObject, string> action)
        {
            var meta = new JsonObject { ["timestamp"] = DateTime.Now, ["tool"] = toolName, ["success"] = false, ["offlineOnly"] = true };
            try
            {
                var message = action(meta);
                meta["success"] = true; meta["operationSuccess"] = true;
                return new ResponseMessage { Message = message, Meta = meta };
            }
            catch (Exception ex)
            {
                var cause = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                meta["error"] = cause.ToString();
                meta["operationSuccess"] = false; meta["apiCallSuccess"] = false; meta["dataComplete"] = false;
                meta["status"] = cause is PortalException pex ? pex.Code.ToString()
                    : cause is ArgumentException || cause is FileNotFoundException || cause is DirectoryNotFoundException || cause is JsonException ? "InvalidParams"
                    : "ReadOrWriteFailed";
                return new ResponseMessage { Message = toolName + " failed: " + cause.Message, Meta = meta };
            }
        }
    }
}
