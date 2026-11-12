using TiaMcp.Logic.V4.Hmi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
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
    [McpServerToolType]
    public sealed partial class OfflineSuiteTools
    {
        #region plc software - OfflineSuites

        [McpServerTool(Name = "BuildClassicHmiTagTable"),
                Description("[L2][HMI-Classic]Offline-only helper: build a Classic/Basic WinCC HMI tag table XML document from structured JSON. Supports " +
                "plain HMI tags and symbolic PLC bindings through Connection + ControllerTag/PlcTag. It does not connect to TIA Portal, import tags, or " +
                "modify projects.")]
        public CallToolResult BuildClassicHmiTagTableXmlV4(
            [Description("table: ClassicTagTableSpec. Supply the structured value directly; exact camelCase fields, no null or JSON string " +
                "encoding.")] ClassicTagTableSpec table)
            => OfflineContracts
                .Run("BuildClassicHmiTagTable", () => BuildClassicHmiTagTableXml(table.ToBuilderInput().ToJsonString()), writes: false, current: false);

        public ResponseXmlBuild BuildClassicHmiTagTableXml(
            [Description("tableJson: JSON object with Name/TableName and Tags[]. Tag fields: Name, DataType, Length, optional Connection and " +
                "ControllerTag/PlcTag.")] string tableJson)
        {
            try
            {
                return XmlBuildResults
                    .BuildOfflineXmlBuilderReport(ClassicHmiTagTableXmlBuilder.BuildFromJson(tableJson), "Classic HMI tag table XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Classic HMI tag table XML offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "BuildClassicHmiMinimalPackage"),
                Description("[L2][HMI-Classic]Offline-only helper: build a minimal Classic/Basic HMI package from structured JSON. It returns tag-table XML, " +
                "screen XML, import order, and readiness checks that screen item tag references are declared in the tag table. It does not connect to TIA " +
                "Portal, import files, or modify projects. screen.width/height MUST equal the panel display (TP700 Comfort 800x480, TP900 800x480, TP1200 " +
                "1280x800; the builder default is 640x480): a classic screen imported with another size makes TIA Portal V21 exit (crash 10, guarded by " +
                "ImportHmiScreen when the panel already has a screen).")]
        public CallToolResult BuildClassicHmiMinimalPackageV4(
            [Description("package: ClassicPackageSpec. Supply the structured value directly; exact camelCase fields, no null or JSON string " +
                "encoding.")] ClassicPackageSpec package)
            => OfflineContracts
                .Run("BuildClassicHmiMinimalPackage", () => BuildClassicHmiMinimalPackage(package.ToBuilderInput().ToJsonString()), writes: false, current
                    : false);

        public ResponseJsonReport BuildClassicHmiMinimalPackage(
            [Description("packageJson: JSON object with Name, ScreenDesign, and TagTable. Screen items may reference HMI tags through " +
                "Tag/HmiTag/ProcessValueTag or Properties.*Tag.")] string packageJson)
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
                    Meta = ResponseMeta.Basic(DateTime.Now, ok, ("offlineOnly", true))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Classic HMI minimal package offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "WriteClassicHmiMinimalPackageFiles"),
                Description("[L2][HMI-Classic]Offline-only helper: build a minimal Classic/Basic HMI package and write tag-table XML, screen XML, and " +
                "manifest JSON to an output directory. It does not connect to TIA Portal, import files, or modify projects. screen.width/height MUST equal " +
                "the panel display (TP700 Comfort 800x480, TP900 800x480, TP1200 1280x800; the builder default is 640x480): a classic screen imported with " +
                "another size makes TIA Portal V21 exit (crash 10, guarded by ImportHmiScreen when the panel already has a screen).")]
        public CallToolResult WriteClassicHmiMinimalPackageFilesV4(
            [Description("package: ClassicPackageSpec. Supply the structured value directly; exact camelCase fields, no null or JSON string " +
                "encoding.")] ClassicPackageSpec package,
            [Description("outputDirectory: directory where tag-table XML, screen XML, and manifest JSON will be written.")] string outputDirectory)
            => OfflineContracts
                .Run("WriteClassicHmiMinimalPackageFiles", () => WriteClassicHmiMinimalPackageFiles(package.ToBuilderInput().ToJsonString(), outputDirectory),
                    writes: true, current: false);

        public ResponseJsonReport WriteClassicHmiMinimalPackageFiles(
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
                    Meta = ResponseMeta.Basic(DateTime.Now, ok, ("offlineOnly", true), ("fileCount", data["fileCount"]?.GetValue<int>() ?? 0))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error writing Classic HMI minimal package files offline: {ex.Message}{McpHints.Recovery(ex)}", ex,
                        McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ValidateClassicHmiMinimalPackageFiles"),
                Description("[L2][HMI-Classic]Offline-only helper: validate an already written Classic/Basic HMI minimal package folder or manifest. It " +
                "reads manifest/XML, checks parseability and HMI tag references, and does not connect to TIA Portal or modify projects.")]
        public CallToolResult ValidateClassicHmiMinimalPackageFilesV4(
            [Description("path: package output directory or *_manifest.json path to validate.")] string path)
            => OfflineContracts.Run("ValidateClassicHmiMinimalPackageFiles", () => ValidateClassicHmiMinimalPackageFiles(path), writes: false, current: false);

        public ResponseJsonReport ValidateClassicHmiMinimalPackageFiles(
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
                    Meta = ResponseMeta.Basic(DateTime.Now, ok, ("offlineOnly", true), ("missingTagCount", data["missingTagCount"]?.GetValue<int>() ?? 0))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error validating Classic HMI minimal package files offline: {ex.Message}{McpHints.Recovery(ex)}", ex,
                        McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ValidateClassicHmiMinimalPackagePlcSync"),
                Description("[L2][HMI-Classic]Offline-only helper: validate that Classic/Basic HMI tag-table ControllerTag/PlcTag bindings exist in a " +
                "caller-provided exact PLC symbol list. It does not connect to TIA Portal or modify projects.")]
        public CallToolResult ValidateClassicHmiMinimalPackagePlcSyncV4(
            [Description("path: package output directory or *_manifest.json path to validate.")] string path,
            [Description("plcSymbols: string[]. Supply the structured value directly; exact camelCase fields, no null or JSON string " +
                "encoding.")] string[] plcSymbols)
            => OfflineContracts
                .Run("ValidateClassicHmiMinimalPackagePlcSync", () => ValidateClassicHmiMinimalPackagePlcSync(path, OfflineContracts.Names(plcSymbols, null)),
                    writes: false, current: false);

        public ResponseJsonReport ValidateClassicHmiMinimalPackagePlcSync(
            [Description("path: package output directory or *_manifest.json path to validate.")] string path,
            [Description("plcSymbolsJson: JSON array of exact PLC symbols, or object with Symbols[]. Example: " +
                "[\"DB1_MotorData.Motor.Start\"].")] string plcSymbolsJson)
        {
            try
            {
                var data = ClassicHmiMinimalPackageBuilder.ValidateFilesWithPlcSymbols(path, plcSymbolsJson);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI minimal package PLC symbol sync validated offline"
                        : "Classic HMI minimal package PLC symbol sync validation found issues",
                    Data = data,
                    Meta = ResponseMeta
                        .Basic(DateTime.Now, ok, ("offlineOnly", true), ("missingPlcSymbolCount", data["missingPlcSymbolCount"]?.GetValue<int>() ?? 0))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error validating Classic HMI PLC symbol sync offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "BuildPlcSymbolManifestFromPath"),
                Description("[L2][PLC-Builders] Offline-only helper: extract PLC tag and GlobalDB declarations from XML files or directories. Empty tag " +
                "tables are valid, tag comments are ignored, user constants include their raw value, and system constants are skipped with a warning. It " +
                "does not connect to TIA Portal or modify projects.")]
        public CallToolResult BuildPlcSymbolManifestFromXmlPathV4(
            [Description("path: XML file or directory containing PLC tag table / GlobalDB XML exports.")] string path)
            => OfflineContracts.Run("BuildPlcSymbolManifestFromPath", () => BuildPlcSymbolManifestFromXmlPath(path), writes: false, current: false);

        public ResponseJsonReport BuildPlcSymbolManifestFromXmlPath(
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
                    Meta = ResponseMeta.Basic(DateTime.Now, ok, ("offlineOnly", true), ("symbolCount", data["symbolCount"]?.GetValue<int>() ?? 0))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building PLC symbol manifest offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "RunClassicHmiOfflineValidationSuite"),
                Description("[L2][HMI-Classic]Offline-only helper: run the Classic/Basic HMI validation suite covering PLC symbol extraction, HMI package " +
                "generation, HMI tag references, and PLC-HMI sync positive/negative gates. It writes reports only to the requested report directory.")]
        public CallToolResult RunClassicHmiOfflineValidationSuiteV4(
            [Description("reportDirectory: directory where suite files and reports will be written.")] string reportDirectory)
            => OfflineContracts
                .Run("RunClassicHmiOfflineValidationSuite", () => RunClassicHmiOfflineValidationSuite(reportDirectory), writes: true, current: false);

        public ResponseJsonReport RunClassicHmiOfflineValidationSuite(
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
                    Meta = ResponseMeta.Basic(DateTime.Now, ok, ("offlineOnly", true))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running Classic HMI offline validation suite: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "RunOfflineReleaseValidationSuite"),
                Description("[L2][Validation]Offline-only helper: run the release smoke suite covering PLC Builder, Classic HMI, PLC symbol extraction, " +
                "Unified HMI template layout, HMI action recipes, and online-monitoring safety guardrails. It does not connect to TIA Portal or modify " +
                "projects.")]
        public CallToolResult RunOfflineReleaseValidationSuiteV4(
            [Description("workspaceRoot: repository/workspace root containing TMP_EXPORT, docs, and tools.")] string workspaceRoot,
            [Description("reportDirectory: directory where suite files and reports will be written.")] string reportDirectory)
            => OfflineContracts
                .Run("RunOfflineReleaseValidationSuite", () => RunOfflineReleaseValidationSuite(OfflineContracts.Workspace(workspaceRoot), reportDirectory),
                    writes: true, current: false);

        public ResponseJsonReport RunOfflineReleaseValidationSuite(
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
                    Meta = ResponseMeta.Basic(DateTime.Now, ok, ("offlineOnly", true))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running offline release validation suite: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }


        [McpServerTool(Name = "BuildReleaseDiagnosticReport"),
                Description("[L2][Reports]Build an offline diagnostic report from a previously generated OfflineReleaseValidationSuite JSON report. It does " +
                "not connect to TIA Portal or modify projects.")]
        public CallToolResult BuildReleaseDiagnosticReportV4(
            [Description("offlineReleaseSuiteJsonPath: path to offline_release_validation_suite_*.json.")] string offlineReleaseSuiteJsonPath)
            => OfflineContracts
                .Run("BuildReleaseDiagnosticReport", () => BuildReleaseDiagnosticReport(offlineReleaseSuiteJsonPath), writes: false, current: false);

        public ResponseJsonReport BuildReleaseDiagnosticReport(
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
                    Meta = ResponseMeta.Basic(DateTime.Now, true, ("offlineOnly", true))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building release diagnostic report: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "BuildReleaseRunbook"),
                Description("[L2][Reports]Build an offline first-user runbook from a previously generated OfflineReleaseValidationSuite JSON report. It does " +
                "not connect to TIA Portal or modify projects.")]
        public CallToolResult BuildReleaseRunbookV4(
            [Description("offlineReleaseSuiteJsonPath: path to offline_release_validation_suite_*.json.")] string offlineReleaseSuiteJsonPath)
            => OfflineContracts.Run("BuildReleaseRunbook", () => BuildReleaseRunbook(offlineReleaseSuiteJsonPath), writes: false, current: false);

        public ResponseJsonReport BuildReleaseRunbook(
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
                    Meta = ResponseMeta.Basic(DateTime.Now, true, ("offlineOnly", true))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building release runbook: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildReleaseManifest"),
                Description("[L2][Reports]Build an offline machine-readable release manifest from a previously generated OfflineReleaseValidationSuite JSON " +
                "report. It does not connect to TIA Portal or modify projects.")]
        public CallToolResult BuildReleaseManifestV4(
            [Description("offlineReleaseSuiteJsonPath: path to offline_release_validation_suite_*.json.")] string offlineReleaseSuiteJsonPath)
            => OfflineContracts.Run("BuildReleaseManifest", () => BuildReleaseManifest(offlineReleaseSuiteJsonPath), writes: false, current: false);

        public ResponseJsonReport BuildReleaseManifest(
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
                    Meta = ResponseMeta.Basic(DateTime.Now, true, ("offlineOnly", true))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building release manifest: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildReleaseHandoffArtifacts"),
                Description("[L2][Reports]Rebuild diagnostics, runbook, and manifest files from an existing OfflineReleaseValidationSuite JSON report. " +
                "Offline-only and does not connect to TIA Portal.")]
        public CallToolResult RebuildReleaseHandoffArtifactsV4(
            [Description("offlineReleaseSuiteJsonPath: path to offline_release_validation_suite_*.json.")] string offlineReleaseSuiteJsonPath,
            [Description("outputDirectory: directory where rebuilt handoff artifacts will be written.")] string outputDirectory)
            => OfflineContracts
                .Run("BuildReleaseHandoffArtifacts", () => RebuildReleaseHandoffArtifacts(offlineReleaseSuiteJsonPath, outputDirectory), writes: true, current
                    : false);

        public ResponseJsonReport RebuildReleaseHandoffArtifacts(
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
                    Meta = ResponseMeta.Basic(DateTime.Now, true, ("offlineOnly", true))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error rebuilding release handoff artifacts: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "RunClassicHmiTemporaryImportPreflight"),
                Description("[L2][HMI-Classic]Offline-only helper: run the Classic/Basic HMI temporary-import preflight. It checks TIA V21 environment, " +
                "Openness group, package files, PLC-HMI sync, and emits an import/readback plan without connecting to TIA Portal or creating projects.")]
        public CallToolResult RunClassicHmiTemporaryImportPreflightV4(
            [Description("workspaceRoot: repository/workspace root.")] string workspaceRoot,
            [Description("reportDirectory: directory where preflight files and reports will be written.")] string reportDirectory)
            => OfflineContracts
                .Run("RunClassicHmiTemporaryImportPreflight", () =>
                    RunClassicHmiTemporaryImportPreflight(OfflineContracts.Workspace(workspaceRoot), reportDirectory), writes: true, current: false);

        public ResponseJsonReport RunClassicHmiTemporaryImportPreflight(
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
                    Meta = ResponseMeta.Basic(DateTime.Now, ok, ("offlineOnly", true))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running Classic HMI temporary import preflight: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "RunHmiTemplatePlcSyncPrecheckSuite"),
                Description("[L2][Validation]Offline-only helper: verify Unified HMI template RequiredTags against real PLC tag/DB-member XML symbols before " +
                "any HMI binding. It does not connect to TIA Portal or modify projects.")]
        public CallToolResult RunHmiTemplatePlcSyncPrecheckSuiteV4(
            [Description("templateDirectory: directory containing Unified HMI template JSON files.")] string templateDirectory,
            [Description("plcXmlPath: PLC XML file or directory exported from TIA, containing tag tables and/or GlobalDB XML.")] string plcXmlPath,
            [Description("reportDirectory: directory where reports will be written.")] string reportDirectory,
            [Description("mappingFilePath: optional explicit mapping file produced by the mapping skeleton flow.")] string mappingFilePath = "")
            => OfflineContracts
                .Run("RunHmiTemplatePlcSyncPrecheckSuite", () =>
                    RunHmiTemplatePlcSyncPrecheckSuite(templateDirectory, plcXmlPath, reportDirectory, mappingFilePath), writes: true, current: HardwareContract
                    .ReleaseKey == "19");

        public ResponseJsonReport RunHmiTemplatePlcSyncPrecheckSuite(
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
                    Message = ok ? "HMI template PLC sync precheck suite completed"
                        : "HMI template PLC sync precheck suite found blocking issues: "
                            + string.Join("; ", (data["blockingReasons"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString() ?? "")),
                    Data = data,
                    Meta = ResponseMeta.Basic(DateTime.Now, ok, ("offlineOnly", true))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running HMI template PLC sync precheck suite: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        private static int GetManifestInt(JsonObject? obj, string name)
        {
            if (obj == null || obj[name] == null) return 0;
            return int
                .TryParse(obj[name]?.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value : 0;
        }

        #endregion
    }
}
