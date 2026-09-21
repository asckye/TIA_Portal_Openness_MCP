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
        #region plc software - OfflineSuites

        [McpServerTool(Name = "BuildClassicHmiTagTableXml"), Description("[L2][HMI-Classic]Offline-only helper: build a Classic/Basic WinCC HMI tag table XML document from structured JSON. Supports plain HMI tags and symbolic PLC bindings through Connection + ControllerTag/PlcTag. It does not connect to TIA Portal, import tags, or modify projects.")]
        public static ResponseXmlBuild BuildClassicHmiTagTableXml(
            [Description("tableJson: JSON object with Name/TableName and Tags[]. Tag fields: Name, DataType, Length, optional Connection and ControllerTag/PlcTag.")] string tableJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(ClassicHmiTagTableXmlBuilder.BuildFromJson(tableJson), "Classic HMI tag table XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Classic HMI tag table XML offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildClassicHmiMinimalPackage"), Description("[L2][HMI-Classic]Offline-only helper: build a minimal Classic/Basic HMI package from structured JSON. It returns tag-table XML, screen XML, import order, and readiness checks that screen item tag references are declared in the tag table. It does not connect to TIA Portal, import files, or modify projects. screen.width/height MUST equal the panel display (TP700 Comfort 800x480, TP900 800x480, TP1200 1280x800; the builder default is 640x480): a classic screen imported with another size makes TIA Portal V21 exit (crash 10, guarded by ImportHmiScreen when the panel already has a screen).")]
        public static ResponseJsonReport BuildClassicHmiMinimalPackage(
            [Description("packageJson: JSON object with Name, ScreenDesign, and TagTable. Screen items may reference HMI tags through Tag/HmiTag/ProcessValueTag or Properties.*Tag.")] string packageJson)
        {
            try
            {
                var data = ClassicHmiMinimalPackageBuilder.BuildFromJson(packageJson);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI minimal package built offline" : "Classic HMI minimal package built with validation findings",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Classic HMI minimal package offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "WriteClassicHmiMinimalPackageFiles"), Description("[L2][HMI-Classic]Offline-only helper: build a minimal Classic/Basic HMI package and write tag-table XML, screen XML, and manifest JSON to an output directory. It does not connect to TIA Portal, import files, or modify projects. screen.width/height MUST equal the panel display (TP700 Comfort 800x480, TP900 800x480, TP1200 1280x800; the builder default is 640x480): a classic screen imported with another size makes TIA Portal V21 exit (crash 10, guarded by ImportHmiScreen when the panel already has a screen).")]
        public static ResponseJsonReport WriteClassicHmiMinimalPackageFiles(
            [Description("packageJson: JSON object with Name, ScreenDesign, and TagTable.")] string packageJson,
            [Description("outputDirectory: directory where tag-table XML, screen XML, and manifest JSON will be written.")] string outputDirectory)
        {
            try
            {
                var data = ClassicHmiMinimalPackageBuilder.WriteFiles(packageJson, outputDirectory);
                var ok = data["ok"]?.GetValue<bool>() == true;

                string[]? files = null;
                if (data["files"] is JsonArray arr)
                {
                    files = arr.Where(f => f != null).Select(f => f!.GetValue<string>()).ToArray();
                    if (files.Length == 0) files = null;
                }
                var outDir = data["outputDirectory"]?.GetValue<string>();

                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI minimal package files written offline" : "Classic HMI minimal package files written with validation findings",
                    Data = data,
                    OutputPath = outDir,
                    OutputFiles = files,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["fileCount"] = data["fileCount"]?.GetValue<int>() ?? 0
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error writing Classic HMI minimal package files offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ValidateClassicHmiMinimalPackageFiles"), Description("[L2][HMI-Classic]Offline-only helper: validate an already written Classic/Basic HMI minimal package folder or manifest. It reads manifest/XML, checks parseability and HMI tag references, and does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport ValidateClassicHmiMinimalPackageFiles(
            [Description("path: package output directory or *_manifest.json path to validate.")] string path)
        {
            try
            {
                var data = ClassicHmiMinimalPackageBuilder.ValidateFiles(path);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI minimal package files validated offline" : "Classic HMI minimal package file validation found issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["missingTagCount"] = data["missingTagCount"]?.GetValue<int>() ?? 0
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error validating Classic HMI minimal package files offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ValidateClassicHmiMinimalPackagePlcSync"), Description("[L2][HMI-Classic]Offline-only helper: validate that Classic/Basic HMI tag-table ControllerTag/PlcTag bindings exist in a caller-provided exact PLC symbol list. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport ValidateClassicHmiMinimalPackagePlcSync(
            [Description("path: package output directory or *_manifest.json path to validate.")] string path,
            [Description("plcSymbolsJson: JSON array of exact PLC symbols, or object with Symbols[]. Example: [\"DB1_MotorData.Motor.Start\"].")] string plcSymbolsJson)
        {
            try
            {
                var data = ClassicHmiMinimalPackageBuilder.ValidateFilesWithPlcSymbols(path, plcSymbolsJson);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI minimal package PLC symbol sync validated offline" : "Classic HMI minimal package PLC symbol sync validation found issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["missingPlcSymbolCount"] = data["missingPlcSymbolCount"]?.GetValue<int>() ?? 0
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error validating Classic HMI PLC symbol sync offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildPlcSymbolManifestFromXmlPath"), Description("[L2][PLC-Builders]Offline-only helper: extract a PLC symbol manifest from PLC tag table and GlobalDB XML files or directories. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildPlcSymbolManifestFromXmlPath(
            [Description("path: XML file or directory containing PLC tag table / GlobalDB XML exports.")] string path)
        {
            try
            {
                var data = PlcSymbolManifestBuilder.BuildFromXmlPath(path);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "PLC symbol manifest built offline" : "PLC symbol manifest built with findings",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["symbolCount"] = data["symbolCount"]?.GetValue<int>() ?? 0
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building PLC symbol manifest offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunClassicHmiOfflineValidationSuite"), Description("[L2][HMI-Classic]Offline-only helper: run the Classic/Basic HMI validation suite covering PLC symbol extraction, HMI package generation, HMI tag references, and PLC-HMI sync positive/negative gates. It writes reports only to the requested report directory.")]
        public static ResponseJsonReport RunClassicHmiOfflineValidationSuite(
            [Description("reportDirectory: directory where suite files and reports will be written.")] string reportDirectory)
        {
            try
            {
                var data = ClassicHmiOfflineValidationSuite.Run(reportDirectory);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI offline validation suite passed" : "Classic HMI offline validation suite found issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running Classic HMI offline validation suite: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunOfflineReleaseValidationSuite"), Description("[L2][Validation]Offline-only helper: run the release smoke suite covering PLC Builder, Classic HMI, PLC symbol extraction, Unified HMI template layout, HMI action recipes, and online-monitoring safety guardrails. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport RunOfflineReleaseValidationSuite(
            [Description("workspaceRoot: repository/workspace root containing TMP_EXPORT, docs, and tools.")] string workspaceRoot,
            [Description("reportDirectory: directory where suite files and reports will be written.")] string reportDirectory)
        {
            try
            {
                var data = OfflineReleaseValidationSuite.Run(workspaceRoot, reportDirectory);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Offline release validation suite passed" : "Offline release validation suite found issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running offline release validation suite: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }


        [McpServerTool(Name = "BuildReleaseDiagnosticReport"), Description("[L2][Reports]Build an offline diagnostic report from a previously generated OfflineReleaseValidationSuite JSON report. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildReleaseDiagnosticReport(
            [Description("offlineReleaseSuiteJsonPath: path to offline_release_validation_suite_*.json.")] string offlineReleaseSuiteJsonPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(offlineReleaseSuiteJsonPath) || !File.Exists(offlineReleaseSuiteJsonPath))
                    throw new FileNotFoundException("Offline release suite JSON report not found.", offlineReleaseSuiteJsonPath);
                var root = JsonNode.Parse(File.ReadAllText(offlineReleaseSuiteJsonPath)) as JsonObject
                    ?? throw new InvalidOperationException("Offline release suite JSON root must be an object.");
                var data = ReleaseDiagnosticReportBuilder.Build(root);
                return new ResponseJsonReport
                {
                    Ok = data["ok"]?.GetValue<bool>() == true,
                    Message = "Release diagnostic report built.",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building release diagnostic report: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildReleaseRunbook"), Description("[L2][Reports]Build an offline first-user runbook from a previously generated OfflineReleaseValidationSuite JSON report. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildReleaseRunbook(
            [Description("offlineReleaseSuiteJsonPath: path to offline_release_validation_suite_*.json.")] string offlineReleaseSuiteJsonPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(offlineReleaseSuiteJsonPath) || !File.Exists(offlineReleaseSuiteJsonPath))
                    throw new FileNotFoundException("Offline release suite JSON report not found.", offlineReleaseSuiteJsonPath);
                var root = JsonNode.Parse(File.ReadAllText(offlineReleaseSuiteJsonPath)) as JsonObject
                    ?? throw new InvalidOperationException("Offline release suite JSON root must be an object.");
                var diagnostics = root["diagnostics"] as JsonObject ?? ReleaseDiagnosticReportBuilder.Build(root);
                var data = ReleaseRunbookBuilder.Build(root, diagnostics);
                return new ResponseJsonReport
                {
                    Ok = data["ok"]?.GetValue<bool>() == true,
                    Message = "Release runbook built.",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building release runbook: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildReleaseManifest"), Description("[L2][Reports]Build an offline machine-readable release manifest from a previously generated OfflineReleaseValidationSuite JSON report. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildReleaseManifest(
            [Description("offlineReleaseSuiteJsonPath: path to offline_release_validation_suite_*.json.")] string offlineReleaseSuiteJsonPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(offlineReleaseSuiteJsonPath) || !File.Exists(offlineReleaseSuiteJsonPath))
                    throw new FileNotFoundException("Offline release suite JSON report not found.", offlineReleaseSuiteJsonPath);
                var root = JsonNode.Parse(File.ReadAllText(offlineReleaseSuiteJsonPath)) as JsonObject
                    ?? throw new InvalidOperationException("Offline release suite JSON root must be an object.");
                var diagnostics = root["diagnostics"] as JsonObject ?? ReleaseDiagnosticReportBuilder.Build(root);
                var runbook = root["runbook"] as JsonObject ?? ReleaseRunbookBuilder.Build(root, diagnostics);
                var data = ReleaseManifestBuilder.Build(root, diagnostics, runbook);
                return new ResponseJsonReport
                {
                    Ok = data["ok"]?.GetValue<bool>() == true,
                    Message = "Release manifest built.",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building release manifest: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RebuildReleaseHandoffArtifacts"), Description("[L2][Reports]Rebuild diagnostics, runbook, and manifest files from an existing OfflineReleaseValidationSuite JSON report. Offline-only and does not connect to TIA Portal.")]
        public static ResponseJsonReport RebuildReleaseHandoffArtifacts(
            [Description("offlineReleaseSuiteJsonPath: path to offline_release_validation_suite_*.json.")] string offlineReleaseSuiteJsonPath,
            [Description("outputDirectory: directory where rebuilt handoff artifacts will be written.")] string outputDirectory)
        {
            try
            {
                var data = ReleaseHandoffArtifactBuilder.RebuildFromSuiteJson(offlineReleaseSuiteJsonPath, outputDirectory);
                return new ResponseJsonReport
                {
                    Ok = data["ok"]?.GetValue<bool>() == true,
                    Message = "Release handoff artifacts rebuilt.",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error rebuilding release handoff artifacts: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunClassicHmiTemporaryImportPreflight"), Description("[L2][HMI-Classic]Offline-only helper: run the Classic/Basic HMI temporary-import preflight. It checks TIA V21 environment, Openness group, package files, PLC-HMI sync, and emits an import/readback plan without connecting to TIA Portal or creating projects.")]
        public static ResponseJsonReport RunClassicHmiTemporaryImportPreflight(
            [Description("workspaceRoot: repository/workspace root.")] string workspaceRoot,
            [Description("reportDirectory: directory where preflight files and reports will be written.")] string reportDirectory)
        {
            try
            {
                var data = ClassicHmiTemporaryImportPreflightSuite.Run(workspaceRoot, reportDirectory);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI temporary import preflight passed" : "Classic HMI temporary import preflight blocked",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running Classic HMI temporary import preflight: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunHmiTemplatePlcSyncPrecheckSuite"), Description("[L2][Validation]Offline-only helper: verify Unified HMI template RequiredTags against real PLC tag/DB-member XML symbols before any HMI binding. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport RunHmiTemplatePlcSyncPrecheckSuite(
            [Description("templateDirectory: directory containing Unified HMI template JSON files.")] string templateDirectory,
            [Description("plcXmlPath: PLC XML file or directory exported from TIA, containing tag tables and/or GlobalDB XML.")] string plcXmlPath,
            [Description("reportDirectory: directory where reports will be written.")] string reportDirectory,
            [Description("mappingFilePath: optional explicit mapping file produced by the mapping skeleton flow.")] string mappingFilePath = "")
        {
            try
            {
                var data = HmiTemplatePlcSyncPrecheckSuite.Run(templateDirectory, plcXmlPath, reportDirectory, mappingFilePath);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "HMI template PLC sync precheck suite completed" : "HMI template PLC sync precheck suite found blocking issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running HMI template PLC sync precheck suite: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildUnifiedHmiTemplateApplyDesignJson"), Description("[L2][HMI-Unified]Offline-only helper: convert one Unified HMI template JSON file into the execution JSON accepted by ApplyUnifiedHmiScreenDesignJson, with layout QA attached. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildUnifiedHmiTemplateApplyDesignJson(
            [Description("templateFile: full path to a Unified HMI template JSON file")] string templateFile,
            [Description("fallbackWidth: width used only if the template omits Screen.Width")] int fallbackWidth = 800,
            [Description("fallbackHeight: height used only if the template omits Screen.Height")] int fallbackHeight = 480)
        {
            try
            {
                var layout = HmiTemplateLayoutAnalyzer.AnalyzeFile(templateFile, path =>
                {
                    var templateRoot = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                    var expectedItems = (templateRoot?["Items"] as JsonArray ?? templateRoot?["items"] as JsonArray ?? new JsonArray()).Count;
                    var design = HmiTemplateDesignJsonBuilder.BuildApplyDesign(path, fallbackWidth, fallbackHeight);
                    return design["items"] is JsonArray executionItems && executionItems.Count == expectedItems;
                });
                var designJson = HmiTemplateDesignJsonBuilder.BuildApplyDesign(templateFile, fallbackWidth, fallbackHeight);
                var ok = string.Equals(layout["status"]?.ToString(), "pass", StringComparison.OrdinalIgnoreCase);
                var itemCount = (designJson["items"] as JsonArray)?.Count ?? 0;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Unified HMI template execution design JSON built offline" : "Unified HMI template execution design JSON built with blocking layout findings",
                    Data = new JsonObject
                    {
                        ["format"] = "tia-unified-hmi-template-apply-design-offline-v1",
                        ["timestamp"] = DateTime.Now.ToString("O"),
                        ["offlineOnly"] = true,
                        ["templateFile"] = templateFile,
                        ["ok"] = ok,
                        ["itemCount"] = itemCount,
                        ["layoutQa"] = layout,
                        ["applyDesign"] = designJson
                    },
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["itemCount"] = itemCount
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Unified HMI template execution design JSON: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildUnifiedHmiTemplateApplyDesignManifest"), Description("[L2][HMI-Unified]Offline-only helper: build a directory-level manifest for Unified HMI templates. It summarizes layout QA and execution-design readiness for every unified_*.json template without returning full apply payloads. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildUnifiedHmiTemplateApplyDesignManifest(
            [Description("templateDirectory: directory containing Unified HMI JSON templates")] string templateDirectory,
            [Description("fallbackWidth: width used only if a template omits Screen.Width")] int fallbackWidth = 800,
            [Description("fallbackHeight: height used only if a template omits Screen.Height")] int fallbackHeight = 480)
        {
            try
            {
                var files = Directory.Exists(templateDirectory)
                    ? Directory.GetFiles(templateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                        .Where(path => Path.GetFileName(path).StartsWith("unified_", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : Array.Empty<string>();
                var rows = new JsonArray();
                var referenceAnalysis = HmiTemplateReferenceAnalyzer.Analyze(templateDirectory, "", "");
                var referenceRows = (referenceAnalysis["templates"] as JsonArray ?? new JsonArray())
                    .OfType<JsonObject>()
                    .ToDictionary(
                        x => x["templateName"]?.ToString() ?? "",
                        x => x,
                        StringComparer.OrdinalIgnoreCase);
                var referenceRowsByFile = (referenceAnalysis["templates"] as JsonArray ?? new JsonArray())
                    .OfType<JsonObject>()
                    .Where(x => !string.IsNullOrWhiteSpace(x["file"]?.ToString()))
                    .ToDictionary(
                        x => Path.GetFullPath(x["file"]?.ToString() ?? ""),
                        x => x,
                        StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    var templateName = Path.GetFileNameWithoutExtension(file);
                    referenceRowsByFile.TryGetValue(Path.GetFullPath(file), out var referenceRow);
                    if (referenceRow == null)
                    {
                        referenceRows.TryGetValue(templateName, out referenceRow);
                    }
                    rows.Add(BuildUnifiedHmiTemplateApplyDesignManifestRow(file, fallbackWidth, fallbackHeight, referenceRow));
                }

                var failed = rows.OfType<JsonObject>().Count(x => x["ok"]?.GetValue<bool>() != true);
                var totalItems = rows.OfType<JsonObject>().Sum(x => x["itemCount"]?.GetValue<int>() ?? 0);
                var root = new JsonObject
                {
                    ["format"] = "tia-unified-hmi-template-apply-design-manifest-v1",
                    ["timestamp"] = DateTime.Now.ToString("O"),
                    ["offlineOnly"] = true,
                    ["templateDirectory"] = templateDirectory,
                    ["templateCount"] = files.Length,
                    ["failed"] = failed,
                    ["totalItems"] = totalItems,
                    ["ok"] = failed == 0,
                    ["policy"] = new JsonObject
                    {
                        ["fullPayloadTool"] = "BuildUnifiedHmiTemplateApplyDesignJson",
                        ["applyTool"] = "ApplyUnifiedHmiScreenDesignJson",
                        ["rule"] = "Use this manifest as a pre-apply gate; inspect a full payload for any template before writing it to TIA."
                    },
                    ["templates"] = rows
                };

                var ok = failed == 0;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Unified HMI template execution design manifest built offline" : "Unified HMI template execution design manifest has blocking findings",
                    Data = root,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["templateCount"] = files.Length,
                        ["failed"] = failed,
                        ["totalItems"] = totalItems
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Unified HMI template execution design manifest: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        private static JsonObject BuildUnifiedHmiTemplateApplyDesignManifestRow(string templateFile, int fallbackWidth, int fallbackHeight, JsonObject? referenceRow)
        {
            var layout = HmiTemplateLayoutAnalyzer.AnalyzeFile(templateFile, path =>
            {
                var templateRoot = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                var expectedItems = (templateRoot?["Items"] as JsonArray ?? templateRoot?["items"] as JsonArray ?? new JsonArray()).Count;
                var design = HmiTemplateDesignJsonBuilder.BuildApplyDesign(path, fallbackWidth, fallbackHeight);
                return design["items"] is JsonArray executionItems && executionItems.Count == expectedItems;
            });
            var designJson = HmiTemplateDesignJsonBuilder.BuildApplyDesign(templateFile, fallbackWidth, fallbackHeight);
            var items = designJson["items"] as JsonArray ?? new JsonArray();
            var errors = layout["errors"] as JsonArray ?? new JsonArray();
            var warnings = layout["warnings"] as JsonArray ?? new JsonArray();
            var ok = string.Equals(layout["status"]?.ToString(), "pass", StringComparison.OrdinalIgnoreCase);
            var eventReadiness = BuildUnifiedHmiTemplateEventReadiness(referenceRow);
            var row = new JsonObject
            {
                ["templateFile"] = templateFile,
                ["templateName"] = layout["templateName"]?.DeepClone(),
                ["ok"] = ok,
                ["status"] = layout["status"]?.DeepClone(),
                ["width"] = designJson["width"]?.DeepClone(),
                ["height"] = designJson["height"]?.DeepClone(),
                ["itemCount"] = items.Count,
                ["errorCount"] = errors.Count,
                ["warningCount"] = warnings.Count,
                ["errors"] = new JsonArray(errors.Select(x => x?.DeepClone()).ToArray()),
                ["warnings"] = new JsonArray(warnings.Select(x => x?.DeepClone()).ToArray()),
                ["layoutDensity"] = layout["layoutDensity"]?.DeepClone(),
                ["applyDesignReady"] = layout["executionJsonChecked"]?.DeepClone(),
                ["requiredTagCount"] = GetManifestInt(referenceRow, "requiredTagCount"),
                ["dynamizationCount"] = GetManifestInt(referenceRow, "dynamizationCount"),
                ["actionCount"] = GetManifestInt(referenceRow, "actionCount"),
                ["eventReadiness"] = eventReadiness,
                ["recommendedNextAction"] = ok
                    ? "Inspect full payload with BuildUnifiedHmiTemplateApplyDesignJson, then apply in a temporary TIA project before using a real project."
                    : "Fix blocking layout/template findings before applying to TIA."
            };
            row["eventRecommendedNextAction"] = eventReadiness["recommendedNextAction"]?.DeepClone();
            return row;
        }

        private static JsonObject BuildUnifiedHmiTemplateEventReadiness(JsonObject? referenceRow)
        {
            if (referenceRow == null)
            {
                return new JsonObject
                {
                    ["status"] = "not-analyzed",
                    ["effectiveActionCount"] = 0,
                    ["safeDeterministicActionCount"] = 0,
                    ["apiDiscoveryRequiredCount"] = 0,
                    ["highRiskActionCount"] = 0,
                    ["todoActionCount"] = 0,
                    ["missingTargetCount"] = 0,
                    ["duplicateActionCount"] = 0,
                    ["commandActionCount"] = 0,
                    ["navigationActionCount"] = 0,
                    ["recommendedNextAction"] = "Run HmiTemplateReferenceAnalyzer first; event and binding readiness could not be joined for this template."
                };
            }

            var summary = referenceRow["actionRecipeSummary"] as JsonObject ?? new JsonObject();
            var effectiveRecipes = summary["effectiveRecipes"] as JsonArray ?? new JsonArray();
            var generated = new JsonArray();
            var safeDeterministic = 0;
            var apiDiscovery = 0;
            var todo = 0;
            var blocked = 0;
            var command = 0;
            var navigation = 0;

            foreach (var recipeNode in effectiveRecipes.OfType<JsonObject>())
            {
                var targetTags = (recipeNode["targetTags"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString() ?? "");
                var built = HmiActionScriptRecipeBuilder.Build(
                    recipeNode["recipeKind"]?.ToString() ?? "",
                    recipeNode["event"]?.ToString() ?? "",
                    targetTags,
                    recipeNode["targetScreen"]?.ToString() ?? "",
                    recipeNode["targetPopup"]?.ToString() ?? "");
                generated.Add(built);
                var kind = built["recipeKind"]?.ToString() ?? "";
                var safety = built["safetyLevel"]?.ToString() ?? "";
                if (string.Equals(safety, "command", StringComparison.OrdinalIgnoreCase)) command++;
                if (string.Equals(safety, "navigation", StringComparison.OrdinalIgnoreCase)) navigation++;
                if (built["requiresApiDiscovery"]?.GetValue<bool>() == true) apiDiscovery++;
                if (built["applyBlocked"]?.GetValue<bool>() == true || !string.IsNullOrWhiteSpace(built["applyBlockedReason"]?.ToString())) blocked++;
                if ((built["script"]?.ToString() ?? "").IndexOf("TODO", StringComparison.OrdinalIgnoreCase) >= 0) todo++;
                if (built["ok"]?.GetValue<bool>() == true
                    && built["requiresApiDiscovery"]?.GetValue<bool>() != true
                    && (kind.Equals("set-bit", StringComparison.OrdinalIgnoreCase)
                        || kind.Equals("reset-bit", StringComparison.OrdinalIgnoreCase)
                        || kind.Equals("toggle-bit", StringComparison.OrdinalIgnoreCase)))
                {
                    safeDeterministic++;
                }
            }

            var missingTargets = summary["missingTargets"] as JsonArray ?? new JsonArray();
            var duplicateActions = summary["duplicateActions"] as JsonArray ?? new JsonArray();
            var highRisk = GetManifestInt(summary, "highRiskWrites");
            var missingRequiredTags = summary["missingRequiredTags"] as JsonArray ?? new JsonArray();
            var status = "ready-for-temp-project-validation";
            var recommended = "Generate safe deterministic scripts, then verify HMI tags, PLC-side symbols, TIA SyntaxCheck, and readback in a temporary project.";

            if (missingRequiredTags.Count > 0 || missingTargets.Count > 0 || duplicateActions.Count > 0)
            {
                status = "needs-template-fix";
                recommended = "Fix missing action tags, missing target screens/popups, or duplicate actions before applying events.";
            }
            else if (highRisk > 0)
            {
                status = "blocked-by-high-risk-actions";
                recommended = "High-risk value writes require explicit operator confirmation, range validation, SyntaxCheck, and readback before any apply path is enabled.";
            }
            else if (apiDiscovery > 0 || todo > 0)
            {
                status = "needs-api-discovery";
                recommended = "Keep API-discovery actions blocked until the exact WinCC Unified V21 event/navigation/popup API is verified from TIA readback.";
            }

            return new JsonObject
            {
                ["status"] = status,
                ["requiredTagCount"] = GetManifestInt(referenceRow, "requiredTagCount"),
                ["dynamizationCount"] = GetManifestInt(referenceRow, "dynamizationCount"),
                ["actionCount"] = GetManifestInt(referenceRow, "actionCount"),
                ["effectiveActionCount"] = GetManifestInt(summary, "effectiveActionCount"),
                ["safeDeterministicActionCount"] = safeDeterministic,
                ["apiDiscoveryRequiredCount"] = apiDiscovery,
                ["blockedActionCount"] = blocked,
                ["highRiskActionCount"] = highRisk,
                ["todoActionCount"] = todo,
                ["missingRequiredTagCount"] = missingRequiredTags.Count,
                ["missingTargetCount"] = missingTargets.Count,
                ["duplicateActionCount"] = duplicateActions.Count,
                ["commandActionCount"] = command,
                ["navigationActionCount"] = navigation,
                ["recommendedNextAction"] = recommended
            };
        }

        private static int GetManifestInt(JsonObject? obj, string name)
        {
            if (obj == null || obj[name] == null) return 0;
            return int.TryParse(obj[name]?.ToString(), out var value) ? value : 0;
        }

        #endregion
    }
}
