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
using TiaMcp.Logic.V4.Hmi;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens;
using static TiaMcpServer.ModelContextProtocol.McpServer;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class HmiOfflineTools
    {
        [McpServerTool(Name = "AnalyzeGlobalLibraryPackage"), Description("[L2][HMI-Library]Analyze a TIA global library folder offline by file-system structure. It does not connect to TIA Portal, open the library, import content, or modify files.")]
        public CallToolResult AnalyzeGlobalLibraryPackageV4(
            [Description("libraryPath: global library folder path or .al* file path")] string libraryPath)
            => LibraryToolContract.Run("AnalyzeGlobalLibraryPackage", false, false, () =>
            {
                return AnalyzeGlobalLibraryPackage(libraryPath);
            });

        internal ResponseJsonReport AnalyzeGlobalLibraryPackage(
            string libraryPath)
        {
            try
            {
                var data = GlobalLibraryPackageAnalyzer.Analyze(libraryPath);
                // envelope: legacy-roundtrip-data-stamp
                data["timestamp"] = DateTime.Now.ToString("O");
                data["safetyPolicy"] = new JsonObject
                {
                    ["mode"] = "Offline file-system analysis only.",
                    ["tia"] = "TIA Portal is not connected or opened by this analysis.",
                    ["write"] = "No global library content is imported, modified, or written."
                };

                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Global library package offline analysis completed" : "Global library package offline analysis completed with findings",
                    Data = data,
                    Meta = ResponseMeta.Basic(DateTime.Now, ok)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error analyzing global library package: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "PlanGlobalLibraryTemplateReuse"), Description("[L2][HMI-Library] Plan the commercial fallback when direct MasterCopy import is not publicly verifiable: learn reference/global-library template evidence and rebuild screens with native Unified HMI MCP theme/layout/action tools. Offline planning only; it does not import library content or modify projects.")]
        public CallToolResult PlanGlobalLibraryTemplateReuseV4(
            [Description("libraryPath: reference global library folder path or .al* file path.")] string libraryPath,
            [Description("templateIntent: optional JSON {\"screenType\":\"overview\",\"targetRuntime\":\"Unified\",\"preferredComponents\":[...]}.")] TemplateIntent templateIntent = null!)
            => LibraryToolContract.Run("PlanGlobalLibraryTemplateReuse", false, false, () =>
            {
                string templateIntentJson = templateIntent == null ? "{}" : V4Json.Serialize(templateIntent);
                return PlanGlobalLibraryTemplateReuse(libraryPath, templateIntentJson);
            });

        internal ResponseJsonReport PlanGlobalLibraryTemplateReuse(
            string libraryPath,
            string templateIntentJson = "{}")
        {
            try
            {
                var analysis = GlobalLibraryPackageAnalyzer.Analyze(libraryPath);
                var intent = ToolJsonArguments.ParseJsonObjectOrEmpty(templateIntentJson, "templateIntentJson");
                var exists = analysis["exists"]?.GetValue<bool>() == true;
                var hasCoreFiles = analysis["ok"]?.GetValue<bool>() == true;
                var stringHints = analysis["stringHints"] as JsonObject;
                var patternCounts = stringHints?["patternCounts"] as JsonObject;
                var screenHintCount = patternCounts?["Screen"]?.GetValue<int>() ?? 0;
                var templateHintCount = patternCounts?["Template"]?.GetValue<int>() ?? 0;
                var masterCopyHintCount = patternCounts?["MasterCopy"]?.GetValue<int>() ?? 0;

                var data = new JsonObject
                {
                    ["libraryPath"] = libraryPath,
                    ["intent"] = intent,
                    ["offlineAnalysisOk"] = exists,
                    ["hasCoreGlobalLibraryFiles"] = hasCoreFiles,
                    ["strategy"] = "template-learn-and-native-rebuild",
                    ["directMasterCopyImportRequired"] = false,
                    ["directMasterCopyImportStatus"] = "optional-unverified-path",
                    ["commercialFallbackReady"] = exists,
                    ["safety"] = new JsonObject
                    {
                        ["offlineOnly"] = true,
                        ["importsLibraryContent"] = false,
                        ["modifiesProject"] = false,
                        ["requiresReadbackBeforeClaimingDirectImport"] = true
                    },
                    ["templateEvidence"] = new JsonObject
                    {
                        ["screenHintCount"] = screenHintCount,
                        ["templateHintCount"] = templateHintCount,
                        ["masterCopyHintCount"] = masterCopyHintCount
                    },
                    ["recommendedMcpTools"] = new JsonArray(
                        "AnalyzeGlobalLibraryPackage",
                        "ProbeGlobalLibrary",
                        "BuildUnifiedHmiThemeDesign",
                        "BuildUnifiedHmiLayoutDesign",
                        "BuildUnifiedHmiTemplateApplyDesign",
                        "ApplyUnifiedHmiScreenDesign",
                        "EnsureUnifiedHmiButtonAction"),
                    ["validationGates"] = new JsonArray(
                        "Template plan has offline package evidence.",
                        "Generated Unified design JSON passes layout QA.",
                        "Applied HMI screen items are read back by DescribeHmiScreenItem.",
                        "Button actions pass SyntaxCheck with zero errors.",
                        "HMI tags bind only to declared PLC symbols/DB members."),
                    ["reconstructionPlan"] = new JsonArray(
                        "Analyze global library/package structure and string hints without importing content.",
                        "Use ProbeGlobalLibrary only as read-only evidence when TIA is available; do not claim direct MasterCopy import unless readback succeeds.",
                        "Map reusable UI intent to Unified HMI native tools: theme, layout, template apply design, and button action recipes.",
                        "Apply generated design with ApplyUnifiedHmiScreenDesign and verify with item readback plus action SyntaxCheck.",
                        "Bind controls only to declared PLC symbols or DB members discovered from project exports/readback."),
                    ["analysis"] = analysis
                };

                return new ResponseJsonReport
                {
                    Ok = exists,
                    Message = exists
                        ? "Global library template reuse plan built. Direct MasterCopy import remains optional until real readback is verified."
                        : "Global library template reuse plan blocked because the library path was not found.",
                    Data = data,
                    Meta = ResponseMeta.Basic(DateTime.Now, exists)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error planning global library template reuse: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AnalyzeHmiTemplateReference"), Description("[L2][HMI-Library]Analyze local Unified HMI JSON templates against reference-project/runtime/global-library hints offline. It does not connect to TIA Portal or modify projects.")]
        public CallToolResult AnalyzeHmiTemplateReferenceV4(
            [Description("templateDirectory: directory containing Unified HMI JSON templates")] string templateDirectory,
            [Description("referenceProjectPath: reference TIA project folder containing HMI runtime export/currentConfiguration")] string referenceProjectPath,
            [Description("referenceGlobalLibraryPath: reference global library folder or .al* file")] string referenceGlobalLibraryPath)
            => LibraryToolContract.Run("AnalyzeHmiTemplateReference", false, false, () =>
            {
                return AnalyzeHmiTemplateReference(templateDirectory, referenceProjectPath, referenceGlobalLibraryPath);
            });

        internal ResponseJsonReport AnalyzeHmiTemplateReference(
            string templateDirectory,
            string referenceProjectPath,
            string referenceGlobalLibraryPath)
        {
            try
            {
                var data = HmiTemplateReferenceAnalyzer.Analyze(templateDirectory, referenceProjectPath, referenceGlobalLibraryPath);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "HMI template/reference offline analysis completed" : "HMI template/reference offline analysis completed with findings",
                    Data = data,
                    Meta = ResponseMeta.Basic(DateTime.Now, ok)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error analyzing HMI template/reference assets: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AnalyzeUnifiedHmiTemplateLayout"), Description("[L2][HMI-Library]Offline-only QA for Unified HMI JSON templates. Checks theme metadata, screen bounds, duplicate item names, size issues, layout overlap warnings, density, and execution JSON shape. It does not connect to TIA Portal or modify projects.")]
        public CallToolResult AnalyzeUnifiedHmiTemplateLayoutV4(
            [Description("templateDirectory: directory containing Unified HMI JSON templates")] string templateDirectory)
            => LibraryToolContract.Run("AnalyzeUnifiedHmiTemplateLayout", false, false, () =>
            {
                return AnalyzeUnifiedHmiTemplateLayout(templateDirectory);
            });

        internal ResponseJsonReport AnalyzeUnifiedHmiTemplateLayout(
            string templateDirectory)
        {
            try
            {
                // 显式传入检查委托，确保 execution JSON shape 检查会验证模板并报告错误。
                var data = HmiTemplateLayoutAnalyzer.AnalyzeDirectory(templateDirectory, HmiTemplateLayoutAnalyzer.ExecutionJsonBuilds);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Unified HMI template layout offline QA completed" : "Unified HMI template layout offline QA found blocking issues",
                    Data = data,
                    Meta = ResponseMeta.Basic(DateTime.Now, ok, ("offlineOnly", true))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error analyzing Unified HMI template layout: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildUnifiedHmiThemeDesign"), Description("[L2][HMI-Unified][OFFLINE] Build ApplyUnifiedHmiScreenDesign-compatible JSON from a theme/palette. It does not connect to TIA Portal or modify projects.")]
        public CallToolResult BuildUnifiedHmiThemeDesignJsonV4(
            [Description("theme: JSON {name?, palette:{Page?,Surface?,Text?,Border?,...}} with TIA ARGB colors like 0xFFF4F6F8.")] UnifiedThemeSpec theme)
            => UnifiedHmiContract.Run("BuildUnifiedHmiThemeDesign", false, false, () => BuildUnifiedHmiThemeDesignJson(theme.ToBuilderInput().ToJsonString()));

        public ResponseJsonReport BuildUnifiedHmiThemeDesignJson(string themeJson)
         => HmiDesignBuilder.BuildUnifiedHmiThemeDesignJson(themeJson);

        [McpServerTool(Name = "BuildUnifiedHmiLayoutDesign"), Description("[L2][HMI-Unified][OFFLINE] Build ApplyUnifiedHmiScreenDesign-compatible JSON from a grid layout. It does not connect to TIA Portal or modify projects.")]
        public CallToolResult BuildUnifiedHmiLayoutDesignJsonV4(
            [Description("layout: JSON {grid?,left?,top?,gap?,columns?,cellWidth?,cellHeight?,items:[{name,type?,row?,col?,rowSpan?,colSpan?,text?,properties?}]}.")] UnifiedLayoutSpec layout)
            => UnifiedHmiContract.Run("BuildUnifiedHmiLayoutDesign", false, false, () => BuildUnifiedHmiLayoutDesignJson(layout.ToBuilderInput().ToJsonString()));

        public ResponseJsonReport BuildUnifiedHmiLayoutDesignJson(string layoutJson)
         => HmiDesignBuilder.BuildUnifiedHmiLayoutDesignJson(layoutJson);

        [McpServerTool(Name = "BuildUnifiedHmiButtonActionScript"), Description("[L2][HMI-Unified]Build a safe Unified HMI button action script from a high-level action recipe without connecting to TIA.")]
        public CallToolResult BuildUnifiedHmiButtonActionScriptV4(
            [Description("actionKind: set-bit, reset-bit, toggle-bit, open-popup, goto-screen, confirm-write")] string actionKind,
            [Description("eventType: HmiButtonEventType value, e.g. Down (press), Up (release), Tapped — NOT Pressed/Released")] string eventType,
            [Description("targetTag: target HMI tag for set/reset/toggle actions")] string targetTag = "",
            [Description("targetScreen: target screen for goto-screen actions")] string targetScreen = "",
            [Description("targetPopup: target popup for open-popup actions")] string targetPopup = "")
            => UnifiedHmiContract.Run("BuildUnifiedHmiButtonActionScript", false, false, () => BuildUnifiedHmiButtonActionScript(actionKind, eventType, targetTag, targetScreen, targetPopup));

        public ResponseMessage BuildUnifiedHmiButtonActionScript(string actionKind,
            string eventType,
            string targetTag = "",
            string targetScreen = "",
            string targetPopup = "")
         => HmiDesignBuilder.BuildUnifiedHmiButtonActionScript(actionKind, eventType, targetTag, targetScreen, targetPopup);

        [McpServerTool(Name = "RunHmiActionScriptRecipeSafetySelfTest"), Description("[L2][Diagnostics]Offline-only helper: prove deterministic HMI button action scripts are allowed only for safe set/reset/toggle bit recipes, while high-risk writes and unverified navigation/popup recipes are blocked.")]
        public CallToolResult RunHmiActionScriptRecipeSafetySelfTestV4()
            => UnifiedHmiContract.Run("RunHmiActionScriptRecipeSafetySelfTest", false, false, () => RunHmiActionScriptRecipeSafetySelfTest());

        public ResponseJsonReport RunHmiActionScriptRecipeSafetySelfTest()
        {
            try
            {
                var data = HmiActionScriptRecipeBuilder.RunSafetySelfTest();
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "HMI action script recipe safety self-test passed" : "HMI action script recipe safety self-test failed",
                    Data = data,
                    Meta = ResponseMeta.Basic(DateTime.Now, ok, ("offlineOnly", true))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running HMI action script recipe safety self-test: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
