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
using TiaMcp.Logic.Exchange;
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
                Description("[L2][PLC-Builders][FILE] Patch an exported single-block or single-UDT SimaticML file into a NEW file, preserving all unrelated " +
                    "XML. Requires "
                        +
                "expectedDocumentHash from GetPlcBlockEditCapabilities. changes array: setBlockText(field Title/Comment,culture,expectedValue,value), " +
                "setNetworkText(same plus zero-based networkIndex), setMemberStartValue(section,memberPath slash-separated,expectedValue,value), " +
                "setMemberComment(section,memberPath,culture,expectedValue,value), e.g. Static/Motor/Speed in zh-CN with expectedValue=\"\". " +
                "StartValue and block/network text must exist; member comments may be added or removed. Use a culture contained in the export, or an explicit "
                    +
                "culture when it contains none; no guessed defaults, library-connected blocks or read-only members. UDTs return with ImportPlcType " +
                    "overwrite=true; "
                    +
                "overwrite makes dependent DBs and blocks inconsistent until compiled. " +
                "Default dryRun=true previews; false writes outputPath. No native calls/import/compile; input remains unchanged.")]
        public CallToolResult PatchPlcBlockDocumentV4(
            [Description("Absolute existing single-block or single-UDT SimaticML XML file.")] string filePath,
            [Description("JSON array of objects with action=setBlockText/setNetworkText/setMemberStartValue/setMemberComment, exact target fields, " +
                "expectedValue and value. "
                        +
                "The selector key is action. Example: " +
                    "[{\"action\":\"setMemberComment\",\"section\":\"Static\",\"memberPath\":\"Motor/Speed\",\"culture\":\"zh-CN\","
                    +
                "\"expectedValue\":\"\",\"value\":\"Motor speed\"}]. UDT members use section=None; empty value removes that culture's " +
                    "comment.")] BlockEdit[] changes,
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

        [McpServerTool(Name = "ConvertEngineeringTable"),
            Description("[L2][PLC-Builders][FILE] Offline conversion: kind plcTagTable or rows; direction toTable or fromTable. " +
                "plcTagTable uses one exported SimaticML table; fromTable requires its templatePath. Fixed columns Kind, Name, DataType, Address, " +
                "Value, then template scalar attributes in first-seen order and Comment[culture] ordered by culture. Culture syntax " +
                "^[a-zA-Z]{2}(-[a-zA-Z]{4})?-[a-zA-Z]{2}$. Kind is Tag, Constant or read-only SystemConstant; (Kind,Name) is an exact key, " +
                "never renamed. Merge updates/adds rows; omitted rows are kept, empty editable cells remove attributes/comments to TIA defaults. " +
                "Unknown columns and duplicate keys refuse. Header, DocumentInfo and unrelated XML are preserved. Delete explicitly through " +
                "ManagePlcTagDefinition/ApplyToolBatch. rows toTable accepts 1..1000 flat scalar objects and optional column order; " +
                "fromTable returns strings with offset/limit 1..200 (default 50), columns, total and V4 paging; writes no file. " +
                "CSV is UTF-8 BOM, comma, all cells quoted with doubled quotes and LF; accepts no BOM and CR/LF/CRLF; rejects NUL, broken quotes, " +
                "uneven rows and empty/duplicate headers. No formula escaping. Use XLSX when Excel's locale uses semicolon separators. " +
                "XLSX uses one sheet PlcTagTable/Rows, all inline text cells; reads that sheet or the only sheet, shared/inline strings, raw numbers, " +
                "booleans and formula cached values; refuses formulas without cache, errors, ambiguous sheets, .xls/.xlsm and encrypted files. " +
                "Limits: 16 MiB uncompressed, 10000 data rows, 1024 columns, 65536 CSV/32767 XLSX characters per cell. " +
                "Default dryRun=true validates and previews columns/counts, changes (first 50 old/new rows) and planned exportFile without writing. " +
                "false creates a new file, never overwrites; returns path, bytes and SHA-256. Omitted exportFile uses the session output directory. " +
                "Output extension must match format (xlsx default/csv), or xml for plcTagTable fromTable. Import the XML with ImportPlcTagTable " +
                "overwrite=true after preview/approval, binding folderPath to the original table folder; then ListPlcTags readback and compile " +
                "affected blocks. No TIA calls, imports or compilation.")]
        public CallToolResult ConvertEngineeringTableV4(
            [Description("plcTagTable | rows.")] string kind,
            [Description("toTable | fromTable.")] string direction,
            [Description("Absolute input XML for plcTagTable toTable, or .csv/.xlsx for fromTable.")] string inputPath = "",
            [Description("Original exported tag table XML; required for plcTagTable fromTable.")] string templatePath = "",
            [Description("1..1000 flat scalar objects for rows toTable; numbers use invariant text, booleans true/false, null empty.")]
                JsonElement[]? rows = null,
            [Description("Optional unique column order for rows toTable; must include every supplied property.")] string[]? columns = null,
            [Description("xlsx | csv for toTable; default xlsx, text cells preserve addresses and leading zeros.")] string format = "xlsx",
            [Description("New absolute .xlsx/.csv output, or .xml for plcTagTable fromTable; omitted uses the session output directory.")]
                string exportFile = "",
            [Description("Zero-based offset for rows fromTable.")] int offset = 0,
            [Description("Page size 1..200 for rows fromTable; default 50 for PreviewToolBatch.")] int limit = 50,
            [Description("Validate and preview without writing by default; false creates a new file, never imports.")] bool dryRun = true)
            => PlcToolContract.Run("ConvertEngineeringTable", !dryRun && !(kind == "rows" && direction == "fromTable"), false, () => {
                var response = ConvertEngineeringTable(kind, direction, inputPath, templatePath, rows, columns, format, exportFile, offset, limit, dryRun);
                var data = response.Meta?["data"]?.DeepClone(); response.Meta?.Remove("data");
                return new { response.Message, response.Meta, Data = data };
            });

        public ResponseMessage ConvertEngineeringTable(string kind, string direction, string inputPath = "", string templatePath = "",
            JsonElement[]? rows = null, string[]? columns = null, string format = "xlsx", string exportFile = "", int offset = 0, int limit = 50,
            bool dryRun = true)
            => OfflineToolExecution.RunOfflineAnalysisTool("ConvertEngineeringTable", meta => {
                meta["mayHaveChanged"] = false;
                if (kind != "plcTagTable" && kind != "rows") throw new ArgumentException("Expected plcTagTable or rows.", nameof(kind));
                if (direction != "toTable" && direction != "fromTable") throw new ArgumentException("Expected toTable or fromTable.", nameof(direction));
                byte[] Read(string path, string parameter)
                {
                    if (!Path.IsPathRooted(path)) throw new ArgumentException("Absolute input path required.", parameter);
                    var file = new FileInfo(path);
                    if (!file.Exists || file.Length > TableFile.MaximumBytes)
                        throw new ArgumentException(path + ": row 1, column 1: file missing or exceeds 16 MiB.", parameter);
                    return File.ReadAllBytes(path);
                }
                System.Xml.Linq.XDocument Xml(string path, string parameter)
                    => PlcTagTableExchange.Parse(Encoding.UTF8.GetString(Read(path, parameter)).TrimStart('\ufeff'));
                string sheet = kind == "plcTagTable" ? "PlcTagTable" : "Rows";
                TableData table;
                byte[]? outputBytes = null;
                var data = new JsonObject { ["kind"] = kind, ["direction"] = direction, ["dryRun"] = dryRun };
                meta["data"] = data;
                if (direction == "toTable")
                {
                    if (format != "xlsx" && format != "csv") throw new ArgumentException("Expected xlsx or csv.", nameof(format));
                    table = kind == "plcTagTable" ? PlcTagTableExchange.ToTable(Xml(inputPath, nameof(inputPath))) : ScalarTable(rows, columns);
                    outputBytes = format == "csv" ? TableFile.WriteCsv(table) : TableFile.WriteXlsx(table, sheet);
                }
                else
                {
                    byte[] input = Read(inputPath, nameof(inputPath));
                    string extension = Path.GetExtension(inputPath);
                    table = extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) ? TableFile.ReadCsv(input, inputPath)
                        : TableFile.ReadXlsx(input, sheet, inputPath);
                    if (kind == "rows")
                    {
                        if (offset < 0 || offset > table.Rows.Count) throw new ArgumentException("Offset must be within the table.", nameof(offset));
                        if (limit < 1 || limit > 200) throw new ArgumentException("Limit must be 1..200.", nameof(limit));
                        data["offset"] = offset; data["limit"] = limit; data["total"] = table.Rows.Count;
                        data["rows"] = new JsonArray(table.Rows.Skip(offset).Take(limit).Select(row => (JsonNode)new JsonObject(
                            table.Columns.Select((column, i) => new KeyValuePair<string, JsonNode?>(column, JsonValue.Create(row[i]))))).ToArray());
                    }
                    else
                    {
                        var edited = PlcTagTableExchange.FromTable(Xml(templatePath, nameof(templatePath)), table);
                        data["changes"] = edited.Summary;
                        using var buffer = new MemoryStream();
                        using (var writer = System.Xml.XmlWriter.Create(buffer, new System.Xml.XmlWriterSettings {
                            Encoding = new UTF8Encoding(false), NewLineHandling = System.Xml.NewLineHandling.Entitize })) edited.Document.Save(writer);
                        outputBytes = buffer.ToArray();
                        if (outputBytes.Length > TableFile.MaximumBytes) throw new ArgumentException("Merged XML exceeds 16 MiB.");
                    }
                }
                data["columns"] = new JsonArray(table.Columns.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
                data["rowCount"] = table.Rows.Count;
                if (outputBytes != null)
                {
                    string expectedExtension = direction == "fromTable" ? ".xml" : "." + format;
                    if (!string.Equals(Path.GetExtension(exportFile), expectedExtension, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("exportFile must end with " + expectedExtension + ".", nameof(exportFile));
                    var output = Siemens.NativeFileOutput.Plan(exportFile);
                    data["exportFile"] = output.FullName;
                    if (kind == "plcTagTable" && direction == "fromTable") data["nextStep"] = new JsonObject {
                        ["tool"] = "ImportPlcTagTable", ["requiresBinding"] = true,
                        ["arguments"] = new JsonObject { ["softwarePath"] = "<original PLC software path>",
                            ["folderPath"] = "<original table folder>", ["importPath"] = output.FullName,
                            ["overwrite"] = true, ["dryRun"] = true } };
                    if (!dryRun)
                    {
                        meta["mayHaveChanged"] = true;
                        using (var stream = new FileStream(output.FullName, FileMode.CreateNew, FileAccess.Write))
                            stream.Write(outputBytes, 0, outputBytes.Length);
                        data["output"] = Siemens.NativeFileOutput.Verify(output);
                    }
                }
                return dryRun ? "Table conversion preview validated; no file written." : "Offline table conversion completed; native import unverified.";
            });

        private static TableData ScalarTable(JsonElement[]? rows, string[]? columns)
        {
            if (rows == null || rows.Length < 1 || rows.Length > 1000) throw new ArgumentException("Expected 1..1000 rows.", nameof(rows));
            var objects = new List<Dictionary<string, string>>();
            foreach (var row in rows)
            {
                if (row.ValueKind != JsonValueKind.Object) throw new ArgumentException("Every row must be a flat object.", nameof(rows));
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in row.EnumerateObject())
                {
                    if (values.ContainsKey(property.Name)) throw new ArgumentException("Duplicate row property: " + property.Name, nameof(rows));
                    values.Add(property.Name, property.Value.ValueKind switch {
                        JsonValueKind.String => property.Value.GetString()!, JsonValueKind.Number => property.Value.GetRawText(),
                        JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Null => "",
                        _ => throw new ArgumentException("Rows must contain scalar values only: " + property.Name, nameof(rows)) });
                }
                objects.Add(values);
            }
            columns ??= objects.SelectMany(row => row.Keys).Distinct(StringComparer.Ordinal).ToArray();
            if (objects.SelectMany(row => row.Keys).Any(key => !columns.Contains(key)))
                throw new ArgumentException("columns must include every supplied property.", nameof(columns));
            var table = new TableData(columns, objects.Select(row => columns.Select(c => row.TryGetValue(c, out var value) ? value : "").ToArray()));
            TableFile.Validate(table); return table;
        }

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
