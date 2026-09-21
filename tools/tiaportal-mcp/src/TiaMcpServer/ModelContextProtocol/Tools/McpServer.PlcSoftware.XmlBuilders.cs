using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;


namespace TiaMcpServer.ModelContextProtocol
{
    // Partial: plc software. Family file split out of McpServer.PlcSoftware.cs (2.8.0); behavior unchanged.
    public static partial class McpServer
    {
        #region plc software - XmlBuilders

        [McpServerTool(Name = "BuildClassicHmiScreenXml"), Description("[L2][HMI-Classic]Offline-only helper: build a Classic/Basic WinCC HMI screen XML document from structured JSON. It does not connect to TIA Portal, import screens, or modify projects. Validate in a temporary Classic HMI project before using on a real project. screen.width/height MUST equal the panel display (TP700 Comfort 800x480, TP900 800x480, TP1200 1280x800; the builder default is 640x480): a classic screen imported with another size makes TIA Portal V21 exit (crash 10, guarded by ImportHmiScreen when the panel already has a screen).")]
        public static ResponseXmlBuild BuildClassicHmiScreenXml(
            [Description("designJson: JSON object with Screen/Items. Items support Type=Text/Button/IOField/Lamp/Rectangle plus Name/Left/Top/Width/Height/Text/Properties.")] string designJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(ClassicHmiScreenXmlBuilder.BuildFromJson(designJson), "Classic HMI screen XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Classic HMI screen XML offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildPlcUdtXml"), Description("[L2][PLC-Builders][OFFLINE] Build a TIA V21 PLC UDT/PlcStruct XML document from structured JSON. Input: {members:[{name,datatype,externalWritable?,commentZhCn?}]}. It only returns XML; it does not connect to TIA Portal, import types, write files, or modify projects.")]
        public static ResponseXmlBuild BuildPlcUdtXml(
            [Description("udtJson: JSON object with members[]. Required member fields: name, datatype. Optional: externalWritable, commentZhCn/comment.")] string udtJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildUdt(udtJson), "PLC UDT XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC UDT builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildPlcTagTableXml"), Description("[L2][PLC-Builders][OFFLINE] Build a TIA V21 PLC tag table XML document from structured JSON. Input: {tableName,tags:[{name,dataTypeName,logicalAddress}]}. It only returns XML; it does not connect to TIA Portal, import tag tables, write files, or modify projects.")]
        public static ResponseXmlBuild BuildPlcTagTableXml(
            [Description("tagTableJson: JSON object with tableName/name and tags[]. Required tag fields: name, dataTypeName/datatype, logicalAddress/address.")] string tagTableJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildTagTable(tagTableJson), "PLC tag table XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC tag table builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildPlcGlobalDbXml"), Description("[L2][PLC-Builders][OFFLINE] Build a TIA V21 PLC GlobalDB XML document from structured JSON. Input: {dbName,dbNumber,staticMembers:[{name,datatype,externalWritable?,commentZhCn?,startValue?}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.")]
        public static ResponseXmlBuild BuildPlcGlobalDbXml(
            [Description("globalDbJson: JSON object with dbName/name, dbNumber/number, and staticMembers[] or members[].")] string globalDbJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildGlobalDb(globalDbJson), "PLC GlobalDB XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC GlobalDB builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildStructuredTextXml"), Description("[L2][PLC-Builders][OFFLINE] Build a TIA V21 StructuredText/v4 XML fragment from operation JSON. Input: {operations:[{op:'if'|'else'|'endif'|'assignment'|'token'|'blank'|'newline', ...}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.")]
        public static ResponseXmlBuild BuildStructuredTextXml(
            [Description("structuredTextJson: JSON object with operations[]. assignment uses target + literalValue/value; if uses condition/variable; token uses text.")] string structuredTextJson,
            [Description("innerOnly: true returns only inner XML for embedding into a block composer; false returns <StructuredText>.")] bool innerOnly = false)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildStructuredText(structuredTextJson, innerOnly), "PLC StructuredText XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid StructuredText builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildFlgNetCallXml"), Description("[L2][PLC-Builders][OFFLINE] NARROW SCOPE: builds ONLY a LAD network that calls one FC with parameters. For general ladder (contacts/coils/SR/compare/Move/math) author S7DCL text and import with ImportBlocksFromDocuments — there is no XML builder for those, and hand-written FlgNet XML is the usual cause of import errors. Build a TIA V21 LAD FlgNet/v5 FC call network XML from structured JSON. Input: {callName,parameters:[{name,section,dataType,sourceKind?,symbolPath?|symbol?|value?}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.")]
        public static ResponseXmlBuild BuildFlgNetCallXml(
            [Description("flgNetJson: JSON object with callName/name and parameters[]. Global parameters use symbolPath[] or dotted symbol; constants use sourceKind='constant' and value.")] string flgNetJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildFlgNetCall(flgNetJson), "PLC FlgNet call XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid FlgNet call builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "ComposePlcFcBlockXml"), Description("[L2][PLC-Builders][OFFLINE] Compose a TIA V21 SCL FC block XML from interface JSON and StructuredText content. Input: {blockName,blockNumber,inputs:[{name,datatype}],outputs:[{name,datatype}],structuredTextInnerXml? or structuredText:{operations:[]}}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.")]
        public static ResponseXmlBuild ComposePlcFcBlockXml(
            [Description("fcBlockJson: JSON object with blockName/name, blockNumber/number, inputs[], outputs[], and structuredTextInnerXml or structuredText.operations[].")] string fcBlockJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.ComposeFcBlock(fcBlockJson), "PLC FC block XML composed offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC FC composer input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "ComposePlcFbBlockXml"), Description("[L2][PLC-Builders][OFFLINE] Compose a TIA V21 SCL FB block XML from interface JSON and StructuredText content. Input: {blockName,blockNumber,inputs?,outputs?,inouts?,statics?,temps?,structuredTextInnerXml? or structuredText:{operations:[]}}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, create instance DBs, or modify projects.")]
        public static ResponseXmlBuild ComposePlcFbBlockXml(
            [Description("fbBlockJson: JSON object with blockName/name, blockNumber/number, optional inputs/outputs/inouts/statics/temps arrays, and structuredTextInnerXml or structuredText.operations[].")] string fbBlockJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.ComposeFbBlock(fbBlockJson), "PLC FB block XML composed offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC FB composer input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "ComposePlcLadFcBlockXml"), Description("[L2][PLC-Builders][OFFLINE] NARROW SCOPE: every network must be an FC call; this cannot emit contacts/coils/SR/compare/Move/math. For general ladder, author S7DCL text (.s7dcl + .s7res) and import with ImportBlocksFromDocuments instead. Compose a TIA V21 LAD FC block XML containing one or more FlgNet/v5 FC-call networks. Each network is an FC call described as { callJson: { callName, parameters[] }, titleZhCn?, commentZhCn? }. Top-level: blockName, blockNumber, optional inputs/outputs members, optional commentZhCn / titleZhCn. Returns XML only; does not connect to TIA Portal or import. Pair with ImportBlock.")]
        public static ResponseXmlBuild ComposePlcLadFcBlockXml(
            [Description("ladFcBlockJson: JSON object with blockName, blockNumber, networks[] (each with callJson{callName,parameters[]}, optional titleZhCn/commentZhCn), optional inputs[]/outputs[] interface members with commentZhCn, optional commentZhCn/titleZhCn block-level.")] string ladFcBlockJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.ComposeLadFcBlock(ladFcBlockJson), "PLC LAD FC block XML composed offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC LAD FC composer input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "PlcBuildAndImport"), Description("[L1][PLC-Software] MAIN tool for creating new PLC blocks from natural language. Build one PLC artifact (UDT/tag table/GlobalDB/FC/FB) from structured JSON, then optionally import and compile. Use dryRun=true first to validate. Workflow: describe block in JSON → dryRun → review → dryRun=false to import. Replaces the multi-step Build*Xml + ImportBlock sequence.")]
        public static ResponsePlcProgramImport PlcBuildAndImport(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'. Required only when dryRun=false.")] string softwarePath,
            [Description("kind: udt|tagtable|globaldb|fc|fb")] string kind,
            [Description("json: structured JSON matching the corresponding BuildPlc* tool.")] string json,
            [Description("typeGroupPath: PLC data type group path for kind=udt.")] string typeGroupPath = "",
            [Description("tagFolderPath: PLC tag table group path for kind=tagtable.")] string tagFolderPath = "",
            [Description("blockGroupPath: PLC block group path for kind=globaldb|fc.")] string blockGroupPath = "",
            [Description("compileAfter: compile PLC after import when dryRun=false.")] bool compileAfter = true,
            [Description("dryRun: true builds XML and returns the import plan without importing/compiling.")] bool dryRun = true)
        {
            var failed = new List<ImportFailure>();
            var importedTypes = new List<string>();
            var importedTagTables = new List<string>();
            var importedBlocks = new List<string>();
            ResponseCompile? compile = null;

            try
            {
                var normalizedKind = NormalizePlcBuildKind(kind);
                var capability = AnalyzePlcBuildCapability(normalizedKind, json);
                var build = BuildPlcArtifact(normalizedKind, json);
                var xml = build["xml"]?.ToString() ?? "";
                var objectName = ResolveBuiltPlcObjectName(xml);
                var tempDir = Path.Combine(Path.GetTempPath(), "tia_mcp_plc_build_import_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
                Directory.CreateDirectory(tempDir);
                var fileName = MakeSafeFileName(string.IsNullOrWhiteSpace(objectName) ? normalizedKind : objectName) + ".xml";
                var xmlPath = Path.Combine(tempDir, fileName);
                File.WriteAllText(xmlPath, xml, System.Text.Encoding.UTF8);

                var classifiedKind = ClassifyPlcXml(xmlPath, out var subKind, out var classifiedObjectName);
                if (classifiedKind == "unknown")
                {
                    failed.Add(new ImportFailure { Path = xmlPath, Error = "Generated XML could not be classified as a supported PLC XML artifact." });
                }

                var discoveredTypes = classifiedKind == "type" ? new List<string> { classifiedObjectName } : new List<string>();
                var discoveredTagTables = classifiedKind == "tagtable" ? new List<string> { classifiedObjectName } : new List<string>();
                var discoveredBlocks = classifiedKind == "block" ? new List<string> { classifiedObjectName } : new List<string>();

                if (!dryRun && failed.Count == 0)
                    compile = PlcBuildAndImportApply(softwarePath, typeGroupPath, tagFolderPath, blockGroupPath, compileAfter, xmlPath, classifiedKind, classifiedObjectName, importedTypes, importedTagTables, importedBlocks, failed);

                var response = BuildPlcProgramImportResponse(
                    tempDir,
                    dryRun,
                    discoveredTypes,
                    discoveredTagTables,
                    new List<string>(),
                    discoveredBlocks,
                    importedTypes,
                    importedTagTables,
                    new List<string>(),
                    importedBlocks,
                    failed,
                    compile);
                response.BuildKind = normalizedKind;
                response.CapabilityDecision = capability.Decision;
                response.CapabilityWarnings = capability.Warnings;
                response.RecommendedNextActions = capability.NextActions;
                response.GeneratedDirectory = tempDir;
                response.WrittenFiles = new[] { xmlPath };
                response.Message = dryRun
                    ? $"PLC build/import dry-run kind={normalizedKind}: generated '{xmlPath}', classified={classifiedKind}/{subKind}, failed={failed.Count}"
                    : $"PLC build/import kind={normalizedKind}: generated '{xmlPath}', importedTypes={importedTypes.Count}, importedTagTables={importedTagTables.Count}, importedBlocks={importedBlocks.Count}, failed={failed.Count}, compileState={compile?.State ?? "-"}";
                response.Meta ??= new JsonObject();
                response.Meta["offlineBuildOk"] = build["ok"]?.GetValue<bool>() == true;
                response.Meta["classifiedKind"] = classifiedKind;
                response.Meta["classifiedSubKind"] = subKind;
                response.Meta["capabilityDecision"] = capability.Decision;
                response.Meta["capabilityWarnings"] = new JsonArray(capability.Warnings.Select(x => (JsonNode)x).ToArray());
                response.Meta["recommendedNextActions"] = new JsonArray(capability.NextActions.Select(x => (JsonNode)x).ToArray());
                return response;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running PlcBuildAndImport: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        private static ResponseCompile? PlcBuildAndImportApply(
            string softwarePath,
            string typeGroupPath,
            string tagFolderPath,
            string blockGroupPath,
            bool compileAfter,
            string xmlPath,
            string classifiedKind,
            string classifiedObjectName,
            List<string> importedTypes,
            List<string> importedTagTables,
            List<string> importedBlocks,
            List<ImportFailure> failed)
        {
            if (classifiedKind == "type")
            {
                try { Portal.ImportType(softwarePath, typeGroupPath, xmlPath); importedTypes.Add(classifiedObjectName); }
                catch (PortalException pex) { failed.Add(new ImportFailure { Path = xmlPath, Error = pex.Message }); }
            }
            else if (classifiedKind == "tagtable")
            {
                try { Portal.ImportPlcTagTable(softwarePath, tagFolderPath, xmlPath); importedTagTables.Add(classifiedObjectName); }
                catch (PortalException pex) { failed.Add(new ImportFailure { Path = xmlPath, Error = pex.Message }); }
            }
            else if (classifiedKind == "block")
            {
                try { Portal.ImportBlock(softwarePath, blockGroupPath, xmlPath); importedBlocks.Add(classifiedObjectName); }
                catch (PortalException pex) { failed.Add(new ImportFailure { Path = xmlPath, Error = pex.Message }); }
            }
            else
            {
                failed.Add(new ImportFailure { Path = xmlPath, Error = "Unsupported classified kind: " + classifiedKind });
            }

            if (!compileAfter || failed.Count != 0)
                return null;

            try
            {
                var result = Portal.CompileSoftware(softwarePath);
                return BuildCompileResponse(softwarePath, result);
            }
            catch (PortalException pex)
            {
                failed.Add(new ImportFailure { Path = softwarePath, Error = $"[{pex.Code}] {pex.Message}" });
                return null;
            }
        }

        private static ResponseXmlBuild BuildOfflineXmlBuilderReport(JsonObject data, string successMessage)
        {
            var ok = data["ok"]?.GetValue<bool>() == true;
            var xml = data["xml"]?.GetValue<string>();

            // Builders use one of two error shapes:
            //   PlcBuilderToolJson:        ["error"] = string?
            //   ClassicHmi*XmlBuilder:     ["errors"] = JsonArray of string
            string[]? errorList = null;
            if (data["errors"] is JsonArray errArr)
            {
                errorList = errArr.Where(e => e != null).Select(e => e!.GetValue<string>()).ToArray();
                if (errorList.Length == 0) errorList = null;
            }
            else
            {
                var singleError = data["error"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(singleError))
                    errorList = new[] { singleError! };
            }

            string[]? warningList = null;
            if (data["warnings"] is JsonArray warnArr)
            {
                warningList = warnArr.Where(w => w != null).Select(w => w!.GetValue<string>()).ToArray();
                if (warningList.Length == 0) warningList = null;
            }

            return new ResponseXmlBuild
            {
                Ok = ok,
                Message = ok ? successMessage : successMessage + " with validation findings",
                Data = data,
                Xml = xml,
                Errors = errorList,
                Warnings = warningList,
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = ok,
                    ["offlineOnly"] = true
                }
            };
        }


        private static string NormalizePlcBuildKind(string kind)
        {
            var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
            return normalized switch
            {
                "udt" => "udt",
                "type" => "udt",
                "plcstruct" => "udt",
                "tagtable" => "tagtable",
                "plctagtable" => "tagtable",
                "globaldb" => "globaldb",
                "db" => "globaldb",
                "fc" => "fc",
                "function" => "fc",
                "fb" => "fb",
                "functionblock" => "fb",
                _ => throw new ArgumentException("Unsupported PLC build kind. Supported values: udt|tagtable|globaldb|fc|fb.")
            };
        }

        private static JsonObject BuildPlcArtifact(string kind, string json)
        {
            return kind switch
            {
                "udt" => PlcBuilderToolJson.BuildUdt(json),
                "tagtable" => PlcBuilderToolJson.BuildTagTable(json),
                "globaldb" => PlcBuilderToolJson.BuildGlobalDb(json),
                "fc" => PlcBuilderToolJson.ComposeFcBlock(json),
                "fb" => PlcBuilderToolJson.ComposeFbBlock(json),
                _ => throw new ArgumentException("Unsupported PLC build kind: " + kind)
            };
        }

        private sealed class PlcBuildCapabilityDecision
        {
            public string Decision { get; set; } = "xml-dsl";
            public List<string> Warnings { get; } = new();
            public List<string> NextActions { get; } = new();
        }

        private static PlcBuildCapabilityDecision AnalyzePlcBuildCapability(string kind, string json)
        {
            var result = new PlcBuildCapabilityDecision();
            if (kind != "fc" && kind != "fb")
            {
                result.Decision = "declaration-xml";
                result.NextActions.Add("Import generated declaration XML in dependency order, then run CompileAndDiagnosePlc.");
                return result;
            }

            JsonObject? root = null;
            try
            {
                root = JsonNode.Parse(json) as JsonObject;
            }
            catch
            {
                result.Warnings.Add("PLC JSON could not be parsed for capability analysis; builder will still validate the schema.");
                result.NextActions.Add("Fix JSON parsing/schema errors before importing into TIA Portal.");
                return result;
            }

            var structuredText = root?["structuredText"] as JsonObject;
            var operations = structuredText?["operations"] as JsonArray;
            if (operations == null)
            {
                if (root?["structuredTextInnerXml"] != null || root?["structuredTextXml"] != null || root?["sclInnerXml"] != null)
                {
                    result.Decision = "raw-structuredtext-xml";
                    result.Warnings.Add("Raw StructuredText XML was supplied. This path is only safe when cloned from a TIA export or generated by a verified builder.");
                    result.NextActions.Add("Dry-run first, import into a disposable project, then require CompileAndDiagnosePlc errors=0.");
                }
                return result;
            }

            var risky = new List<string>();
            for (var i = 0; i < operations.Count; i++)
            {
                if (operations[i] is not JsonObject op) continue;
                foreach (var name in new[] { "condition", "source", "sym", "name" })
                {
                    if (op[name] is JsonNode n && LooksLikeSclExpression(n.ToString()))
                        risky.Add($"$.structuredText.operations[{i}].{name}='{n}'");
                }
            }

            if (risky.Count > 0)
            {
                result.Decision = "external-scl-recommended";
                result.Warnings.Add("The PLC XML DSL is intentionally narrow. Complex SCL expressions were detected: " + string.Join("; ", risky.Take(8)));
                result.NextActions.Add("Prefer a native .scl/.s7dcl external source and import via ImportFromDocuments/ImportBlocksFromDocuments, or use a verified SCL template from templates/plc/scl-examples.");
                result.NextActions.Add("If you still use XML DSL, split expressions into verified primitive operations and run dryRun=true plus CompileAndDiagnosePlc.");
            }
            else
            {
                result.NextActions.Add("Run dryRun=true first, then import with compileAfter=true and require CompileAndDiagnosePlc errors=0.");
            }

            return result;
        }

        private static bool LooksLikeSclExpression(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var s = value.Trim();
            if (s.StartsWith("\"", StringComparison.Ordinal) && s.EndsWith("\"", StringComparison.Ordinal)) return false;
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (string.Equals(s, "TRUE", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "FALSE", StringComparison.OrdinalIgnoreCase)) return true;
            return s.Any(IsComplexSclToken);
        }

        private static bool IsComplexSclToken(char ch)
        {
            return char.IsWhiteSpace(ch) || ch == '(' || ch == ')' || ch == '+' || ch == '-' || ch == '*' || ch == '/' ||
                   ch == '<' || ch == '>' || ch == '=' || ch == ':' || ch == ';' || ch == ',';
        }

        private static string ResolveBuiltPlcObjectName(string xml)
        {
            try
            {
                var doc = XDocument.Parse(xml);
                var obj = doc.Root?.Elements().FirstOrDefault(e =>
                    e.Name.LocalName.StartsWith("SW.Types.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.Tags.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase));
                var attrs = obj?.Element("AttributeList");
                var name = attrs?.Element("Name")?.Value;
                return string.IsNullOrWhiteSpace(name) ? "" : name!.Trim();
            }
            catch
            {
                return "";
            }
        }

        private static string MakeSafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (string.IsNullOrWhiteSpace(name) ? "plc_artifact" : name.Trim())
                .Select(ch => invalid.Contains(ch) ? '_' : ch)
                .ToArray();
            var safe = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(safe) ? "plc_artifact" : safe;
        }

        private static ResponseCompile BuildCompileResponse(string softwarePath, object result)
        {
            var collected = new CompilerMessageCollectResult();
            try
            {
                var messagesValue = result.GetType().GetProperty("Messages")?.GetValue(result);
                collected = CollectCompilerMessages(messagesValue);
            }
            catch (Exception ex)
            {
                collected.CollectFailures.Add(ex.Message);
            }

            var state = result.GetType().GetProperty("State")?.GetValue(result)?.ToString() ?? "";
            var errorCount = ReadIntProperty(result, "ErrorCount");
            var warningCount = ReadIntProperty(result, "WarningCount");
            var summary = collected.Summary(state, errorCount, warningCount);
            return new ResponseCompile
            {
                Message = $"Software '{softwarePath}' compile state={summary["effectiveState"]}; see Meta for root counts and diagnostic scopes.",
                State = summary["effectiveState"]!.ToString(),
                ErrorCount = collected.HasError && errorCount == 0 ? (int?)null : errorCount,
                WarningCount = collected.HasWarning && warningCount == 0 ? (int?)null : warningCount,
                Messages = collected.Raw,
                Meta = summary
            };
        }

        private static int ReadIntProperty(object value, string propertyName)
        {
            var raw = value.GetType().GetProperty(propertyName)?.GetValue(value);
            if (raw is int i) return i;
            return int.TryParse(raw?.ToString(), out var parsed) ? parsed : 0;
        }

        #endregion
    }
}
